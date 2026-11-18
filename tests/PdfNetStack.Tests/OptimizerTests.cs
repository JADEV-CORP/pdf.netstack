using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class OptimizerTests
{
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static (byte[] Output, PdfOptimizationResult Result) Run(byte[] input, PdfOptimizerOptions? options = null) =>
        PdfOptimizer.Optimize(input, options ?? NoThreshold);

    private static PdfFile Open(byte[] pdf) => PdfFile.Open(pdf, 64 << 20);

    private static void AssertOptimizedAndEquivalent(byte[] input, byte[] output, PdfOptimizationResult result)
    {
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsLessThan(input.Length, output.Length);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
        PdfFile file = Open(output);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(0, file.LengthsRepaired);
    }

    private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

    [TestMethod]
    public void SimpleDocumentIsRewrittenWithObjectStreams()
    {
        PdfBuilder b = PdfBuilder.Simple("one", "two", "three");
        b.SetStream(4, "", Repeat("0 0 m 100 100 l S\n", 200));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(3, result.PageCount);
        string text = Compat.Latin1.GetString(output);
        StringAssert.StartsWith(text, "%PDF-1.5");
        StringAssert.Contains(text, "/Type/ObjStm");
        StringAssert.Contains(text, "/Type/XRef");
        Assert.AreEqual(result.OutputSha256, Compat.ToHex(Compat.Sha256(output)).ToLowerInvariant());
    }

    [TestMethod]
    public void ObjectStreamsHoldAlikeObjectsTogetherInNumberOrder()
    {
        // Annotations and fonts alternate in the order given; packed, each kind is contiguous.
        var objects = new List<PdfObject>();
        for (int i = 0; i < 6; i++)
        {
            objects.Add(new PdfDictionary { ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Link") });
            objects.Add(new PdfDictionary { ["Type"] = new PdfName("Font"), ["BaseFont"] = new PdfName("F" + i) });
        }

        objects.Add(PdfArray.Of(1, 2, 3));
        List<int> grouped = Writing.PdfDocumentWriter.GroupByKind([.. Enumerable.Range(1, objects.Count)], objects);
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 7, 9, 11, 2, 4, 6, 8, 10, 12, 13 }, grouped);

        // In a rewrite, the objects of each object stream are numbered one after another, so the
        // cross-reference rows stay regular, and the file proves equal to its source.
        PdfBuilder b = PdfBuilder.Simple("one", "two", "three", "four", "five");
        b.SetStream(4, "", Repeat("0 0 m 100 100 l S\n", 200));
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { ObjectsPerStream = 4 });
        AssertOptimizedAndEquivalent(input, output, result);
        PdfFile file = Open(output);
        int streams = 0;
        foreach ((int number, XrefEntry entry) in file.Xref)
        {
            if (entry.Type != XrefEntryType.InFile || file.GetObject(number) is not PdfStream { } stream || stream.Dictionary.GetName("Type") != "ObjStm") continue;
            streams++;
            using Filters.DecodedStream decoded = Filters.StreamCodec.Decode(stream.Data, Filters.StreamCodec.GetFilters(stream.Dictionary, file.Resolve), Filters.DecodeDepth.Full, 1 << 20, file.Resolve);
            int first = (int)((PdfNumber)stream.Dictionary["First"]!).ToDouble();
            int[] header = [.. Encoding.ASCII.GetString(decoded.Data.Span[..first]).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
            for (int k = 2; k < header.Length; k += 2) Assert.AreEqual(header[k - 2] + 1, header[k]);
        }

        Assert.IsGreaterThan(1, streams);
    }

    [TestMethod]
    public void BytesInAreNeverTheBytesOut()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 99 });
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreNotSame(input, output, "The caller's array is never handed back.");
    }

    [TestMethod]
    public void WorkersWaitForTheMemoryGate()
    {
        var gate = new StreamRecompressor.MemoryGate(100);

        // A share larger than the budget is cut to it and runs alone.
        long whole = gate.Enter(1000, CancellationToken.None);
        Assert.AreEqual(100, whole);
        Task<long> waiting = Task.Run(() => gate.Enter(10, CancellationToken.None));
        Assert.IsFalse(waiting.Wait(300), "A second share waits while the budget is spent.");
        gate.Exit(whole);
        Assert.IsTrue(waiting.Wait(10_000));
        Assert.AreEqual(10, waiting.Result);
        gate.Exit(waiting.Result);
        Assert.AreEqual(0, gate.Enter(0, CancellationToken.None));
    }

    [TestMethod]
    public void UnreachableObjectsAreRemoved()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.AddStream("", Repeat("orphan data that nothing references ", 300));
        b.Add("<</Orphan true>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(2, result.Statistics.UnreachableRemoved);
        Assert.AreEqual(-1, Compat.Latin1.GetString(output).IndexOf("Orphan", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IdenticalImagesAndFontsAreMerged()
    {
        var b = new PdfBuilder();
        byte[] pixels = new byte[64 * 64 * 3];
        new Random(3).NextBytes(pixels);
        string imageDict = "/Type/XObject/Subtype/Image/Width 64/Height 64/ColorSpace/DeviceRGB/BitsPerComponent 8";
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Resources<</XObject<</Im0 5 0 R>>/Font<</F1 9 0 R>>>>/Contents 7 0 R>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Resources<</XObject<</Im0 6 0 R>>/Font<</F1 10 0 R>>>>/Contents 8 0 R>>");
        b.SetStream(5, imageDict, pixels);
        b.SetStream(6, imageDict, pixels);
        b.SetStream(7, "", "q 64 0 0 64 0 0 cm /Im0 Do Q BT /F1 12 Tf (a) Tj ET"u8.ToArray());
        b.SetStream(8, "", "q 32 0 0 32 0 0 cm /Im0 Do Q BT /F1 12 Tf (b) Tj ET"u8.ToArray());
        b.Set(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(2, result.Statistics.ObjectsDeduplicated);
        Assert.IsLessThan((int)(pixels.Length * 1.2), output.Length);
    }

    [TestMethod]
    public void PagesAndAnnotationsWithEqualContentAreNeverMerged()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Annots[5 0 R 6 0 R]>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]>>");
        b.Set(5, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/Border[0 0 0]>>");
        b.Set(6, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/Border[0 0 0]>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 100));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(0, result.Statistics.ObjectsDeduplicated);
        PdfFile file = Open(output);
        var pages = (PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!;
        Assert.AreNotEqual(((PdfReference)pages[0]).Number, ((PdfReference)pages[1]).Number);
        var annots = (PdfArray)file.ResolveDictionary(pages[0])!["Annots"]!;
        Assert.AreNotEqual(((PdfReference)annots[0]).Number, ((PdfReference)annots[1]).Number);
    }

    [TestMethod]
    public void TextEncodingsAndLzwBecomeFlate()
    {
        byte[] content = Repeat("BT /F1 12 Tf 10 10 Td (repeated text) Tj ET\n", 100);
        string hex = Compat.ToHex(content) + ">";
        PdfBuilder b = PdfBuilder.Simple("x");
        b.SetStream(4, "/Filter/ASCIIHexDecode", Encoding.ASCII.GetBytes(hex));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        PdfFile file = Open(output);
        var stream = (PdfStream)file.Resolve(file.ResolveDictionary(file.GetObject(FindPage(file)))!["Contents"])!;
        Assert.AreEqual("FlateDecode", stream.Dictionary.GetName("Filter"));
    }

    [TestMethod]
    public void RawImageGetsPngPredictorWhenSmaller()
    {
        var b = PdfBuilder.Simple("x");
        var pixels = new byte[200 * 100 * 3];
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                int o = (y * 200 + x) * 3;
                pixels[o] = (byte)(x + y);
                pixels[o + 1] = (byte)(x * 2);
                pixels[o + 2] = (byte)(y * 3);
            }
        }

        int image = b.AddStream("/Type/XObject/Subtype/Image/Width 200/Height 100/ColorSpace/DeviceRGB/BitsPerComponent 8", pixels);
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</I {image} 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "q 200 0 0 100 0 0 cm /I Do Q"u8.ToArray());
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        StringAssert.Contains(Compat.Latin1.GetString(output), "/Predictor 15");
    }

    [TestMethod]
    [DataRow(10, true)]
    [DataRow(2, true)]
    [DataRow(15, false)]
    public void ProducerPredictorsAreChosenAgain(int predictor, bool streaming)
    {
        // Every row is one noisy pattern, a step brighter than the row above: the Up filter leaves
        // almost nothing, while the producer's rows (PNG filter None, or TIFF differences along
        // the row) keep the noise.
        const int width = 300, height = 80;
        var pattern = new byte[width];
        new Random(4).NextBytes(pattern);
        var samples = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) samples[(y * width) + x] = (byte)(pattern[x] + y);
        }

        byte[] predicted;
        if (predictor == 2)
        {
            predicted = (byte[])samples.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = width - 1; x > 0; x--) predicted[(y * width) + x] -= samples[(y * width) + x - 1];
            }
        }
        else
        {
            predicted = new byte[height * (width + 1)];
            for (int y = 0; y < height; y++) samples.AsSpan(y * width, width).CopyTo(predicted.AsSpan((y * (width + 1)) + 1));
        }

        var b = PdfBuilder.Simple("x");
        int image = b.AddStream($"/Type/XObject/Subtype/Image/Width {width}/Height {height}/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/FlateDecode/DecodeParms<</Predictor {predictor}/Columns {width}>>", PdfBuilder.Deflate(predicted));
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</I {image} 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "q 300 0 0 80 0 0 cm /I Do Q"u8.ToArray());
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { Streaming = streaming });

        AssertOptimizedAndEquivalent(input, output, result);
        PdfFile file = Open(output);
        var page = file.ResolveDictionary(file.GetObject(FindPage(file)))!;
        var written = (PdfStream)file.Resolve(file.ResolveDictionary(file.ResolveDictionary(page["Resources"])!["XObject"])!["I"])!;
        Assert.AreEqual(15.0, ((PdfNumber)((PdfDictionary)written.Dictionary["DecodeParms"]!)["Predictor"]!).ToDouble(), "Rows predicted afresh win.");
        Assert.IsLessThan(Filters.StreamCodec.Deflate(predicted, Compat.MaximumCompression).Length, written.Data.Length, "Smaller than the producer's rows at the same level.");
    }

    [TestMethod]
    public void JpegBytesAreNeverTouched()
    {
        var b = PdfBuilder.Simple("x");
        var jpeg = new byte[5000];
        new Random(9).NextBytes(jpeg);
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        int image = b.AddStream("/Type/XObject/Subtype/Image/Width 10/Height 10/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode", jpeg);
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</I {image} 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "q 200 0 0 100 0 0 cm /I Do Q"u8.ToArray());
        b.AddStream("", Repeat("padding ", 500));
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/PieceInfo<</X<</Private 11 0 R>>>>>>");
        b.SetStream(11, "", Repeat("padding ", 500));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.IsGreaterThanOrEqualTo(0, output.AsSpan().IndexOf(jpeg));
    }

    [TestMethod]
    public void EncryptedFileIsReturnedUnchanged()
    {
        PdfBuilder b = PdfBuilder.Simple("secret");
        b.Add("<</Filter/Standard/V 2/R 3/O(x)/U(y)/P -4>>");
        byte[] input = b.Build(trailerExtra: "/Encrypt 6 0 R/ID[<01><01>]");

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(result.SourceSha256, result.OutputSha256);
    }

    [TestMethod]
    public void SignedFileIsReturnedUnchanged()
    {
        PdfBuilder b = PdfBuilder.Simple("signed");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[7 0 R]/SigFlags 3>>>>");
        b.Set(7, "<</FT/Sig/T(Signature1)/V 8 0 R/Rect[0 0 0 0]>>");
        b.Set(8, "<</Type/Sig/Filter/Adobe.PPKLite/Contents<00>/ByteRange[0 1 2 3]>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void SignatureInsideObjectStreamIsStillDetected()
    {
        PdfBuilder b = PdfBuilder.Simple("signed");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[7 0 R]>>>>");
        b.Set(7, "<</FT/Sig/T(S)/V 8 0 R>>");
        b.Set(8, "<</Type/Sig/Contents<00>>>");
        byte[] input = b.BuildWithXrefStream(new HashSet<int> { 1, 2, 7, 8 });

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
    }

    private const string UsageRightsSignature =
        "<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached/Contents<00>/ByteRange[0 1 2 3]" +
        "/Reference[<</Type/SigRef/TransformMethod/UR3/TransformParams<</Type/TransformParams/V/2.2/Form[/FillIn]>>>>]>>";

    /// <summary>A file whose catalog carries usage rights, the way Reader Extensions writes them.</summary>
    private static PdfBuilder UsageRightsFile(string perms = "/UR3 7 0 R")
    {
        PdfBuilder b = PdfBuilder.Simple("enabled");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Perms<<{perms}>>>>");
        b.Set(7, UsageRightsSignature);
        return b;
    }

    [TestMethod]
    public void UsageRightsAreKeptByDefault()
    {
        byte[] input = UsageRightsFile().Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UsageRightsAreRemovedOnRequest(bool streaming)
    {
        byte[] input = UsageRightsFile().Build();
        PdfOptimizerOptions options = NoThreshold with { RemoveUsageRights = true, Streaming = streaming };

        byte[] output;
        PdfOptimizationResult result;
        if (streaming)
        {
            using var source = new MemoryStream(input);
            using var sink = new MemoryStream();
            result = PdfOptimizer.OptimizeAsync(source, sink, options).GetAwaiter().GetResult();
            output = sink.ToArray();
            Assert.IsTrue(result.Statistics.Streamed);
        }
        else
        {
            (output, result) = Run(input, options);
        }

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsLessThan(input.Length, output.Length);
        StringAssert.Contains(result.Reason, "Usage rights removed");
        Assert.AreEqual(1, result.Statistics.EntriesStripped);

        // The usage rights are the one difference: the plain check reports them, and nothing else
        // differs once they are allowed to be missing.
        StringAssert.Contains(PdfEquivalence.FindDifference(input, output), "Perms");
        var rules = VerificationRules.Lossless with { IgnoredSourceKeys = ["Perms"] };
        Assert.IsNull(EquivalenceVerifier.Verify(Open(input), Open(output), rules, 64 << 20, 1, CancellationToken.None, out _));

        PdfFile file = Open(output);
        Assert.IsFalse(file.WasReconstructed);
        Assert.IsNull(file.ResolveDictionary(file.Trailer["Root"])!["Perms"]);
        Assert.AreEqual(-1, output.AsSpan().IndexOf("/ByteRange"u8));
        Assert.AreEqual(1, PdfDocument.Open(output).Pages.Count);
    }

    /// <summary>
    /// Only usage rights are removed: a certification (/DocMDP) or any other signature the file's
    /// bytes hold, reachable or not, keeps the file as it is.
    /// </summary>
    [TestMethod]
    [DataRow("certified")]
    [DataRow("signed field")]
    [DataRow("unreachable signature")]
    public void FilesWithOtherSignaturesKeepTheirUsageRights(string other)
    {
        PdfBuilder b = other == "certified" ? UsageRightsFile("/UR3 7 0 R/DocMDP 8 0 R") : UsageRightsFile();
        b.Set(8, "<</Type/Sig/Filter/Adobe.PPKLite/Contents<00>/ByteRange[0 1 2 3]>>");
        if (other == "signed field")
        {
            b.Set(1, "<</Type/Catalog/Pages 2 0 R/Perms<</UR3 7 0 R>>/AcroForm<</Fields[9 0 R]/SigFlags 3>>>>");
            b.Set(9, "<</FT/Sig/T(Signature1)/V 8 0 R/Rect[0 0 0 0]>>");
        }

        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { RemoveUsageRights = true });

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void IncrementalUpdatesAreRetainedByDefaultAndFlattenedOnRequest()
    {
        PdfBuilder b = PdfBuilder.Simple("Old");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] v1 = b.Build();
        byte[] v2 = PdfBuilder.AppendUpdate(v1, new Dictionary<int, string>
        {
            [6] = "<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream",
            [5] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 6 0 R>>",
        }, size: 7);

        (byte[] retained, PdfOptimizationResult r1) = Run(v2);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, r1.Disposition);
        CollectionAssert.AreEqual(v2, retained);

        (byte[] flattened, PdfOptimizationResult r2) = Run(v2, NoThreshold with { AllowIncrementalUpdates = true });
        AssertOptimizedAndEquivalent(v2, flattened, r2);
        PdfFile file = Open(flattened);
        Assert.AreEqual(1, file.RevisionCount);
        var page = file.ResolveDictionary(file.GetObject(FindPage(file)))!;
        StringAssert.Contains(Encoding.ASCII.GetString(((PdfStream)file.Resolve(page["Contents"])!).Data.Span), "(New)");
    }

    [TestMethod]
    public void PdfA1KeepsClassicXrefAndVersion()
    {
        PdfBuilder b = PdfBuilder.Simple("archival");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        const string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF><rdf:Description xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' pdfaid:part='1' pdfaid:conformance='B'/></rdf:RDF></x:xmpmeta>";
        int meta = b.AddStream("/Type/Metadata/Subtype/XML", Encoding.ASCII.GetBytes(xmp));
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Metadata {meta} 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        string text = Compat.Latin1.GetString(output);
        StringAssert.StartsWith(text, "%PDF-1.4");
        Assert.AreEqual(-1, text.IndexOf("/ObjStm", StringComparison.Ordinal));
        StringAssert.Contains(text, "xref\n0 ");
        StringAssert.Contains(text, "pdfaid:part='1'");
    }

    [TestMethod]
    [DataRow("1", false, false)]
    [DataRow("2", true, false)]
    [DataRow("2", true, true)]
    [DataRow(null, true, false)]
    public void UnfilteredMetadataIsCompressedOutsidePdfA1(string? part, bool compressed, bool streaming)
    {
        PdfBuilder b = PdfBuilder.Simple("metadata");
        string identification = part is null ? "" : $"<rdf:Description xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' pdfaid:part='{part}' pdfaid:conformance='B'/>";
        string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF>" + identification
            + string.Concat(Enumerable.Repeat("<rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:format>application/pdf</dc:format></rdf:Description>", 40))
            + "</rdf:RDF></x:xmpmeta>" + new string(' ', 2000) + "<?xpacket end='w'?>";
        int meta = b.AddStream("/Type/Metadata/Subtype/XML", Encoding.ASCII.GetBytes(xmp));
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Metadata {meta} 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { Streaming = streaming });

        AssertOptimizedAndEquivalent(input, output, result);
        PdfFile file = Open(output);
        var metadata = (PdfStream)file.Resolve(file.ResolveDictionary(file.Trailer["Root"])!["Metadata"])!;
        Assert.AreEqual(compressed, metadata.Dictionary["Filter"] is not null, "PDF/A-1 forbids a filter on metadata (ISO 19005-1, 6.7.2); nothing else does.");
        Assert.AreEqual(!compressed, Compat.Latin1.GetString(output).Contains("<?xpacket begin", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BelowThresholdKeepsOriginalBytes()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 99 });
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void EmptyAndGarbageInputNeverThrow()
    {
        foreach (byte[] input in new[] { Array.Empty<byte>(), "not a pdf"u8.ToArray(), "%PDF-1.7\n"u8.ToArray() })
        {
            (byte[] output, PdfOptimizationResult result) = Run(input);
            Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
            CollectionAssert.AreEqual(input, output);
        }
    }

    [TestMethod]
    public void DecompressionBombStreamIsCarriedOverUntouched()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        byte[] bomb = PdfBuilder.Deflate(new byte[8 << 20]);
        int s = b.AddStream("/Filter/FlateDecode", bomb);
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Bomb {s} 0 R>>");
        b.AddStream("", Repeat("padding ", 2000));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { MaxDecodedStreamBytes = 1 << 20 });

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.IsGreaterThanOrEqualTo(0, output.AsSpan().IndexOf(bomb));
    }

    [TestMethod]
    public void DamagedFileIsRetainedUnlessRepairIsAllowed()
    {
        PdfBuilder b = PdfBuilder.Simple("Hello");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        string text = Compat.Latin1.GetString(b.Build());
        int firstObject = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        byte[] input = Compat.Latin1.GetBytes(text[..firstObject] + "% shifted\n" + text[firstObject..]);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, Run(input).Result.Disposition);
        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { AllowRepairedFiles = true });
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void StrippingOptionsRemoveOnlyTheRequestedData()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        int thumb = b.AddStream("/Width 10/Height 10/ColorSpace/DeviceGray/BitsPerComponent 8", new byte[100]);
        int piece = b.AddStream("", Repeat("illustrator private data ", 200));
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/Thumb {thumb} 0 R/PieceInfo<</Illustrator<</Private {piece} 0 R>>>>>>");
        byte[] input = b.Build();

        (byte[] kept, PdfOptimizationResult r1) = Run(input);
        Assert.AreEqual(OptimizationDisposition.Optimized, r1.Disposition);
        PdfFile keptFile = Open(kept);
        Assert.IsNotNull(keptFile.ResolveDictionary(keptFile.GetObject(FindPage(keptFile)))!["Thumb"]);

        (byte[] stripped, PdfOptimizationResult r2) = Run(input, NoThreshold with { RemovePageThumbnails = true, RemovePieceInfo = true });
        Assert.AreEqual(OptimizationDisposition.Optimized, r2.Disposition, r2.Reason);
        Assert.AreEqual(2, r2.Statistics.EntriesStripped);
        PdfFile strippedFile = Open(stripped);
        PdfDictionary strippedPage = strippedFile.ResolveDictionary(strippedFile.GetObject(FindPage(strippedFile)))!;
        Assert.IsNull(strippedPage["Thumb"]);
        Assert.IsNull(strippedPage["PieceInfo"]);
        Assert.IsNotNull(strippedPage["Contents"]);
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, stripped), "Stripping is not lossless and must be visible to the checker.");
        Assert.IsLessThan(kept.Length, stripped.Length);
    }

    [TestMethod]
    public void EmptyNameFollowedByNumberSurvives()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.Set(3, "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding<</Differences[32/ 39/quotesingle]>>>>");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void StreamsWithParametersWeCannotRebuildAreLeftAlone()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        byte[] odd = PdfBuilder.Deflate(Repeat("odd parameters ", 400));
        int s1 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 3.5>>", odd);
        int s2 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 9/Columns 4>>", odd);
        int s3 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 4/Vendor 1>>", odd);
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/X[{s1} 0 R {s2} 0 R {s3} 0 R]>>");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { DeduplicateObjects = false });

        AssertOptimizedAndEquivalent(input, output, result);
        string text = Compat.Latin1.GetString(output);
        StringAssert.Contains(text, "/Columns 3.5");
        StringAssert.Contains(text, "/Predictor 9");
        StringAssert.Contains(text, "/Vendor 1");
    }

    [TestMethod]
    public void JunkBeforeHeaderWithAbsoluteOffsetsIsNotDamage()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] clean = b.Build();

        // Prepend three bytes and shift every offset by three, the way a correct producer would.
        string text = Compat.Latin1.GetString(clean);
        string shifted = System.Text.RegularExpressions.Regex.Replace(text, @"(\d{10}) 00000 n",
            m => (long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 3).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n");
        int sx = shifted.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
        int end = shifted.IndexOf('\n', sx);
        long xref = long.Parse(shifted[sx..end], System.Globalization.CultureInfo.InvariantCulture);
        shifted = shifted[..sx] + (xref + 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + shifted[end..];
        byte[] input = Compat.Latin1.GetBytes("ï»¿" + shifted);

        PdfFile file = Open(input);
        Assert.IsFalse(file.WasReconstructed);
        (byte[] output, PdfOptimizationResult result) = Run(input);
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void CancellationIsHonored()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PdfOptimizer.Optimize(input, Stream.Null, NoThreshold, cts.Token));
    }

    [TestMethod]
    public async Task FileApiWritesAtomicallyAndRejectsSamePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PdfBuilder b = PdfBuilder.Simple("x");
            b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
            string source = Path.Combine(dir, "in.pdf");
            string target = Path.Combine(dir, "sub", "out.pdf");
            File.WriteAllBytes(source, b.Build());

            PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync(source, target);

            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition);
            Assert.AreEqual(result.OutputBytes, new FileInfo(target).Length);
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(dir, "sub")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => PdfOptimizer.OptimizeFileAsync(source, source));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void InspectorReportsStructure()
    {
        PdfBuilder b = PdfBuilder.Simple("a", "b");
        PdfInspection i = PdfInspector.Inspect(b.BuildWithXrefStream(new HashSet<int> { 1, 2 }));
        Assert.AreEqual(2, i.PageCount);
        Assert.IsTrue(i.UsesXrefStreams);
        Assert.IsTrue(i.UsesObjectStreams);
        Assert.IsFalse(i.IsEncrypted);
        Assert.IsFalse(i.NeededRepair);
    }

    private static int FindPage(PdfFile file)
    {
        var kids = (PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!;
        return ((PdfReference)kids[0]).Number;
    }
}
