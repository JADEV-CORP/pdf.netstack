using System.Globalization;
using PdfNetStack.Filters;
using PdfNetStack.Objects;

namespace PdfNetStack.Writing;

/// <summary>
/// Writes a linearized PDF (ISO 32000-1, Annex F): the first page and everything needed to show
/// it come first with a cross-reference section of their own, so a viewer reading over a network
/// can display page one before the rest arrives, and a primary hint stream tells it where every
/// other page and shared object starts.
/// </summary>
/// <remarks>
/// <para>
/// Objects are assigned to the parts of Annex F by what uses them, the way qpdf 12.4.2 does it,
/// so its <c>--check-linearization</c> computes the same layout from the output: the catalog and
/// what opening the document needs (viewer preferences, open action, interactive form) in part 4;
/// the first page with everything it uses in part 6; each further page with what no other page
/// uses in part 7; objects several later pages share in part 8; and the page tree, thumbnails,
/// outlines, document information and the rest in part 9. An object a page shares with the
/// outlines or the structure tree goes with the page; qpdf before 12.4.2 put it in part 9, so
/// its check reports such files. The hint tables use one object per shared group. Page objects
/// must not inherit attributes; see <see cref="Optimization.InheritedAttributes"/>.
/// </para>
/// <para>
/// With object streams (PDF 1.5) the objects of a part are packed only with objects of the same
/// part. A later page whose own objects are enough to compress on their own gets an object
/// stream in its section; smaller groups are pooled in page order with the objects later pages
/// share, in part 8 streams that each page using them lists, so the bytes a viewer fetches for
/// a page still hold what the page needs. Page objects and the catalog stay outside object
/// streams, and both cross-reference sections are streams. A reader of the hint tables, qpdf's
/// check among them, takes an object stream in place of the objects it holds, used by whatever
/// uses any of them; the parts are computed again that way and the file is laid out by them.
/// When the object streams cannot be laid out, the file is written without them and the result
/// says why.
/// </para>
/// </remarks>
internal static class LinearizedWriter
{
    private static readonly HashSet<string> OpenDocumentKeys = new(StringComparer.Ordinal) { "ViewerPreferences", "PageMode", "Threads", "OpenAction", "AcroForm" };
    private static readonly byte[] BinaryMarker = [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];

    // Width every number in the linearization dictionary and the first trailer is padded to,
    // so their size is known before the offsets they hold.
    private const int NumberWidth = 10;

    // What an object costs written on its own besides its body ("n 0 obj", "endobj"), and what an
    // object stream costs besides its compressed content (its dictionary and keywords). They
    // decide whether packing a handful of small objects saves anything.
    private const int LooseObjectOverhead = 20;
    private const int ObjectStreamOverhead = 90;

    // Objects this large together always pay for an object stream; below it a trial deflate decides.
    private const int TrialDeflateLimit = 4096;

    // A later page keeps an object stream of its own in its section when what only it uses comes
    // to this much. Less deflates poorly on its own (a few link annotations, a font dictionary),
    // so it is pooled with the neighbouring pages' instead: on NIST SP 800-53 that takes 30% off
    // what linearizing costs over the plain rewrite.
    private const int OwnStreamMinimum = 4096;

    // The first page cross-reference stream holds offsets of what follows it, so its size is
    // found by laying the file out again until the space kept for it is enough.
    private const int MaxLayoutPasses = 8;
    private const int FirstXrefSlack = 8;

    /// <summary>A linearized file, and why object streams were not used when the settings asked for them.</summary>
    internal readonly record struct Result(ReadOnlyMemory<byte> Bytes, string? WithoutObjectStreams);

    private enum UserKind
    {
        Page,
        Thumbnail,
        TrailerKey,
        RootKey,
        Root,
    }

    private readonly record struct User(UserKind Kind, int Page = 0, string Key = "");

    /// <summary>
    /// Where an object goes, as qpdf 12.4.2 decides it. The order is precedence: when uses
    /// combine, the lower value generally wins (see <see cref="Placement.With"/>).
    /// </summary>
    private enum Category
    {
        Root,
        OpenDocument,
        FirstPageShared,
        FirstPagePrivate,
        OtherPageShared,
        OtherPagePrivate,
        ThumbnailShared,
        ThumbnailPrivate,
        PagesTree,
        OutlinesRoot,
        Outlines,
        Other,
    }

    /// <summary>An object's category and, once a page or thumbnail uses it, the lowest such page.</summary>
    private readonly record struct Placement(Category Category, int Page)
    {
        /// <summary>Before any use.</summary>
        public static Placement Unused => new(Category.Other, -1);

        /// <summary>
        /// The placement once <paramref name="user"/> uses the object as well; <paramref name="top"/>
        /// when the object is the value the use starts from. This is qpdf's ObjCategory::update:
        /// whatever the first page uses goes in its section, unless part 4 needs it first; what
        /// the first page and a later one use is shared; what two later pages use is shared;
        /// what one later page uses is that page's, whatever else uses it.
        /// </summary>
        public Placement With(User user, bool top)
        {
            (Category used, int page) = Of(user, top);
            Category min = Category < used ? Category : used;
            Category max = Category < used ? used : Category;
            int merged = Page <= 0 || page <= 0 ? Math.Max(Page, page) : Math.Min(Page, page);
            return min switch
            {
                <= Category.FirstPageShared => new(min, merged),
                Category.FirstPagePrivate => new(max is Category.OtherPagePrivate or Category.OtherPageShared ? Category.FirstPageShared : Category.FirstPagePrivate, merged),
                Category.OtherPagePrivate => new(max == Category.OtherPagePrivate && Page != page ? Category.OtherPageShared : Category.OtherPagePrivate, merged),
                Category.ThumbnailPrivate => new(max == Category.ThumbnailPrivate && Page != page ? Category.ThumbnailShared : Category.ThumbnailPrivate, merged),

                // Shared stays shared; the page tree, outlines and the rest keep the highest precedence.
                _ => new(min, merged),
            };
        }

        private static (Category Category, int Page) Of(User user, bool top) => user.Kind switch
        {
            UserKind.Page => (user.Page == 0 ? Category.FirstPagePrivate : Category.OtherPagePrivate, user.Page),
            UserKind.Thumbnail => (Category.ThumbnailPrivate, user.Page),
            UserKind.Root => (Category.Root, -1),
            UserKind.RootKey when OpenDocumentKeys.Contains(user.Key) => (Category.OpenDocument, -1),
            UserKind.RootKey when user.Key == "Outlines" => (top ? Category.OutlinesRoot : Category.Outlines, -1),
            UserKind.RootKey when user.Key == "Pages" => (Category.PagesTree, -1),
            UserKind.TrailerKey when user.Key == "Encrypt" => (Category.OpenDocument, -1),
            _ => (Category.Other, -1),
        };
    }

