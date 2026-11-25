using PdfNetStack.Objects;

namespace PdfNetStack.Documents;

/// <summary>
/// One reading of a document's structure tree (ISO 32000-1, 14.7): each element's parent, and for
/// each page the elements whose content is on it, so the elements a set of pages needs can be
/// found without walking the whole tree again. The walk is bounded in depth and in elements, as
/// the tree comes from an untrusted file.
/// </summary>
internal sealed class StructureIndex
{
    /// <summary>Levels of nesting read. Real trees stay within a few dozen.</summary>
    public const int MaxDepth = 256;

    /// <summary>Elements read; a larger tree is read this far and the rest of the content goes untagged.</summary>
    public const int MaxElements = 1_000_000;

    /// <summary>
    /// Children listed, counted every time an element lists one. Elements may share a /K array
    /// in a damaged or hostile file, so the count of elements alone does not bound the work.
    /// </summary>
    public const long MaxKids = 10_000_000;

    /// <summary>Kids read between two looks at the cancellation token.</summary>
    public const int KidInterval = 1024;

    // Element number to its parent's, -1 under the root.
    private readonly Dictionary<int, int> _parents = [];

    // Page number to the elements with content on it. An element held directly, not as an
    // object of its own, counts for the nearest element that is one, or -1 for the root.
    private readonly Dictionary<int, HashSet<int>> _byPage = [];

    private StructureIndex(PdfDictionary root, int rootNumber, List<PdfObject> rootKids)
    {
        Root = root;
        RootNumber = rootNumber;
        RootKids = rootKids;
    }

    public PdfDictionary Root { get; }

    /// <summary>The root's object number, or -1 when the catalog holds it directly.</summary>
    public int RootNumber { get; }

    public List<PdfObject> RootKids { get; }

    /// <summary>The parent tree's entries (ISO 32000-1, 14.7.4.4), by key.</summary>
    public Dictionary<int, PdfObject> ParentTree { get; } = [];

    /// <summary>True when some element has an /ID, so the ID tree needs merging.</summary>
    public bool HasIds { get; private set; }

    public bool IsElement(int number) => _parents.ContainsKey(number);

    /// <summary>The structure tree of <paramref name="doc"/>, or null when it is not tagged.</summary>
    public static StructureIndex? Read(PdfDocument doc, CancellationToken ct)
    {
        PdfObject? entry = doc.Catalog["StructTreeRoot"];
        if (doc.Resolve(entry) is not PdfDictionary root) return null;
        var index = new StructureIndex(root, entry is PdfReference r ? r.Number : -1, [.. Kids(doc, root)]);

        if (doc.Resolve(root["ParentTree"]) is PdfDictionary tree)
        {
            foreach ((PdfObject key, PdfObject value) in TreeReader.Entries(doc, tree, "Nums"))
            {
                if (doc.Resolve(key) is PdfNumber { IsInteger: true } k && k.IntegerValue is >= 0 and <= int.MaxValue) index.ParentTree[(int)k.IntegerValue] = value;
            }
        }

        index.Walk(doc, ct);
        return index;
    }

    /// <summary>
    /// The children of an element or of the root (/K): one object or an array of them. References
    /// to elements stay references, so their numbers are known; a reference to anything but a
    /// dictionary, such as an MCID held as an object, is resolved.
    /// </summary>
    public static IEnumerable<PdfObject> Kids(PdfDocument doc, PdfDictionary element)
    {
        PdfObject? k = element["K"];
        PdfObject? resolved = doc.Resolve(k);
        IEnumerable<PdfObject> items = resolved switch
        {
            PdfArray array => array,
            null or PdfNull => [],
            _ => [k!],
        };

        foreach (PdfObject item in items)
        {
            if (item is PdfReference && doc.Resolve(item) is PdfObject target and not PdfDictionary) yield return target;
            else yield return item;
        }
    }

    /// <summary>
    /// True when the /K of <paramref name="element"/> is an indirect array that another element
    /// already listed, recorded in <paramref name="listed"/>. A real tree gives each element its
    /// own children; reading a shared array again for every element costs their product.
    /// </summary>
    public static bool KidsAlreadyListed(PdfDocument doc, PdfDictionary element, HashSet<int> listed) =>
        element["K"] is PdfReference r && doc.Resolve(r) is PdfArray && !listed.Add(r.Number);

