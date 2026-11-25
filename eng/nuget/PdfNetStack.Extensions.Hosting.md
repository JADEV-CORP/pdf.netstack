# PdfNetStack.Extensions.Hosting

Dependency injection, options and OpenTelemetry for [PdfNetStack](https://www.nuget.org/packages/PdfNetStack)
in ASP.NET Core, worker services and any .NET host.

```bash
dotnet add package PdfNetStack.Extensions.Hosting
```

```csharp
builder.Services.AddPdfNetStack(builder.Configuration.GetSection(PdfNetStackOptions.SectionName));

app.MapPost("/compress", async (HttpRequest request, HttpResponse response, IPdfOptimizer optimizer, CancellationToken ct) =>
{
    response.ContentType = "application/pdf";
    await optimizer.OptimizeAsync(request.Body, response.Body, ct);
});
```

```json
{
  "PdfNetStack": {
    "Optimizer": { "Preset": "Balanced", "JpegQuality": 80 },
    "Limits": { "MaxInputBytes": 104857600, "Timeout": "00:00:30" }
  }
}
```

Keys are the properties of `PdfOptimizerOptions` and `PdfLimits`. A key that names no option, or a
value that does not parse, fails at startup with the key's path. Binding reads each key
explicitly, without reflection, so it is safe under trimming and Native AOT. Options can also be
set in code: `AddPdfNetStack(o => o.Optimizer = o.Optimizer with { Linearize = true })`.

`Limits` defaults to `PdfLimits.Untrusted` (256 MiB in, 60 seconds a call), since a host usually
processes files it did not make. It applies to every optimization and is registered as
`IOptions<PdfLimits>` for documents the application opens itself.

## Telemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(PdfNetStackTelemetry.Name))
    .WithMetrics(m => m.AddMeter(PdfNetStackTelemetry.Name));
```

Each run is an activity, `pdfnetstack.optimize`, and records `pdfnetstack.optimize.duration`
(seconds), `pdfnetstack.optimize.input.size` and `pdfnetstack.optimize.output.size` (bytes) and
`pdfnetstack.optimize.runs`, tagged with the disposition: Optimized, OriginalRetained,
UnsupportedRetained, LimitExceeded, or Failed with `error.type`.

## Thread safety

`IPdfOptimizer` and every other service registered here are singletons and thread safe. A
`PdfDocument` is not: open one per request, never register one as a singleton.

.NET 8 and later. MIT licensed.