    /// <summary>The linearized file, or null when the objects have no page tree to lay out.</summary>
    /// <param name="objects">Object i is numbered i + 1 in <paramref name="mapReference"/>'s numbering.</param>
    /// <param name="trailer">Root, Info and ID.</param>
    /// <param name="mapReference">Maps any reference in the objects to its object's number, or -1.</param>
    /// <param name="settings">
    /// Compression, object streams and their size, and the version of a file with object streams.
    /// Encryption is not used.
    /// </param>
    /// <param name="classicVersion">The version of a file written without object streams.</param>
    /// <param name="ct">Observed while object streams are deflated.</param>
    public static Result? Write(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings, string classicVersion, CancellationToken ct = default)
    {
        var layout = Layout.Build(objects, trailer, mapReference, unitOf: null);
        if (layout is null) return null;

        WriterSettings classic = settings with { UseObjectStreams = false, Version = classicVersion };
        if (!settings.UseObjectStreams) return new Result(Assemble(layout, objects, trailer, mapReference, classic), null);

        string? reason = WriteCompact(layout, objects, trailer, mapReference, settings, ct, out ReadOnlyMemory<byte> compact);
        return reason is null
            ? new Result(compact, null)
            : new Result(Assemble(layout, objects, trailer, mapReference, classic), reason);
    }

    /// <summary>The file with object streams, or why it cannot be laid out.</summary>
    private static string? WriteCompact(Layout objectLayout, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings, CancellationToken ct, out ReadOnlyMemory<byte> file)
    {
        file = default;
        Packing packing = Pack(objectLayout, objects, mapReference, settings);

        // The parts again, as a reader of the hint tables sees them: each object stream as one
        // object used by everything that uses what it holds.
        Layout? layout = Layout.Build(objects, trailer, mapReference, n => packing.UnitOf[n]);
        return layout is null
            ? "the parts cannot be laid out with object streams"
            : AssembleCompact(layout, packing, objects, trailer, mapReference, settings, ct, out file);
    }

    /// <summary>
    /// Which objects go where. Without object streams every object is placed on its own, in the
    /// numbering of the input; with them, an object stream is placed in place of the objects it
    /// holds and numbered after every object (see <see cref="Packing"/>).
    /// </summary>
    private sealed class Layout
    {
        /// <summary>The catalog's number.</summary>
        public int Root { get; private set; }

        /// <summary>Where each object, or object stream, goes, from every use of it.</summary>
        public Dictionary<int, Placement> Placements { get; } = [];

        public List<int> Part4 { get; } = [];

        public List<int> Part6 { get; } = [];

        public List<List<int>> Part7 { get; } = [];

        public List<int> Part8 { get; } = [];

        public List<int> Part9 { get; } = [];

        public List<int> Pages { get; } = [];

        public int OutlineRoot { get; private set; } = -1;

        public int OutlineCount { get; private set; }

        /// <summary>For each page after the first, the objects it shares that the shared object hint table lists.</summary>
        public List<List<int>> SharedReferences { get; } = [];

