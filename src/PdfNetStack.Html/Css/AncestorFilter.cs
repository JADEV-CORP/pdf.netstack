using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Css;

/// <summary>
/// A counting Bloom filter of the tag names, ids and classes of the ancestors of the element being
/// styled, as browsers keep while matching (Blink's SelectorFilter, WebKit's SelectorFilter): a
/// selector such as <c>.sidebar .note p</c> is rejected without walking up the tree when no
/// ancestor can be <c>.sidebar</c> or <c>.note</c>. The filter answers "maybe" or "no" and is never
/// wrong about "no", so a rejected selector could not have matched.
/// </summary>
/// <remarks>
/// The filter holds one chain of elements from the root, like a stack. Styling visits elements in
/// document order, so moving to the next element pushes or pops one ancestor; any other order
/// still works, at the cost of a walk up from the new element to the chain.
/// </remarks>
internal sealed class AncestorFilter
{
    // 4096 counters and two per key, as in browsers: a deep document has a few hundred keys among
    // its ancestors, and a selector that needs a missing one is then rejected nearly every time.
    private const int Bits = 12;
    private const int Mask = (1 << Bits) - 1;

    // A counter that reaches the maximum is never decremented again. It may then answer "maybe"
    // for a key that left, which only costs a full match, never a wrong answer.
    private const byte Saturated = byte.MaxValue;

    /// <summary>The most keys a selector keeps for the filter; more add little rejection power.</summary>
    internal const int MaxSelectorKeys = 4;

    private readonly byte[] _counts = new byte[1 << Bits];
    private readonly List<HtmlElement> _chain = [];
    private readonly List<int> _keys = [];
    private readonly List<int> _keyStarts = [];
    private readonly HashSet<HtmlElement> _onChain = [];
    private readonly List<HtmlElement> _path = [];

    /// <summary>The key for an element name in a type selector.</summary>
    public static int TagKey(string name) => Hash('t', name);

    /// <summary>The key for an id.</summary>
    public static int IdKey(string id) => Hash('#', id);

    /// <summary>The key for a class name.</summary>
    public static int ClassKey(string name) => Hash('.', name);

    /// <summary>Makes the filter hold exactly the ancestors of an element whose parent is <paramref name="parent"/>.</summary>
    /// <param name="parent">The parent element, or null for the root element.</param>
    public void MoveTo(HtmlElement? parent)
    {
        if (_chain.Count > 0 ? ReferenceEquals(_chain[^1], parent) : parent is null) return;

        // Up from the parent to the first element already on the chain, or past the root.
        _path.Clear();
        HtmlElement? anchor = parent;
        while (anchor is not null && !_onChain.Contains(anchor))
        {
            _path.Add(anchor);
            anchor = anchor.ParentElement;
        }

        while (_chain.Count > 0 && !ReferenceEquals(_chain[^1], anchor)) Pop();
        for (int i = _path.Count - 1; i >= 0; i--) Push(_path[i]);
    }

    /// <summary>Whether every key may be among the ancestors; false means some key surely is not.</summary>
    public bool MightContainAll(int[] keys)
    {
        foreach (int key in keys)
        {
            if (_counts[key & Mask] == 0 || _counts[(key >> Bits) & Mask] == 0) return false;
        }

        return true;
    }

    private void Push(HtmlElement element)
    {
        _keyStarts.Add(_keys.Count);
        _keys.Add(TagKey(element.Name));
        if (element.GetAttribute("id") is string id) _keys.Add(IdKey(id));
        IReadOnlyList<string> classes = element.ClassList;
        for (int i = 0; i < classes.Count; i++) _keys.Add(ClassKey(classes[i]));
        for (int i = _keyStarts[^1]; i < _keys.Count; i++)
        {
            Increment(_keys[i] & Mask);
            Increment((_keys[i] >> Bits) & Mask);
        }

        _chain.Add(element);
        _onChain.Add(element);
    }

    private void Pop()
    {
        int start = _keyStarts[^1];
        for (int i = start; i < _keys.Count; i++)
        {
            Decrement(_keys[i] & Mask);
            Decrement((_keys[i] >> Bits) & Mask);
        }

        _keys.RemoveRange(start, _keys.Count - start);
        _keyStarts.RemoveAt(_keyStarts.Count - 1);
        _onChain.Remove(_chain[^1]);
        _chain.RemoveAt(_chain.Count - 1);
    }

    private void Increment(int slot)
    {
        if (_counts[slot] != Saturated) _counts[slot]++;
    }

    private void Decrement(int slot)
    {
        if (_counts[slot] != Saturated) _counts[slot]--;
    }

    // FNV-1a over the kind and the characters: stable, cheap, and enough spread for 12 bit slots.
    private static int Hash(char kind, string value)
    {
        uint h = 2166136261;
        h = (h ^ kind) * 16777619;
        foreach (char c in value) h = (h ^ c) * 16777619;
        return (int)h;
    }
}
