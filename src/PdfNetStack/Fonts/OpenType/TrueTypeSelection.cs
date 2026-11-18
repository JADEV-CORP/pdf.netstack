using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The ways a reader can pick a glyph of a TrueType program for a simple font's code: every
/// cmap subtable and the post table's glyph names (ISO 32000-1, 9.6.6.4). Readers differ in
/// which of them they try and in what order, so callers take the union.
/// </summary>
internal sealed class TrueTypeSelection
{
    /// <summary>Cmap entries visited at most per lookup; valid subtables stay far below.</summary>
    private const int MaxSegments = 1 << 16;

    private readonly byte[] _cmap;
    private readonly List<(int Platform, int Encoding, int Offset)> _subtables = [];
    private readonly Dictionary<string, List<int>> _names = new(StringComparer.Ordinal);

    /// <exception cref="FontFormatException">A cmap subtable or the post table cannot be read for certain.</exception>
    public TrueTypeSelection(ReadOnlySpan<byte> cmap, ReadOnlySpan<byte> post, int glyphCount)
    {
        _cmap = cmap.ToArray();
        if (_cmap.Length > 0)
        {
            int count = U16(_cmap, 2);
            for (int i = 0; i < count; i++)
            {
                int record = 4 + 8 * i;
                int offset = Offset32(_cmap, record + 4);
                Check(_cmap, offset, 2);
                int format = U16(_cmap, offset);

                // Format 14 holds variation sequences, never reached from a single code.
                if (format == 14) continue;
                if (format is not (0 or 4 or 6 or 10 or 12 or 13)) throw new FontFormatException($"cmap subtable format {format} is not read.");
                _subtables.Add((U16(_cmap, record), U16(_cmap, record + 2), offset));
            }
        }

        ReadNames(post, glyphCount);
    }

    /// <summary>The cmap subtables as (platform, encoding).</summary>
    public IEnumerable<(int Platform, int Encoding)> Subtables => _subtables.Select(s => (s.Platform, s.Encoding));

    /// <summary>The glyph each subtable of the given platform and encoding maps <paramref name="code"/> to; zero entries are left out.</summary>
    public IEnumerable<int> Lookup(int code, int? platform = null, int? encoding = null)
    {
        foreach ((int p, int e, int offset) in _subtables)
        {
            if (platform is not null && p != platform) continue;
            if (encoding is not null && e != encoding) continue;
            int glyph = Find(offset, code);
            if (glyph > 0) yield return glyph;
        }
    }

    /// <summary>The glyphs the post table gives this name.</summary>
    public IReadOnlyList<int> ByName(string name) => _names.TryGetValue(name, out List<int>? glyphs) ? glyphs : [];

    private int Find(int offset, int code)
    {
        ReadOnlySpan<byte> d = _cmap;
        int format = U16(d, offset);
        switch (format)
        {
            case 0:
                return code is >= 0 and < 256 ? U8(d, offset + 6 + code) : 0;
            case 4:
            {
                if (code is < 0 or > 0xFFFF) return 0;
                int segments = U16(d, offset + 6) / 2;
                int ends = offset + 14, starts = ends + 2 * segments + 2, deltas = starts + 2 * segments, ranges = deltas + 2 * segments;
                for (int s = 0; s < segments && s < MaxSegments; s++)
                {
                    int end = U16(d, ends + 2 * s);
                    if (code > end) continue;
                    int start = U16(d, starts + 2 * s);
                    if (code < start) return 0;
                    int delta = I16(d, deltas + 2 * s), range = U16(d, ranges + 2 * s);
                    if (range == 0) return (code + delta) & 0xFFFF;
                    int at = ranges + 2 * s + range + 2 * (code - start);
                    int glyph = U16(d, at);
                    return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                }

                return 0;
            }

            case 6:
            {
                int first = U16(d, offset + 6), count = U16(d, offset + 8);
                return code >= first && code < first + count ? U16(d, offset + 10 + 2 * (code - first)) : 0;
            }

            case 10:
            {
                long first = U32(d, offset + 12), count = U32(d, offset + 16);
                return code >= first && code < first + count ? U16(d, offset + 20 + 2 * (int)(code - first)) : 0;
            }

            default:
            {
                // Formats 12 and 13: groups of (first, last, glyph).
                long groups = U32(d, offset + 12);
                if (groups > MaxSegments) throw new FontFormatException("cmap subtable has too many groups.");
                for (int g = 0; g < groups; g++)
                {
                    int at = offset + 16 + 12 * g;
                    long first = U32(d, at), last = U32(d, at + 4), glyph = U32(d, at + 8);
                    if (code < first || code > last) continue;
                    long id = format == 12 ? glyph + (code - first) : glyph;
                    return id <= int.MaxValue ? (int)id : 0;
                }

                return 0;
            }
        }
    }

