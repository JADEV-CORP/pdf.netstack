using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

namespace PdfNetStack.Html;

/// <summary>Settings for <see cref="HtmlConverter"/>.</summary>
public sealed class HtmlConverterOptions
{
    /// <summary>
    /// The address relative URLs resolve against: a file path's URI such as
    /// <c>new Uri(@"C:\reports\")</c>, or a web address when <see cref="ResourceLoader"/> fetches
    /// from the network. Null when the HTML only uses absolute URLs or data: URIs.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Fetches style sheets, images and fonts by absolute URL; return null for anything that
    /// should not load. Without a loader only data: URIs and files under <see cref="BaseUri"/>
    /// load, and nothing comes from the network: keep it that way for untrusted HTML, or check
    /// each address before fetching it.
    /// </summary>
    public Func<Uri, byte[]?>? ResourceLoader { get; init; }

    /// <summary>
    /// Fetches style sheets, images and fonts by absolute URL, asynchronously, with the
    /// conversion's cancellation token; return null for anything that should not load. When set
    /// it is used instead of <see cref="ResourceLoader"/>, with the same rules: check each address
    /// before fetching it when the HTML is untrusted. Resources load one at a time, when layout
    /// first needs them, on the conversion's own thread, which waits for each without blocking the
    /// thread pool.
    /// </summary>
    public Func<Uri, CancellationToken, Task<byte[]?>>? ResourceLoaderAsync { get; init; }

    /// <summary>Fonts to use before installed ones, matched by family name, width, style and weight.</summary>
    public PdfFontCollection? Fonts { get; init; }

    /// <summary>
    /// Whether installed fonts are used, for families the HTML names and as fallbacks for
    /// characters such as Arabic. Default true. With false, output depends only on the HTML, the
    /// supplied fonts and the standard 14 fonts, and is the same on every machine.
    /// </summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// The page size and margins before the document's @page rules, as CSS. Default
    /// <c>size: A4; margin: 2cm</c>.
    /// </summary>
    public string PageStyle { get; init; } = "size: A4; margin: 2cm";

    /// <summary>A style sheet applied before the document's own, for house styles.</summary>
    public string? StyleSheet { get; init; }

    /// <summary>Headings become bookmarks (the bookmark-level property). Default true.</summary>
    public bool Bookmarks { get; init; } = true;

    /// <summary>
    /// Writes a tagged PDF (ISO 32000-1, 14.8): headings, paragraphs, lists, tables, figures with
    /// their alt text and links in a structure tree, in reading order, with backgrounds, borders,
    /// repeated headers and page margins marked as artifacts, so screen readers and reflow work.
    /// Default true.
    /// </summary>
    public bool Tagged { get; init; } = true;

    /// <summary>
    /// How <see cref="HtmlConverter.ConvertToPdf(string, HtmlConverterOptions?)"/> saves the
    /// document, such as a PDF/A conformance level. Null, the default, saves as
    /// <see cref="PdfSaveOptions.Default"/> does but without the optimizer's equivalence proof
    /// (<see cref="PdfSaveOptions.VerifyOptimization"/> false): the converter draws all the
    /// content itself, and the bytes are the same whenever the proof would pass. Set
    /// <see cref="PdfSaveOptions.Default"/> to keep the proof.
    /// </summary>
    public PdfSaveOptions? SaveOptions { get; init; }

    /// <summary>
    /// Finds the hyphenator for a language tag, or null when there is none, for text with
    /// <c>hyphens: auto</c>; its language is the nearest <c>lang</c> attribute, and text without
    /// one is not hyphenated, as CSS Text 3 says. Called once per tag in a conversion.
    /// <c>PdfHyphenationPatterns.ForLanguage</c> from the <c>PdfNetStack.Hyphenation</c> package
    /// fits here. Null, the default, treats <c>hyphens: auto</c> like <c>manual</c>: words break
    /// only at soft hyphens.
    /// </summary>
    public Func<string, PdfHyphenator?>? Hyphenators { get; init; }

    /// <summary>
    /// The most pages a conversion may produce. Default 10000. A document that needs more, such as
    /// one whose sizes run to kilometres, ends with <see cref="HtmlConversionException"/> instead of
    /// using memory without bound.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxPages
    {
        get => _maxPages;
        init => _maxPages = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPages), value, "At least one page.");
    }

    private readonly int _maxPages = 10_000;

    /// <summary>
    /// The most style work a conversion may do: one unit per rule considered for an element, per
    /// selector part tested while walking ancestors or siblings, and per value a <c>var()</c>
    /// copies. Ordinary documents use a few hundred units per element. Style sheets built to be
    /// slow to match, such as thousands of descendant selectors over a deep tree, end with
    /// <see cref="HtmlConversionException"/> within seconds instead of running for minutes.
    /// Null, the default, scales with the input: 50 million plus 250 per character of the HTML
    /// and of <see cref="StyleSheet"/>, a hundred times what ordinary documents use.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public long? MaxStyleWork
    {
        get => _maxStyleWork;
        init => _maxStyleWork = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxStyleWork), value, "At least one unit, or null to scale with the input.");
    }

    /// <summary>
    /// The most layout work a conversion may do: one unit per box laid out or measured, per line,
    /// per box repeated on a page (a fixed box or a repeated table header), and per 64 floats a
    /// line or box is checked against. A box sized from its content is measured and then laid
    /// out, so ordinary documents use a few units per box. Markup built to be laid out again and
    /// again ends with <see cref="HtmlConversionException"/>. Null, the default, scales with the
    /// input: 1 million plus 20 per character of the HTML, fifty times what ordinary documents use.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public long? MaxLayoutWork
    {
        get => _maxLayoutWork;
        init => _maxLayoutWork = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxLayoutWork), value, "At least one unit, or null to scale with the input.");
    }

    // The scaled budgets: what any document may spend, plus an amount per character of input.
    // Honest documents cost a roughly fixed amount per element; the attacks these stop multiply
    // work by the number of rules, ancestors or siblings, so a budget that grows with the input
    // stops them without refusing large ordinary documents.
    private const long StyleAllowance = 50_000_000;
    private const long StylePerCharacter = 250;
    private const long LayoutAllowance = 1_000_000;
    private const long LayoutPerCharacter = 20;

    private readonly long? _maxStyleWork;
    private readonly long? _maxLayoutWork;

    internal long StyleWorkFor(string html) => _maxStyleWork ?? Scaled(StyleAllowance, StylePerCharacter, (long)html.Length + (StyleSheet?.Length ?? 0));

    internal long LayoutWorkFor(string html) => _maxLayoutWork ?? Scaled(LayoutAllowance, LayoutPerCharacter, html.Length);

    private static long Scaled(long allowance, long perCharacter, long characters) =>
        characters >= (long.MaxValue - allowance) / perCharacter ? long.MaxValue : allowance + (perCharacter * characters);
}
