using PdfNetStack.Text.Extraction;

namespace PdfNetStack;

public sealed partial class PdfPage
{
    /// <summary>
    /// Reads this page's text as <see cref="PdfDocument.ReadText(PdfTextOptions?, CancellationToken)"/>
    /// reads each page: in reading order, as plain text or Markdown, with its blocks, words and the
    /// hidden text found on it. For several pages, enumerate the document instead: it reads the
    /// structure tree once for all of them.
    /// </summary>
    /// <param name="options">Format, reading order and what to leave out; <see cref="PdfTextOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while the page's content is read.</param>
    /// <exception cref="OperationCanceledException">Cancelled while the page was read.</exception>
    public PdfPageText ReadText(PdfTextOptions? options = null, CancellationToken cancellationToken = default) =>
        new DocumentTextReader(Owner, options ?? PdfTextOptions.Default).Read(this, cancellationToken);
}
