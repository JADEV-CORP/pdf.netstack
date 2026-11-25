using System.Runtime.InteropServices;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Bounds, cancellation and cleanup on the open, merge, split and save paths of a document.</summary>
[TestClass]
public sealed class DocumentHardeningTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    private sealed class Recorder : IProgress<PdfProgress>
    {
        public List<PdfProgress> Reports { get; } = [];

        public void Report(PdfProgress value) => Reports.Add(value);

        public string[] Pairs() => [.. Reports.Select(r => $"{r.Completed}/{r.Total}")];
    }

    /// <summary>A stream that notes when it is disposed and can fail doing so.</summary>
    private sealed class TrackedStream(byte[] data, bool failOnDispose = false) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (disposing && failOnDispose) throw new IOException("The stream cannot be closed.");
        }
    }

    private static byte[] Pdf(params string[] pages) => PdfBuilder.Simple(pages).Build();

    [TestMethod]
    public async Task MergeAsyncDisposesAnInputWhenCancelledBeforeItIsOpened()
    {
        var input = new TrackedStream(Pdf("A"));
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => PdfDocument.MergeAsync([input], output, Cancelled()));

        Assert.IsTrue(input.Disposed);
    }

    [TestMethod]
    public async Task MergeAsyncDisposesAnInputWhenCancelledAfterItsPagesAreCopied()
    {
        using var source = new CancellationTokenSource();
        var first = new TrackedStream(Pdf("A"));
        var second = new TrackedStream(Pdf("B"));
        var recorder = new Recorder();
        using var output = new MemoryStream();

        IEnumerable<Stream> Inputs()
        {
            yield return first;
            source.Cancel();
            yield return second;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => PdfDocument.MergeAsync(Inputs(), output, null, recorder, source.Token));

        Assert.IsTrue(first.Disposed);
        Assert.IsTrue(second.Disposed, "The input reached after the token fired is still released.");
        Assert.IsEmpty(recorder.Reports.Skip(1));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task MergeAsyncReleasesTheOpenedSourceWhenDisposingTheInputFails()
    {
        // A copy spooled to a file holds a handle; /dev/fd and /proc/self/fd list them.
        string? fds = new[] { "/proc/self/fd", "/dev/fd" }.FirstOrDefault(Directory.Exists);
        if (fds is null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Assert.Inconclusive("Open handles cannot be counted here.");

        using var temp = new TempFolder();
        var options = new PdfMergeOptions { LoadOptions = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, SpoolThreshold = 0, TempDirectory = temp.Path } };
        var input = new TrackedStream(Pdf("A"), failOnDispose: true);
        using var output = new MemoryStream();
        int before = Directory.GetFileSystemEntries(fds).Length;

        await Assert.ThrowsAsync<IOException>(() => PdfDocument.MergeAsync([input], output, options));

        Assert.AreEqual(before, Directory.GetFileSystemEntries(fds).Length, "The source's temporary copy is closed.");
    }

    [TestMethod]
    public void MergeChecksTheTokenBeforeItReadsTheSequence()
    {
        int pulled = 0;

        IEnumerable<PdfDocument> Lazy()
        {
            pulled++;
            yield return PdfDocument.Open(Pdf("A"));
        }

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfDocument.Merge(Lazy(), Cancelled()));

        Assert.AreEqual(0, pulled);
    }

    [TestMethod]
    public void MergeWithoutProgressTakesTheSequenceOneDocumentAtATime()
    {
        using var source = new CancellationTokenSource();
        int pulled = 0;

        IEnumerable<PdfDocument> Lazy()
        {
            for (int i = 0; i < 5; i++)
            {
                pulled++;
                if (i == 1) source.Cancel();
                yield return PdfDocument.Open(Pdf("A"));
            }
        }

        Assert.ThrowsExactly<OperationCanceledException>(() => PdfDocument.Merge(Lazy(), source.Token));

        Assert.AreEqual(2, pulled);
    }

    [TestMethod]
    public void MergeWithProgressStillReportsTheTotal()
    {
        var recorder = new Recorder();
        PdfDocument merged = PdfDocument.Merge([PdfDocument.Open(Pdf("A", "B")), PdfDocument.Open(Pdf("C"))], recorder);

        Assert.HasCount(3, merged.Pages);
        CollectionAssert.AreEqual(new[] { "2/3", "3/3" }, recorder.Pairs());
    }

    [TestMethod]
    public void SplitReportsTheTotalItStartedWith()
    {
        PdfDocument doc = PdfDocument.Open(Pdf("A", "B"));
        var recorder = new Recorder();

        using IEnumerator<PdfDocument> parts = doc.Split(recorder).GetEnumerator();
        Assert.IsTrue(parts.MoveNext());
        doc.Pages.RemoveAt(1);
        Assert.IsTrue(parts.MoveNext());

        CollectionAssert.AreEqual(new[] { "1/2", "2/2" }, recorder.Pairs());
    }

    [TestMethod]
    public void SplitToReportsTheTotalItStartedWith()
    {
        PdfDocument doc = PdfDocument.Open(Pdf("A", "B"));
        var recorder = new Recorder();

        doc.SplitTo(i =>
        {
            if (i == 0) doc.Pages.RemoveAt(1);
            return new MemoryStream();
        }, Plain, recorder);

        CollectionAssert.AreEqual(new[] { "1/2", "2/2" }, recorder.Pairs());
    }

    [TestMethod]
    public async Task SplitToAsyncReportsTheTotalItStartedWith()
    {
        PdfDocument doc = PdfDocument.Open(Pdf("A", "B"));
        var recorder = new Recorder();

        await doc.SplitToAsync(i =>
        {
            if (i == 0) doc.Pages.RemoveAt(1);
            return new MemoryStream();
        }, Plain, recorder);

        CollectionAssert.AreEqual(new[] { "1/2", "2/2" }, recorder.Pairs());
    }

    [TestMethod]
    public void AnIncrementalSaveInPlaceKeepsTheDecodeBudgetOfTheBaseline()
    {
        byte[] pdf = PdfBuilder.Simple("A", "B").BuildWithXrefStream(new HashSet<int> { 1, 2, 3, 5, 7 });
        using var stream = new MemoryStream();
        stream.Write(pdf);
        stream.Position = 0;
        using PdfDocument doc = PdfDocument.Open(stream, new PdfLoadOptions { Mode = PdfLoadMode.OnDemand });

        doc.Pages[0].Rotation = 90;
        doc.SaveIncremental(stream);
        doc.Pages[1].Rotation = 90;
        long left = doc.Budget.DecodedBytesLeft;
        doc.SaveIncremental(stream);

        Assert.IsTrue(doc.Budget.DecodedBytesLeft < left, "The second save reads the first update's file through the budget.");
    }

    [TestMethod]
    public void InspectChecksTheDeadlineAfterTheMetadataIsDecoded()
    {
        PdfBuilder b = PdfBuilder.Simple("A");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Metadata 99 0 R>>");
        b.SetStream(99, "/Type/Metadata/Subtype/XML/Filter/FlateDecode", PdfBuilder.Deflate(new byte[128 << 20]));
        byte[] pdf = b.Build();
        PdfInspector.Inspect(pdf);

        // Everything before the metadata is a few objects; decoding 128 MiB takes far longer than 5 ms.
        var limits = PdfLimits.Default with { Timeout = TimeSpan.FromMilliseconds(5) };
        var e = Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfInspector.Inspect(pdf, limits));

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    [DataRow(PdfLoadMode.InMemory)]
    [DataRow(PdfLoadMode.OnDemand)]
    public void AMisplacedXrefStreamEntryDoesNotHideARootThatPointsAtIt(PdfLoadMode mode)
    {
        // Object 7 is the xref stream; its own entry points at the catalog and /Root names 7.
        byte[] pdf = PdfBuilder.Simple("A").BuildWithXrefStream(root: 7, misplaceOwnEntry: true);

        using PdfDocument doc = PdfDocument.Open(pdf, new PdfLoadOptions { Mode = mode });

        Assert.AreEqual("A", doc.Pages[0].ExtractText().Trim());
    }

