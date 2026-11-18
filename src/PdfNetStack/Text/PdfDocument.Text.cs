#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif
using PdfNetStack.Text.Extraction;

namespace PdfNetStack;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Reads the document's text page by page for search, indexing and retrieval augmented
    /// generation: each page's text in reading order, as plain text or Markdown, with its blocks,
    /// its words and the hidden text found on it. Pages are read as the sequence is enumerated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each page's content is read once, for its text, its words and its layout together. By
    /// default (see <see cref="PdfTextOptions"/>) a tagged page reads in the order of the structure
    /// tree, artifacts such as running headers and page numbers are left out, and so is text a
    /// reader cannot see: invisible, transparent, white on white, clipped away, off the page, under
    /// a point in size, on a hidden layer or covered by a shape. Hidden text is a common carrier
    /// of prompt injection (OWASP LLM01:2025); <see cref="PdfPageText.HiddenText"/> lists what was
    /// found on each page.
    /// </para>
    /// <para>
    /// The document is not thread safe: do not use it from another thread while the sequence is
    /// enumerated. The pages returned are immutable. Damaged content and fonts are read as far as
    /// they go, as by <see cref="PdfPage.ExtractText()"/>, within the document's
    /// <see cref="PdfLimits"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using PdfDocument doc = PdfDocument.Open("handbook.pdf");
    /// foreach (PdfPageText page in doc.ReadText(new PdfTextOptions { Format = PdfTextFormat.Markdown }))
    /// {
    ///     Console.WriteLine($"## Page {page.PageNumber}");
    ///     Console.WriteLine(page.Text);
    /// }
    /// </code>
    /// </example>
    /// <param name="options">Format, reading order and what to leave out; <see cref="PdfTextOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while each page's content is read.</param>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while a page was read.</exception>
    public IEnumerable<PdfPageText> ReadText(PdfTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ReadPages(new DocumentTextReader(this, options ?? PdfTextOptions.Default), cancellationToken);
    }

    private IEnumerable<PdfPageText> ReadPages(DocumentTextReader reader, CancellationToken ct)
    {
        // By index, so a page added or removed while the sequence is read is seen or skipped as in a loop.
        for (int i = 0; i < Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return reader.Read(Pages[i], ct);
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Reads the document's text page by page, as <see cref="ReadText(PdfTextOptions?, CancellationToken)"/>
    /// does, as an asynchronous sequence for pipelines that consume one, such as an ingestion
    /// pipeline writing to a vector store.
    /// </summary>
    /// <remarks>
    /// Reading a page is CPU bound and runs on the enumerating thread; the sequence yields to the
    /// caller's context before each page. The document is not thread safe: do not use it
    /// elsewhere while the sequence is enumerated. .NET 8 and later; on .NET Standard 2.0 use
    /// <see cref="ReadText(PdfTextOptions?, CancellationToken)"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// await using FileStream file = File.OpenRead("contract.pdf");
    /// using PdfDocument doc = await PdfDocument.OpenAsync(file, cancellationToken: cancellationToken);
    /// await foreach (PdfPageText page in doc.ReadTextAsync(new PdfTextOptions { Format = PdfTextFormat.Markdown }, cancellationToken))
    /// {
    ///     foreach (PdfHiddenText hidden in page.HiddenText) logger.LogWarning("Hidden text on page {Page}: {Reason}", page.PageNumber, hidden.Reason);
    ///     await store.UpsertAsync(page.PageNumber, page.Text, cancellationToken);
    /// }
    /// </code>
    /// </example>
    /// <param name="options">Format, reading order and what to leave out; <see cref="PdfTextOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed before and while each page is read, together with the token given to the enumerator.</param>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public IAsyncEnumerable<PdfPageText> ReadTextAsync(PdfTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ReadPagesAsync(new DocumentTextReader(this, options ?? PdfTextOptions.Default), cancellationToken);
    }

    private async IAsyncEnumerable<PdfPageText> ReadPagesAsync(DocumentTextReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Lets the caller's context run between pages: a long document does not hold it throughout.
            await Task.Yield();
            yield return reader.Read(Pages[i], ct);
        }
    }
#endif
}
