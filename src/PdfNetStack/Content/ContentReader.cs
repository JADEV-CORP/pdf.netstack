using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Content;

/// <summary>An operator with its operands; for BI ... EI, the inline image's dictionary and data.</summary>
internal sealed class ContentOperation
{
    public string Operator { get; set; } = "";

    public List<PdfObject> Operands { get; } = [];

    public PdfDictionary? InlineImage { get; set; }

    public ReadOnlyMemory<byte> InlineData { get; set; }
}

/// <summary>
/// The tokens the content of one page may take to read, shared by its streams and the forms it
/// draws. Tokens are counted as read, so a damaged operand read twice counts twice.
/// </summary>
internal sealed class ContentBudget(long tokens)
{
    public long TokensLeft { get; set; } = tokens;

    public bool Exhausted => TokensLeft < 0;
}

/// <summary>
/// Reads a content stream as operators and operands (ISO 32000-1, 7.8.2), tolerantly: a damaged
/// operand or a stray delimiter is skipped, as viewers skip them, and the rest still reads.
/// </summary>
internal sealed class ContentReader
{
    /// <summary>Operands one operator may take. TJ arrays are one operand however long.</summary>
    private const int MaxOperands = 4096;

    // Inline image abbreviations (ISO 32000-1, Table 93 and 94).
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace", ["D"] = "Decode", ["DP"] = "DecodeParms", ["F"] = "Filter",
        ["H"] = "Height", ["IM"] = "ImageMask", ["I"] = "Interpolate", ["W"] = "Width", ["L"] = "Length",
    };

    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        ["G"] = "DeviceGray", ["RGB"] = "DeviceRGB", ["CMYK"] = "DeviceCMYK", ["I"] = "Indexed",
        ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
    };

    // The operators of ISO 32000-1, Table A.1, keyed by their bytes: each read shares one string.
    private static readonly Dictionary<int, string> Operators = OperatorTable(
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI", "EMC",
        "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re",
        "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr",
        "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"");

    private readonly PdfParser _parser;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ContentBudget? _budget;

    // Tokens of the parser already charged to the budget.
    private long _charged;

    /// <param name="budget">Shared with the page's other streams; null reads without a bound.</param>
    public ContentReader(ReadOnlyMemory<byte> data, ContentBudget? budget = null)
    {
        _data = data;
        _parser = new PdfParser(data);
        _budget = budget;
    }

    /// <summary>True when reading stopped because the budget ran out rather than at the end of the data.</summary>
    public bool BudgetExhausted => _budget is { Exhausted: true };

    /// <summary>Reads the next operation into <paramref name="op"/>; false at the end of the data.</summary>
    public bool Next(ContentOperation op)
    {
        op.Operands.Clear();
        op.InlineImage = null;
        op.InlineData = default;
        while (true)
        {
            if (!Charge()) return false;
            int start;
            Token t;
            try
            {
                start = _parser.Position;
                t = _parser.NextToken();
            }
            catch (PdfFormatException)
            {
                // An unterminated string or hex string: nothing after it can be read.
                return false;
            }

            if (t.Kind == TokenKind.EndOfInput) return false;
            if (t.Kind == TokenKind.Keyword)
            {
                ReadOnlySpan<byte> k = _parser.Span[t.Start..t.End];
                if (k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8) || k.SequenceEqual("null"u8))
                {
                    _parser.Position = start;
                    AddOperand(op, _parser.ParseObject());
                    continue;
                }

                if (k.Length == 1 && k[0] is (byte)')' or (byte)'{' or (byte)'}') continue;
                op.Operator = OperatorName(k);
                if (op.Operator == "BI") ReadInlineImage(op);
                return true;
            }

            _parser.Position = start;
            try
            {
                AddOperand(op, _parser.ParseObject());
            }
            catch (PdfFormatException) when (_parser.NestingExceeded)
            {
                // Retrying one bracket further in would parse the same deep run again for every
                // bracket in it, quadratic in its length: step over the whole run at once.
                if (!SkipNested(start)) return false;
            }
            catch (PdfFormatException)
            {
                // A damaged operand: skip past the token that started it.
                _parser.Position = Math.Max(t.End, start + 1);
            }
        }
    }

    /// <summary>Charges the tokens read since the last call; false once the budget has run out.</summary>
    private bool Charge()
    {
        if (_budget is null) return true;
        _budget.TokensLeft -= _parser.Tokens - _charged;
        _charged = _parser.Tokens;
        return !_budget.Exhausted;
    }

    /// <summary>
    /// Steps over an array or dictionary nested too deep to read, in one pass: to the bracket
    /// that closes it, or to the next operator, since an operand cannot hold one. False when the
    /// data ends inside a string, after which nothing can be read.
    /// </summary>
    private bool SkipNested(int start)
    {
        _parser.Position = start;
        int depth = 0;
        try
        {
            while (true)
            {
                int at = _parser.Position;
                Token t = _parser.NextToken();
                switch (t.Kind)
                {
                    case TokenKind.EndOfInput:
                        return true;
                    case TokenKind.ArrayStart or TokenKind.DictStart:
                        depth++;
                        break;
                    case TokenKind.ArrayEnd or TokenKind.DictEnd:
                        if (--depth <= 0) return true;
                        break;
                    case TokenKind.Keyword when !IsValueKeyword(_parser.Span[t.Start..t.End]):
                        _parser.Position = at;
                        return true;
                }
            }
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    private static bool IsValueKeyword(ReadOnlySpan<byte> k) =>
        k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8) || k.SequenceEqual("null"u8) || (k.Length == 1 && k[0] is (byte)')' or (byte)'{' or (byte)'}');

    private static string OperatorName(ReadOnlySpan<byte> keyword) =>
        OperatorKey(keyword) is int key && Operators.TryGetValue(key, out string? name) ? name : Compat.Latin1.GetString(keyword);

    /// <summary>Up to three bytes and the length in one number; null for longer keywords.</summary>
    private static int? OperatorKey(ReadOnlySpan<byte> keyword)
    {
        if (keyword.Length is 0 or > 3) return null;
        int key = keyword.Length;
        foreach (byte b in keyword) key = (key << 8) | b;
        return key;
    }

    private static Dictionary<int, string> OperatorTable(params string[] names)
    {
        var table = new Dictionary<int, string>();
        foreach (string name in names) table[OperatorKey(Compat.Latin1.GetBytes(name))!.Value] = name;
        return table;
    }

    private static void AddOperand(ContentOperation op, PdfObject value)
    {
        // Operands beyond any operator's need are dropped; the newest matter, as the operator
        // follows them. Half go at once: dropping one at a time moves the whole list per operand.
        if (op.Operands.Count >= MaxOperands) op.Operands.RemoveRange(0, MaxOperands / 2);
        op.Operands.Add(value);
    }

    /// <summary>BI key value pairs ID data EI (ISO 32000-1, 8.9.7).</summary>
    private void ReadInlineImage(ContentOperation op)
    {
        var dict = new PdfDictionary();
        op.Operator = "BI";
        while (true)
        {
            int start = _parser.Position;
            Token t;
            try
            {
                t = _parser.NextToken();
            }
            catch (PdfFormatException)
            {
                _parser.Position = _data.Length;
                return;
            }

            if (t.Kind == TokenKind.EndOfInput) return;
            if (t.Kind == TokenKind.Keyword && _parser.Span[t.Start..t.End].SequenceEqual("ID"u8)) break;
            if (t.Kind != TokenKind.Name) continue;
            string key = PdfParser.DecodeName(_parser.Span[(t.Start + 1)..t.End]);
            try
            {
                PdfObject value = Expand(_parser.ParseObject());
                dict.Set(Keys.TryGetValue(key, out string? full) ? full : key, value);
            }
            catch (PdfFormatException)
            {
                _parser.Position = Math.Max(_parser.Position, start + 1);
            }
        }

        // One white-space byte separates ID from the data.
        int dataStart = _parser.Position + 1;
        if (dataStart > _data.Length) return;
        int end = DataEnd(dict, dataStart);
        op.InlineImage = dict;
        op.InlineData = _data.Slice(dataStart, end - dataStart);
        _parser.Position = Math.Min(_data.Length, end);

        // Step over EI.
        int saved = _parser.Position;
        try
        {
            Token ei = _parser.NextToken();
            if (!(ei.Kind == TokenKind.Keyword && _parser.Span[ei.Start..ei.End].SequenceEqual("EI"u8))) _parser.Position = saved;
        }
        catch (PdfFormatException)
        {
            _parser.Position = saved;
        }
    }

    private static PdfObject Expand(PdfObject value) => value switch
    {
        PdfName n when Values.TryGetValue(n.Value, out string? full) => new PdfName(full),
        PdfArray a => new PdfArray(a.Select(Expand).ToList()),
        _ => value,
    };

    /// <summary>
    /// Where inline image data ends. Unfiltered data has a known length; otherwise the end is the
    /// first EI between white space, the rule viewers use, since filtered data has no length.
    /// </summary>
    private int DataEnd(PdfDictionary dict, int start)
    {
        ReadOnlySpan<byte> s = _data.Span;
        if (dict["Length"] is PdfNumber { IsInteger: true } length && length.IntegerValue >= 0 && start + length.IntegerValue <= s.Length)
        {
            return start + (int)length.IntegerValue;
        }

        if (dict["Filter"] is null && Size(dict) is long size && start + size <= s.Length) return start + (int)size;

        for (int i = start; i + 1 < s.Length; i++)
        {
            if (s[i] == (byte)'E' && s[i + 1] == (byte)'I'
                && (i == start || PdfSyntax.IsWhitespace(s[i - 1]))
                && (i + 2 == s.Length || PdfSyntax.IsWhitespace(s[i + 2]) || PdfSyntax.IsDelimiter(s[i + 2])))
            {
                return i > start ? i - 1 : i;
            }
        }

        return s.Length;
    }

    /// <summary>Bytes of unfiltered samples: rows padded to whole bytes.</summary>
    private static long? Size(PdfDictionary dict)
    {
        if (dict["Width"] is not PdfNumber { IsInteger: true } w || dict["Height"] is not PdfNumber { IsInteger: true } h) return null;
        if (w.IntegerValue is <= 0 or > 1 << 20 || h.IntegerValue is <= 0 or > 1 << 20) return null;
        bool mask = dict["ImageMask"] is PdfBoolean { Value: true };
        long bpc = mask ? 1 : dict["BitsPerComponent"] is PdfNumber { IsInteger: true } b ? b.IntegerValue : 8;
        int colors = mask ? 1 : dict["ColorSpace"] switch
        {
            PdfName { Value: "DeviceRGB" or "CalRGB" or "Lab" } => 3,
            PdfName { Value: "DeviceCMYK" } => 4,
            PdfName { Value: "DeviceGray" or "CalGray" } => 1,
            PdfArray a when a.Count > 0 && a[0] is PdfName { Value: "Indexed" } => 1,
            _ => -1,
        };
        if (colors < 0 || bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        return ((w.IntegerValue * colors * bpc) + 7) / 8 * h.IntegerValue;
    }
}
