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
