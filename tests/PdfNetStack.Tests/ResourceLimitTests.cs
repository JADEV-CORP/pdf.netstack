using System.Diagnostics;
using System.Text;
using PdfNetStack.Content;

namespace PdfNetStack.Tests;

/// <summary>
/// The work a small file can cause is bounded: per page, per document, per call. Each test builds
/// its input in code, small enough to commit, from the shapes found in review.
/// </summary>
[TestClass]
public sealed class ResourceLimitTests
{
    private const string Font = "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>";

    /// <summary>One page drawing the given content streams, all objects in one file.</summary>
    private static byte[] Page(string resources, params (string Dictionary, byte[] Data)[] contents)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        var refs = new StringBuilder();
        for (int i = 0; i < contents.Length; i++)
        {
            b.SetStream(10 + i, contents[i].Dictionary, contents[i].Data);
            refs.Append(10 + i).Append(" 0 R ");
        }

        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<{resources}>>/Contents[{refs}]>>");
        return b.Build();
    }

    private static (string, byte[]) Flate(string content) => ("/Filter/FlateDecode", PdfBuilder.Deflate(Encoding.ASCII.GetBytes(content)));

    private static long Allocated() =>
#if NET8_0_OR_GREATER
        GC.GetTotalAllocatedBytes(precise: true);
#else
        GC.GetTotalMemory(false);
