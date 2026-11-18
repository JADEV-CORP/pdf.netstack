using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Content;
using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>Cancellation and progress of the calls that can run long on a big or hostile document.</summary>
[TestClass]
public sealed class CancellationTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    /// <summary>Records reports as they are made, on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Recorder(Action<PdfProgress>? onReport = null) : IProgress<PdfProgress>
    {
        public List<PdfProgress> Reports { get; } = [];

        public void Report(PdfProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    /// <summary>One page showing <paramref name="count"/> strings, one operator each.</summary>
    private static PdfDocument ManyOperators(int count)
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td ");
        for (int i = 0; i < count; i++) content.Append("(x) Tj ");
        content.Append("ET");
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(3, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(content.ToString()));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R>>");
        return PdfDocument.Open(b.Build());
    }

    /// <summary>Cancels its own token on the first string shown, to see how soon the walk notices.</summary>
    private sealed class CancellingWalk(PdfDocument document, CancellationTokenSource source) : ContentInterpreter(document)
    {
        public int Shown { get; private set; }

        protected override void OnShowText(List<PdfObject> items, PdfDictionary? resources)
        {
            if (++Shown == 1) source.Cancel();
        }
    }

    [TestMethod]
    public void ContentWalkStopsWithinAFewHundredOperatorsOfCancellation()
    {
        PdfDocument doc = ManyOperators(5000);
        using var source = new CancellationTokenSource();
        var walk = new CancellingWalk(doc, source) { Cancellation = source.Token };

        Assert.ThrowsExactly<OperationCanceledException>(() => walk.Run(doc.Pages[0]));
        Assert.IsLessThanOrEqualTo(256, walk.Shown, $"{walk.Shown} strings shown after cancelling");
    }

    [TestMethod]
    public void TextCallsStopWhenCancelledAndMatchTheUncancelledOnes()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("Hello world").Build());
        PdfPage page = doc.Pages[0];

        Assert.ThrowsExactly<OperationCanceledException>(() => page.ExtractText(Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => page.GetWords(Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => page.GetLetters(Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => page.GetImages(Cancelled()));

        Assert.AreEqual(page.ExtractText(), page.ExtractText(CancellationToken.None));
        Assert.AreEqual("Hello world", page.ExtractText(CancellationToken.None));
        CollectionAssert.AreEqual(page.GetWords().Select(w => w.Text).ToList(), page.GetWords(CancellationToken.None).Select(w => w.Text).ToList());
        Assert.HasCount(page.GetLetters().Count, page.GetLetters(CancellationToken.None));
    }

    [TestMethod]
    public void ImageDecodeStopsWhenCancelled()
    {
        PdfDocument doc = PdfDocument.Create();
        byte[] jpeg = SyntheticImages.Jpeg(SyntheticImages.Photo(64, 48, 3), 85);
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(PdfImage.Load(jpeg), 0, 0);
        PdfDocument back = PdfDocument.Open(doc.ToArray(Plain));
        PdfPageImage image = back.Pages[0].GetImages(CancellationToken.None).Single();

        Assert.ThrowsExactly<OperationCanceledException>(() => image.Decode(Cancelled()));
        PdfBitmap decoded = image.Decode(CancellationToken.None)!;
        Assert.AreEqual(64, decoded.Width);
        CollectionAssert.AreEqual(image.Decode()!.ToPng(), decoded.ToPng());
    }

    [TestMethod]
    public void ImageCodecsLookAtTheTokenInsideTheirLoops()
    {
        byte[] jpeg = SyntheticImages.Jpeg(SyntheticImages.Photo(64, 48, 3), 85);
        Assert.ThrowsExactly<OperationCanceledException>(() => JpegDecoder.Decode(jpeg, null, 1 << 24, ct: Cancelled()));

        Raster bilevel = SyntheticImages.Bilevel(64, 64);
        var rows = new byte[8 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (bilevel.Samples[(y * 64) + x] == 0) rows[(y * 8) + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        byte[] g4 = CcittEncoder.EncodeGroup4(rows, 64, 64, true, 1 << 20)!;
        var parameters = new CcittParameters { K = -1, Columns = 64, Rows = 64, BlackIs1 = true };
        Assert.ThrowsExactly<OperationCanceledException>(() => CcittDecoder.Decode(g4, parameters, 64, Cancelled()));
        Assert.HasCount(8 * 64, CcittDecoder.Decode(g4, parameters, 64, CancellationToken.None));
    }

    [TestMethod]
    public void MergeReportsPagesAndStopsBetweenDocuments()
    {
        PdfDocument a = PdfDocument.Open(PdfBuilder.Simple("A1", "A2").Build());
        PdfDocument b = PdfDocument.Open(PdfBuilder.Simple("B1").Build());
        PdfDocument c = PdfDocument.Open(PdfBuilder.Simple("C1", "C2", "C3").Build());

        var recorder = new Recorder();
        PdfDocument merged = PdfDocument.Merge([a, b, c], recorder);
        Assert.HasCount(6, merged.Pages);
        CollectionAssert.AreEqual(new[] { new PdfProgress(2, 6), new PdfProgress(3, 6), new PdfProgress(6, 6) }, recorder.Reports);
        Assert.AreEqual(0.5, recorder.Reports[1].Fraction);

        // Cancelled from the progress handler once the first document is in: the second is not merged.
        using var source = new CancellationTokenSource();
        var stopper = new Recorder(_ => source.Cancel());
        Assert.ThrowsExactly<OperationCanceledException>(() => PdfDocument.Merge([a, b, c], stopper, source.Token));
        Assert.HasCount(1, stopper.Reports);

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfDocument.Merge([a, b], Cancelled()));
    }

    /// <summary>
    /// A page with a form field and 600 fonts, so copying it queues more objects than the
    /// importer copies between two looks at the token; the first font's data cancels when read.
    /// </summary>
    private static PdfDocument ManyResources(Func<ReadOnlyMemory<byte>> firstFontData)
    {
        var b = new PdfBuilder();
        var fonts = new StringBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[5 0 R]>>>>");
        b.Set(5, "<</Type/Annot/Subtype/Widget/FT/Tx/T(incoming)/Rect[72 600 172 620]/P 3 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes("BT /F0 12 Tf 72 700 Td (x) Tj ET"));
        for (int i = 0; i < 600; i++)
        {
            b.SetStream(10 + (2 * i), "", [(byte)i]);
            b.Set(11 + (2 * i), $"<</Type/Font/Subtype/Type1/BaseFont/Helvetica/FontDescriptor<</FontFile {10 + (2 * i)} 0 R>>>>");
            fonts.Append($"/F{i} {11 + (2 * i)} 0 R");
        }

        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<<{fonts}>>>>/Contents 4 0 R/Annots[5 0 R]>>");
        PdfDocument doc = PdfDocument.Open(b.Build());
        var original = (PdfStream)doc.Objects[10];
        doc.Objects[10] = new PdfStream(original.Dictionary, firstFontData, 1);
        return doc;
    }

    [TestMethod]
    public void CancelledImportLeavesTheTargetAsItWasAndCanBeRetried()
    {
        using var source = new CancellationTokenSource();
        PdfDocument from = ManyResources(() =>
        {
            source.Cancel();
            return new byte[] { 0 };
        });

        PdfDocument target = PdfDocument.Create();
        target.Form.AddTextField(target.Pages.Add(PdfRectangle.A4), "existing", 72, 72, 100, 20, "kept");
        int fields = target.Form.Fields.Count;

        Assert.ThrowsExactly<OperationCanceledException>(() => target.Pages.Import(from.Pages, null, source.Token));
        Assert.HasCount(1, target.Pages);
        Assert.HasCount(fields, target.Form.Fields);

        // The objects the cancelled call reserved are not reused as if they had been copied.
        target.Pages.Import(from.Pages, null, CancellationToken.None);
        PdfDocument back = PdfDocument.Open(target.ToArray(Plain));
        var fonts = (PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(back.Pages[1].Dictionary["Resources"])!)["Font"])!;
        Assert.AreEqual(600, fonts.Count);
        Assert.IsTrue(fonts.All(f => back.Resolve(f.Value) is PdfDictionary), "every font of the retried import exists");
        Assert.AreEqual("kept", ((PdfTextField)back.Form["existing"]!).Text);
        CollectionAssert.AreEqual(new[] { "existing", "incoming" }, back.Form.Fields.Select(f => f.Name).ToArray(), "the cancelled call added no field");
    }

    [TestMethod]
    public void SplitStopsWhenCancelledAndReportsEachPart()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("P1", "P2", "P3").Build());
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Split(Cancelled()).ToList());

        var recorder = new Recorder();
        List<PdfDocument> parts = [.. doc.Split(recorder)];
        Assert.HasCount(3, parts);
        CollectionAssert.AreEqual(new[] { new PdfProgress(1, 3), new PdfProgress(2, 3), new PdfProgress(3, 3) }, recorder.Reports);
        foreach (PdfDocument part in parts) part.Dispose();
    }

    /// <summary>A memory stream that remembers being disposed.</summary>
    private sealed class TrackedStream : MemoryStream
    {
        public TrackedStream()
        {
        }

        public TrackedStream(byte[] data)
            : base(data)
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    public void SplitToWritesAndDisposesOneStreamPerPage()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("P1", "P2", "P3").Build());
        var outputs = new List<TrackedStream>();
        var recorder = new Recorder();
        int written = doc.SplitTo(i =>
        {
            Assert.IsTrue(outputs.All(o => o.Disposed), "the previous part's stream is disposed before the next is asked for");
            var stream = new TrackedStream();
            outputs.Add(stream);
            return stream;
        }, Plain, recorder);

        Assert.AreEqual(3, written);
        Assert.IsTrue(outputs.All(o => o.Disposed));
        Assert.HasCount(3, recorder.Reports);
        for (int i = 0; i < 3; i++)
        {
            using PdfDocument part = PdfDocument.Open(outputs[i].ToArray());
            Assert.HasCount(1, part.Pages);
            Assert.AreEqual($"P{i + 1}", part.Pages[0].ExtractText());
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SplitTo(_ => null!));
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.SplitTo(_ => new MemoryStream(), Plain, null, Cancelled()));
    }

    [TestMethod]
    public async Task SplitToAsyncWritesEveryPart()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("P1", "P2").Build());
        var outputs = new List<TrackedStream>();
        int written = await doc.SplitToAsync(_ =>
        {
            var stream = new TrackedStream();
            outputs.Add(stream);
            return stream;
        });

        Assert.AreEqual(2, written);
        Assert.IsTrue(outputs.All(o => o.Disposed));
        using PdfDocument second = PdfDocument.Open(outputs[1].ToArray());
        Assert.AreEqual("P2", second.Pages[0].ExtractText());
    }

    [TestMethod]
    public void ExtractPagesCopiesARangeAndLeavesTheSourceAlone()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("P1", "P2", "P3", "P4").Build());

        using PdfDocument middle = doc.ExtractPages(1, 2);
        CollectionAssert.AreEqual(new[] { "P2", "P3" }, middle.Pages.Select(p => p.ExtractText()).ToArray());

        using PdfDocument last = doc.ExtractPages(^2..);
        CollectionAssert.AreEqual(new[] { "P3", "P4" }, PdfDocument.Open(last.ToArray(Plain)).Pages.Select(p => p.ExtractText()).ToArray());

        Assert.HasCount(4, doc.Pages);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.ExtractPages(3, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.ExtractPages(-1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.ExtractPages(2..9));
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.ExtractPages(0, 1, Cancelled()));
    }

    [TestMethod]
    public async Task MergeAsyncHoldsOneInputAtATimeAndDisposesEach()
    {
        byte[][] files = [PdfBuilder.Simple("A1", "A2").Build(), PdfBuilder.Simple("B1").Build(), PdfBuilder.Simple("C1").Build()];
        var opened = new List<TrackedStream>();
        IEnumerable<Stream> Inputs()
        {
            foreach (byte[] file in files)
            {
                Assert.IsTrue(opened.All(s => s.Disposed), "an input is disposed before the next is opened");
                var stream = new TrackedStream(file);
                opened.Add(stream);
                yield return stream;
            }
        }

        var recorder = new Recorder();
        using var output = new MemoryStream();
        await PdfDocument.MergeAsync(Inputs(), output, new PdfMergeOptions { SaveOptions = Plain }, recorder);

        Assert.IsTrue(opened.All(s => s.Disposed));
        CollectionAssert.AreEqual(new[] { new PdfProgress(1, null), new PdfProgress(2, null), new PdfProgress(3, null) }, recorder.Reports);
        Assert.IsNull(recorder.Reports[0].Fraction);
        using PdfDocument merged = PdfDocument.Open(output.ToArray());
        CollectionAssert.AreEqual(new[] { "A1", "A2", "B1", "C1" }, merged.Pages.Select(p => p.ExtractText()).ToArray());
    }

    [TestMethod]
    public async Task MergeAsyncCopiesDataOutOfInputsSpooledToTemporaryFiles()
    {
        // Every input goes to a temporary file, deleted once its pages are copied: the merged
        // document must hold its own copy of everything it draws.
        string folder = Path.Combine(Path.GetTempPath(), "pdfnetstack-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new PdfMergeOptions { LoadOptions = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, SpoolThreshold = 0, TempDirectory = folder } };
            using var output = new MemoryStream();
            await PdfDocument.MergeAsync([new MemoryStream(PdfBuilder.Simple("A1", "A2").Build()), new MemoryStream(PdfBuilder.Simple("B1").Build())], output, options);

            Assert.IsEmpty(Directory.GetFiles(folder), "each spooled input is deleted");
            using PdfDocument merged = PdfDocument.Open(output.ToArray());
            CollectionAssert.AreEqual(new[] { "A1", "A2", "B1" }, merged.Pages.Select(p => p.ExtractText()).ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task MergeAsyncCanLeaveInputsOpenAndReportsTheTotalOfACollection()
    {
        TrackedStream[] inputs = [new(PdfBuilder.Simple("A").Build()), new(PdfBuilder.Simple("B").Build())];
        var recorder = new Recorder();
        using var output = new MemoryStream();
        await PdfDocument.MergeAsync(inputs, output, new PdfMergeOptions { LeaveInputsOpen = true, LoadOptions = PdfLoadOptions.Default }, recorder);

        Assert.IsFalse(inputs.Any(s => s.Disposed));
        CollectionAssert.AreEqual(new[] { new PdfProgress(1, 2), new PdfProgress(2, 2) }, recorder.Reports);
        Assert.HasCount(2, PdfDocument.Open(output.ToArray()).Pages);

        using var cancelledOutput = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PdfDocument.MergeAsync([new MemoryStream(PdfBuilder.Simple("A").Build())], cancelledOutput, Cancelled()));
        Assert.AreEqual(0, cancelledOutput.Length);
    }

    [TestMethod]
    public void FlattenAnnotationsReportsPagesAndStopsBetweenThem()
    {
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < 3; i++) doc.Pages.Add(PdfRectangle.A4).Annotations.AddNote(72, 72, "note " + i);

        using var source = new CancellationTokenSource();
        var stopper = new Recorder(p =>
        {
            if (p.Completed == 2) source.Cancel();
        });
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.FlattenAnnotations(null, stopper, source.Token));
        Assert.AreEqual(0, doc.Pages[0].AnnotationCount);
        Assert.AreEqual(0, doc.Pages[1].AnnotationCount);
        Assert.AreEqual(1, doc.Pages[2].AnnotationCount, "the page after cancellation is untouched");

        Assert.AreEqual(1, doc.FlattenAnnotations(null, CancellationToken.None));
    }

    [TestMethod]
    public void FormFlattenCancelledBeforeAnyChangeLeavesTheFormWhole()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Form.AddTextField(doc.Pages.Add(PdfRectangle.A4), "name", 72, 72, 100, 20, "Ada");

        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Form.Flatten(Cancelled()));
        Assert.IsTrue(doc.Form.Exists);
        Assert.AreEqual(1, doc.Pages[0].AnnotationCount);

        doc.Form.Flatten(CancellationToken.None);
        Assert.IsFalse(doc.Form.Exists);
    }

    [TestMethod]
    public void SaveAndToArrayStopWhenCancelled()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("x").Build());
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Save(new MemoryStream(), Plain, Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Save(new MemoryStream(), null, Cancelled()));
        Assert.ThrowsExactly<OperationCanceledException>(() => doc.ToArray(null, Cancelled()));

        using var stream = new MemoryStream();
        doc.Save(stream, Plain, CancellationToken.None);
        CollectionAssert.AreEqual(doc.ToArray(Plain), stream.ToArray());
        CollectionAssert.AreEqual(doc.ToArray(Plain), doc.ToArray(Plain, CancellationToken.None));
    }

    private sealed class NoRevocation : IPdfRevocationSource
    {
        public PdfRevocationValues GetRevocationValues(PdfRevocationRequest request) => PdfRevocationValues.None;
    }

    [TestMethod]
    public void SigningCallsStopWhenCancelled()
    {
        using RSA key = RSA.Create(2048);
        X509Certificate2 certificate = new CertificateRequest("CN=Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] document = PdfDocument.Open(PdfBuilder.Simple("Contract").Build()).ToArray(Plain);

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfSigner.Sign(document, certificate, null, Cancelled()));
        byte[] signed = PdfSigner.Sign(document, certificate, null, CancellationToken.None);

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfSigner.Verify(signed, null, Cancelled()));
        Assert.IsTrue(PdfSigner.Verify(signed, null, CancellationToken.None).Single().IsIntact);

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfSigner.AddValidationData(signed, new NoRevocation(), null, Cancelled()));
    }
}
