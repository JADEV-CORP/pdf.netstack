#:sdk Microsoft.NET.Sdk.Web
#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.3
// run: build-only starts a web server and waits for requests
using System.Text;
using PdfNetStack;

WebApplication app = WebApplication.CreateBuilder(args).Build();

#region text
// Text of an uploaded PDF. When the client goes away, RequestAborted stops the work.
app.MapPost("/text", async (HttpContext context) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    CancellationToken cancellationToken = timeout.Token;

    var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, Limits = PdfLimits.Untrusted };
    using PdfDocument doc = await PdfDocument.OpenAsync(context.Request.Body, options, cancellationToken);

    var text = new StringBuilder();
    foreach (PdfPage page in doc.Pages)
    {
        text.AppendLine(page.ExtractText(cancellationToken));
    }

    return Results.Text(text.ToString());
});
#endregion

#region merge
// Merge uploaded files straight into the response, one input in memory at a time.
app.MapPost("/merge", async (HttpContext context) =>
{
    IFormFileCollection files = (await context.Request.ReadFormAsync(context.RequestAborted)).Files;
    var options = new PdfMergeOptions
    {
        LoadOptions = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, Limits = PdfLimits.Untrusted },
        SaveOptions = new PdfSaveOptions { Optimize = false },   // written progressively
    };

    context.Response.ContentType = "application/pdf";
    await PdfDocument.MergeAsync(files.Select(file => file.OpenReadStream()), context.Response.Body, options,
        progress: null, context.RequestAborted);
});
#endregion

app.Run();
