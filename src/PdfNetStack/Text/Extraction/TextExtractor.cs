using PdfNetStack.Content;
using PdfNetStack.Documents;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Reads the letters a page draws, in content order: each glyph's text, box and baseline from the
/// text state and the font (ISO 32000-1, 9.4.4), with spans marked with /ActualText (14.9.4)
/// replaced by their text.
/// </summary>
/// <remarks>
/// With <c>detail</c> set, as <see cref="PdfDocument.ReadText(PdfTextOptions?, CancellationToken)"/>
/// asks, each letter also gets a <see cref="LetterState"/>: its marked content, whether it is an
/// artifact, its colours and whether the graphics state hides it. Plain extraction leaves that off
/// and pays nothing for it.
/// </remarks>
internal sealed class TextExtractor(PdfDocument document, OptionalContent? detail = null) : ContentInterpreter(document)
{
    /// <summary>Letters read per page; text beyond them is ignored.</summary>
    public const int MaxLetters = 500_000;

    /// <summary>Distinct fonts loaded per page; text in further fonts is read with default metrics.</summary>
    private const int MaxFonts = 4096;

    private readonly Dictionary<PdfDictionary, TextFont> _fonts = [];
    private readonly Dictionary<string, TextFont> _missing = new(StringComparer.Ordinal);
    private readonly FontBudget _budget = new();

    // Open marked content sequences: the replacement text of an ActualText span, and the first letter in it.
    private readonly List<(string? ActualText, int Start)> _marked = [];
    private int _actualTextDepth;

    // Characters in Letters, against PdfLimits.MaxTextCharsPerPage.
    private long _chars;
    private bool _full;

    // With detail: the graphics state, and the open marked content sequences' own state.
    private readonly GraphicsTracker? _graphics = detail is null ? null : new GraphicsTracker(document, detail);
    private readonly List<MarkedState> _markedState = [];

    public List<PdfLetter> Letters { get; } = [];

    /// <summary>With detail, one entry per letter of <see cref="Letters"/>, at the same index; else empty.</summary>
    public List<LetterState> States { get; } = [];

    /// <summary>With detail, what the page painted and the lines it drew; else null.</summary>
    public GraphicsTracker? Graphics => _graphics;

    /// <summary>The visible region of the page, for <see cref="PdfHiddenTextReason.OutsidePage"/>; null when not checked.</summary>
    public PdfRectangle? CropBox { get; init; }

    /// <summary>
    /// With detail, apply /ActualText that is far longer than the glyphs it stands for; when
    /// false such a span keeps its glyphs' own text. Either way it is listed in <see cref="Replacements"/>.
    /// </summary>
    public bool ApplyLongReplacements { get; init; } = true;

    /// <summary>With detail, spans whose /ActualText is far longer than their glyphs: the replacement text and the glyphs' box.</summary>
    public List<(string Text, PdfRectangle Bounds)> Replacements { get; } = [];

    protected override void OnShowText(List<PdfObject> items, PdfDictionary? resources)
    {
        TextFont font = Font(resources, Text.FontName);
        foreach (PdfObject item in items)
        {
            if (item is PdfNumber number)
            {
                // A TJ adjustment, in thousandths of the font size, moves against the writing direction (9.4.3).
                double adjust = number.ToDouble();
                if (double.IsNaN(adjust) || double.IsInfinity(adjust)) continue;
                double shift = -adjust / 1000 * Text.FontSize;
                if (font.IsVertical) AdvanceText(0, shift);
                else AdvanceText(shift * Text.HorizontalScaling, 0);
                continue;
            }

            if (item is not PdfString s) continue;
            ReadOnlySpan<byte> bytes = s.Bytes;
            for (int at = 0; at < bytes.Length;)
            {
                int length = font.NextCode(bytes, at, out uint code);
                at += length;
                Show(font, code, length);
            }
        }
    }

    protected override void OnBeginMarkedContent(string tag, PdfDictionary? properties)
    {
        // An ActualText span inside another is part of the outer replacement.
        string? actualText = null;
        if (_actualTextDepth == 0 && properties is not null && Document.Resolve(properties["ActualText"]) is PdfString s)
        {
            actualText = PdfText.Decode(s.Value);
        }

        if (actualText is not null) _actualTextDepth++;
        _marked.Add((actualText, Letters.Count));
        if (detail is not null) _markedState.Add(Marked(tag, properties));
    }

