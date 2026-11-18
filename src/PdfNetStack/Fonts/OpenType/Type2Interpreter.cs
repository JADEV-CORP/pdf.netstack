using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Runs a Type 2 charstring (Adobe Technical Note 5177) the way a rasterizer does, to the
/// <see cref="GlyphOutline"/> it draws: path, stems, hint and counter masks, width, and the parts
/// of an accented character. Also records which subroutines the glyph calls, so a subset knows
/// what it must keep. Anything the specification does not define, or a reader could take two
/// ways, throws <see cref="FontFormatException"/>.
/// </summary>
internal sealed class Type2Interpreter
{
    private const int MaxStack = 48, MaxDepth = 10, MaxSteps = 1 << 20, MaxStems = 96;

    private readonly IReadOnlyList<byte[]> _global;
    private readonly IReadOnlyList<byte[]> _local;
    private readonly double _defaultWidth, _nominalWidth;
    private readonly List<double> _stack = new(MaxStack);
    private readonly double[] _transient = new double[32];
    private readonly List<Stem> _stems = [];
    private readonly GlyphOutline _outline = new();
    private int _hstems;
    private bool _widthSeen, _maskSeen, _pathStarted, _ended;
    private double _x, _y;
    private int _steps;

    private Type2Interpreter(IReadOnlyList<byte[]> global, IReadOnlyList<byte[]> local, double defaultWidth, double nominalWidth)
    {
        _global = global;
        _local = local;
        _defaultWidth = defaultWidth;
        _nominalWidth = nominalWidth;
    }

    /// <summary>Indices (before bias) of the local subroutines the glyph called.</summary>
    public HashSet<int> LocalSubrs { get; } = [];

    /// <summary>Indices (before bias) of the global subroutines the glyph called.</summary>
    public HashSet<int> GlobalSubrs { get; } = [];

    public GlyphOutline Outline => _outline;

    /// <summary>Operators and operands the glyph took, for a budget over a whole font.</summary>
    public int Steps => _steps;

    /// <summary>Work a whole font may take, glyphs together, so many costly glyphs cannot add up past it.</summary>
    public const long MaxStepsPerFont = 1L << 26;

    /// <summary>Interprets one glyph.</summary>
    /// <exception cref="FontFormatException">The charstring is malformed or uses an undefined operator.</exception>
    public static Type2Interpreter Run(byte[] charstring, IReadOnlyList<byte[]> global, IReadOnlyList<byte[]> local, double defaultWidth, double nominalWidth)
    {
        var interpreter = new Type2Interpreter(global, local, defaultWidth, nominalWidth);
        interpreter.Execute(charstring, 0);
        if (!interpreter._ended) throw new FontFormatException("Type 2 charstring does not end with endchar.");
        if (!interpreter._widthSeen) interpreter._outline.Width = defaultWidth;
        interpreter._outline.Finish();
        return interpreter;
    }

