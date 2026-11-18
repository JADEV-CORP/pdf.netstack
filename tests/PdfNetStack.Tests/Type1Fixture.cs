using System.Globalization;
using System.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// Writes Type 1 font programs for tests, as a PDF embeds them (FontFile: clear text, then the
/// eexec section), following the Adobe Type 1 Font Format: charstrings are encoded and encrypted
/// here, independently of the library's reader. Glyphs are given as charstring tokens: integers,
/// and operator names such as "hsbw" or "callothersubr".
/// </summary>
internal sealed class Type1Fixture
{
    private static readonly Dictionary<string, int[]> Operators = new(StringComparer.Ordinal)
    {
        ["hstem"] = [1], ["vstem"] = [3], ["vmoveto"] = [4], ["rlineto"] = [5], ["hlineto"] = [6], ["vlineto"] = [7],
        ["rrcurveto"] = [8], ["closepath"] = [9], ["callsubr"] = [10], ["return"] = [11], ["hsbw"] = [13], ["endchar"] = [14],
        ["rmoveto"] = [21], ["hmoveto"] = [22], ["vhcurveto"] = [30], ["hvcurveto"] = [31],
        ["dotsection"] = [12, 0], ["vstem3"] = [12, 1], ["hstem3"] = [12, 2], ["seac"] = [12, 6], ["sbw"] = [12, 7], ["div"] = [12, 12],
        ["callothersubr"] = [12, 16], ["pop"] = [12, 17], ["setcurrentpoint"] = [12, 33],
    };

    public string Name { get; set; } = "TestType1";

    public int PaintType { get; set; }

    /// <summary>Glyph name to charstring tokens, in CharStrings order.</summary>
    public List<(string Name, object[] Tokens)> Glyphs { get; } = [];

    public List<object[]> Subrs { get; } = [];

    /// <summary>Code to glyph name; null keeps StandardEncoding.</summary>
    public Dictionary<int, string>? Encoding { get; set; }

    /// <summary>Private entries as PostScript text, such as "/BlueValues [-20 0 700 720]".</summary>
    public List<string> Private { get; } = ["/BlueValues [-20 0 700 720]", "/BlueScale 0.039625", "/StdHW [50]", "/StdVW [80]", "/StemSnapH [50 60]", "/ForceBold false"];

    /// <summary>Writes the eexec section in hex, as a PFA file would.</summary>
    public bool HexEexec { get; set; }

    /// <summary>The standard flex and hint replacement subroutines 0 to 3 (Type 1 Font Format, chapter 8).</summary>
    public static List<object[]> StandardSubrs() =>
    [
        [3, 0, "callothersubr", "pop", "pop", "setcurrentpoint", "return"],
        [0, 1, "callothersubr", "return"],
        [0, 2, "callothersubr", "return"],
        ["return"],
    ];

