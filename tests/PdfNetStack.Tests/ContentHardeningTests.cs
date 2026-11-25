using System.Text;
using PdfNetStack.Content;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>The content reader, interpreter and text extractor stay bounded on hostile content.</summary>
[TestClass]
public sealed class ContentHardeningTests
{
    private const string Font = "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>";

    private static byte[] Page(params byte[][] contents)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        var refs = new StringBuilder();
        for (int i = 0; i < contents.Length; i++)
        {
            b.SetStream(10 + i, "", contents[i]);
            refs.Append(10 + i).Append(" 0 R ");
        }

        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<{Font}>>/Contents[{refs}]>>");
        return b.Build();
    }

    private static byte[] Page(string content) => Page(Encoding.ASCII.GetBytes(content));

    [TestMethod]
    public void InlineImageTokensAreChargedBeforeTheOperatorIsReturned()
    {
        var budget = new ContentBudget(1);
        var reader = new ContentReader(Encoding.ASCII.GetBytes("BI /W 1 /H 1 /CS /G /BPC 8 ID x EI"), budget);

        Assert.IsFalse(reader.Next(new ContentOperation()));
        Assert.IsTrue(reader.BudgetExhausted);
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("false")]
    [DataRow("null")]
    public void AKeywordOperandReadWithTheLastTokenStopsAtTheBudget(string keyword)
    {
        var budget = new ContentBudget(0);
        var reader = new ContentReader(Encoding.ASCII.GetBytes(keyword + " Tj"), budget);

        Assert.IsFalse(reader.Next(new ContentOperation()));
        Assert.IsTrue(reader.BudgetExhausted);
    }

    [TestMethod]
    public void KeywordOperandsCostOneTokenEach()
    {
        var budget = new ContentBudget(10);
        var reader = new ContentReader("true false null Tj"u8.ToArray(), budget);
        var op = new ContentOperation();

        Assert.IsTrue(reader.Next(op));

        Assert.AreEqual("Tj", op.Operator);
        CollectionAssert.AreEqual(new PdfObject[] { PdfBoolean.True, PdfBoolean.False, PdfNull.Instance }, op.Operands);
        Assert.AreEqual(6, budget.TokensLeft);
    }

    [TestMethod]
    public void OneHugeOperandStopsAtTheTokenBudget()
    {
        // The array used to be read whole, 100,000 objects, before its tokens were charged.
        var budget = new ContentBudget(1000);
        var reader = new ContentReader(Encoding.ASCII.GetBytes("[" + string.Concat(Enumerable.Repeat("1 ", 100_000)) + "] TJ"), budget);

        Assert.IsFalse(reader.Next(new ContentOperation()));
        Assert.IsTrue(reader.BudgetExhausted);
        Assert.IsGreaterThan(-10, budget.TokensLeft);
    }

    [TestMethod]
    public void OneHugeInlineImageDictionaryStopsAtTheTokenBudget()
    {
        var budget = new ContentBudget(1000);
        var reader = new ContentReader(Encoding.ASCII.GetBytes("BI " + string.Concat(Enumerable.Repeat("/K 1 ", 100_000)) + "ID x EI"), budget);

        Assert.IsFalse(reader.Next(new ContentOperation()));
        Assert.IsTrue(reader.BudgetExhausted);
        Assert.IsGreaterThan(-10, budget.TokensLeft);
    }

    [TestMethod]
    public void ActualTextCrossingThePageCharacterLimitIsKept()
    {
        byte[] input = Page("BT /F1 12 Tf 72 700 Td /Span <</ActualText(X)>> BDC (ab) Tj EMC ET");
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Default with { MaxTextCharsPerPage = 1 } });

        Assert.AreEqual("X", doc.Pages[0].ExtractText().Trim());
    }

    [TestMethod]
    public void SpentOperationBudgetStopsLaterPages()
    {
        byte[] input = Page("BT /F1 12 Tf (A) Tj ET");
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Default with { MaxOperationsPerDocument = 3 } });

        Assert.AreEqual("A", doc.Pages[0].ExtractText().Trim());
        Assert.AreEqual("", doc.Pages[0].ExtractText());
    }

    [TestMethod]
    public void OneHugeTextStringEndsAtTheTimeLimit()
    {
        byte[] input = Page("BT /F1 12 Tf (" + new string('a', 1_000_000) + ") Tj ET");
        using PdfDocument doc = PdfDocument.Open(input);

        // Opening is bound by the timeout too, so the document is opened without one and given it after.
        doc.Budget = new ResourceBudget(PdfLimits.Default with { Timeout = TimeSpan.FromTicks(1) }, input.Length, new PdfDiagnostics());
        PdfLimitExceededException e = Assert.ThrowsExactly<PdfLimitExceededException>(() => doc.Pages[0].ExtractText());

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    public void StreamEndingExactlyAtTheDecodeLimitReportsThePageLimit()
    {
        const int limit = 1000;
        byte[] input = Page(Encoding.ASCII.GetBytes(new string(' ', limit)), Encoding.ASCII.GetBytes("BT /F1 12 Tf (late) Tj ET"));
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { MaxDecodedStreamBytes = limit });

        Assert.AreEqual("", doc.Pages[0].ExtractText());
        PdfDiagnostic warning = doc.Diagnostics.Single();
        Assert.AreEqual(PdfDiagnosticKind.LimitReached, warning.Kind);
        Assert.AreEqual(PdfLimitKind.PageContent, warning.Limit);
    }
}
