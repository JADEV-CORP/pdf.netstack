using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// A block of a page while it is built: lines of words, or a table's rows of cells, each cell its
/// lines of words.
/// </summary>
internal sealed class TextBlock
{
    public TextBlock(PdfTextBlockKind kind, int level, List<List<PdfWord>> lines)
    {
        Kind = kind;
        Level = level;
        Lines = lines;
    }

    public PdfTextBlockKind Kind { get; set; }

    public int Level { get; set; }

    /// <summary>The block's lines; empty for a table.</summary>
    public List<List<PdfWord>> Lines { get; }

    /// <summary>A table's rows, each a list of cells, each the cell's lines; null for other blocks.</summary>
    public List<List<List<List<PdfWord>>>>? Rows { get; init; }

    /// <summary>Every word of the block in reading order; a table's row by row.</summary>
    public IEnumerable<PdfWord> Words()
    {
        if (Rows is null)
        {
            foreach (List<PdfWord> line in Lines)
            {
                foreach (PdfWord word in line) yield return word;
            }

            yield break;
        }

        foreach (List<List<List<PdfWord>>> row in Rows)
        {
            foreach (List<List<PdfWord>> cell in row)
            {
                foreach (List<PdfWord> line in cell)
                {
                    foreach (PdfWord word in line) yield return word;
                }
            }
        }
    }

    public PdfRectangle Bounds()
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (PdfWord w in Words())
        {
            left = Math.Min(left, w.Bounds.Left);
            bottom = Math.Min(bottom, w.Bounds.Bottom);
            right = Math.Max(right, w.Bounds.Right);
            top = Math.Max(top, w.Bounds.Top);
        }

        return left == double.MaxValue ? default : new PdfRectangle(left, bottom, right, top);
    }
}
