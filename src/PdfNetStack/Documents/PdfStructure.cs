using PdfNetStack.Content;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>The standard structure types of tagged PDF (ISO 32000-1, 14.8.4), which assistive technology understands.</summary>
public enum PdfStructureType
{
    /// <summary>The whole document; the root element of the tree.</summary>
    Document,

    /// <summary>A large division, such as a chapter.</summary>
    Part,

    /// <summary>A self contained article.</summary>
    Art,

    /// <summary>A section.</summary>
    Sect,

    /// <summary>A generic block level group.</summary>
    Div,

    /// <summary>A quotation of one or more paragraphs.</summary>
    BlockQuote,

    /// <summary>The caption of a table, figure or list.</summary>
    Caption,

    /// <summary>A table of contents.</summary>
    TOC,

    /// <summary>An item of a table of contents.</summary>
    TOCI,

    /// <summary>An index.</summary>
    Index,

    /// <summary>A grouping without meaning of its own; its content belongs to its parent.</summary>
    NonStruct,

    /// <summary>Content private to the producer.</summary>
    Private,

    /// <summary>A paragraph.</summary>
    P,

    /// <summary>A heading in a document structured with nested sections; do not mix with H1 to H6.</summary>
    H,

    /// <summary>A first level heading.</summary>
    H1,

    /// <summary>A second level heading.</summary>
    H2,

    /// <summary>A third level heading.</summary>
    H3,

    /// <summary>A fourth level heading.</summary>
    H4,

    /// <summary>A fifth level heading.</summary>
    H5,

    /// <summary>A sixth level heading.</summary>
    H6,

    /// <summary>A list: holds LI items.</summary>
    L,

    /// <summary>A list item: holds a Lbl and a LBody.</summary>
    LI,

    /// <summary>The bullet or number of a list item.</summary>
    Lbl,

    /// <summary>The body of a list item.</summary>
    LBody,

    /// <summary>A table: holds rows, or THead, TBody and TFoot groups of rows.</summary>
    Table,

    /// <summary>A table row: holds TH and TD cells.</summary>
    TR,

    /// <summary>A table header cell. Give it a <see cref="PdfStructureElement.Scope"/>.</summary>
    TH,

    /// <summary>A table data cell.</summary>
    TD,

    /// <summary>The header rows of a table.</summary>
    THead,

    /// <summary>The body rows of a table.</summary>
    TBody,

    /// <summary>The footer rows of a table.</summary>
    TFoot,

    /// <summary>A generic inline group.</summary>
    Span,

    /// <summary>An inline quotation.</summary>
    Quote,

    /// <summary>A footnote or endnote. Gets a unique ID, which PDF/UA requires.</summary>
    Note,

    /// <summary>A reference to elsewhere in the document.</summary>
    Reference,

    /// <summary>A bibliography entry.</summary>
    BibEntry,

    /// <summary>Computer code.</summary>
    Code,

    /// <summary>A link: its text, and the link annotation added with <see cref="PdfStructureElement.AddAnnotation(PdfAnnotation)"/>.</summary>
    Link,

    /// <summary>An annotation other than a link or a form field.</summary>
    Annot,

    /// <summary>Ruby: base text with annotation text, as in Japanese.</summary>
    Ruby,

    /// <summary>The base text of ruby.</summary>
    RB,

    /// <summary>The annotation text of ruby.</summary>
    RT,

    /// <summary>The punctuation around ruby annotation text.</summary>
    RP,

    /// <summary>Warichu: comment text set in two smaller lines.</summary>
    Warichu,

    /// <summary>The text of warichu.</summary>
    WT,

    /// <summary>The punctuation around warichu text.</summary>
    WP,

    /// <summary>A picture or graphic. Needs <see cref="PdfStructureElement.AlternateText"/>.</summary>
    Figure,

    /// <summary>A mathematical formula. Needs <see cref="PdfStructureElement.AlternateText"/>.</summary>
    Formula,

    /// <summary>A form field: its widget annotation and any content drawn for it.</summary>
    Form,
}

/// <summary>Which cells a table header cell (TH) heads.</summary>
public enum PdfTableHeaderScope
{
    /// <summary>The cells of its row.</summary>
    Row,

    /// <summary>The cells of its column.</summary>
    Column,

    /// <summary>The cells of its row and its column.</summary>
    Both,
}

/// <summary>How a list's items are labelled (the ListNumbering attribute, ISO 32000-1, 14.8.5.5).</summary>
public enum PdfListNumbering
{
    /// <summary>No label.</summary>
    None,

    /// <summary>A filled round bullet.</summary>
    Disc,

    /// <summary>An open round bullet.</summary>
    Circle,

    /// <summary>A square bullet.</summary>
    Square,

    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>A, B, C.</summary>
    UpperAlpha,

