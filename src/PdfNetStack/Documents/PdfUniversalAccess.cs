using System.Globalization;
using System.Text.RegularExpressions;
using PdfNetStack.Content;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>An accessibility standard the saved file conforms to.</summary>
public enum PdfAccessibility
{
    /// <summary>No particular standard.</summary>
    None,

    /// <summary>
    /// PDF/UA-1 (ISO 14289-1), universally accessible PDF, alone or with a PDF/A level. Saving
    /// declares it in the XMP metadata (pdfuaid:part 1), marks the document as tagged, tells
    /// viewers to show the title, sets the tab order of pages with annotations to the structure
    /// order and fills in missing file names of embedded files. It then refuses to save, listing
    /// every failure, when the document has no title or language, is not tagged, has content
    /// that is neither tagged nor an artifact, figures or formulas without alternate text, links
    /// or other annotations outside their tags or without a description, fonts that are not
    /// embedded, or structure that breaks the rules for tables, lists, headings, notes and role
    /// mapping that a program can check.
    /// </summary>
    /// <remarks>
    /// Machine checks cannot prove a file accessible: the reading order, the choice of tags, the
    /// quality of alternate text, colour contrast and the other human checks of the Matterhorn
    /// Protocol remain the author's to verify, ideally with a screen reader.
    /// </remarks>
    PdfUA1,
}

/// <summary>
/// Prepares a document for PDF/UA-1 output (ISO 14289-1): repairs what has one right answer, and
/// lists the failures of the Matterhorn Protocol's machine checks it covers, by checkpoint
/// number. The XMP identification is written by <see cref="PdfArchive.WriteMetadata"/>.
/// </summary>
internal static class PdfUniversalAccess
{
    /// <summary>Most structure elements, and content operators per document, the checks walk through.</summary>
    private const int MaxNodes = 1_000_000;
    private const long MaxOperations = 50_000_000;
    private const int MaxListedPages = 10;

    private static readonly HashSet<string> Painting = new(StringComparer.Ordinal) { "Tj", "TJ", "'", "\"", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "sh", "BI" };

    /// <summary>Allowed parents (ISO 32000-1, Tables 333, 336 and 337).</summary>
    private static readonly Dictionary<string, string[]> Parents = new(StringComparer.Ordinal)
    {
        ["TR"] = ["Table", "THead", "TBody", "TFoot"],
        ["TH"] = ["TR"],
        ["TD"] = ["TR"],
        ["THead"] = ["Table"],
        ["TBody"] = ["Table"],
        ["TFoot"] = ["Table"],
        ["LI"] = ["L"],
        ["Lbl"] = ["LI"],
        ["LBody"] = ["LI"],
        ["TOCI"] = ["TOC"],
    };

    /// <summary>Allowed children of the grouping elements of tables, lists and tables of contents.</summary>
    private static readonly Dictionary<string, string[]> Children = new(StringComparer.Ordinal)
    {
        ["Table"] = ["TR", "THead", "TBody", "TFoot", "Caption"],
        ["THead"] = ["TR"],
        ["TBody"] = ["TR"],
        ["TFoot"] = ["TR"],
        ["TR"] = ["TH", "TD"],
        ["L"] = ["LI", "L", "Caption"],
        ["LI"] = ["Lbl", "LBody"],
        ["TOC"] = ["TOCI", "TOC", "Caption"],
    };

