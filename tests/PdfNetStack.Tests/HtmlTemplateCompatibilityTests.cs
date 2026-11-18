using PdfNetStack.Drawing;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// What public invoice and report templates of the wkhtmltopdf era rely on, each reduced to a few
/// lines: fonts next to a linked style sheet, logos sized in percent inside layout tables,
/// responsive grids printed on paper, clearfix rows across a page break, right to left tables and
/// the alignment of email layouts.
/// </summary>
[TestClass]
public sealed class HtmlTemplateCompatibilityTests
{
    private static readonly HtmlConverterOptions Standard = new() { UseSystemFonts = false };

    /// <summary>Base font names of a page's fonts, read back from the saved file.</summary>
    private static List<string> FontNames(PdfDocument created, int page)
    {
        PdfDocument doc = PdfDocument.Open(created.ToArray());
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        if (doc.Resolve(resources["Font"]) is not PdfDictionary fonts) return [];
        return [.. fonts.Select(f => ((PdfName)((PdfDictionary)doc.Resolve(f.Value)!)["BaseFont"]!).Value)];
    }

    private static Dictionary<HtmlElement, ComputedStyle> Styles(HtmlDocument doc, StyleResolver resolver)
    {
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants()) styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        return styles;
    }

    [TestMethod]
    public void UrlsInALinkedStyleSheetResolveAgainstTheSheet()
    {
        // site/index.html links css/site.css, which imports theme.css next to it and takes its font
        // from ../fonts: both resolve against css/, as in browsers, not against the page.
        string site = Path.Combine(Path.GetTempPath(), "pdfnetstack-sheet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(site, "css"));
        Directory.CreateDirectory(Path.Combine(site, "fonts"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"), Path.Combine(site, "fonts", "brand.ttf"));
            File.WriteAllText(Path.Combine(site, "css", "site.css"), "@import \"theme.css\"; @font-face { font-family: Brand; src: url(../fonts/brand.ttf) } h1 { font-family: Brand }");
            File.WriteAllText(Path.Combine(site, "css", "theme.css"), "h1 { width: 123px }");
            string page = Path.Combine(site, "index.html");
            File.WriteAllText(page, "<link rel=stylesheet href=\"css/site.css\"><h1>Invoice</h1>");

            PdfDocument doc = HtmlConverter.ConvertFileToDocument(page, Standard);
            Assert.IsTrue(FontNames(doc, 0).Any(n => n.Contains("Test")), string.Join(", ", FontNames(doc, 0)));

            var resources = new Resources(new Uri(page), null);
            HtmlDocument html = HtmlParser.Parse(File.ReadAllText(page));
            var resolver = new StyleResolver(CssMedia.Default) { ResolveUrl = resources.Resolve };
            resolver.AddDocument(html, resources.LoadText);
            Assert.AreEqual(123, Styles(html, resolver).Single(s => s.Key.Name == "h1").Value.Length("width"));
        }
        finally
        {
            Directory.Delete(site, recursive: true);
        }
    }

    [TestMethod]
    public void StyleSheetUrlsAreRebasedBeforeParsing()
    {
        List<CssRule> rules = CssParser.ParseStyleSheet(
            "@import \"a.css\"; @import url(b.css); @font-face { src: url(f.woff2), url( \"g.woff2\" ) } p { background: url(i.png) } q { background: url(data:image/png;base64,AA==) }",
            url => url.StartsWith("data:", StringComparison.Ordinal) ? null : "sheet/" + url);
        CollectionAssert.AreEqual(new[] { "sheet/a.css", "sheet/b.css" }, rules.OfType<CssImportRule>().Select(r => r.Url).ToArray());
        string text = string.Join(" ", rules.OfType<CssFontFaceRule>().SelectMany(r => r.Declarations).Concat(rules.OfType<CssStyleRule>().SelectMany(r => r.Declarations)));
        StringAssert.Contains(text, "sheet/f.woff2");
        StringAssert.Contains(text, "sheet/g.woff2");
        StringAssert.Contains(text, "sheet/i.png");
        StringAssert.Contains(text, "data:image/png;base64,AA==");

        var resources = new Resources(new Uri("https://example.test/shop/invoice.html"), _ => null);
        Assert.AreEqual("https://example.test/shop/css/fonts/a.woff2", resources.Resolve("fonts/a.woff2", "https://example.test/shop/css/site.css"));
        Assert.AreEqual("https://example.test/shop/logo.png", resources.Resolve("logo.png", null));
        Assert.AreEqual("#clip", resources.Resolve("#clip", "https://example.test/shop/css/site.css"));
        Assert.IsNull(new Resources(null, null).Resolve("logo.png", null));
    }

    [TestMethod]
    public void ALogoSizedInPercentKeepsItsWidthInALayoutTable()
    {
        // The common invoice header: a logo at width 100% with a max-width, beside the invoice
        // number. A percentage of a width not known yet once made the logo's column, and the logo, 0px wide.
        // A logo of 600 by 100.
        var image = new HtmlImage(PdfSvg.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"100\"><rect width=\"600\" height=\"100\"/></svg>"));
        HtmlDocument doc = HtmlParser.Parse("<table style=\"width:100%\"><tr><td><img id=logo src=logo.svg style=\"width:100%;max-width:300px\"></td><td>Invoice #: 123<br>Due: February 1</td></tr></table>");
        var resolver = new StyleResolver(new CssMedia(600, 1000));
        resolver.AddDocument(doc);
        BlockBox box = new BoxBuilder(resolver, _ => image).Build(doc);
        BoxFragment root = new LayoutEngine(new FontResolver(null, useSystemFonts: false)).Layout(box, 600);
        BoxFragment logo = HtmlLayoutFixture.ById(root, "logo");
        Assert.AreEqual(300, logo.Width, HtmlLayoutFixture.Tolerance);
        Assert.AreEqual(50, logo.Height, HtmlLayoutFixture.Tolerance);
    }

    [TestMethod]
    public void MediaQueriesSeeThePageArea()
    {
        // A4 is 794px wide; with 2cm margins the page area is 642px, so a 768px breakpoint for
        // desktop screens does not apply, as when a browser prints.
        Assert.AreEqual(793.7 - (2 * 96 / 2.54 * 2), PageMaster.DefaultPageArea("size: A4; margin: 2cm").Width, 0.1);
        Assert.AreEqual(793.7, PageMaster.DefaultPageArea("size: A4; margin: 0").Width, 0.1);

        const string html = "<style>@media (min-width: 768px) { .narrow { display: none } }</style><p class=narrow>Stacked</p><p>Always</p>";
        StringAssert.Contains(PdfDocument.Open(HtmlConverter.ConvertToPdf(html, Standard)).Pages[0].ExtractText(), "Stacked");
        string wide = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false, PageStyle = "size: A4 landscape; margin: 1cm" })).Pages[0].ExtractText();
        Assert.IsFalse(wide.Contains("Stacked"), wide);
    }

    [TestMethod]
    public void AClearfixRowKeepsItsFloatsAcrossAPageBreak()
    {
        // Bootstrap's row: floated columns, then ::after with clear: both. The row starts where its
        // floats do, so a break before it puts the floats on the next page instead of skipping them.
        const string css = ".row::after { content: \" \"; display: table; clear: both } .col { float: left; width: 50%; height: 400px }";
        BoxFragment root = HtmlLayoutFixture.Layout("<div style=\"height:900px\">x</div><div id=row class=row><div class=col>Paypal details</div></div>", css);
        BoxFragment row = HtmlLayoutFixture.ById(root, "row");
        Assert.AreEqual(row.Children.OfType<BoxFragment>().First().Y, row.Y, HtmlLayoutFixture.Tolerance);
        Assert.AreEqual(400, row.Height, HtmlLayoutFixture.Tolerance);

        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf($"<style>{css}</style><div style=\"height:900px\">x</div><div class=row><div class=col>Paypal details</div></div>", Standard));
        Assert.HasCount(2, doc.Pages);
        StringAssert.Contains(doc.Pages[1].ExtractText(), "Paypal details");
    }

    [TestMethod]
    public void PercentageColumnsGiveWayToTheMinimumsOfOthers()
    {
        // A payslip header: 57% and 55% columns, the second holding boxes that do not wrap. The
        // percentages are cut to 100% in total and the first column narrows, so the boxes stay
        // inside the table instead of running off the page.
        BoxFragment root = HtmlLayoutFixture.Layout(
            "<table id=t style=\"width:500px; border-spacing:0\"><tr><td id=a style=\"width:57%\">Brochhaus Consulting, 3 Fusionopolis Way</td>"
            + "<td id=b style=\"width:55%; white-space:nowrap\">Tax ID: S8812345D Department: Engineering</td></tr></table>");
        BoxFragment table = HtmlLayoutFixture.ById(root, "t"), b = HtmlLayoutFixture.ById(root, "b");
        double nowrap = HtmlLayoutFixture.Lines(b).Single().Width;
        Assert.AreEqual(500, table.Width, HtmlLayoutFixture.Tolerance);
        Assert.IsLessThanOrEqualTo(table.X + table.Width + HtmlLayoutFixture.Tolerance, b.X + b.Width);
        Assert.IsGreaterThanOrEqualTo(nowrap - HtmlLayoutFixture.Tolerance, b.ContentWidth);
        Assert.IsLessThan(0.57 * 500, HtmlLayoutFixture.ById(root, "a").Width);
    }

    [TestMethod]
    public void RightToLeftTablesStartFromTheRight()
    {
        BoxFragment root = HtmlLayoutFixture.Layout(
            "<table dir=rtl style=\"width:400px; border-collapse: collapse\"><tr><td id=first style=\"border-right: 3px solid\">one</td><td id=second>two</td></tr></table>");
        BoxFragment first = HtmlLayoutFixture.ById(root, "first"), second = HtmlLayoutFixture.ById(root, "second");
        BoxFragment table = HtmlLayoutFixture.All(root).OfType<BoxFragment>().First(f => f.Box is TableBox);
        Assert.IsGreaterThan(second.X, first.X);
        Assert.AreEqual(table.X + table.Width, first.X + first.Width, 2);

        // Collapsed borders keep their physical sides: the first cell's border-right is now the
        // table's outer edge, which the cell owns whole, and its left edge is shared with "two".
        Assert.AreEqual(3, first.Border.Right, HtmlLayoutFixture.Tolerance);
        Assert.AreEqual(0, first.Border.Left, HtmlLayoutFixture.Tolerance);
    }

    [TestMethod]
    public void NestedTablesDropTheAlignmentOfAnAlignAttribute()
    {
        // Email layouts center an outer cell with align=center; the text of the tables inside
        // starts on the left, as browsers lay it out.
        HtmlDocument doc = HtmlParser.Parse(
            "<table><tr><td id=outer align=center>Centered<table><tr><td id=inner>Left</td></tr></table></td></tr></table><center><table><tr><td id=old>Left too</td></tr></table></center>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        Dictionary<HtmlElement, ComputedStyle> styles = Styles(doc, resolver);
        string Align(string id) => styles.Single(s => s.Key.Id == id).Value.Keyword("text-align");
        Assert.AreEqual("-webkit-center", Align("outer"));
        Assert.AreEqual("start", Align("inner"));
        Assert.AreEqual("start", Align("old"));

        // The legacy values still align the cell's own text.
        BoxFragment root = HtmlLayoutFixture.Layout("<div style=\"width:600px\" align=center><span id=t>Centered</span></div>");
        LineFragment line = HtmlLayoutFixture.All(root).OfType<LineFragment>().First();
        Assert.IsGreaterThan(200, line.Items.Min(i => i.X));
    }
}