#if NET
    [TestMethod]
    [DoNotParallelize]
    public void ObjectStreamsClaimingMoreEntriesThanTheyHoldAllocateNothingForThem()
    {
        byte[] sound = PdfBuilder.Simple("A").Build();
        int xref = Compat.Latin1.GetString(sound).LastIndexOf("\nxref", StringComparison.Ordinal);
        var damaged = new MemoryStream();
        damaged.Write(sound, 0, xref + 1);
        for (int n = 10; n < 210; n++)
        {
            damaged.Write(Compat.Latin1.GetBytes($"{n} 0 obj\n<</Type/ObjStm/N 1000000/First 0/Length 0>>\nstream\n\nendstream\nendobj\n"));
        }

        byte[] pdf = damaged.ToArray();
        PdfDocument.Open(sound);

        long before = GC.GetAllocatedBytesForCurrentThread();
        using PdfDocument doc = PdfDocument.Open(pdf);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual("A", doc.Pages[0].ExtractText().Trim());
        Assert.IsTrue(allocated < 64L << 20, $"Opening allocated {allocated >> 20} MiB.");
    }
#endif

    [TestMethod]
    public void APageListedManyTimesIsChargedToTheBudgetForEachCopy()
    {
        PdfBuilder b = PdfBuilder.Simple("A");
        b.Set(2, $"<</Type/Pages/Kids[{string.Join(" ", Enumerable.Repeat("5 0 R", 20_000))}]/Count 20000>>");
        byte[] pdf = b.Build();
        var limited = new PdfLoadOptions { Limits = PdfLimits.Default with { MaxTotalDecodedBytes = 64 << 10 } };

        var e = Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(pdf, limited));

        Assert.AreEqual(PdfLimitKind.TotalDecodedBytes, e.Limit);
        Assert.HasCount(20_000, PdfDocument.Open(pdf).Pages);
    }

    /// <summary>A stream that cannot seek and hands out one byte per read, slowly.</summary>
    private sealed class TricklingStream(byte[] data) : Stream
    {
        private int _at;

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
            if (_at >= data.Length || count == 0) return 0;
            Thread.Sleep(2);
            buffer[offset] = data[_at++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(PdfLoadMode.InMemory)]
    [DataRow(PdfLoadMode.OnDemand)]
    public void TheTimeoutBoundsReadingAStreamThatTrickles(PdfLoadMode mode)
    {
        // About 1 KB at 2 ms a byte is two seconds of reading; the limit is a tenth of a second.
        byte[] pdf = Pdf("A", "B", "C");
        var options = new PdfLoadOptions { Mode = mode, Limits = PdfLimits.Default with { Timeout = TimeSpan.FromMilliseconds(100) } };

        var e = Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(new TricklingStream(pdf), options));

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    [DataRow(PdfLoadMode.InMemory)]
    [DataRow(PdfLoadMode.OnDemand)]
    public async Task TheTimeoutBoundsReadingAStreamAsynchronouslyToo(PdfLoadMode mode)
    {
        byte[] pdf = Pdf("A", "B", "C");
        var options = new PdfLoadOptions { Mode = mode, Limits = PdfLimits.Default with { Timeout = TimeSpan.FromMilliseconds(100) } };

        PdfLimitExceededException e = await Assert.ThrowsExactlyAsync<PdfLimitExceededException>(() => PdfDocument.OpenAsync(new TricklingStream(pdf), options));

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    public void TheTimeoutBoundsParsingADocumentOpenedOnDemandFromMemory()
    {
        byte[] pdf = Pdf("A");
        var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, Limits = PdfLimits.Default with { Timeout = TimeSpan.FromTicks(1) } };

        var e = Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(pdf.AsMemory(), options));

        Assert.AreEqual(PdfLimitKind.Timeout, e.Limit);
    }

    [TestMethod]
    [DataRow(PdfLoadMode.InMemory)]
    [DataRow(PdfLoadMode.OnDemand)]
    public void AFileOverTheInputLimitIsRefusedAndReleased(PdfLoadMode mode)
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "big.pdf");
        File.WriteAllBytes(path, Pdf("A"));
        var options = new PdfLoadOptions { Mode = mode, Limits = PdfLimits.Default with { MaxInputBytes = 100 } };

        var e = Assert.ThrowsExactly<PdfLimitExceededException>(() => PdfDocument.Open(path, options));

        Assert.AreEqual(PdfLimitKind.InputBytes, e.Limit);
        File.Delete(path);
        Assert.IsFalse(File.Exists(path), "No handle is left on the file.");
    }

    /// <summary>A form of <paramref name="fields"/> text fields whose widgets name page <paramref name="pageOf"/> by /P and are on no page's /Annots.</summary>
    private static byte[] FormWithPageReferences(int pages, int fields, Func<int, int> pageOf)
    {
        PdfBuilder b = PdfBuilder.Simple([.. Enumerable.Range(1, pages).Select(i => "P" + i)]);
        int firstField = b.NextNumber;
        var refs = new List<string>();
        for (int i = 0; i < fields; i++)
        {
            int page = 5 + (pageOf(i) * 2);
            b.Set(firstField + i, $"<</FT/Tx/T(f{i})/Subtype/Widget/Rect[0 0 10 10]/P {page} 0 R>>");
            refs.Add($"{firstField + i} 0 R");
        }

        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[{string.Join(" ", refs)}]>>>>");
        return b.Build();
    }

    [TestMethod]
    public void TheFieldsOfAFormAreGatheredUnderTheCallersToken()
    {
        using PdfDocument doc = PdfDocument.Open(FormWithPageReferences(1, 3, _ => 0));

        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Form.Collect(Cancelled()));
        Assert.HasCount(3, doc.Form.Collect(CancellationToken.None));
    }

    [TestMethod]
    public void AWidgetFindsItsPageThroughPWhenNoPageListsIt()
    {
        using PdfDocument doc = PdfDocument.Open(FormWithPageReferences(4, 8, i => i % 4));

        PdfPage?[] pages = [.. doc.Form.Fields.Select(f => f.Widgets[0].Page)];

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 0, 1, 2, 3 }, pages.Select(p => doc.Pages.ToList().IndexOf(p!)).ToArray());
    }

    [TestMethod]
    public void FlatteningAFormWithManyFieldsStopsWhenCancelledWhileGathering()
    {
        using PdfDocument doc = PdfDocument.Open(FormWithPageReferences(50, 20_000, i => i % 50));
        using var source = new CancellationTokenSource();
        source.CancelAfter(1);

        Assert.ThrowsExactly<OperationCanceledException>(() => doc.Form.Flatten(source.Token));
        Assert.IsTrue(doc.Form.Exists, "A cancelled flatten leaves the form in place.");
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfnetstack-hardening-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
