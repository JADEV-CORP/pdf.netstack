using System.IO.Compression;
using System.Text;
using PdfNetStack.Filters;
using PdfNetStack.Optimization;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Tests;

/// <summary>Limits and cancellation of the optimizer, the decoders and the image decoding.</summary>
[TestClass]
public sealed class OptimizerHardeningTests
{
    private const int Chunk = 81920;

    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    /// <summary>A valid PDF of about <paramref name="size"/> bytes, padded with an incompressible stream.</summary>
    private static byte[] PaddedPdf(int size)
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        byte[] filler = new byte[size];
        new Random(5).NextBytes(filler);
        b.AddStream("", filler);
        return b.Build();
    }

    /// <summary>
    /// A stream that cannot seek and fails the test once it has served more than <paramref name="allowance"/>
    /// bytes while nothing has reached <paramref name="output"/>: the input must not be taken in
    /// beyond the limit before it is passed on.
    /// </summary>
    private sealed class GuardedStream(byte[] data, long allowance, Stream output) : Stream
    {
        private int _position;

        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Served > allowance && output.Length == 0) throw new InvalidOperationException("The input was read past the limit before it was passed on.");
            int n = Math.Min(count, data.Length - _position);
            Buffer.BlockCopy(data, _position, buffer, offset, n);
            _position += n;
            Served += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(PdfOptimizationResult Result, byte[] Output)> OptimizeForwardOnly(byte[] input, PdfOptimizerOptions options, long cap)
    {
        using var output = new MemoryStream();
        using var guarded = new GuardedStream(input, cap + 2 * Chunk, output);
        PdfOptimizationResult result = await PdfOptimizer.OptimizeAsync(guarded, output, options);
        return (result, output.ToArray());
    }

    [TestMethod]
    public async Task StreamingSpoolStopsAtTheLimit()
    {
        byte[] input = PaddedPdf(1 << 20);
        var options = new PdfOptimizerOptions { Streaming = true, Limits = new PdfLimits { MaxInputBytes = 64 << 10 } };

        (PdfOptimizationResult result, byte[] output) = await OptimizeForwardOnly(input, options, 64 << 10);

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(PdfLimitKind.InputBytes, result.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public async Task StreamingSpoolStopsAtTheOptionLimit()
    {
        byte[] input = PaddedPdf(1 << 20);
        var options = new PdfOptimizerOptions { Streaming = true, MaxInputBytes = 64 << 10 };

        (PdfOptimizationResult result, byte[] output) = await OptimizeForwardOnly(input, options, 64 << 10);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public async Task BufferedInputStopsAtTheLimit()
    {
        byte[] input = PaddedPdf(1 << 20);
        var options = new PdfOptimizerOptions { Limits = new PdfLimits { MaxInputBytes = 64 << 10 } };

        (PdfOptimizationResult result, byte[] output) = await OptimizeForwardOnly(input, options, 64 << 10);

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(PdfLimitKind.InputBytes, result.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public async Task BufferedInputStopsAtTheOptionLimit()
    {
        byte[] input = PaddedPdf(1 << 20);
        var options = new PdfOptimizerOptions { MaxInputBytes = 64 << 10 };

        (PdfOptimizationResult result, byte[] output) = await OptimizeForwardOnly(input, options, 64 << 10);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public async Task SeekableInputOverTheLimitIsPassedThroughUnbuffered()
    {
        byte[] input = PaddedPdf(1 << 20);
        var options = new PdfOptimizerOptions { Limits = new PdfLimits { MaxInputBytes = 64 << 10 } };
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizationResult result = await PdfOptimizer.OptimizeAsync(source, output, options);

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition);
        CollectionAssert.AreEqual(input, output.ToArray());
    }

    [TestMethod]
    public async Task InvalidOptionsAreRefusedBeforeTheInputIsRead()
    {
        using var output = new MemoryStream();
        using var guarded = new GuardedStream(PaddedPdf(1 << 16), 0, output);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            PdfOptimizer.OptimizeAsync(guarded, output, new PdfOptimizerOptions { MaxInputBytes = -1 }));

        Assert.AreEqual(0, guarded.Served);
    }

    [TestMethod]
    public async Task FileOverTheLimitIsCopiedThroughWithoutBeingRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "in.pdf");
            string destination = Path.Combine(directory, "out.pdf");
            byte[] input = PaddedPdf(10 << 20);
            await File.WriteAllBytesAsync(source, input);
            var options = new PdfOptimizerOptions { Limits = new PdfLimits { MaxInputBytes = 1024 } };

            // Only the part before the first suspension runs on this thread, and it is where a read
            // of the whole file would allocate its buffer.
#if NET
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            Task<PdfOptimizationResult> running = PdfOptimizer.OptimizeFileAsync(source, destination, options);
#if NET
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            PdfOptimizationResult result = await running;

            Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition);
            Assert.AreEqual(PdfLimitKind.InputBytes, result.Diagnostics.Single().Limit);
            CollectionAssert.AreEqual(input, await File.ReadAllBytesAsync(destination));
#if NET
            Assert.IsLessThan(2 << 20, allocated, "The file must not be read into memory.");
#endif
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>One page drawing a photograph, 320 by 240 pixels, as a JPEG at 72 dpi.</summary>
    private static byte[] PhotoPdf()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, "/Type/XObject/Subtype/Image/Width 320/Height 240/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode", JpegCodecTests.Fixture("photo-q95.jpg"));
        b.SetStream(4, "", "q 320 0 0 240 0 0 cm /Im0 Do Q"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    // Downsampling decodes the JPEG; a lossless run only moves its coefficients.
    private static PdfOptimizerOptions Downsampling(PdfLimits limits) => new() { MinSavingPercent = 0, MaxImageDpi = 20, Limits = limits };

    [TestMethod]
    public void LossyImageDecodesSpendTheImagePixelBudget()
    {
        byte[] input = PhotoPdf();

        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Downsampling(new PdfLimits { MaxTotalImagePixels = 50_000 }));

        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition, result.Reason);
        Assert.AreEqual(PdfLimitKind.TotalImagePixels, result.Diagnostics.Single().Limit);
    }

    [TestMethod]
    public void LossyPassOverALimitOtherThanTimeIsNotRepaidWithTheLosslessRewrite()
    {
        byte[] input = PhotoPdf();
        var limits = new PdfLimits { MaxTotalImagePixels = 50_000, Timeout = TimeSpan.FromSeconds(60) };

        (_, PdfOptimizationResult lossless) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 0, Limits = limits });
        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Downsampling(limits));

        Assert.AreEqual(OptimizationDisposition.Optimized, lossless.Disposition, lossless.Reason);
        Assert.AreEqual(OptimizationDisposition.LimitExceeded, result.Disposition, result.Reason);
    }

    /// <summary>A black RGB image of <paramref name="side"/> pixels square under PNG prediction, which deflates to almost nothing.</summary>
    private static byte[] PredictedImagePdf(int side)
    {
        byte[] samples = new byte[side * side * 3];
        byte[] rows = new byte[side * (side * 3 + 1)];
        for (int y = 0; y < side; y++)
        {
            Buffer.BlockCopy(samples, y * side * 3, rows, y * (side * 3 + 1) + 1, side * 3);
        }

        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, $"/Type/XObject/Subtype/Image/Width {side}/Height {side}/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/FlateDecode/DecodeParms<</Predictor 15/Colors 3/Columns {side}>>", StreamCodec.Deflate(rows, CompressionLevel.Optimal));
        b.SetStream(4, "", Encoding.ASCII.GetBytes($"q {side} 0 0 {side} 0 0 cm /Im0 Do Q"));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    [TestMethod]
    public void PredictorUndoneWhileChoosingIsChargedToTheBudget()
    {
        // The decoded rows take 12,352 bytes and the samples recovered from them 12,288 more.
        var budget = new ResourceBudget(new PdfLimits { MaxTotalDecodedBytes = 20_000 }, 0, new PdfDiagnostics());
        PdfFile file = PdfFile.Open(PredictedImagePdf(64), 64 << 20, budget);
        ObjectGraph graph = ObjectGraph.Collect(file, false, false, CancellationToken.None);
        StreamRecompressor.Work work = StreamRecompressor.Prepare(file, graph, RecompressionScope.All, CompressionLevel.Optimal, false, true, false, null, null, 64 << 20, CancellationToken.None);
        int image = work.IndexOf(10);

        PdfLimitExceededException exceeded = Assert.ThrowsExactly<PdfLimitExceededException>(() => work.Execute(image, false, CancellationToken.None));

        Assert.AreEqual(PdfLimitKind.TotalDecodedBytes, exceeded.Limit);
    }

    /// <summary>The objects of a one page file, numbered as the writers take them.</summary>
    private static (List<PdfObject> Objects, PdfDictionary Trailer, Func<PdfReference, int> Map) WriterInput()
    {
        PdfFile file = PdfFile.Open(PdfBuilder.Simple("x").Build(), 64 << 20);
        ObjectGraph graph = ObjectGraph.Collect(file, false, false, CancellationToken.None);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < graph.Order.Count; i++) index[graph.Order[i]] = i + 1;
        var trailer = new PdfDictionary();
        trailer.Set("Root", file.Trailer["Root"]!);
        return ([.. graph.Order.Select(file.GetObject)], trailer, r => index.TryGetValue(r.Number, out int n) ? n : -1);
    }

    [TestMethod]
    public void WritersStopWhenCancelled()
    {
        (List<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> map) = WriterInput();
        var settings = new WriterSettings(false, 200, "1.7", CompressionLevel.Optimal);

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfDocumentWriter.Write(objects, trailer, map, settings, Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => LinearizedWriter.Write(objects, trailer, map, settings, Cancelled()));
        Assert.IsGreaterThan(0, PdfDocumentWriter.Write(objects, trailer, map, settings).Length);
    }

    private static PdfDocument TwoImages(string dictionary, byte[] first, byte[] second, PdfLimits limits)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</A 5 0 R/B 6 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", "q 100 0 0 100 0 0 cm /A Do Q q 100 0 0 100 0 0 cm /B Do Q"u8.ToArray());
        b.SetStream(5, dictionary, first);
        b.SetStream(6, dictionary, second);
        return PdfDocument.Open(b.Build(), new PdfLoadOptions { Limits = limits });
    }

    [TestMethod]
    public void DecodedImagesSpendTheDecodedByteBudget()
    {
        // 3 MiB of samples each, decoded into the array that becomes the pixels. The array is not
        // given back to the budget when it is handed on, so the second image does not fit in 4 MiB.
        byte[] samples = new byte[1024 * 1024 * 3];
        using PdfDocument doc = TwoImages("/Type/XObject/Subtype/Image/Width 1024/Height 1024/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/FlateDecode",
            PdfBuilder.Deflate(samples), PdfBuilder.Deflate(samples), PdfLimits.Default with { MaxTotalDecodedBytes = 4 << 20 });
        IReadOnlyList<PdfPageImage> images = doc.Pages[0].GetImages();

        Assert.IsNotNull(images[0].Decode());
        PdfLimitExceededException e = Assert.ThrowsExactly<PdfLimitExceededException>(() => images[1].Decode());

        Assert.AreEqual(PdfLimitKind.TotalDecodedBytes, e.Limit);
    }
}
