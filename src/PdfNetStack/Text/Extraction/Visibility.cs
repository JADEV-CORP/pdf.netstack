using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Settles whether each letter of a page can be seen once the whole page is read, from what the
/// graphics state said as it was drawn (<see cref="LetterState.Hidden"/>) and what was painted
/// before and after it: invisible text over or under an image is a scan's OCR layer and is kept;
/// text under an opaque filled rectangle drawn later is covered; text in the colour of the
/// rectangle or page under it does not show.
/// </summary>
/// <remarks>
/// Only rectangles of one known colour count as a background or a cover; anything else under or
/// over a letter leaves it visible. The work per page is bounded: past
/// <see cref="MaxComparisons"/> region tests the remaining letters are not judged and stay visible.
/// </remarks>
internal static class Visibility
{
    /// <summary>Letter against region tests per page.</summary>
    public const long MaxComparisons = 20_000_000;

    /// <summary>The share of a letter's box an opaque rectangle must cover to hide it.</summary>
    private const double CoveredShare = 0.7;

    /// <summary>
    /// The reason each letter is hidden, null when it shows, and which visible letters are a scan's
    /// text layer: invisible, or covered by an image, with an image under or over them.
    /// </summary>
    public static (PdfHiddenTextReason?[] Hidden, bool[] Ocr) Judge(IReadOnlyList<PdfLetter> letters, IReadOnlyList<LetterState> states, GraphicsTracker graphics)
    {
        int n = letters.Count;
        var hidden = new PdfHiddenTextReason?[n];
        var ocr = new bool[n];
        List<PaintedRegion> regions = graphics.Regions;

        // Images, and opaque rectangles of one colour, by their position in painting order.
        var images = new List<int>();
        var covers = new List<int>();
        var colors = new List<TextColor> { TextColor.White };
        for (int r = 0; r < regions.Count; r++)
        {
            PaintedRegion region = regions[r];
            if (region.Kind == PaintKind.Image) images.Add(r);
            else if (region.Kind == PaintKind.Solid && region.Opaque)
            {
                covers.Add(r);
                if (!colors.Exists(c => c.LooksLike(region.Color))) colors.Add(region.Color);
            }
        }

        long budget = MaxComparisons;
        for (int i = 0; i < n; i++)
        {
            LetterState state = states[i];
            PdfLetter letter = letters[i];
            hidden[i] = state.Hidden;
            PdfRectangle box = letter.Bounds;
            PdfPoint center = new((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);

            if (state.Hidden == PdfHiddenTextReason.InvisibleRenderMode)
            {
                // The text layer of a scanned page sits over (or under) the page's image.
                if (AnyImage(regions, images, 0, center, ref budget))
                {
                    hidden[i] = null;
                    ocr[i] = true;
                }

                continue;
            }

            if (state.Hidden is not null || budget <= 0) continue;

            // Visible text under an image drawn after it: text under a scanned image, kept as OCR.
            if (AnyImage(regions, images, state.Painted, center, ref budget)) ocr[i] = true;
            else if (Covered(regions, covers, state.Painted, box, ref budget)) hidden[i] = PdfHiddenTextReason.Covered;
            else if (state.Complete && NoContrast(regions, colors, state, letter.RenderMode, center, ref budget) && !OverGlyph(letters, states, i, center, ref budget))
            {
                hidden[i] = PdfHiddenTextReason.NoContrast;
            }
        }

        return (hidden, ocr);
    }

    /// <summary>
    /// Whether a letter drawn earlier in another colour lies under <paramref name="center"/>: a
    /// white mark on a dark glyph, as in an icon font, shows although the page under both is white.
    /// </summary>
    private static bool OverGlyph(IReadOnlyList<PdfLetter> letters, IReadOnlyList<LetterState> states, int index, PdfPoint center, ref long budget)
    {
        TextColor color = states[index].Fill.Known ? states[index].Fill : states[index].Stroke;
        for (int j = index - 1; j >= 0 && budget > 0; j--)
        {
            budget--;
            if (states[j].Hidden is not null || !Contains(letters[j].Bounds, center)) continue;
            TextColor under = states[j].Fill.Known ? states[j].Fill : states[j].Stroke;
            if (!under.LooksLike(color)) return true;
        }

        return false;
    }

    /// <summary>Whether an image painted at or after <paramref name="from"/> holds <paramref name="point"/>.</summary>
    private static bool AnyImage(List<PaintedRegion> regions, List<int> images, int from, PdfPoint point, ref long budget)
    {
        for (int k = images.Count - 1; k >= 0 && images[k] >= from; k--)
        {
            budget--;
            if (Contains(regions[images[k]].Bounds, point)) return true;
        }

        return false;
    }

    /// <summary>Whether an opaque rectangle painted at or after <paramref name="from"/> covers most of <paramref name="box"/>.</summary>
    private static bool Covered(List<PaintedRegion> regions, List<int> covers, int from, PdfRectangle box, ref long budget)
    {
        double area = Math.Max(box.Width, 0) * Math.Max(box.Height, 0);
        if (area <= 0) return false;
        for (int k = covers.Count - 1; k >= 0 && covers[k] >= from; k--)
        {
            budget--;
            PdfRectangle r = regions[covers[k]].Bounds;
            double w = Math.Min(r.Right, box.Right) - Math.Max(r.Left, box.Left), h = Math.Min(r.Top, box.Top) - Math.Max(r.Bottom, box.Bottom);
            if (w > 0 && h > 0 && w * h >= CoveredShare * area) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every paint the letter uses has the colour of what lies under its centre: the topmost
    /// region painted before it there, or the white page. A region that is not a solid opaque
    /// rectangle under it makes the background unknown, and the letter shows.
    /// </summary>
    private static bool NoContrast(List<PaintedRegion> regions, List<TextColor> colors, LetterState state, int mode, PdfPoint center, ref long budget)
    {
        // A paint the rendering mode uses in a colour that is not known counts as visible.
        bool fills = mode is 0 or 2 or 4 or 6, strokes = mode is 1 or 2 or 5 or 6;
        if ((fills && !state.Fill.Known) || (strokes && !state.Stroke.Known) || (!fills && !strokes)) return false;

        // Most text is in a colour no background on the page has: nothing to look up.
        if ((!fills || !Candidate(state.Fill, colors)) && (!strokes || !Candidate(state.Stroke, colors))) return false;

        TextColor background = TextColor.White;
        for (int r = state.Painted - 1; r >= 0; r--)
        {
            budget--;
            PaintedRegion region = regions[r];
            if (!Contains(region.Bounds, center)) continue;
            background = region.Kind == PaintKind.Solid && region.Opaque ? region.Color : TextColor.Unknown;
            break;
        }

        if (!background.Known) return false;
        return (!fills || state.Fill.LooksLike(background)) && (!strokes || state.Stroke.LooksLike(background));
    }

    private static bool Candidate(TextColor color, List<TextColor> colors) => color.Known && colors.Exists(c => c.LooksLike(color));

    private static bool Contains(PdfRectangle r, PdfPoint p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;
}
