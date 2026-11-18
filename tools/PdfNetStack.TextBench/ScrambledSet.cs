using System.Globalization;
using System.Text;

namespace PdfNetStack.TextBench;

/// <summary>
/// Untagged pages whose content draws the lines in a shuffled order, so only layout analysis
/// recovers the reading order, as with many real producers (text boxes, floats, edited files).
/// Set in Courier, whose fixed advance makes the wrapping exact, in one, two or three columns,
/// with a title and full width paragraphs between column runs. The truth is the text in the
/// order it was laid out.
/// </summary>
internal static class ScrambledSet
{
    private const double FontSize = 10, Leading = 12, ParagraphGap = 8, Margin = 54, Gutter = 24, Advance = 0.6 * FontSize;

    public static IReadOnlyList<(string Id, byte[] Pdf, string Truth)> All { get; } =
    [
        Build("one-column", [(1, 6)]),
        Build("two-columns", [(2, 8)]),
        Build("three-columns", [(3, 9)]),
        Build("columns-and-bands", [(2, 4), (1, 1), (2, 4)]),
        Build("long-two-columns", [(2, 24)]),
    ];

    /// <summary>A document: a title, then runs of (columns, paragraphs), each run below the last.</summary>
    private static (string, byte[], string) Build(string id, (int Columns, int Paragraphs)[] runs)
    {
        var truth = new StringBuilder();
        var pages = new List<List<(double X, double Y, double Size, string Text)>> { new() };
        double y = 792 - Margin - 16;
        string title = "Basin report " + id.Replace('-', ' ');
        pages[0].Add((Margin, y, 16, title));
        truth.AppendLine(title);
        y -= 28;
        int sentence = 0;
        foreach ((int columns, int paragraphs) in runs)
        {
            double width = (612 - (2 * Margin) - ((columns - 1) * Gutter)) / columns;
            int perLine = (int)(width / Advance);
            var lines = new List<(string Text, bool Last)>();
            for (int p = 0; p < paragraphs; p++)
            {
                // A note number makes repeated sentences distinct, so the order stays checkable.
                string text = $"Note {p + 1}: " + string.Join(" ", Enumerable.Range(sentence, 3).Select(Documents.Sentence));
                sentence += 3;
                truth.AppendLine(text);
                List<string> wrapped = Wrap(text, perLine);
                for (int k = 0; k < wrapped.Count; k++) lines.Add((wrapped[k], k == wrapped.Count - 1));
            }

            // A run that fits on the page is balanced across its columns; a longer one fills each
            // column to the foot of the page, then the next column, then a new page.
            double top = y;
            int perColumn = (int)Math.Ceiling(lines.Count / (double)columns);
            bool fits = top - (perColumn * (Leading + ParagraphGap)) > Margin;
            int column = 0, inColumn = 0;
            double lowest = y;
            foreach ((string text, bool last) in lines)
            {
                if ((fits && inColumn == perColumn) || (!fits && y < Margin))
                {
                    column++;
                    inColumn = 0;
                    y = top;
                    if (column == columns)
                    {
                        pages.Add([]);
                        column = 0;
                        top = y = lowest = 792 - Margin - 16;
                    }
                }

                pages[^1].Add((Margin + (column * (width + Gutter)), y, FontSize, text));
                inColumn++;
                y -= Leading + (last ? ParagraphGap : 0);
                lowest = Math.Min(lowest, y);
            }

            y = lowest - 12;
        }

        // A seed from the id, the same in every run (string hash codes are not).
        return (id, Write(pages, id.Sum(c => c)), truth.ToString());
    }

    private static List<string> Wrap(string text, int perLine)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > perLine)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    /// <summary>Pages of Courier lines, each page's lines drawn in a shuffled order.</summary>
    private static byte[] Write(List<List<(double X, double Y, double Size, string Text)>> pages, int seed)
    {
        var random = new Random(seed);
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding/WinAnsiEncoding>>",
        };
        var kids = new List<int>();
        int next = 4;
        foreach (List<(double X, double Y, double Size, string Text)> page in pages)
        {
            var content = new StringBuilder();
            foreach ((double x, double y, double fontSize, string text) in page.OrderBy(_ => random.Next()))
            {
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 {fontSize} Tf {x:0.##} {y:0.##} Td ({text.Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)}) Tj ET\n");
            }

            int stream = next++, pageObject = next++;
            objects[stream] = $"<</Length {Encoding.Latin1.GetByteCount(content.ToString())}>>\nstream\n{content}\nendstream";
            objects[pageObject] = $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents {stream} 0 R>>";
            kids.Add(pageObject);
        }

        objects[2] = $"<</Type/Pages/Kids[{string.Join(" ", kids.Select(k => k + " 0 R"))}]/Count {kids.Count}>>";
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach ((int number, string body) in objects)
        {
            offsets[number] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        int size = objects.Keys.Max() + 1;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) sb.Append(offsets.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append($"trailer\n<</Size {size}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