        /// <param name="unitOf">
        /// The object stream an object is written in, or the object itself; null when no object
        /// is in an object stream. Page objects and the catalog must be their own.
        /// </param>
        public static Layout? Build(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> map, Func<int, int>? unitOf)
        {
            int Target(PdfObject? o) => o is PdfReference r && map(r) is var n && n >= 1 && n <= objects.Count ? n : -1;
            int Unit(int n) => n > 0 && unitOf is not null ? unitOf(n) : n;
            PdfObject Get(int n) => objects[n - 1];

            int rootNumber = Target(trailer["Root"]);
            if (rootNumber < 0 || Get(rootNumber) is not PdfDictionary catalog || Unit(rootNumber) != rootNumber) return null;

            var layout = new Layout { Root = rootNumber };
            if (!CollectPages(catalog["Pages"], 0, new HashSet<int>())) return null;
            if (layout.Pages.Count == 0 || layout.Pages.Exists(p => Unit(p) != p)) return null;

            // Every use of an object, found the way qpdf finds it: page by page without crossing
            // into other pages or back up the tree, then the trailer's and the catalog's entries.
            // A use of an object in an object stream is a use of the object stream.
            Dictionary<int, Placement> placements = layout.Placements;
            var pageUnits = new List<SortedSet<int>>(layout.Pages.Count);
            for (int i = 0; i < layout.Pages.Count; i++)
            {
                pageUnits.Add([]);
                Traverse(new User(UserKind.Page, i), PdfNull.Instance, layout.Pages[i]);
            }

            foreach ((string key, PdfObject value) in Entries(trailer))
            {
                if (key != "Root" && !IsNull(value)) Traverse(new User(UserKind.TrailerKey, Key: key), value, -1);
            }

            foreach ((string key, PdfObject value) in Entries(catalog))
            {
                if (!IsNull(value)) Traverse(new User(UserKind.RootKey, Key: key), value, -1);
            }

            Use(new User(UserKind.Root), rootNumber, top: true);

            // A page object is used by its own page only, and the catalog is the root.
            if (placements[rootNumber].Category != Category.Root) return null;
            for (int i = 0; i < layout.Pages.Count; i++)
            {
                Placement page = placements[layout.Pages[i]];
                if (page.Category != (i == 0 ? Category.FirstPagePrivate : Category.OtherPagePrivate) || (i > 0 && page.Page != i)) return null;
            }

            // Each category to its part, in number order within a category, as qpdf orders them.
            var open = new List<int>();
            var firstPrivate = new List<int>();
            var firstShared = new List<int>();
            var otherPrivate = new List<int>[layout.Pages.Count];
            var thumbnails = new List<int>[layout.Pages.Count];
            var thumbShared = new List<int>();
            var outlineRoot = new List<int>();
            var outlines = new List<int>();
            var pagesTree = new List<int>();
            var other = new List<int>();
            var shared = new HashSet<int>();
            for (int i = 0; i < layout.Pages.Count; i++)
            {
                otherPrivate[i] = [];
                thumbnails[i] = [];
            }

            var thumbOf = layout.Pages.Select(p => Unit(Target(((PdfDictionary)Get(p))["Thumb"]))).ToList();
            foreach ((int unit, Placement placement) in placements.OrderBy(e => e.Key))
            {
                bool hasPage = placement.Page >= 0 && placement.Page < layout.Pages.Count;
                switch (placement.Category)
                {
                    case Category.Root:
                        if (unit != rootNumber) return null;
                        break;
                    case Category.OpenDocument:
                        open.Add(unit);
                        break;
                    case Category.FirstPageShared:
                        firstShared.Add(unit);
                        shared.Add(unit);
                        break;
                    case Category.FirstPagePrivate:
                        if (unit != layout.Pages[0]) firstPrivate.Add(unit);
                        break;
                    case Category.OtherPageShared:
                        layout.Part8.Add(unit);
                        shared.Add(unit);
                        break;
                    case Category.OtherPagePrivate:
                        if (!hasPage) return null;
                        if (unit != layout.Pages[placement.Page]) otherPrivate[placement.Page].Add(unit);
                        break;
                    case Category.ThumbnailPrivate:
                        if (!hasPage) return null;

                        // The thumbnail image comes before what only it uses.
                        if (unit == thumbOf[placement.Page]) thumbnails[placement.Page].Insert(0, unit);
                        else thumbnails[placement.Page].Add(unit);
                        break;
                    case Category.ThumbnailShared:
                        thumbShared.Add(unit);
                        break;
                    case Category.PagesTree:
                        pagesTree.Add(unit);
                        break;
                    case Category.OutlinesRoot:
                        outlineRoot.Add(unit);
                        break;
                    case Category.Outlines:
                        outlines.Add(unit);
                        break;
                    default:
                        other.Add(unit);
                        break;
                }
            }

            // Part 4: the catalog and what opening the document needs.
            layout.Part4.Add(rootNumber);
            layout.Part4.AddRange(open);

            // Part 6: the first page object, then what only it uses, then what it shares, then
            // the outlines when the document opens on them.
            layout.Part6.Add(layout.Pages[0]);
            layout.Part6.AddRange(firstPrivate);
            layout.Part6.AddRange(firstShared);
            bool outlinesFirst = catalog.GetName("PageMode") == "UseOutlines" && catalog["Outlines"] is not null;
            if (outlinesFirst) layout.PlaceOutlines(layout.Part6, outlineRoot, outlines);

            // Part 7: every other page, page object first, with what only that page uses.
            for (int i = 1; i < layout.Pages.Count; i++)
            {
                layout.Part7.Add([layout.Pages[i], .. otherPrivate[i]]);
            }

            // Part 8 holds what several later pages share; part 9 the page tree, thumbnails,
            // outlines, then everything else.
            layout.Part9.AddRange(pagesTree);
            foreach (List<int> thumbnail in thumbnails) layout.Part9.AddRange(thumbnail);
            layout.Part9.AddRange(thumbShared);
            if (!outlinesFirst) layout.PlaceOutlines(layout.Part9, outlineRoot, outlines);
            layout.Part9.AddRange(other);

            // Objects no use reaches, such as those behind a page object that is not in the
            // page tree, are still referenced; they go last.
            layout.Part9.AddRange(Referenced(objects, trailer, Target).Select(Unit).Where(n => !placements.ContainsKey(n)).Distinct().OrderBy(n => n));

            // Shared object references of each later page: what it reaches of parts 6 and 8 that
            // is shared, which is what the shared object hint table lists.
            for (int i = 1; i < layout.Pages.Count; i++)
            {
                layout.SharedReferences.Add(pageUnits[i].Where(shared.Contains).ToList());
            }

            return layout;

            bool CollectPages(PdfObject? node, int depth, HashSet<int> path)
            {
                int n = Target(node);
                if (n < 0 || depth > 64 || !path.Add(n)) return false;
                if (Get(n) is not PdfDictionary dict) return false;
                if (dict.GetName("Type") == "Page")
                {
                    layout.Pages.Add(n);
                    return true;
                }

                if (dict.GetName("Type") != "Pages" || dict["Kids"] is not PdfArray kids) return false;
                foreach (PdfObject kid in kids)
                {
                    if (!CollectPages(kid, depth + 1, path)) return false;
                }

                return true;
            }

            // A dictionary entry whose value is null, directly or through a reference, is absent:
            // qpdf does not follow it, though it follows an array item that refers to a null.
            bool IsNull(PdfObject value) => value is PdfNull || (Target(value) is var n && n > 0 && Get(n) is PdfNull);

            void Use(User user, int unit, bool top)
            {
                placements[unit] = placements.TryGetValue(unit, out Placement current) ? current.With(user, top) : Placement.Unused.With(user, top);
                if (user.Kind == UserKind.Page) pageUnits[user.Page].Add(unit);
            }

            // One walk from a starting value, its visited set shared by the thumbnail it may
            // branch into, as qpdf walks it. A page met anywhere but at the start is not entered,
            // nor, from a page, a page tree node.
            void Traverse(User user, PdfObject start, int startNumber)
            {
                var visited = new HashSet<int>();
                var pending = new Stack<(User User, PdfObject Value, int Number, bool Top, int Depth)>();
                pending.Push((user, start, startNumber, true, 0));
                while (pending.Count > 0)
                {
                    (User owner, PdfObject value, int number, bool top, int depth) = pending.Pop();
                    if (depth > Parsing.PdfParser.MaxNesting) continue;
                    if (number < 0)
                    {
                        number = Target(value);
                        if (value is PdfReference && number < 0) continue;
                    }

                    PdfObject current = number > 0 ? Get(number) : value;
                    bool isPage = false;
                    if (number > 0)
                    {
                        if (!visited.Add(number)) continue;
                        if (current is PdfDictionary { } typed && typed.GetName("Type") is { } type)
                        {
                            isPage = type == "Page";
                            if (isPage && !top) continue;
                            if (type == "Pages" && user.Kind == UserKind.Page) continue;
                        }

                        Use(owner, Unit(number), top);
                    }

                    switch (current)
                    {
                        case PdfArray array:
                            for (int i = 0; i < array.Count; i++) pending.Push((owner, array[i], -1, false, depth + 1));
                            break;
                        case PdfDictionary dict:
                            Push(owner, dict, isPage, isStream: false, depth);
                            break;
                        case PdfStream stream:
                            Push(owner, stream.Dictionary, isPage: false, isStream: true, depth);
                            break;
                    }
                }

                void Push(User owner, PdfDictionary dict, bool isPage, bool isStream, int depth)
                {
                    foreach ((string key, PdfObject value) in Entries(dict))
                    {
                        if (IsNull(value)) continue;

                        // The writer puts /Length in the stream dictionary directly.
                        if (isStream && key == "Length") continue;
                        if (isPage && key == "Parent") continue;
                        User next = isPage && key == "Thumb" ? new User(UserKind.Thumbnail, owner.Page) : owner;
                        pending.Push((next, value, -1, false, depth + 1));
                    }
                }
            }
        }

