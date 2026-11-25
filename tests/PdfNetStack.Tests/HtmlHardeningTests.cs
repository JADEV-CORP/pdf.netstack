using System.Diagnostics;
using System.Text;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Work the HTML converter does for hostile input is counted where it multiplies: pseudo-element
/// rules, long selectors and class lists, long text, repeated fixed boxes. Counts, not clocks, so
/// the bounds hold on any machine.
/// </summary>
[TestClass]
public sealed class HtmlHardeningTests
{
    private static string Repeat(int count, Func<int, string> part)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(part(i));
        return sb.ToString();
    }

    private static (StyleResolver Resolver, WorkBudget Budget, HtmlElement P) Resolver(string html)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var budget = WorkBudget.Unlimited();
        var resolver = new StyleResolver(CssMedia.Default, budget);
        resolver.AddDocument(doc);
        return (resolver, budget, doc.Body.Descendants().First(e => e.Name == "p"));
    }

    [TestMethod]
    public void PseudoElementRulesForAnotherPseudoElementCountAsStyleWork()
    {
        // Every element asks for its before and after style; the rules for the other one were skipped uncounted.
        (StyleResolver resolver, WorkBudget budget, HtmlElement p) = Resolver("<style>" + Repeat(1000, i => $"p::after{{content:'{i}'}}") + "</style><p>x</p>");

        long before = budget.StyleWork;
        Assert.IsFalse(resolver.HasRulesFor(p, "before"));
        Assert.IsGreaterThanOrEqualTo(1000, budget.StyleWork - before);

        before = budget.StyleWork;
        resolver.Compute(p, null, "before");
        Assert.IsGreaterThanOrEqualTo(1000, budget.StyleWork - before);
    }

    [TestMethod]
    public void EveryConditionOfACompoundCountsAsStyleWork()
    {
        HtmlElement p = HtmlParser.Parse("<p>x</p>").Body.Descendants().First(e => e.Name == "p");
        var budget = WorkBudget.Unlimited();

        // The first condition rejects at once, but a compound of 200 conditions is 200 units of the sheet.
        Assert.IsFalse(CssSelector.Parse(string.Concat(Enumerable.Range(0, 200).Select(i => ".c" + i)))!.Single().Matches(p, budget));

        Assert.IsGreaterThanOrEqualTo(200, budget.StyleWork);
    }

    [TestMethod]
    public void ScanningALongClassListCountsAsStyleWork()
    {
        HtmlElement p = HtmlParser.Parse("<p class=\"" + string.Join(" ", Enumerable.Range(0, 4096).Select(i => "k" + i)) + "\">x</p>").Body.Descendants().First(e => e.Name == "p");
        var budget = WorkBudget.Unlimited();

        Assert.IsFalse(CssSelector.Parse(".none")!.Single().Matches(p, budget));

        Assert.IsGreaterThanOrEqualTo(200, budget.StyleWork);
    }

    [TestMethod]
    public void ParsingTextObservesCancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => HtmlParser.Parse(new string('x', 1_000_000), cancel.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => HtmlParser.Parse("<textarea>" + new string('&', 1_000_000) + "</textarea>", cancel.Token));
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void ALongWordBrokenAnywhereIsLaidOutInLinearTime()
    {
        // 200,000 lines of one character from one shaped run: each line scanned all the glyphs of
        // the run, 20 billion tests. Laid out alone: pagination of that many lines is a cost of its own.
        HtmlDocument doc = HtmlParser.Parse("<p style=\"width:1px;overflow-wrap:anywhere\">" + new string('x', 200_000) + "</p>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver).Build(doc);
        var engine = new LayoutEngine(new FontResolver(null, useSystemFonts: false), WorkBudget.Unlimited());

        var clock = Stopwatch.StartNew();
        engine.Layout(root, 600);

        Assert.IsLessThan(15_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void FixedBoxesRepeatedOnEveryPageCountTheirWholeSubtree()
    {
        // A fixed box paints, whole, on each of 1,000 pages: 2,000,000 boxes drawn for 1,000 units charged before.
        string html = "<div style=\"position:fixed;top:0\">" + Repeat(2000, _ => "<span>x</span>") + "</div>"
            + Repeat(1000, i => "<p style=\"break-before:page\">p" + i + "</p>");

        HtmlConversionException e = Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertToDocument(html, new HtmlConverterOptions { UseSystemFonts = false, MaxLayoutWork = 500_000, MaxPages = 100_000 }));

        StringAssert.Contains(e.Message, "MaxLayoutWork");
    }
}