    /// <summary>a, b, c.</summary>
    LowerAlpha,
}

/// <summary>
/// The logical structure of a tagged PDF (ISO 32000-1, 14.7): a tree of structure elements, such
/// as headings, paragraphs, lists, tables and figures, in reading order, each owning the content
/// drawn for it. Screen readers, reflow and PDF/UA rely on it.
/// </summary>
/// <remarks>
/// Elements are added under <see cref="Root"/> with <see cref="PdfStructureElement.Add(PdfStructureType)"/>
/// or opened while drawing with <see cref="Drawing.PdfGraphics.BeginTag(PdfStructureType)"/>; the
/// order elements are added in is the reading order. The tree, the parent tree and the catalog's
/// /MarkInfo are written on save. A document opened with a structure tree keeps it: new elements
/// go into its Document element, or into a new one after its existing elements.
/// </remarks>
/// <example>
/// <code>
/// using PdfGraphics g = page.CreateGraphics();
/// g.BeginTag(PdfStructureType.H1);
/// g.DrawString("Annual report", font, 24, PdfColor.Black, 72, 72);
/// g.EndTag();
/// g.BeginTag(PdfStructureType.Figure).AlternateText = "Revenue grew 12 percent";
/// g.DrawImage(chart, 72, 120, 300, 200);
/// g.EndTag();
/// g.BeginArtifact(PdfArtifactType.Footer);
/// g.DrawString("Page 1", font, 9, PdfColor.Gray(0.4), 72, 800);
/// g.EndArtifact();
/// </code>
/// </example>
public sealed class PdfStructureTree
{
    /// <summary>Most elements a document's existing tree is searched through for its Document element and IDs.</summary>
    private const int MaxNodes = 1_000_000;

    private readonly PdfDocument _doc;
    private readonly bool _rootIsNew;

    // The parent tree as last read from the catalog's tree, and the entries this tree added, with
    // the dictionary whose /StructParents or /StructParent holds each key, for renumbering.
    private readonly SortedDictionary<int, PdfObject> _parentTree = [];
    private readonly SortedDictionary<int, (PdfObject Value, PdfDictionary Owner, string Entry)> _own = [];
    private readonly Dictionary<int, PageEntry> _pages = [];
    private readonly SortedDictionary<string, string> _roles = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, PdfReference> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<PdfArray, PdfReference> _arrays = [];
    private PdfDictionary _root;
    private PdfReference _rootReference;
    private int _nextKey;
    private int _noteIds;
    private bool _changed;

    internal PdfStructureTree(PdfDocument doc)
    {
        _doc = doc;
        if (doc.Resolve(doc.Catalog["StructTreeRoot"]) is PdfDictionary existing)
        {
            _root = existing;
            _rootReference = doc.Catalog["StructTreeRoot"] as PdfReference ?? doc.AddObject(existing);
            ReadParentTree();
            CollectIds();
            Root = ExistingDocument() ?? NewDocument(out _rootIsNew);
        }
        else
        {
            _root = new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot") };
            _rootReference = doc.AddObject(_root);
            Root = NewDocument(out _rootIsNew);
        }
    }

    /// <summary>The Document element at the top of the tree; new elements go under it unless given another parent.</summary>
    public PdfStructureElement Root { get; }

    internal PdfDocument Document => _doc;

    /// <summary>
    /// Maps a structure type of your own to the standard type it behaves as (the role map,
    /// ISO 32000-1, 14.8.3), so readers that know only the standard types understand it.
    /// </summary>
    /// <param name="type">The custom type, such as "Subtitle": letters, digits, '_', '-' and '.'.</param>
    /// <param name="standardType">The standard type it is read as.</param>
    /// <exception cref="ArgumentException"><paramref name="type"/> is a standard type, which PDF/UA forbids remapping, or not a plain name.</exception>
    public void MapRole(string type, PdfStructureType standardType)
    {
        CheckTypeName(type);
        if (IsStandard(type)) throw new ArgumentException($"{type} is a standard structure type and cannot be remapped.", nameof(type));
        _roles[type] = standardType.ToString();
        _changed = true;
    }

    /// <summary>The standard type names, those of <see cref="PdfStructureType"/>.</summary>
    internal static bool IsStandard(string type) => Enum.TryParse(type, ignoreCase: false, out PdfStructureType parsed) && parsed.ToString() == type;

