using Microsoft.Extensions.DataIngestion;

namespace PdfNetStack.DataIngestion;

/// <summary>
/// Reads PDF files into an <see cref="IngestionDocument"/> for a Microsoft.Extensions.DataIngestion
/// pipeline: one <see cref="IngestionDocumentSection"/> per page, holding the page's headings,
/// paragraphs, list items and tables in reading order, each with its Markdown, its plain text and
/// its page number.
/// </summary>
/// <remarks>
/// <para>
/// Text is read by <see cref="PdfDocument.ReadTextAsync(PdfTextOptions?, CancellationToken)"/>:
/// tagged PDFs in the order of their structure tree, artifacts such as running headers and page
/// numbers left out, and text a reader cannot see left out too (see
/// <see cref="PdfTextOptions.VisibleOnly"/>). A page with hidden text has the number of hidden runs
/// in its section's metadata under <see cref="HiddenTextKey"/>, so a pipeline can log or quarantine
/// documents that carry prompt injection.
/// </para>
/// <para>
/// Headings become <see cref="IngestionDocumentHeader"/> with their level, tables
/// <see cref="IngestionDocumentTable"/> with their cells, and paragraphs and list items
/// <see cref="IngestionDocumentParagraph"/>. Images are not extracted.
/// </para>
/// <para>
/// An instance holds only its options and is safe to use from several threads at once; each read
/// opens its own document.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var reader = new PdfNetStackReader();
/// IngestionDocument doc = await reader.ReadAsync(new FileInfo("handbook.pdf"), cancellationToken);
/// foreach (IngestionDocumentElement element in doc.EnumerateContent())
/// {
///     Console.WriteLine($"p{element.PageNumber}: {element.GetMarkdown()}");
/// }
/// </code>
/// </example>
public sealed class PdfNetStackReader : IngestionDocumentReader
{
    /// <summary>Section metadata key: the number of runs of hidden text found on the page, set when there are any.</summary>
    public const string HiddenTextKey = "pdfnetstack.hidden_text_runs";

    /// <summary>Section metadata key: true when the page holds the OCR text layer of a scanned image.</summary>
    public const string OcrTextKey = "pdfnetstack.ocr_text";

    /// <summary>Section metadata key: "StructureTree" or "Layout", where the page's reading order came from.</summary>
    public const string ReadingOrderKey = "pdfnetstack.reading_order";

    private readonly PdfTextOptions _textOptions;
    private readonly PdfLoadOptions? _loadOptions;

    /// <summary>Creates a reader with the default text options: structure tree order, artifacts and hidden text left out.</summary>
    public PdfNetStackReader()
        : this(null, null)
    {
    }

    /// <summary>Creates a reader with the given options.</summary>
    /// <param name="textOptions">
    /// Reading order and what to leave out; <see cref="PdfTextOptions.Default"/> when null. Its
    /// <see cref="PdfTextOptions.Format"/> does not matter: elements carry both Markdown and text.
    /// </param>
    /// <param name="loadOptions">
    /// Password and limits for opening each file; <see cref="PdfLoadOptions.Default"/> when null. For
    /// untrusted uploads, set <see cref="PdfLimits"/> with a timeout.
    /// </param>
    public PdfNetStackReader(PdfTextOptions? textOptions, PdfLoadOptions? loadOptions = null)
    {
        _textOptions = textOptions ?? PdfTextOptions.Default;
        _loadOptions = loadOptions;
    }

    /// <summary>Reads a PDF from <paramref name="source"/>, from its current position to its end.</summary>
    /// <param name="source">The PDF. It is read, not disposed.</param>
    /// <param name="identifier">The document's identifier, such as its path or URL.</param>
    /// <param name="mediaType">The content's media type. The content is read as PDF whatever it says.</param>
    /// <param name="cancellationToken">Observed while the file is read and each page's text is read.</param>
    /// <returns>The document, one section per page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is null or empty.</exception>
    /// <exception cref="PdfFormatException">The content is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the load options' password does not open it.</exception>
    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(identifier);

        using PdfDocument pdf = await PdfDocument.OpenAsync(source, _loadOptions, cancellationToken).ConfigureAwait(false);
        var document = new IngestionDocument(identifier);
        await foreach (PdfPageText page in pdf.ReadTextAsync(_textOptions, cancellationToken).ConfigureAwait(false))
        {
            document.Sections.Add(ToSection(page));
        }

        return document;
    }

    /// <summary>A page as a section: its blocks as elements, and what was found hidden in its metadata.</summary>
    internal static IngestionDocumentSection ToSection(PdfPageText page)
    {
        var section = new IngestionDocumentSection { PageNumber = page.PageNumber };
        section.Metadata[ReadingOrderKey] = page.ReadingOrder.ToString();
        if (page.HiddenText.Count > 0) section.Metadata[HiddenTextKey] = page.HiddenText.Count;
        if (page.HasOcrText) section.Metadata[OcrTextKey] = true;

        foreach (PdfTextBlock block in page.Blocks)
        {
            // An element's Markdown may not be empty; a block that is only spaces has nothing to say.
            if (string.IsNullOrWhiteSpace(block.Markdown)) continue;
            IngestionDocumentElement element = block.Kind switch
            {
                PdfTextBlockKind.Heading => new IngestionDocumentHeader(block.Markdown) { Level = Math.Clamp(block.Level, 1, 10) },
                PdfTextBlockKind.Table => new IngestionDocumentTable(block.Markdown, Cells(block, page.PageNumber)),
                _ => new IngestionDocumentParagraph(block.Markdown),
            };

            element.Text = block.Text;
            element.PageNumber = page.PageNumber;
            section.Elements.Add(element);
        }

        return section;
    }

    /// <summary>A table's cells, the first row as the header; an empty or missing cell is null.</summary>
    private static IngestionDocumentElement?[,] Cells(PdfTextBlock table, int pageNumber)
    {
        int columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count);
        var cells = new IngestionDocumentElement?[table.Rows.Count, columns];
        for (int r = 0; r < table.Rows.Count; r++)
        {
            for (int c = 0; c < table.Rows[r].Count; c++)
            {
                string text = table.Rows[r][c];
                if (string.IsNullOrWhiteSpace(text)) continue;

                // The cell as it reads inside the table's Markdown: a pipe would end the cell there.
                cells[r, c] = new IngestionDocumentParagraph(text.Replace("|", "\\|", StringComparison.Ordinal)) { Text = text, PageNumber = pageNumber };
            }
        }

        return cells;
    }
}
