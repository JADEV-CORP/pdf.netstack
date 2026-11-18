using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class ReaderTests
{
    private const long Limit = 64 << 20;

    private static string? PageText(PdfFile file, int pageNumber)
    {
        var page = (PdfDictionary)file.GetObject(pageNumber);
        var content = (PdfStream)file.Resolve(page["Contents"])!;
        return Encoding.ASCII.GetString(content.Data.Span);
    }

    [TestMethod]
    public void ReadsClassicXref()
    {
        PdfFile file = PdfFile.Open(PdfBuilder.Simple("Hello").Build(), Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(1, file.RevisionCount);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReadsXrefStreamsAndObjectStreams(bool predictor)
    {
        PdfBuilder b = PdfBuilder.Simple("A", "B");
        byte[] pdf = b.BuildWithXrefStream(new HashSet<int> { 1, 2, 3, 5, 7 }, predictor);
        PdfFile file = PdfFile.Open(pdf, Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.IsTrue(file.UsesXrefStreams);
        Assert.IsTrue(file.UsesObjectStreams);
        Assert.AreEqual("Catalog", file.ResolveDictionary(file.Trailer["Root"])!.GetName("Type"));
        StringAssert.Contains(PageText(file, 7), "(B)");
    }

    [TestMethod]
    public void IncrementalUpdateNewestRevisionWins()
    {
        PdfBuilder b = PdfBuilder.Simple("Old");
        byte[] v1 = b.Build();
        string newContent = "<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream";
        byte[] v2 = PdfBuilder.AppendUpdate(v1, new Dictionary<int, string> { [4] = newContent }, size: 6);

        PdfFile file = PdfFile.Open(v2, Limit);
        Assert.AreEqual(2, file.RevisionCount);
        StringAssert.Contains(PageText(file, 5), "(New)");
    }

    /// <summary>
    /// An xref stream's entry for itself only says where the stream is, which the reader already
    /// knows. A wrong one (qpdf's hybrid-xref.pdf has one) damages nothing and is no reason to
    /// rebuild the file from a scan.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void XrefStreamWithMisplacedOwnEntryIsNotRepaired(bool inMemory)
    {
        byte[] pdf = PdfBuilder.Simple("A").BuildWithXrefStream(misplaceOwnEntry: true);
        PdfFile file = inMemory ? PdfFile.Open(pdf, Limit) : PdfFile.Open(new MemoryStream(pdf), Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(0, file.LengthsRepaired);
        StringAssert.Contains(PageText(file, 5), "(A)");
    }

    /// <summary>
    /// The shape of qpdf's hybrid-xref.pdf: a classic file, then an update whose classic section is
    /// empty and names, with /XRefStm, an xref stream that lists every object, its own entry wrong.
    /// </summary>
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void HybridUpdateIsReadWithoutRepair(bool misplaceOwnEntry, bool inMemory)
    {
        byte[] pdf = HybridUpdate(misplaceOwnEntry);
        PdfFile file = inMemory ? PdfFile.Open(pdf, Limit) : PdfFile.Open(new MemoryStream(pdf), Limit);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(0, file.LengthsRepaired);
        Assert.AreEqual(2, file.RevisionCount);
        StringAssert.Contains(PageText(file, 5), "(New)");
    }

    [TestMethod]
    public void OptimizerFlattensHybridUpdateWhenAllowed()
    {
        byte[] pdf = HybridUpdate(misplaceOwnEntry: true);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(pdf, new PdfOptimizerOptions { MinSavingPercent = 0 });
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "incremental updates");
        CollectionAssert.AreEqual(pdf, output);

        (output, result) = PdfOptimizer.Optimize(pdf, new PdfOptimizerOptions { MinSavingPercent = 0, AllowIncrementalUpdates = true });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        StringAssert.Contains(PdfDocument.Open(output).Pages[0].ExtractText(), "New");
    }

    /// <summary>
    /// <see cref="PdfBuilder.Simple"/> with an update that replaces the content stream (object 4)
    /// through a hybrid section. The uncompressed xref stream is object 6.
    /// </summary>
    private static byte[] HybridUpdate(bool misplaceOwnEntry)
    {
        byte[] original = PdfBuilder.Simple("Old").Build();
        string text = Compat.Latin1.GetString(original);
        long prev = long.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        var offsets = new long[7];
        for (int n = 1; n <= 5; n++) offsets[n] = text.IndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + 1;

        var ms = new MemoryStream();
        ms.Write(original);
        offsets[4] = ms.Position;
        ms.Write(Compat.Latin1.GetBytes("4 0 obj\n<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream\nendobj\n"));
        offsets[6] = ms.Position;
        long own = misplaceOwnEntry ? offsets[4] : offsets[6];

        // W [1 4 2], no filter: type, offset, generation.
        var rows = new byte[7 * 7];
        for (int n = 1; n <= 6; n++)
        {
            long at = n == 6 ? own : offsets[n];
            rows[n * 7] = 1;
            for (int i = 0; i < 4; i++) rows[n * 7 + 1 + i] = (byte)(at >> (24 - 8 * i));
        }

        rows[5] = 0xFF;
        rows[6] = 0xFF;
        ms.Write(Compat.Latin1.GetBytes($"6 0 obj\n<</Type/XRef/Size 7/W[1 4 2]/Root 1 0 R/Length {rows.Length}>>\nstream\n"));
        ms.Write(rows);
        ms.Write("\nendstream\nendobj\n"u8);
        long xref = ms.Position;
        ms.Write(Compat.Latin1.GetBytes($"xref\n0 0\ntrailer\n<</Size 7/Root 1 0 R/Prev {prev}/XRefStm {offsets[6]}>>\nstartxref\n{xref}\n%%EOF\n"));
        return ms.ToArray();
    }

    [TestMethod]
    public void PrevLoopTerminates()
    {
        byte[] v1 = PdfBuilder.Simple("x").Build();
        string text = Compat.Latin1.GetString(v1);
        int startxref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        string looped = text.Replace("/Root 1 0 R>>", $"/Root 1 0 R/Prev {startxref}>>");
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(looped), Limit);
        Assert.IsNotNull(file.ResolveDictionary(file.Trailer["Root"]));
    }

    /// <summary>
    /// The newest section of an incremental update lists only what changed and names itself as
    /// /Prev, so following the chain alone never reaches the catalog or the page tree. The file
    /// must be rebuilt from its objects, with the update still winning, and flagged as repaired so
    /// the optimizer does not rewrite it into a document without pages.
    /// </summary>
    [TestMethod]
    public void PrevLoopHidingOlderSectionsIsRebuilt()
    {
        byte[] v1 = PdfBuilder.Simple("Old").Build();
        string newContent = "<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream";
        string text = Compat.Latin1.GetString(PdfBuilder.AppendUpdate(v1, new Dictionary<int, string> { [4] = newContent }, size: 6));
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        string own = text[(at + 9)..].Trim().Split('\n')[0].Trim();
        int prev = text.LastIndexOf("/Prev ", StringComparison.Ordinal);
        string looped = text[..(prev + 6)] + own + text[text.IndexOf(">>", prev, StringComparison.Ordinal)..];

        byte[] pdf = Compat.Latin1.GetBytes(looped);
        PdfFile file = PdfFile.Open(pdf, Limit);
        Assert.IsTrue(file.WasReconstructed);
        StringAssert.Contains(PageText(file, 5), "(New)");
        Assert.AreEqual(1, PdfDocument.Open(pdf).Pages.Count);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(pdf);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(pdf, output);

        (output, result) = PdfOptimizer.Optimize(pdf, new PdfOptimizerOptions { AllowRepairedFiles = true, MinSavingPercent = 0 });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition);
        StringAssert.Contains(PdfDocument.Open(output).Pages[0].ExtractText(), "New");
    }

    [TestMethod]
    public void BrokenOffsetsTriggerReconstruction()
    {
        byte[] pdf = PdfBuilder.Simple("Hello").Build();
        string text = Compat.Latin1.GetString(pdf);

        // Shift every object by inserting a comment, so every xref offset is now wrong.
        int firstObject = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        string broken = text[..firstObject] + "% padding that moves every object\n" + text[firstObject..];

        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(broken), Limit);
        Assert.IsTrue(file.WasReconstructed);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    public void MissingStartxrefTriggersReconstruction()
    {
        byte[] pdf = PdfBuilder.Simple("Hello").Build();
        string text = Compat.Latin1.GetString(pdf);
        string truncated = text[..text.LastIndexOf("startxref", StringComparison.Ordinal)];
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(truncated), Limit);
        Assert.IsTrue(file.WasReconstructed);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
    }

    [TestMethod]
    public void WrongStreamLengthIsRecoveredFromEndstream()
    {
        var b = PdfBuilder.Simple("Hello");
        byte[] pdf = b.Build();
        string text = new System.Text.RegularExpressions.Regex(@"/Length \d+>>").Replace(Compat.Latin1.GetString(pdf), "/Length 99999999>>", 1);
        PdfFile file = PdfFile.Open(Compat.Latin1.GetBytes(text), Limit);
        StringAssert.Contains(PageText(file, 5), "(Hello)");
        Assert.AreEqual(1, file.LengthsRepaired);
    }

    [TestMethod]
    public void MissingObjectResolvesToNull()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Metadata 999 0 R>>");
        PdfFile file = PdfFile.Open(b.Build(), Limit);
        Assert.IsInstanceOfType<PdfNull>(file.Resolve(file.ResolveDictionary(file.Trailer["Root"])!["Metadata"]));
    }

    [TestMethod]
    public void GarbageIsRejected()
    {
        Assert.ThrowsExactly<PdfFormatException>(() => PdfFile.Open("hello world"u8.ToArray(), Limit));
        Assert.ThrowsExactly<PdfFormatException>(() => PdfFile.Open("%PDF-1.4\nnothing here"u8.ToArray(), Limit));
    }
}
