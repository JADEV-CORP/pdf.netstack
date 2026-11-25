using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfNetStack.TextBench;

/// <summary>
/// The HTML documents of the benchmark, written for it, and the text a reader sees in each: the
/// ground truth. Every sentence is distinct, so the order of the words is checkable. The same HTML
/// is printed by Chrome and by PdfNetStack.Html, so the two sets differ only in the producer.
/// </summary>
internal static partial class Documents
{
    private static readonly string[] Sentences =
    [
        "The river authority measured flow at eleven gauges along the upper basin during March.",
        "Snowmelt arrived two weeks earlier than the twenty year average predicted.",
        "Field crews replaced three pressure sensors that had drifted beyond calibration.",
        "Turbidity peaked after the storm of the fourteenth and settled within four days.",
        "Irrigation districts requested a revised release schedule for the dry months.",
        "The board approved a pilot program for reusing treated water in parks.",
        "Groundwater levels near the eastern wells continued a slow decline.",
        "Volunteers logged bird counts at the wetland restoration site every Sunday.",
        "A new telemetry link now reports reservoir levels every fifteen minutes.",
        "Maintenance of the spillway gates was postponed until the autumn inspection.",
        "Engineers recommended lining the northern canal to reduce seepage losses.",
        "The laboratory tested samples for nitrates, phosphates and dissolved oxygen.",
        "Public meetings drew more residents than any session in the past decade.",
        "Forecast models were retrained with sixty years of corrected records.",
        "The drought contingency plan defines four stages of voluntary and mandatory cuts.",
        "Hydropower output rose by nine percent compared with the same quarter last year.",
        "Sediment surveys found the lower pool had lost capacity since the last dredging.",
        "School groups visited the fish ladder to watch the spring migration.",
        "Budget pressure delayed the purchase of two replacement survey boats.",
        "Agreements with neighbouring counties now share flood warnings automatically.",
        "Stream temperature rose above the threshold for trout on six afternoons.",
        "The archive of paper charts was scanned and indexed for researchers.",
        "Pump station seven ran on backup power for a full day after the outage.",
        "Native willows planted last year showed a survival rate above eighty percent.",
        "Billing errors affected a small number of agricultural accounts in February.",
        "An independent audit confirmed the accuracy of the metering network.",
        "Ice jams near the old bridge raised water levels by almost a metre overnight.",
        "Staff training covered confined space entry and first aid refreshers.",
        "The website now publishes daily water quality results in plain language.",
        "A study of evaporation losses will compare floating covers with shade balls.",
        "Contract negotiations with the operators union concluded without a strike.",
        "Leak detection crews found and repaired forty kilometres of failing pipe.",
        "Rainfall totals for the year finished slightly below the long term mean.",
        "The emergency spillway has not been used since its construction in 1962.",
        "Beavers returned to the tributary and built two dams near the trailhead.",
        "Customer complaints about taste and odour fell after the filter upgrade.",
        "The capital plan prioritizes dam safety, then treatment, then distribution.",
        "Satellite imagery helped map the extent of the spring flooding.",
        "Water rights transfers require notice to downstream users and a public hearing.",
        "Energy use per cubic metre treated declined for the fourth consecutive year.",
    ];

    /// <summary>Sentence <paramref name="i"/>, wrapping round the list.</summary>
    public static string Sentence(int i) => Sentences[i % Sentences.Length];

    /// <summary>A paragraph of <paramref name="count"/> sentences starting at <paramref name="first"/>, wrapping round the list.</summary>
    private static string Para(int first, int count) =>
        string.Join(" ", Enumerable.Range(first, count).Select(i => Sentences[i % Sentences.Length]));

    private const string Style = """
        <style>
        body { font-family: Helvetica, Arial, sans-serif; font-size: 11pt; line-height: 1.35; margin: 0; }
        h1 { font-size: 22pt; margin: 0 0 10pt 0; }
        h2 { font-size: 16pt; margin: 14pt 0 6pt 0; }
        h3 { font-size: 13pt; margin: 10pt 0 4pt 0; }
        p { margin: 0 0 8pt 0; }
        table { border-collapse: collapse; margin: 8pt 0; }
        td, th { border: 0.75pt solid #333; padding: 3pt 6pt; text-align: left; }
        @page { size: A4; margin: 20mm; }
        </style>
        """;

