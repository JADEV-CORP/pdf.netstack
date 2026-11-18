using PdfNetStack.Fonts.OpenType;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.Type1;

/// <summary>
/// Runs a decrypted Type 1 charstring (Adobe Type 1 Font Format, chapter 6) to the
/// <see cref="GlyphOutline"/> a rasterizer draws, with the semantics FreeType and Adobe readers
/// share: subroutines inlined, flex through OtherSubrs 0 to 2, hint replacement through OtherSubr
/// 3, stem3 hints as counter groups and seac as an accented character. Everything else that
/// changes rendering (counter control OtherSubrs, dotsection, a vertical advance, multiple master
/// blends) throws <see cref="FontFormatException"/>, so such a font is left as it is.
/// </summary>
internal sealed class Type1Interpreter
{
    private const int MaxStack = 64, MaxDepth = 10, MaxSteps = 1 << 20;

    private readonly byte[][] _subrs;
    private readonly List<double> _stack = new(MaxStack);
    private readonly Stack<double> _results = new();
    private readonly GlyphOutline _outline = new();
    private readonly List<(double X, double Y)> _flex = [];
    private double _x, _y, _sbx, _sby;
    private bool _widthSeen, _open, _flexing, _ended, _drawn;
    private int _steps;

    private Type1Interpreter(byte[][] subrs) => _subrs = subrs;

    /// <summary>The glyph a charstring draws.</summary>
    /// <param name="budget">Steps left for the whole font; this glyph's are taken from it.</param>
    /// <exception cref="FontFormatException">The charstring is malformed or uses something without an exact CFF equivalent.</exception>
    public static GlyphOutline Run(byte[] charstring, byte[][] subrs, ref long budget)
    {
        var interpreter = new Type1Interpreter(subrs);
        interpreter.Execute(charstring, 0);
        if (!interpreter._ended) throw new FontFormatException("Type 1 charstring does not end with endchar or seac.");
        if ((budget -= interpreter._steps) < 0) throw new FontFormatException("Type 1 font takes too much work to read.");
        interpreter._outline.Finish();
        return interpreter._outline;
    }

    /// <summary>The glyph a charstring draws, on its own.</summary>
    public static GlyphOutline Run(byte[] charstring, byte[][] subrs)
    {
        long budget = Type2Interpreter.MaxStepsPerFont;
        return Run(charstring, subrs, ref budget);
    }

