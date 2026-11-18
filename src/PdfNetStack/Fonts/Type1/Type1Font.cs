using System.Globalization;
using System.Text;

namespace PdfNetStack.Fonts.Type1;

/// <summary>
/// A Type 1 font program as embedded in a PDF (FontFile): the clear text part, then the eexec
/// encrypted part, binary or hex (Adobe Type 1 Font Format, chapters 2, 7 and 8). Reads what
/// rendering depends on: the font matrix and box, paint type, built-in encoding, the Private
/// dictionary's hinting values, and the decrypted subroutines and charstrings. Anything it cannot
/// read for certain throws <see cref="FontFormatException"/>, so callers leave the font alone.
/// </summary>
internal sealed class Type1Font
{
    /// <summary>Glyphs a program may define at most; real fonts stay far below.</summary>
    private const int MaxGlyphs = 65535;

    private const ushort EexecKey = 55665, CharstringKey = 4330;

    private Type1Font()
    {
    }

    public string FontName { get; private set; } = "";

    /// <summary>The font matrix as written, six numbers.</summary>
    public decimal[] FontMatrix { get; private set; } = [0.001m, 0, 0, 0.001m, 0, 0];

    public decimal[] FontBBox { get; private set; } = [0, 0, 0, 0];

    public int PaintType { get; private set; }

    /// <summary>The built-in encoding: null for StandardEncoding, else a glyph name (or null) per code.</summary>
    public string?[]? Encoding { get; private set; }

    /// <summary>Text entries of the FontInfo dictionary (version, Notice, FullName, FamilyName, Weight, Copyright).</summary>
    public Dictionary<string, string> Info { get; } = new(StringComparer.Ordinal);

    /// <summary>Numeric and boolean FontInfo entries (ItalicAngle, isFixedPitch, UnderlinePosition, UnderlineThickness).</summary>
    public Dictionary<string, decimal> InfoNumbers { get; } = new(StringComparer.Ordinal);

    public decimal? UniqueId { get; private set; }

    /// <summary>Private dictionary values that steer hinting: arrays and numbers, ForceBold as 1 or 0.</summary>
    public Dictionary<string, decimal[]> Private { get; } = new(StringComparer.Ordinal);

    public byte[][] Subrs { get; private set; } = [];

    /// <summary>Glyph names and decrypted charstrings, in the order of the CharStrings dictionary.</summary>
    public List<(string Name, byte[] CharString)> Glyphs { get; } = [];

    /// <summary>Private keys a CFF Private DICT carries with the same meaning.</summary>
    public static readonly string[] HintKeys =
    [
        "BlueValues", "OtherBlues", "FamilyBlues", "FamilyOtherBlues", "BlueScale", "BlueShift", "BlueFuzz",
        "StdHW", "StdVW", "StemSnapH", "StemSnapV", "ForceBold", "LanguageGroup", "ExpansionFactor",
    ];

    /// <summary>Reads a FontFile stream's decoded data.</summary>
    /// <exception cref="FontFormatException">The program is not a Type 1 font this reader understands exactly.</exception>
    public static Type1Font Parse(ReadOnlySpan<byte> program)
    {
        byte[] data = StripPfb(program);
        int eexec = FindEexec(data);
        var font = new Type1Font();
        font.ReadClearText(new PsReader(data.AsSpan(0, eexec).ToArray()));

        // The first cipher byte is never white space (Type 1 Font Format, 7.2); readers skip it all.
        int start = eexec + 5;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        byte[] cipher = data.AsSpan(start).ToArray();
        if (cipher.Length >= 4 && IsHex(cipher[0]) && IsHex(cipher[1]) && IsHex(cipher[2]) && IsHex(cipher[3])) cipher = FromHex(cipher);
        byte[] plain = Decrypt(cipher, EexecKey, 4);
        font.ReadPrivate(new PsReader(plain));
        if (font.Glyphs.Count == 0 || font.Glyphs[0].Name != ".notdef" && !font.Glyphs.Exists(g => g.Name == ".notdef"))
        {
            throw new FontFormatException("Type 1 font has no .notdef glyph.");
        }

        return font;
    }

