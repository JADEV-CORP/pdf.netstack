using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfNetStack;
using PdfNetStack.Html;
using PdfNetStack.TextBench;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Pig = UglyToad.PdfPig.PdfDocument;

// Text extraction for retrieval, PdfNetStack against PdfPig.
//
//   dotnet run --project tools/PdfNetStack.TextBench -c Release -- --work <dir> [--chrome <path>] [--corpus <dir>] [--runs 3]
//
// Sets: the HTML documents of Documents.cs printed by Chrome (tagged PDF, an independent producer)
// and converted by PdfNetStack.Html (tagged, PdfNetStack's own producer: read it as a check, not a
// win); the hidden text cases of HiddenSet.cs; and, with --corpus, public files from
// eng/corpus/manifest.json (fetched by tools/fetch_corpus.py), which have no ground truth and
// are timed only. Everything generated goes to --work; nothing is written elsewhere.
string work = Arg("--work") ?? throw new ArgumentException("--work <dir> is required");
string? chrome = Arg("--chrome") ?? (OperatingSystem.IsMacOS() ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" : null);
string? corpus = Arg("--corpus");
int runs = int.Parse(Arg("--runs") ?? "3", CultureInfo.InvariantCulture);
Directory.CreateDirectory(work);

var tools = new (string Name, Func<byte[], string> Read)[]
{
    ("PdfPig 0.1.16, template (NN words + Docstrum blocks)", PigTemplate),
    ("PdfPig 0.1.16, ContentOrderTextExtractor", PigContentOrder),
    ("PdfNetStack ExtractText (per page)", NetStackExtractText),
    ("PdfNetStack ReadText, layout order", bytes => NetStackRead(bytes, new PdfTextOptions { UseStructureTree = false })),
    ("PdfNetStack ReadText, defaults", bytes => NetStackRead(bytes, PdfTextOptions.Default)),
};

var sets = new List<(string Set, List<(string Id, byte[] Pdf, string Truth)> Docs)>();
if (chrome is not null && File.Exists(chrome)) sets.Add(("Chrome " + ChromeVersion(chrome), Generate("chrome", PrintWithChrome)));
else Console.Error.WriteLine("Chrome not found: the Chrome set is skipped.");
sets.Add(("PdfNetStack.Html", Generate("pdfnetstack", (html, _) => HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = true, Tagged = true }))));
sets.Add(("Shuffled content, untagged", [.. ScrambledSet.All]));

var report = new StringBuilder();
report.AppendLine("| Set | Tool | Pages | Word precision | Word recall | Word F1 | Order | ms per page |");
report.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
foreach ((string set, List<(string Id, byte[] Pdf, string Truth)> docs) in sets)
{
    int pages = docs.Sum(d => PageCount(d.Pdf));
    foreach ((string name, Func<byte[], string> read) in tools)
    {
        (List<string> outputs, double ms) = Time(docs.Select(d => d.Pdf).ToList(), read);
        long truthWords = 0, outputWords = 0, matched = 0, ordered = 0;
        string dump = Path.Combine(work, "out", Slug(set), Slug(name));
        Directory.CreateDirectory(dump);
        for (int i = 0; i < docs.Count; i++)
        {
            File.WriteAllText(Path.Combine(dump, docs[i].Id + ".txt"), outputs[i]);
            File.WriteAllText(Path.Combine(work, "out", docs[i].Id + ".truth.txt"), docs[i].Truth);
            List<string> truth = Metrics.Words(docs[i].Truth), output = Metrics.Words(outputs[i]);
            truthWords += truth.Count;
            outputWords += output.Count;
            matched += Metrics.Matched(truth, output);
            ordered += Metrics.InOrder(truth, output);
        }

        double precision = (double)matched / Math.Max(1, outputWords), recall = (double)matched / Math.Max(1, truthWords);
        report.AppendLine(CultureInfo.InvariantCulture, $"| {set} | {name} | {pages} | {precision:P1} | {recall:P1} | {2 * precision * recall / Math.Max(1e-9, precision + recall):P1} | {(double)ordered / Math.Max(1, truthWords):P1} | {ms / pages:F2} |");
    }
}