    /// <summary>A sequence's state: the innermost MCID, and whether it or one around it is an artifact or optional content that is off.</summary>
    private MarkedState Marked(string tag, PdfDictionary? properties)
    {
        MarkedState outer = _markedState.Count > 0 ? _markedState[^1] : new MarkedState(-1, false, false);
        int mcid = Document.Resolve(properties?["MCID"]) is PdfNumber { IsInteger: true } n && n.IntegerValue is >= 0 and <= int.MaxValue ? (int)n.IntegerValue : outer.Mcid;
        bool hidden = outer.Hidden || (tag == "OC" && properties is not null && !detail!.IsVisible(properties));
        return new MarkedState(mcid, outer.Artifact || tag == "Artifact", hidden);
    }

    protected override void OnEndMarkedContent()
    {
        if (_marked.Count == 0) return;
        (string? actualText, int start) = _marked[^1];
        _marked.RemoveAt(_marked.Count - 1);
        if (_markedState.Count > 0) _markedState.RemoveAt(_markedState.Count - 1);
        if (actualText is null) return;
        _actualTextDepth--;
        Replace(start, actualText);
    }

    protected override void OnOperator(string op, List<PdfObject> operands, PdfDictionary? resources) => _graphics?.Operator(op, operands, resources, Ctm);

    protected override void OnSaveState() => _graphics?.Save();

    protected override void OnRestoreState() => _graphics?.Restore();

    protected override void OnBeginForm(PdfStream form) => _graphics?.BeginForm(form, Ctm);

    protected override void OnImage(PdfStream image, PdfDictionary? resources) => _graphics?.Image(Ctm);

    protected override void OnInlineImage(PdfDictionary image, ReadOnlyMemory<byte> data, PdfDictionary? resources) => _graphics?.Image(Ctm);

    /// <summary>One glyph: a letter where the text rendering matrix puts it, then the text matrix moves by its displacement.</summary>
    private void Show(TextFont font, uint code, int length)
    {
        TextState t = Text;
        double size = t.FontSize;
        PdfMatrix m = PdfMatrix.Multiply(TextMatrix, Ctm);

        // Trm = [Tfs Th, 0, 0, Tfs, 0, Trise] x Tm x CTM (9.4.4).
        PdfMatrix trm = PdfMatrix.Multiply(new PdfMatrix(size * t.HorizontalScaling, 0, 0, size, 0, t.Rise), m);

        // Word spacing applies to the single byte code 32, in simple and composite fonts alike (9.3.3).
        double spacing = t.CharacterSpacing + (length == 1 && code == 32 ? t.WordSpacing : 0);
        double w0 = font.Width(code) / 1000;
        string text = font.Unicode(code, length) ?? "\uFFFD";
        double em = size * Math.Sqrt((m.C * m.C) + (m.D * m.D));

        // The glyph's width scale on the page, which Tz can squeeze to nothing while the height stays.
        _width = size * Math.Abs(t.HorizontalScaling) * Math.Sqrt((m.A * m.A) + (m.B * m.B));
        if (!font.IsVertical)
        {
            if (Letters.Count < MaxLetters)
            {
                Add(text, trm, (0, w0), (font.Descent / 1000, font.Ascent / 1000), (0, 0), (w0, 0), font, em, t.RenderMode);
            }

            AdvanceText(((w0 * size) + spacing) * t.HorizontalScaling, 0);
        }
        else
        {
            // Vertical writing (9.7.4.3): the glyph hangs from its vertical origin, the position
            // vector away from the horizontal one, and the advance w1 moves down.
            (double w1, double vx, _) = font.VerticalMetrics(code);
            w1 /= 1000;
            vx /= 1000;
            if (Letters.Count < MaxLetters)
            {
                Add(text, trm, (-vx, -vx + w0), (w1, 0), (0, 0), (0, w1), font, em, t.RenderMode);
            }

            AdvanceText(0, (w1 * size) + spacing);
        }
    }

