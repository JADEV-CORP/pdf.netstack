#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// The PdfNetStack side of the PDFsharp migration guide, one region per before and after pair.
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

#region open
PdfDocument doc = PdfDocument.Open("contract.pdf");   // no open mode: every document can be edited and imported from
doc.Pages.RemoveAt(0);
doc.Pages[0].Rotation = 90;                           // PDFsharp: page.Rotate = 90
doc.Info.Title = "Signed contract";
doc.Outlines.Add("Annex", doc.Pages[^1]);
doc.Save("contract-final.pdf");                       // optimized and verified by default
#endregion

#region create
PdfDocument invoice = PdfDocument.Create();
PdfPage page = invoice.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())         // origin top left, y down, points: as in XGraphics
{
    PdfFont bold = PdfStandardFont.HelveticaBold;     // a font is a file or a standard font, no resolver
    g.DrawRectangle(null, PdfColor.FromRgb(30, 58, 138), 0, 0, page.Width, 140);
    g.DrawString("Invoice 2026-114", bold, 28, PdfColor.White, 40, 80);   // size per call, y is the baseline
    g.DrawLine(new PdfPen(PdfColor.Gray(0.5), 0.5), 40, 160, 555, 160);

    PdfFont body = PdfStandardFont.Helvetica;
    double width = body.MeasureString("Total: 1,250.00 EUR", 14);
    g.DrawString("Total: 1,250.00 EUR", body, 14, PdfColor.Black, 555 - width, 200);

    PdfImage logo = PdfImage.Load("logo.png");
    g.DrawImage(logo, 40, 220, 120, 40);

    // XTextFormatter: wraps at the width, top left corner at (40, 280).
    g.DrawText("Payment is due within 30 days of the invoice date.", new PdfTextStyle(body, 14), 40, 280, 300);
}
invoice.Save("invoice.pdf");
#endregion

#region fonts
PdfFont inter = PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2");   // .ttf .otf .ttc .woff .woff2, embedded as a subset
PdfFont? arial = PdfFontCollection.System.Find("Arial");              // installed fonts; null when there are none
var brand = new PdfFontCollection();
brand.AddDirectory("fonts");
PdfFont heading = (PdfFont?)brand.Find("Inter", weight: 700) ?? PdfStandardFont.HelveticaBold;
#endregion

#region merge
string[] files = ["a.pdf", "b.pdf", "c.pdf"];
PdfDocument merged = PdfDocument.Merge(files.Select(file => PdfDocument.Open(file)));
merged.Save("merged.pdf");

// Or page by page, as with AddPage: shared fonts and images are copied once.
PdfDocument output = PdfDocument.Create();
foreach (string file in files)
{
    output.Pages.Import(PdfDocument.Open(file).Pages);
}
output.Save("merged-by-import.pdf");
#endregion

#region split
int number = 0;
foreach (PdfDocument single in PdfDocument.Open("report.pdf").Split())
{
    single.Save($"report-page-{++number}.pdf");
}
#endregion

// A one page letterhead for the stamp region; PDFsharp readers would have their own file.
PdfDocument letterheadSource = PdfDocument.Create();
using (PdfGraphics g = letterheadSource.Pages.Add(PdfRectangle.A4).CreateGraphics())
{
    g.DrawRectangle(null, PdfColor.FromHex("#E0F2FE"), 0, 0, g.Width, 120);
    g.DrawString("Example Company, 1 Main Street", PdfStandardFont.Helvetica, 9, PdfColor.FromHex("#0369A1"), 400, 24);
}
letterheadSource.Save("letterhead.pdf");

#region stamp
PdfDocument report = PdfDocument.Open("report.pdf");
PdfForm letterhead = PdfForm.FromPage(report, PdfDocument.Open("letterhead.pdf").Pages[0]);   // copied once
foreach (PdfPage reportPage in report.Pages)
{
    using PdfGraphics g = reportPage.CreateGraphics(PdfGraphicsLayer.Background);   // XGraphicsPdfPageOptions.Prepend
    g.DrawForm(letterhead, 0, 0, reportPage.Width, reportPage.Height);
}
report.Save("report-letterhead.pdf");
#endregion

#region forms
PdfDocument application = PdfDocument.Open("application.pdf");
((PdfTextField)application.Form["name"]!).Text = "Jane Doe";   // the appearance is drawn now
((PdfCheckBoxField)application.Form["subscribe"]!).Checked = true;       // no NeedAppearances needed
application.Save("application-filled.pdf");
#endregion

#region encrypt
PdfDocument secret = PdfDocument.Open("report.pdf");
secret.Security.Encrypt(
    userPassword: "read",
    ownerPassword: "admin",
    permissions: PdfPermissions.Print | PdfPermissions.CopyContent);    // AES-256 by default
secret.Save("report-protected.pdf");
#endregion

#region compression
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual-small.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");   // lossless, proved equivalent
#endregion

#region details
PdfDocument details = PdfDocument.Open("report.pdf");
PdfPage first = details.Pages[0];
first.Annotations.AddLink(72, 700, 120, 14, new Uri("https://example.com"));   // top left coordinates, like drawing
first.Annotations.AddLink(72, 720, 120, 14, details.Pages[2]);
details.Catalog.Set("PageMode", new PdfName("UseOutlines"));                   // PDFsharp: doc.Internals.Catalog
Console.WriteLine(first.ExtractText());                                       // text in reading order
const double Millimeter = 72 / 25.4;                                          // XUnit.FromMillimeter(10) is 10 * Millimeter
details.Save("report-details.pdf");
#endregion

Console.WriteLine($"{arial?.FamilyName ?? "no Arial"}, {inter.FamilyName}, {heading.FamilyName}, {Millimeter:F3} points per mm");
Console.WriteLine($"merged: {merged.Pages.Count} and {output.Pages.Count} pages, split: {number} files");