// Hidden text: a codeword absent from the output is caught; a control's codeword must be present.
report.AppendLine();
report.AppendLine("| Tool | Hidden lines left out | Visible controls kept | Visible words kept | ms per page |");
report.AppendLine("|---|---:|---:|---:|---:|");
IReadOnlyList<(string Id, bool Hidden, string Codeword, byte[] Pdf)> cases = HiddenSet.All;
var caughtBy = new Dictionary<string, List<string>>();
foreach ((string name, Func<byte[], string> read) in tools)
{
    (List<string> outputs, double ms) = Time(cases.Select(c => c.Pdf).ToList(), read);
    int caught = 0, kept = 0;
    long visible = 0, visibleKept = 0;
    caughtBy[name] = [];
    for (int i = 0; i < cases.Count; i++)
    {
        bool present = outputs[i].Contains(cases[i].Codeword, StringComparison.Ordinal);
        if (cases[i].Hidden && !present)
        {
            caught++;
            caughtBy[name].Add(cases[i].Id);
        }

        if (!cases[i].Hidden && present) kept++;
        List<string> truth = Metrics.Words(HiddenSet.VisibleText);
        visible += truth.Count;
        visibleKept += Metrics.Matched(truth, Metrics.Words(outputs[i]));
    }

    int hiddenCount = cases.Count(c => c.Hidden);
    report.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {caught} of {hiddenCount} | {kept} of {cases.Count - hiddenCount} | {(double)visibleKept / visible:P1} | {ms / cases.Count:F2} |");
}

report.AppendLine();
foreach ((string name, List<string> ids) in caughtBy) report.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {(ids.Count == 0 ? "none" : string.Join(", ", ids))}");

// Public files: time and robustness only.
if (corpus is not null && Directory.Exists(corpus))
{
    List<string> files = [.. Directory.EnumerateFiles(corpus, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
    var bytes = files.Select(File.ReadAllBytes).ToList();
    int pages = bytes.Sum(PageCount);
    report.AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"Public files: {files.Count} files, {pages} pages, {bytes.Sum(b => (long)b.Length) / 1024} KiB.");
    report.AppendLine();
    report.AppendLine("| Tool | Failures | ms per page | Words out |");
    report.AppendLine("|---|---:|---:|---:|");
    foreach ((string name, Func<byte[], string> read) in tools)
    {
        int failures = 0;
        Func<byte[], string> safe = b =>
        {
            try
            {
                return read(b);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures++;
                return "";
            }
        };
        (List<string> outputs, double ms) = Time(bytes, safe);
        report.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {failures / (runs + 1)} | {ms / pages:F2} | {outputs.Sum(o => Metrics.Words(o).Count)} |");
    }

    // What the default reading found on real files: tagged pages, hidden runs, artifacts.
    int structure = 0, layout = 0, hidden = 0;
    var reasons = new Dictionary<PdfHiddenTextReason, int>();
    var samples = new List<string>();
    for (int f = 0; f < bytes.Count; f++)
    {
        try
        {
            using PdfDocument doc = PdfDocument.Open(bytes[f]);
            foreach (PdfPageText page in doc.ReadText())
            {
                if (page.ReadingOrder == PdfReadingOrder.StructureTree) structure++;
                else layout++;
                hidden += page.HiddenText.Count;
                foreach (PdfHiddenText h in page.HiddenText)
                {
                    reasons[h.Reason] = reasons.GetValueOrDefault(h.Reason) + 1;
                    string text = h.Text.ReplaceLineEndings(" ");
                    if (samples.Count < 40) samples.Add($"{Path.GetFileName(files[f])} p{page.PageNumber} {h.Reason}: {(text.Length > 60 ? text[..60] + "..." : text)}");
                }
            }
        }
        catch (Exception e) when (e is PdfException or PdfFormatException)
        {
        }
    }

    report.AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"PdfNetStack ReadText on the public files: {structure} pages in structure order, {layout} in layout order, {hidden} hidden runs ({string.Join(", ", reasons.Select(r => $"{r.Key} {r.Value}"))}).");
    report.AppendLine();
    foreach (string sample in samples) report.AppendLine("    " + sample);
}

