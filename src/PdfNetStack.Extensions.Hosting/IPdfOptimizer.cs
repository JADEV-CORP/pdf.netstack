namespace PdfNetStack.Extensions.Hosting;

/// <summary>
/// Optimizes PDFs with the host's configured <see cref="PdfNetStackOptions"/>, and reports each
/// run to OpenTelemetry (see <see cref="PdfNetStackTelemetry"/>). Registered by
/// <see cref="Microsoft.Extensions.DependencyInjection.PdfNetStackServiceCollectionExtensions.AddPdfNetStack(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{PdfNetStackOptions}?)"/>
/// as a singleton.
/// </summary>
/// <remarks>
/// The registered implementation is thread safe: it holds no state between runs, so one instance
/// serves concurrent requests. Each run is <see cref="PdfOptimizer.OptimizeAsync(Stream, Stream, PdfOptimizerOptions?, CancellationToken)"/>,
/// which never changes its input and writes the original bytes when no verified saving exists.
/// </remarks>
/// <example>
/// <code>
/// app.MapPost("/compress", async (HttpRequest request, HttpResponse response, IPdfOptimizer optimizer, CancellationToken ct) =>
/// {
///     response.ContentType = "application/pdf";
///     PdfOptimizationResult result = await optimizer.OptimizeAsync(request.Body, response.Body, ct);
/// });
/// </code>
/// </example>
public interface IPdfOptimizer
{
    /// <summary>Optimizes the PDF read from <paramref name="input"/> into <paramref name="output"/> with the configured options.</summary>
    /// <param name="input">The source PDF, read from its current position to its end.</param>
    /// <param name="output">Receives the optimized PDF, or the input unchanged.</param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    /// <returns>What was written and why.</returns>
    Task<PdfOptimizationResult> OptimizeAsync(Stream input, Stream output, CancellationToken cancellationToken = default);

    /// <summary>Optimizes with <paramref name="options"/> instead of the configured options, for one run.</summary>
    /// <param name="input">The source PDF, read from its current position to its end.</param>
    /// <param name="output">Receives the optimized PDF, or the input unchanged.</param>
    /// <param name="options">The options for this run, limits included; the configured options when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    /// <returns>What was written and why.</returns>
    Task<PdfOptimizationResult> OptimizeAsync(Stream input, Stream output, PdfOptimizerOptions? options, CancellationToken cancellationToken = default);
}
