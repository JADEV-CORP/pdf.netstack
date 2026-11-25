using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// The lighter object model: shared small integers and names, integers without text, one array
/// per dictionary, and a parser that reads ahead of a number only when a reference may follow.
/// Each must read exactly as before.
/// </summary>
[TestClass]
public sealed class ObjectSharingTests
{
    private static readonly string[] Vocabulary =
    [
        "0", "7", "12", "+3", "-0", "-4", "4.5", ".5", "65535", "65536", "2147483648", "1e5", "R", "RG", "obj", "endobj", "stream",
        "true", "false", "null", "/N", "/A#20B", "/", "(s)", "(a(b)c)", "(open", "<41>", "<4", "<<", ">>", ">", "[", "]", "{", "}", ")",
        "%note\n", "Tj", "1 0 R", "3 0 R", "12 65535 R",
    ];

    private static readonly string[] Separators = [" ", "", "\n", "\r\n", "%c\n", "  "];

    private static byte[] RandomSyntax(Random random)
    {
        var sb = new StringBuilder();
        int count = random.Next(1, 14);
        for (int i = 0; i < count; i++)
        {
            sb.Append(Vocabulary[random.Next(Vocabulary.Length)]);
            sb.Append(Separators[random.Next(Separators.Length)]);
        }

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The parser's object reader before it peeked past numbers: every lookahead reads a token.</summary>
    private static string ReferenceParse(PdfParser p, int depth = 0)
    {
        if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
        Token t = p.NextToken();
        ReadOnlySpan<byte> s = p.Span;
        switch (t.Kind)
        {
            case TokenKind.Number:
            {
                string text = Encoding.Latin1.GetString(s[t.Start..t.End]);
                if (PdfParser.TryParseInteger(s[t.Start..t.End], out long n) && n >= 0 && n <= int.MaxValue)
                {
                    int afterFirst = p.Position;
                    if (p.TryReadInteger(out long gen) && gen >= 0 && gen <= 65535 && p.TryReadKeyword("R"u8)) return $"ref {n} {gen}";
                    p.Position = afterFirst;
                }

                return "num " + PdfNumber.Parse(Encoding.Latin1.GetBytes(text));
            }

            case TokenKind.Name:
                return "name " + PdfParser.DecodeName(s[(t.Start + 1)..t.End]);
            case TokenKind.LiteralString:
            case TokenKind.HexString:
                return "string";
            case TokenKind.ArrayStart:
            {
                var items = new List<string>();
                while (true)
                {
                    int saved = p.Position;
                    Token next = p.NextToken();
                    if (next.Kind == TokenKind.ArrayEnd) break;
                    if (next.Kind is TokenKind.EndOfInput or TokenKind.DictEnd) throw new PdfFormatException("array");
                    if (next.Kind == TokenKind.Keyword && s[next.Start..next.End] is var k && (k.SequenceEqual("endobj"u8) || k.SequenceEqual("stream"u8) || k.SequenceEqual("obj"u8)))
                    {
                        throw new PdfFormatException("array");
                    }

                    p.Position = saved;
                    items.Add(ReferenceParse(p, depth + 1));
                }

                return "[" + string.Join(",", items) + "]";
            }

            case TokenKind.DictStart:
            {
                // Set semantics: a repeated key keeps its first place and takes the last value.
                var items = new List<(string Key, string Value)>();
                while (true)
                {
                    Token key = p.NextToken();
                    if (key.Kind == TokenKind.DictEnd) break;
                    if (key.Kind == TokenKind.EndOfInput) throw new PdfFormatException("dict");
                    if (key.Kind != TokenKind.Name)
                    {
                        if (key.Kind == TokenKind.Keyword && s[key.Start..key.End] is var k && (k.SequenceEqual("endobj"u8) || k.SequenceEqual("stream"u8) || k.SequenceEqual("obj"u8)))
                        {
                            throw new PdfFormatException("dict");
                        }

                        continue;
                    }

                    string name = PdfParser.DecodeName(s[(key.Start + 1)..key.End]);
                    int saved = p.Position;
                    if (p.NextToken().Kind == TokenKind.DictEnd) break;
                    p.Position = saved;
                    string value = ReferenceParse(p, depth + 1);
                    int at = items.FindIndex(e => e.Key == name);
                    if (at >= 0) items[at] = (name, value);
                    else items.Add((name, value));
                }

                return "<<" + string.Join(",", items.Select(e => e.Key + "=" + e.Value)) + ">>";
            }

            case TokenKind.Keyword:
            {
                ReadOnlySpan<byte> k = s[t.Start..t.End];
                if (k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8)) return "bool " + Encoding.Latin1.GetString(k);
                if (k.SequenceEqual("null"u8)) return "null";
                throw new PdfFormatException("keyword");
            }

            default:
                throw new PdfFormatException("token");
        }
    }

