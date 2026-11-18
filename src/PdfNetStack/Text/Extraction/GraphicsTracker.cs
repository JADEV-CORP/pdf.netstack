using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Text.Extraction;

/// <summary>A colour as red, green and blue from 0 to 1, or unknown.</summary>
internal readonly record struct TextColor(double R, double G, double B, bool Known)
{
    /// <summary>
    /// Channels closer than this are one colour to the eye: #FAFAFA text on white is. Light grey
    /// text such as #F0F0F0 still reads, and is kept.
    /// </summary>
    public const double Tolerance = 0.025;

    public static TextColor Unknown => default;

    public static TextColor Black => new(0, 0, 0, true);

    public static TextColor White => new(1, 1, 1, true);

    /// <summary>True only when both colours are known and look the same.</summary>
    public bool LooksLike(TextColor other) =>
        Known && other.Known && Math.Abs(R - other.R) <= Tolerance && Math.Abs(G - other.G) <= Tolerance && Math.Abs(B - other.B) <= Tolerance;
}

/// <summary>What painted a region of the page.</summary>
internal enum PaintKind : byte
{
    /// <summary>A filled rectangle of one known colour.</summary>
    Solid,

    /// <summary>An image.</summary>
    Image,

    /// <summary>Any other fill or shading: its colour or shape is not known well enough to judge what is under or over it.</summary>
    Unknown,
}

/// <summary>
/// A painted region, in default user space. <see cref="Bounds"/> is exact for a
/// <see cref="PaintKind.Solid"/> rectangle and a bounding box otherwise.
/// </summary>
internal readonly record struct PaintedRegion(PdfRectangle Bounds, PaintKind Kind, TextColor Color, bool Opaque);

/// <summary>A straight horizontal or vertical line drawn on the page, as a table's ruling: at <see cref="Position"/> across, from <see cref="From"/> to <see cref="To"/> along.</summary>
internal readonly record struct RuleLine(bool Horizontal, double Position, double From, double To);

/// <summary>
/// Follows the parts of the graphics state that decide whether text can be seen (ISO 32000-1,
/// 8.4): fill and stroke colour, constant opacity and soft mask, blend mode, the clipping path as
/// a bounding box, and optional content. Records what the page paints, in order, and the straight
/// lines it draws, which tables are ruled with.
/// </summary>
/// <remarks>
/// Every approximation keeps text rather than drops it: the clip is a box that holds the real
/// clipping path, a colour that cannot be read is unknown, and only a filled rectangle of a known
/// colour counts as a background or a cover. Records are bounded per page; past the bound a page
/// is reported as <see cref="Complete"/> false and its colour checks are skipped.
/// </remarks>
internal sealed class GraphicsTracker
{
    public const int MaxRegions = 20_000;

    public const int MaxRules = 20_000;

    // Line segments of the current path kept for rules; a path with more is not a ruling.
    private const int MaxSegments = 4096;

    /// <summary>A filled rectangle thinner than this, in points, and at least <see cref="MinRuleLength"/> long, is a rule.</summary>
    private const double MaxRuleThickness = 2.5;

    private const double MinRuleLength = 4;

    // Straight within this much, in points.
    private const double Straight = 0.5;

    private readonly PdfDocument _document;
    private readonly OptionalContent _optional;
    private readonly Stack<State> _saved = new();
    private readonly List<(double X0, double Y0, double X1, double Y1)> _segments = [];
    private State _state = State.Initial;

    // The current path: its box in default user space, whether it is one rectangle of the page's
    // axes (its exact box then), and the current point and subpath start in user space.
    private bool _hasPath;
    private double _left, _bottom, _right, _top;
    private int _rectangles;
    private bool _otherSegments;
    private PdfPoint _current, _start;
    private bool _clipPending;

    public GraphicsTracker(PdfDocument document, OptionalContent optional)
    {
        _document = document;
        _optional = optional;
    }

    /// <summary>What the page painted, in order.</summary>
    public List<PaintedRegion> Regions { get; } = [];

    /// <summary>Horizontal and vertical lines the page drew.</summary>
    public List<RuleLine> Rules { get; } = [];

    /// <summary>False once a page painted more than <see cref="MaxRegions"/> regions: later colour and cover checks cannot be trusted.</summary>
    public bool Complete { get; private set; } = true;