    /// <returns>False once the glyph has ended.</returns>
    private bool Execute(byte[] code, int depth)
    {
        if (depth > MaxDepth) throw new FontFormatException("Type 1 subroutines nest too deeply.");
        for (int i = 0; i < code.Length;)
        {
            if (++_steps > MaxSteps) throw new FontFormatException("Type 1 charstring takes too long.");
            int b0 = code[i];
            if (b0 >= 32)
            {
                if (b0 == 255)
                {
                    Check(code, i + 1, 4);
                    Push((int)U32(code, i + 1));
                    i += 5;
                }
                else if (b0 <= 246)
                {
                    Push(b0 - 139);
                    i++;
                }
                else
                {
                    Check(code, i + 1, 1);
                    Push(b0 <= 250 ? (b0 - 247) * 256 + code[i + 1] + 108 : -(b0 - 251) * 256 - code[i + 1] - 108);
                    i += 2;
                }

                continue;
            }

            i++;
            int op = b0;
            if (b0 == 12)
            {
                op = 1200 + U8(code, i);
                i++;
            }

            if (!_widthSeen && op is not (13 or 1207 or 10 or 11 or 1216 or 1217 or 1212))
            {
                throw new FontFormatException("Type 1 charstring draws before hsbw or sbw.");
            }

            switch (op)
            {
                case 13: // hsbw
                    Need(2);
                    SetWidth(_stack[0], 0, _stack[1], 0);
                    break;
                case 1207: // sbw
                    Need(4);
                    SetWidth(_stack[0], _stack[1], _stack[2], _stack[3]);
                    break;
                case 1: // hstem
                    Need(2);
                    AddStem(new Stem(true, _stack[0] + _sby, _stack[1]));
                    break;
                case 3: // vstem
                    Need(2);
                    AddStem(new Stem(false, _stack[0] + _sbx, _stack[1]));
                    break;
                case 1202 or 1201: // hstem3, vstem3: three stems and a counter group, glyph-wide as in FreeType and Type 2
                {
                    Need(6);
                    bool horizontal = op == 1202;
                    double offset = horizontal ? _sby : _sbx;
                    var group = new List<Stem>();
                    for (int k = 0; k < 6; k += 2)
                    {
                        var stem = new Stem(horizontal, _stack[k] + offset, _stack[k + 1]);
                        AddStem(stem);
                        group.Add(stem);
                    }

                    _outline.Counters.Add(group);
                    break;
                }

                case 21: // rmoveto
                    Need(2);
                    MoveBy(_stack[0], _stack[1]);
                    break;
                case 22: // hmoveto
                    Need(1);
                    MoveBy(_stack[0], 0);
                    break;
                case 4: // vmoveto
                    Need(1);
                    MoveBy(0, _stack[0]);
                    break;
                case 5: // rlineto
                    Need(2);
                    LineBy(_stack[0], _stack[1]);
                    break;
                case 6: // hlineto
                    Need(1);
                    LineBy(_stack[0], 0);
                    break;
                case 7: // vlineto
                    Need(1);
                    LineBy(0, _stack[0]);
                    break;
                case 8: // rrcurveto
                    Need(6);
                    CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    break;
                case 30: // vhcurveto
                    Need(4);
                    CurveBy(0, _stack[0], _stack[1], _stack[2], _stack[3], 0);
                    break;
                case 31: // hvcurveto
                    Need(4);
                    CurveBy(_stack[0], 0, _stack[1], _stack[2], 0, _stack[3]);
                    break;
                case 9: // closepath: the next drawing starts a new subpath; the current point stays (6.4).
                    if (_flexing) throw new FontFormatException("Type 1 closepath inside flex.");
                    _open = false;
                    break;
                case 10: // callsubr
                {
                    int index = Index(Pop());
                    if (index >= _subrs.Length) throw new FontFormatException("Type 1 subroutine number is out of range.");
                    if (!Execute(_subrs[index], depth + 1)) return false;
                    continue;
                }

                case 11: // return
                    if (depth == 0) throw new FontFormatException("Type 1 return outside a subroutine.");
                    return true;
                case 14: // endchar
                    if (_flexing) throw new FontFormatException("Type 1 glyph ends inside flex.");
                    _ended = true;
                    return false;
                case 1206: // seac
                {
                    Need(5);
                    if (_drawn || _outline.CurrentHints.Count > 0 || _outline.Hints.Count > 1) throw new FontFormatException("Type 1 accented character also draws or hints.");
                    if (_sby != 0) throw new FontFormatException("Type 1 accented character has a vertical side bearing.");
                    double asb = _stack[0], adx = _stack[1], ady = _stack[2];

                    // The accent's origin lands at adx + sbx - asb from the base's (FreeType, fontTools).
                    _outline.Seac = (Code(_stack[3]), Code(_stack[4]), adx + _sbx - asb, ady);
                    _ended = true;
                    return false;
                }

                case 1212: // div
                {
                    double b = Pop(), a = Pop();
                    if (b == 0) throw new FontFormatException("Type 1 division by zero.");
                    Push(Divide(a, b));
                    continue;
                }

                case 1216: // callothersubr
                    OtherSubr();
                    continue;
                case 1217: // pop
                    if (_results.Count == 0) throw new FontFormatException("Type 1 pop without an OtherSubr result.");
                    Push(_results.Pop());
                    continue;
                case 1233: // setcurrentpoint: only after flex, where it names the point already reached.
                    Need(2);
                    if (_stack[0] != _x || _stack[1] != _y) throw new FontFormatException("Type 1 setcurrentpoint moves the current point.");
                    break;
                case 1200:
                    throw new FontFormatException("Type 1 dotsection has no CFF equivalent.");
                default:
                    throw new FontFormatException("Type 1 charstring uses an undefined operator.");
            }

            _stack.Clear();
        }

        return true;
    }

    private void SetWidth(double sbx, double sby, double wx, double wy)
    {
        if (_widthSeen) throw new FontFormatException("Type 1 charstring sets its width twice.");
        if (wy != 0) throw new FontFormatException("Type 1 glyph has a vertical advance.");
        _widthSeen = true;
        _sbx = sbx;
        _sby = sby;
        _x = sbx;
        _y = sby;
        _outline.Width = wx;
    }

    private void AddStem(Stem stem)
    {
        if (_flexing) throw new FontFormatException("Type 1 hint inside flex.");
        _outline.CurrentHints.Add(stem);
    }