    private void ReadNames(ReadOnlySpan<byte> post, int glyphCount)
    {
        if (post.Length < 32) return;
        uint version = U32(post, 0);
        switch (version)
        {
            case 0x00010000:
                for (int g = 0; g < glyphCount && g < MacGlyphNames.Count; g++) Add(MacGlyphNames.Names[g], g);
                break;
            case 0x00020000:
            {
                int count = U16(post, 32);
                var strings = new List<string>();
                int at = 34 + 2 * count;
                while (at < post.Length)
                {
                    int length = post[at];
                    Check(post, at + 1, length);
                    var chars = new char[length];
                    for (int k = 0; k < length; k++) chars[k] = (char)post[at + 1 + k];
                    strings.Add(new string(chars));
                    at += 1 + length;
                }

                for (int g = 0; g < count && g < glyphCount; g++)
                {
                    int index = U16(post, 34 + 2 * g);
                    if (index < MacGlyphNames.Count) Add(MacGlyphNames.Names[index], g);
                    else if (index - MacGlyphNames.Count < strings.Count) Add(strings[index - MacGlyphNames.Count], g);
                    else throw new FontFormatException("post table names a missing string.");
                }

                break;
            }

            case 0x00030000:
                break;
            default:
                // Format 2.5 and Apple's format 4 name glyphs in ways readers do not agree on.
                throw new FontFormatException("post table format is not read.");
        }

        void Add(string name, int glyph)
        {
            if (!_names.TryGetValue(name, out List<int>? list))
            {
                list = [];
                _names[name] = list;
            }

            list.Add(glyph);
        }
    }
}

/// <summary>The 258 standard Macintosh glyph names that post formats 1 and 2 refer to by index.</summary>
internal static class MacGlyphNames
{
    public const int Count = 258;

    private static string[]? s_names;

    public static string[] Names => LazyInitializer.EnsureInitialized(ref s_names, static () => Data.Split(' '))!;

    private const string Data =
        ".notdef .null nonmarkingreturn space exclam quotedbl numbersign dollar percent ampersand quotesingle " +
        "parenleft parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight " +
        "nine colon semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z " +
        "bracketleft backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v " +
        "w x y z braceleft bar braceright asciitilde Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis " +
        "aacute agrave acircumflex adieresis atilde aring ccedilla eacute egrave ecircumflex edieresis iacute igrave " +
        "icircumflex idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis " +
        "dagger degree cent sterling section bullet paragraph germandbls registered copyright trademark acute " +
        "dieresis notequal AE Oslash infinity plusminus lessequal greaterequal yen mu partialdiff summation product " +
        "pi integral ordfeminine ordmasculine Omega ae oslash questiondown exclamdown logicalnot radical florin " +
        "approxequal Delta guillemotleft guillemotright ellipsis nonbreakingspace Agrave Atilde Otilde OE oe endash " +
        "emdash quotedblleft quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis fraction currency " +
        "guilsinglleft guilsinglright fi fl daggerdbl periodcentered quotesinglbase quotedblbase perthousand " +
        "Acircumflex Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex " +
        "apple Ograve Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla " +
        "hungarumlaut ogonek caron Lslash lslash Scaron scaron Zcaron zcaron brokenbar Eth eth Yacute yacute Thorn " +
        "thorn minus multiply onesuperior twosuperior threesuperior onehalf onequarter threequarters franc Gbreve " +
        "gbreve Idotaccent Scedilla scedilla Cacute cacute Ccaron ccaron dcroat";
}
