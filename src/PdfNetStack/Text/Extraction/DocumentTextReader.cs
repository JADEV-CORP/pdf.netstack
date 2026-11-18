using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Reads pages for <see cref="PdfDocument.ReadText(PdfTextOptions?, CancellationToken)"/>: one
/// content walk per page gives the letters and the state that decides what can be seen, then the
/// kept letters are put in reading order, from the structure tree or the layout, split into
/// blocks and written out. What is read once per document (the structure tree, the optional
/// content configuration) is kept between pages.
/// </summary>
internal sealed class DocumentTextReader(PdfDocument document, PdfTextOptions options)
{
    /// <summary>
    /// The share of a page's kept letters the structure tree must reach for its order to be used.
    /// Below it the tags are partial (a few tagged runs on an untagged page, or a tree for other
    /// pages), and the layout reads more reliably.
    /// </summary>
    internal const double MinTaggedShare = 0.75;

    private OptionalContent? _optional;
    private StructureOrder? _structure;
    private bool _structureRead;

    public PdfPageText Read(PdfPage page, CancellationToken ct)
    {
        _optional ??= OptionalContent.Read(document);
        var extractor = new TextExtractor(document, _optional) { Cancellation = ct, CropBox = page.CropBox, ApplyLongReplacements = !options.VisibleOnly };
        extractor.Run(page);
        ct.ThrowIfCancellationRequested();

        List<PdfLetter> letters = extractor.Letters;
        List<LetterState> states = extractor.States;
        (PdfHiddenTextReason?[] hidden, bool[] ocr) = Visibility.Judge(letters, states, extractor.Graphics!);
        List<PdfHiddenText> hiddenRuns = HiddenRuns(letters, hidden);
        foreach ((string replacement, PdfRectangle bounds) in extractor.Replacements) hiddenRuns.Add(new PdfHiddenText(PdfHiddenTextReason.ReplacementText, replacement, bounds));

        var kept = new List<int>(letters.Count);
        bool hasOcr = false;
        for (int i = 0; i < letters.Count; i++)
        {
            if (options.VisibleOnly && hidden[i] is not null) continue;
            if (options.ExcludeArtifacts && states[i].Artifact) continue;
            kept.Add(i);
            hasOcr |= ocr[i];
        }

        PdfReadingOrder order = PdfReadingOrder.Layout;
        List<TextBlock> blocks;
        if (options.UseStructureTree && Tagged(page, kept, states, ct) is { } map)
        {
            order = PdfReadingOrder.StructureTree;
            blocks = FromStructure(letters, states, kept, map);
        }
        else
        {
            blocks = FromLayout([.. kept.Select(i => letters[i])], extractor.Graphics!.Rules, page.CropBox);
        }

        ct.ThrowIfCancellationRequested();
        IReadOnlyDictionary<PdfWord, string> links = Links(page, blocks);
        var rendered = new List<PdfTextBlock>(blocks.Count);
        var words = new List<PdfWord>();
        foreach (TextBlock block in blocks)
        {
            rendered.Add(TextRenderer.Render(block, links));
            words.AddRange(block.Words());
        }

        string text = string.Join("\n\n", rendered.Select(b => options.Format == PdfTextFormat.Markdown ? b.Markdown : b.Text));
        return new PdfPageText(page.Index, options.Format, text, rendered, words, order, hiddenRuns, hasOcr);
    }

    /// <summary>The page's MCIDs in the structure tree, when they reach enough of its kept letters; else null.</summary>
    private Dictionary<int, (int Order, int Block)>? Tagged(PdfPage page, List<int> kept, List<LetterState> states, CancellationToken ct)
    {
        if (!_structureRead)
        {
            _structure = StructureOrder.Read(document, ct);
            _structureRead = true;
        }

        if (_structure?.Page(page.Number) is not { } map || kept.Count == 0) return null;
        int tagged = 0;
        foreach (int i in kept)
        {
            if (states[i].Mcid >= 0 && map.ContainsKey(states[i].Mcid)) tagged++;
        }

        return tagged >= MinTaggedShare * kept.Count ? map : null;
    }