    public TextColor Fill => _state.Fill;

    public TextColor Stroke => _state.Stroke;

    /// <summary>Opacity of fills, 0 to 1; 1 when a soft mask makes it unknown.</summary>
    public double FillAlpha => _state.SoftMask ? 1 : _state.FillAlpha;

    /// <summary>Opacity of strokes, 0 to 1; 1 when a soft mask makes it unknown.</summary>
    public double StrokeAlpha => _state.SoftMask ? 1 : _state.StrokeAlpha;

    /// <summary>The clipping path's bounding box in default user space, or null for none.</summary>
    public PdfRectangle? Clip => _state.Clip;

    /// <summary>True inside a form XObject whose optional content is off.</summary>
    public bool Hidden => _state.Hidden;

    /// <summary>The interpreter saved its state; each save is matched by one <see cref="Restore"/>.</summary>
    public void Save() => _saved.Push(_state);

    public void Restore()
    {
        if (_saved.Count > 0) _state = _saved.Pop();
    }

    /// <summary>A form XObject begins: its /BBox clips what it draws (ISO 32000-1, 8.10.1), and its /OC may hide it.</summary>
    public void BeginForm(PdfStream form, PdfMatrix ctm)
    {
        if (PdfRectangle.From(form.Dictionary["BBox"], _document.Resolve) is PdfRectangle box)
        {
            Intersect(Transform(box, ctm));
        }

        if (_document.Resolve(form.Dictionary["OC"]) is PdfDictionary oc && !_optional.IsVisible(oc)) _state.Hidden = true;
    }

    /// <summary>An image is drawn: the unit square through <paramref name="ctm"/>.</summary>
    public void Image(PdfMatrix ctm) => AddRegion(new PaintedRegion(Transform(new PdfRectangle(0, 0, 1, 1), ctm), PaintKind.Image, TextColor.Unknown, true));

    /// <summary>A path, clipping, colour or graphics state operator.</summary>
    public void Operator(string op, List<PdfObject> o, PdfDictionary? resources, PdfMatrix ctm)
    {
        switch (op)
        {
            case "m" when Numbers(o, 2) is { } p:
                _current = _start = new PdfPoint(p[0], p[1]);
                Extend(_current, ctm);
                break;
            case "l" when Numbers(o, 2) is { } p:
                LineTo(new PdfPoint(p[0], p[1]), ctm);
                break;
            case "c" when Numbers(o, 6) is { } p:
                CurveTo([new(p[0], p[1]), new(p[2], p[3]), new(p[4], p[5])], ctm);
                break;
            case "v" when Numbers(o, 4) is { } p:
                CurveTo([new(p[0], p[1]), new(p[2], p[3])], ctm);
                break;
            case "y" when Numbers(o, 4) is { } p:
                CurveTo([new(p[0], p[1]), new(p[2], p[3])], ctm);
                break;
            case "h":
                if (_hasPath && _current != _start) LineTo(_start, ctm);
                break;
            case "re" when Numbers(o, 4) is { } r:
                Rectangle(r[0], r[1], r[2], r[3], ctm);
                break;
            case "W":
            case "W*":
                _clipPending = true;
                break;
            case "n":
                EndPath();
                break;
            case "f":
            case "F":
            case "f*":
                Paint(fill: true, stroke: false);
                break;
            case "S":
                Paint(fill: false, stroke: true);
                break;
            case "s":
                if (_hasPath && _current != _start) LineTo(_start, ctm);
                Paint(fill: false, stroke: true);
                break;
            case "B":
            case "B*":
                Paint(fill: true, stroke: true);
                break;
            case "b":
            case "b*":
                if (_hasPath && _current != _start) LineTo(_start, ctm);
                Paint(fill: true, stroke: true);
                break;
            case "sh":
                // A shading fills the clip region, whatever its colours (ISO 32000-1, 8.7.4.2).
                AddRegion(new PaintedRegion(_state.Clip ?? Everywhere, PaintKind.Unknown, TextColor.Unknown, false));
                break;
            case "g" when Numbers(o, 1) is { } c:
                _state.Fill = Gray(c[0]);
                _state.FillSpace = Space.Gray;
                break;
            case "G" when Numbers(o, 1) is { } c:
                _state.Stroke = Gray(c[0]);
                _state.StrokeSpace = Space.Gray;
                break;
            case "rg" when Numbers(o, 3) is { } c:
                _state.Fill = Rgb(c[0], c[1], c[2]);
                _state.FillSpace = Space.Rgb;
                break;
            case "RG" when Numbers(o, 3) is { } c:
                _state.Stroke = Rgb(c[0], c[1], c[2]);
                _state.StrokeSpace = Space.Rgb;
                break;
            case "k" when Numbers(o, 4) is { } c:
                _state.Fill = Cmyk(c[0], c[1], c[2], c[3]);
                _state.FillSpace = Space.Cmyk;
                break;
            case "K" when Numbers(o, 4) is { } c:
                _state.Stroke = Cmyk(c[0], c[1], c[2], c[3]);
                _state.StrokeSpace = Space.Cmyk;
                break;
            case "cs" when o.Count > 0 && o[^1] is PdfName name:
                _state.FillSpace = ColorSpace(name.Value, resources);
                _state.Fill = InitialColor(_state.FillSpace);
                break;
            case "CS" when o.Count > 0 && o[^1] is PdfName name:
                _state.StrokeSpace = ColorSpace(name.Value, resources);
                _state.Stroke = InitialColor(_state.StrokeSpace);
                break;
            case "sc":
            case "scn":
                _state.Fill = Components(_state.FillSpace, o);
                break;
            case "SC":
            case "SCN":
                _state.Stroke = Components(_state.StrokeSpace, o);
                break;
            case "gs" when o.Count > 0 && o[^1] is PdfName name:
                GraphicsState(name.Value, resources);
                break;
        }
    }

