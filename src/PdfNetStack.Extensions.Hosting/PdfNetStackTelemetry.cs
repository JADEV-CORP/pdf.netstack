using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PdfNetStack.Extensions.Hosting;

/// <summary>
/// The names PdfNetStack reports under, for OpenTelemetry: one <see cref="ActivitySource"/> and one
/// <see cref="Meter"/>, both named <see cref="Name"/>.
/// </summary>
/// <remarks>
/// <para>Each run of <see cref="IPdfOptimizer"/> is an activity named <see cref="OptimizeActivity"/> and records:</para>
/// <list type="bullet">
/// <item><description><c>pdfnetstack.optimize.duration</c>: histogram of run time, in seconds;</description></item>
/// <item><description><c>pdfnetstack.optimize.input.size</c> and <c>pdfnetstack.optimize.output.size</c>: histograms of bytes in and out;</description></item>
/// <item><description><c>pdfnetstack.optimize.runs</c>: counter of runs.</description></item>
/// </list>
/// <para>
/// Every measurement carries <c>pdfnetstack.optimize.disposition</c> (Optimized, OriginalRetained,
/// UnsupportedRetained, LimitExceeded, or Failed when the run threw) and, for a run that threw,
/// <c>error.type</c>. Byte sizes are recorded for runs that complete.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(PdfNetStackTelemetry.Name))
///     .WithMetrics(m => m.AddMeter(PdfNetStackTelemetry.Name));
/// </code>
/// </example>
public static class PdfNetStackTelemetry
{
    /// <summary>The name of the activity source and of the meter: "PdfNetStack".</summary>
    public const string Name = "PdfNetStack";

    /// <summary>The name of the activity of one optimization run: "pdfnetstack.optimize".</summary>
    public const string OptimizeActivity = "pdfnetstack.optimize";

    /// <summary>The disposition tag's value for a run that threw.</summary>
    internal const string Failed = "Failed";

    internal static readonly ActivitySource Source = new(Name, typeof(PdfNetStackTelemetry).Assembly.GetName().Version?.ToString());
}

/// <summary>
/// The instruments, from the host's meter factory, which owns and disposes the meter; without one,
/// from a meter of their own, disposed with the service provider.
/// </summary>
internal sealed class PdfNetStackMetrics : IDisposable
{
    private readonly Meter? _own;

    public PdfNetStackMetrics(IMeterFactory? factory)
    {
        Meter meter = factory?.Create(PdfNetStackTelemetry.Name) ?? (_own = new Meter(PdfNetStackTelemetry.Name));
        Duration = meter.CreateHistogram<double>("pdfnetstack.optimize.duration", "s", "Duration of PDF optimization runs.");
        InputSize = meter.CreateHistogram<long>("pdfnetstack.optimize.input.size", "By", "Size of the PDFs optimized.");
        OutputSize = meter.CreateHistogram<long>("pdfnetstack.optimize.output.size", "By", "Size of the PDFs written by optimization runs.");
        Runs = meter.CreateCounter<long>("pdfnetstack.optimize.runs", "{run}", "PDF optimization runs, by disposition.");
    }

    public Histogram<double> Duration { get; }

    public Histogram<long> InputSize { get; }

    public Histogram<long> OutputSize { get; }

    public Counter<long> Runs { get; }

    public void Dispose() => _own?.Dispose();
}
