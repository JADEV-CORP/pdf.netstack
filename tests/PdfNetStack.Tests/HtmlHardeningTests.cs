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
    public void ScanningALongAttributeValueCountsAsStyleWork()
    {
        // 64K words: ~= split them all into strings and *= searched them all, for one unit each.
        HtmlElement p = HtmlParser.Parse("<p a=\"" + Repeat(65_536, _ => "w ") + "\">x</p>").Body.Descendants().First(e => e.Name == "p");
        foreach (string selector in new[] { "[a~=none]", "[a*=none]" })
        {
            var budget = WorkBudget.Unlimited();
            Assert.IsFalse(CssSelector.Parse(selector)!.Single().Matches(p, budget), selector);
            Assert.IsGreaterThanOrEqualTo(256, budget.StyleWork, selector);
        }

        // Words are separated by any HTML space; |= takes the value alone or before a hyphen.
        HtmlElement q = HtmlParser.Parse("<p a=\"one\ttwo\n three\" b=\"en-GB\">x</p>").Body.Descendants().First(e => e.Name == "p");
        string Matching(params string[] selectors) => string.Join(" ", selectors.Where(s => CssSelector.Parse(s)!.Single().Matches(q)));
        Assert.AreEqual("[a~=one] [a~=two] [a~=three] [a~=TWO i] [b|=en] [b|=en-GB] [b|=EN i]", Matching(
            "[a~=one]", "[a~=two]", "[a~=three]", "[a~=TWO i]", "[a~=tw]", "[a~=wo]", "[a~=\"one two\"]", "[a~=\"\"]",
            "[b|=en]", "[b|=en-GB]", "[b|=EN i]", "[b|=e]", "[b|=en-]", "[b|=GB]"));
    }

    [TestMethod]
    public void NthChildOfCountsNestedSiblingsOnce()
    {
        // Styling goes depth first: between two <div> children of <body> it styles the <span> in
        // the first, whose parent is that <div>. With one parent remembered, every <div> counted
        // all its siblings again: four such rules over 6,000 <div> took 144 million tests, past
        // the default MaxStyleWork for the document.
        const int Divs = 6000;
        string html = "<style>" + Repeat(4, i => $":nth-child({i + 2}n of *) {{ color: red }}") + "</style>" + Repeat(Divs, _ => "<div><span>x</span></div>");
        HtmlDocument doc = HtmlParser.Parse(html);
        var budget = new WorkBudget(new HtmlConverterOptions().StyleWorkFor(html), long.MaxValue, CancellationToken.None);
        var resolver = new StyleResolver(CssMedia.Default, budget);
        resolver.AddDocument(doc);

        void Style(HtmlElement e, ComputedStyle? parent)
        {
            ComputedStyle style = resolver.Compute(e, parent);
            foreach (HtmlElement child in e.ChildElements) Style(child, style);
        }

        // About 40 units per element now, whatever the number of <div>.
        Style(doc.Html, null);
        Assert.IsLessThan(100L * 2 * Divs, budget.StyleWork);

        // Each element gets its own parent's counts: every <span> is the last child of its <div>.
        CssSelector last = CssSelector.Parse(":nth-last-child(1 of *)")!.Single();
        var matched = doc.Body.Descendants().Where(e => last.Matches(e)).ToList();
        Assert.HasCount(Divs + 1, matched);
        Assert.AreEqual(Divs, matched.Count(e => e.Name == "span"));
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