    /// <summary>A type name the content writer can write as is, and the role map can hold.</summary>
    internal static void CheckTypeName(string type)
    {
        Compat.ThrowIfNullOrEmpty(type, nameof(type));
        if (type.Length > 127 || !type.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.'))
        {
            throw new ArgumentException("A structure type is a name of up to 127 letters, digits, '_', '-' and '.'.", nameof(type));
        }
    }

    internal PdfStructureElement NewElement(PdfStructureElement parent, string type)
    {
        var dictionary = new PdfDictionary
        {
            ["Type"] = new PdfName("StructElem"),
            ["S"] = new PdfName(type),
            ["P"] = parent.Reference,
        };
        PdfReference reference = _doc.AddObject(dictionary);
        Kids(parent.Dictionary).Add(reference);
        _changed = true;
        var element = new PdfStructureElement(this, parent, dictionary, reference);

        // PDF/UA asks every Note for a unique ID (ISO 14289-1, 7.9).
        if (type == nameof(PdfStructureType.Note))
        {
            string id;
            do
            {
                id = "note-" + (++_noteIds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            while (_ids.ContainsKey(id));

            element.Id = id;
        }

        return element;
    }

    /// <summary>Records an element's ID for the tree's IDTree; null removes it.</summary>
    internal void SetId(PdfStructureElement element, string? old, string? id)
    {
        if (id is not null && _ids.TryGetValue(id, out PdfReference? owner) && owner.Number != element.Reference.Number)
        {
            throw new ArgumentException($"Another element has the ID {id}.", nameof(id));
        }

        if (old is not null) _ids.Remove(old);
        if (id is not null) _ids[id] = element.Reference;
        _changed = true;
    }

    /// <summary>
    /// A new marked content identifier on <paramref name="page"/> for content of
    /// <paramref name="element"/>: recorded in the element's kids and the page's parent tree entry.
    /// </summary>
    /// <returns>The MCID, and whether the element's /Pg was set by this call.</returns>
    internal (int Mcid, bool PageSet) Mark(PdfPage page, PdfStructureElement element)
    {
        PageEntry entry = Page(page);
        int mcid = entry.Next++;
        while (entry.Content.Count < mcid) entry.Content.Add(PdfNull.Instance);
        entry.Content.Add(element.Reference);

        // An element is placed on the page its first content is on; content elsewhere names its page.
        var pageReference = new PdfReference(page.Number, 0);
        bool pageSet = element.Dictionary["Pg"] is null;
        if (pageSet) element.Dictionary.Set("Pg", pageReference);
        PdfObject kid = element.Dictionary["Pg"] is PdfReference pg && pg.Number == page.Number
            ? PdfNumber.FromInteger(mcid)
            : new PdfDictionary { ["Type"] = new PdfName("MCR"), ["Pg"] = pageReference, ["MCID"] = PdfNumber.FromInteger(mcid) };
        Kids(element.Dictionary).Add(kid);
        _changed = true;
        return (mcid, pageSet);
    }

    /// <summary>
    /// Takes back the MCID <see cref="Mark"/> gave last on the page, for a sequence that stayed
    /// empty. False when it is no longer the last, and the empty sequence stays.
    /// </summary>
    internal bool Unmark(PdfPage page, PdfStructureElement element, int mcid, bool pageSet)
    {
        if (!_pages.TryGetValue(page.Number, out PageEntry? entry) || entry.Next != mcid + 1 || entry.Content.Count != mcid + 1) return false;

        // The element's last content kid; elements added since, such as the child that paused it, may follow.
        PdfArray kids = Kids(element.Dictionary);
        int at = kids.Count - 1;
        while (at >= 0 && McidOf(kids[at]) < 0) at--;
        if (at < 0 || McidOf(kids[at]) != mcid) return false;
        kids.RemoveAt(at);
        entry.Content.RemoveAt(mcid);
        entry.Next--;
        if (pageSet) element.Dictionary.Remove("Pg");
        return true;
    }

    /// <summary>Puts an annotation in the tree under <paramref name="element"/> (ISO 32000-1, 14.7.4.3).</summary>
    internal void AddAnnotation(PdfStructureElement element, PdfPage page, PdfDictionary annotation, PdfObject entry)
    {
        if (!ReferenceEquals(page.Owner, _doc)) throw new ArgumentException("The annotation belongs to another document.", nameof(annotation));
        if (annotation["StructParent"] is not null) throw new InvalidOperationException("The annotation is already in the structure tree.");
        PdfReference reference = entry as PdfReference ?? MakeIndirect(page, annotation);
        int key = _nextKey++;
        annotation.Set("StructParent", PdfNumber.FromInteger(key));
        _own[key] = (element.Reference, annotation, "StructParent");
        Kids(element.Dictionary).Add(new PdfDictionary
        {
            ["Type"] = new PdfName("OBJR"),
            ["Obj"] = reference,
            ["Pg"] = new PdfReference(page.Number, 0),
        });
        _changed = true;
    }

    /// <summary>Writes the parent tree, role map and IDs, and marks the document as tagged. Runs on every save; does nothing when the tree was never changed.</summary>
    internal void WriteTo()
    {
        if (!_changed) return;

        // Another writer, such as a page import, may have put a tree in the catalog since: join it.
        if (_doc.Resolve(_doc.Catalog["StructTreeRoot"]) is PdfDictionary current && !ReferenceEquals(current, _root)) Join(current);
        if (_rootIsNew)
        {
            PdfArray top = Kids(_root);
            if (!top.Any(k => k is PdfReference r && r.Number == Root.Reference.Number)) top.Add(Root.Reference);
        }

        // Entries an earlier save wrote stay where they are. New ones take keys after every key the
        // tree holds now, page imports included, and are appended: the existing tree, however large
        // or deep, is never rewritten.
        int free = ReadParentTree();
        var added = new List<(int Old, int Key, (PdfObject Value, PdfDictionary Owner, string Entry) Own)>();
        foreach (KeyValuePair<int, (PdfObject Value, PdfDictionary Owner, string Entry)> own in _own)
        {
            if (_parentTree.TryGetValue(own.Key, out PdfObject? written) && SameEntry(written, own.Value.Value)) continue;
            int key = free++;
            own.Value.Owner.Set(own.Value.Entry, PdfNumber.FromInteger(key));
            added.Add((own.Key, key, own.Value));
        }

        // Old keys go first: a new key may be another entry's old one.
        foreach ((int old, int _, _) in added) _own.Remove(old);
        var pairs = new PdfArray();
        foreach ((int _, int key, (PdfObject Value, PdfDictionary Owner, string Entry) own) in added)
        {
            _own[key] = own;
            _parentTree[key] = own.Value;

            // The arrays of MCID parents are indirect objects, as they must be.
            pairs.Add(PdfNumber.FromInteger(key));
            pairs.Add(own.Value is PdfArray array ? ArrayReference(array) : own.Value);
        }

        if (pairs.Count > 0)
        {
            switch (_doc.Resolve(_root["ParentTree"]))
            {
                case PdfDictionary tree when _doc.Resolve(tree["Nums"]) is PdfArray nums:
                    foreach (PdfObject item in pairs) nums.Add(item);
                    break;
                case PdfDictionary tree when _doc.Resolve(tree["Kids"]) is PdfArray kids:
                    kids.Add(_doc.AddObject(new PdfDictionary { ["Limits"] = PdfArray.Of(pairs[0], pairs[pairs.Count - 2]), ["Nums"] = pairs }));
                    break;
                default:
                    _root.Set("ParentTree", _doc.AddObject(new PdfDictionary { ["Nums"] = pairs }));
                    break;
            }
        }

        _nextKey = Math.Max(_nextKey, free);
        _root.Set("ParentTreeNextKey", PdfNumber.FromInteger(_nextKey));
        if (_roles.Count > 0)
        {
            PdfDictionary map = _doc.Resolve(_root["RoleMap"]) as PdfDictionary ?? new PdfDictionary();
            foreach (KeyValuePair<string, string> role in _roles) map.Set(role.Key, new PdfName(role.Value));
            if (_root["RoleMap"] is not PdfReference) _root.Set("RoleMap", map);
        }

        if (_ids.Count > 0) WriteIds();
        _doc.Catalog.Set("StructTreeRoot", _rootReference);

        // The catalog's MarkInfo may be shared or indirect; a direct copy keeps its other entries.
        PdfDictionary markInfo = _doc.Catalog["MarkInfo"] as PdfDictionary
            ?? (_doc.Resolve(_doc.Catalog["MarkInfo"]) is PdfDictionary existing ? PdfDocument.Clone(existing) : new PdfDictionary());
        markInfo.Set("Marked", PdfBoolean.True);
        _doc.Catalog.Set("MarkInfo", markInfo);
    }

    /// <summary>Moves this tree's top level elements and role map into the tree the catalog now holds.</summary>
    private void Join(PdfDictionary current)
    {
        PdfReference reference = _doc.Catalog["StructTreeRoot"] as PdfReference ?? _doc.AddObject(current);
        PdfArray theirs = Kids(current);
        foreach (PdfObject kid in Kids(_root))
        {
            if (_doc.Resolve(kid) is PdfDictionary element) element.Set("P", reference);
            if (!theirs.Any(k => k is PdfReference r && kid is PdfReference mine && r.Number == mine.Number)) theirs.Add(kid);
        }

        if (_rootIsNew) Root.Dictionary.Set("P", reference);
        if (_doc.Resolve(_root["RoleMap"]) is PdfDictionary roles)
        {
            PdfDictionary map = _doc.Resolve(current["RoleMap"]) as PdfDictionary ?? new PdfDictionary();
            foreach (KeyValuePair<string, PdfObject> role in roles) map.Set(role.Key, role.Value);
            if (current["RoleMap"] is not PdfReference) current.Set("RoleMap", map);
        }

        _root = current;
        _rootReference = reference;
    }

    /// <summary>Whether a parent tree value is the one this tree wrote: the same element, or the same MCID array.</summary>
    private bool SameEntry(PdfObject existing, PdfObject own) => own switch
    {
        PdfArray array => ReferenceEquals(_doc.Resolve(existing), array),
        PdfReference r => existing is PdfReference e && e.Number == r.Number,
        _ => ReferenceEquals(existing, own),
    };

    private PdfReference ArrayReference(PdfArray array)
    {
        if (!_arrays.TryGetValue(array, out PdfReference? reference)) _arrays[array] = reference = _doc.AddObject(array);
        return reference;
    }

    /// <summary>
    /// The IDTree as one sorted leaf: the existing tree's entries, then those set here. A tree too
    /// large to read whole is left as it is, so none of its entries is lost; the IDs stay on their
    /// elements.
    /// </summary>
    private void WriteIds()
    {
        var ids = new SortedDictionary<string, PdfObject>(StringComparer.Ordinal);
        if (_doc.Resolve(_root["IDTree"]) is PdfDictionary existing)
        {
            List<(PdfObject Key, PdfObject Value)> entries = ReadTree(_doc, existing, "Names", out bool complete);
            if (!complete) return;
            foreach ((PdfObject key, PdfObject value) in entries)
            {
                if (_doc.Resolve(key) is PdfString s) ids[s.ToText()] = value;
            }
        }

        foreach (KeyValuePair<string, PdfReference> id in _ids) ids[id.Key] = id.Value;
        var names = new PdfArray();
        foreach (KeyValuePair<string, PdfObject> id in ids)
        {
            names.Add(PdfString.FromText(id.Key));
            names.Add(id.Value);
        }

        _root.Set("IDTree", new PdfDictionary { ["Names"] = names });
    }

    /// <summary>The kids of an element as an array it holds, converting a single kid or a missing entry.</summary>
    internal PdfArray Kids(PdfDictionary element)
    {
        PdfObject? kids = element["K"];
        if (kids is PdfArray direct) return direct;
        if (kids is PdfReference && _doc.Resolve(kids) is PdfArray shared) return shared;
        PdfArray created = kids is null or PdfNull ? new PdfArray() : PdfArray.Of(kids);
        element.Set("K", created);
        return created;
    }

    private static int McidOf(PdfObject kid) => kid switch
    {
        PdfNumber { IsInteger: true } n => (int)Math.Min(n.IntegerValue, int.MaxValue),
        PdfDictionary { } mcr when mcr["MCID"] is PdfNumber { IsInteger: true } m => (int)Math.Min(m.IntegerValue, int.MaxValue),
        _ => -1,
    };

    private PdfStructureElement NewDocument(out bool isNew)
    {
        isNew = true;
        var dictionary = new PdfDictionary
        {
            ["Type"] = new PdfName("StructElem"),
            ["S"] = new PdfName(nameof(PdfStructureType.Document)),
            ["P"] = _rootReference,
        };
        return new PdfStructureElement(this, null, dictionary, _doc.AddObject(dictionary));
    }

    /// <summary>The existing tree's Document element: the only element at its top, when it is a Document or maps to one.</summary>
    private PdfStructureElement? ExistingDocument()
    {
        PdfObject[] top = _doc.Resolve(_root["K"]) switch
        {
            PdfArray a => [.. a.Items],
            PdfDictionary => [_root["K"]!],
            _ => [],
        };
        if (top.Length != 1 || _doc.Resolve(top[0]) is not PdfDictionary element || element.GetName("S") is not string type) return null;
        var roles = _doc.Resolve(_root["RoleMap"]) as PdfDictionary;
        for (int i = 0; i < 16 && !IsStandard(type) && roles?.GetName(type) is string mapped; i++) type = mapped;
        if (type != nameof(PdfStructureType.Document)) return null;

        PdfReference reference = top[0] as PdfReference ?? _doc.AddObject(element);
        if (top[0] is not PdfReference) _root.Set("K", PdfArray.Of(reference));
        return new PdfStructureElement(this, null, element, reference);
    }

    /// <summary>The existing IDTree's IDs, so new ones do not repeat them.</summary>
    private void CollectIds()
    {
        if (_doc.Resolve(_root["IDTree"]) is not PdfDictionary tree) return;
        foreach ((PdfObject key, PdfObject value) in ReadTree(_doc, tree, "Names", out _))
        {
            if (_doc.Resolve(key) is PdfString s && value is PdfReference r) _ids[s.ToText()] = r;
        }
    }

    /// <summary>
    /// Reads the catalog tree's parent tree into <see cref="_parentTree"/>. Its /ParentTreeNextKey
    /// is a hint only: keys a page import added may lie beyond it.
    /// </summary>
    /// <returns>The first key after every key the tree holds.</returns>
    private int ReadParentTree()
    {
        _parentTree.Clear();
        long next = _doc.Resolve(_root["ParentTreeNextKey"]) is PdfNumber { IsInteger: true, IntegerValue: >= 0 } n ? n.IntegerValue : 0;
        if (_doc.Resolve(_root["ParentTree"]) is PdfDictionary tree)
        {
            foreach ((PdfObject key, PdfObject value) in ReadTree(_doc, tree, "Nums", out _))
            {
                if (_doc.Resolve(key) is not PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } k) continue;
                _parentTree[(int)k.IntegerValue] = value;
                next = Math.Max(next, k.IntegerValue + 1);
            }
        }

        int free = (int)Math.Min(next, int.MaxValue - 1);
        _nextKey = Math.Max(_nextKey, free);
        return free;
    }

    /// <summary>
    /// The entries of a name or number tree (ISO 32000-1, 7.9.6 and 7.9.7), in order, through at
    /// most 32 levels and <see cref="MaxNodes"/> nodes. <paramref name="complete"/> is false when
    /// a bound cut the walk short, so callers do not rewrite a tree they read only in part.
    /// </summary>
    internal static List<(PdfObject Key, PdfObject Value)> ReadTree(PdfDocument doc, PdfDictionary root, string leafKey, out bool complete)
    {
        var entries = new List<(PdfObject, PdfObject)>();
        var visited = new HashSet<PdfDictionary>();
        var pending = new Stack<(PdfDictionary Node, int Depth)>();
        pending.Push((root, 0));
        complete = true;
        while (pending.Count > 0)
        {
            (PdfDictionary node, int depth) = pending.Pop();
            if (depth > 32 || visited.Count >= MaxNodes)
            {
                complete = false;
                break;
            }

            if (!visited.Add(node)) continue;
            if (doc.Resolve(node[leafKey]) is PdfArray leaf)
            {
                for (int i = 0; i + 1 < leaf.Count; i += 2) entries.Add((leaf[i], leaf[i + 1]));
            }

            if (doc.Resolve(node["Kids"]) is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (doc.Resolve(kids[i]) is PdfDictionary kid) pending.Push((kid, depth + 1));
                }
            }
        }

        return entries;
    }

    private PageEntry Page(PdfPage page)
    {
        if (!ReferenceEquals(page.Owner, _doc)) throw new ArgumentException("The page belongs to another document.", nameof(page));
        if (_pages.TryGetValue(page.Number, out PageEntry? entry)) return entry;
        PdfArray content;
        if (Adopted(page) is PdfArray adopted)
        {
            content = adopted;
        }
        else
        {
            content = new PdfArray();
            int key = _nextKey++;
            _own[key] = (content, page.Dictionary, "StructParents");
            page.Dictionary.Set("StructParents", PdfNumber.FromInteger(key));
        }

        // MCIDs already in the page's content, from the opened file, must not be given again.
        entry = new PageEntry(content, Math.Max(content.Count, HighestMcid(page) + 1));
        _pages[page.Number] = entry;
        return entry;
    }

    /// <summary>
    /// The parent tree array of a page that already has tagged content, read again from the
    /// catalog's tree when its key is new since the last read, as after a page import.
    /// </summary>
    private PdfArray? Adopted(PdfPage page)
    {
        if (_doc.Resolve(page.Dictionary["StructParents"]) is not PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } n) return null;
        int key = (int)n.IntegerValue;
        if (_own.ContainsKey(key)) return null;
        if (!_parentTree.ContainsKey(key) && _doc.Resolve(_doc.Catalog["StructTreeRoot"]) is PdfDictionary current)
        {
            if (!ReferenceEquals(current, _root)) Join(current);
            ReadParentTree();
        }

        return _parentTree.TryGetValue(key, out PdfObject? existing) ? _doc.Resolve(existing) as PdfArray : null;
    }

