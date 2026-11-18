using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Tables on untagged pages, found only where the page rules them. A grid of horizontal and
/// vertical lines gives the rows and columns. Horizontal rules alone (a top, a middle and a bottom
/// rule, as in most typeset tables) bound a region whose columns come from gaps that run down
/// every line of it, and only when its cells are short, as a table's are and prose is not.
/// </summary>
/// <remarks>
/// Text in columns without lines is never read as a table: two columns of prose, or label and
/// value pairs, read as paragraphs. A candidate whose words cross its column lines, or whose
/// cells are mostly empty, is dropped. Only upright text on an upright page is considered.
/// </remarks>
internal static class TableDetector
{
    // Rules within this many points are one line, or touch.
    private const double Tolerance = 2.5;

    private const double MinHorizontalRule = 24;

    private const double MinVerticalRule = 8;

    // Columns from alignment: the most words a cell holds on average, and the least share of cells filled.
    private const double MaxWordsPerAlignedCell = 5;

    private const double MinFilledShare = 0.5;

    // Rules alone: the tallest region, as a share of the page, read as a table.
    private const double MaxRuledShare = 0.6;

    private const int MaxRules = 4096;

    // A grid row holds at most this many lines; more between two rules means the rules part
    // groups of rows, not rows.
    private const int MaxLinesPerRuledRow = 3;

    /// <summary>
    /// The tables of a page, and for each letter whether it is in one. <paramref name="letters"/>
    /// are the page's kept letters.
    /// </summary>
    public static List<(TextBlock Table, PdfRectangle Region)> Find(IReadOnlyList<PdfLetter> letters, IReadOnlyList<RuleLine> rules, PdfRectangle page, bool[] used)
    {
        var tables = new List<(TextBlock, PdfRectangle)>();
        if (rules.Count == 0 || rules.Count > MaxRules) return tables;
        List<RuleLine> h = Merge(rules.Where(r => r.Horizontal), MinHorizontalRule);
        List<RuleLine> v = Merge(rules.Where(r => !r.Horizontal), MinVerticalRule);
        if (h.Count < 2) return tables;

        // Connected rules: a grid, or a run of horizontal rules of one width.
        var parent = Enumerable.Range(0, h.Count + v.Count).ToArray();
        for (int i = 0; i < h.Count; i++)
        {
            for (int j = 0; j < v.Count; j++)
            {
                if (Crosses(h[i], v[j])) Union(parent, i, h.Count + j);
            }

            for (int j = i + 1; j < h.Count; j++)
            {
                if (Math.Abs(h[i].From - h[j].From) <= 2 * Tolerance && Math.Abs(h[i].To - h[j].To) <= 2 * Tolerance) Union(parent, i, j);
            }
        }

        var groups = new Dictionary<int, (List<RuleLine> H, List<RuleLine> V)>();
        for (int k = 0; k < parent.Length; k++)
        {
            int root = Find(parent, k);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = ([], []);
            if (k < h.Count) g.H.Add(h[k]);
            else g.V.Add(v[k - h.Count]);
        }

        foreach ((List<RuleLine> hs, List<RuleLine> vs) in groups.Values)
        {
            if (hs.Count < 2) continue;
            if (Build(letters, hs, vs, page, used) is { } table) tables.Add(table);
        }

        tables.Sort((a, b) => b.Item2.Top.CompareTo(a.Item2.Top));
        return tables;
    }

