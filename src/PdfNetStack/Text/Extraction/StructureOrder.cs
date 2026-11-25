using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// The reading order a document's structure tree gives (ISO 32000-1, 14.7 and 14.8.4): its marked
/// content in tree order, each piece assigned to the block it reads in, a paragraph, heading, list
/// item or table cell. Read once per document, in one walk of the tree.
/// </summary>
/// <remarks>
/// Standard structure types decide what a block is, through the root's /RoleMap for others.
/// Grouping elements (Document, Sect, Div, L, Table, TR and the like) make no block of their own;
/// content directly under one reads as a paragraph. Block elements inside a list item or table cell
/// (a P in an LBody) belong to that item or cell. The walk is bounded as
/// <see cref="StructureIndex"/>'s is.
/// </remarks>
internal sealed class StructureOrder
{
    // Role map links followed to reach a standard type.
    private const int MaxRoleMapping = 8;

    // Page object number to MCID to its position in tree order and its block.
    private readonly Dictionary<int, Dictionary<int, (int Order, int Block)>> _pages = [];
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private int _order;
    private int _tables;

    private StructureOrder()
    {
    }

    /// <summary>The blocks, in reading order.</summary>
    public List<StructureBlock> Blocks { get; } = [];

    /// <summary>The structure order of <paramref name="doc"/>, or null when it is not tagged.</summary>
    public static StructureOrder? Read(PdfDocument doc, CancellationToken ct)
    {
        if (doc.Resolve(doc.Catalog["StructTreeRoot"]) is not PdfDictionary root) return null;
        var order = new StructureOrder();
        if (doc.Resolve(root["RoleMap"]) is PdfDictionary roles)
        {
            foreach (KeyValuePair<string, PdfObject> role in roles)
            {
                if (doc.Resolve(role.Value) is PdfName target) order._roles[role.Key] = target.Value;
            }
        }

        order.Walk(doc, root, ct);
        return order;
    }

    /// <summary>The page's MCIDs with their order and block, or null when the tree has none on it.</summary>
    public Dictionary<int, (int Order, int Block)>? Page(int pageNumber) => _pages.TryGetValue(pageNumber, out var map) ? map : null;

