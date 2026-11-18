#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.3
using PdfNetStack;

#region open
// Files from users of a service: the Untrusted preset, with a shorter time limit per call.
var options = new PdfLoadOptions
{
    Limits = PdfLimits.Untrusted with { Timeout = TimeSpan.FromSeconds(20) },
    OnWarning = warning => Console.WriteLine($"warning: {warning.Message}"),   // as they happen
};
using PdfDocument doc = PdfDocument.Open("report.pdf", options);
foreach (PdfPage page in doc.Pages)
{
    Console.WriteLine(page.ExtractText());
}

// A page whose budget ran out reads as far as it got; the diagnostics say what is missing.
foreach (PdfDiagnostic diagnostic in doc.Diagnostics)
{
    Console.WriteLine($"{diagnostic.Kind} {diagnostic.Limit} page {diagnostic.PageIndex}: {diagnostic.Message}");
}
#endregion

#region errors
try
{
    using PdfDocument upload = PdfDocument.Open("manual.pdf", new PdfLoadOptions { Limits = PdfLimits.Untrusted });
    Console.WriteLine($"{upload.Pages.Count} pages");
}
catch (PdfLimitExceededException e)          // too costly: e.Limit says which limit
{
    Console.WriteLine($"Refused ({e.Limit}): {e.Message}");
}
catch (PdfPasswordException)                 // encrypted, and no password opens it
{
    Console.WriteLine("Encrypted");
}
catch (PdfFormatException e)                 // damaged beyond repair, or not a PDF
{
    Console.WriteLine($"Not readable: {e.Message}");
}
#endregion

#region optimize
var optimizerOptions = new PdfOptimizerOptions { Limits = PdfLimits.Untrusted };
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual-small.pdf", optimizerOptions);
if (result.Disposition == OptimizationDisposition.LimitExceeded)
{
    // The input was written unchanged; the diagnostics name the limit.
    Console.WriteLine(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
}
#endregion

#region inspect
PdfInspection inspection = PdfInspector.Inspect(File.ReadAllBytes("report.pdf"), PdfLimits.Untrusted);
Console.WriteLine($"{inspection.PageCount} pages, {inspection.Diagnostics.Count} warnings");
#endregion

#region long-lived
// A document kept open and read again and again, such as one held in a cache, spends its
// budgets on every read. Lift the totals for it, and keep the per call time limit.
var cached = new PdfLoadOptions
{
    Limits = PdfLimits.Default with
    {
        MaxTotalDecodedBytes = long.MaxValue,
        MaxTotalImagePixels = long.MaxValue,
        MaxOperationsPerDocument = long.MaxValue,
        Timeout = TimeSpan.FromSeconds(30),
    },
};
using PdfDocument catalogue = PdfDocument.Open("report.pdf", cached);
#endregion

Console.WriteLine($"{result.Disposition}, {catalogue.Pages.Count} pages in the cached document");