    /// <summary>The program, and the lengths of its clear and encrypted parts.</summary>
    public (byte[] Program, int Length1, int Length2) Build()
    {
        var clear = new StringBuilder();
        clear.Append("%!PS-AdobeFont-1.0: ").Append(Name).Append(" 001.000\n");
        clear.Append("11 dict begin\n");
        clear.Append("/FontInfo 3 dict dup begin /FullName (").Append(Name).Append(") readonly def /ItalicAngle 0 def /isFixedPitch false def end readonly def\n");
        clear.Append("/FontName /").Append(Name).Append(" def\n");
        clear.Append("/PaintType ").Append(PaintType).Append(" def\n/FontType 1 def\n");
        clear.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {-50 -250 1050 900} readonly def\n");
        if (Encoding is null)
        {
            clear.Append("/Encoding StandardEncoding def\n");
        }
        else
        {
            clear.Append("/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\n");
            foreach ((int code, string glyph) in Encoding.OrderBy(e => e.Key)) clear.Append("dup ").Append(code).Append(" /").Append(glyph).Append(" put\n");
            clear.Append("readonly def\n");
        }

        clear.Append("currentfile eexec\n");

        var priv = new MemoryStream();
        Put(priv, "dup /Private 12 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n/NP{noaccess put}executeonly def\n");
        foreach (string entry in Private) Put(priv, entry + " def\n");
        Put(priv, "/MinFeature{16 16}def\n/password 5839 def\n/OtherSubrs [ {} {} {} { systemdict /internaldict known not {pop 3} if } ] ND\n");
        Put(priv, $"/Subrs {Subrs.Count} array\n");
        for (int i = 0; i < Subrs.Count; i++)
        {
            byte[] cs = Encrypt(Encode(Subrs[i]), 4330, 4);
            Put(priv, $"dup {i} {cs.Length} RD ");
            priv.Write(cs);
            Put(priv, " NP\n");
        }

        Put(priv, $"ND\n2 index /CharStrings {Glyphs.Count} dict dup begin\n");
        foreach ((string name, object[] tokens) in Glyphs)
        {
            byte[] cs = Encrypt(Encode(tokens), 4330, 4);
            Put(priv, $"/{name} {cs.Length} RD ");
            priv.Write(cs);
            Put(priv, " ND\n");
        }

        Put(priv, "end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        byte[] cipher = Encrypt(priv.ToArray(), 55665, 4);
        if (HexEexec)
        {
            var hex = new StringBuilder();
            for (int i = 0; i < cipher.Length; i++)
            {
                hex.Append(cipher[i].ToString("x2", CultureInfo.InvariantCulture));
                if (i % 32 == 31) hex.Append('\n');
            }

            cipher = System.Text.Encoding.ASCII.GetBytes(hex.ToString());
        }

        byte[] head = Compat.Latin1.GetBytes(clear.ToString());
        return ([.. head, .. cipher], head.Length, cipher.Length);
    }

    /// <summary>A Type 1 charstring from tokens.</summary>
    public static byte[] Encode(object[] tokens)
    {
        var ms = new MemoryStream();
        foreach (object token in tokens)
        {
            if (token is string op)
            {
                foreach (int b in Operators[op]) ms.WriteByte((byte)b);
                continue;
            }

            int v = (int)token;
            switch (v)
            {
                case >= -107 and <= 107:
                    ms.WriteByte((byte)(v + 139));
                    break;
                case >= 108 and <= 1131:
                    ms.WriteByte((byte)(((v - 108) >> 8) + 247));
                    ms.WriteByte((byte)((v - 108) & 0xFF));
                    break;
                case >= -1131 and <= -108:
                    ms.WriteByte((byte)(((-v - 108) >> 8) + 251));
                    ms.WriteByte((byte)((-v - 108) & 0xFF));
                    break;
                default:
                    ms.WriteByte(255);
                    ms.WriteByte((byte)(v >> 24));
                    ms.WriteByte((byte)(v >> 16));
                    ms.WriteByte((byte)(v >> 8));
                    ms.WriteByte((byte)v);
                    break;
            }
        }

        return ms.ToArray();
    }

    /// <summary>Type 1 encryption (chapter 7) after <paramref name="random"/> leading bytes.</summary>
    public static byte[] Encrypt(byte[] plain, ushort key, int random)
    {
        byte[] data = [.. Enumerable.Repeat((byte)0x5A, random), .. plain];
        var cipher = new byte[data.Length];
        ushort r = key;
        for (int i = 0; i < data.Length; i++)
        {
            byte c = (byte)(data[i] ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            cipher[i] = c;
        }

        return cipher;
    }

    private static void Put(Stream s, string text) => s.Write(Compat.Latin1.GetBytes(text));

    /// <summary>
    /// A font exercising what conversion must keep: lines, curves, closepath, hints with a side
    /// bearing, hint replacement, flex, an accented character, stem3 counters, division and sbw.
    /// </summary>
    public static Type1Fixture Rich()
    {
        var f = new Type1Fixture { Encoding = new Dictionary<int, string> { [65] = "A", [66] = "B", [67] = "C", [68] = "D", [69] = "E", [70] = "F", [71] = "Aacute", [72] = "acute", [97] = "A" } };
        f.Subrs.AddRange(StandardSubrs());
        f.Subrs.Add([0, 40, "hstem", "return"]);
        f.Subrs.Add([100, 40, "hstem", 300, 30, "vstem", "return"]);
        f.Subrs.Add([560, 0, "rlineto", "return"]);
        f.Glyphs.Add((".notdef", [0, 500, "hsbw", "endchar"]));
        f.Glyphs.Add(("A", [20, 600, "hsbw", 0, 50, "hstem", 10, 80, "vstem", 0, 0, "rmoveto", 6, "callsubr", 0, 700, "rlineto", -560, 0, "rlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("B", [30, 600, "hsbw", 4, 1, 3, "callothersubr", "pop", "callsubr", 0, 0, "rmoveto", 100, 0, 100, 100, 0, 100, "rrcurveto", "closepath",
            5, 1, 3, "callothersubr", "pop", "callsubr", 200, 0, "rmoveto", 50, "hlineto", 50, "vlineto", -50, "hlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("C", [20, 500, "hsbw", 0, 100, "rmoveto", 1, "callsubr", 50, 0, "rmoveto", 2, "callsubr", 50, -5, "rmoveto", 2, "callsubr",
            50, -5, "rmoveto", 2, "callsubr", 50, 0, "rmoveto", 2, "callsubr", 50, 0, "rmoveto", 2, "callsubr", 50, 5, "rmoveto", 2, "callsubr",
            50, 5, "rmoveto", 2, "callsubr", 50, 370, 100, 0, "callsubr", 0, 100, "rlineto", -350, 0, "rlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("D", [20, 1001, 2, "div", "hsbw", 0, 0, "rmoveto", 1000, 3, "div", 0, "rlineto", 0, 700, "rlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("E", [20, 600, "hsbw", 0, 50, 300, 50, 600, 50, "hstem3", 0, 40, 200, 40, 400, 40, "vstem3", 0, 0, "rmoveto", 500, "hlineto", 650, "vlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("F", [20, 0, 600, 0, "sbw", 0, 0, "rmoveto", 400, 300, "rlineto", "closepath", 10, 10, "rlineto", 5, 5, "rlineto", "endchar"]));
        f.Glyphs.Add(("acute", [50, 300, "hsbw", 0, 600, "rmoveto", 100, 100, "rlineto", -50, 0, "rlineto", "closepath", "endchar"]));
        f.Glyphs.Add(("Aacute", [20, 600, "hsbw", 50, 200, 100, 65, 194, "seac"]));
        return f;
    }

    /// <summary>A PDF showing <paramref name="text"/> with the font as a simple Type 1 font.</summary>
    public static byte[] Document(byte[] program, int length1, int length2, string text, string fontName = "TestType1", string extraFont = "")
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>");
        b.SetStream(4, "", Compat.Latin1.GetBytes($"BT /F1 24 Tf 10 100 Td ({text}) Tj ET"));
        string widths = string.Join(" ", Enumerable.Repeat("600", 256));
        b.Set(5, $"<</Type/Font/Subtype/Type1/BaseFont/{fontName}/FirstChar 0/LastChar 255/Widths[{widths}]/FontDescriptor 6 0 R{extraFont}>>");
        b.Set(6, $"<</Type/FontDescriptor/FontName/{fontName}/Flags 34/FontBBox[-50 -250 1050 900]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile 7 0 R>>");
        b.SetStream(7, $"/Length1 {length1}/Length2 {length2}/Length3 0", program);
        return b.Build();
    }
}