    /// <summary>The bias added to a subroutine number (Technical Note 5177, 4.7).</summary>
    public static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <returns>False once endchar has run.</returns>
    private bool Execute(byte[] code, int depth)
    {
        if (depth > MaxDepth) throw new FontFormatException("Type 2 subroutines nest too deeply.");
        for (int i = 0; i < code.Length;)
        {
            if (++_steps > MaxSteps) throw new FontFormatException("Type 2 charstring takes too long.");
            int b0 = code[i];
            if (b0 == 255)
            {
                Check(code, i + 1, 4);
                Push((int)U32(code, i + 1) / 65536.0);
                i += 5;
                continue;
            }

            if (b0 >= 32 || b0 == 28)
            {
                Push(ReadNumber(code, ref i));
                continue;
            }

            i++;
            int op = b0;
            if (b0 == 12)
            {
                op = 1200 + U8(code, i);
                i++;
            }

            switch (op)
            {
                case 1 or 18:
                    Stems(horizontal: true);
                    break;
                case 3 or 23:
                    Stems(horizontal: false);
                    break;
                case 19 or 20:
                {
                    // Values left on the stack before a mask are vertical stems.
                    if (_stack.Count > 0) Stems(horizontal: false);
                    else TakeWidth(0);
                    int bytes = (_stems.Count + 7) / 8;
                    Check(code, i, bytes);
                    var selected = new List<Stem>();
                    for (int s = 0; s < _stems.Count; s++)
                    {
                        if ((code[i + s / 8] & (0x80 >> (s % 8))) != 0) selected.Add(_stems[s]);
                    }

                    i += bytes;
                    if (op == 20)
                    {
                        _outline.Counters.Add(selected);
                    }
                    else
                    {
                        _outline.ReplaceHints();
                        _outline.CurrentHints.AddRange(selected);
                        _maskSeen = true;
                    }

                    break;
                }

                case 21:
                    TakeWidth(2);
                    Exactly(2);
                    MoveBy(_stack[0], _stack[1]);
                    _stack.Clear();
                    break;
                case 22:
                    TakeWidth(1);
                    Exactly(1);
                    MoveBy(_stack[0], 0);
                    _stack.Clear();
                    break;
                case 4:
                    TakeWidth(1);
                    Exactly(1);
                    MoveBy(0, _stack[0]);
                    _stack.Clear();
                    break;
                case 5:
                    Pairs(2);
                    for (int k = 0; k < _stack.Count; k += 2) LineBy(_stack[k], _stack[k + 1]);
                    _stack.Clear();
                    break;
                case 6 or 7:
                {
                    if (_stack.Count == 0) throw new FontFormatException("Type 2 line has no operands.");
                    bool horizontal = op == 6;
                    foreach (double d in _stack)
                    {
                        if (horizontal) LineBy(d, 0);
                        else LineBy(0, d);
                        horizontal = !horizontal;
                    }

                    _stack.Clear();
                    break;
                }

                case 8:
                    Pairs(6);
                    for (int k = 0; k < _stack.Count; k += 6) CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _stack.Clear();
                    break;
                case 24:
                {
                    if (_stack.Count < 8 || (_stack.Count - 2) % 6 != 0) throw new FontFormatException("Type 2 rcurveline has a wrong operand count.");
                    int k = 0;
                    for (; k + 6 <= _stack.Count - 2; k += 6) CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    LineBy(_stack[k], _stack[k + 1]);
                    _stack.Clear();
                    break;
                }

                case 25:
                {
                    if (_stack.Count < 8 || (_stack.Count - 6) % 2 != 0) throw new FontFormatException("Type 2 rlinecurve has a wrong operand count.");
                    int k = 0;
                    for (; k + 2 <= _stack.Count - 6; k += 2) LineBy(_stack[k], _stack[k + 1]);
                    CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _stack.Clear();
                    break;
                }

                case 26 or 27:
                {
                    // vvcurveto: dx1? {dya dxb dyb dyc}+; hhcurveto: dy1? {dxa dxb dyb dxc}+
                    int n = _stack.Count, k = n % 4;
                    if (n < 4 || k > 1) throw new FontFormatException("Type 2 curve has a wrong operand count.");
                    double first = k == 1 ? _stack[0] : 0;
                    for (; k < n; k += 4)
                    {
                        if (op == 26) CurveBy(first, _stack[k], _stack[k + 1], _stack[k + 2], 0, _stack[k + 3]);
                        else CurveBy(_stack[k], first, _stack[k + 1], _stack[k + 2], _stack[k + 3], 0);
                        first = 0;
                    }

                    _stack.Clear();
                    break;
                }

                case 30 or 31:
                {
                    int n = _stack.Count, k = 0;
                    if (n < 4 || (n % 4 != 0 && n % 4 != 1)) throw new FontFormatException("Type 2 curve has a wrong operand count.");
                    bool horizontal = op == 31;
                    while (k + 4 <= n)
                    {
                        bool last = n - k == 5;
                        double extra = last ? _stack[k + 4] : 0;
                        if (horizontal) CurveBy(_stack[k], 0, _stack[k + 1], _stack[k + 2], extra, _stack[k + 3]);
                        else CurveBy(0, _stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], extra);
                        k += last ? 5 : 4;
                        horizontal = !horizontal;
                    }

                    _stack.Clear();
                    break;
                }

                case 1235:
                    Need(13);
                    if (_stack.Count != 13) throw new FontFormatException("Type 2 flex has a wrong operand count.");
                    FlexBy([.. _stack.Take(12)], _stack[12]);
                    _stack.Clear();
                    break;
                case 1234:
                {
                    if (_stack.Count != 7) throw new FontFormatException("Type 2 hflex has a wrong operand count.");
                    List<double> a = _stack;
                    FlexBy([a[0], 0, a[1], a[2], a[3], 0, a[4], 0, a[5], -a[2], a[6], 0], 50);
                    _stack.Clear();
                    break;
                }

                case 1236:
                {
                    if (_stack.Count != 9) throw new FontFormatException("Type 2 hflex1 has a wrong operand count.");
                    List<double> a = _stack;
                    FlexBy([a[0], a[1], a[2], a[3], a[4], 0, a[5], 0, a[6], a[7], a[8], -(a[1] + a[3] + a[7])], 50);
                    _stack.Clear();
                    break;
                }

                case 1237:
                {
                    if (_stack.Count != 11) throw new FontFormatException("Type 2 flex1 has a wrong operand count.");
                    List<double> a = _stack;
                    double dx = a[0] + a[2] + a[4] + a[6] + a[8], dy = a[1] + a[3] + a[5] + a[7] + a[9];
                    double dx6 = Math.Abs(dx) > Math.Abs(dy) ? a[10] : -dx, dy6 = Math.Abs(dx) > Math.Abs(dy) ? -dy : a[10];
                    FlexBy([a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], dx6, dy6], 50);
                    _stack.Clear();
                    break;
                }

                case 10 or 29:
                {
                    IReadOnlyList<byte[]> subrs = op == 10 ? _local : _global;
                    int index = (int)Pop() + Bias(subrs.Count);
                    if ((uint)index >= (uint)subrs.Count) throw new FontFormatException("Type 2 subroutine number is out of range.");
                    (op == 10 ? LocalSubrs : GlobalSubrs).Add(index);
                    if (!Execute(subrs[index], depth + 1)) return false;
                    break;
                }

                case 11:
                    if (depth == 0) throw new FontFormatException("Type 2 return outside a subroutine.");
                    return true;
                case 14:
                    EndChar();
                    return false;
                case 1200:
                    // dotsection: deprecated in Type 2, a no-op.
                    _stack.Clear();
                    break;
                default:
                    Arithmetic(op);
                    break;
            }
        }

        return true;
    }

    private void EndChar()
    {
        int n = _stack.Count;
        TakeWidth(n is 1 or 5 ? n - 1 : n);
        if (_stack.Count == 4)
        {
            // Accented character: adx ady bchar achar (5177, Appendix C).
            if (_pathStarted) throw new FontFormatException("Type 2 accented character also draws a path.");
            int bchar = Code(_stack[2]), achar = Code(_stack[3]);
            _outline.Seac = (bchar, achar, _stack[0], _stack[1]);
        }
        else if (_stack.Count != 0)
        {
            throw new FontFormatException("Type 2 endchar has a wrong operand count.");
        }

        _stack.Clear();
        _ended = true;

        static int Code(double v) => v is >= 0 and <= 255 && v == Math.Floor(v) ? (int)v : throw new FontFormatException("Type 2 accent code is invalid.");
    }

    private void Arithmetic(int op)
    {
        switch (op)
        {
            case 1203: Binary((a, b) => a != 0 && b != 0 ? 1 : 0); break;
            case 1204: Binary((a, b) => a != 0 || b != 0 ? 1 : 0); break;
            case 1205: Push(Pop() == 0 ? 1 : 0); break;
            case 1209: Push(Math.Abs(Pop())); break;
            case 1210: Binary((a, b) => a + b); break;
            case 1211: Binary((a, b) => a - b); break;
            case 1212: Binary((a, b) => b == 0 ? throw new FontFormatException("Type 2 division by zero.") : a / b); break;
            case 1214: Push(-Pop()); break;
            case 1215: Binary((a, b) => a == b ? 1 : 0); break;
            case 1218: Pop(); break;
            case 1220:
            {
                int index = Index(Pop());
                _transient[index] = Pop();
                break;
            }

            case 1221: Push(_transient[Index(Pop())]); break;
            case 1222:
            {
                double v2 = Pop(), v1 = Pop(), s2 = Pop(), s1 = Pop();
                Push(v1 <= v2 ? s1 : s2);
                break;
            }

            case 1224: Binary((a, b) => a * b); break;
            case 1226:
            {
                double v = Pop();
                Push(v < 0 ? throw new FontFormatException("Type 2 square root of a negative number.") : Math.Sqrt(v));
                break;
            }

            case 1227:
            {
                double v = Pop();
                Push(v);
                Push(v);
                break;
            }

            case 1228:
            {
                double b = Pop(), a = Pop();
                Push(b);
                Push(a);
                break;
            }

            case 1229:
            {
                int i = (int)Pop();
                if (i < 0) i = 0;
                if (i >= _stack.Count) throw new FontFormatException("Type 2 index is out of range.");
                Push(_stack[_stack.Count - 1 - i]);
                break;
            }

            case 1230:
            {
                int j = (int)Pop(), n = (int)Pop();
                if (n < 0 || n > _stack.Count) throw new FontFormatException("Type 2 roll is out of range.");
                if (n == 0) break;
                var top = _stack.GetRange(_stack.Count - n, n);
                for (int k = 0; k < n; k++) _stack[_stack.Count - n + ((k + j) % n + n) % n] = top[k];
                break;
            }

            default:
                // random (12 23) would make the glyph differ from one rendering to the next.
                throw new FontFormatException("Type 2 charstring uses an undefined or unsupported operator.");
        }

        void Binary(Func<double, double, double> f)
        {
            double b = Pop(), a = Pop();
            Push(f(a, b));
        }

        static int Index(double v) => v is >= 0 and < 32 ? (int)v : throw new FontFormatException("Type 2 transient array index is out of range.");
    }

    private void Stems(bool horizontal)
    {
        if (_pathStarted) throw new FontFormatException("Type 2 stems follow the start of the path.");
        TakeWidth(_stack.Count % 2 == 1 ? _stack.Count - 1 : _stack.Count);
        if (_stack.Count % 2 != 0) throw new FontFormatException("Type 2 stem has an odd operand count.");
        if (horizontal && _stems.Count != _hstems) throw new FontFormatException("Type 2 horizontal stems follow vertical ones.");
        double position = 0;
        for (int k = 0; k < _stack.Count; k += 2)
        {
            double edge = position + _stack[k];
            double width = _stack[k + 1];
            position = edge + width;
            _stems.Add(new Stem(horizontal, edge, width));
            if (horizontal) _hstems++;
        }

        if (_stems.Count > MaxStems) throw new FontFormatException("Type 2 charstring has too many stems.");
        _stack.Clear();
    }

    /// <summary>The first stack clearing operator may carry the width before <paramref name="expected"/> operands.</summary>
    private void TakeWidth(int expected)
    {
        if (_widthSeen) return;
        _widthSeen = true;
        if (_stack.Count > expected)
        {
            _outline.Width = _nominalWidth + _stack[0];
            _stack.RemoveAt(0);
        }
        else
        {
            _outline.Width = _defaultWidth;
        }
    }

    private void StartPath()
    {
        if (_pathStarted) return;
        _pathStarted = true;

        // Without a hint mask before the path, every stem is in force.
        if (!_maskSeen) _outline.CurrentHints.AddRange(_stems);
    }

    private void MoveBy(double dx, double dy)
    {
        StartPath();
        _x += dx;
        _y += dy;
        _outline.Move(_x, _y);
    }

    private void LineBy(double dx, double dy)
    {
        RequireMove();
        _x += dx;
        _y += dy;
        _outline.Draw(new PathElement('L', [_x, _y]));
    }

    private void CurveBy(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        RequireMove();
        double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
        _x = x2 + dx3;
        _y = y2 + dy3;
        _outline.Draw(new PathElement('C', [x1, y1, x2, y2, _x, _y]));
    }

    private void FlexBy(double[] d, double depth)
    {
        RequireMove();
        var points = new double[12];
        double x = _x, y = _y;
        for (int k = 0; k < 12; k += 2)
        {
            x += d[k];
            y += d[k + 1];
            points[k] = x;
            points[k + 1] = y;
        }

        _x = x;
        _y = y;
        _outline.Draw(new PathElement('F', points, depth));
    }

    private void RequireMove()
    {
        // Type 2 draws only after a move (5177, 4.1).
        if (!_pathStarted || _outline.Path.Count == 0) throw new FontFormatException("Type 2 path does not start with a move.");
    }

    private void Need(int count)
    {
        if (_stack.Count < count) throw new FontFormatException("Type 2 operator has too few operands.");
    }

    private void Exactly(int count)
    {
        if (_stack.Count != count) throw new FontFormatException("Type 2 operator has a wrong operand count.");
    }

    private void Pairs(int size)
    {
        if (_stack.Count == 0 || _stack.Count % size != 0) throw new FontFormatException("Type 2 operator has a wrong operand count.");
    }

    private void Push(double v)
    {
        if (_stack.Count >= MaxStack) throw new FontFormatException("Type 2 operand stack overflows.");
        _stack.Add(v);
    }

    private double Pop()
    {
        if (_stack.Count == 0) throw new FontFormatException("Type 2 operand stack underflows.");
        double v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    private static double ReadNumber(byte[] code, ref int i)
    {
        int b0 = code[i];
        switch (b0)
        {
            case 28:
                i += 3;
                return I16(code, i - 2);
            case <= 246:
                i++;
                return b0 - 139;
            case <= 250:
                i += 2;
                return (b0 - 247) * 256 + U8(code, i - 1) + 108;
            default:
                i += 2;
                return -(b0 - 251) * 256 - U8(code, i - 1) - 108;
        }
    }
}