    /// <summary>The highest MCID the page's content streams use, or -1.</summary>
    private int HighestMcid(PdfPage page)
    {
        IEnumerable<PdfObject> parts = _doc.Resolve(page.Dictionary["Contents"]) switch
        {
            PdfArray a => a,
            PdfStream s => [s],
            _ => [],
        };
        PdfDictionary? properties = _doc.Resolve((_doc.Resolve(page.Dictionary["Resources"]) as PdfDictionary)?["Properties"]) as PdfDictionary;
        int highest = -1;
        foreach (PdfObject part in parts)
        {
            if (_doc.Resolve(part) is not PdfStream stream) continue;
            byte[] data;
            try
            {
                data = _doc.GetStreamData(stream);
            }
            catch (PdfFormatException)
            {
                continue;
            }

            var reader = new ContentReader(data);
            var op = new ContentOperation();
            while (reader.Next(op))
            {
                if (op.Operator != "BDC" || op.Operands.Count < 2) continue;
                PdfDictionary? list = op.Operands[op.Operands.Count - 1] switch
                {
                    PdfDictionary d => d,
                    PdfName name => _doc.Resolve(properties?[name.Value]) as PdfDictionary,
                    _ => null,
                };
                if (list?["MCID"] is PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } mcid) highest = Math.Max(highest, (int)mcid.IntegerValue);
            }
        }