    private static readonly PdfRectangle Everywhere = new(-1e9, -1e9, 1e9, 1e9);

    private void AddRegion(PaintedRegion region)
    {
        if (Regions.Count < MaxRegions) Regions.Add(region);
        else Complete = false;
    }

    private void AddRule(RuleLine rule)
    {
        if (Rules.Count < MaxRules) Rules.Add(rule);
    }

    private void Extend(PdfPoint userPoint, PdfMatrix ctm)
    {
        PdfPoint p = ctm.Transform(userPoint);
        if (!Finite(p.X) || !Finite(p.Y)) return;
        if (!_hasPath)
        {
            (_left, _bottom, _right, _top) = (p.X, p.Y, p.X, p.Y);
            _hasPath = true;
            return;
        }

        _left = Math.Min(_left, p.X);
        _bottom = Math.Min(_bottom, p.Y);
        _right = Math.Max(_right, p.X);
        _top = Math.Max(_top, p.Y);
    }

    private void LineTo(PdfPoint to, PdfMatrix ctm)
    {
        Segment(_current, to, ctm);
        _current = to;
        Extend(to, ctm);
        _otherSegments = true;
    }

    private void CurveTo(PdfPoint[] points, PdfMatrix ctm)
    {
        // The curve lies within the hull of its control points, so their box holds it.
        foreach (PdfPoint p in points) Extend(p, ctm);
        _current = points[^1];
        _otherSegments = true;
    }

    private void Rectangle(double x, double y, double w, double h, PdfMatrix ctm)
    {
        PdfPoint a = new(x, y), b = new(x + w, y), c = new(x + w, y + h), d = new(x, y + h);
        Extend(a, ctm);
        Extend(b, ctm);
        Extend(c, ctm);
        Extend(d, ctm);
        Segment(a, b, ctm);
        Segment(b, c, ctm);
        Segment(c, d, ctm);
        Segment(d, a, ctm);
        _current = _start = a;
        _rectangles++;
    }

    private void Segment(PdfPoint from, PdfPoint to, PdfMatrix ctm)
    {
        if (_segments.Count >= MaxSegments) return;
        PdfPoint a = ctm.Transform(from), b = ctm.Transform(to);
        if (Finite(a.X) && Finite(a.Y) && Finite(b.X) && Finite(b.Y)) _segments.Add((a.X, a.Y, b.X, b.Y));
    }