#endif

    [TestMethod]
    [DoNotParallelize] // allocation: other tests' allocations would land in the measurement
    public void DeepArrayNestingIsLinear()
    {
        // Past the nesting limit a run of brackets used to be parsed again from each bracket in
        // it: 500,000 of them took 87 s and 59 GB. Now the run is stepped over once.
        byte[] input = Page(Font, Flate(new string('[', 500_000)));
        using PdfDocument doc = PdfDocument.Open(input);

        long before = Allocated();
        var clock = Stopwatch.StartNew();
        Assert.AreEqual("", doc.Pages[0].ExtractText());
        long allocated = Allocated() - before;

        Assert.IsLessThan(64L << 20, allocated, $"allocated {allocated:N0} bytes");
        Assert.IsLessThan(10_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void TextAfterADeepArrayStillReads()
    {
        // A balanced run too deep to read is one damaged operand; the operators around it count.
        string content = "BT /F1 12 Tf 10 10 Td (x) Tj ET " + new string('[', 100_000) + new string(']', 100_000) + " TJ BT /F1 12 Tf 10 30 Td (y) Tj ET";
        using PdfDocument doc = PdfDocument.Open(Page(Font, Flate(content)));

        string text = doc.Pages[0].ExtractText();

        StringAssert.Contains(text, "x");
        StringAssert.Contains(text, "y");
    }

    [TestMethod]
    public void ContentReaderStopsAtItsTokenBudget()
    {
        // Unterminated arrays each re-read the numbers after them, a cost of brackets times
        // numbers; the budget counts every token read, re-reads included.
        byte[] data = Encoding.ASCII.GetBytes(new string('[', 100) + string.Concat(Enumerable.Repeat("1 ", 10_000)) + " Tj");
        var budget = new ContentBudget(50_000);
        var reader = new ContentReader(data, budget);
        var op = new ContentOperation();

        while (reader.Next(op))
        {
        }

        Assert.IsTrue(reader.BudgetExhausted);
        Assert.IsTrue(budget.Exhausted);
    }

    [TestMethod]
    public void ManyOperandsWithoutAnOperatorKeepTheNewest()
    {
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 100_000).Select(i => i + " ")) + "Tj");
        var reader = new ContentReader(data);
        var op = new ContentOperation();

        Assert.IsTrue(reader.Next(op));
        Assert.AreEqual("Tj", op.Operator);
        Assert.AreEqual(99999, ((Objects.PdfNumber)op.Operands[^1]).IntegerValue);
    }

    [TestMethod]
    public void PageWithinItsByteBudgetIsReadOnce()
    {
        // The page's bytes used to be counted when decoded and again when read, so a page between
        // half the budget and the budget came out empty.
        string content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET" + new string(' ', 3 << 20);
        byte[] input = Page(Font, Flate(content));
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { MaxDecodedStreamBytes = 4 << 20 });

        Assert.AreEqual("Hello", doc.Pages[0].ExtractText());
        Assert.IsEmpty(doc.Diagnostics);
    }

    [TestMethod]
    public void PageOverItsByteBudgetStopsAndSaysSo()
    {
        // The same stream named twice: the second copy no longer fits the page's budget, which
        // is given to the decoder, so it stops there instead of after allocating the copy.
        (string, byte[]) part = Flate("BT /F1 24 Tf 72 700 Td (Hello) Tj ET" + new string(' ', 3 << 20));
        byte[] input = Page(Font, part, part);
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { MaxDecodedStreamBytes = 4 << 20 });

        Assert.AreEqual("Hello", doc.Pages[0].ExtractText());
        PdfDiagnostic warning = doc.Diagnostics.Single();
        Assert.AreEqual(PdfDiagnosticKind.LimitReached, warning.Kind);
        Assert.AreEqual(PdfLimitKind.PageContent, warning.Limit);
        Assert.AreEqual(0, warning.PageIndex);
    }

    [TestMethod]
    public void DamagedContentStreamIsReportedWithItsObject()
    {
        byte[] input = Page(Font, ("/Filter/FlateDecode", [1, 2, 3, 4]), Flate("BT /F1 24 Tf 72 700 Td (Hello) Tj ET"));
        using PdfDocument doc = PdfDocument.Open(input);

        Assert.AreEqual("Hello", doc.Pages[0].ExtractText());
        PdfDiagnostic warning = doc.Diagnostics.Single();
        Assert.AreEqual(PdfDiagnosticKind.Skipped, warning.Kind);
        Assert.AreEqual(10, warning.ObjectNumber);
        Assert.AreEqual(0, warning.PageIndex);
    }

    [TestMethod]
    public void DecodedBytesAreBudgetedAcrossPages()
    {
        // 40 pages sharing one stream of 1 MiB: each page decodes it again. With an 8 MiB budget
        // the first pages read and the rest draw nothing, and the limit is reported once.
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.SetStream(4, "/Filter/FlateDecode", PdfBuilder.Deflate(Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td (Hello) Tj ET" + new string(' ', 1 << 20))));
        var kids = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            b.Set(10 + i, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<{Font}>>/Contents 4 0 R>>");
            kids.Append(10 + i).Append(" 0 R ");
        }

        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count 40>>");
        var warnings = new List<PdfDiagnostic>();
        var options = new PdfLoadOptions { Limits = PdfLimits.Default with { MaxTotalDecodedBytes = 8 << 20 }, OnWarning = warnings.Add };
        using PdfDocument doc = PdfDocument.Open(b.Build(), options);

        string[] texts = doc.Pages.Select(p => p.ExtractText()).ToArray();

        Assert.AreEqual("Hello", texts[0]);
        Assert.AreEqual("", texts[^1]);
        Assert.IsLessThan(9, texts.Count(t => t.Length > 0));
        PdfDiagnostic warning = doc.Diagnostics.Single();
        Assert.AreEqual(PdfLimitKind.TotalDecodedBytes, warning.Limit);
        CollectionAssert.AreEqual(doc.Diagnostics.ToList(), warnings);
    }

    [TestMethod]
    public void DefaultDecodeBudgetScalesWithTheFile()
    {
        Assert.AreEqual((1L << 30) + (4096L * 1000), PdfLimits.Default.DecodedBytesFor(1000));
        Assert.AreEqual(long.MaxValue, PdfLimits.Default.DecodedBytesFor(long.MaxValue / 2));
        Assert.AreEqual(1L << 30, PdfLimits.Untrusted.DecodedBytesFor(1L << 40));
        Assert.IsLessThanOrEqualTo(PdfLimits.Default.ImagePixelsFor(1), PdfLimits.Untrusted.ImagePixelsFor(1));
    }

    [TestMethod]
    public void TextPerPageIsBounded()
    {
        // A ToUnicode range whose destination is 8000 characters: every glyph repeated it, so 60,000
        // glyphs made a string of 480 million. Destinations are cut at the 512 bytes the standard
        // allows, and the page's text at the limit.
        string cmap = "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <" + new string('4', 4 * 8000) + "> endbfrange endcmap";
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 5 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "/Filter/FlateDecode", PdfBuilder.Deflate(Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 10 Td <" + string.Concat(Enumerable.Repeat("0001", 60_000)) + "> Tj ET")));
        b.Set(5, "<</Type/Font/Subtype/Type0/BaseFont/X/Encoding/Identity-H/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/X/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/W[0[1000]]>>]/ToUnicode 6 0 R>>");
        b.SetStream(6, "", Encoding.ASCII.GetBytes(cmap));
        byte[] input = b.Build();

        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Untrusted });
        IReadOnlyList<PdfLetter> letters = doc.Pages[0].GetLetters();

        Assert.AreEqual(256, letters[0].Text.Length);
        Assert.IsLessThanOrEqualTo(PdfLimits.Untrusted.MaxTextCharsPerPage, letters.Sum(l => l.Text.Length));
        Assert.AreEqual(PdfLimitKind.TextCharsPerPage, doc.Diagnostics.Single().Limit);
    }

    [TestMethod]
    [DoNotParallelize] // allocation: other tests' allocations would land in the measurement
    public void InlineImageOverTheCeilingIsRefusedBeforeDecoding()
    {
        // 16384 x 16384 RGB declares 805 MB of samples: past the 256 MiB ceiling, so it is refused
        // from its dictionary. It used to decode 705 MB first.
        byte[] zeros = PdfBuilder.Deflate(new byte[8 << 20]);
        string content = "q 100 0 0 100 0 0 cm BI /W 16384 /H 16384 /CS /RGB /BPC 8 /F /Fl ID " + Compat.Latin1.GetString(zeros) + " EI Q";
        using PdfDocument doc = PdfDocument.Open(Page("", ("", Compat.Latin1.GetBytes(content))));
        PdfPageImage image = doc.Pages[0].GetImages().Single();

        long before = Allocated();
        Assert.ThrowsExactly<PdfFormatException>(() => image.Decode());
        long allocated = Allocated() - before;

        Assert.IsLessThan(4L << 20, allocated, $"allocated {allocated:N0} bytes");
    }

    [TestMethod]
    [DoNotParallelize] // allocation: other tests' allocations would land in the measurement
    public void RawImageDecodesIntoOneArray()
    {
        // Samples of a known size are decoded into one array of that size, which becomes the
        // pixels: 12 MB of RGB used to take 40 MB through growing buffers and two copies.
        const int side = 2000;
        byte[] samples = new byte[side * side * 3];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im 5 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "q 612 0 0 792 0 0 cm /Im Do Q"u8.ToArray());
        b.SetStream(5, $"/Type/XObject/Subtype/Image/Width {side}/Height {side}/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/FlateDecode", PdfBuilder.Deflate(samples));
        using PdfDocument doc = PdfDocument.Open(b.Build());
        PdfPageImage image = doc.Pages[0].GetImages().Single();

        long before = Allocated();
        PdfBitmap bitmap = image.Decode()!;
        long allocated = Allocated() - before;

        Assert.AreEqual(side, bitmap.Width);
        Assert.IsLessThan(samples.Length + (4L << 20), allocated, $"allocated {allocated:N0} bytes");
    }

    [TestMethod]
    public void ImagePixelsAreBudgetedAcrossImages()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</A 5 0 R/B 6 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "/A Do /B Do"u8.ToArray());
        b.SetStream(5, "/Type/XObject/Subtype/Image/Width 100/Height 100/ColorSpace/DeviceGray/BitsPerComponent 8", new byte[10_000]);
        b.SetStream(6, "/Type/XObject/Subtype/Image/Width 100/Height 100/ColorSpace/DeviceGray/BitsPerComponent 8", new byte[10_000]);
        using PdfDocument doc = PdfDocument.Open(b.Build(), new PdfLoadOptions { Limits = PdfLimits.Default with { MaxTotalImagePixels = 15_000 } });
        IReadOnlyList<PdfPageImage> images = doc.Pages[0].GetImages();

        Assert.IsNotNull(images[0].Decode());
        PdfLimitExceededException e = Assert.ThrowsExactly<PdfLimitExceededException>(() => images[1].Decode());
        Assert.AreEqual(PdfLimitKind.TotalImagePixels, e.Limit);
        Assert.AreEqual(PdfLimitKind.TotalImagePixels, doc.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public void InputOverTheLimitIsRefusedBeforeItIsRead()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        var options = new PdfLoadOptions { Limits = PdfLimits.Default with { MaxInputBytes = input.Length - 1 } };

        Assert.AreEqual(PdfLimitKind.InputBytes, Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(input.AsMemory(), options)).Limit);
        Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(new MemoryStream(input), options));
        Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(new MemoryStream(input), options with { Mode = PdfLoadMode.OnDemand }));
        Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfInspector.Inspect(input, options.Limits));
        using PdfDocument fits = PdfDocument.Open(input, options with { Limits = PdfLimits.Default with { MaxInputBytes = input.Length } });
        Assert.AreEqual("x", fits.Pages[0].ExtractText());

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Limits = options.Limits });
        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(PdfLimitKind.InputBytes, result.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public void ExtractionEndsAtTheTimeLimit()
    {
        // The clock is read every 1024 operators; a page of more than that ends at once.
        byte[] input = Page(Font, Flate(string.Concat(Enumerable.Repeat("1 0 0 1 0 0 cm ", 5000))));
        using PdfDocument doc = PdfDocument.Open(input);

        // Opening is bound by the timeout too, so the document is opened without one and given it after.
        doc.Budget = new ResourceBudget(PdfLimits.Default with { Timeout = TimeSpan.FromTicks(1) }, input.Length, new PdfDiagnostics());
        PdfLimitExceededException e = Assert.ThrowsExactly<PdfLimitExceededException>(() => doc.Pages[0].ExtractText());

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    public void OperatorsAreBudgetedAcrossTheDocument()
    {
        byte[] input = Page(Font, Flate(string.Concat(Enumerable.Repeat("1 0 0 1 0 0 cm ", 5000)) + "BT /F1 24 Tf 72 700 Td (late) Tj ET"));
        using PdfDocument doc = PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Default with { MaxOperationsPerDocument = 2000 } });

        Assert.AreEqual("", doc.Pages[0].ExtractText());
        Assert.AreEqual(PdfLimitKind.OperationsPerDocument, doc.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public void OptimizerKeepsTheInputWhenDecodingRunsOverBudget()
    {
        // Streams that each decode to 2 MiB from a few hundred bytes: the budget of the run is
        // spent, by the first passes or the proof, and the input is returned unchanged.
        var b = PdfBuilder.Simple("x");
        var refs = new StringBuilder();
        byte[] inner = PdfBuilder.Deflate(new byte[2 << 20]);
        for (int i = 0; i < 8; i++) refs.Append(b.AddStream("/Filter[/FlateDecode/FlateDecode]", PdfBuilder.Deflate(inner))).Append(" 0 R ");
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/X[{refs}]>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Limits = PdfLimits.Default with { MaxTotalDecodedBytes = 8 << 20 } });

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(PdfLimitKind.TotalDecodedBytes, result.Diagnostics.Single().Limit);

        (_, PdfOptimizationResult streamed) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Streaming = true, Limits = PdfLimits.Default with { MaxTotalDecodedBytes = 8 << 20 } });
        Assert.AreEqual(OptimizationDisposition.LimitExceeded, streamed.Disposition, streamed.Reason);
    }

    [TestMethod]
    public void OptimizerKeepsTheInputAtTheTimeLimit()
    {
        byte[] input = PdfBuilder.Simple(Enumerable.Range(0, 50).Select(i => "page " + i).ToArray()).Build();

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Limits = PdfLimits.Default with { Timeout = TimeSpan.FromTicks(1) } });

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(PdfLimitKind.Timeout, result.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public void OptimizerNeverSwallowsOutOfMemory()
    {
        using var guard = new PdfOptimizer.RunGuard(PdfLimits.Default, 0, default, CancellationToken.None);

        Assert.IsNull(guard.Failure(new OutOfMemoryException()));
        Assert.IsNull(guard.Failure(new AggregateException(new InvalidOperationException(), new OutOfMemoryException())));
        Assert.IsNull(guard.Failure(new OperationCanceledException()));
        Assert.AreEqual(OptimizationDisposition.LimitExceeded, guard.Failure(new AggregateException(new PdfLimitExceededException(PdfLimitKind.TotalDecodedBytes, "spent")))?.Disposition);
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, guard.Failure(new InvalidOperationException("damaged"))?.Disposition);
    }

    [TestMethod]
    public void PageTreeNestedTooDeepIsReported()
    {
        // 50,000 nested /Pages opened with no pages and said nothing; the optimizer then copied
        // a path string per level, 95 GB in all.
        const int depth = 2000;
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        for (int i = 0; i < depth; i++) b.Set(2 + i, $"<</Type/Pages/Kids[{3 + i} 0 R]/Count 1>>");
        b.Set(2 + depth, $"<</Type/Page/Parent {1 + depth} 0 R/MediaBox[0 0 612 792]>>");
        byte[] input = b.Build();

        using PdfDocument doc = PdfDocument.Open(input);
        Assert.IsEmpty(doc.Pages);
        Assert.IsTrue(doc.Diagnostics.Any(d => d.Kind == PdfDiagnosticKind.Skipped), string.Join("; ", doc.Diagnostics));

        PdfInspection inspection = PdfInspector.Inspect(input);
        Assert.AreEqual(0, inspection.PageCount);
        Assert.IsTrue(inspection.Diagnostics.Any(d => d.Kind == PdfDiagnosticKind.Skipped));

        long before = Allocated();
        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 0, MaxDegreeOfParallelism = 1 });
        Assert.IsLessThan(256L << 20, Allocated() - before, result.Reason);
    }

    [TestMethod]
    public void InspectorCountsPagesRatherThanTrustingCount()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(2, "<</Type/Pages/Kids[5 0 R 2 0 R]/Count 1000000000>>");
        byte[] input = b.Build();

        PdfInspection inspection = PdfInspector.Inspect(input);

        Assert.AreEqual(1, inspection.PageCount);
        Assert.IsTrue(inspection.Diagnostics.Any(d => d.Kind == PdfDiagnosticKind.Repaired));
        Assert.AreEqual(1, PdfOptimizer.Optimize(input).Result.PageCount);
    }

    [TestMethod]
    public void ExceptionsShareOneBase()
    {
        Assert.IsTrue(typeof(PdfException).IsAssignableFrom(typeof(PdfFormatException)));
        Assert.IsTrue(typeof(PdfException).IsAssignableFrom(typeof(PdfPasswordException)));
        Assert.IsTrue(typeof(PdfFormatException).IsAssignableFrom(typeof(PdfLimitExceededException)));
        Assert.IsTrue(typeof(Exception).IsAssignableFrom(typeof(PdfException)));
    }

    [TestMethod]
    public void LimitsAreValidated()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Default with { MaxTotalDecodedBytes = 0 } }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfDocument.Open(input, new PdfLoadOptions { Limits = PdfLimits.Default with { Timeout = TimeSpan.Zero } }));
        Assert.ThrowsExactly<ArgumentNullException>(() => PdfDocument.Open(input, new PdfLoadOptions { Limits = null! }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Limits = PdfLimits.Default with { MaxTextCharsPerPage = 0 } }));
    }
}
