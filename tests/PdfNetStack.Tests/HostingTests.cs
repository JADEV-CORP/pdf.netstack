#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PdfNetStack.Extensions.Hosting;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class HostingTests
{
    private static byte[] SmallPdf()
    {
        PdfDocument doc = PdfDocument.Create();
        using (Drawing.PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Hello", Fonts.PdfStandardFont.Helvetica, 12, Drawing.PdfColor.Black, 72, 72);
        return doc.ToArray(new PdfSaveOptions { Optimize = false });
    }

    [TestMethod]
    public void RegistersTheOptimizerAndOptionsOnce()
    {
        var services = new ServiceCollection();
        services.AddPdfNetStack(o => o.Optimizer = o.Optimizer with { Linearize = true });
        services.AddPdfNetStack(o => o.Limits = o.Limits with { MaxTextCharsPerPage = 1000 });
        using ServiceProvider provider = services.BuildServiceProvider();

        IPdfOptimizer optimizer = provider.GetRequiredService<IPdfOptimizer>();
        Assert.AreSame(optimizer, provider.GetRequiredService<IPdfOptimizer>(), "a singleton");
        Assert.HasCount(1, services.Where(s => s.ServiceType == typeof(IPdfOptimizer)));

        PdfOptimizerOptions optimizerOptions = provider.GetRequiredService<IOptions<PdfOptimizerOptions>>().Value;
        Assert.IsTrue(optimizerOptions.Linearize);
        Assert.AreEqual(1000, optimizerOptions.Limits.MaxTextCharsPerPage, "the host's limits replace the optimizer's");
        Assert.AreEqual(PdfLimits.Untrusted.Timeout, provider.GetRequiredService<IOptions<PdfLimits>>().Value.Timeout);
    }

    [TestMethod]
    public void BindsOptionsFromConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PdfNetStack:Optimizer:JpegQuality"] = "80",
            ["PdfNetStack:Optimizer:Linearize"] = "true",
            ["PdfNetStack:Limits:MaxInputBytes"] = "1048576",
            ["PdfNetStack:Limits:Timeout"] = "00:00:30",
        }).Build();

        var services = new ServiceCollection();
        services.AddPdfNetStack(configuration.GetSection(PdfNetStackOptions.SectionName), o => o.Optimizer = o.Optimizer with { MaxImageDpi = 150 });
        using ServiceProvider provider = services.BuildServiceProvider();
        PdfNetStackOptions options = provider.GetRequiredService<IOptions<PdfNetStackOptions>>().Value;
        Assert.AreEqual(80, options.Optimizer.JpegQuality);
        Assert.IsTrue(options.Optimizer.Linearize);
        Assert.AreEqual(150, options.Optimizer.MaxImageDpi, "code runs after the binding");
        Assert.AreEqual(1048576, options.Limits.MaxInputBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.Limits.Timeout);
        Assert.AreEqual(PdfLimits.Untrusted.MaxOperationsPerDocument, options.Limits.MaxOperationsPerDocument, "unbound values keep the host default");
    }

    [TestMethod]
    public void BindsAPresetAndRejectsUnknownKeys()
    {
        IConfiguration preset = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Optimizer:Verify"] = "false",
            ["Optimizer:Preset"] = "smallest",
            ["Optimizer:JpegQuality"] = "",
            ["Limits:Timeout"] = "Infinite",
        }).Build();
        var options = new PdfNetStackOptions();
        PdfNetStackConfiguration.Bind(preset, options);
        Assert.AreEqual(PdfOptimizerOptions.Smallest with { Verify = false, JpegQuality = null }, options.Optimizer);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, options.Limits.Timeout);

        IConfiguration typo = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Optimizer:JpgQuality"] = "80" }).Build();
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => PdfNetStackConfiguration.Bind(typo, new PdfNetStackOptions()));
        StringAssert.Contains(e.Message, "Optimizer:JpgQuality");

        IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Limits:MaxInputBytes"] = "lots" }).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfNetStackConfiguration.Bind(bad, new PdfNetStackOptions()));
    }

    [TestMethod]
    public void BindsEveryOption()
    {
        // A property added to the options without a configuration key fails here.
        static string[] Settable(Type type) => [.. type.GetProperties().Where(p => p.CanWrite && p.PropertyType != typeof(PdfLimits)).Select(p => p.Name).Order()];
        CollectionAssert.AreEquivalent(Settable(typeof(PdfOptimizerOptions)), PdfNetStackConfiguration.OptimizerProperties.ToArray());
        CollectionAssert.AreEquivalent(Settable(typeof(PdfLimits)), PdfNetStackConfiguration.LimitProperties.ToArray());
    }

    [TestMethod]
    public async Task TracesAndMeasuresEachRun()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddPdfNetStack();
        using ServiceProvider provider = services.BuildServiceProvider();
        IPdfOptimizer optimizer = provider.GetRequiredService<IPdfOptimizer>();

        var measurements = new List<(string Name, double Value, string? Disposition)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PdfNetStackTelemetry.Name) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => measurements.Add((i.Name, v, Disposition(tags))));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => measurements.Add((i.Name, v, Disposition(tags))));
        listener.Start();

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PdfNetStackTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        byte[] input = SmallPdf();
        using var output = new MemoryStream();
        PdfOptimizationResult result = await optimizer.OptimizeAsync(new MemoryStream(input), output);
        Assert.AreEqual(input.Length, result.OriginalBytes);

        string disposition = result.Disposition.ToString();
        Assert.IsTrue(measurements.Exists(m => m.Name == "pdfnetstack.optimize.duration" && m.Disposition == disposition));
        Assert.IsTrue(measurements.Exists(m => m.Name == "pdfnetstack.optimize.input.size" && m.Value == input.Length));
        Assert.IsTrue(measurements.Exists(m => m.Name == "pdfnetstack.optimize.output.size" && m.Value == output.Length));
        Assert.IsTrue(measurements.Exists(m => m.Name == "pdfnetstack.optimize.runs" && m.Value == 1 && m.Disposition == disposition));
        Activity activity = activities.Single(a => a.OperationName == PdfNetStackTelemetry.OptimizeActivity);
        Assert.AreEqual(disposition, activity.GetTagItem("pdfnetstack.optimize.disposition"));

        // A run that throws is counted as failed, with the error's type, and rethrown.
        measurements.Clear();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => optimizer.OptimizeAsync(new MemoryStream(input), new MemoryStream(), cts.Token));
        Assert.IsTrue(measurements.Exists(m => m.Name == "pdfnetstack.optimize.runs" && m.Disposition == "Failed"));
    }

    private static string? Disposition(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "pdfnetstack.optimize.disposition") return tag.Value as string;
        }

        return null;
    }
}
#endif
