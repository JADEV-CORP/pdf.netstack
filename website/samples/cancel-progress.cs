#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.3
using PdfNetStack;

#region progress
// Report progress and allow cancelling a long merge.
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
PdfDocument[] parts = [PdfDocument.Open("a.pdf"), PdfDocument.Open("b.pdf"), PdfDocument.Open("c.pdf")];
using PdfDocument merged = PdfDocument.Merge(parts, new ConsoleProgress("merged"), cancel.Token);
await merged.SaveAsync("merged.pdf", cancellationToken: cancel.Token);
#endregion

#region split-to
// One file per page, each stream closed before the next page is written.
using PdfDocument report = PdfDocument.Open("report.pdf");
int written = report.SplitTo(
    page => File.Create($"report-page-{page + 1}.pdf"),
    progress: new ConsoleProgress("pages"),
    cancellationToken: cancel.Token);
#endregion

#region merge-files
// Merge files from disk, opening each when its turn comes and closing it once read.
string[] paths = ["a.pdf", "b.pdf", "c.pdf"];
await using (FileStream output = File.Create("merged-from-files.pdf"))
{
    await PdfDocument.MergeAsync(paths.Select(File.OpenRead), output, cancel.Token);
}
#endregion

#region extract
using PdfDocument source = PdfDocument.Open("report.pdf");
using PdfDocument firstTwo = source.ExtractPages(0, 2);
using PdfDocument lastOne = source.ExtractPages(^1..);
firstTwo.Save("report-first-two.pdf");
#endregion

Console.WriteLine($"{merged.Pages.Count} merged pages, {written} split files, {lastOne.Pages.Count} page extracted");

/// <summary>Writes each report as it arrives, on the thread doing the work.</summary>
sealed class ConsoleProgress(string what) : IProgress<PdfProgress>
{
    public void Report(PdfProgress value) => Console.WriteLine($"{what}: {value.Completed} of {value.Total?.ToString() ?? "?"}");
}