        return highest;
    }

    /// <summary>An annotation written directly in the page's /Annots becomes an indirect object, as an object reference needs.</summary>
    private PdfReference MakeIndirect(PdfPage page, PdfDictionary annotation)
    {
        PdfReference reference = _doc.AddObject(annotation);
        if (_doc.Resolve(page.Dictionary["Annots"]) is PdfArray annots)
        {
            for (int i = 0; i < annots.Count; i++)
            {
                if (ReferenceEquals(annots[i], annotation)) annots[i] = reference;
            }
        }

        return reference;
    }

    /// <summary>A page's parent tree entry: the structure element of each MCID, and the next MCID to give.</summary>
    private sealed class PageEntry(PdfArray content, int next)
    {
        public PdfArray Content { get; } = content;

        public int Next { get; set; } = next;
    }
}

/// <summary>
/// A structure element of a tagged PDF: a heading, paragraph, table cell, figure and so on, with
/// its children in reading order. Create elements with <see cref="Add(PdfStructureType)"/> or
/// <see cref="Drawing.PdfGraphics.BeginTag(PdfStructureType)"/>; content drawn between
/// <see cref="Drawing.PdfGraphics.BeginTag(PdfStructureElement)"/> and
/// <see cref="Drawing.PdfGraphics.EndTag"/> belongs to it.
/// </summary>
public sealed class PdfStructureElement
{
    private readonly PdfStructureTree _tree;

