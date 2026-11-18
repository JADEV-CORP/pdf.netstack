---
title: Cancellation and progress
description: Stop long PDF work in .NET services with cancellation tokens, report progress, and merge uploads into an ASP.NET Core response one input at a time.
since: '1.3'
sidebar:
  label: Cancellation and progress
faq:
  - q: How do I stop PDF work when an ASP.NET Core client disconnects?
    a: Pass HttpContext.RequestAborted, or a token linked to it with a timeout, to OpenAsync, ExtractText, Merge, Save, the optimizer and the other calls that take one. Work stops within 256 content operators, inside image decoders, and between objects while saving.
  - q: Can PdfNetStack merge large uploads without holding them all in memory?
    a: Yes. PdfDocument.MergeAsync reads the inputs one at a time, opens each on demand, copies its pages and releases it before the next. With PdfSaveOptions.Optimize set to false, the merged file is written progressively to the output stream.
---

Long calls take a `CancellationToken` and report progress through `IProgress<PdfProgress>`, so
a service can stop work nobody waits for and show how far a batch has come.

## Short answer

Pass the request's token to every call that takes one. In ASP.NET Core that is
`HttpContext.RequestAborted`; link it to a timeout of your own:

```csharp sample="cancel-server.cs" region="text"
```

`PdfLimits.Untrusted` bounds what the file may cost on top of that, see
[Resource limits](/concepts/resource-limits/).

## Calls that take a token

| Area | Calls |
|---|---|
| Opening and saving | `PdfDocument.OpenAsync`, `Save(Stream, options, token)`, `SaveAsync`, `ToArray(options, token)` |
| Reading pages | `PdfPage.ExtractText`, `GetWords`, `GetLetters`, `GetImages`, `PdfPageImage.Decode` |
| Pages | `Pages.Import`, `PdfDocument.Merge`, `MergeAsync`, `Split`, `SplitTo`, `SplitToAsync`, `ExtractPages`, `FlattenAnnotations`, `PdfAcroForm.Flatten` |
| Optimizer | `PdfOptimizer.Optimize`, `OptimizeAsync`, `OptimizeFileAsync` |
| Signatures | `PdfSigner.Sign`, `Verify`, `AddValidationData` |
| HTML | `HtmlConverter.ConvertToPdf`, `ConvertToPdfAsync`, `ConvertToDocument`, `ConvertToDocumentAsync` |

The overloads without a token behave as before. Content is checked every 256 operators, the JPEG,
JPEG 2000, JBIG2 and CCITT decoders check inside an image, and saving checks before each object.
A cancelled call raises `OperationCanceledException`.

What a cancelled call leaves behind:

- `Pages.Import` leaves the target document as it was; `Merge` and `ExtractPages` return nothing.
- `Split` stops yielding parts; those already returned are yours to dispose.
- `SplitTo` and `SplitToAsync` keep the parts written before; the stream asked for last is
  disposed and holds no complete file.
- `FlattenAnnotations` keeps the pages flattened before the token fired.
- `Save`, `SaveAsync` and `MergeAsync` leave an incomplete file in the output stream: discard it.

Some steps take no token yet: parsing the file once `OpenAsync` has read it, `PdfInspector.Inspect`
and `PdfEquivalence.FindDifference`. `PdfLimits.Timeout` bounds opening and inspection, so set it
when those run on files from users.

## Merge uploads into a response

`MergeAsync` takes the inputs as streams and holds one at a time: it reads an input to its end,
opens it on demand, copies its pages, releases it and disposes the stream before it asks for the
next. With the optimizer off, the merged file is written to the response as it is serialized:

```csharp sample="cancel-server.cs" region="merge"
```

With the default save options the whole output is built and optimized in memory before it is
written, which proves it and makes it smaller. Choose by the size of what you merge. Pass
`PdfMergeOptions.LeaveInputsOpen` to keep the input streams open for your own code to dispose.

The same call merges files from disk, opening each when its turn comes:

```csharp sample="cancel-progress.cs" region="merge-files"
```

## Progress

`PdfProgress` carries the items done so far and, when it is known in advance, the total. Each
call says what it counts:

| Call | Counts |
|---|---|
| `Merge` | Pages merged, reported after each document |
| `MergeAsync` | Inputs merged, out of their number when the inputs are a collection |
| `Split`, `SplitTo`, `SplitToAsync` | Parts made or written, one per page |
| `FlattenAnnotations` | Pages flattened |

```csharp sample="cancel-progress.cs" region="progress"
```

Reports arrive on the thread doing the work, unless the `IProgress<PdfProgress>` given is a
`Progress<T>`, which posts them to the context it was created on. A handler that throws stops the
operation with its exception.

## Split into streams

`SplitTo` writes each page to a stream your code returns for it, disposing that stream before it
asks for the next, which commits zip entries, files and blob uploads one at a time:

```csharp sample="cancel-progress.cs" region="split-to"
```

`ExtractPages` copies a range into a new document, with ranges on .NET 8 and later:

```csharp sample="cancel-progress.cs" region="extract"
```

## Limits

- Parsing after `OpenAsync` has read the input, `PdfInspector.Inspect` and
  `PdfEquivalence.FindDifference` do not take a token yet; use `PdfLimits.Timeout`.
- A cancelled save or merge writes part of a file to the output stream. Write to a temporary file
  or buffer first when a partial response is a problem.
- `MergeAsync` holds a copy of every page's objects until the output is written.

## Related

- [Resource limits and diagnostics](/concepts/resource-limits/)
- [Edit pages](/guides/edit-pages/): merge, split and extract
- [Large files](/guides/large-files/): opening on demand
- API: [PdfProgress](/api/pdfnetstack/pdfprogress/), [PdfMergeOptions](/api/pdfnetstack/pdfmergeoptions/), [PdfDocument](/api/pdfnetstack/pdfdocument/)
