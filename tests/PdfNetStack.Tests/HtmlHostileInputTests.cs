using System.Diagnostics;
using System.Text;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// HTML built to make styling or layout slow or memory hungry, each attack built here in code. The
/// conversion either finishes with bounded work and memory, or ends quickly with
/// <see cref="HtmlConversionException"/> or, when asked, <see cref="OperationCanceledException"/>.
/// Work is counted rather than timed, so those bounds hold on any machine; the time bounds are
/// loose, many times what the code takes and far below what the attacks cost before the fixes.
/// </summary>
[TestClass]
public sealed class HtmlHostileInputTests
{
    private static readonly HtmlConverterOptions Standard = new() { UseSystemFonts = false };

    /// <summary>A conversion's outcome, the work it counted and the bytes it allocated (0 where the runtime cannot tell).</summary>
    private sealed record Outcome(WorkBudget Budget, long Allocated, TimeSpan Elapsed, Exception? Error);

    /// <summary>Converts on a thread of its own with a deep stack, as the converter does, measuring that thread's allocations.</summary>
    private static Outcome Convert(string html, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= Standard;
        var budget = new WorkBudget(options.StyleWorkFor(html), options.LayoutWorkFor(html), cancellationToken);
        long allocated = 0;
        Exception? error = null;
        var clock = Stopwatch.StartNew();
        var worker = new Thread(
            () =>
            {
#if NET8_0_OR_GREATER
                long before = GC.GetAllocatedBytesForCurrentThread();
#endif
                try
                {
                    HtmlConverter.Convert(html, options, budget);
                }
                catch (Exception e)
                {
                    error = e;
                }
#if NET8_0_OR_GREATER
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            },
            64 << 20);
        worker.Start();
        worker.Join();
        return new Outcome(budget, allocated, clock.Elapsed, error);
    }