    private static string Describe(PdfObject o) => o switch
    {
        PdfReference r => $"ref {r.Number} {r.Generation}",
        PdfNumber n => "num " + n,
        PdfName n => "name " + n.Value,
        PdfString => "string",
        PdfArray a => "[" + string.Join(",", a.Select(Describe)) + "]",
        PdfDictionary d => "<<" + string.Join(",", d.Select(e => e.Key + "=" + Describe(e.Value))) + ">>",
        PdfBoolean b => "bool " + (b.Value ? "true" : "false"),
        PdfNull => "null",
        _ => o.GetType().Name,
    };

    private static (string Result, int Position, long Tokens) Outcome(Func<PdfParser, string> read, byte[] data, NameTable? names = null)
    {
        var parser = new PdfParser(data) { Names = names };
        string result;
        try
        {
            result = read(parser);
        }
        catch (PdfFormatException)
        {
            result = "fails";
        }

        return (result, parser.Position, parser.Tokens);
    }

    [TestMethod]
    public void ObjectsReadAsBeforeThePeekAtNumbers()
    {
        var random = new Random(20261118);
        var names = new NameTable();
        for (int i = 0; i < 30_000; i++)
        {
            byte[] data = RandomSyntax(random);
            var expected = Outcome(p => ReferenceParse(p), data);
            var parsed = Outcome(p => Describe(p.ParseObject()), data);
            var shared = Outcome(p => Describe(p.ParseObject()), data, names);
            string text = Encoding.Latin1.GetString(data);
            if (expected.Result == "fails")
            {
                Assert.AreEqual("fails", parsed.Result, text);
                Assert.AreEqual("fails", shared.Result, text);
                continue;
            }

            Assert.AreEqual(expected, parsed, text);
            Assert.AreEqual(expected, shared, text);
        }
    }

    [TestMethod]
    public void SkippingAnObjectEndsWhereReadingItEnds()
    {
        var random = new Random(118);
        for (int i = 0; i < 30_000; i++)
        {
            byte[] data = RandomSyntax(random);
            var read = new PdfParser(data);
            var skipped = new PdfParser(data);
            Exception? readError = null, skipError = null;
            try
            {
                read.ParseObject();
            }
            catch (PdfFormatException ex)
            {
                readError = ex;
            }

            try
            {
                skipped.SkipObject();
            }
            catch (PdfFormatException ex)
            {
                skipError = ex;
            }

            string text = Encoding.Latin1.GetString(data);
            Assert.AreEqual(readError is null, skipError is null, text);
            Assert.AreEqual(read.NestingExceeded, skipped.NestingExceeded, text);
            if (readError is not null) continue;
            Assert.AreEqual(read.Position, skipped.Position, text);
            Assert.AreEqual(read.Tokens, skipped.Tokens, text);
        }
    }

    [TestMethod]
    public void SkippingTooDeepAnObjectFailsLikeReadingIt()
    {
        byte[] deep = Encoding.ASCII.GetBytes(new string('[', PdfParser.MaxNesting + 5));
        var parser = new PdfParser(deep);
        Assert.ThrowsExactly<PdfFormatException>(() => parser.SkipObject());
        Assert.IsTrue(parser.NestingExceeded);
    }

    [TestMethod]
    [DataRow("5 >", "fails")]
    [DataRow("5 (open", "fails")]
    [DataRow("5 0 (open", "fails")]
    [DataRow("5 <4", "fails")]
    [DataRow("5 0 >", "fails")]
    [DataRow("5 0 R", "ref 5 0")]
    [DataRow("5 +0 R", "ref 5 0")]
    [DataRow("5 0 R/", "ref 5 0")]
    [DataRow("5 0 RG", "num 5")]
    [DataRow("5 0 %c\nR", "ref 5 0")]
    [DataRow("5 70000 R", "num 5")]
    [DataRow("5 >>", "num 5")]
    [DataRow("5 <<", "num 5")]
    [DataRow("5 /Name", "num 5")]
    public void ANumberIsAReferenceOnlyWhenOneFollows(string syntax, string expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(syntax);
        Assert.AreEqual(expected, Outcome(p => ReferenceParse(p), data).Result);
        Assert.AreEqual(Outcome(p => ReferenceParse(p), data), Outcome(p => Describe(p.ParseObject()), data));
    }

    [TestMethod]
    public void SmallIntegersAreSharedAndKeepTheirText()
    {
        Assert.AreSame(PdfNumber.Parse("12"u8), PdfNumber.Parse("+012"u8));
        Assert.AreSame(PdfNumber.Parse("-1"u8), PdfNumber.Parse("-1"u8));
        Assert.AreNotSame(PdfNumber.Parse("1024"u8), PdfNumber.Parse("1024"u8));
        foreach (string text in new[] { "0", "-1", "1023", "1024", "-2", "123456789012345678", "-987654321" })
        {
            PdfNumber n = PdfNumber.Parse(Encoding.ASCII.GetBytes(text));
            Assert.IsTrue(n.IsInteger);
            Assert.AreEqual(text, n.ToString());
            Assert.AreEqual(text, Encoding.ASCII.GetString(n.Text));
        }

        Assert.AreEqual("-9223372036854775808", PdfNumber.FromInteger(long.MinValue).ToString());
    }