    public static void Prepare(PdfDocument doc)
    {
        var problems = new List<string>();
        var pageIndex = new Dictionary<int, int>();
        for (int i = 0; i < doc.Pages.Count; i++) pageIndex[doc.Pages[i].Number] = i;

        if (doc.Security.Target is { } encryption && (encryption.Permissions & (int)PdfPermissions.ExtractForAccessibility) == 0)
        {
            problems.Add("The encryption must allow text extraction for accessibility: include PdfPermissions.ExtractForAccessibility (Matterhorn 26-002).");
        }

        if (string.IsNullOrWhiteSpace(doc.Info.Title)) problems.Add("The document has no title: set Info.Title (Matterhorn 06-003).");
        if (string.IsNullOrWhiteSpace(doc.Language)) problems.Add("The document has no natural language: set Language, such as \"en\" (Matterhorn 11-001).");
        doc.Viewer.DisplayDocumentTitle = true;

        var structure = doc.Resolve(doc.Catalog["StructTreeRoot"]) as PdfDictionary;
        var roles = new RoleMap(doc, structure, problems);
        var parentTree = new Dictionary<int, PdfObject>();
        if (structure is null)
        {
            problems.Add("The document is not tagged: tag its content with PdfGraphics.BeginTag and mark decoration with BeginArtifact (ISO 14289-1, 7.1).");
        }
        else
        {
            PdfDictionary markInfo = doc.Catalog["MarkInfo"] as PdfDictionary
                ?? (doc.Resolve(doc.Catalog["MarkInfo"]) is PdfDictionary existing ? PdfDocument.Clone(existing) : new PdfDictionary());
            markInfo.Set("Marked", PdfBoolean.True);
            doc.Catalog.Set("MarkInfo", markInfo);
            if (doc.Resolve(markInfo["Suspects"]) is PdfBoolean { Value: true }) problems.Add("MarkInfo /Suspects is true: the tagging is marked as unreliable (ISO 14289-1, 7.1).");
            if (doc.Resolve(structure["ParentTree"]) is PdfDictionary tree)
            {
                foreach ((PdfObject key, PdfObject value) in PdfStructureTree.ReadTree(doc, tree, "Nums", out _))
                {
                    if (doc.Resolve(key) is PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } k) parentTree[(int)k.IntegerValue] = value;
                }
            }

            CheckStructure(doc, structure, roles, pageIndex, problems);
        }

