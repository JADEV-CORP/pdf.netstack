using System.Text;
using PdfNetStack.Content;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Content read once and replayed: shared forms analysed per context from one reading, operands
/// kept as spans of the content, compaction straight from the bytes, and the proof that renders
/// paths only for a difference and lets the analysis run beside its walk.
/// </summary>
[TestClass]
public sealed class ContentReplayTests
{
    private static PdfFile Open(byte[] pdf, PdfLimits? limits = null) =>
        PdfFile.Open(pdf, 64 << 20, limits is null ? null : new ResourceBudget(limits, pdf.Length, new PdfDiagnostics()));

    private static ContentAnalysis Analyze(PdfFile file) =>
        ContentAnalysis.Run(file, ObjectGraph.Collect(file, false, false, CancellationToken.None).Order, [], 64 << 20, CancellationToken.None);

    private static string Type0(int descendant) =>
        $"<</Type/Font/Subtype/Type0/BaseFont/F/Encoding/Identity-H/DescendantFonts[{descendant} 0 R]>>";

    private const string Descendant = "<</Type/Font/Subtype/CIDFontType2/BaseFont/F/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>>>";

    /// <summary>
    /// Pages that draw one form showing text in its caller's font: under the page's composite
    /// font (A on even pages, B on odd ones), under a simple font, then under the first again,
    /// which is the same context and runs nothing new. Each page has resources of its own.
    /// </summary>
    private static byte[] SharedForm(string formContent, int pages = 2, bool flate = false)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        var kids = new StringBuilder();
        for (int i = 0; i < pages; i++) kids.Append(10 + i).Append(" 0 R ");
        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count {pages}>>");
        b.Set(3, Type0(4));
        b.Set(4, Descendant);
        b.Set(5, Type0(6));
        b.Set(6, Descendant);
        b.Set(7, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        byte[] form = Encoding.ASCII.GetBytes(formContent);
        b.SetStream(8, "/Type/XObject/Subtype/Form/BBox[0 0 10 10]" + (flate ? "/Filter/FlateDecode" : ""),
            flate ? Filters.StreamCodec.Deflate(form, System.IO.Compression.CompressionLevel.Fastest) : form);
        for (int i = 0; i < pages; i++)
        {
            b.Set(10 + i, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]/Contents {100 + i} 0 R/Resources<</Font<</A 3 0 R/B 5 0 R/S 7 0 R/Unused 7 0 R>>/XObject<</Fm 8 0 R>>>>>>");
            string font = i % 2 == 0 ? "A" : "B";
            b.SetStream(100 + i, "", Encoding.ASCII.GetBytes($"BT /{font} 1 Tf ET /Fm Do BT /S 1 Tf ET /Fm Do BT /{font} 2 Tf ET /Fm Do"));
        }

        return b.Build();
    }

    private static PdfDictionary Font(PdfFile file, int number) => file.ResolveDictionary(new PdfReference(number, 0))!;

