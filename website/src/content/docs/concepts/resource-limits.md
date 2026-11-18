---
title: Resource limits and diagnostics
description: Bound what a whole PDF may cost in .NET with PdfLimits, read files from users with the Untrusted preset, and see what a limit or damage left out.
since: '1.3'
sidebar:
  label: Resource limits
faq:
  - q: How do I read PDF files uploaded by users safely?
    a: Open them with PdfLoadOptions.Limits set to PdfLimits.Untrusted, and the optimizer with PdfOptimizerOptions.Limits. That caps the input at 256 MiB, decoding at 1 GiB and 2^29 image pixels in total, content at 25 million operators, text at 250,000 characters a page and each call at 60 seconds. Run the work in a process or container with a memory limit as well.
  - q: Why does a page read as empty text?
    a: Its content may have run past a limit or been damaged. PdfDocument.Diagnostics lists every limit reached, every damaged part skipped and every repair, with the page and object concerned, and PdfLoadOptions.OnWarning hears each one as it happens.
  - q: Does a document kept open hit the limits after many reads?
    a: It can. The totals are spent over the document's whole life, so text read twice is decoded twice. For a document kept open and read many times, lift MaxTotalDecodedBytes, MaxTotalImagePixels and MaxOperationsPerDocument to long.MaxValue, or open it again from time to time.
---

What one document may cost in memory and time, how to set that for files from users of a service,
and how to learn what a limit or a damaged part left out.

## Short answer

`PdfLimits` caps the work a whole document may cause: the size of the file, the bytes and image
pixels decoded in total, the content operators run, the characters read from a page and the time
one call takes. `PdfLimits.Default` stops only what no ordinary document needs. For files from
users of a service, use `PdfLimits.Untrusted`:

```csharp sample="limits-untrusted.cs" region="open"
```

When a limit is reached, the work that needs it stops and the rest of the document stays usable.
`PdfDocument.Diagnostics` says what was left out. Images raise `PdfLimitExceededException`, and
the optimizer writes the input unchanged with `OptimizationDisposition.LimitExceeded`.

## The limits

| Limit | `Default` | `Untrusted` | When it is reached |
|---|---|---|---|
| `MaxInputBytes` | None | 256 MiB | The file is refused before it is read: `PdfLimitExceededException` |
| `MaxTotalDecodedBytes` | 1 GiB plus 4096 bytes per byte of the file | 1 GiB | What needs more decoding is skipped: a page reads and draws nothing more, an image raises `PdfLimitExceededException` |
| `MaxTotalImagePixels` | 2^30 plus 4096 per byte of the file | 2^29 | The image raises `PdfLimitExceededException` |
| `MaxOperationsPerDocument` | 500 million | 25 million | Content stops running; the rest of the page reads as empty |
| `MaxTextCharsPerPage` | 4 million | 250,000 | Letters past it are left out of that page's text |
| `Timeout` | None | 60 seconds | The call raises `PdfLimitExceededException`; the optimizer writes its input, or its lossless rewrite when time ran out during lossy work |

`MaxTotalDecodedBytes` counts every filter layer of every stream: page content each time it is
drawn, fonts, images, object streams, and in the optimizer every pass. The defaults grow with the
file because real documents decode to a few hundred times their size, while a
decompression bomb decodes to millions of times its size. `Untrusted` is never more than
`Default` allows for any file. Start from either with a `with` expression to change one value.

