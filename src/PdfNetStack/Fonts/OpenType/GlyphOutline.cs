namespace PdfNetStack.Fonts.OpenType;

/// <summary>One stem hint in glyph space: its first edge and its width (negative for ghost stems).</summary>
internal readonly record struct Stem(bool Horizontal, double Edge, double Width) : IComparable<Stem>
{
    public int CompareTo(Stem other)
    {
        int c = Horizontal.CompareTo(other.Horizontal);
        if (c != 0) return c;
        c = Edge.CompareTo(other.Edge);
        return c != 0 ? c : Width.CompareTo(other.Width);
    }
}

/// <summary>A path element in absolute glyph space coordinates.</summary>
/// <param name="Kind">'M' move, 'L' line, 'C' curve, 'F' flex (two curves with a depth).</param>
/// <param name="Points">x, y pairs: one for M and L, three for C, six for F.</param>
/// <param name="Depth">The flex depth in hundredths of a device pixel; 0 for other elements.</param>
internal sealed record PathElement(char Kind, double[] Points, double Depth = 0)
{
    public bool SameAs(PathElement other) => Kind == other.Kind && Depth.Equals(other.Depth) && Points.AsSpan().SequenceEqual(other.Points);
}

/// <summary>
/// A glyph as a rasterizer sees it, in a form that Type 1 and Type 2 charstrings both reduce to:
/// the advance width, the path in absolute coordinates, the hint sets in force from each point
/// of the path, the counter groups, or for an accented character its two parts. Moves with
/// nothing drawn between them collapse into the last one, and closepath is implied, since both
/// formats close a subpath at the next move.
/// </summary>
internal sealed class GlyphOutline
{
    public double Width { get; set; }

    public List<PathElement> Path { get; } = [];

    /// <summary>The stems in force from a path index on; the first entry starts at 0.</summary>
    public List<(int Start, List<Stem> Stems)> Hints { get; } = [(0, [])];

    /// <summary>Groups of stems whose counters are controlled together (Type 1 stem3, Type 2 cntrmask).</summary>
    public List<List<Stem>> Counters { get; } = [];

    /// <summary>An accented character: StandardEncoding codes of base and accent, and the accent's offset.</summary>
    public (int Base, int Accent, double Dx, double Dy)? Seac { get; set; }

    /// <summary>True once something other than a move is in the path.</summary>
    private bool _open;

    public void Move(double x, double y)
    {
        // A move right after a move only shifts the start of the next subpath. Hints replaced
        // between the two moves apply from that start on, as they would have from the second.
        if (Path.Count > 0 && Path[^1].Kind == 'M' && !_open)
        {
            Path[^1] = new PathElement('M', [x, y]);
            if (Hints.Count > 1 && Hints[^1].Start == Path.Count)
            {
                if (Hints[^2].Start == Path.Count - 1) Hints.RemoveAt(Hints.Count - 2);
                Hints[^1] = (Path.Count - 1, Hints[^1].Stems);
            }
        }
        else
        {
            Path.Add(new PathElement('M', [x, y]));
        }

        _open = false;
    }

    public void Draw(PathElement element)
    {
        Path.Add(element);
        _open = true;
    }

    /// <summary>The stems of the hint set being declared.</summary>
    public List<Stem> CurrentHints => Hints[^1].Stems;

    /// <summary>Starts a new hint set at the current point of the path.</summary>
    public void ReplaceHints()
    {
        if (Hints[^1].Start == Path.Count) Hints[^1].Stems.Clear();
        else Hints.Add((Path.Count, []));
    }

    /// <summary>Drops a move the path ends with, which draws nothing.</summary>
    public void Finish()
    {
        if (Path.Count > 0 && Path[^1].Kind == 'M')
        {
            Path.RemoveAt(Path.Count - 1);
        }

        // Hints of a glyph that draws nothing act on nothing.
        if (Path.Count == 0)
        {
            Hints.Clear();
            Hints.Add((0, []));
            Counters.Clear();
            return;
        }

        // Hint sets that start past the end of the path, or repeat the set before them, change nothing.
        for (int i = Hints.Count - 1; i > 0; i--)
        {
            if (Hints[i].Start >= Path.Count || SameStems(Hints[i].Stems, Hints[i - 1].Stems)) Hints.RemoveAt(i);
        }

        // A counter group declared again (stem3 repeated in each hint set) controls nothing more.
        for (int i = Counters.Count - 1; i > 0; i--)
        {
            for (int k = 0; k < i; k++)
            {
                if (!SameStems(Counters[i], Counters[k])) continue;
                Counters.RemoveAt(i);
                break;
            }
        }
    }

    private static bool SameStems(List<Stem> a, List<Stem> b)
    {
        if (a.Count != b.Count) return false;
        var x = a.Distinct().OrderBy(s => s).ToList();
        var y = b.Distinct().OrderBy(s => s).ToList();
        return x.SequenceEqual(y);
    }

    /// <summary>Null when both glyphs render alike, hinted or not; otherwise what differs.</summary>
    public static string? FindDifference(GlyphOutline a, GlyphOutline b)
    {
        if (!a.Width.Equals(b.Width)) return "advance width differs";
        if (a.Seac != b.Seac) return "accented character differs";
        if (a.Path.Count != b.Path.Count) return "path length differs";
        for (int i = 0; i < a.Path.Count; i++)
        {
            if (!a.Path[i].SameAs(b.Path[i])) return $"path element {i} differs";
        }

        if (a.Hints.Count != b.Hints.Count) return "hint replacement differs";
        for (int i = 0; i < a.Hints.Count; i++)
        {
            if (a.Hints[i].Start != b.Hints[i].Start || !SameStems(a.Hints[i].Stems, b.Hints[i].Stems)) return $"hint set {i} differs";
        }

        if (a.Counters.Count != b.Counters.Count) return "counter hints differ";
        foreach (List<Stem> group in a.Counters)
        {
            if (!b.Counters.Exists(other => SameStems(group, other))) return "counter hints differ";
        }

        return null;
    }
}
