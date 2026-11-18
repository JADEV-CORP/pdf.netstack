using System.Globalization;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Text.Extraction;

namespace PdfNetStack.Fonts.Type1;

/// <summary>
/// Writes a Type 1 font as a name-keyed CFF font (FontFile3 /Type1C, Adobe Technical Notes 5176
/// and 5177) holding every glyph, with the same names, widths, paths, hints, encoding and Private
/// values, and checks a conversion by interpreting both programs. Subroutines are inlined: Type 1
/// subroutines mix path and hint code that Type 2 cannot share as is, and the CFF data compresses
/// where encrypted Type 1 data cannot.
/// </summary>
internal static class Type1ToCff
{
    private const int MaxStemsPerOperator = 24, MaxStems = 96;

    // Type 2 charstring operators.
    private const int HStem = 1, VStem = 3, RLineTo = 5, RRCurveTo = 8, EndChar = 14, HStemHm = 18, HintMask = 19, CntrMask = 20, RMoveTo = 21, VStemHm = 23, Flex = 1235;

    // DICT operators.
    private const int OpVersion = 0, OpNotice = 1, OpFullName = 2, OpFamilyName = 3, OpWeight = 4, OpFontBBox = 5, OpUniqueId = 13;
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18, OpDefaultWidth = 20, OpNominalWidth = 21;
    private const int OpCopyright = 1200, OpFixedPitch = 1201, OpItalicAngle = 1202, OpUnderlinePosition = 1203, OpUnderlineThickness = 1204;
    private const int OpPaintType = 1205, OpCharstringType = 1206, OpFontMatrix = 1207;

    /// <summary>Private DICT operators of the Type 1 hinting keys, and whether CFF stores them as deltas.</summary>
    private static readonly Dictionary<string, (int Op, bool Delta)> PrivateOps = new(StringComparer.Ordinal)
    {
        ["BlueValues"] = (6, true),
        ["OtherBlues"] = (7, true),
        ["FamilyBlues"] = (8, true),
        ["FamilyOtherBlues"] = (9, true),
        ["StdHW"] = (10, false),
        ["StdVW"] = (11, false),
        ["BlueScale"] = (1209, false),
        ["BlueShift"] = (1210, false),
        ["BlueFuzz"] = (1211, false),
        ["StemSnapH"] = (1212, true),
        ["StemSnapV"] = (1213, true),
        ["ForceBold"] = (1214, false),
        ["LanguageGroup"] = (1217, false),
        ["ExpansionFactor"] = (1218, false),
    };

    /// <summary>Values a missing Private entry stands for, the same in Type 1 and CFF.</summary>
    private static readonly Dictionary<string, decimal[]> Defaults = new(StringComparer.Ordinal)
    {
        ["BlueScale"] = [0.039625m],
        ["BlueShift"] = [7],
        ["BlueFuzz"] = [1],
        ["ForceBold"] = [0],
        ["LanguageGroup"] = [0],
        ["ExpansionFactor"] = [0.06m],
    };

    private static readonly decimal[] DefaultMatrix = [0.001m, 0, 0, 0.001m, 0, 0];