    /// <summary>
    /// Blocks in structure order: the letters of each tagged block laid out among themselves, table
    /// cells gathered into their table where its first cell reads, then any untagged letters in
    /// layout order.
    /// </summary>
    private List<TextBlock> FromStructure(List<PdfLetter> letters, List<LetterState> states, List<int> kept, Dictionary<int, (int Order, int Block)> map)
    {
        var byBlock = new Dictionary<int, (int First, List<PdfLetter> Letters)>();
        var untagged = new List<PdfLetter>();
        foreach (int i in kept)
        {
            if (states[i].Mcid < 0 || !map.TryGetValue(states[i].Mcid, out var at))
            {
                untagged.Add(letters[i]);
                continue;
            }

            if (!byBlock.TryGetValue(at.Block, out var entry)) byBlock[at.Block] = entry = (at.Order, []);
            else if (at.Order < entry.First) byBlock[at.Block] = entry = (at.Order, entry.Letters);
            entry.Letters.Add(letters[i]);
        }

        var blocks = new List<TextBlock>();
        var tables = new Dictionary<int, SortedDictionary<int, SortedDictionary<int, List<List<PdfWord>>>>>();
        foreach (KeyValuePair<int, (int First, List<PdfLetter> Letters)> entry in byBlock.OrderBy(e => e.Value.First))
        {
            StructureBlock tag = _structure!.Blocks[entry.Key];
            List<List<PdfWord>> lines = TextLayout.Lines(entry.Value.Letters);
            if (lines.Count == 0) continue;
            switch (tag.Kind)
            {
                case StructureBlockKind.Cell:
                    if (!tables.TryGetValue(tag.Table, out var rows))
                    {
                        tables[tag.Table] = rows = [];
                        blocks.Add(new TextBlock(PdfTextBlockKind.Table, 0, []) { Rows = [] });
                        _tableSlots[tag.Table] = blocks[^1];
                    }

                    if (!rows.TryGetValue(tag.Row, out var row)) rows[tag.Row] = row = [];
                    row[tag.Column] = lines;
                    break;
                case StructureBlockKind.Heading:
                    blocks.Add(new TextBlock(PdfTextBlockKind.Heading, Math.Min(6, Math.Max(1, tag.Level)), lines));
                    break;
                case StructureBlockKind.ListItem:
                    blocks.Add(new TextBlock(PdfTextBlockKind.ListItem, Math.Min(6, Math.Max(1, tag.Level)), lines));
                    break;
                default:
                    blocks.Add(new TextBlock(PdfTextBlockKind.Paragraph, 0, lines));
                    break;
            }
        }

        // Each table's rows in order, cells by column, a missing cell left empty.
        foreach (KeyValuePair<int, SortedDictionary<int, SortedDictionary<int, List<List<PdfWord>>>>> table in tables)
        {
            List<List<List<List<PdfWord>>>> rows = _tableSlots[table.Key].Rows!;
            foreach (SortedDictionary<int, List<List<PdfWord>>> row in table.Value.Values)
            {
                var cells = new List<List<List<PdfWord>>>();
                int last = row.Keys.Max();
                for (int c = 0; c <= last; c++) cells.Add(row.TryGetValue(c, out var cell) ? cell : []);
                rows.Add(cells);
            }
        }

        _tableSlots.Clear();
        if (options.ExcludeArtifacts) untagged = Margins(untagged, byBlock.Values.SelectMany(b => b.Letters));
        if (untagged.Count > 0) blocks.AddRange(LayoutBlocks.Build(TextLayout.BlockLines(untagged), LayoutBlocks.BodySize(untagged)));
        return blocks;
    }

    /// <summary>
    /// The untagged letters of a tagged page that are not above or below all of its tagged text.
    /// Some producers (Chrome among them) leave running headers, footers and page numbers
    /// untagged instead of marking them as artifacts; on a page whose text is otherwise tagged,
    /// text in those bands is pagination.
    /// </summary>
    private static List<PdfLetter> Margins(List<PdfLetter> untagged, IEnumerable<PdfLetter> tagged)
    {
        if (untagged.Count == 0) return untagged;
        double top = double.MinValue, bottom = double.MaxValue;
        foreach (PdfLetter l in tagged)
        {
            top = Math.Max(top, l.Bounds.Top);
            bottom = Math.Min(bottom, l.Bounds.Bottom);
        }

        return top < bottom ? untagged : untagged.FindAll(l => l.Bounds.Bottom < top && l.Bounds.Top > bottom);
    }