    private static string Page(string title, string body, string extraStyle = "") =>
        $"<!doctype html><html lang=en><head><meta charset=utf-8><title>{title}</title>{Style}{extraStyle}</head><body>{body}</body></html>";

    /// <summary>The documents: an id and the HTML.</summary>
    public static IReadOnlyList<(string Id, string Html)> All { get; } = Build();

    private static List<(string, string)> Build()
    {
        var docs = new List<(string, string)>();

        // 1. An article: headings, paragraphs, a quotation.
        docs.Add(("article", Page("Annual water report", $"""
            <h1>Annual water report</h1>
            <p>{Para(0, 4)}</p>
            <h2>Supply</h2>
            <p>{Para(4, 3)}</p>
            <blockquote><p>{Para(7, 2)}</p></blockquote>
            <h2>Quality</h2>
            <p>{Para(9, 4)}</p>
            """)));

        // 2. Two columns of prose, laid out by a grid: the left column reads first.
        docs.Add(("columns", Page("Two columns", $"""
            <h1>Basin bulletin</h1>
            <div style="display: grid; grid-template-columns: 1fr 1fr; column-gap: 24pt">
            <div><p>{Para(0, 3)}</p><p>{Para(3, 3)}</p><p>{Para(6, 3)}</p></div>
            <div><p>{Para(9, 3)}</p><p>{Para(12, 3)}</p><p>{Para(15, 3)}</p></div>
            </div>
            <p>{Para(18, 2)}</p>
            """)));

        // 3. Lists, nested, bulleted and numbered.
        docs.Add(("lists", Page("Lists", $"""
            <h1>Action items</h1>
            <p>{Para(20, 1)}</p>
            <ul><li>{Para(21, 1)}</li><li>{Para(22, 1)}<ul><li>{Para(23, 1)}</li><li>{Para(24, 1)}</li></ul></li><li>{Para(25, 1)}</li></ul>
            <h2>Steps</h2>
            <ol><li>{Para(26, 1)}</li><li>{Para(27, 1)}</li><li>{Para(28, 1)}</li></ol>
            """)));

        // 4. A ruled table between paragraphs.
        var rows = new StringBuilder();
        string[] sites = ["Upper gauge", "Mill creek", "Old bridge", "East wells", "Canal head", "Lower pool", "Fish ladder", "Wetland"];
        for (int i = 0; i < sites.Length; i++) rows.Append($"<tr><td>{sites[i]}</td><td>{12 + (i * 7)}.{i}</td><td>{(i % 3 == 0 ? "Normal" : i % 3 == 1 ? "Low" : "High")}</td><td>{3 + i} March</td></tr>");
        docs.Add(("table", Page("Gauge table", $"""
            <h1>Gauge readings</h1>
            <p>{Para(29, 2)}</p>
            <table><thead><tr><th>Site</th><th>Flow</th><th>Status</th><th>Date</th></tr></thead><tbody>{rows}</tbody></table>
            <p>{Para(31, 2)}</p>
            """)));

        // 5. A side box beside the main text.
        docs.Add(("sidebar", Page("Sidebar", $"""
            <h1>Field notes</h1>
            <div style="display: grid; grid-template-columns: 2fr 1fr; column-gap: 18pt">
            <div><p>{Para(33, 4)}</p><p>{Para(37, 4)}</p></div>
            <div style="background: #eef; padding: 8pt"><h3>At a glance</h3><p>{Para(1, 2)}</p></div>
            </div>
            """)));

        // 6. A long report over several pages.
        var report = new StringBuilder("<h1>Operations review</h1>");
        for (int s = 0; s < 8; s++)
        {
            report.Append($"<h2>Section {s + 1}</h2><p>{Para(s * 5, 4)}</p><h3>Details {s + 1}</h3><p>{Para((s * 5) + 4, 5)}</p>");
        }

        docs.Add(("long", Page("Operations review", report.ToString())));

        // 7. Running header and page numbers in the page margins: not part of the text.
        var paged = new StringBuilder("<h1>Board minutes</h1>");
        for (int s = 0; s < 5; s++) paged.Append($"<h2>Item {s + 1}</h2><p>{Para((s * 7) + 3, 6)}</p>");
        docs.Add(("margins", Page("Board minutes", paged.ToString(), """
            <style>@page { @top-center { content: "Confidential draft"; } @bottom-center { content: "Page " counter(page); } }</style>
            """)));

        // 8. A table without lines, after a heading.
        var plain = new StringBuilder();
        for (int i = 0; i < 5; i++) plain.Append($"<tr><td style='border: none'>{sites[i]}</td><td style='border: none'>{40 + i}</td></tr>");
        docs.Add(("unruled", Page("Unruled", $"""
            <h1>Station list</h1>
            <p>{Para(10, 2)}</p>
            <table>{plain}</table>
            <p>{Para(12, 2)}</p>
            """)));

        // 9. A newsletter in three columns, each a heading and two paragraphs.
        var columns = new StringBuilder();
        for (int c = 0; c < 3; c++) columns.Append($"<div><h3>Column {c + 1}</h3><p>{Para(c * 6, 3)}</p><p>{Para((c * 6) + 3, 3)}</p></div>");
        docs.Add(("newsletter", Page("Newsletter", $"""
            <h1>Basin newsletter</h1>
            <div style="display: grid; grid-template-columns: 1fr 1fr 1fr; column-gap: 14pt">{columns}</div>
            """)));

        // 10. A two column paper, justified, over two pages, with captions in small print.
        var paper = new StringBuilder("<h1>Seepage losses in lined and unlined canals</h1><p>" + Para(5, 3) + "</p>");
        for (int part = 0; part < 2; part++)
        {
            paper.Append("<div style=\"display: grid; grid-template-columns: 1fr 1fr; column-gap: 18pt; text-align: justify; break-inside: avoid\"><div>");
            for (int k = 0; k < 3; k++) paper.Append($"<p>{Para((part * 12) + (k * 2), 4)}</p>");
            paper.Append("</div><div>");
            for (int k = 0; k < 3; k++) paper.Append($"<p>{Para((part * 12) + 6 + (k * 2), 4)}</p>");
            paper.Append($"</div></div><p style=\"font-size: 8pt\">Figure {part + 1}. {Para(30 + part, 1)}</p>");
        }

        docs.Add(("paper", Page("Paper", paper.ToString())));

        // 11. Label and value pairs on a grid: they read row by row, not as two columns.
        var pairs = new StringBuilder();
        string[] labels = ["Account", "Holder", "Service address", "Meter", "Tariff", "Billing period", "Previous reading", "Current reading"];
        for (int i = 0; i < labels.Length; i++) pairs.Append($"<div>{labels[i]}</div><div>{Sentences[i].Split(' ')[1]} {100 + (i * 37)} {Sentences[i + 5].Split(' ')[2]}</div>");
        docs.Add(("pairs", Page("Statement", $"""
            <h1>Water statement</h1>
            <div style="display: grid; grid-template-columns: 140pt 1fr; row-gap: 4pt">{pairs}</div>
            <p>{Para(14, 2)}</p>
            """)));

        return docs;
    }