Every limit is checked before the memory it guards is allocated. The per object ceilings stay in
force alongside: `PdfLoadOptions.MaxDecodedStreamBytes` (256 MiB) bounds one stream and
`PdfLoadOptions.MaxImagePixels` (2^28) one image, and each page's content is bounded in bytes,
tokens and operators. See [Safety model](/concepts/safety-model/#limits-on-hostile-input).

## Where limits apply

| Set it on | It bounds |
|---|---|
| `PdfLoadOptions.Limits` | Opening, text and image extraction, and everything else the document reads |
| `PdfOptimizerOptions.Limits` | One optimization, all passes and the proof together |
| `PdfInspector.Inspect(input, limits)` | One inspection |

`Timeout` applies to each call on its own: opening a document, one `ExtractText`, `GetWords`,
`GetLetters` or `GetImages`, one inspection, one optimization. It also covers the steps that take
no cancellation token yet, such as parsing the file after `OpenAsync` has read it. With lossy
options, an optimization in memory that runs out of time during lossy image work writes its
lossless rewrite instead of the input; in streaming mode it writes the input.

The optimizer reports a limit in its result rather than throwing:

```csharp sample="limits-untrusted.cs" region="optimize"
```

```csharp sample="limits-untrusted.cs" region="inspect"
```

## Budgets last as long as the document

A document opened with `Open` or `OpenAsync` spends its totals over its whole life, across every
call made on it: text read twice is decoded twice. A document opened, read once and disposed, as
a request handler does, never notices. A document kept open and read again and again, such as one
held in a cache, can reach the default decode budget after enough reads, and from then on pages
read as empty. For such a document, lift the totals and keep the time limit per call, or open it
again from time to time:

```csharp sample="limits-untrusted.cs" region="long-lived"
```

A document made with `PdfDocument.Create` has no file to scale from; only the per page bounds
apply to it.

## Exceptions

| Exception | Raised for |
|---|---|
| `PdfException` | The base of the exceptions below, so one `catch` handles them all |
| `PdfFormatException` | Data that is not a readable PDF, and the per object ceilings `MaxDecodedStreamBytes` and `MaxImagePixels` |
| `PdfLimitExceededException` | One of the `PdfLimits`; `Limit` says which. Derives from `PdfFormatException`, so code that catches that keeps working |
| `PdfPasswordException` | An encrypted file that no password given opens |

```csharp sample="limits-untrusted.cs" region="errors"
```

Argument errors still raise the `ArgumentException` family, and font files raise
`FontFormatException`. The optimizer no longer catches `OutOfMemoryException`: before 1.3 it came
back as `OriginalRetained` with a reason starting "Rewrite failed", now it reaches your code. Use
the limits to stop work before memory runs out.

## Diagnostics

`PdfDocument.Diagnostics` lists warnings about a document that was still read, oldest first:

| `Kind` | Means |
|---|---|
| `LimitReached` | A limit stopped work early; `Limit` says which |
| `Skipped` | Something damaged or unreadable was left out and the rest went on: a stream that cannot be decoded, a branch of the page tree that loops or nests too deep |
| `Repaired` | The file needed repair to be read: rebuilt cross-reference data, corrected stream lengths, a page count that disagrees with the pages |

Each entry has a `Message` for a log and, where there is one, the `PageIndex` and `ObjectNumber`
concerned. A limit is reported once, the same message for the same object and page is kept once,
and at most 1000 entries are kept: a last entry says how many more were dropped.
`PdfLoadOptions.OnWarning` hears each one as it is added, on the thread doing the work, during
the call that found it; it must not throw. `PdfOptimizationResult.Diagnostics` and
`PdfInspection.Diagnostics` hold the same for one optimization and one inspection.

Text extraction does not throw when a total runs out, only at the time limit: the page returns
what it read so far. In a service, check `Diagnostics` before treating an empty or short text as
the content of the page.

## Limits

- `PdfNetStack.Html` has its own limits on style and layout work and on concurrent conversions,
  not `PdfLimits`. See [HTML to PDF](/guides/html-to-pdf/#untrusted-html).
- The limits bound one document. Several documents processed at once each have their own; bound
  the total with the number you run in parallel and a memory limit on the process or container.
- A limit reached by the optimizer returns the input, not a partial rewrite.

## Related

- [Safety model](/concepts/safety-model/): what the optimizer refuses, and the per object bounds
- [Cancellation and progress](/guides/cancellation-and-progress/): tokens for long calls in a service
- [Memory and performance](/concepts/memory-and-performance/): sizing containers and workers
- API: [PdfLimits](/api/pdfnetstack/pdflimits/), [PdfDiagnostic](/api/pdfnetstack/pdfdiagnostic/), [PdfLimitExceededException](/api/pdfnetstack/pdflimitexceededexception/), [PdfLoadOptions](/api/pdfnetstack/pdfloadoptions/)