    private void Add(string text, PdfMatrix trm, (double Left, double Right) x, (double Bottom, double Top) y, (double X, double Y) start, (double X, double Y) end, TextFont font, double size, int mode)
    {
        PdfPoint a = trm.Transform(new(x.Left, y.Bottom)), b = trm.Transform(new(x.Right, y.Bottom));
        PdfPoint c = trm.Transform(new(x.Right, y.Top)), d = trm.Transform(new(x.Left, y.Top));
        var bounds = new PdfRectangle(Min(a.X, b.X, c.X, d.X), Min(a.Y, b.Y, c.Y, d.Y), Max(a.X, b.X, c.X, d.X), Max(a.Y, b.Y, c.Y, d.Y));
        PdfPoint from = trm.Transform(new(start.X, start.Y)), to = trm.Transform(new(end.X, end.Y));

        // The writing direction: the text space x axis, or y pointing down for vertical writing.
        double dx = font.IsVertical ? -trm.C : trm.A, dy = font.IsVertical ? -trm.D : trm.B;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        PdfPoint direction = length > 1e-12 ? new(dx / length, dy / length) : new(1, 0);
        if (!Finite(bounds.Left) || !Finite(bounds.Bottom) || !Finite(bounds.Right) || !Finite(bounds.Top) || !Finite(size) || !Finite(direction.X) || !Finite(direction.Y)) return;
        if (!Room(text.Length)) return;
        Letters.Add(new PdfLetter(text, bounds, from, to, direction, font.Name, size, mode));
        if (detail is not null) States.Add(State(bounds, size, mode));
    }

    // The width scale of the glyph being added, set by Show.
    private double _width;

    /// <summary>
    /// A letter's state as it is drawn: its marked content, and the first reason the graphics state
    /// hides it. Colour against the background, covering shapes and images over invisible text are
    /// judged once the whole page is read (see <see cref="Visibility"/>).
    /// </summary>
    private LetterState State(PdfRectangle bounds, double size, int mode)
    {
        GraphicsTracker g = _graphics!;
        MarkedState marked = _markedState.Count > 0 ? _markedState[^1] : new MarkedState(-1, false, false);
        bool fills = mode is 0 or 2 or 4 or 6, strokes = mode is 1 or 2 or 5 or 6;
        PdfHiddenTextReason? hidden = null;
        if (marked.Hidden || g.Hidden) hidden = PdfHiddenTextReason.OptionalContentOff;
        else if (CropBox is PdfRectangle page && !Overlaps(bounds, page)) hidden = PdfHiddenTextReason.OutsidePage;
        else if (g.Clip is PdfRectangle clip && !Overlaps(bounds, clip)) hidden = PdfHiddenTextReason.ClippedAway;
        else if (size < 1 || _width < 1) hidden = PdfHiddenTextReason.TooSmall;
        else if ((fills || strokes) && (!fills || g.FillAlpha <= 0) && (!strokes || g.StrokeAlpha <= 0)) hidden = PdfHiddenTextReason.Transparent;
        else if (!fills && !strokes) hidden = PdfHiddenTextReason.InvisibleRenderMode;
        return new LetterState(marked.Mcid, marked.Artifact, hidden, fills ? g.Fill : TextColor.Unknown, strokes ? g.Stroke : TextColor.Unknown, g.Regions.Count, g.Complete);
    }

    /// <summary>Whether two rectangles share any area, edges included, so a letter touching the page's edge is on it.</summary>
    private static bool Overlaps(PdfRectangle a, PdfRectangle b) => a.Left <= b.Right && b.Left <= a.Right && a.Bottom <= b.Top && b.Bottom <= a.Top;

    /// <summary>
    /// Takes <paramref name="length"/> characters from the page's budget; false, reported once,
    /// when they do not fit, and for every letter after.
    /// </summary>
    private bool Room(int length)
    {
        if (!_full && _chars + length <= Document.Limits.MaxTextCharsPerPage)
        {
            _chars += length;
            return true;
        }

        if (!_full)
        {
            _full = true;
            ReportPageLimit(PdfLimitKind.TextCharsPerPage, Compat.Format("The page's text is longer than {0} characters (PdfLimits.MaxTextCharsPerPage); the rest is left out.", Document.Limits.MaxTextCharsPerPage));
        }

        return false;
    }