        CheckContent(doc, parentTree, problems);
        CheckAnnotations(doc, parentTree, roles, problems);
        CheckFonts(doc, problems);
        CheckFiles(doc, problems);
        CheckOptionalContent(doc, problems);
        CheckXfa(doc, problems);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The document cannot be saved as PDF/UA-1:\n- " + string.Join("\n- ", problems.Distinct()));
        }
    }

    /// <summary>The role map: each type's standard type, with the mapping errors reported once (Matterhorn 02).</summary>
    private sealed class RoleMap
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
        private readonly List<string> _problems;

        public RoleMap(PdfDocument doc, PdfDictionary? structure, List<string> problems)
        {
            _problems = problems;
            if (doc.Resolve(structure?["RoleMap"]) is not PdfDictionary map) return;
            foreach (KeyValuePair<string, PdfObject> role in map)
            {
                if (doc.Resolve(role.Value) is not PdfName target) continue;
                _map[role.Key] = target.Value;
                if (PdfStructureTree.IsStandard(role.Key)) problems.Add($"The role map remaps the standard type {role.Key} (Matterhorn 02-004).");
            }
        }

        /// <summary>The standard type a type is, or maps to; null when it maps to none.</summary>
        public string? Standard(string type)
        {
            if (_resolved.TryGetValue(type, out string? known)) return known;
            string? result = null;
            string at = type;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (PdfStructureTree.IsStandard(at))
                {
                    result = at;
                    break;
                }

                if (!seen.Add(at))
                {
                    _problems.Add($"The role map of {type} is circular (Matterhorn 02-003).");
                    break;
                }

                if (!_map.TryGetValue(at, out string? next))
                {
                    _problems.Add($"The structure type {type} is not mapped to a standard type: map it with PdfStructureTree.MapRole (Matterhorn 02-001).");
                    break;
                }

                at = next;
            }

            _resolved[type] = result;
            return result;
        }
    }

    /// <summary>Walks the structure tree in reading order: alternate text, notes, headings and the nesting of tables, lists and tables of contents.</summary>
    private static void CheckStructure(PdfDocument doc, PdfDictionary root, RoleMap roles, Dictionary<int, int> pageIndex, List<string> problems)
    {
        var pending = new Stack<(PdfDictionary Element, string? Parent, PdfDictionary? Table)>();
        var visited = new HashSet<PdfDictionary>();
        var noteIds = new HashSet<string>(StringComparer.Ordinal);
        var tables = new Dictionary<PdfDictionary, (bool Headers, bool Unscoped)>();
        int previousLevel = 0, budget = MaxNodes;
        bool numbered = false, unnumbered = false;
        Push(root, "StructTreeRoot", null);

        while (pending.Count > 0 && budget-- > 0)
        {
            (PdfDictionary element, string? parent, PdfDictionary? table) = pending.Pop();
            string type = element.GetName("S") ?? "";
            string? standard = roles.Standard(type);
            string where = Where(element);

            if (standard is not null && parent is not null && Parents.TryGetValue(standard, out string[]? allowed) && !allowed.Contains(parent))
            {
                problems.Add($"{Label(type, standard)}{where} must be inside {string.Join(" or ", allowed)}, not {parent} (Matterhorn {Checkpoint(standard)}).");
            }

            if (standard is not null && parent is not null && Children.TryGetValue(parent, out string[]? kids) && !kids.Contains(standard))
            {
                problems.Add($"{parent} may hold only {string.Join(", ", kids)}, not {Label(type, standard)}{where} (Matterhorn {Checkpoint(parent)}).");
            }

            switch (standard)
            {
                case "Figure" or "Formula" when Text(doc, element, "Alt") is null && Text(doc, element, "ActualText") is null:
                    problems.Add($"{Label(type, standard)}{where} has no alternate text: set PdfStructureElement.AlternateText (Matterhorn {(standard == "Figure" ? "13-004" : "17-002")}).");
                    break;
                case "Note":
                    if (Text(doc, element, "ID") is not string id) problems.Add($"A Note{where} has no ID (Matterhorn 19-003).");
                    else if (!noteIds.Add(id)) problems.Add($"Two Notes share the ID {id} (Matterhorn 19-004).");
                    break;
                case "Table":
                    table = element;
                    tables[element] = (false, false);
                    break;
                case "TH" or "TD" when table is not null:
                    PdfDictionary? attributes = TableAttributes(doc, element);
                    (bool headers, bool unscoped) = tables[table];
                    tables[table] = (headers || attributes?["Headers"] is not null, unscoped || (standard == "TH" && attributes?["Scope"] is null));
                    break;
                case "H":
                    unnumbered = true;
                    break;
                case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                    int level = standard[1] - '0';
                    if (!numbered && level != 1) problems.Add($"The first numbered heading is {standard}; it must be H1 (Matterhorn 14-002).");
                    else if (numbered && level > previousLevel + 1) problems.Add($"H{level} follows H{previousLevel}: heading levels must not be skipped (Matterhorn 14-003).");
                    numbered = true;
                    previousLevel = level;
                    break;
            }

            Push(element, standard, table);
        }

        if (numbered && unnumbered) problems.Add("Both H and H1 to H6 headings are used; use one kind (Matterhorn 14-007).");
        foreach (KeyValuePair<PdfDictionary, (bool Headers, bool Unscoped)> t in tables)
        {
            if (t.Value.Unscoped && !t.Value.Headers) problems.Add($"A table{Where(t.Key)} has TH cells without a scope: set PdfStructureElement.Scope (Matterhorn 15-003).");
        }

        // Children are pushed last to first, so they come off the stack in reading order.
        void Push(PdfDictionary element, string? standard, PdfDictionary? table)
        {
            var children = new List<PdfDictionary>();
            foreach (PdfObject kid in Kids(doc, element))
            {
                if (doc.Resolve(kid) is PdfDictionary child && child["S"] is not null && child.GetName("Type") is null or "StructElem" && visited.Add(child)) children.Add(child);
            }

            int headings = children.Count(c => roles.Standard(c.GetName("S") ?? "") == "H");
            if (headings > 1) problems.Add($"An element holds {headings} H headings; nest each in its own section (Matterhorn 14-006).");
            for (int i = children.Count - 1; i >= 0; i--) pending.Push((children[i], standard, table));
        }

        // The page an element is on: its own /Pg, or failing that the first one among its first descendants.
        string Where(PdfDictionary element)
        {
            var queue = new Queue<PdfDictionary>([element]);
            for (int looked = 0; queue.Count > 0 && looked < 64; looked++)
            {
                PdfDictionary at = queue.Dequeue();
                if (at["Pg"] is PdfReference r && pageIndex.TryGetValue(r.Number, out int index)) return " on page " + (index + 1).ToString(CultureInfo.InvariantCulture);
                foreach (PdfDictionary kid in Kids(doc, at).Select(doc.Resolve).OfType<PdfDictionary>()) queue.Enqueue(kid);
            }

            return "";
        }
    }

    private static string Label(string type, string standard) => type == standard ? standard : $"{type} ({standard})";

    private static string Checkpoint(string standard) => standard switch
    {
        "LI" or "Lbl" or "LBody" or "L" => "09-005",
        "TOCI" or "TOC" => "09-006",
        _ => "09-004",
    };

    private static IEnumerable<PdfObject> Kids(PdfDocument doc, PdfDictionary element) => doc.Resolve(element["K"]) switch
    {
        PdfArray a => a,
        null or PdfNull => [],
        _ => [element["K"]!],
    };

    private static string? Text(PdfDocument doc, PdfDictionary element, string key) =>
        doc.Resolve(element[key]) is PdfString s && s.ToText() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>The Table owner's attributes of a cell, whether /A holds one dictionary or a list.</summary>
    private static PdfDictionary? TableAttributes(PdfDocument doc, PdfDictionary element) => doc.Resolve(element["A"]) switch
    {
        PdfDictionary d when d.GetName("O") == "Table" => d,
        PdfArray list => list.Select(doc.Resolve).OfType<PdfDictionary>().FirstOrDefault(d => d.GetName("O") == "Table"),
        _ => null,
    };

    /// <summary>
    /// Every page's content, with the forms it draws: painting that is neither inside an artifact
    /// nor inside marked content whose MCID the parent tree knows, artifacts and tags nested in
    /// each other, reference XObjects and tagged forms drawn more than once.
    /// </summary>
    private static void CheckContent(PdfDocument doc, Dictionary<int, PdfObject> parentTree, List<string> problems)
    {
        var scan = new ContentScan(doc, parentTree);
        var untagged = new List<string>();
        foreach (PdfPage page in doc.Pages)
        {
            int count = scan.Page(page);
            if (count > 0) untagged.Add($"{(page.Index + 1).ToString(CultureInfo.InvariantCulture)} ({count.ToString(CultureInfo.InvariantCulture)} drawing operation{(count == 1 ? "" : "s")})");
        }

        if (untagged.Count > 0)
        {
            string pages = string.Join(", ", untagged.Take(MaxListedPages)) + (untagged.Count > MaxListedPages ? $" and {untagged.Count - MaxListedPages} more pages" : "");
            problems.Add($"Content on page{(untagged.Count == 1 ? "" : "s")} {pages} is neither tagged nor marked as an artifact: draw it between BeginTag and EndTag, or BeginArtifact and EndArtifact (Matterhorn 01-005).");
        }

        if (scan.ArtifactInTagged) problems.Add("An artifact is inside tagged content (Matterhorn 01-003).");
        if (scan.TaggedInArtifact) problems.Add("Tagged content is inside an artifact (Matterhorn 01-004).");
        if (scan.ReferenceXObject) problems.Add("A reference XObject shows a page of another file (Matterhorn 30-001).");
        if (scan.SharedTaggedForm) problems.Add("A form XObject holding tagged content is drawn more than once (Matterhorn 30-002).");
    }

    /// <summary>Walks content streams with the marked content they are inside.</summary>
    private sealed class ContentScan(PdfDocument doc, Dictionary<int, PdfObject> parentTree)
    {
        private const int MaxFormDepth = 16;

        private readonly Dictionary<PdfStream, byte[]?> _forms = [];
        private readonly Dictionary<PdfStream, int> _formUses = [];
        private readonly HashSet<PdfStream> _taggedForms = [];
        private readonly HashSet<PdfStream> _active = [];
        private long _operations;

        public bool ArtifactInTagged { get; private set; }

        public bool TaggedInArtifact { get; private set; }

        public bool ReferenceXObject { get; private set; }

        public bool SharedTaggedForm => _taggedForms.Any(f => _formUses.TryGetValue(f, out int uses) && uses > 1);

        private enum Marked
        {
            Artifact,
            Tagged,
            Other,
        }

        /// <summary>How many painting operators of the page are untagged.</summary>
        public int Page(PdfPage page)
        {
            using var joined = new MemoryStream();
            IEnumerable<PdfObject> parts = doc.Resolve(page.Dictionary["Contents"]) switch
            {
                PdfArray a => a,
                PdfStream s => [s],
                _ => [],
            };
            foreach (PdfObject part in parts)
            {
                if (doc.Resolve(part) is not PdfStream stream || Decode(stream) is not byte[] data) continue;
                joined.Write(data, 0, data.Length);
                joined.WriteByte((byte)'\n');
            }

            return Run(joined.ToArray(), doc.Resolve(page.Dictionary["Resources"]) as PdfDictionary, McidsOf(page.Dictionary), [], 0);
        }

        /// <summary>The MCIDs the parent tree gives an element for, through a page's or form's /StructParents.</summary>
        private HashSet<int> McidsOf(PdfDictionary owner)
        {
            var mcids = new HashSet<int>();
            if (doc.Resolve(owner["StructParents"]) is PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } key
                && parentTree.TryGetValue((int)key.IntegerValue, out PdfObject? entry) && doc.Resolve(entry) is PdfArray parents)
            {
                for (int i = 0; i < parents.Count; i++)
                {
                    if (doc.Resolve(parents[i]) is PdfDictionary) mcids.Add(i);
                }
            }

            return mcids;
        }

        private byte[]? Decode(PdfStream stream)
        {
            try
            {
                return doc.GetStreamData(stream);
            }
            catch (PdfFormatException)
            {
                return null;
            }
        }

        private int Run(byte[] content, PdfDictionary? resources, HashSet<int> mcids, List<Marked> outer, int depth)
        {
            int untagged = 0;
            var marks = new List<Marked>(outer);
            int floor = marks.Count;
            var reader = new ContentReader(content);
            var op = new ContentOperation();
            while (reader.Next(op) && ++_operations < MaxOperations)
            {
                List<PdfObject> o = op.Operands;
                switch (op.Operator)
                {
                    case "BMC" when o.Count > 0 && o[o.Count - 1] is PdfName tag:
                        Begin(tag.Value == "Artifact" ? Marked.Artifact : Marked.Other);
                        break;
                    case "BDC" when o.Count >= 2 && o[o.Count - 2] is PdfName tag:
                        PdfDictionary? properties = o[o.Count - 1] switch
                        {
                            PdfDictionary inline => inline,
                            PdfName name => doc.Resolve((doc.Resolve(resources?["Properties"]) as PdfDictionary)?[name.Value]) as PdfDictionary,
                            _ => null,
                        };
                        if (tag.Value == "Artifact") Begin(Marked.Artifact);
                        else if (properties?["MCID"] is PdfNumber { IsInteger: true } mcid) Begin(mcids.Contains((int)Math.Min(mcid.IntegerValue, int.MaxValue)) ? Marked.Tagged : Marked.Other, tagged: true);
                        else Begin(Marked.Other);
                        break;
                    case "EMC":
                        if (marks.Count > floor) marks.RemoveAt(marks.Count - 1);
                        break;
                    case "Do" when o.Count > 0 && o[o.Count - 1] is PdfName xname
                        && doc.Resolve((doc.Resolve(resources?["XObject"]) as PdfDictionary)?[xname.Value]) is PdfStream xobject:
                        if (xobject.Dictionary.GetName("Subtype") == "Image") untagged += Paint();
                        else if (xobject.Dictionary.GetName("Subtype") == "Form") untagged += Form(xobject, resources, marks, depth);
                        break;
                    default:
                        if (Painting.Contains(op.Operator)) untagged += Paint();
                        break;
                }
            }

            return untagged;

            void Begin(Marked kind, bool tagged = false)
            {
                if (kind == Marked.Artifact && marks.Contains(Marked.Tagged)) ArtifactInTagged = true;
                if (tagged && marks.Contains(Marked.Artifact)) TaggedInArtifact = true;
                marks.Add(kind);
            }

            int Paint() => marks.Contains(Marked.Artifact) || marks.Contains(Marked.Tagged) ? 0 : 1;
        }

        /// <summary>A form's content runs inside the marked content it is drawn in; one with its own MCIDs uses its /StructParents.</summary>
        private int Form(PdfStream form, PdfDictionary? resources, List<Marked> marks, int depth)
        {
            if (form.Dictionary["Ref"] is not null) ReferenceXObject = true;
            _formUses[form] = _formUses.TryGetValue(form, out int uses) ? uses + 1 : 1;
            if (depth >= MaxFormDepth || !_active.Add(form)) return 0;
            try
            {
                if (!_forms.TryGetValue(form, out byte[]? data)) _forms[form] = data = Decode(form);
                if (data is null) return 0;
                HashSet<int> mcids = McidsOf(form.Dictionary);
                if (mcids.Count > 0) _taggedForms.Add(form);
                return Run(data, doc.Resolve(form.Dictionary["Resources"]) as PdfDictionary ?? resources, mcids, marks, depth + 1);
            }
            finally
            {
                _active.Remove(form);
            }
        }
    }

    /// <summary>
    /// Annotations sit in the structure under the element their kind needs, with a description,
    /// and pages that have them take the structure order for tabbing (ISO 14289-1, 7.18).
    /// </summary>
    private static void CheckAnnotations(PdfDocument doc, Dictionary<int, PdfObject> parentTree, RoleMap roles, List<string> problems)
    {
        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray { Count: > 0 } annots) continue;
            page.Dictionary.Set("Tabs", new PdfName("S"));
            string on = " on page " + (page.Index + 1).ToString(CultureInfo.InvariantCulture);
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is not PdfDictionary annot) continue;
                string subtype = annot.GetName("Subtype") ?? "";
                long flags = doc.Resolve(annot["F"]) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
                PdfDictionary? element = doc.Resolve(annot["StructParent"]) is PdfNumber { IsInteger: true, IntegerValue: >= 0 and < int.MaxValue } key
                    && parentTree.TryGetValue((int)key.IntegerValue, out PdfObject? parent) ? doc.Resolve(parent) as PdfDictionary : null;
                string? standard = element?.GetName("S") is string type ? roles.Standard(type) : null;
                string? description = Text(doc, annot, "Contents") ?? (element is null ? null : Text(doc, element, "Alt"));

                switch (subtype)
                {
                    case "TrapNet":
                        problems.Add($"A TrapNet annotation{on} is not allowed (Matterhorn 28-007).");
                        continue;
                    case "Popup":
                        continue;
                    case "PrinterMark":
                        if (element is not null) problems.Add($"A PrinterMark annotation{on} is in the structure tree (Matterhorn 28-017).");
                        continue;
                }

                // Hidden annotations are not presented, so they need neither tag nor description.
                if ((flags & 2) != 0) continue;
                switch (subtype)
                {
                    case "Widget":
                        if (standard != "Form") problems.Add($"A form field{on} is not inside a Form element: add its widget with PdfStructureElement.AddAnnotation (Matterhorn 28-010).");
                        if (Inherited(doc, annot, "TU") is null && (element is null || Text(doc, element, "Alt") is null)) problems.Add($"A form field{on} has neither a tooltip nor alternate text: set PdfFieldOptions.ToolTip (Matterhorn 28-005).");
                        break;
                    case "Link":
                        if (standard != "Link") problems.Add($"A link{on} is not inside a Link element: add it with PdfStructureElement.AddAnnotation (Matterhorn 28-011).");
                        if (Text(doc, annot, "Contents") is null) problems.Add($"A link{on} has no description: set PdfAnnotation.Contents (Matterhorn 28-012).");
                        break;
                    default:
                        if (standard != "Annot") problems.Add($"A {subtype} annotation{on} is not inside an Annot element (Matterhorn 28-002).");
                        if (description is null) problems.Add($"A {subtype} annotation{on} has no description: set PdfAnnotation.Contents (Matterhorn 28-004).");
                        break;
                }
            }
        }
    }

    /// <summary>A text entry of a widget or the field it belongs to.</summary>
    private static string? Inherited(PdfDocument doc, PdfDictionary widget, string key)
    {
        PdfDictionary? at = widget;
        for (int depth = 0; at is not null && depth < 32; depth++)
        {
            if (Text(doc, at, key) is string text) return text;
            at = doc.Resolve(at["Parent"]) as PdfDictionary;
        }

        return null;
    }

    /// <summary>Every font is embedded (Matterhorn 31-009), and composite fonts map their codes to Unicode (10-001).</summary>
    private static void CheckFonts(PdfDocument doc, List<string> problems)
    {
        foreach (PdfDictionary font in PdfArchive.Collect(doc).Fonts)
        {
            string name = font.GetName("BaseFont") ?? "(unnamed font)";
            string subtype = font.GetName("Subtype") ?? "";
            if (subtype == "Type3") continue;
            PdfDictionary? descriptor = doc.Resolve(font["FontDescriptor"]) as PdfDictionary;
            if (subtype == "Type0" && doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } d && doc.Resolve(d[0]) is PdfDictionary descendant)
            {
                descriptor = doc.Resolve(descendant["FontDescriptor"]) as PdfDictionary;
                bool identity = doc.Resolve(font["Encoding"]) is PdfStream || font.GetName("Encoding") is "Identity-H" or "Identity-V";
                if (identity && font["ToUnicode"] is null) problems.Add($"Font {name} has no ToUnicode map, so its text cannot be read as Unicode (Matterhorn 10-001).");
            }

            if (descriptor is null || (descriptor["FontFile"] ?? descriptor["FontFile2"] ?? descriptor["FontFile3"]) is null)
            {
                problems.Add($"Font {name} is not embedded: draw with a PdfOpenTypeFont instead of the standard fonts (Matterhorn 31-009).");
            }
        }
    }

    /// <summary>Embedded files carry both file names (Matterhorn 21-001); one missing is copied from the other.</summary>
    private static void CheckFiles(PdfDocument doc, List<string> problems)
    {
        var specs = new List<PdfDictionary>();
        foreach ((string _, PdfObject entry) in doc.Attachments.Entries())
        {
            if (doc.Resolve(entry) is PdfDictionary spec) specs.Add(spec);
        }

        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (PdfObject a in annots)
            {
                if (doc.Resolve(a) is PdfDictionary annot && annot.GetName("Subtype") == "FileAttachment" && doc.Resolve(annot["FS"]) is PdfDictionary spec) specs.Add(spec);
            }
        }

        foreach (PdfDictionary spec in specs)
        {
            if (spec["EF"] is null) continue;
            PdfObject? f = doc.Resolve(spec["F"]), uf = doc.Resolve(spec["UF"]);
            if (f is PdfString && uf is not PdfString) spec.Set("UF", f);
            else if (uf is PdfString && f is not PdfString) spec.Set("F", uf);
            else if (f is not PdfString) problems.Add("An embedded file has no file name (Matterhorn 21-001).");
        }
    }

    /// <summary>Optional content configurations are named and carry no automatic state changes (Matterhorn 20).</summary>
    private static void CheckOptionalContent(PdfDocument doc, List<string> problems)
    {
        if (doc.Resolve(doc.Catalog["OCProperties"]) is not PdfDictionary properties) return;
        if (doc.Resolve(properties["D"]) is PdfDictionary standard) Check(standard, "20-002");
        if (doc.Resolve(properties["Configs"]) is PdfArray configs)
        {
            foreach (PdfObject c in configs)
            {
                if (doc.Resolve(c) is PdfDictionary config) Check(config, "20-001");
            }
        }

        void Check(PdfDictionary config, string checkpoint)
        {
            if (Text(doc, config, "Name") is null) problems.Add($"An optional content configuration has no name (Matterhorn {checkpoint}).");
            if (config["AS"] is not null) problems.Add("An optional content configuration has an AS entry (Matterhorn 20-003).");
        }
    }

    /// <summary>XFA that must be rendered dynamically has no fixed structure to check (Matterhorn 25-001).</summary>
    private static void CheckXfa(PdfDocument doc, List<string> problems)
    {
        if (doc.Resolve(doc.Catalog["AcroForm"]) is not PdfDictionary form) return;
        IEnumerable<PdfObject> parts = doc.Resolve(form["XFA"]) switch
        {
            PdfStream s => [s],
            PdfArray a => a.Items.Where((_, i) => i % 2 == 1),
            _ => [],
        };
        foreach (PdfObject part in parts)
        {
            if (doc.Resolve(part) is not PdfStream stream) continue;
            try
            {
                string xml = System.Text.Encoding.UTF8.GetString(doc.GetStreamData(stream));
                if (Regex.IsMatch(xml, @"<dynamicRender[^>]*>\s*required\s*</dynamicRender>", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    problems.Add("The XFA form requires dynamic rendering (Matterhorn 25-001).");
                    return;
                }
            }
            catch (Exception e) when (e is PdfFormatException or RegexMatchTimeoutException)
            {
                problems.Add("The XFA form cannot be read to check that it does not require dynamic rendering (Matterhorn 25-001).");
                return;
            }
        }
    }
}