        /// <summary>
        /// The outline dictionary, then the rest of the outlines, which the outline hint table
        /// gives as one run. Without the dictionary (another part took it), the run starts at
        /// the first of the rest, as qpdf counts it.
        /// </summary>
        private void PlaceOutlines(List<int> part, List<int> root, List<int> rest)
        {
            if (root.Count + rest.Count == 0) return;
            OutlineRoot = root.Count > 0 ? root[0] : rest[0];
            OutlineCount = root.Count + rest.Count;
            part.AddRange(root);
            part.AddRange(rest);
        }

        /// <summary>Entries in name order, the order qpdf visits them in.</summary>
        private static IEnumerable<(string Key, PdfObject Value)> Entries(PdfDictionary d)
        {
            var entries = new List<(string, PdfObject)>(d.Count);
            for (int i = 0; i < d.Count; i++) entries.Add((d.EntryAt(i).Key, d.ValueAt(i)));
            return entries.OrderBy(e => e.Item1, StringComparer.Ordinal);
        }

        /// <summary>Every object the written file refers to, from the trailer on.</summary>
        private static HashSet<int> Referenced(IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfObject?, int> target)
        {
            var found = new HashSet<int>();
            var pending = new Stack<(PdfObject Value, int Depth)>();
            pending.Push((trailer, 0));
            while (pending.Count > 0)
            {
                (PdfObject value, int depth) = pending.Pop();
                if (depth > Parsing.PdfParser.MaxNesting) continue;
                switch (value)
                {
                    case PdfReference:
                        int n = target(value);
                        if (n > 0 && found.Add(n)) pending.Push((objects[n - 1], depth + 1));
                        break;
                    case PdfArray array:
                        foreach (PdfObject item in array.Items) pending.Push((item, depth + 1));
                        break;
                    case PdfDictionary dict:
                        for (int i = 0; i < dict.Count; i++) pending.Push((dict.ValueAt(i), depth + 1));
                        break;
                    case PdfStream stream:
                        foreach ((string key, PdfObject item) in Entries(stream.Dictionary))
                        {
                            if (key != "Length") pending.Push((item, depth + 1));
                        }

                        break;
                }
            }

            return found;
        }
    }