    /// <summary>The CFF program for a Type 1 font.</summary>
    /// <exception cref="FontFormatException">The font uses something CFF cannot say exactly.</exception>
    public static byte[] Convert(Type1Font font)
    {
        if (font.PaintType != 0) throw new FontFormatException("Stroked Type 1 fonts are not converted.");
        if (font.FontName.Length == 0 || font.FontName.Length > 127) throw new FontFormatException("Type 1 font name is missing or too long.");
        var outlines = new Dictionary<string, GlyphOutline>(StringComparer.Ordinal);
        long budget = Type2Interpreter.MaxStepsPerFont;
        foreach ((string name, byte[] charString) in font.Glyphs) outlines[name] = Type1Interpreter.Run(charString, font.Subrs, ref budget);

        // Accented characters need both parts, found by their StandardEncoding names.
        foreach (GlyphOutline o in outlines.Values)
        {
            if (o.Seac is not { } seac) continue;
            if (FontEncodings.StandardEncoding[seac.Base] is not { } b || !outlines.ContainsKey(b) || FontEncodings.StandardEncoding[seac.Accent] is not { } a || !outlines.ContainsKey(a))
            {
                throw new FontFormatException("Type 1 accented character names a glyph the font lacks.");
            }
        }

        // Glyph order: .notdef, the encoded glyphs by their first code (a CFF encoding numbers
        // glyphs from 1 on), then the rest in the font's order.
        string?[] encoding = BuiltIn(font, outlines);
        var order = new List<string> { ".notdef" };
        var firstCode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int code = 0; code < 256; code++)
        {
            if (encoding[code] is { } n && n != ".notdef" && !firstCode.ContainsKey(n))
            {
                firstCode[n] = code;
                order.Add(n);
            }
        }

        var placed = new HashSet<string>(order, StringComparer.Ordinal);
        foreach ((string name, _) in font.Glyphs)
        {
            if (placed.Add(name)) order.Add(name);
        }

        double defaultWidth = outlines.Values.Select(o => o.Width).Where(w => w == Math.Floor(w) && Math.Abs(w) <= 32767)
            .GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        var charStrings = order.Select(name => Encode(outlines[name], defaultWidth)).ToList();

        var strings = new Strings();
        var charset = new FontWriter(2 * order.Count + 1);
        charset.U8(0);
        for (int g = 1; g < order.Count; g++) charset.U16(strings.Sid(order[g]));

        byte[]? encodingBytes = font.Encoding is null ? null : EncodingBytes(encoding, order, firstCode, strings);

        var privateDict = new FontWriter(128);
        foreach ((string key, (int op, bool delta)) in PrivateOps)
        {
            if (!font.Private.TryGetValue(key, out decimal[]? values)) continue;
            if (key is "StdHW" or "StdVW" && values.Length != 1) throw new FontFormatException("Type 1 standard stem width is not a single number.");
            if (key is "BlueScale" or "BlueShift" or "BlueFuzz" or "ForceBold" or "LanguageGroup" or "ExpansionFactor" && values.Length != 1) throw new FontFormatException("Type 1 Private value is invalid.");
            decimal previous = 0;
            foreach (decimal v in values)
            {
                Number(privateDict, delta ? v - previous : v);
                previous = v;
            }

            Cff2Converter.DictOp(privateDict, op);
        }

        Cff2Converter.DictNumber(privateDict, defaultWidth);
        Cff2Converter.DictOp(privateDict, OpDefaultWidth);
        Cff2Converter.DictNumber(privateDict, 0);
        Cff2Converter.DictOp(privateDict, OpNominalWidth);
        byte[] privateBytes = privateDict.ToArray();

        byte[] names = Cff2Converter.WriteIndex([Compat.Latin1.GetBytes(font.FontName)]);
        byte[] charStringIndex = Cff2Converter.WriteIndex(charStrings);
        byte[] Top(int charsetAt, int encodingAt, int charStringsAt, int privateAt) =>
            TopDict(font, strings, charsetAt, encodingBytes is null ? null : encodingAt, charStringsAt, privateBytes.Length, privateAt);

        // Offsets are 5 byte integers, so the Top DICT has the same size whatever they are. Writing
        // it once first also gives its strings their ids before the String INDEX is made.
        int topSize = Cff2Converter.WriteIndex([Top(0, 0, 0, 0)]).Length;
        byte[] stringIndex = strings.Index();
        int at = 4 + names.Length + topSize + stringIndex.Length + 2;
        int charsetAt = at;
        at += charset.Length;
        int encodingAt = encodingBytes is null ? 0 : at;
        at += encodingBytes?.Length ?? 0;
        int charStringsAt = at;
        at += charStringIndex.Length;
        int privateAt = at;