    [TestMethod]
    [DataRow("5", "5", true)]
    [DataRow("5", "005.000", true)]
    [DataRow("5.5", "5.50", true)]
    [DataRow("-0", "0.0", true)]
    [DataRow("5", "5.5", false)]
    [DataRow("1234567890123456789", "1234567890123456789", true)]
    [DataRow("1234567890123456789", "1234567890123456788", false)]
    [DataRow("1-2", "1-2", true)]
    [DataRow("1-2", "12", false)]
    public void NumbersCompareByCanonicalText(string a, string b, bool same)
    {
        PdfNumber x = PdfNumber.Parse(Encoding.ASCII.GetBytes(a)), y = PdfNumber.Parse(Encoding.ASCII.GetBytes(b));
        Assert.AreEqual(same, x.SameValue(y));
        Assert.AreEqual(same, y.SameValue(x));
        Assert.AreEqual(same, x.Text.AsSpan().SequenceEqual(y.Text));
    }

    [TestMethod]
    public void CanonicalTextIsNeverLongerThanTheToken()
    {
        var random = new Random(5);
        const string alphabet = "0123456789.+-";
        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(1, 12);
            var chars = new char[length];
            for (int c = 0; c < length; c++) chars[c] = alphabet[random.Next(alphabet.Length)];
            byte[] raw = Encoding.ASCII.GetBytes(chars);
            var text = new byte[raw.Length];
            int written = PdfNumber.Canonicalize(raw, text, out bool isInteger, out long value);
            PdfNumber parsed = PdfNumber.Parse(raw);
            Assert.IsTrue(text.AsSpan(0, written).SequenceEqual(parsed.Text), new string(chars));
            Assert.AreEqual(parsed.IsInteger, isInteger);
            if (isInteger) Assert.AreEqual(parsed.IntegerValue, value);
        }
    }

    [TestMethod]
    public void NamesAreDecodedOnceAndShared()
    {
        var table = new NameTable();
        Assert.AreEqual("A B", table.Get("A#20B"u8));
        Assert.AreSame(table.Get("Type"u8), table.Get("Type"u8));
        Assert.AreSame(table.GetName("Type"u8), table.GetName("Type"u8));
        Assert.AreEqual("Type", table.GetName("Type"u8).Value);
        Assert.AreEqual("a#zz", table.Get("a#zz"u8));
        Assert.AreEqual("", table.Get(""u8));

        string longName = new('x', 200);
        Assert.AreEqual(longName, table.Get(Encoding.ASCII.GetBytes(longName)));
        Assert.AreEqual(longName, table.GetName(Encoding.ASCII.GetBytes(longName)).Value);

        // Far more names than the table keeps: every one still reads right.
        for (int i = 0; i < 40_000; i++)
        {
            string name = "N" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(name, table.Get(Encoding.ASCII.GetBytes(name)));
        }

        Assert.AreEqual("N7", table.GetName("N7"u8).Value);
    }

    [TestMethod]
    public void AFileSharesItsNamesAcrossObjects()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]>>");
        PdfFile file = PdfFile.Open(b.Build(), 1 << 20);
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        PdfDictionary page = file.ResolveDictionary(new PdfReference(3, 0))!;
        Assert.AreSame(catalog.Keys[0], page.Keys[0]);
        Assert.AreSame(file.ResolveDictionary(new PdfReference(2, 0))!["Type"], file.ResolveDictionary(new PdfReference(2, 0))!["Type"]);
    }

    [TestMethod]
    public void DictionariesKeepOrderReplaceInPlaceAndIndexLargeOnes()
    {
        var d = new PdfDictionary();
        for (int i = 0; i < 30; i++) d.Set("K" + i, PdfNumber.FromInteger(i));
        d.Set("K3", PdfNumber.FromInteger(99));
        Assert.AreEqual("K3", d.Keys[3]);
        Assert.AreEqual(99, ((PdfNumber)d["K3"]!).IntegerValue);
        Assert.IsTrue(d.Remove("K0"));
        Assert.IsFalse(d.Remove("K0"));
        Assert.AreEqual(29, d.Count);
        Assert.AreEqual("K1", d.Keys[0]);
        Assert.AreEqual(29, ((PdfNumber)d["K29"]!).IntegerValue);
        d.Set("K0", PdfNull.Instance);
        Assert.AreEqual("K0", d.Keys[^1]);
        CollectionAssert.AreEqual(d.Keys.ToList(), d.Select(e => e.Key).ToList());
        d["K5"] = null;
        Assert.IsFalse(d.ContainsKey("K5"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = d.Keys[d.Count]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => d.EntryAt(d.Count));
    }

    [TestMethod]
    public void KeysAreALiveViewThatFailsWhenChangedDuringEnumeration()
    {
        var d = new PdfDictionary();
        d.Set("A", PdfNull.Instance);
        IReadOnlyList<string> keys = d.Keys;
        d.Set("B", PdfNull.Instance);
        Assert.HasCount(2, keys);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            foreach (string key in d.Keys) d.Remove(key);
        });

        // Replacing a value is no change to the keys.
        foreach (string key in d.Keys) d.Set(key, PdfBoolean.True);
        Assert.AreSame(PdfBoolean.True, d["B"]);
    }
}