    /// <summary>Replaces the letters of an ActualText span by one letter holding its text, or drops them for empty text.</summary>
    private void Replace(int start, string text)
    {
        int count = Letters.Count - start;
        if (count <= 0) return;
        if (detail is not null && Suspect(start, text)) return;
        for (int i = start; i < Letters.Count; i++) _chars -= Letters[i].Text.Length;

        // The span's state is its first letter's; it is hidden only when every glyph in it is,
        // for one reason, since replacement text stands for all of them.
        LetterState state = default;
        if (detail is not null)
        {
            state = States[start];
            for (int i = start + 1; i < States.Count && state.Hidden is not null; i++)
            {
                if (States[i].Hidden != state.Hidden) state = state with { Hidden = null };
            }

            States.RemoveRange(start, count);
        }

        if (text.Length == 0 || !Room(text.Length))
        {
            Letters.RemoveRange(start, count);
            return;
        }

        PdfLetter first = Letters[start];
        PdfPoint d = first.Direction;
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        double min = double.MaxValue, max = double.MinValue;
        PdfPoint from = first.BaselineStart, to = first.BaselineEnd;
        for (int i = start; i < Letters.Count; i++)
        {
            PdfLetter l = Letters[i];
            left = Math.Min(left, l.Bounds.Left);
            bottom = Math.Min(bottom, l.Bounds.Bottom);
            right = Math.Max(right, l.Bounds.Right);
            top = Math.Max(top, l.Bounds.Top);

            // The baseline runs from the first point to the last along the writing direction, whatever order the glyphs came in.
            foreach (PdfPoint p in new[] { l.BaselineStart, l.BaselineEnd })
            {
                double u = (p.X * d.X) + (p.Y * d.Y);
                if (u < min) (min, from) = (u, p);
                if (u > max) (max, to) = (u, p);
            }
        }

        Letters.RemoveRange(start, count);
        Letters.Add(new PdfLetter(text, new PdfRectangle(left, bottom, right, top), from, to, d, first.FontName, first.FontSize, first.RenderMode));
        if (detail is not null) States.Add(state);
    }

    /// <summary>
    /// Records a span whose replacement text is far longer than the visible glyphs it replaces:
    /// text a reader is never shown, and a way to plant some. True when the glyphs are to keep
    /// their own text. Ligatures, symbols and formulas spell out a few characters per glyph;
    /// this takes 24 characters plus 8 per visible one.
    /// </summary>
    private bool Suspect(int start, string text)
    {
        int glyphs = 0;
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        for (int i = start; i < Letters.Count; i++)
        {
            PdfLetter l = Letters[i];
            foreach (char c in l.Text)
            {
                if (!char.IsWhiteSpace(c)) glyphs++;
            }

            left = Math.Min(left, l.Bounds.Left);
            bottom = Math.Min(bottom, l.Bounds.Bottom);
            right = Math.Max(right, l.Bounds.Right);
            top = Math.Max(top, l.Bounds.Top);
        }

        if (text.Trim().Length <= 24 + (8 * glyphs)) return false;
        Replacements.Add((text, new PdfRectangle(left, bottom, right, top)));
        return !ApplyLongReplacements;
    }

    private TextFont Font(PdfDictionary? resources, string? name)
    {
        name ??= "";
        if (Document.Resolve(resources?["Font"]) is PdfDictionary fonts && Document.Resolve(fonts[name]) is PdfDictionary dict)
        {
            if (_fonts.TryGetValue(dict, out TextFont? font)) return font;
            if (_fonts.Count < MaxFonts)
            {
                font = TextFont.Load(Document, dict, name, _budget);
                _fonts[dict] = font;
                return font;
            }
        }

        if (!_missing.TryGetValue(name, out TextFont? missing))
        {
            missing = TextFont.Missing(name);
            if (_missing.Count < MaxFonts) _missing[name] = missing;
        }

        return missing;
    }

    private static double Min(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private static double Max(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

/// <summary>
/// What text extraction with detail knows of a letter besides the letter: the innermost marked
/// content identifier (-1 for none), whether it is an artifact, the first reason the graphics
/// state hides it, the colours it is filled and stroked with (unknown for a paint its rendering
/// mode does not use), the regions painted before it, and whether those are all recorded.
/// </summary>
internal readonly record struct LetterState(int Mcid, bool Artifact, PdfHiddenTextReason? Hidden, TextColor Fill, TextColor Stroke, int Painted, bool Complete);

/// <summary>A marked content sequence's state, inherited by the sequences inside it.</summary>
internal readonly record struct MarkedState(int Mcid, bool Artifact, bool Hidden);