        var w = new FontWriter(at + privateBytes.Length);
        w.U8(1);
        w.U8(0);
        w.U8(4);
        w.U8(4);
        w.Bytes(names);
        w.Bytes(Cff2Converter.WriteIndex([Top(charsetAt, encodingAt, charStringsAt, privateAt)]));
        w.Bytes(stringIndex);
        w.Bytes([0, 0]);
        w.Bytes(charset.Written);
        if (encodingBytes is not null) w.Bytes(encodingBytes);
        w.Bytes(charStringIndex);
        w.Bytes(privateBytes);
        return w.ToArray();
    }

    /// <summary>The glyph each code names in the font's own encoding, null where that glyph does not exist.</summary>
    private static string?[] BuiltIn(Type1Font font, IReadOnlyDictionary<string, GlyphOutline> glyphs)
    {
        string?[] table = font.Encoding ?? FontEncodings.StandardEncoding;
        var result = new string?[256];
        for (int code = 0; code < 256; code++)
        {
            if (table[code] is { } name && name != ".notdef" && glyphs.ContainsKey(name)) result[code] = name;
        }

        return result;
    }

    /// <summary>A custom CFF encoding: format 0 for each encoded glyph's first code, supplements for the others.</summary>
    private static byte[] EncodingBytes(string?[] encoding, List<string> order, Dictionary<string, int> firstCode, Strings strings)
    {
        var supplements = new List<(int Code, int Sid)>();
        for (int code = 0; code < 256; code++)
        {
            if (encoding[code] is { } name && firstCode[name] != code) supplements.Add((code, strings.Sid(name)));
        }

        var w = new FontWriter(4 + firstCode.Count + 3 * supplements.Count);
        w.U8(supplements.Count > 0 ? 0x80 : 0);
        w.U8(firstCode.Count);
        for (int g = 1; g <= firstCode.Count; g++) w.U8(firstCode[order[g]]);
        if (supplements.Count > 0)
        {
            w.U8(supplements.Count);
            foreach ((int code, int sid) in supplements)
            {
                w.U8(code);
                w.U16(sid);
            }
        }

        return w.ToArray();
    }

    /// <param name="encodingAt">The custom encoding's offset, or null for StandardEncoding.</param>
    private static byte[] TopDict(Type1Font font, Strings strings, int charsetAt, int? encodingAt, int charStringsAt, int privateSize, int privateAt)
    {
        var top = new FontWriter(128);
        foreach ((string key, int op) in new[] { ("version", OpVersion), ("Notice", OpNotice), ("Copyright", OpCopyright), ("FullName", OpFullName), ("FamilyName", OpFamilyName), ("Weight", OpWeight) })
        {
            if (!font.Info.TryGetValue(key, out string? text) || text.Any(c => c > 0x7E)) continue;
            Cff2Converter.DictNumber(top, strings.Sid(text));
            Cff2Converter.DictOp(top, op);
        }

        foreach ((string key, int op) in new[] { ("isFixedPitch", OpFixedPitch), ("ItalicAngle", OpItalicAngle), ("UnderlinePosition", OpUnderlinePosition), ("UnderlineThickness", OpUnderlineThickness) })
        {
            if (!font.InfoNumbers.TryGetValue(key, out decimal v)) continue;
            Number(top, v);
            Cff2Converter.DictOp(top, op);
        }

        if (!font.FontMatrix.SequenceEqual(DefaultMatrix))
        {
            foreach (decimal v in font.FontMatrix) Number(top, v);
            Cff2Converter.DictOp(top, OpFontMatrix);
        }

        if (font.UniqueId is { } id && id == decimal.Truncate(id) && Math.Abs(id) <= int.MaxValue)
        {
            Number(top, id);
            Cff2Converter.DictOp(top, OpUniqueId);
        }

        foreach (decimal v in font.FontBBox) Number(top, v);
        Cff2Converter.DictOp(top, OpFontBBox);
        Cff2Converter.DictOffset(top, charsetAt);
        Cff2Converter.DictOp(top, OpCharset);
        if (encodingAt is { } encoding)
        {
            Cff2Converter.DictOffset(top, encoding);
            Cff2Converter.DictOp(top, OpEncoding);
        }

        Cff2Converter.DictOffset(top, charStringsAt);
        Cff2Converter.DictOp(top, OpCharStrings);
        Cff2Converter.DictOffset(top, privateSize);
        Cff2Converter.DictOffset(top, privateAt);
        Cff2Converter.DictOp(top, OpPrivate);
        return top.ToArray();
    }

    /// <summary>A DICT number written exactly: an integer form, or a real from its decimal digits.</summary>
    private static void Number(FontWriter w, decimal v)
    {
        if (v == decimal.Truncate(v) && Math.Abs(v) <= int.MaxValue)
        {
            Cff2Converter.DictNumber(w, (double)v);
            return;
        }

        string text = v.ToString(CultureInfo.InvariantCulture);
        var nibbles = new List<int>();
        foreach (char c in text) nibbles.Add(c switch { '.' => 0xA, '-' => 0xE, _ => c - '0' });
        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
        w.U8(30);
        for (int k = 0; k < nibbles.Count; k += 2) w.U8(nibbles[k] << 4 | nibbles[k + 1]);
    }

    /// <summary>
    /// The Type 2 charstring of a glyph: width when it differs from the default, every stem
    /// declared once in order, counter masks, a hint mask wherever the hint set changes, then the
    /// path. Every coordinate must be exact in 16.16 fixed point.
    /// </summary>
    private static byte[] Encode(GlyphOutline o, double defaultWidth)
    {
        var w = new FontWriter(64);
        bool widthPending = o.Width != defaultWidth;
        void Num(double v)
        {
            if (Math.Abs(v) >= 32768 || v * 65536 != Math.Round(v * 65536)) throw new FontFormatException("Type 1 value is not exact in CFF 16.16 fixed point: " + v.ToString("R", CultureInfo.InvariantCulture));
            Cff2Converter.CharstringNumber(w, v);
        }

        void Width()
        {
            if (!widthPending) return;
            Num(o.Width);
            widthPending = false;
        }

        void Op(int op)
        {
            if (op >= 1200)
            {
                w.U8(12);
                w.U8(op - 1200);
            }
            else
            {
                w.U8(op);
            }
        }

        if (o.Seac is { } seac)
        {
            Width();
            Num(seac.Dx);
            Num(seac.Dy);
            Num(seac.Base);
            Num(seac.Accent);
            Op(EndChar);
            return w.ToArray();
        }

        var stems = o.Hints.SelectMany(h => h.Stems).Concat(o.Counters.SelectMany(c => c)).Distinct().ToList();
        var hstems = stems.Where(s => s.Horizontal).OrderBy(s => s).ToList();
        var vstems = stems.Where(s => !s.Horizontal).OrderBy(s => s).ToList();
        var all = hstems.Concat(vstems).ToList();
        if (all.Count > MaxStems) throw new FontFormatException("Type 1 glyph has more stems than CFF allows.");
        bool masks = o.Hints.Count > 1 || o.Counters.Count > 0 || o.Hints[0].Stems.Distinct().Count() != all.Count;

        foreach ((List<Stem> list, int plain, int masked) in new[] { (hstems, HStem, HStemHm), (vstems, VStem, VStemHm) })
        {
            for (int k = 0; k < list.Count; k += MaxStemsPerOperator - 1)
            {
                // Each operator starts again from 0, so its first edge is absolute.
                Width();
                double position = 0;
                foreach (Stem s in list.Skip(k).Take(MaxStemsPerOperator - 1))
                {
                    Num(s.Edge - position);
                    Num(s.Width);
                    position = s.Edge + s.Width;
                }

                Op(masks ? masked : plain);
            }
        }

        void Mask(int op, IEnumerable<Stem> selected)
        {
            Width();
            Op(op);
            var bits = new byte[(all.Count + 7) / 8];
            foreach (Stem s in selected)
            {
                int i = all.IndexOf(s);
                bits[i / 8] |= (byte)(0x80 >> (i % 8));
            }

            w.Bytes(bits);
        }

        foreach (List<Stem> group in o.Counters) Mask(CntrMask, group);

        int hint = 0;
        double x = 0, y = 0;
        for (int i = 0; i < o.Path.Count;)
        {
            if (masks && hint < o.Hints.Count && o.Hints[hint].Start == i) Mask(HintMask, o.Hints[hint++].Stems);
            PathElement e = o.Path[i];
            switch (e.Kind)
            {
                case 'M':
                    Width();
                    Num(e.Points[0] - x);
                    Num(e.Points[1] - y);
                    Op(RMoveTo);
                    (x, y) = (e.Points[0], e.Points[1]);
                    i++;
                    break;
                case 'F':
                    for (int k = 0; k < 12; k += 2)
                    {
                        Num(e.Points[k] - x);
                        Num(e.Points[k + 1] - y);
                        (x, y) = (e.Points[k], e.Points[k + 1]);
                    }

                    Num(e.Depth);
                    Op(Flex);
                    i++;
                    break;
                default:
                {
                    // Runs of lines or curves share an operator, up to the 48 operand limit, unless the hints change.
                    char kind = e.Kind;
                    int per = kind == 'L' ? 1 : 3, limit = 48 / (2 * per), n = 0;
                    while (i < o.Path.Count && o.Path[i].Kind == kind && n < limit && (n == 0 || !StartsHints(o, hint, i)))
                    {
                        double[] p = o.Path[i].Points;
                        for (int k = 0; k < p.Length; k += 2)
                        {
                            Num(p[k] - x);
                            Num(p[k + 1] - y);
                            (x, y) = (p[k], p[k + 1]);
                        }

                        n++;
                        i++;
                    }

                    Op(kind == 'L' ? RLineTo : RRCurveTo);
                    break;
                }
            }
        }

        Width();
        Op(EndChar);
        return w.ToArray();

        static bool StartsHints(GlyphOutline o, int hint, int index) => hint < o.Hints.Count && o.Hints[hint].Start == index;
    }

    /// <summary>String ids: the 391 standard strings, then the font's own in order of first use.</summary>
    private sealed class Strings
    {
        private readonly List<string> _custom = [];
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

        public int Sid(string s)
        {
            int standard = Array.IndexOf(CffStandardData.StandardStrings, s);
            if (standard >= 0 && standard < CffStandardData.StandardStringCount) return standard;
            if (_ids.TryGetValue(s, out int id)) return id;
            if (_custom.Count >= 65000) throw new FontFormatException("Type 1 font has too many names for CFF.");
            id = CffStandardData.StandardStringCount + _custom.Count;
            _custom.Add(s);
            _ids[s] = id;
            return id;
        }

        public byte[] Index() => Cff2Converter.WriteIndex(_custom.Select(s => Compat.Latin1.GetBytes(s)).ToList());
    }

    /// <summary>
    /// Null when the CFF program draws and hints every glyph of the Type 1 program the same way:
    /// the same glyph names, and for each the same width, path, hint sets, counters and accent
    /// parts; the same font matrix, bounding box, paint type, built-in encoding and Private hinting
    /// values; and nothing else in its DICTs that could change rendering.
    /// </summary>
    public static string? FindDifference(byte[] type1, byte[] cff)
    {
        Type1Font source;
        try
        {
            source = Type1Font.Parse(type1);
        }
        catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return "Type 1 program cannot be read: " + ex.Message;
        }

        try
        {
            var target = new CffFont(cff);
            if (target.IsCidKeyed) return "CFF program is CID-keyed";
            string?[] names = target.Names()!;
            var gidOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int g = 0; g < names.Length; g++)
            {
                if (names[g] is not { } n || !gidOf.TryAdd(n, g)) return "CFF program names a glyph twice or not at all";
            }

            if (names.Length != source.Glyphs.Count) return "the glyph count differs";
            var outlines = new Dictionary<string, GlyphOutline>(StringComparer.Ordinal);
            long budget = Type2Interpreter.MaxStepsPerFont, cffBudget = Type2Interpreter.MaxStepsPerFont;
            foreach ((string name, byte[] cs) in source.Glyphs)
            {
                if (!gidOf.TryGetValue(name, out int gid)) return $"glyph {name} is missing";
                GlyphOutline a = Type1Interpreter.Run(cs, source.Subrs, ref budget);
                Type2Interpreter run = target.Interpret(gid);
                if ((cffBudget -= run.Steps) < 0) return "CFF program takes too much work to read";
                GlyphOutline b = run.Outline;
                if (GlyphOutline.FindDifference(a, b) is { } d) return $"glyph {name}: {d}";
                outlines[name] = a;
            }

            // Only entries with a known meaning, none of which changes how glyphs are drawn beyond what is compared here.
            foreach (CffFont.DictEntry e in target.TopDict)
            {
                if (e.Op is not (0 or 1 or 2 or 3 or 4 or 5 or 13 or 14 or 15 or 16 or 17 or 18 or 1200 or 1201 or 1202 or 1203 or 1204 or 1205 or 1206 or 1207)) return "CFF Top DICT has an unexpected entry";
            }

            if (!Values(target.TopDict, OpFontMatrix, DefaultMatrix).SequenceEqual(source.FontMatrix)) return "font matrix differs";
            if (!Values(target.TopDict, OpFontBBox, [0, 0, 0, 0]).SequenceEqual(source.FontBBox)) return "font bounding box differs";
            if (!Values(target.TopDict, OpPaintType, [0]).SequenceEqual([source.PaintType])) return "paint type differs";
            if (!Values(target.TopDict, OpCharstringType, [2]).SequenceEqual([2m])) return "charstring type differs";

            string?[] own = BuiltIn(source, outlines);
            string?[] encoded = target.BuiltInEncoding()!;
            for (int code = 0; code < 256; code++)
            {
                string? b = encoded[code] is { } n && n != ".notdef" && gidOf.ContainsKey(n) ? n : null;
                if (own[code] != b) return $"built-in encoding differs at code {code}";
            }

            List<CffFont.DictEntry> priv = target.Private(0).Entries;
            foreach (CffFont.DictEntry e in priv)
            {
                if (e.Op is not (6 or 7 or 8 or 9 or 10 or 11 or 19 or 20 or 21 or 1209 or 1210 or 1211 or 1212 or 1213 or 1214 or 1217 or 1218)) return "CFF Private DICT has an unexpected entry";
            }

            foreach ((string key, (int op, bool delta)) in PrivateOps)
            {
                decimal[] expected = source.Private.TryGetValue(key, out decimal[]? v) ? v : Defaults.TryGetValue(key, out decimal[]? d) ? d : [];
                decimal[] actual = Values(priv, op, Defaults.TryGetValue(key, out decimal[]? dd) ? dd : []);
                if (delta)
                {
                    for (int k = 1; k < actual.Length; k++) actual[k] += actual[k - 1];
                }

                if (!actual.SequenceEqual(expected)) return $"Private value {key} differs";
            }

            return null;
        }
        catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return "CFF program cannot be read: " + ex.Message;
        }
    }

    /// <summary>A DICT entry's operands as exact decimals, or <paramref name="missing"/> when absent.</summary>
    private static decimal[] Values(List<CffFont.DictEntry> dict, int op, decimal[] missing)
    {
        foreach (CffFont.DictEntry e in dict)
        {
            if (e.Op == op) return e.Numbers();
        }

        return (decimal[])missing.Clone();
    }
}