    private static ReadOnlyMemory<byte> Assemble(Layout layout, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings)
    {
        // Numbers: parts 7, 8 and 9 from 1 in file order, then the linearization dictionary,
        // part 4, part 6 and the hint stream, which the first page's cross-reference section covers.
        var main = layout.Part7.SelectMany(p => p).Concat(layout.Part8).Concat(layout.Part9).ToList();
        var renumber = new Dictionary<int, int>();
        foreach (int n in main) renumber[n] = renumber.Count + 1;
        int mainCount = main.Count;
        int lindictNumber = mainCount + 1;
        int next = lindictNumber + 1;
        foreach (int n in layout.Part4.Concat(layout.Part6)) renumber[n] = next++;
        int hintNumber = next;
        int size = hintNumber + 1;
        int firstSectionCount = size - lindictNumber;

        int Map(PdfReference r) => mapReference(r) is var n && renumber.TryGetValue(n, out int m) ? m : -1;
        var serializer = new PdfSerializer(Map);
        var plain = settings with { Encryption = null, EncryptionDictionaryNumber = -1 };
        var bodies = new Dictionary<int, byte[]>(renumber.Count);
        var sink = new ByteSink(4096);
        foreach ((int old, int number) in renumber)
        {
            sink.Clear();
            PdfDocumentWriter.WriteIndirect(sink, serializer, number, objects[old - 1], plain);
            bodies[old] = sink.ToArray();
        }

        // Fixed size pieces before the objects.
        byte[] header = [.. System.Text.Encoding.ASCII.GetBytes("%PDF-" + settings.Version + "\n"), .. BinaryMarker];
        int lindictLength = Lindict(lindictNumber, 0, 0, 0, 0, 0, 0, 0).Length;
        int firstXrefLength = FirstXrefHeader(lindictNumber, firstSectionCount).Length + 20 * firstSectionCount;
        int firstTrailerLength = FirstTrailer(trailer, serializer, size, 0).Length;
        long firstXrefOffset = header.Length + lindictLength;

        // Offsets as if the hint stream were absent, which is how the hint tables give them.
        var offset = new Dictionary<int, long>(renumber.Count);
        long position = firstXrefOffset + firstXrefLength + firstTrailerLength;
        foreach (int n in layout.Part4) Place(n);
        long hintOffset = position;
        foreach (int n in layout.Part6) Place(n);
        long firstPageEnd = position;
        foreach (int n in main) Place(n);
        long mainXrefOffset = position;

        byte[] hint = HintStream(hintNumber, layout, bodies, offset, renumber, settings);
        long hintLength = hint.Length;

        // Real offsets: everything from the first page on moves by the hint stream's length.
        long Real(long o) => o >= hintOffset ? o + hintLength : o;
        string mainXrefHeader = $"xref\n0 {mainCount + 1}";
        long spaceBeforeZero = Real(mainXrefOffset) + mainXrefHeader.Length;
        byte[] mainSection = MainSection(mainXrefHeader, main, n => Real(offset[n]), mainCount, firstXrefOffset);
        long fileLength = Real(mainXrefOffset) + mainSection.Length;

        var output = new ByteSink((int)Math.Min(fileLength, Compat.MaxArrayLength));
        output.Write(header);
        output.Write(Lindict(lindictNumber, fileLength, hintOffset, hintLength, renumber[layout.Pages[0]], Real(firstPageEnd), layout.Pages.Count, spaceBeforeZero));
        output.Write(FirstXrefHeader(lindictNumber, firstSectionCount));
        var firstSection = new SortedDictionary<int, long> { [lindictNumber] = header.Length, [hintNumber] = hintOffset };
        foreach (int n in layout.Part4.Concat(layout.Part6)) firstSection[renumber[n]] = Real(offset[n]);
        foreach (long o in firstSection.Values) output.WriteAscii(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        output.Write(FirstTrailer(trailer, serializer, size, Real(mainXrefOffset)));
        foreach (int n in layout.Part4) output.Write(bodies[n]);
        output.Write(hint);
        foreach (int n in layout.Part6.Concat(main)) output.Write(bodies[n]);
        output.Write(mainSection);
        return output.WrittenMemory;

        void Place(int n)
        {
            offset[n] = position;
            position += bodies[n].Length;
        }
    }

    /// <summary>The hint stream object, its tables deflated.</summary>
    private static byte[] HintStream(int number, Layout layout, Dictionary<int, byte[]> bodies, Dictionary<int, long> offset, Dictionary<int, int> renumber, WriterSettings settings)
    {
        byte[] hintData = HintTables(layout, bodies, offset, renumber, out int sharedTableOffset, out int outlineTableOffset);
        byte[] compressed = StreamCodec.Deflate(hintData, settings.CompressionLevel);
        var hint = new ByteSink(compressed.Length + 128);
        hint.WriteAscii($"{number} 0 obj\n<</Filter/FlateDecode/Length {compressed.Length}/S {sharedTableOffset}");
        if (outlineTableOffset >= 0) hint.WriteAscii($"/O {outlineTableOffset}");
        hint.Write(">>stream\n"u8);
        hint.Write(compressed);
        hint.Write("\nendstream\nendobj\n"u8);
        return hint.ToArray();
    }

    /// <summary>
    /// The object streams to write and the objects each holds, in order. An object stream is a
    /// unit of the layout like an object, numbered after every object: the k-th (from 0) is
    /// object count + 1 + k.
    /// </summary>
    private sealed class Packing
    {
        public Packing(int objectCount)
        {
            ObjectCount = objectCount;
            UnitOf = new int[objectCount + 1];
            for (int n = 0; n <= objectCount; n++) UnitOf[n] = n;
        }

        public int ObjectCount { get; }

        /// <summary>For each object number, the object stream holding the object, or the number itself.</summary>
        public int[] UnitOf { get; }

        public List<List<int>> Streams { get; } = [];

        public bool IsStream(int unit) => unit > ObjectCount;

        public List<int> Members(int unit) => Streams[unit - ObjectCount - 1];

        public void Add(List<int> members)
        {
            Streams.Add(members);
            int unit = ObjectCount + Streams.Count;
            foreach (int n in members) UnitOf[n] = unit;
        }
    }

    /// <summary>
    /// Packs the objects of each part with objects of the same part only; page objects, the
    /// catalog and streams stay on their own. An object stream goes where all the uses of its
    /// objects together put it, which for objects of one part is that part, except for outline
    /// objects, which the outline hint table gives as one run and are packed apart. A later
    /// page's own objects below <see cref="OwnStreamMinimum"/> join part 8, packed in the order
    /// of the first page using each object, so one stream serves a run of neighbouring pages and
    /// becomes shared by them. Objects of one kind sit side by side within a stream, which
    /// deflates better (see <see cref="PdfDocumentWriter.GroupByKind"/>). A group too small to
    /// gain from compression is left unpacked.
    /// </summary>
    private static Packing Pack(Layout layout, IReadOnlyList<PdfObject> objects, Func<PdfReference, int> mapReference, WriterSettings settings)
    {
        var packing = new Packing(objects.Count);
        var pages = new HashSet<int>(layout.Pages);
        var serializer = new PdfSerializer(mapReference);
        var header = new ByteSink(1024);
        var body = new ByteSink(16 * 1024);

        ByKind(layout.Part4);
        ByKind(layout.Part6.Where(n => !InOutlines(n)));
        ByKind(layout.Part6.Where(InOutlines));
        var pooled = new List<int>(layout.Part8);
        foreach (List<int> section in layout.Part7)
        {
            List<int> own = section.Where(Packable).ToList();
            if (BodyLength(own) >= OwnStreamMinimum) ByKind(own);
            else pooled.AddRange(own);
        }

        ByPage(pooled);
        ByKind(layout.Part9.Where(InOutlines));
        ByKind(layout.Part9.Where(n => !InOutlines(n)));
        return packing;

        bool Packable(int n) => objects[n - 1] is not PdfStream && n != layout.Root && !pages.Contains(n);

        bool InOutlines(int n) =>
            layout.Placements.TryGetValue(n, out Placement p) && p.Category is Category.OutlinesRoot or Category.Outlines;

        int FirstPage(int n) => layout.Placements.TryGetValue(n, out Placement p) ? p.Page : int.MaxValue;

        void ByKind(IEnumerable<int> part)
        {
            List<int> grouped = PdfDocumentWriter.GroupByKind(part.Where(Packable).ToList(), objects);
            for (int start = 0; start < grouped.Count; start += settings.ObjectsPerStream)
            {
                Add(grouped.GetRange(start, Math.Min(settings.ObjectsPerStream, grouped.Count - start)));
            }
        }

        void ByPage(IEnumerable<int> part)
        {
            List<int> ordered = part.Where(Packable).OrderBy(FirstPage).ThenBy(n => n).ToList();
            for (int start = 0; start < ordered.Count; start += settings.ObjectsPerStream)
            {
                Add(PdfDocumentWriter.GroupByKind(ordered.GetRange(start, Math.Min(settings.ObjectsPerStream, ordered.Count - start)), objects));
            }
        }

        int BodyLength(List<int> members)
        {
            body.Clear();
            foreach (int n in members) serializer.Write(body, objects[n - 1]);
            return body.Length;
        }

        void Add(List<int> members)
        {
            if (members.Count < 2) return;
            byte[] content = ObjectStreamContent(members, objects, serializer, n => n, header, body, out _);
            if (body.Length < TrialDeflateLimit)
            {
                // Numbers here are the input's, a digit or so off the written ones: close enough to compare.
                long loose = body.Length + ((long)LooseObjectOverhead * members.Count);
                if (StreamCodec.Deflate(content, settings.CompressionLevel).Length + ObjectStreamOverhead >= loose) return;
            }

            packing.Add(members);
        }
    }

    /// <summary>An object stream's content: pairs of number and offset, then the objects.</summary>
    private static byte[] ObjectStreamContent(List<int> members, IReadOnlyList<PdfObject> objects, PdfSerializer serializer, Func<int, int> numberOf, ByteSink header, ByteSink body, out int first)
    {
        header.Clear();
        body.Clear();
        for (int k = 0; k < members.Count; k++)
        {
            if (k > 0) header.Write((byte)' ');
            header.WriteInteger(numberOf(members[k]));
            header.Write((byte)' ');
            header.WriteInteger(body.Length);
            serializer.Write(body, objects[members[k] - 1]);
        }

        header.Write((byte)'\n');
        first = header.Length;
        header.Write(body.WrittenSpan);
        return header.ToArray();
    }

    /// <summary>
    /// Lays out and writes the file with object streams, or says why it cannot. Numbers follow
    /// qpdf, as Annex F readers expect them: in each cross-reference section the objects written
    /// in the file come first and the objects in object streams after them. The main section
    /// holds parts 7 to 9, its own cross-reference stream, then their packed objects; the first
    /// page section holds the linearization dictionary, its cross-reference stream, part 4, the
    /// hint stream, part 6, then their packed objects.
    /// </summary>
    private static string? AssembleCompact(Layout layout, Packing packing, IReadOnlyList<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> mapReference, WriterSettings settings, CancellationToken ct, out ReadOnlyMemory<byte> file)
    {
        file = default;
        var main = layout.Part7.SelectMany(p => p).Concat(layout.Part8).Concat(layout.Part9).ToList();
        var first = layout.Part4.Concat(layout.Part6).ToList();

        // Keys are units, as placed, and the objects packed in object streams.
        var renumber = new Dictionary<int, int>();
        int next = 1;
        foreach (int u in main) renumber[u] = next++;
        int mainXrefNumber = next++;
        NumberPacked(main);
        int mainSize = next;
        int lindictNumber = next++;
        int firstXrefNumber = next++;
        foreach (int u in layout.Part4) renumber[u] = next++;
        int hintNumber = next++;
        foreach (int u in layout.Part6) renumber[u] = next++;
        NumberPacked(first);
        int size = next;
        if (packing.Streams.Exists(s => !s.TrueForAll(renumber.ContainsKey))) return "an object stream was left out of the layout";

        int Map(PdfReference r) => mapReference(r) is var n && n > 0 && renumber.TryGetValue(n, out int m) ? m : -1;
        var serializer = new PdfSerializer(Map);
        Dictionary<int, byte[]> bodies = Bodies(first.Concat(main), packing, objects, serializer, n => renumber[n], settings, ct);

        byte[] header = [.. System.Text.Encoding.ASCII.GetBytes("%PDF-" + settings.Version + "\n"), .. BinaryMarker];
        long firstXrefOffset = header.Length + Lindict(lindictNumber, 0, 0, 0, 0, 0, 0, 0).Length;
        byte[] tail = System.Text.Encoding.ASCII.GetBytes($"startxref\n{firstXrefOffset}\n%%EOF\n");

        // The first cross-reference stream holds offsets of what follows it, which move with its
        // size: lay out with room kept for it until the room is enough, then pad what is left.
        var offset = new Dictionary<int, long>(bodies.Count);
        long position = 0;
        int reserved = 0;
        for (int pass = 0; pass < MaxLayoutPasses; pass++)
        {
            // Offsets as if the hint stream were absent, which is how the hint tables give them.
            position = firstXrefOffset + reserved;
            foreach (int u in layout.Part4) Place(u);
            long hintOffset = position;
            foreach (int u in layout.Part6) Place(u);
            long firstPageEnd = position;
            foreach (int u in main) Place(u);
            long mainXrefOffset = position;
            byte[] hint = HintStream(hintNumber, layout, bodies, offset, renumber, settings);

            // Real offsets: everything from the first page on moves by the hint stream's length.
            long Real(long o) => o >= hintOffset ? o + hint.Length : o;
            var firstSection = new XrefRows(lindictNumber, size - lindictNumber);
            firstSection.Set(lindictNumber, 1, header.Length, 0);
            firstSection.Set(firstXrefNumber, 1, firstXrefOffset, 0);
            firstSection.Set(hintNumber, 1, hintOffset, 0);
            foreach (int u in first) firstSection.Set(renumber[u], 1, Real(offset[u]), 0);
            SetPacked(firstSection, first);
            byte[] firstXref = firstSection.Write(firstXrefNumber, size, Real(mainXrefOffset), trailer, serializer, settings);
            if (firstXref.Length > reserved)
            {
                reserved = firstXref.Length + FirstXrefSlack;
                continue;
            }

            var mainSection = new XrefRows(0, mainSize);
            mainSection.Set(0, 0, 0, 65535);
            foreach (int u in main) mainSection.Set(renumber[u], 1, Real(offset[u]), 0);
            mainSection.Set(mainXrefNumber, 1, Real(mainXrefOffset), 0);
            SetPacked(mainSection, main);
            byte[] mainXref = mainSection.Write(mainXrefNumber, mainSize, -1, null, serializer, settings);
            long fileLength = Real(mainXrefOffset) + mainXref.Length + tail.Length;

            // /T is the main cross-reference stream's offset in a file with streams only.
            var output = new ByteSink((int)Math.Min(fileLength, Compat.MaxArrayLength));
            output.Write(header);
            output.Write(Lindict(lindictNumber, fileLength, hintOffset, hint.Length, renumber[layout.Pages[0]], Real(firstPageEnd), layout.Pages.Count, Real(mainXrefOffset)));
            output.Write(firstXref);
            for (int i = firstXref.Length; i < reserved; i++) output.Write(i == reserved - 1 ? (byte)'\n' : (byte)' ');
            foreach (int u in layout.Part4) output.Write(bodies[u]);
            output.Write(hint);
            foreach (int u in layout.Part6.Concat(main)) output.Write(bodies[u]);
            output.Write(mainXref);
            output.Write(tail);
            if (output.Length != fileLength) return "the layout does not add up";
            file = output.WrittenMemory;
            return null;
        }

        return "the first page cross-reference stream did not settle";

        void NumberPacked(List<int> units)
        {
            foreach (int u in units)
            {
                if (!packing.IsStream(u)) continue;
                foreach (int n in packing.Members(u)) renumber[n] = next++;
            }
        }

        void SetPacked(XrefRows rows, List<int> units)
        {
            foreach (int u in units)
            {
                if (!packing.IsStream(u)) continue;
                List<int> members = packing.Members(u);
                for (int k = 0; k < members.Count; k++) rows.Set(renumber[members[k]], 2, renumber[u], k);
            }
        }

        void Place(int u)
        {
            offset[u] = position;
            position += bodies[u].Length;
        }
    }

    /// <summary>
    /// Every unit as written: an object as "n 0 obj ... endobj", an object stream with its
    /// objects packed and deflated. Object streams are filled in order and deflated side by side;
    /// the bytes do not depend on how many at once.
    /// </summary>
    private static Dictionary<int, byte[]> Bodies(IEnumerable<int> units, Packing packing, IReadOnlyList<PdfObject> objects, PdfSerializer serializer, Func<int, int> numberOf, WriterSettings settings, CancellationToken ct)
    {
        var plain = settings with { Encryption = null, EncryptionDictionaryNumber = -1 };
        var bodies = new Dictionary<int, byte[]>();
        var sink = new ByteSink(4096);
        var streams = new List<int>();
        foreach (int u in units)
        {
            if (packing.IsStream(u))
            {
                streams.Add(u);
                continue;
            }

            sink.Clear();
            PdfDocumentWriter.WriteIndirect(sink, serializer, numberOf(u), objects[u - 1], plain);
            bodies[u] = sink.ToArray();
        }

        var contents = new byte[streams.Count][];
        var firsts = new int[streams.Count];
        var header = new ByteSink(1024);
        var body = new ByteSink(16 * 1024);
        for (int i = 0; i < streams.Count; i++)
        {
            contents[i] = ObjectStreamContent(packing.Members(streams[i]), objects, serializer, numberOf, header, body, out firsts[i]);
        }

        if (settings.Parallelism > 1)
        {
            Parallel.For(0, contents.Length, new ParallelOptions { MaxDegreeOfParallelism = settings.Parallelism, CancellationToken = ct },
                i => contents[i] = StreamCodec.Deflate(contents[i], settings.CompressionLevel, settings.Exhaustive, ct));
        }
        else
        {
            for (int i = 0; i < contents.Length; i++) contents[i] = StreamCodec.Deflate(contents[i], settings.CompressionLevel, settings.Exhaustive, ct);
        }

        for (int i = 0; i < streams.Count; i++)
        {
            int unit = streams[i];
            sink.Clear();
            sink.WriteInteger(numberOf(unit));
            sink.Write(" 0 obj\n<</Type/ObjStm/N "u8);
            sink.WriteInteger(packing.Members(unit).Count);
            sink.Write("/First "u8);
            sink.WriteInteger(firsts[i]);
            sink.Write("/Filter/FlateDecode/Length "u8);
            sink.WriteInteger(contents[i].Length);
            sink.Write(">>stream\n"u8);
            sink.Write(contents[i]);
            sink.Write("\nendstream\nendobj\n"u8);
            bodies[unit] = sink.ToArray();
            contents[i] = [];
        }

        return bodies;
    }

    /// <summary>The rows of one cross-reference stream: numbers from a first one, three fields each.</summary>
    private sealed class XrefRows(int start, int count)
    {
        private readonly byte[] _types = new byte[count];
        private readonly long[] _field2 = new long[count];
        private readonly int[] _field3 = new int[count];

        public void Set(int number, byte type, long field2, int field3)
        {
            _types[number - start] = type;
            _field2[number - start] = field2;
            _field3[number - start] = field3;
        }

        /// <summary>
        /// The stream object, its rows predicted and deflated as <see cref="PdfDocumentWriter"/>
        /// writes them, field 3 two bytes wide. With <paramref name="previous"/> and the trailer
        /// it is the first page section, which names its numbers with /Index; otherwise it holds
        /// only /Size.
        /// </summary>
        public byte[] Write(int number, int size, long previous, PdfDictionary? trailer, PdfSerializer serializer, WriterSettings settings)
        {
            // Field 2, an offset or an object stream's number, as wide as the largest needs.
            int width = 1;
            foreach (long v in _field2)
            {
                while (width < 8 && v >= 1L << (8 * width)) width++;
            }

            int row = 1 + width + 2;
            var table = new byte[count * row];
            for (int i = 0; i < count; i++)
            {
                int o = i * row;
                table[o] = _types[i];
                for (int b = 0; b < width; b++) table[o + 1 + b] = (byte)(_field2[i] >> (8 * (width - 1 - b)));
                table[o + 1 + width] = (byte)(_field3[i] >> 8);
                table[o + 2 + width] = (byte)_field3[i];
            }

            var predictor = new PredictorParams(12, 1, 8, row);
            byte[] data;
            using (BoundedBuffer predicted = Predictors.EncodePng(table, predictor))
            {
                data = StreamCodec.Deflate(predicted.Span, settings.CompressionLevel);
            }

            var x = new PdfDictionary();
            x.Set("Type", new PdfName("XRef"));
            x.Set("Size", PdfNumber.FromInteger(size));
            x.Set("W", PdfArray.Of(PdfNumber.FromInteger(1), PdfNumber.FromInteger(width), PdfNumber.FromInteger(2)));
            if (start > 0) x.Set("Index", PdfArray.Of(PdfNumber.FromInteger(start), PdfNumber.FromInteger(count)));
            if (previous >= 0) x.Set("Prev", PdfNumber.FromInteger(previous));
            foreach (string key in new[] { "Root", "Info", "ID" })
            {
                if (trailer?[key] is { } value) x.Set(key, value);
            }

            x.Set("Filter", new PdfName("FlateDecode"));
            x.Set("DecodeParms", StreamCodec.PredictorDictionary(predictor));
            var sink = new ByteSink(data.Length + 256);
            PdfDocumentWriter.WriteIndirect(sink, serializer, number, new PdfStream(x, data), settings with { Encryption = null, EncryptionDictionaryNumber = -1 });
            return sink.ToArray();
        }
    }

    private static string Pad(long value) => value.ToString(CultureInfo.InvariantCulture).PadRight(NumberWidth);

    private static byte[] Lindict(int number, long length, long hintOffset, long hintLength, int firstPage, long firstPageEnd, int pages, long spaceBeforeZero) =>
        System.Text.Encoding.ASCII.GetBytes(
            $"{number} 0 obj\n<</Linearized 1/L {Pad(length)}/H[ {Pad(hintOffset)} {Pad(hintLength)}]/O {Pad(firstPage)}/E {Pad(firstPageEnd)}/N {Pad(pages)}/T {Pad(spaceBeforeZero)}>>\nendobj\n");

    private static byte[] FirstXrefHeader(int first, int count) => System.Text.Encoding.ASCII.GetBytes($"xref\n{first} {count}\n");

    private static byte[] FirstTrailer(PdfDictionary source, PdfSerializer serializer, int size, long previous)
    {
        var sink = new ByteSink(256);
        sink.WriteAscii($"trailer\n<</Size {size}");
        foreach (string key in new[] { "Root", "Info", "ID" })
        {
            if (source[key] is not { } value) continue;
            PdfSerializer.WriteName(sink, key);
            serializer.Write(sink, value);
        }

        sink.WriteAscii($"/Prev {Pad(previous)}>>\nstartxref\n0\n%%EOF\n");
        return sink.ToArray();
    }

    private static byte[] MainSection(string header, List<int> main, Func<int, long> offsetOf, int count, long firstXrefOffset)
    {
        var sink = new ByteSink(64 + 20 * (count + 1));
        sink.WriteAscii(header + "\n0000000000 65535 f \n");
        foreach (int n in main) sink.WriteAscii(offsetOf(n).ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        sink.WriteAscii($"trailer\n<</Size {count + 1}>>\nstartxref\n{firstXrefOffset}\n%%EOF\n");
        return sink.ToArray();
    }

    /// <summary>
    /// The page offset hint table, the shared object hint table and, with outlines, the outline
    /// hint table (ISO 32000-1, F.4). Offsets are those of the file without the hint stream.
    /// Each run of per-page or per-group items starts on a byte boundary.
    /// </summary>
    private static byte[] HintTables(Layout layout, Dictionary<int, byte[]> bodies, Dictionary<int, long> offset, Dictionary<int, int> renumber, out int sharedTableOffset, out int outlineTableOffset)
    {
        int pageCount = layout.Pages.Count;
        var pageObjects = new int[pageCount];
        var pageLengths = new long[pageCount];
        pageObjects[0] = layout.Part6.Count;
        pageLengths[0] = layout.Part6.Sum(n => (long)bodies[n].Length);
        for (int i = 1; i < pageCount; i++)
        {
            pageObjects[i] = layout.Part7[i - 1].Count;
            pageLengths[i] = layout.Part7[i - 1].Sum(n => (long)bodies[n].Length);
        }

        var sharedIndex = new Dictionary<int, int>();
        foreach (int n in layout.Part6.Concat(layout.Part8)) sharedIndex[n] = sharedIndex.Count;
        var shared = new List<List<int>> { new() };
        foreach (List<int> refs in layout.SharedReferences) shared.Add(refs.Select(n => sharedIndex[n]).ToList());

        int minObjects = pageObjects.Min();
        long minLength = pageLengths.Min();
        int objectBits = Bits(pageObjects.Max() - minObjects);
        int lengthBits = Bits(pageLengths.Max() - minLength);
        int countBits = Bits(shared.Max(s => s.Count));
        int idBits = Bits(shared.SelectMany(s => s).DefaultIfEmpty(0).Max());

        var w = new BitWriter();
        w.Write(minObjects, 32);
        w.Write(offset[layout.Pages[0]], 32);
        w.Write(objectBits, 16);
        w.Write(minLength, 32);
        w.Write(lengthBits, 16);
        w.Write(0, 32); // least content stream offset: readers ignore the content items
        w.Write(0, 16);
        w.Write(minLength, 32); // content stream length: the page length, an upper bound
        w.Write(lengthBits, 16);
        w.Write(countBits, 16);
        w.Write(idBits, 16);
        w.Write(0, 16); // numerators of fractional positions: none
        w.Write(1, 16);
        foreach (int n in pageObjects) w.Write(n - minObjects, objectBits);
        w.Align();
        foreach (long l in pageLengths) w.Write(l - minLength, lengthBits);
        w.Align();
        foreach (List<int> s in shared) w.Write(s.Count, countBits);
        w.Align();
        foreach (int id in shared.SelectMany(s => s)) w.Write(id, idBits);
        w.Align();
        w.Align(); // numerators, zero bits each
        w.Align(); // content stream offsets, zero bits each
        foreach (long l in pageLengths) w.Write(l - minLength, lengthBits);
        w.Align();

        // Shared object hint table: part 6 then part 8, one object per group.
        sharedTableOffset = w.Length;
        var groups = layout.Part6.Concat(layout.Part8).Select(n => (long)bodies[n].Length).ToList();
        long minGroup = groups.Min();
        int groupBits = Bits(groups.Max() - minGroup);
        w.Write(layout.Part8.Count > 0 ? renumber[layout.Part8[0]] : 0, 32);
        w.Write(layout.Part8.Count > 0 ? offset[layout.Part8[0]] : 0, 32);
        w.Write(layout.Part6.Count, 32);
        w.Write(groups.Count, 32);
        w.Write(0, 16); // objects per group, less one: always zero
        w.Write(minGroup, 32);
        w.Write(groupBits, 16);
        foreach (long g in groups) w.Write(g - minGroup, groupBits);
        w.Align();
        foreach (long _ in groups) w.Write(0, 1); // no MD5 signatures
        w.Align();

        outlineTableOffset = -1;
        if (layout.OutlineRoot > 0)
        {
            outlineTableOffset = w.Length;
            List<int> outline = layout.Part6.Contains(layout.OutlineRoot) ? layout.Part6 : layout.Part9;
            int start = outline.IndexOf(layout.OutlineRoot);
            w.Write(renumber[layout.OutlineRoot], 32);
            w.Write(offset[layout.OutlineRoot], 32);
            w.Write(layout.OutlineCount, 32);
            w.Write(outline.Skip(start).Take(layout.OutlineCount).Sum(n => (long)bodies[n].Length), 32);
        }

        return w.ToArray();
    }

    /// <summary>Bits needed to write <paramref name="value"/>; zero for zero.</summary>
    private static int Bits(long value)
    {
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>Writes unsigned fields most significant bit first.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _used;

        /// <summary>Whole bytes written so far; call after <see cref="Align"/>.</summary>
        public int Length => _bytes.Count;

        public void Write(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_used == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = 0;
                    _used = 0;
                }
            }
        }

        /// <summary>Pads with zero bits to the next byte boundary.</summary>
        public void Align()
        {
            if (_used > 0) Write(0, 8 - _used);
        }

        public byte[] ToArray()
        {
            Align();
            return [.. _bytes];
        }
    }
}