    private static string Repeat(int count, Func<int, string> part)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(part(i));
        return sb.ToString();
    }

    /// <summary>Rules of the form ".a{i} .b span" over spans nested in a deep tree: every rule once walked every ancestor of every span.</summary>
    private static string DescendantSelectors(int rules, int depth, int spans, string ancestorClasses = "") =>
        "<style>" + Repeat(rules, i => $".a{i} .b span {{ color: red }}") + "</style>"
        + $"<div class=\"{ancestorClasses}\"><div class=\"{(ancestorClasses.Length > 0 ? "b" : "")}\">"
        + Repeat(depth, _ => "<div>") + "<p>" + Repeat(spans, _ => "<span>x</span>") + "</p>" + Repeat(depth, _ => "</div>")
        + "</div></div>";

    private static void AssertStyleBudgetEnded(Outcome outcome)
    {
        Assert.IsInstanceOfType<HtmlConversionException>(outcome.Error, outcome.Error?.ToString());
        StringAssert.Contains(outcome.Error!.Message, "MaxStyleWork");
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void DescendantSelectorsAreRejectedWithoutWalkingTheTree()
    {
        // 1,500 rules, 3,000 spans under 300 ancestors: 1.35 billion ancestor tests before the
        // ancestor filter, one filter lookup per rule and span now.
        Outcome outcome = Convert(DescendantSelectors(rules: 1500, depth: 300, spans: 3000));
        Assert.IsNull(outcome.Error, outcome.Error?.ToString());
        Assert.IsLessThan(10_000_000, outcome.Budget.StyleWork);
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    public void TheAncestorFilterNeverRejectsAMatchingSelector()
    {
        const string page = """
            <div id="main" class="box wide">
              <section class="note"><p class="lead">One <span class="x">two</span></p><p>three <em>four</em></p></section>
              <section><h2>Five</h2><p class="lead"><a href="#">six</a></p></section>
              <ul class="list"><li>a<li class="x">b<li>c</ul>
            </div>
            <article><div><div><p class="deep"><span>seven</span></p></div></div></article>
            """;
        string[] selectors =
        [
            "div p", "#main p", ".box .lead", ".wide > section p", ".note span", "section .x", "div div p span", "article span",
            "body > div .x", "h2 + p a", "h2 ~ p a", ".note + section p", "ul li", ".list > .x", "#main .note > p > em",
            "html body article div p.deep span", ":is(.note, ul) .x", "p:not(.lead) em", ".missing span", "#nowhere p", "div.box ~ article span",
        ];
        HtmlDocument doc = HtmlParser.Parse(page);
        List<HtmlElement> elements = [.. doc.Descendants()];
        List<CssSelector> parsed = [.. selectors.Select(s => CssSelector.Parse(s)!.Single())];

        // In document order, as styling visits elements, and in reverse, which moves the filter by
        // more than one step at a time.
        foreach (IEnumerable<HtmlElement> order in new[] { elements, Enumerable.Reverse(elements) })
        {
            var filter = new AncestorFilter();
            int matches = 0, rejections = 0;
            foreach (HtmlElement e in order)
            {
                filter.MoveTo(e.ParentElement);
                foreach (CssSelector s in parsed)
                {
                    bool may = filter.MightContainAll(s.AncestorKeys);
                    if (s.Matches(e))
                    {
                        matches++;
                        Assert.IsTrue(may, $"{s} matches <{e.Name}> but the filter rejected it");
                    }
                    else if (!may)
                    {
                        rejections++;
                    }
                }
            }

            Assert.IsGreaterThan(20, matches);
            Assert.IsGreaterThan(100, rejections);
        }

        // Keys come only from compounds that must be ancestors: not the subject, not a sibling's.
        Assert.IsEmpty(CssSelector.Parse("h2 + p")!.Single().AncestorKeys);
        Assert.HasCount(1, CssSelector.Parse(".a + .b .c")!.Single().AncestorKeys);
        Assert.HasCount(2, CssSelector.Parse(".x > .a + .b .c")!.Single().AncestorKeys);
    }

    [TestMethod]
    public void TheCascadeStillAppliesDescendantRules()
    {
        // The same answers through the resolver, which moves the filter itself.
        HtmlDocument doc = HtmlParser.Parse("<div class=a><p class=b><span id=hit>x</span></p></div><div><p class=b><span id=miss>y</span></p></div>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddStyleSheet(".a .b span { width: 7px } .a > .b > span { height: 9px } div span { --seen: 1 }", CssOrigin.Author);
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants()) styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        ComputedStyle hit = styles.Single(s => s.Key.Id == "hit").Value, miss = styles.Single(s => s.Key.Id == "miss").Value;
        Assert.AreEqual(7, hit.Length("width"));
        Assert.AreEqual(9, hit.Length("height"));
        Assert.IsNull(miss.Length("width"));
        Assert.IsNotNull(hit.Custom.Find("--seen"));
        Assert.IsNotNull(miss.Custom.Find("--seen"));
    }

    [TestMethod]
    [DoNotParallelize] // timing and allocation: parallel tests would slow the measured code on a small runner
    public void InheritedCustomPropertiesAreSharedNotCopied()
    {
        // 3,000 custom properties on the root and 20,000 elements that each set one: the inherited
        // map was copied for every element, about 1.7 GB; the trie shares it.
        string html = "<style>:root{" + Repeat(3000, i => $"--p{i}:{i}px;") + "} i{--x:1}</style><p>" + Repeat(20000, _ => "<i></i>") + "<b style=\"width: var(--p2999)\" id=last>x</b></p>";
        Outcome outcome = Convert(html);
        Assert.IsNull(outcome.Error, outcome.Error?.ToString());
#if NET8_0_OR_GREATER
        Assert.IsLessThan(400L << 20, outcome.Allocated, $"allocated {outcome.Allocated:N0} bytes");
#endif
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    public void CustomPropertiesBehaveAsAPersistentMap()
    {
        // Against a dictionary per version: every version keeps its own contents after later changes.
        var random = new Random(7);
        var versions = new List<(CustomProperties Map, Dictionary<string, List<CssValue>?> Model)> { (CustomProperties.Empty, new()) };
        for (int step = 0; step < 3000; step++)
        {
            (CustomProperties map, Dictionary<string, List<CssValue>?> model) = versions[random.Next(versions.Count)];
            string name = "--p" + random.Next(800);
            List<CssValue>? value = random.Next(8) == 0 ? null : [new CssValue(new CssToken(CssTokenType.Ident, "v" + step))];
            versions.Add((map.With(name, value), new Dictionary<string, List<CssValue>?>(model) { [name] = value }));
        }

        foreach ((CustomProperties map, Dictionary<string, List<CssValue>?> model) in versions.Where((_, i) => i % 50 == 0))
        {
            for (int k = 0; k < 800; k++)
            {
                string name = "--p" + k;
                Assert.AreSame(model.TryGetValue(name, out List<CssValue>? expected) ? expected : null, map.Find(name), name);
            }
        }

        // Two names with the same hash share a leaf and stay apart. The hash is randomized per
        // process, so the pair is found here, among random names.
        var seen = new Dictionary<int, string>();
        string? first = null, second = null;
        for (int i = 0; first is null; i++)
        {
            string name = "--" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int hash = StringComparer.Ordinal.GetHashCode(name);
            if (seen.TryGetValue(hash, out string? other)) (first, second) = (other, name);
            else seen[hash] = name;
        }

        List<CssValue> one = [new CssValue(new CssToken(CssTokenType.Ident, "one"))], two = [new CssValue(new CssToken(CssTokenType.Ident, "two"))];
        CustomProperties both = CustomProperties.Empty.With(first, one).With(second!, two);
        Assert.AreSame(one, both.Find(first));
        Assert.AreSame(two, both.Find(second!));
        Assert.IsNull(both.With(first, null).Find(first));
        Assert.AreSame(two, both.With(first, null).Find(second!));
    }

    [TestMethod]
    public void CustomPropertiesStillCascade()
    {
        HtmlDocument doc = HtmlParser.Parse("<div id=a><p id=b><span id=c>x</span></p></div>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddStyleSheet(":root { --w: 5px; --h: 6px } #a { --w: 7px } #b { --h: var(--w) } #c { --loop: var(--loop); width: var(--w); height: var(--h) }", CssOrigin.Author);
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants()) styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        ComputedStyle c = styles.Single(s => s.Key.Id == "c").Value;
        Assert.AreEqual(7, c.Length("width"));
        Assert.AreEqual(7, c.Length("height"));
        Assert.IsNull(c.Custom.Find("--loop"));
        Assert.AreEqual("5px", string.Concat(styles.Single(s => s.Key.Name == "html").Value.Custom.Find("--w")!.Select(v => v.ToString())).Trim());
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void VarAmplificationEndsWithinTheStyleBudget()
    {
        // Each element expands 40 references to a 1,000 value property: 40,000 values copied per
        // element, 29 GB allocated for 30,000 elements before the budget counted them.
        string html = "<style>:root{--a:" + Repeat(1000, _ => "1 ") + "} i{--b:" + Repeat(40, _ => "var(--a) ") + "}</style><p>" + Repeat(3000, _ => "<i></i>") + "</p>";
        Outcome outcome = Convert(html);
        AssertStyleBudgetEnded(outcome);
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void SiblingSelectorsEndWithinTheStyleBudget()
    {
        // ".x ~ p" with no .x walks back over every earlier sibling: quadratic in the siblings.
        string html = "<style>" + Repeat(20, i => $".x{i} ~ p {{ color: red }}") + "</style>" + Repeat(5000, _ => "<p>x</p>");
        Outcome outcome = Convert(html);
        AssertStyleBudgetEnded(outcome);
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    [DoNotParallelize] // timing and allocation: parallel tests would slow the measured code on a small runner
    public void NthChildOfCountsEachParentOnce()
    {
        // :nth-child(An+B of S) tested S on every earlier sibling of every element, and allocated
        // for each test: 200 million tests and 18 GB for 20,000 siblings. The siblings that match
        // S are now counted once per parent.
        string html = "<style>" + Repeat(4, i => $"p:nth-child({i + 2}n of .x) {{ color: red }}") + "</style>" + Repeat(8000, i => i % 3 == 0 ? "<p>x</p>" : "<p class=x>x</p>");
        Outcome outcome = Convert(html);
        Assert.IsNull(outcome.Error, outcome.Error?.ToString());
        Assert.IsLessThan(2_000_000, outcome.Budget.StyleWork);
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
#if NET8_0_OR_GREATER
        Assert.IsLessThan(512L << 20, outcome.Allocated, $"allocated {outcome.Allocated:N0} bytes");
#endif

        // The same answers as counting each time: the second, fourth and sixth .x child, not the <p> between.
        HtmlDocument doc = HtmlParser.Parse("<div><p class=x id=a></p><p id=b></p><p class=x id=c></p><p class=x id=d></p><p class=x id=e></p></div>");
        CssSelector even = CssSelector.Parse(":nth-child(even of .x)")!.Single(), last = CssSelector.Parse(":nth-last-child(1 of .x)")!.Single();
        Assert.AreEqual("c e", string.Join(" ", doc.Descendants().Where(e => even.Matches(e)).Select(e => e.Id)));
        Assert.AreEqual("e", string.Join(" ", doc.Descendants().Where(e => last.Matches(e)).Select(e => e.Id)));
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void AncestorsTheFilterCannotRejectEndWithinTheStyleBudget()
    {
        // Every class the rules need is on an ancestor, so the filter answers "maybe" and each
        // rule walks 300 ancestors for each span: the budget is what stops it.
        string classes = string.Join(" ", Enumerable.Range(0, 200).Select(i => "a" + i));
        Outcome outcome = Convert(DescendantSelectors(rules: 200, depth: 300, spans: 3000, classes));
        AssertStyleBudgetEnded(outcome);
        Assert.IsLessThan(30_000, outcome.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    public void TheLayoutBudgetEndsALayoutAndCountsFloats()
    {
        string paragraphs = Repeat(200, i => $"<p>Paragraph {i}</p>");
        Outcome tight = Convert(paragraphs, new HtmlConverterOptions { UseSystemFonts = false, MaxLayoutWork = 100 });
        Assert.IsInstanceOfType<HtmlConversionException>(tight.Error, tight.Error?.ToString());
        StringAssert.Contains(tight.Error!.Message, "MaxLayoutWork");

        // Each line and float is checked against every float of its context: counted in blocks of 64.
        string floats = "<div style=\"width:500px\">" + Repeat(5000, i => i % 2 == 0 ? "<div style=\"float:left;width:1px;height:1px\"></div>" : "<div style=\"float:right;width:1px;height:1px\"></div>") + "</div>";
        Outcome counted = Convert(floats);
        Assert.IsNull(counted.Error, counted.Error?.ToString());
        Assert.IsGreaterThan(5000L * 5000 / 2 / 64, counted.Budget.LayoutWork);
    }

    [TestMethod]
    public void OrdinaryDocumentsStayFarBelowTheDefaultBudgets()
    {
        // A report of 20 sections of 100 rows: the defaults allow 250 style and 20 layout units a
        // character, about a hundred times what it needs. The counts are the same on every run.
        string html = "<style>body{font-family:Helvetica} .t td{padding:2px;border-bottom:1px solid #ccc} .t tr:nth-child(even) td{background:#eee} h2{color:#333} .r{text-align:right}</style>"
            + Repeat(20, s => $"<h2>Section {s}</h2><table class=t>" + Repeat(100, r => $"<tr><td>Item {r}</td><td class=r>{r * 3}.00</td><td>Description of item {r}</td></tr>") + "</table>");
        Outcome first = Convert(html), second = Convert(html);
        Assert.IsNull(first.Error, first.Error?.ToString());
        Assert.IsLessThan(5.0, (double)first.Budget.StyleWork / html.Length);
        Assert.IsLessThan(1.0, (double)first.Budget.LayoutWork / html.Length);
        Assert.AreEqual(first.Budget.StyleWork, second.Budget.StyleWork);
        Assert.AreEqual(first.Budget.LayoutWork, second.Budget.LayoutWork);
    }

    [TestMethod]
    public void ParsingStylingAndLayoutObserveCancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        string html = Repeat(3000, i => $"<p class=c{i % 7}>Paragraph {i}</p>");

        Assert.ThrowsExactly<OperationCanceledException>(() => HtmlParser.Parse(html, cancel.Token));

        HtmlDocument doc = HtmlParser.Parse(html);
        var canceled = new WorkBudget(long.MaxValue, long.MaxValue, cancel.Token);
        var styling = new StyleResolver(CssMedia.Default, canceled);
        styling.AddDocument(doc);
        Assert.ThrowsExactly<OperationCanceledException>(() => new BoxBuilder(styling).Build(doc));

        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver).Build(doc);
        var engine = new LayoutEngine(new FontResolver(null, useSystemFonts: false), canceled);
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.Layout(root, 600));
    }

    [TestMethod]
    public void PaintingObservesCancellation()
    {
        // The background image loads while page 1 is painted; the loader cancels, and page 2 does not start.
        using var cancel = new CancellationTokenSource();
        var options = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            ResourceLoaderAsync = (_, _) =>
            {
                cancel.Cancel();
                return Task.FromResult<byte[]?>(null);
            },
        };
        string html = "<div style=\"background-image:url(http://example.test/a.png);height:50px\"></div>" + Repeat(3, _ => "<p style=\"break-before:page\">x</p>");
        Assert.ThrowsExactly<OperationCanceledException>(() => HtmlConverter.ConvertToDocument(html, options, cancel.Token));
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public async Task CancellationEndsAnUnboundedConversionQuickly()
    {
        // With no style budget the attack would run for minutes; the token ends it.
        string classes = string.Join(" ", Enumerable.Range(0, 200).Select(i => "a" + i));
        var options = new HtmlConverterOptions { UseSystemFonts = false, MaxStyleWork = long.MaxValue };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() => HtmlConverter.ConvertToDocumentAsync(DescendantSelectors(200, 300, 20000, classes), options, cancel.Token));
        Assert.IsLessThan(15_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task AsynchronousResourceLoadingGetsTheToken()
    {
        string png = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8.png");
        var loaded = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            ResourceLoaderAsync = async (uri, token) =>
            {
                await Task.Yield();
                return uri.Host == "example.test" ? File.ReadAllBytes(png) : null;
            },
        };

        // Through the synchronous call too, which waits on its own thread.
        Assert.HasCount(1, HtmlConverter.ConvertToDocument("<img src=\"http://example.test/a.png\">", loaded).Pages[0].GetImages());
        Assert.HasCount(1, (await HtmlConverter.ConvertToDocumentAsync("<img src=\"http://example.test/a.png\">", loaded)).Pages[0].GetImages());

        // A fetch that never ends is cancelled with the conversion.
        var hanging = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            ResourceLoaderAsync = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() => HtmlConverter.ConvertToDocumentAsync("<img src=\"http://example.test/a.png\">", hanging, cancel.Token));
        Assert.IsLessThan(15_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    [DoNotParallelize] // changes a process wide setting
    public async Task ConcurrentConversionsWaitForAFreeSlot()
    {
        int original = HtmlConverter.MaxConcurrentConversions;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondStarted = 0;
        var blocking = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            ResourceLoaderAsync = async (_, token) =>
            {
                await release.Task.ConfigureAwait(false);
                return null;
            },
        };
        var counting = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            ResourceLoaderAsync = (_, _) =>
            {
                Interlocked.Increment(ref secondStarted);
                return Task.FromResult<byte[]?>(null);
            },
        };
        const string html = "<img src=\"http://example.test/a.png\"><p>x</p>";
        try
        {
            HtmlConverter.MaxConcurrentConversions = 1;
            Task<PdfDocument> first = HtmlConverter.ConvertToDocumentAsync(html, blocking);
            await Task.Delay(100);
            Task<PdfDocument> second = HtmlConverter.ConvertToDocumentAsync(html, counting);

            // A third one, cancelled while it waits, leaves the queue without running.
            using var cancel = new CancellationTokenSource();
            Task<PdfDocument> third = HtmlConverter.ConvertToDocumentAsync(html, counting, cancel.Token);
            await Task.Delay(300);
            Assert.AreEqual(0, Volatile.Read(ref secondStarted));
            Assert.IsFalse(second.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => third);

            release.SetResult(true);
            await first;
            await second;
            Assert.AreEqual(1, Volatile.Read(ref secondStarted));
        }
        finally
        {
            release.TrySetResult(true);
            HtmlConverter.MaxConcurrentConversions = original;
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HtmlConverter.MaxConcurrentConversions = 0);
        Assert.AreEqual(original, HtmlConverter.MaxConcurrentConversions);
    }

    [TestMethod]
    public void LimitsAreValidatedAndReachFileConversions()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HtmlConverterOptions { MaxStyleWork = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HtmlConverterOptions { MaxLayoutWork = -1 });
        Assert.IsNull(new HtmlConverterOptions().MaxStyleWork);

        // The defaults grow with the input.
        var defaults = new HtmlConverterOptions();
        Assert.IsGreaterThan(defaults.StyleWorkFor("x"), defaults.StyleWorkFor(new string('x', 1_000_000)));
        Assert.IsGreaterThan(defaults.LayoutWorkFor("x"), defaults.LayoutWorkFor(new string('x', 1_000_000)));
        Assert.AreEqual(5, new HtmlConverterOptions { MaxStyleWork = 5 }.StyleWorkFor(new string('x', 1_000_000)));

        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-limits-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(path, Repeat(100, i => $"<p>Paragraph {i}</p>"));
        try
        {
            var e = Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertFileToDocument(path, new HtmlConverterOptions { UseSystemFonts = false, MaxStyleWork = 10 }));
            StringAssert.Contains(e.Message, "MaxStyleWork");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