    private static (TextBlock, PdfRectangle)? Build(IReadOnlyList<PdfLetter> letters, List<RuleLine> hs, List<RuleLine> vs, PdfRectangle page, bool[] used)
    {
        double left = hs.Min(r => r.From), right = hs.Max(r => r.To);
        double bottom = hs.Min(r => r.Position), top = hs.Max(r => r.Position);
        foreach (RuleLine r in vs)
        {
            left = Math.Min(left, r.Position);
            right = Math.Max(right, r.Position);
            bottom = Math.Min(bottom, r.From);
            top = Math.Max(top, r.To);
        }

        var region = new PdfRectangle(left, bottom, right, top);
        bool grid = vs.Count > 0;
        if (!grid && region.Height > MaxRuledShare * page.Height) return null;

        // The region's letters: upright, centred inside it, not already in a table.
        var inside = new List<int>();
        for (int i = 0; i < letters.Count; i++)
        {
            PdfLetter l = letters[i];
            if (used[i]) continue;
            double x = (l.Bounds.Left + l.Bounds.Right) / 2, y = (l.Bounds.Bottom + l.Bounds.Top) / 2;
            if (x < left || x > right || y < bottom || y > top) continue;
            if (Math.Abs(l.Direction.X - 1) > 0.01) return null;
            inside.Add(i);
        }

        if (inside.Count < 4) return null;
        List<List<PdfWord>> lines = TextLayout.Lines([.. inside.Select(i => letters[i])]);
        List<PdfWord> words = [.. lines.SelectMany(l => l)];
        double em = Median(words.Select(w => w.Letters[0].FontSize));

        // Columns: the vertical rules of a grid, else gaps down every line.
        List<double> columns = Positions(vs.Select(r => r.Position), Tolerance);
        bool aligned = columns.Count < 3;
        if (aligned) columns = Gaps(words, left, right, em);
        else
        {
            if (columns[0] > left + Tolerance) columns.Insert(0, left);
            if (columns[^1] < right - Tolerance) columns.Add(right);
        }

        if (columns.Count < 3) return null;

        // Rows: between the horizontal rules of a grid whose rules part every row, a cell holding
        // a few lines; else one row per line, as under the top, middle and bottom rules of a
        // typeset table, where body rows have no rule between them.
        List<double> ruled = Positions(hs.Select(r => r.Position), Tolerance);
        ruled.Reverse();
        bool byRules = !aligned && ruled.Count >= 3 && MostLinesBetween(lines, ruled) <= MaxLinesPerRuledRow;
        int rowCount = byRules ? ruled.Count - 1 : lines.Count;
        int columnCount = columns.Count - 1;
        if (rowCount < 2) return null;

        var cells = new List<List<PdfWord>>[rowCount, columnCount];
        int crossing = 0;
        for (int li = 0; li < lines.Count; li++)
        {
            foreach (PdfWord w in lines[li])
            {
                double x = (w.Bounds.Left + w.Bounds.Right) / 2, y = (w.Bounds.Bottom + w.Bounds.Top) / 2;
                int row = byRules ? Interval(ruled, y, descending: true) : li;
                int col = Interval(columns, x, descending: false);
                if (row < 0 || col < 0) continue;
                for (int c = 1; c < columns.Count - 1; c++)
                {
                    if (w.Bounds.Left < columns[c] - 1 && w.Bounds.Right > columns[c] + 1) crossing++;
                }

                List<List<PdfWord>> cell = cells[row, col] ??= [];
                if (cell.Count == 0 || cell[^1][^1].Letters[0].BaselineStart.Y - w.Letters[0].BaselineStart.Y > 0.5 * em) cell.Add([]);
                cell[^1].Add(w);
            }
        }

        if (crossing > 0.1 * words.Count) return null;
        int filled = 0, filledWords = 0;
        var rows = new List<List<List<List<PdfWord>>>>();
        for (int r = 0; r < rowCount; r++)
        {
            var row = new List<List<List<PdfWord>>>();
            bool any = false;
            for (int c = 0; c < columnCount; c++)
            {
                List<List<PdfWord>> cell = cells[r, c] ?? [];
                if (cell.Count > 0)
                {
                    filled++;
                    filledWords += cell.Sum(l => l.Count);
                    any = true;
                }

                row.Add(cell);
            }

            if (any) rows.Add(row);
        }

        if (rows.Count < 2) return null;
        if (aligned && (filled < MinFilledShare * rows.Count * columnCount || filledWords > MaxWordsPerAlignedCell * filled)) return null;

        foreach (int i in inside) used[i] = true;
        return (new TextBlock(PdfTextBlockKind.Table, 0, []) { Rows = rows }, region);
    }

    /// <summary>The most text lines between two consecutive rules.</summary>
    private static int MostLinesBetween(List<List<PdfWord>> lines, List<double> ruled)
    {
        var counts = new int[ruled.Count];
        foreach (List<PdfWord> line in lines)
        {
            double y = line[0].Letters[0].BaselineStart.Y;
            int row = Interval(ruled, y, descending: true);
            if (row >= 0) counts[row]++;
        }

        return counts.Max();
    }

    /// <summary>The interval of sorted <paramref name="bounds"/> holding <paramref name="value"/>, or -1.</summary>
    private static int Interval(List<double> bounds, double value, bool descending)
    {
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            bool inside = descending ? value <= bounds[i] + Tolerance && value >= bounds[i + 1] - Tolerance : value >= bounds[i] - Tolerance && value <= bounds[i + 1] + Tolerance;
            if (inside) return i;
        }

        return -1;
    }

    /// <summary>Column boundaries from vertical gaps of at least 0.8 em that no word in the region crosses.</summary>
    private static List<double> Gaps(List<PdfWord> words, double left, double right, double em)
    {
        var spans = words.Select(w => (w.Bounds.Left, w.Bounds.Right)).OrderBy(s => s.Left).ToList();
        var bounds = new List<double> { left };
        double reach = double.MinValue;
        foreach ((double l, double r) in spans)
        {
            if (reach > double.MinValue && l - reach >= 0.8 * em) bounds.Add((l + reach) / 2);
            reach = Math.Max(reach, r);
        }

        bounds.Add(right);
        return bounds;
    }

    /// <summary>Distinct positions, ascending, those within <paramref name="tolerance"/> taken as one.</summary>
    private static List<double> Positions(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (double p in values.OrderBy(x => x))
        {
            if (result.Count == 0 || p - result[^1] > tolerance) result.Add(p);
        }

        return result;
    }

    /// <summary>Joins collinear rules that overlap or touch, and drops those shorter than <paramref name="minLength"/>.</summary>
    private static List<RuleLine> Merge(IEnumerable<RuleLine> rules, double minLength)
    {
        var merged = new List<RuleLine>();
        foreach (RuleLine r in rules.OrderBy(r => r.Position).ThenBy(r => r.From))
        {
            int at = merged.FindLastIndex(m => Math.Abs(m.Position - r.Position) <= 1.5 && r.From <= m.To + Tolerance);
            if (at >= 0)
            {
                RuleLine m = merged[at];
                merged[at] = m with { From = Math.Min(m.From, r.From), To = Math.Max(m.To, r.To) };
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged.FindAll(r => r.To - r.From >= minLength);
    }

    private static bool Crosses(RuleLine h, RuleLine v) =>
        v.Position >= h.From - Tolerance && v.Position <= h.To + Tolerance && h.Position >= v.From - Tolerance && h.Position <= v.To + Tolerance;

    private static int Find(int[] parent, int k)
    {
        while (parent[k] != k) k = parent[k] = parent[parent[k]];
        return k;
    }

    private static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Where(v => v > 0).OrderBy(v => v).ToList();
        return list.Count == 0 ? 10 : list[list.Count / 2];
    }
}
