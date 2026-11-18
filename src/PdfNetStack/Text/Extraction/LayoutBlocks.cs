using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Paragraphs, headings and list items from the layout alone, for pages without usable tags. The
/// layout's blocks (columns and bands, see <see cref="TextLayout"/>) are split into paragraphs at a
/// wider gap between lines, a change of size, an indented first line, a short line that ends a
/// sentence, or a list label. A short paragraph clearly larger than the page's body text is a
/// heading, its level from how much larger; a single bold line before body text is a level 4
/// heading. A paragraph that starts with a bullet or a number label is a list item.
/// </summary>
/// <remarks>
/// The rules are deliberately few and only ever split or label text; they never move it, so the
/// reading order is the layout's.
/// </remarks>
internal static class LayoutBlocks
{
    /// <summary>A paragraph at least this many times the body size is a heading.</summary>
    private const double HeadingRatio = 1.15;

    private const int MaxHeadingLines = 3;

    private const int MaxHeadingWords = 30;

    private const int MaxBoldHeadingWords = 12;

    /// <summary>Characters that start a bulleted list item; U+2013 and U+2014 are the en and em dashes.</summary>
    private const string Bullets = "•◦▪▫‣⁃∙●○■□◆◇➢➤►▶▸▹\u2013\u2014-*·";

    public static List<TextBlock> Build(List<List<List<PdfWord>>> blocks, double bodySize)
    {
        var result = new List<TextBlock>();
        foreach (List<List<PdfWord>> lines in blocks)
        {
            int start = result.Count;
            Split(lines, result);
            Classify(result, start, bodySize);
        }

        return result;
    }

    /// <summary>The size most of the letters are set in, rounded to half a point: the page's body text.</summary>
    public static double BodySize(IEnumerable<PdfLetter> letters)
    {
        var weight = new Dictionary<double, int>();
        foreach (PdfLetter l in letters)
        {
            double size = Math.Round(l.FontSize * 2) / 2;
            weight.TryGetValue(size, out int w);
            weight[size] = w + Math.Max(1, l.Text.Length);
        }

        double best = 0;
        int most = -1;
        foreach (KeyValuePair<double, int> e in weight)
        {
            if (e.Value > most || (e.Value == most && e.Key < best)) (best, most) = (e.Key, e.Value);
        }

        return best > 0 ? best : 10;
    }

    /// <summary>Splits one layout block's lines into paragraphs and list items.</summary>
    private static void Split(List<List<PdfWord>> lines, List<TextBlock> into)
    {
        var info = new Line[lines.Count];
        double left = double.MaxValue, right = double.MinValue;
        for (int i = 0; i < lines.Count; i++)
        {
            info[i] = Measure(lines[i]);
            left = Math.Min(left, info[i].U0);
            right = Math.Max(right, info[i].U1);
        }

        double leading = Leading(info);
        TextBlock? current = null;
        for (int i = 0; i < lines.Count; i++)
        {
            Line line = info[i];
            bool bullet = IsBullet(lines[i]), number = IsNumberLabel(lines[i]);
            bool start = current is null;
            if (!start)
            {
                Line prev = info[i - 1];
                double gap = prev.V - line.V;
                double size = Math.Max(prev.Size, line.Size);
                start = Math.Abs(prev.Size - line.Size) > 0.15 * size
                    || gap > (leading * 1.35) + (0.1 * size)
                    || gap < 0
                    || bullet
                    || (number && (EndsClause(prev.Last) || current!.Kind == PdfTextBlockKind.ListItem))
                    || (prev.U1 < left + (0.75 * (right - left)) && EndsSentence(prev.Last) && current!.Kind != PdfTextBlockKind.ListItem)
                    || (line.U0 > left + line.Size && prev.U1 > right - (2 * line.Size) && prev.U0 <= left + (0.5 * line.Size) && current!.Kind != PdfTextBlockKind.ListItem);
            }

            if (start)
            {
                current = new TextBlock(bullet || number ? PdfTextBlockKind.ListItem : PdfTextBlockKind.Paragraph, 0, []);
                into.Add(current);
            }

            current!.Lines.Add(lines[i]);
        }
    }

    /// <summary>Headings by size and weight, and list levels by indent, for the paragraphs from <paramref name="start"/>.</summary>
    private static void Classify(List<TextBlock> blocks, int start, double bodySize)
    {
        double listLeft = double.MaxValue;
        for (int b = start; b < blocks.Count; b++)
        {
            if (blocks[b].Kind == PdfTextBlockKind.ListItem) listLeft = Math.Min(listLeft, Measure(blocks[b].Lines[0]).U0);
        }

        for (int b = start; b < blocks.Count; b++)
        {
            TextBlock block = blocks[b];
            Line first = Measure(block.Lines[0]);
            if (block.Kind == PdfTextBlockKind.ListItem)
            {
                // Each step of about an em and a half further in is one level deeper.
                block.Level = 1 + (int)Math.Min(5, Math.Max(0, Math.Round((first.U0 - listLeft) / (1.5 * Math.Max(first.Size, 1)))));
                continue;
            }

            int words = block.Lines.Sum(l => l.Count);
            double size = Size(block);
            double ratio = size / bodySize;
            if (block.Lines.Count <= MaxHeadingLines && words <= MaxHeadingWords && ratio >= HeadingRatio)
            {
                block.Kind = PdfTextBlockKind.Heading;
                block.Level = ratio >= 1.8 ? 1 : ratio >= 1.4 ? 2 : 3;
            }
            else if (block.Lines.Count == 1 && words <= MaxBoldHeadingWords && first.Bold && ratio >= 0.95 && !EndsClause(first.Last)
                && b + 1 < blocks.Count && blocks[b + 1].Kind != PdfTextBlockKind.Heading && !Measure(blocks[b + 1].Lines[0]).Bold)
            {
                block.Kind = PdfTextBlockKind.Heading;
                block.Level = 4;
            }
        }
    }