    private void OtherSubr()
    {
        int number = Index(Pop());
        int count = Index(Pop());
        if (count > _stack.Count) throw new FontFormatException("Type 1 OtherSubr has too few arguments.");
        double[] args = [.. _stack.Skip(_stack.Count - count)];
        _stack.RemoveRange(_stack.Count - count, count);
        switch (number)
        {
            case 1 when count == 0:
                // Flex starts: the current point opens a subpath if none is open.
                if (_flexing) throw new FontFormatException("Type 1 flex starts twice.");
                EnsureOpen();
                _flexing = true;
                _flex.Clear();
                break;
            case 2 when count == 0:
                if (!_flexing || _flex.Count >= 7) throw new FontFormatException("Type 1 flex point outside flex.");
                _flex.Add((_x, _y));
                break;
            case 0 when count == 3:
            {
                if (!_flexing || _flex.Count != 7) throw new FontFormatException("Type 1 flex ends without its seven points.");
                _flexing = false;

                // The first point is the reference point; six control points make the two curves.
                var points = new double[12];
                for (int k = 1; k < 7; k++)
                {
                    points[2 * (k - 1)] = _flex[k].X;
                    points[2 * (k - 1) + 1] = _flex[k].Y;
                }

                if (args[1] != points[10] || args[2] != points[11]) throw new FontFormatException("Type 1 flex ends away from its last point.");
                _outline.Draw(new PathElement('F', points, args[0]));
                _drawn = true;
                _results.Push(_y);
                _results.Push(_x);
                break;
            }

            case 3 when count == 1:
                // Hint replacement: the stems that follow form a new set from this point of the path.
                if (_flexing) throw new FontFormatException("Type 1 hint replacement inside flex.");
                _outline.ReplaceHints();
                _results.Push(args[0]);
                break;
            default:
                throw new FontFormatException("Type 1 OtherSubr has no CFF equivalent.");
        }
    }

    private void MoveBy(double dx, double dy)
    {
        _x += dx;
        _y += dy;
        if (_flexing) return;
        _outline.Move(_x, _y);
        _open = true;
    }

    private void LineBy(double dx, double dy)
    {
        if (_flexing) throw new FontFormatException("Type 1 line inside flex.");
        EnsureOpen();
        _x += dx;
        _y += dy;
        _outline.Draw(new PathElement('L', [_x, _y]));
        _drawn = true;
    }

    private void CurveBy(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        if (_flexing) throw new FontFormatException("Type 1 curve inside flex.");
        EnsureOpen();
        double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
        _x = x2 + dx3;
        _y = y2 + dy3;
        _outline.Draw(new PathElement('C', [x1, y1, x2, y2, _x, _y]));
        _drawn = true;
    }

    /// <summary>Drawing with no subpath open (at the start, or after closepath) starts one at the current point.</summary>
    private void EnsureOpen()
    {
        if (_open) return;
        _outline.Move(_x, _y);
        _open = true;
    }

    private void Need(int count)
    {
        if (_stack.Count < count) throw new FontFormatException("Type 1 operator has too few operands.");
    }

    private void Push(double v)
    {
        if (_stack.Count >= MaxStack) throw new FontFormatException("Type 1 operand stack overflows.");
        _stack.Add(v);
    }

    private double Pop()
    {
        if (_stack.Count == 0) throw new FontFormatException("Type 1 operand stack underflows.");
        double v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    /// <summary>
    /// Type 1 division as readers compute it: in 16.16 fixed point, rounded to nearest with ties
    /// away from zero (FreeType's FT_DivFix; pdf.js rounds the same way when it writes the value
    /// out). A CFF program can hold the result exactly.
    /// </summary>
    internal static double Divide(double a, double b)
    {
        decimal q = (decimal)a * 65536m / (decimal)b;
        if (Math.Abs(q) > int.MaxValue) throw new FontFormatException("Type 1 division overflows.");
        return (double)decimal.Round(q, 0, MidpointRounding.AwayFromZero) / 65536;
    }

    private static int Index(double v) => v >= 0 && v == Math.Floor(v) && v < int.MaxValue ? (int)v : throw new FontFormatException("Type 1 index is invalid.");

    private static int Code(double v) => v is >= 0 and <= 255 && v == Math.Floor(v) ? (int)v : throw new FontFormatException("Type 1 accent code is invalid.");
}