    /// <summary>A PFB file's segments joined; other data as it is.</summary>
    private static byte[] StripPfb(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || data[0] != 0x80 || data[1] is not (1 or 2)) return data.ToArray();
        using var joined = new MemoryStream(data.Length);
        int at = 0;
        while (at + 2 <= data.Length && data[at] == 0x80 && data[at + 1] is 1 or 2)
        {
            if (at + 6 > data.Length) throw new FontFormatException("PFB segment header is truncated.");
            int length = data[at + 2] | data[at + 3] << 8 | data[at + 4] << 16 | data[at + 5] << 24;
            if (length < 0 || at + 6 + length > data.Length) throw new FontFormatException("PFB segment is truncated.");
            joined.Write(data.Slice(at + 6, length).ToArray(), 0, length);
            at += 6 + length;
        }

        return joined.ToArray();
    }

    private static int FindEexec(byte[] data)
    {
        ReadOnlySpan<byte> d = data;
        int at = d.IndexOf("eexec"u8);
        if (at < 0) throw new FontFormatException("Type 1 font has no eexec section.");
        return at;
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static byte[] FromHex(byte[] hex)
    {
        var bytes = new List<byte>(hex.Length / 2);
        int high = -1;
        foreach (byte b in hex)
        {
            int v = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (v < 0)
            {
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
                break;
            }

            if (high < 0) high = v;
            else
            {
                bytes.Add((byte)(high << 4 | v));
                high = -1;
            }
        }

        return [.. bytes];
    }

    /// <summary>eexec or charstring decryption (Type 1 Font Format, 7.1), dropping the first <paramref name="skip"/> bytes.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ushort key, int skip)
    {
        if (skip < 0) return cipher.ToArray();
        if (cipher.Length < skip) throw new FontFormatException("Type 1 encrypted data is shorter than its random bytes.");
        var plain = new byte[cipher.Length - skip];
        ushort r = key;
        for (int i = 0; i < cipher.Length; i++)
        {
            byte c = cipher[i];
            byte p = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            if (i >= skip) plain[i - skip] = p;
        }

        return plain;
    }

    private void ReadClearText(PsReader ps)
    {
        while (ps.Next() is { } token)
        {
            if (token.Kind != PsKind.Name) continue;
            switch (token.Text)
            {
                // Header code such as "/FontType get 1 eq" names keys without defining them; only
                // a name followed by its value counts.
                case "FontName":
                    if (ps.Next() is { Kind: PsKind.Name } name) FontName = name.Text;
                    break;
                case "FontType":
                    if (ps.TryNumber() is { } type && type != 1) throw new FontFormatException("Not a Type 1 font.");
                    break;
                case "PaintType":
                    if (ps.TryNumber() is { } paint) PaintType = paint == 0 ? 0 : 1;
                    break;
                case "FontMatrix":
                    FontMatrix = ps.NumberArray();
                    if (FontMatrix.Length != 6) throw new FontFormatException("Type 1 /FontMatrix needs six numbers.");
                    break;
                case "FontBBox":
                    FontBBox = ps.NumberArray();
                    if (FontBBox.Length != 4) throw new FontFormatException("Type 1 /FontBBox needs four numbers.");
                    break;
                case "UniqueID":
                    if (ps.TryNumber() is { } id) UniqueId = id;
                    break;
                case "Encoding":
                    ReadEncoding(ps);
                    break;
                case "version" or "Notice" or "FullName" or "FamilyName" or "Weight" or "Copyright":
                    if (ps.Next() is { Kind: PsKind.String } text) Info[token.Text] = text.Text;
                    break;
                case "ItalicAngle" or "UnderlinePosition" or "UnderlineThickness":
                    if (ps.TryNumber() is { } number) InfoNumbers[token.Text] = number;
                    break;
                case "isFixedPitch":
                    InfoNumbers[token.Text] = ps.Next() is { Kind: PsKind.Keyword, Text: "true" } ? 1 : 0;
                    break;
                case "WeightVector" or "BlendDesignPositions" or "BlendDesignMap" or "BlendAxisTypes":
                    throw new FontFormatException("Multiple master Type 1 fonts are not converted.");
            }
        }
    }

    private void ReadEncoding(PsReader ps)
    {
        PsToken first = ps.Next() ?? throw new FontFormatException("Type 1 /Encoding is truncated.");
        if (first is { Kind: PsKind.Keyword, Text: "StandardEncoding" })
        {
            Encoding = null;
            return;
        }

        if (first.Kind != PsKind.Number) throw new FontFormatException("Type 1 /Encoding is not understood.");
        var table = new string?[256];

        // "dup code /name put" until "def", whatever comes between (array, for loop, readonly).
        while (ps.Next() is { } token)
        {
            if (token is { Kind: PsKind.Keyword, Text: "def" }) break;
            if (token is not { Kind: PsKind.Keyword, Text: "dup" }) continue;
            PsToken? code = ps.Next(), name = ps.Next(), put = ps.Next();
            if (code is not { Kind: PsKind.Number } || name is not { Kind: PsKind.Name } || put is not { Kind: PsKind.Keyword, Text: "put" })
            {
                throw new FontFormatException("Type 1 /Encoding entry is not understood.");
            }

            if (code.Value is < 0 or > 255 || code.Value != decimal.Truncate(code.Value)) throw new FontFormatException("Type 1 /Encoding code is out of range.");
            int c = (int)code.Value;
            table[c] = name.Text;
        }

        Encoding = table;
    }

    private void ReadPrivate(PsReader ps)
    {
        int lenIV = 4;
        bool inPrivate = false;
        while (ps.Next() is { } token)
        {
            if (token.Kind != PsKind.Name) continue;
            switch (token.Text)
            {
                case "Private":
                    inPrivate = true;
                    break;
                case "lenIV" when inPrivate:
                    lenIV = ps.Int(-1, 64);
                    break;
                case "BlueValues" or "OtherBlues" or "FamilyBlues" or "FamilyOtherBlues" or "StdHW" or "StdVW" or "StemSnapH" or "StemSnapV" when inPrivate:
                    Private[token.Text] = ps.NumberArray();
                    break;
                case "BlueScale" or "BlueShift" or "BlueFuzz" or "LanguageGroup" or "ExpansionFactor" when inPrivate:
                    Private[token.Text] = [ps.Number()];
                    break;
                case "ForceBold" when inPrivate:
                    Private[token.Text] = [ps.Next() is { Kind: PsKind.Keyword, Text: "true" } ? 1 : 0];
                    break;
                case "Subrs" when inPrivate:
                    Subrs = ReadSubrs(ps, lenIV);
                    break;
                case "OtherSubrs":
                    // PostScript procedures for flex and hint replacement; the interpreter knows what they do.
                    ps.SkipValue();
                    break;
                case "CharStrings":
                    ReadCharStrings(ps, lenIV);
                    return;
                case "WeightVector" or "NDV" or "CDV":
                    throw new FontFormatException("Multiple master Type 1 fonts are not converted.");
            }
        }

        throw new FontFormatException("Type 1 font has no CharStrings.");
    }

    private static byte[][] ReadSubrs(PsReader ps, int lenIV)
    {
        int count = ps.Int(0, MaxGlyphs);
        var subrs = new byte[count][];
        for (int read = 0; read < count;)
        {
            PsToken token = ps.Next() ?? throw new FontFormatException("Type 1 /Subrs is truncated.");
            if (token is not { Kind: PsKind.Keyword, Text: "dup" }) continue;
            int index = ps.Int(0, count - 1);
            subrs[index] = Decrypt(ps.Binary(), CharstringKey, lenIV);
            read++;
        }

        for (int i = 0; i < count; i++) subrs[i] ??= [11];
        return subrs;
    }

    private void ReadCharStrings(PsReader ps, int lenIV)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (ps.Next() is { } token)
        {
            if (token is { Kind: PsKind.Keyword, Text: "end" }) return;
            if (token.Kind != PsKind.Name) continue;
            if (Glyphs.Count >= MaxGlyphs) throw new FontFormatException("Type 1 font has too many glyphs.");
            byte[] cs = Decrypt(ps.Binary(), CharstringKey, lenIV);

            // A repeated name redefines the glyph, as PostScript's def would.
            if (!seen.Add(token.Text)) Glyphs.RemoveAll(g => g.Name == token.Text);
            Glyphs.Add((token.Text, cs));
        }
    }

    private enum PsKind
    {
        Name,
        Number,
        String,
        Keyword,
        Open,
        Close,
    }

    private sealed record PsToken(PsKind Kind, string Text, decimal Value = 0);

    /// <summary>Just enough of the PostScript scanner for a font's dictionaries.</summary>
    private sealed class PsReader(byte[] data)
    {
        private int _at;

        public PsToken? Next()
        {
            SkipSpace();
            if (_at >= data.Length) return null;
            byte b = data[_at];
            switch (b)
            {
                case (byte)'/':
                {
                    int start = ++_at;
                    while (_at < data.Length && IsRegular(data[_at])) _at++;
                    return new PsToken(PsKind.Name, Latin1(start, _at));
                }

                case (byte)'(':
                    return new PsToken(PsKind.String, ReadString());
                case (byte)'[' or (byte)'{':
                    _at++;
                    return new PsToken(PsKind.Open, ((char)b).ToString());
                case (byte)']' or (byte)'}':
                    _at++;
                    return new PsToken(PsKind.Close, ((char)b).ToString());
                case (byte)'<':
                {
                    // Hex strings and dictionaries carry nothing this reader needs.
                    int close = Array.IndexOf(data, (byte)'>', _at + 1);
                    _at = close < 0 ? data.Length : close + 1;
                    return new PsToken(PsKind.String, "");
                }

                default:
                {
                    int start = _at;
                    while (_at < data.Length && IsRegular(data[_at])) _at++;
                    if (_at == start) _at++;
                    string text = Latin1(start, _at);
                    return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v)
                        ? new PsToken(PsKind.Number, text, v)
                        : new PsToken(PsKind.Keyword, text);
                }
            }
        }

        public decimal Number() =>
            Next() is { Kind: PsKind.Number } n ? n.Value : throw new FontFormatException("Type 1 font has a value that is not a number.");

        /// <summary>An integer between <paramref name="min"/> and <paramref name="max"/>.</summary>
        public int Int(int min, int max)
        {
            decimal v = Number();
            if (v < min || v > max || v != decimal.Truncate(v)) throw new FontFormatException("Type 1 font has a number out of range.");
            return (int)v;
        }

        /// <summary>The next token's value when it is a number; consumes the token either way.</summary>
        public decimal? TryNumber() => Next() is { Kind: PsKind.Number } n ? n.Value : null;

        /// <summary>Skips one value, a bracketed array or procedure included.</summary>
        public void SkipValue()
        {
            int depth = 0;
            do
            {
                PsToken t = Next() ?? throw new FontFormatException("Type 1 value is truncated.");
                if (t.Kind == PsKind.Open) depth++;
                else if (t.Kind == PsKind.Close) depth--;
            }
            while (depth > 0);
        }

        /// <summary>An array or procedure of numbers: [a b c] or {a b c}.</summary>
        public decimal[] NumberArray()
        {
            if (Next() is not { Kind: PsKind.Open }) throw new FontFormatException("Type 1 font has a value that is not an array.");
            var values = new List<decimal>();
            while (true)
            {
                PsToken t = Next() ?? throw new FontFormatException("Type 1 array is truncated.");
                if (t.Kind == PsKind.Close) return [.. values];
                if (t.Kind != PsKind.Number) throw new FontFormatException("Type 1 array holds something other than numbers.");
                values.Add(t.Value);
            }
        }

        /// <summary>"length RD binary": the binary data after a length and one token, past a single space.</summary>
        public byte[] Binary()
        {
            int length = Int(0, data.Length);
            if (Next() is not { Kind: PsKind.Keyword }) throw new FontFormatException("Type 1 binary data has no RD token.");
            _at++;
            if (_at + length > data.Length) throw new FontFormatException("Type 1 binary data is truncated.");
            byte[] bytes = data.AsSpan(_at, length).ToArray();
            _at += length;
            return bytes;
        }

        private void SkipSpace()
        {
            while (_at < data.Length)
            {
                byte b = data[_at];
                if (b == '%')
                {
                    while (_at < data.Length && data[_at] is not ((byte)'\r' or (byte)'\n')) _at++;
                }
                else if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
                {
                    _at++;
                }
                else
                {
                    return;
                }
            }
        }

        private string ReadString()
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (_at++; _at < data.Length; _at++)
            {
                char c = (char)data[_at];
                if (c == '\\' && _at + 1 < data.Length)
                {
                    char e = (char)data[++_at];
                    if (e is >= '0' and <= '7')
                    {
                        // Up to three octal digits.
                        int v = e - '0';
                        for (int k = 0; k < 2 && _at + 1 < data.Length && data[_at + 1] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + data[++_at] - '0';
                        sb.Append((char)(v & 0xFF));
                    }
                    else
                    {
                        sb.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => e });
                    }

                    continue;
                }

                if (c == '(') depth++;
                if (c == ')' && depth-- == 0)
                {
                    _at++;
                    return sb.ToString();
                }

                sb.Append(c);
            }

            throw new FontFormatException("Type 1 string is not closed.");
        }

        private static bool IsRegular(byte b) => b > 32 && b < 127 && b is not ((byte)'/' or (byte)'(' or (byte)')' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'<' or (byte)'>' or (byte)'%');

        private string Latin1(int start, int end)
        {
            var chars = new char[end - start];
            for (int i = start; i < end; i++) chars[i - start] = (char)data[i];
            return new string(chars);
        }
    }
}