    /// <summary>Paints the current path, records what it covers and the lines it draws, applies a pending clip, and ends the path.</summary>
    private void Paint(bool fill, bool stroke)
    {
        if (_hasPath)
        {
            var box = new PdfRectangle(_left, _bottom, _right, _top);
            bool rectangle = _rectangles == 1 && !_otherSegments && IsAxisRectangle();
            if (fill)
            {
                bool solid = rectangle && _state.Fill.Known;
                bool opaque = _state.FillAlpha >= 1 && !_state.SoftMask && _state.NormalBlend;
                AddRegion(new PaintedRegion(box, solid ? PaintKind.Solid : PaintKind.Unknown, solid ? _state.Fill : TextColor.Unknown, opaque));

                // A thin filled rectangle draws a rule.
                if (rectangle && box.Height <= MaxRuleThickness && box.Width >= MinRuleLength) AddRule(new RuleLine(true, (box.Bottom + box.Top) / 2, box.Left, box.Right));
                else if (rectangle && box.Width <= MaxRuleThickness && box.Height >= MinRuleLength) AddRule(new RuleLine(false, (box.Left + box.Right) / 2, box.Bottom, box.Top));
            }

            if (stroke && _segments.Count < MaxSegments)
            {
                foreach ((double x0, double y0, double x1, double y1) in _segments)
                {
                    if (Math.Abs(y1 - y0) <= Straight && Math.Abs(x1 - x0) >= MinRuleLength) AddRule(new RuleLine(true, (y0 + y1) / 2, Math.Min(x0, x1), Math.Max(x0, x1)));
                    else if (Math.Abs(x1 - x0) <= Straight && Math.Abs(y1 - y0) >= MinRuleLength) AddRule(new RuleLine(false, (x0 + x1) / 2, Math.Min(y0, y1), Math.Max(y0, y1)));
                }
            }
        }

        EndPath();
    }

    /// <summary>Whether the four segments of the path's one rectangle run along the page's axes, so its box is exact.</summary>
    private bool IsAxisRectangle()
    {
        if (_segments.Count != 4) return false;
        foreach ((double x0, double y0, double x1, double y1) in _segments)
        {
            if (Math.Abs(x1 - x0) > Straight && Math.Abs(y1 - y0) > Straight) return false;
        }

        return true;
    }

    private void EndPath()
    {
        // W marks the path for clipping; the clip takes effect when the path is painted or ended (ISO 32000-1, 8.5.4).
        if (_clipPending)
        {
            // W without a path clips nothing a viewer agrees on; it is ignored.
            if (_hasPath) Intersect(new PdfRectangle(_left, _bottom, _right, _top));
            _clipPending = false;
        }

        _hasPath = false;
        _rectangles = 0;
        _otherSegments = false;
        _segments.Clear();
    }

    private void Intersect(PdfRectangle box)
    {
        if (_state.Clip is not PdfRectangle clip)
        {
            _state.Clip = box;
            return;
        }

        double left = Math.Max(clip.Left, box.Left), bottom = Math.Max(clip.Bottom, box.Bottom);
        double right = Math.Min(clip.Right, box.Right), top = Math.Min(clip.Top, box.Top);
        _state.Clip = right < left || top < bottom ? new PdfRectangle(left, bottom, left, bottom) : new PdfRectangle(left, bottom, right, top);
    }

    private void GraphicsState(string name, PdfDictionary? resources)
    {
        if (_document.Resolve(resources?["ExtGState"]) is not PdfDictionary states || _document.Resolve(states[name]) is not PdfDictionary gs) return;
        if (_document.Resolve(gs["ca"]) is PdfNumber ca && Finite(ca.ToDouble())) _state.FillAlpha = Clamp(ca.ToDouble());
        if (_document.Resolve(gs["CA"]) is PdfNumber sa && Finite(sa.ToDouble())) _state.StrokeAlpha = Clamp(sa.ToDouble());
        PdfObject? mask = _document.Resolve(gs["SMask"]);
        if (mask is PdfName { Value: "None" }) _state.SoftMask = false;
        else if (mask is PdfDictionary) _state.SoftMask = true;

        PdfObject? blend = _document.Resolve(gs["BM"]);
        if (blend is PdfArray { Count: > 0 } modes) blend = _document.Resolve(modes[0]);
        if (blend is PdfName mode) _state.NormalBlend = mode.Value is "Normal" or "Compatible";
    }