    internal PdfStructureElement(PdfStructureTree tree, PdfStructureElement? parent, PdfDictionary dictionary, PdfReference reference)
    {
        _tree = tree;
        Parent = parent;
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The structure type, such as "P" or "H1", or a custom type mapped with <see cref="PdfStructureTree.MapRole"/>.</summary>
    public string Type => Dictionary.GetName("S") ?? "";

    /// <summary>The parent element; null for the tree's Document element.</summary>
    public PdfStructureElement? Parent { get; }

    /// <summary>The structure element dictionary, for entries the API does not cover.</summary>
    public PdfDictionary Dictionary { get; }

    internal PdfReference Reference { get; }

    internal PdfStructureTree Tree => _tree;

    /// <summary>
    /// Alternate text (/Alt): a description read in place of the content, which figures and
    /// formulas need. For a figure, say what it shows or means, not that it is an image.
    /// </summary>
    public string? AlternateText
    {
        get => Text("Alt");
        set => SetText("Alt", value);
    }

    /// <summary>Replacement text (/ActualText): the exact text the content shows, such as a word drawn as a picture.</summary>
    public string? ActualText
    {
        get => Text("ActualText");
        set => SetText("ActualText", value);
    }

    /// <summary>The expansion of an abbreviation or acronym (/E), such as "Value Added Tax" for "VAT".</summary>
    public string? ExpandedText
    {
        get => Text("E");
        set => SetText("E", value);
    }

    /// <summary>The element's natural language as a BCP 47 tag (/Lang), when it differs from the document's.</summary>
    public string? Language
    {
        get => Text("Lang");
        set => SetText("Lang", value);
    }

    /// <summary>A title for the element (/T), such as a section's name.</summary>
    public string? Title
    {
        get => Text("T");
        set => SetText("T", value);
    }

    /// <summary>A unique identifier (/ID), listed in the tree's IDTree. Notes get one automatically.</summary>
    /// <exception cref="ArgumentException">Another element has this ID.</exception>
    public string? Id
    {
        get => Text("ID");
        set
        {
            if (value is not null && value.Length == 0) throw new ArgumentException("An ID cannot be empty.", nameof(value));
            _tree.SetId(this, Id, value);
            SetText("ID", value);
        }
    }

    /// <summary>For a table header cell: the cells it heads (the Table attribute /Scope). PDF/UA needs it on every TH.</summary>
    public PdfTableHeaderScope? Scope
    {
        get => Attributes("Table", create: false)?.GetName("Scope") switch
        {
            "Row" => PdfTableHeaderScope.Row,
            "Column" => PdfTableHeaderScope.Column,
            "Both" => PdfTableHeaderScope.Both,
            _ => null,
        };
        set
        {
            if (value is PdfTableHeaderScope scope) Attributes("Table", create: true)!.Set("Scope", new PdfName(scope.ToString()));
            else Attributes("Table", create: false)?.Remove("Scope");
        }
    }

    /// <summary>For a table cell: the rows it spans (the Table attribute /RowSpan). Default 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int RowSpan
    {
        get => Span("RowSpan");
        set => SetSpan("RowSpan", value);
    }

    /// <summary>For a table cell: the columns it spans (the Table attribute /ColSpan). Default 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int ColumnSpan
    {
        get => Span("ColSpan");
        set => SetSpan("ColSpan", value);
    }

    /// <summary>For a list: how its items are labelled (the List attribute /ListNumbering), or null when not said.</summary>
    public PdfListNumbering? ListNumbering
    {
        get => Attributes("List", create: false)?.GetName("ListNumbering") is string name && Enum.TryParse(name, out PdfListNumbering numbering) && numbering.ToString() == name ? numbering : null;
        set
        {
            if (value is PdfListNumbering numbering) Attributes("List", create: true)!.Set("ListNumbering", new PdfName(numbering.ToString()));
            else Attributes("List", create: false)?.Remove("ListNumbering");
        }
    }

    /// <summary>Adds a child element of a standard type after the existing children.</summary>
    /// <returns>The new element.</returns>
    public PdfStructureElement Add(PdfStructureType type) => _tree.NewElement(this, type.ToString());

    /// <summary>
    /// Adds a child element after the existing children. A type that is not standard must be
    /// mapped to a standard one with <see cref="PdfStructureTree.MapRole"/> before saving as PDF/UA.
    /// </summary>
    /// <param name="type">A standard type name, or a custom one of letters, digits, '_', '-' and '.'.</param>
    /// <returns>The new element.</returns>
    /// <exception cref="ArgumentException"><paramref name="type"/> is empty or not a plain name.</exception>
    public PdfStructureElement Add(string type)
    {
        PdfStructureTree.CheckTypeName(type);
        return _tree.NewElement(this, type);
    }

    /// <summary>
    /// Makes an annotation a child of this element, after the existing children: a link
    /// annotation under its Link element, any other annotation (except a form field's widget)
    /// under an Annot element. For PDF/UA, give the annotation <see cref="PdfAnnotation.Contents"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The annotation belongs to another document.</exception>
    /// <exception cref="InvalidOperationException">The annotation is already in the structure tree.</exception>
    public void AddAnnotation(PdfAnnotation annotation)
    {
        Compat.ThrowIfNull(annotation, nameof(annotation));
        _tree.AddAnnotation(this, annotation.Page, annotation.Dictionary, annotation.Entry);
    }

    /// <summary>
    /// Makes a form field's widget a child of this element, which should be a Form element. For
    /// PDF/UA, give the field a tooltip (<see cref="PdfFieldOptions.ToolTip"/>) or this element
    /// <see cref="AlternateText"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The widget is on no page, or on a page of another document.</exception>
    /// <exception cref="InvalidOperationException">The widget is already in the structure tree.</exception>
    public void AddAnnotation(PdfWidget widget)
    {
        Compat.ThrowIfNull(widget, nameof(widget));
        PdfPage page = widget.Page ?? throw new ArgumentException("The widget is not on a page.", nameof(widget));
        _tree.AddAnnotation(this, page, widget.Dictionary, widget.Entry);
    }

    /// <inheritdoc/>
    public override string ToString() => Type;

    private string? Text(string key) => _tree.Document.Resolve(Dictionary[key]) is PdfString s ? s.ToText() : null;

    private void SetText(string key, string? value)
    {
        if (value is null) Dictionary.Remove(key);
        else Dictionary.Set(key, PdfString.FromText(value));
    }

    private int Span(string key) => Attributes("Table", create: false)?[key] is PdfNumber { IsInteger: true, IntegerValue: > 1 and <= int.MaxValue } n ? (int)n.IntegerValue : 1;

    private void SetSpan(string key, int value)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "A cell spans at least one row and one column.");
        if (value > 1) Attributes("Table", create: true)!.Set(key, PdfNumber.FromInteger(value));
        else Attributes("Table", create: false)?.Remove(key);
    }

    /// <summary>The attribute dictionary of an owner, such as Table or List, in /A (ISO 32000-1, 14.7.5); created on demand.</summary>
    private PdfDictionary? Attributes(string owner, bool create)
    {
        PdfDocument doc = _tree.Document;
        PdfObject? entry = Dictionary["A"];
        switch (doc.Resolve(entry))
        {
            case PdfDictionary d when d.GetName("O") == owner:
                return d;
            case PdfArray list:
                foreach (PdfObject item in list)
                {
                    if (doc.Resolve(item) is PdfDictionary d && d.GetName("O") == owner) return d;
                }

                break;
        }

        if (!create) return null;
        var created = new PdfDictionary { ["O"] = new PdfName(owner) };
        switch (doc.Resolve(entry))
        {
            case PdfArray list:
                list.Add(created);
                break;
            case PdfDictionary:
                Dictionary.Set("A", PdfArray.Of(entry!, created));
                break;
            default:
                Dictionary.Set("A", created);
                break;
        }

        return created;
    }
}