Console.WriteLine(report);
File.WriteAllText(Path.Combine(work, "report.md"), report.ToString());
return;

// Writes each document's HTML and its PDF from the producer, once; returns the PDFs with their truth.
List<(string, byte[], string)> Generate(string folder, Func<string, string, byte[]> produce)
{
    string dir = Path.Combine(work, folder);
    Directory.CreateDirectory(dir);
    var docs = new List<(string, byte[], string)>();
    foreach ((string id, string html) in Documents.All)
    {
        string htmlPath = Path.Combine(work, id + ".html");
        File.WriteAllText(htmlPath, html);
        string pdfPath = Path.Combine(dir, id + ".pdf");
        if (!File.Exists(pdfPath)) File.WriteAllBytes(pdfPath, produce(html, htmlPath));
        docs.Add((id, File.ReadAllBytes(pdfPath), Documents.Truth(html)));
    }

    return docs;
}

byte[] PrintWithChrome(string html, string htmlPath)
{
    string output = Path.ChangeExtension(htmlPath, ".chrome.pdf");
    var start = new ProcessStartInfo(chrome!)
    {
        ArgumentList =
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--user-data-dir=" + Path.Combine(work, "chrome-profile"), "--no-pdf-header-footer",
            "--print-to-pdf=" + output, new Uri(htmlPath).AbsoluteUri,
        },
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    using Process process = Process.Start(start)!;
    process.StandardError.ReadToEnd();
    if (!process.WaitForExit(120_000)) process.Kill();
    byte[] pdf = File.ReadAllBytes(output);
    File.Delete(output);
    return pdf;
}

static string ChromeVersion(string path)
{
    using Process process = Process.Start(new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true })!;
    string version = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return version.Replace("Google Chrome ", "", StringComparison.Ordinal);
}

// One warm-up, then the median of the timed runs; the outputs of the first timed run.
(List<string> Outputs, double Ms) Time(List<byte[]> pdfs, Func<byte[], string> read)
{
    foreach (byte[] pdf in pdfs) read(pdf);
    var times = new List<double>();
    List<string>? outputs = null;
    for (int r = 0; r < runs; r++)
    {
        var clock = Stopwatch.StartNew();
        List<string> run = [.. pdfs.Select(read)];
        times.Add(clock.Elapsed.TotalMilliseconds);
        outputs ??= run;
    }

    times.Sort();
    return (outputs!, times[times.Count / 2]);
}

static int PageCount(byte[] pdf)
{
    try
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        return doc.Pages.Count;
    }
    catch (Exception e) when (e is PdfException or PdfFormatException)
    {
        return 0;
    }
}

static string PigTemplate(byte[] pdf)
{
    // As Microsoft's AI chat web template reads a PDF for its ingestion pipeline.
    using Pig doc = Pig.Open(pdf);
    var sb = new StringBuilder();
    foreach (Page page in doc.GetPages())
    {
        IEnumerable<Word> words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        foreach (var block in DocstrumBoundingBoxes.Instance.GetBlocks(words)) sb.AppendLine(block.Text);
    }

    return sb.ToString();
}

static string PigContentOrder(byte[] pdf)
{
    using Pig doc = Pig.Open(pdf);
    var sb = new StringBuilder();
    foreach (Page page in doc.GetPages()) sb.AppendLine(ContentOrderTextExtractor.GetText(page));
    return sb.ToString();
}

static string NetStackExtractText(byte[] pdf)
{
    using PdfDocument doc = PdfDocument.Open(pdf);
    return string.Join("\n", doc.Pages.Select(p => p.ExtractText()));
}

static string NetStackRead(byte[] pdf, PdfTextOptions options)
{
    using PdfDocument doc = PdfDocument.Open(pdf);
    return string.Join("\n\n", doc.ReadText(options).Select(p => p.Text));
}

static string Slug(string s) => new([.. s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);

string? Arg(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
