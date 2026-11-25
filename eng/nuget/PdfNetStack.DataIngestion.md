# PdfNetStack.DataIngestion

A PDF reader for [Microsoft.Extensions.DataIngestion](https://learn.microsoft.com/dotnet/dataingestion/microsoft-extensions-dataingestion),
built on [PdfNetStack](https://www.nuget.org/packages/PdfNetStack). Each page becomes a section of
headings, paragraphs, list items and tables, in reading order, each with its Markdown, its plain
text and its page number.

```bash
dotnet add package PdfNetStack.DataIngestion --prerelease
```

```csharp
using Microsoft.Extensions.DataIngestion;
using PdfNetStack.DataIngestion;

IngestionDocumentReader reader = new PdfNetStackReader();
IngestionDocument doc = await reader.ReadAsync(new FileInfo("handbook.pdf"), cancellationToken);
foreach (IngestionDocumentElement element in doc.EnumerateContent())
{
    Console.WriteLine($"p{element.PageNumber}: {element.GetMarkdown()}");
}
```

Use it wherever a pipeline takes an `IngestionDocumentReader`, in place of a PdfPig based reader.

## What it reads

- Tagged PDFs in the order of their structure tree: headings with their level, lists, tables with
  their cells. Untagged pages are read in layout order, columns of prose one after the other;
  headings come from font size, lists from their bullets and numbers, and tables only from ruled
  lines, never from loose columns.
- Artifacts the producer marked (running headers, footers, page numbers, watermarks) are left out.
- Text a reader cannot see is left out: invisible text, white on white or in the colour of the box
  under it, fully transparent, clipped away, off the page, under one point, on a layer that is off,
  or covered by a shape drawn after it. Hidden text is how prompt injection is planted in
  documents (OWASP LLM01:2025). A page where any was found carries the number of hidden runs in
  its section's metadata under `pdfnetstack.hidden_text_runs`; log it or quarantine the document.
- The OCR text layer of a scanned page is kept, and flagged with `pdfnetstack.ocr_text`.

Options go through the constructor:

```csharp
var reader = new PdfNetStackReader(
    new PdfTextOptions { UseStructureTree = true, ExcludeArtifacts = true, VisibleOnly = true },
    new PdfLoadOptions { Limits = PdfLimits.Untrusted });
```

## Status

Microsoft.Extensions.DataIngestion is published as a preview only, so this package is a preview
too. The reader depends on `Microsoft.Extensions.DataIngestion.Abstractions` alone. .NET 8 and
later; pure C#, no native binaries, Native AOT compatible. MIT licensed.
