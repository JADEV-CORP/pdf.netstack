using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace PdfNetStack.Extensions.Hosting;

/// <summary>
/// <see cref="IPdfOptimizer"/> over <see cref="PdfOptimizer"/>: the options current at the start of
/// each run, an activity around it, and its measurements. Stateless between runs, so thread safe.
/// </summary>
internal sealed class PdfOptimizerService(IOptionsMonitor<PdfNetStackOptions> monitor, PdfNetStackMetrics metrics) : IPdfOptimizer
{
    public Task<PdfOptimizationResult> OptimizeAsync(Stream input, Stream output, CancellationToken cancellationToken = default) =>
        OptimizeAsync(input, output, null, cancellationToken);

    public async Task<PdfOptimizationResult> OptimizeAsync(Stream input, Stream output, PdfOptimizerOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        PdfOptimizerOptions run = options ?? monitor.CurrentValue.EffectiveOptimizer;

        using Activity? activity = PdfNetStackTelemetry.Source.StartActivity(PdfNetStackTelemetry.OptimizeActivity);
        long start = Stopwatch.GetTimestamp();
        try
        {
            PdfOptimizationResult result = await PdfOptimizer.OptimizeAsync(input, output, run, cancellationToken).ConfigureAwait(false);
            string disposition = result.Disposition.ToString();
            var tags = new TagList { { "pdfnetstack.optimize.disposition", disposition } };
            metrics.Duration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
            metrics.InputSize.Record(result.OriginalBytes, tags);
            metrics.OutputSize.Record(result.OutputBytes, tags);
            metrics.Runs.Add(1, tags);
            if (activity is not null)
            {
                activity.SetTag("pdfnetstack.optimize.disposition", disposition);
                activity.SetTag("pdfnetstack.optimize.input.size", result.OriginalBytes);
                activity.SetTag("pdfnetstack.optimize.output.size", result.OutputBytes);
                activity.SetTag("pdfnetstack.optimize.pages", result.PageCount);
            }

            return result;
        }
        catch (Exception e)
        {
            // Measured, then rethrown unchanged: cancellation included, as error.type tells it apart.
            var tags = new TagList
            {
                { "pdfnetstack.optimize.disposition", PdfNetStackTelemetry.Failed },
                { "error.type", e.GetType().FullName },
            };
            metrics.Duration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
            metrics.Runs.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            activity?.SetTag("error.type", e.GetType().FullName);
            throw;
        }
    }
}