    /// <summary>A marked content reference or an object reference (ISO 32000-1, 14.7.4.3), not an element.</summary>
    public static bool IsContentReference(PdfDictionary d) => d.GetName("Type") is "MCR" or "OBJR";

    /// <summary>The elements with content on any of <paramref name="pages"/>, with all their ancestors.</summary>
    public HashSet<int> Ancestry(IEnumerable<int> pages)
    {
        var kept = new HashSet<int>();
        foreach (int page in pages)
        {
            if (!_byPage.TryGetValue(page, out HashSet<int>? owners)) continue;
            foreach (int owner in owners)
            {
                // Stops at the root, or at an element already kept with its ancestors.
                for (int e = owner; e >= 0 && kept.Add(e);) e = _parents.TryGetValue(e, out int parent) ? parent : -1;
            }
        }

        return kept;
    }

    private void Walk(PdfDocument doc, CancellationToken ct)
    {
        var stack = new Stack<(PdfObject Entry, int Parent, PdfReference? Page, int Depth)>();
        for (int i = RootKids.Count - 1; i >= 0; i--) stack.Push((RootKids[i], -1, null, 0));

        // Elements written in place have no number to remember them by, and a /K array can
        // contain itself through them, so they are remembered by identity.
        var directSeen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var listedKids = new HashSet<int>();
        long kidCount = 0;
        int sharedKids = 0;
        bool tooManyKids = false;
        while (stack.Count > 0 && !tooManyKids)
        {
            (PdfObject entry, int parent, PdfReference? page, int depth) = stack.Pop();

            // TODO(diagnostics): elements past the depth or count bound are not read, so their
            // content goes untagged in a copy; report it once the diagnostics channel exists.
            if (depth > MaxDepth) continue;
            PdfDictionary element;
            int owner;
            if (entry is PdfReference r)
            {
                if (_parents.Count >= MaxElements) break;
                if (r.Number == RootNumber || _parents.ContainsKey(r.Number) || doc.Resolve(r) is not PdfDictionary d || IsContentReference(d)) continue;
                _parents[r.Number] = parent;
                if (_parents.Count % 1024 == 0) ct.ThrowIfCancellationRequested();
                element = d;
                owner = r.Number;
            }
            else if (entry is PdfDictionary direct && !IsContentReference(direct) && directSeen.Add(direct))
            {
                element = direct;
                owner = parent;
            }
            else
            {
                continue;
            }

            if (element["ID"] is not null) HasIds = true;
            if (KidsAlreadyListed(doc, element, listedKids))
            {
                sharedKids++;
                continue;
            }

            // An element's /Pg is the page of its MCIDs and of references that name none (Table 323).
            PdfReference? own = element["Pg"] as PdfReference ?? page;
            foreach (PdfObject kid in Kids(doc, element))
            {
                if (++kidCount > MaxKids)
                {
                    tooManyKids = true;
                    break;
                }

                if (kidCount % KidInterval == 0) ct.ThrowIfCancellationRequested();
                if (kid is PdfNumber { IsInteger: true })
                {
                    if (own is not null) AddContent(own.Number, owner);
                }
                else if (doc.Resolve(kid) is PdfDictionary item)
                {
                    if (!IsContentReference(item)) stack.Push((kid, owner, own, depth + 1));
                    else if ((item["Pg"] as PdfReference ?? own) is PdfReference at) AddContent(at.Number, owner);
                }
            }
        }

        if (sharedKids > 0) doc.Budget.Diagnostics.Skipped(Compat.Format("{0} structure element(s) list children that another element already lists; they were read once.", sharedKids));
        if (tooManyKids) doc.Budget.Diagnostics.Skipped(Compat.Format("The structure tree lists more than {0} children; the rest was not read, and its content goes untagged in a copy.", MaxKids));
    }

    private void AddContent(int page, int owner)
    {
        if (!_byPage.TryGetValue(page, out HashSet<int>? owners)) _byPage[page] = owners = [];
        owners.Add(owner);
    }
}