    private void Walk(PdfDocument doc, PdfDictionary root, CancellationToken ct)
    {
        var stack = new Stack<(PdfObject Entry, Context Context)>();
        var top = new Context(null, -1, 0, 0, -1, null, new Scope());
        List<PdfObject> rootKids = [.. StructureIndex.Kids(doc, root)];
        for (int i = rootKids.Count - 1; i >= 0; i--) stack.Push((rootKids[i], top));
        var seen = new HashSet<int>();
        int elements = 0;
        while (stack.Count > 0)
        {
            (PdfObject entry, Context context) = stack.Pop();
            if (entry is PdfNumber { IsInteger: true } number)
            {
                Content(context, context.Page, number.IntegerValue);
                continue;
            }

            if (doc.Resolve(entry) is not PdfDictionary element) continue;
            if (StructureIndex.IsContentReference(element))
            {
                // A marked content reference (14.7.4.3) in the page's own content; one in a form
                // XObject (/Stm) or an object reference has no MCID on the page and is skipped.
                if (element.GetName("Type") == "MCR" && element["Stm"] is null && doc.Resolve(element["MCID"]) is PdfNumber { IsInteger: true } mcid)
                {
                    Content(context, element["Pg"] as PdfReference ?? context.Page, mcid.IntegerValue);
                }

                continue;
            }

            if (context.Depth >= StructureIndex.MaxDepth || ++elements > StructureIndex.MaxElements) continue;
            if (entry is PdfReference r && !seen.Add(r.Number)) continue;
            if (elements % 1024 == 0) ct.ThrowIfCancellationRequested();

            // Content after this element in its parent reads as a new paragraph.
            context.Scope.Block = -1;
            Context inner = Enter(context, Role(element.GetName("S")), element["Pg"] as PdfReference ?? context.Page);
            if (inner.Skip) continue;
            List<PdfObject> kids = [.. StructureIndex.Kids(doc, element)];
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], inner));
        }
    }

    /// <summary>The context inside an element of standard type <paramref name="type"/>.</summary>
    private Context Enter(Context outer, string type, PdfReference? page)
    {
        int depth = outer.Depth + 1;
        var scope = new Scope();
        switch (type)
        {
            case "Artifact":
                // A PDF 2.0 artifact element: not part of the text.
                return outer with { Skip = true };
            case "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "H" or "Title":
                if (outer.Block >= 0) break;
                int level = type switch
                {
                    "H" or "Title" => 1,
                    _ => type[1] - '0',
                };
                return outer with { Page = page, Block = NewBlock(StructureBlockKind.Heading, level), Depth = depth, Scope = scope };
            case "P" or "Note" or "Caption" or "BlockQuote" or "TOCI" or "Figure" or "Formula" or "Code" or "BibEntry" or "Index" or "Lbl" or "LBody" or "Span" or "Quote" or "Link" or "Reference" or "Annot" or "Em" or "Strong" or "Sub" or "Form" or "Ruby" or "Warichu":
                // Block or inline: content joins the block around it, else reads as a paragraph of its own.
                if (outer.Block >= 0) break;
                return outer with { Page = page, Block = NewBlock(StructureBlockKind.Paragraph, 0), Depth = depth, Scope = scope };
            case "L":
                return outer with { Page = page, ListDepth = outer.ListDepth + 1, Depth = depth, Scope = scope };
            case "LI":
                // A list item is a block of its own, nested lists included.
                return outer with { Page = page, Block = NewBlock(StructureBlockKind.ListItem, Math.Max(1, outer.ListDepth)), Depth = depth, Scope = scope };
            case "Table":
                if (outer.Block >= 0) break;
                return outer with { Page = page, Table = _tables++, Row = null, Depth = depth, Scope = scope };
            case "TR":
                if (outer.Block >= 0 || outer.Table < 0) break;
                return outer with { Page = page, Row = new Row(NextRow(outer.Table)), Depth = depth, Scope = scope };
            case "TH" or "TD":
                if (outer.Block >= 0 || outer.Table < 0) break;
                Row row = outer.Row ?? new Row(NextRow(outer.Table));
                return outer with { Page = page, Block = NewCell(outer.Table, row.Index, row.Columns++), Row = row, Depth = depth, Scope = scope };
        }

        return outer with { Page = page, Depth = depth, Scope = outer.Block >= 0 ? outer.Scope : scope };
    }

    // Rows started per table.
    private readonly Dictionary<int, int> _rows = [];

    private int NextRow(int table)
    {
        _rows.TryGetValue(table, out int rows);
        _rows[table] = rows + 1;
        return rows;
    }

    private int NewBlock(StructureBlockKind kind, int level)
    {
        Blocks.Add(new StructureBlock(kind, level, -1, -1, -1));
        return Blocks.Count - 1;
    }

    private int NewCell(int table, int row, int column)
    {
        Blocks.Add(new StructureBlock(StructureBlockKind.Cell, 0, table, row, column));
        return Blocks.Count - 1;
    }

    /// <summary>Marked content <paramref name="mcid"/> on <paramref name="page"/> reads next, in the block around it.</summary>
    private void Content(Context context, PdfReference? page, long mcid)
    {
        if (page is null || mcid is < 0 or > int.MaxValue) return;
        int block = context.Block;
        if (block < 0)
        {
            // Content straight under a grouping element: one paragraph for each run of it.
            if (context.Scope.Block < 0) context.Scope.Block = NewBlock(StructureBlockKind.Paragraph, 0);
            block = context.Scope.Block;
        }

        if (!_pages.TryGetValue(page.Number, out var map)) _pages[page.Number] = map = [];
        if (!map.ContainsKey((int)mcid)) map[(int)mcid] = (_order++, block);
    }

    /// <summary>The standard type <paramref name="type"/> maps to, through the role map.</summary>
    private string Role(string? type)
    {
        type ??= "";
        for (int i = 0; i < MaxRoleMapping && _roles.TryGetValue(type, out string? mapped) && mapped != type; i++) type = mapped;
        return type;
    }

    /// <summary>A table row and the cells started in it.</summary>
    private sealed class Row(int index)
    {
        public int Index { get; } = index;

        public int Columns { get; set; }
    }

    /// <summary>The paragraph that content directly under a grouping element reads in, until another element comes.</summary>
    private sealed class Scope
    {
        public int Block { get; set; } = -1;
    }

    private sealed record Context(PdfReference? Page, int Block, int ListDepth, int Depth, int Table, Row? Row, Scope Scope)
    {
        public bool Skip { get; init; }
    }
}

internal enum StructureBlockKind : byte
{
    Paragraph,
    Heading,
    ListItem,
    Cell,
}

/// <summary>A block of the structure order; a cell knows its table, row and column.</summary>
internal readonly record struct StructureBlock(StructureBlockKind Kind, int Level, int Table, int Row, int Column);