    [TestMethod]
    public void AFormReplayedInEachFontRecordsTheCodesOfEach()
    {
        PdfFile file = Open(SharedForm("BT [<0001> 5 <00020003>] TJ <0004> Tj ( ) Tj ET"));
        ContentAnalysis analysis = Analyze(file);

        Assert.IsTrue(analysis.Complete);
        Assert.IsTrue(analysis.GlyphUsageComplete);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, analysis.IdentityCodes[Font(file, 3)].ToArray());
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, analysis.IdentityCodes[Font(file, 5)].ToArray(), "The second page replays the form under B.");

        // The one byte string makes both fonts unreliable, on the first reading and on the replay.
        Assert.Contains(Font(file, 3), analysis.UnreliableFonts);
        Assert.Contains(Font(file, 5), analysis.UnreliableFonts);
        // Each page's fonts: the font no content names may go, whichever run read the form.
        List<PdfDictionary> fonts = analysis.CategoryDictionaries("Font").ToList();
        Assert.HasCount(2, fonts);
        foreach (PdfDictionary c in fonts)
        {
            Assert.DoesNotContain("Unused", analysis.PrunableCategories[c]);
            Assert.Contains("S", analysis.PrunableCategories[c]);
        }
    }

    [TestMethod]
    public void AReplayStopsWhereTheFormStoppedReading()
    {
        // The form's content holds a stray delimiter: every run of it, read or replayed, stops there.
        PdfFile file = Open(SharedForm("BT <0001> Tj ) <0002> Tj ET"));
        ContentAnalysis analysis = Analyze(file);

        Assert.IsTrue(analysis.Complete);
        Assert.IsFalse(analysis.GlyphUsageComplete);
        CollectionAssert.AreEquivalent(new[] { 1 }, analysis.IdentityCodes[Font(file, 3)].ToArray());
        CollectionAssert.AreEquivalent(new[] { 1 }, analysis.IdentityCodes[Font(file, 5)].ToArray());
    }

    [TestMethod]
    public void ReplaysChargeTheOperatorsOfEveryRun()
    {
        // 300 operators a run, two runs a page, four pages: 2,400 operators, past a limit of
        // 2,000 only when every replay counts as a reading would.
        byte[] input = SharedForm(string.Concat(Enumerable.Repeat("0 0 m ", 300)), pages: 4);

        ContentAnalysis unbounded = Analyze(Open(input));
        Assert.IsTrue(unbounded.Complete);
        Assert.IsNotEmpty(unbounded.PrunableCategories);

        ContentAnalysis bounded = Analyze(Open(input, PdfLimits.Default with { MaxOperationsPerDocument = 2000 }));
        Assert.IsFalse(bounded.Complete);
        Assert.IsEmpty(bounded.PrunableCategories);
    }

    [TestMethod]
    public void ReplaysChargeTheBytesDecodingWouldHave()
    {
        // The form decodes to 64 KiB; two runs a page on three pages decode 384 KiB, past a
        // budget of 256 KiB only when replays count as decodes. The fifth run fails inside the
        // page that draws it, as a decode failing there always has.
        string form = "% " + new string('x', (64 * 1024) - 3) + "\n";
        byte[] input = SharedForm(form, pages: 3, flate: true);

        Assert.IsTrue(Analyze(Open(input)).GlyphUsageComplete);
        ContentAnalysis bounded = Analyze(Open(input, PdfLimits.Default with { MaxTotalDecodedBytes = 256 * 1024 }));
        Assert.IsFalse(bounded.GlyphUsageComplete);
    }

    [TestMethod]
    public void OperandsAreReadOnlyByTheOperatorsThatNeedThem()
    {
        // A name in a number's place, a dictionary in a text array and a reference-like run all
        // read as the object reader reads them.
        PdfFile file = Open(SharedForm("BT 1 0 R Tj [<0005> <</A <0006>>> [<0007>]] TJ /F 3 Tf ET"));
        ContentAnalysis analysis = Analyze(file);
        CollectionAssert.AreEquivalent(new[] { 5, 7 }, analysis.IdentityCodes[Font(file, 3)].ToArray());
        CollectionAssert.AreEquivalent(new[] { 5, 7 }, analysis.IdentityCodes[Font(file, 5)].ToArray());
        Assert.IsTrue(analysis.GlyphUsageComplete, "/F names no font of the form's context; that is the Missing font, not a failure.");
    }

    [TestMethod]
    [DataRow("1 +2.500 -0.0 .50 007 Td", "1 2.5 0 .5 7 Td")]
    [DataRow("/A#42 /Na#20me /#2F Do", "/AB/Na#20me/#2F Do")]
    [DataRow("(a\\(b\\)c) Tj (\\101) Tj <4142> Tj", "(a(b)c)Tj(A)Tj(AB)Tj")]
    public void CompactionCanonicalizesFromTheBytes(string content, string expected)
    {
        string padded = " " + content + " ";
        byte[]? compacted = ContentCompactor.Compact(Encoding.ASCII.GetBytes(padded));
        Assert.IsNotNull(compacted);
        Assert.AreEqual(" " + expected + "\n", Encoding.ASCII.GetString(compacted));
        Assert.IsNull(ContentCompactor.FindDifference(Encoding.ASCII.GetBytes(padded), compacted));
    }

    [TestMethod]
    public void LongTokensCompactAndCompareOffTheStack()
    {
        string number = "000" + new string('7', 400) + ".500";
        string name = "/" + new string('n', 300) + "#41";
        string text = "(" + new string('s', 2000) + "\\101)";
        string content = $" {number} {name} {text} Tj ";
        byte[]? compacted = ContentCompactor.Compact(Encoding.ASCII.GetBytes(content));
        Assert.IsNotNull(compacted);
        string expected = $" {new string('7', 400)}.5/{new string('n', 300)}A({new string('s', 2000)}A)Tj\n";
        Assert.AreEqual(expected, Encoding.ASCII.GetString(compacted));
        Assert.IsNull(ContentCompactor.FindDifference(Encoding.ASCII.GetBytes(content), compacted));
        byte[] changed = Encoding.ASCII.GetBytes(expected.Replace("7.5/", "7.6/", StringComparison.Ordinal));
        Assert.AreEqual("token 0 differs", ContentCompactor.FindDifference(Encoding.ASCII.GetBytes(content), changed));
    }

    private static byte[] Document(string pageExtra, string fonts)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]/Contents 4 0 R/Resources<</Font<<{fonts}>>>>{pageExtra}>>");
        b.SetStream(4, "", "BT /F1 1 Tf (a) Tj ET"u8.ToArray());
        b.Set(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        return b.Build();
    }

    [TestMethod]
    public void ADifferenceNamesItsPathAsBefore()
    {
        byte[] source = Document("/Rotate 0/Extra[1 2[3 4]]", "/F1 5 0 R");
        byte[] output = Document("/Rotate 0/Extra[1 2[3 5]]", "/F1 5 0 R");
        Assert.AreEqual("trailer/Root/Pages/Kids[0]/Extra[2][1]: number differs",
            EquivalenceVerifier.Verify(Open(source), Open(output), new VerificationRules(), 64 << 20, 1, CancellationToken.None, out _));
    }

    [TestMethod]
    public void AMissingEntryContentUsesIsReportedBeforeLaterDifferences()
    {
        // F1 is shown, so dropping it is no pruning; the extra value further on differs too, and
        // the walk reports what it met first.
        byte[] source = Document("/Zed 1", "/F1 5 0 R/F2 5 0 R");
        byte[] output = Document("/Zed 2", "/F2 5 0 R");
        Assert.AreEqual("trailer/Root/Pages/Kids[0]/Resources/Font/F1: dictionary vs other",
            EquivalenceVerifier.Verify(Open(source), Open(output), VerificationRules.Lossless, 64 << 20, 2, CancellationToken.None, out _));

        // Without the later difference the same entry is still reported, after the walk.
        byte[] sameZed = Document("/Zed 1", "/F2 5 0 R");
        Assert.AreEqual("trailer/Root/Pages/Kids[0]/Resources/Font/F1: dictionary vs other",
            EquivalenceVerifier.Verify(Open(source), Open(sameZed), VerificationRules.Lossless, 64 << 20, 2, CancellationToken.None, out _));

        // An entry no content names may go.
        byte[] pruned = Document("/Zed 1", "/F1 5 0 R");
        Assert.IsNull(EquivalenceVerifier.Verify(Open(source), Open(pruned), VerificationRules.Lossless, 64 << 20, 2, CancellationToken.None, out _));
    }

    [TestMethod]
    public void StreamsCarriedOverUnchangedAreNotDecoded()
    {
        // The stream decodes to 4 MiB, past the documents' budget of 1 MiB: equal bytes under
        // equal filters prove it without decoding either side.
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Data 4 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]>>");
        b.SetStream(4, "/Filter/FlateDecode", Filters.StreamCodec.Deflate(new byte[4 << 20], System.IO.Compression.CompressionLevel.Fastest));
        byte[] input = b.Build();
        PdfLimits limits = PdfLimits.Default with { MaxTotalDecodedBytes = 1 << 20 };
        var budget = new ResourceBudget(limits, input.Length, new PdfDiagnostics());
        PdfFile source = PdfFile.Open(input, 64 << 20, budget), output = PdfFile.Open(input, 64 << 20, budget);
        Assert.IsNull(EquivalenceVerifier.Verify(source, output, new VerificationRules(), 64 << 20, 2, CancellationToken.None, out _));
        Assert.AreEqual(1 << 20, budget.DecodedBytesLeft);
    }
}
