namespace PdfNetStack;

/// <summary>How <see cref="PdfDocument.ReadText(PdfTextOptions?, CancellationToken)"/> writes each page's text.</summary>
public enum PdfTextFormat
{
    /// <summary>
    /// Plain text: blocks (paragraphs, headings, list items, tables) separated by a blank line,
    /// the lines of a block by a line feed, table cells by a tab. The default.
    /// </summary>
    Plain,

    /// <summary>
    /// CommonMark with GitHub tables: headings, paragraphs, lists, tables and links, in reading
    /// order. Text that would read as Markdown syntax is escaped, and raw HTML is never emitted.
    /// </summary>
    Markdown,
}

/// <summary>
/// Options for reading a document's text for search, indexing and retrieval augmented generation:
/// the output format, the reading order, and which text is left out. The defaults suit text that
/// goes to a language model: artifacts and text a reader cannot see are left out.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument doc = PdfDocument.Open("report.pdf");
/// var options = new PdfTextOptions { Format = PdfTextFormat.Markdown };
/// await foreach (PdfPageText page in doc.ReadTextAsync(options, cancellationToken))
/// {
///     if (page.HiddenText.Count > 0) logger.LogWarning("Page {Page}: {Count} hidden runs left out", page.PageNumber, page.HiddenText.Count);
///     await index.AddAsync(page.PageNumber, page.Text, cancellationToken);
/// }
/// </code>
/// </example>
public sealed record PdfTextOptions
{
    /// <summary>Shared default instance: plain text, structure tree order, artifacts and hidden text left out.</summary>
    public static PdfTextOptions Default { get; } = new();

    /// <summary>Plain text or Markdown. Default <see cref="PdfTextFormat.Plain"/>.</summary>
    public PdfTextFormat Format { get; init; } = PdfTextFormat.Plain;

    /// <summary>
    /// Read tagged pages in the order of the document's structure tree (ISO 32000-1, 14.7), and take
    /// headings, list items and tables from its tags. Default true. A page whose text is mostly
    /// untagged, or a document without a structure tree, is read in layout order; see
    /// <see cref="PdfPageText.ReadingOrder"/> for the order each page got.
    /// </summary>
    public bool UseStructureTree { get; init; } = true;

    /// <summary>
    /// Leave out content marked as an artifact (ISO 32000-1, 14.8.2.2): running headers and
    /// footers, page numbers, watermarks and other decoration the producer tagged as not part of
    /// the document's text. Default true. On a page read in structure tree order, untagged text
    /// above or below all of the tagged text is left out too: some producers leave running
    /// headers and page numbers untagged instead of marking them. Headers and footers of untagged
    /// documents are kept.
    /// </summary>
    public bool ExcludeArtifacts { get; init; } = true;

    /// <summary>
    /// Leave out text a reader cannot see. Default true. Hidden text is found whatever this says
    /// and listed in <see cref="PdfPageText.HiddenText"/>; this decides whether it is also left out
    /// of the page's text and words.
    /// </summary>
    /// <remarks>
    /// Text counts as hidden when it is drawn invisible (text rendering mode 3 or 7), fully
    /// transparent, in the colour of what lies under it (white on white, for one), clipped away,
    /// outside the crop box, smaller than one point, in optional content that is off, or covered by
    /// an opaque shape drawn after it; and replacement text (/ActualText) far longer than the
    /// glyphs it stands for, which then read as their own text. Invisible text over an image, or under one, is the text
    /// layer of a scanned page: it is kept, and <see cref="PdfPageText.HasOcrText"/> says so.
    /// The checks only ever err towards keeping text: a colour, shape or transparency they cannot
    /// judge counts as visible.
    /// </remarks>
    public bool VisibleOnly { get; init; } = true;
}