    private static double Size(TextBlock block)
    {
        double size = 0;
        foreach (List<PdfWord> line in block.Lines) size = Math.Max(size, Measure(line).Size);
        return size;
    }

    /// <summary>The usual distance between baselines in a block: the median of those between lines of one size, else 1.2 em.</summary>
    private static double Leading(Line[] lines)
    {
        var gaps = new List<double>();
        for (int i = 1; i < lines.Length; i++)
        {
            double gap = lines[i - 1].V - lines[i].V, size = lines[i].Size;
            if (Math.Abs(lines[i - 1].Size - size) <= 0.15 * size && gap >= 0.8 * size && gap <= 2.5 * size) gaps.Add(gap);
        }

        if (gaps.Count == 0) return 1.2 * (lines.Length > 0 ? lines.Max(l => l.Size) : 10);
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>Whether a line starts with a bullet: a bullet character alone, or a round one before text.</summary>
    public static bool IsBullet(List<PdfWord> line)
    {
        if (line.Count < 2 && (line.Count == 0 || line[0].Text.Length < 2)) return false;
        string first = line[0].Text;
        if (first.Length == 1) return Bullets.Contains(first[0]) && line.Count > 1;

        // "•Item" without a gap; a hyphen or star joined to text is a word, not a bullet.
        return first[0] is not ('-' or '*' or '\u2013' or '\u2014') && Bullets.Contains(first[0]) && char.IsLetterOrDigit(first[1]);
    }

    /// <summary>Whether a line starts with a number label followed by text: "1.", "2)", "(a)", "iv.", "B.".</summary>
    public static bool IsNumberLabel(List<PdfWord> line)
    {
        if (line.Count < 2) return false;
        string w = line[0].Text;
        if (w.Length < 2 || w.Length > 8) return false;
        int at = 0, end = w.Length - 1;
        if (w[0] == '(')
        {
            if (w[end] != ')') return false;
            at = 1;
        }
        else if (w[end] is not ('.' or ')'))
        {
            return false;
        }

        string label = w.Substring(at, end - at);
        if (label.Length == 0) return false;
        if (label.Length <= 3 && label.All(char.IsDigit)) return true;
        if (label.Length == 1 && char.IsLetter(label[0])) return true;
        return label.Length <= 6 && label.All(c => "ivxlcdmIVXLCDM".Contains(c));
    }

    private static bool EndsSentence(string text) => text.Length > 0 && ".!?:\"”’)»。".Contains(text[^1]);

    private static bool EndsClause(string text) => text.Length > 0 && ".!?:;,\"”’)»。".Contains(text[^1]);

    /// <summary>A line in its own direction's frame: baseline across (V), extent along (U0 to U1), its usual size and whether it is all bold.</summary>
    private readonly record struct Line(double V, double U0, double U1, double Size, bool Bold, string Last);

    private static Line Measure(List<PdfWord> words)
    {
        PdfLetter first = words[0].Letters[0];
        PdfPoint d = first.Direction, n = new(-d.Y, d.X);
        double u0 = double.MaxValue, u1 = double.MinValue;
        bool bold = true;
        var sizes = new Dictionary<double, int>();
        foreach (PdfWord w in words)
        {
            foreach (PdfLetter l in w.Letters)
            {
                double a = Dot(l.BaselineStart, d), b = Dot(l.BaselineEnd, d);
                u0 = Math.Min(u0, Math.Min(a, b));
                u1 = Math.Max(u1, Math.Max(a, b));
                bold &= IsBold(l.FontName);
                double size = Math.Round(l.FontSize * 2) / 2;
                sizes.TryGetValue(size, out int c);
                sizes[size] = c + Math.Max(1, l.Text.Length);
            }
        }

        double usual = sizes.OrderByDescending(e => e.Value).ThenByDescending(e => e.Key).First().Key;
        return new Line(Dot(first.BaselineStart, n), u0, u1, Math.Max(usual, 0.5), bold, words[^1].Text);
    }

    /// <summary>Whether a font name reads as a bold weight: Bold, Black, Heavy, Semibold, Demi.</summary>
    internal static bool IsBold(string fontName)
    {
        string name = fontName.ToLowerInvariant();
        return name.Contains("bold") || name.Contains("black") || name.Contains("heavy") || name.Contains("demi");
    }

    private static double Dot(PdfPoint a, PdfPoint b) => (a.X * b.X) + (a.Y * b.Y);
}
