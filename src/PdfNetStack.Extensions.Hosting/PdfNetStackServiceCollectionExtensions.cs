using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PdfNetStack;
using PdfNetStack.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers PdfNetStack services in an <see cref="IServiceCollection"/>.</summary>
public static class PdfNetStackServiceCollectionExtensions
{
    /// <summary>
    /// Adds PdfNetStack: <see cref="IPdfOptimizer"/> as a singleton, the
    /// <see cref="PdfNetStackOptions"/> it reads, and <c>IOptions&lt;PdfOptimizerOptions&gt;</c> and
    /// <c>IOptions&lt;PdfLimits&gt;</c> for code that calls the library directly. Calling it again
    /// adds <paramref name="configure"/> to the options and registers nothing twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every type registered here is thread safe. <see cref="PdfDocument"/> is not: open one per
    /// request or operation, as the library's own methods do, and never register one as a singleton.
    /// </para>
    /// <para>
    /// Optimization runs are traced and measured under <see cref="PdfNetStackTelemetry.Name"/>. The
    /// meter comes from the host's <see cref="IMeterFactory"/> when there is one.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets options in code, after any configuration binding; may be null.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddPdfNetStack(this IServiceCollection services, Action<PdfNetStackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<PdfNetStackOptions> options = services.AddOptions<PdfNetStackOptions>();
        if (configure is not null) options.Configure(configure);

        services.TryAddSingleton(sp => new PdfNetStackMetrics(sp.GetService<IMeterFactory>()));
        services.TryAddSingleton<IPdfOptimizer, PdfOptimizerService>();
        services.TryAddSingleton<IOptions<PdfOptimizerOptions>>(sp => Options.Options.Create(sp.GetRequiredService<IOptions<PdfNetStackOptions>>().Value.EffectiveOptimizer));
        services.TryAddSingleton<IOptions<PdfLimits>>(sp => Options.Options.Create(sp.GetRequiredService<IOptions<PdfNetStackOptions>>().Value.Limits));
        return services;
    }

    /// <summary>
    /// Adds PdfNetStack as <see cref="AddPdfNetStack(IServiceCollection, Action{PdfNetStackOptions}?)"/>
    /// does, with options bound from <paramref name="configuration"/>, usually the section named
    /// <see cref="PdfNetStackOptions.SectionName"/>: <c>Optimizer</c> holds
    /// <see cref="PdfOptimizerOptions"/> properties (and <c>Preset</c>: Default, Balanced or
    /// Smallest) and <c>Limits</c> <see cref="PdfLimits"/> ones. A reloaded configuration applies to
    /// the next optimization.
    /// </summary>
    /// <remarks>
    /// Keys are read explicitly, without reflection, so the binding is safe under trimming and
    /// Native AOT. A key that names no option, or a value that does not parse, throws
    /// <see cref="InvalidOperationException"/> naming the key when the options are first read.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section to bind.</param>
    /// <param name="configure">Sets options in code, after the binding; may be null.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddPdfNetStack(this IServiceCollection services, IConfiguration configuration, Action<PdfNetStackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<PdfNetStackOptions>().Configure(options => PdfNetStackConfiguration.Bind(configuration, options));
        services.AddSingleton<IOptionsChangeTokenSource<PdfNetStackOptions>>(new ConfigurationChangeTokenSource<PdfNetStackOptions>(configuration));
        return services.AddPdfNetStack(configure);
    }
}