    /// <summary>
    /// The words a reader sees in a document, in reading order: the text of the body, with the
    /// numbers of ordered list items, without the head, styles or margin boxes.
    /// </summary>
    public static string Truth(string html)
    {
        string body = html[(html.IndexOf("<body>", StringComparison.Ordinal) + 6)..];
        var sb = new StringBuilder();
        var ordered = new Stack<int>();
        foreach (Match m in TokenRegex().Matches(body))
        {
            if (m.Groups["tag"].Success)
            {
                string tag = m.Groups["tag"].Value.ToLowerInvariant();
                if (tag == "ol") ordered.Push(0);
                else if (tag == "/ol") ordered.Pop();
                else if (tag == "ul") ordered.Push(-1);
                else if (tag == "/ul") ordered.Pop();
                else if (tag == "li" && ordered.Count > 0 && ordered.Peek() >= 0)
                {
                    int n = ordered.Pop() + 1;
                    ordered.Push(n);
                    sb.Append(' ').Append(n).Append(". ");
                }

                sb.Append(' ');
            }
            else
            {
                sb.Append(WebUtility.HtmlDecode(m.Value));
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"<(?<tag>/?[a-zA-Z0-9]+)[^>]*>|[^<]+")]
    private static partial Regex TokenRegex();
}
