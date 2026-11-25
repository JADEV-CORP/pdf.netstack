namespace PdfNetStack.Extensions.Hosting;

/// <summary>
/// PdfNetStack settings for a host, set in code with
/// <see cref="Microsoft.Extensions.DependencyInjection.PdfNetStackServiceCollectionExtensions.AddPdfNetStack(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{PdfNetStackOptions}?)"/>
/// or bound from a configuration section, by default <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// The options read by <see cref="IPdfOptimizer"/> are taken afresh for every run, so a reloaded
/// configuration applies to the next one. <see cref="PdfOptimizerOptions"/> and
/// <see cref="PdfLimits"/> are immutable records: change them in code with a <c>with</c>
/// expression.
/// </remarks>
/// <example>
/// <code>
/// // appsettings.json
/// // "PdfNetStack": {
/// //   "Optimizer": { "JpegQuality": 80, "MaxImageDpi": 150 },
/// //   "Limits": { "MaxInputBytes": 104857600, "Timeout": "00:00:30" }
/// // }
/// builder.Services.AddPdfNetStack(builder.Configuration.GetSection(PdfNetStackOptions.SectionName));
///
/// // Or in code.
/// builder.Services.AddPdfNetStack(o => o.Optimizer = o.Optimizer with { Linearize = true });
/// </code>
/// </example>
public sealed class PdfNetStackOptions
{
    /// <summary>The configuration section the options are usually bound from: "PdfNetStack".</summary>
    public const string SectionName = "PdfNetStack";

    /// <summary>
    /// How <see cref="IPdfOptimizer"/> optimizes. Its <see cref="PdfOptimizerOptions.Limits"/> is
    /// replaced by <see cref="Limits"/>. Default <see cref="PdfOptimizerOptions.Default"/>.
    /// </summary>
    public PdfOptimizerOptions Optimizer { get; set; } = PdfOptimizerOptions.Default;

    /// <summary>
    /// Resource limits for every optimization, and for documents the application opens with
    /// them (<see cref="PdfLoadOptions.Limits"/>, through <c>IOptions&lt;PdfLimits&gt;</c>). Default
    /// <see cref="PdfLimits.Untrusted"/>, since a host usually processes files it did not make.
    /// </summary>
    public PdfLimits Limits { get; set; } = PdfLimits.Untrusted;

    /// <summary>The optimizer options a run uses: <see cref="Optimizer"/> with <see cref="Limits"/>.</summary>
    internal PdfOptimizerOptions EffectiveOptimizer => Optimizer with { Limits = Limits };
}