    // The table block each structure table fills, while one page is read.
    private readonly Dictionary<int, TextBlock> _tableSlots = [];

    /// <summary>Blocks in layout order: ruled tables first taken out, then paragraphs, with each table put back before the first block below it.</summary>
    private static List<TextBlock> FromLayout(List<PdfLetter> letters, List<RuleLine> rules, PdfRectangle page)
    {
        var used = new bool[letters.Count];
        List<(TextBlock Table, PdfRectangle Region)> tables = TableDetector.Find(letters, rules, page, used);
        var rest = new List<PdfLetter>(letters.Count);
        for (int i = 0; i < letters.Count; i++)
        {
            if (!used[i]) rest.Add(letters[i]);
        }

        List<TextBlock> blocks = LayoutBlocks.Build(TextLayout.BlockLines(rest), LayoutBlocks.BodySize(rest));
        foreach ((TextBlock table, PdfRectangle region) in tables)
        {
            int at = blocks.FindIndex(b => b.Rows is null && Below(b.Bounds(), region));
            if (at < 0) blocks.Add(table);
            else blocks.Insert(at, table);
        }

        return blocks;

        // A block that starts below the table's top and shares some of its width reads after it.
        static bool Below(PdfRectangle block, PdfRectangle table) => block.Top <= table.Top && block.Left < table.Right && block.Right > table.Left;
    }

    /// <summary>The words under the page's http, https and mailto link annotations, with the target each one links to.</summary>
    private static IReadOnlyDictionary<PdfWord, string> Links(PdfPage page, List<TextBlock> blocks)
    {
        var links = new Dictionary<PdfWord, string>(ReferenceEqualityComparer.Instance);
        if (page.AnnotationCount == 0) return links;
        var targets = new List<(PdfRectangle Rect, string Target)>();
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            if (annotation.Subtype != "Link" || TextRenderer.LinkTarget(annotation.Uri) is not { } target) continue;
            PdfRectangle r = annotation.Rectangle;
            targets.Add((new PdfRectangle(Math.Min(r.Left, r.Right) - 1, Math.Min(r.Bottom, r.Top) - 1, Math.Max(r.Left, r.Right) + 1, Math.Max(r.Bottom, r.Top) + 1), target));
        }

        if (targets.Count == 0) return links;
        foreach (TextBlock block in blocks)
        {
            foreach (PdfWord word in block.Words())
            {
                double x = (word.Bounds.Left + word.Bounds.Right) / 2, y = (word.Bounds.Bottom + word.Bounds.Top) / 2;
                foreach ((PdfRectangle rect, string target) in targets)
                {
                    if (x < rect.Left || x > rect.Right || y < rect.Bottom || y > rect.Top) continue;
                    links[word] = target;
                    break;
                }
            }
        }

        return links;
    }

    /// <summary>Consecutive hidden letters, in content order, hidden for one reason and on one line, as runs.</summary>
    private static List<PdfHiddenText> HiddenRuns(List<PdfLetter> letters, PdfHiddenTextReason?[] hidden)
    {
        var runs = new List<PdfHiddenText>();
        for (int i = 0; i < letters.Count;)
        {
            if (hidden[i] is not PdfHiddenTextReason reason)
            {
                i++;
                continue;
            }

            int end = i + 1;
            while (end < letters.Count && hidden[end] == reason && Near(letters[end - 1], letters[end])) end++;
            List<PdfLetter> run = letters.GetRange(i, end - i);
            string text = TextLayout.Text(run);
            if (text.Trim().Length > 0)
            {
                double left = run.Min(l => l.Bounds.Left), bottom = run.Min(l => l.Bounds.Bottom);
                double right = run.Max(l => l.Bounds.Right), top = run.Max(l => l.Bounds.Top);
                runs.Add(new PdfHiddenText(reason, text, new PdfRectangle(left, bottom, right, top)));
            }

            i = end;
        }

        return runs;

        // The next letter starts within a few ems of where the last one ended: the same run of text.
        static bool Near(PdfLetter a, PdfLetter b)
        {
            double dx = b.BaselineStart.X - a.BaselineEnd.X, dy = b.BaselineStart.Y - a.BaselineEnd.Y;
            double reach = 3 * Math.Max(1, Math.Max(a.FontSize, b.FontSize));
            return (dx * dx) + (dy * dy) <= reach * reach;
        }
    }
}