    private Space ColorSpace(string name, PdfDictionary? resources)
    {
        switch (name)
        {
            case "DeviceGray":
            case "CalGray":
                return Space.Gray;
            case "DeviceRGB":
            case "CalRGB":
                return Space.Rgb;
            case "DeviceCMYK":
                return Space.Cmyk;
        }

        if (_document.Resolve(resources?["ColorSpace"]) is not PdfDictionary spaces) return Space.Unknown;
        PdfObject? space = _document.Resolve(spaces[name]);
        if (space is PdfName named) return named.Value == name ? Space.Unknown : ColorSpace(named.Value, null);
        if (space is not PdfArray { Count: > 0 } array || _document.Resolve(array[0]) is not PdfName family) return Space.Unknown;
        switch (family.Value)
        {
            case "CalGray":
                return Space.Gray;
            case "CalRGB":
                return Space.Rgb;
            case "ICCBased" when array.Count > 1 && _document.Resolve(array[1]) is PdfStream profile:
                return (_document.Resolve(profile.Dictionary["N"]) as PdfNumber)?.IntegerValue switch
                {
                    1 => Space.Gray,
                    3 => Space.Rgb,
                    4 => Space.Cmyk,
                    _ => Space.Unknown,
                };
            case "Separation":
                // One colorant: a tint of 0 puts no ink down, which is white; any other tint depends on the colorant.
                return Space.Separation;
            default:
                return Space.Unknown;
        }
    }

    private static TextColor InitialColor(Space space) => space switch
    {
        // The initial colour of each space (ISO 32000-1, 8.6.8): black, or a tint of 1.
        Space.Gray or Space.Rgb or Space.Cmyk => TextColor.Black,
        _ => TextColor.Unknown,
    };

    private static TextColor Components(Space space, List<PdfObject> o)
    {
        int count = space switch
        {
            Space.Gray or Space.Separation => 1,
            Space.Rgb => 3,
            Space.Cmyk => 4,
            _ => 0,
        };

        // A pattern name after the components, or too few numbers, leaves the colour unknown.
        if (count == 0 || o.Count != count || Numbers(o, count) is not { } c) return TextColor.Unknown;
        return space switch
        {
            Space.Gray => Gray(c[0]),
            Space.Rgb => Rgb(c[0], c[1], c[2]),
            Space.Cmyk => Cmyk(c[0], c[1], c[2], c[3]),
            Space.Separation => c[0] <= 0 ? TextColor.White : TextColor.Unknown,
            _ => TextColor.Unknown,
        };
    }

    private static TextColor Gray(double g) => new(Clamp(g), Clamp(g), Clamp(g), true);

    private static TextColor Rgb(double r, double g, double b) => new(Clamp(r), Clamp(g), Clamp(b), true);

    // The naive conversion (ISO 32000-1, 10.3.5): enough to tell white from ink.
    private static TextColor Cmyk(double c, double m, double y, double k) =>
        new((1 - Clamp(c)) * (1 - Clamp(k)), (1 - Clamp(m)) * (1 - Clamp(k)), (1 - Clamp(y)) * (1 - Clamp(k)), true);

    private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    internal static PdfRectangle Transform(PdfRectangle box, PdfMatrix m)
    {
        PdfPoint a = m.Transform(new(box.Left, box.Bottom)), b = m.Transform(new(box.Right, box.Bottom));
        PdfPoint c = m.Transform(new(box.Right, box.Top)), d = m.Transform(new(box.Left, box.Top));
        return new PdfRectangle(
            Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
    }

    private static double[]? Numbers(List<PdfObject> o, int count)
    {
        if (o.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (o[o.Count - count + i] is not PdfNumber n) return null;
            double v = n.ToDouble();
            if (!Finite(v)) return null;
            result[i] = v;
        }

        return result;
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private enum Space : byte
    {
        Gray,
        Rgb,
        Cmyk,
        Separation,
        Unknown,
    }

    /// <summary>The parts of the graphics state followed here; saved and restored with q and Q.</summary>
    private struct State
    {
        public TextColor Fill;
        public TextColor Stroke;
        public Space FillSpace;
        public Space StrokeSpace;
        public double FillAlpha;
        public double StrokeAlpha;
        public bool SoftMask;
        public bool NormalBlend;
        public PdfRectangle? Clip;
        public bool Hidden;

        public static State Initial => new()
        {
            Fill = TextColor.Black,
            Stroke = TextColor.Black,
            FillSpace = Space.Gray,
            StrokeSpace = Space.Gray,
            FillAlpha = 1,
            StrokeAlpha = 1,
            NormalBlend = true,
        };
    }
}
