---
title: Memory and performance
description: Plan memory and CPU for PdfNetStack in .NET services and batch jobs, cap parallelism, choose compression effort, and publish with Native AOT.
sidebar:
  label: Memory and performance
faq:
  - q: How much memory does PdfNetStack need to optimize a PDF?
    a: Plan from the decoded size of the streams, not the file size. Beyond the document itself, the optimizer holds about three times the largest decoded stream for each worker, and from 1.3 its default worker count follows the memory the runtime reports, a container's limit included. Measured on 1.2.0, the peak working set was 24 to 60 times the input file. To read or edit a large file without loading it, open it with PdfDocument.OpenOnDemand (1.1).
  - q: Is PdfNetStack compatible with Native AOT and trimming?
    a: Yes, on .NET 8 and later. The core, HTML and OCR libraries are marked AOT compatible, and CI publishes them with Native AOT on Linux x64 and arm64 and fails on any trim or AOT warning.
---

What PdfNetStack costs in memory and time, and the settings that change it.

## Short answer

The optimizer reads a document whole into memory and processes it there. Plan from the decoded
size of its streams, not from the size of the file: on top of the document, each worker holds
about three times the largest decoded stream it works on, so the peak is about
`workers x 3 x largest decoded stream`. From 1.3 the default worker count follows the memory the
runtime reports, a container's limit included, and `PdfLimits` caps what one document may decode
in total. `MaxDegreeOfParallelism` is the main lever on peak memory, and `Compression` the main
lever on time. From 1.1, `PdfDocument.OpenOnDemand` reads and edits a file of any size page by
page, without loading it.

## Workers and the decoded size

Each worker of the optimizer recompresses or compares one stream at a time and holds about three
copies of it while it does: the decoded data and two working buffers. The largest decoded stream
is usually a raw or decoded image (width x height x components) or a page's content.

| Setting | Effect |
|---|---|
| `MaxDegreeOfParallelism` | The most workers. Default: the processor count |
| Memory the runtime reports | From 1.3, the default count is also capped at half of it divided by three streams of `MaxDecodedStreamBytes`, so a container with little memory runs fewer workers. On .NET Standard 2.0 there is no such figure and only `MaxDegreeOfParallelism` applies |
| `MaxDecodedStreamBytes` (256 MiB) | The largest stream decoded; larger ones are left as they are |
| Recompression budget | From 1.3, the workers of one run share a budget of twice `MaxDecodedStreamBytes` for recompression, and all runs in the process share half the memory the runtime reports. A worker waits until its stream fits |
| `PdfLimits.MaxTotalDecodedBytes` | From 1.3, the most one run may decode in total, all passes together; past it the input is returned with `LimitExceeded` |

## Measured memory use

On 18 November 2026, during 1.3 development, a generated file of 16 RGB images of 2400 by 2400
pixels (2 MB on disk, about 17 MB per decoded image) was optimized with 11 workers on macOS
arm64, .NET 10, 11 cores and 18 GB of memory. The peak working set went from 0.91 to 0.99 GB on
1.2.0 to 0.75 to 0.86 GB. The memory still held after the call went from 1.3 GB to 25 MB: large
decode buffers no longer stay in the shared array pool. On ordinary image heavy files the peak is
8 to 20% lower than on 1.2.0.

Measured on 17 November 2026 with PdfNetStack 1.2.0 on .NET 10, macOS arm64, lossless default
options (`MaxDegreeOfParallelism` equal to the processor count), in a warm process with
workstation GC, kept here because the ratios still guide sizing:

| Input | Size | Peak working set | Times the input | GC heap limit needed |
|---|---:|---:|---:|---|
| NIST SP 800-53 rev. 5 (61,000 objects) | 5.8 MiB | 323 to 350 MB | 56 to 60 | 256 MB (192 MB failed) |
| arXiv 2306.01681 | 34.5 MiB | 821 MB | 24 | Not measured |
| arXiv 2311.06340 (image heavy) | 16.0 MiB | Not measured | | 512 MB, in memory and with `Streaming` |

One NIST run allocated 1.64 GB in total and took 1.2 to 1.5 seconds. The lossy image options
decode images to pixels and need more: on the public corpus in CI (77 files, Linux, 4 cores, 16
November 2026), the largest peak working set was 618 MiB for the lossless default, 1.8 GiB for the
lossy profile and 2.4 GiB for the `Balanced` preset.

`Streaming` keeps the file itself out of memory, but memory still follows the objects and the
streams being recompressed, so image heavy files need room in that mode too.

## Sizing containers and functions

- **Start from the largest decoded stream you expect.** For scans and photographs that is the
  largest image's pixels times its components. Allow `workers x 3 x` that, plus the document
  itself and the output, and not below 512 MB. As a cross check, 1.2.0 peaked at 24 to 60 times
  the file on the files above. Measure your own files: the ratio depends on how many objects and
  how many decoded image bytes a file has, not on its size.
- **Bound the input.** Set `PdfLimits.MaxInputBytes`, or use `PdfLimits.Untrusted` (256 MiB of
  input, 1 GiB decoded in total) for files from users, see
  [Resource limits](/concepts/resource-limits/).
- **Leave room for the heap limit.** In a container with a memory limit, .NET caps the GC heap at
  75% of that limit by default. A 256 MB container gives a 192 MB heap, which is not enough for
  NIST SP 800-53 above. `System.GC.HeapHardLimitPercent` in `runtimeconfig.json`, or
  `DOTNET_GCHeapHardLimitPercent` as an environment variable (a hexadecimal value), changes the
  share; `System.GC.HeapHardLimit` and `DOTNET_GCHeapHardLimit` set it in bytes. See
  [GC configuration](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
- **Lower `MaxDegreeOfParallelism` to cut the peak.** Each worker decodes its own streams. In a
  service, set it per request; in a batch, see [Many documents at once](#many-documents-at-once).
  From 1.3 the default already shrinks when the runtime reports little memory.
- **Out of memory reaches your code.** From 1.3 the optimizer no longer turns an
  `OutOfMemoryException` into a retained result. A run stopped by a limit returns the input with
  `LimitExceeded`, and the result's `Diagnostics` name the limit.
- **Serverless.** In AWS Lambda, memory also buys CPU, so size the function from the numbers above
  and cap the input size. See [AWS Lambda](/guides/aws-lambda/) and
  [Azure Functions](/guides/azure-functions/).

## Server GC and DATAS

The runtime's default for a standalone app is workstation GC; hosts such as ASP.NET Core choose
their own. `GCSettings.IsServerGC` tells you which one a process uses.

- **Server GC is faster here.** On NIST SP 800-53 (1.2.0, same machine as above), Server GC ran
  the optimizer 17 to 22% faster than workstation GC, and total GC pause time per run fell from 258
  ms to 80 ms. Turn it on with `<ServerGarbageCollection>true</ServerGarbageCollection>` in the
  project, or `DOTNET_gcServer=1`.
- **Keep DATAS with Server GC.** Server GC keeps a heap per logical CPU, which costs memory.
  DATAS (dynamic adaptation to application sizes) adapts the number of heaps to the load and is
  on by default from .NET 9. On .NET 8, turn it on with
  `<GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>` or
  `DOTNET_GCDynamicAdaptationMode=1`.
- **Many small instances.** For many processes on few cores, such as one container per request,
  workstation GC can be the better choice. Measure both on your files.

## Memory

PDFs are random access by design and every optimizer pass touches most of the file, so the input
is read into one buffer and stream data is sliced from it, never copied until it is re-encoded.
Peak memory then depends on the decoded size of the streams being worked on and on how many are
worked on at once.

| Setting | Effect on memory |
|---|---|
| `MaxDegreeOfParallelism` (default: processor count) | Caps the worker threads of stream recompression and verification, so fewer streams are decoded at once. From 1.3 the default is also capped by the memory the runtime reports. Lower it for image heavy files |
| `MaxDecodedStreamBytes` (default 256 MiB) | Ceiling for any single decoded stream; larger streams are left untouched |
| `MaxInputBytes` (default 1 GiB) | Larger inputs are returned unchanged. `OptimizeAsync` buffers at most this much and streams anything larger through to the output untouched |
| `Streaming` (1.1, default false) | The source is read on demand and the rewrite goes through a temporary file, proven there: memory follows the number of objects and a batch of at most 32 MiB of source data, not the file |

`PdfDocument.Open(ReadOnlyMemory<byte>)` references the buffer you pass instead of copying it, so
the buffer must not change while the document is in use. The stream and file overloads read the
input into memory first, once (1.1). Opened on demand (`PdfLoadMode.OnDemand`, 1.1), a document
reads only the cross-reference data and the page tree, parses other objects when first used and
reads stream data each time it is needed, so memory follows what your code touches;
`SaveIncremental` then appends only the changes. `PdfLoadOptions` also caps each decoded stream
and image, and from 1.3 `PdfLoadOptions.Limits` caps what the whole document may decode. See
[Large files](/guides/large-files/) and [Resource limits](/concepts/resource-limits/).

A measured example from the README: a 631 page catalogue of 72.6 MiB, already Flate compressed,
took about 5 seconds with all cores and peaked at a working set of about 690 MB.

## Time

| Setting | Effect |
|---|---|
| `Compression = CompressionEffort.Maximum` (default) | zlib level 9 |
| `Compression = CompressionEffort.Balanced` | zlib level 6. On the reference corpus it cut total time by about a third for about 2% larger output |
| `Compression = CompressionEffort.Fast` | zlib level 1, for throughput bound batch jobs |
| `Compression = CompressionEffort.Exhaustive` (1.1) | An optimal parsing Deflate search, kept only where it beats level 9. On synthetic files it saved 3 to 5% more on text, scans and photographs, for 2 to 150 times the CPU time |
| `MaxImageDpi`, `ConvertImagesToJpeg`, `MinImageSsim` (1.1) | The lossy image options decode the images they consider to pixels, resample and encode them, and the proof decodes them again, so they cost time and memory the lossless passes do not. Without them the optimizer never decodes pixels |
| `Verify = false` | Skips the equivalence proof. Only for inputs a validated pipeline already checked |
| `ObjectsPerStream` (default 200) | Larger compresses better, smaller lets viewers load single pages faster |

Parsing and graph work run on one thread. Stream recompression and stream comparison run in
parallel, after every object they need has been resolved.

On the public corpus (196.6 MiB), the Native AOT command line tool on one thread per file took
about as long as qpdf, its proof of every output included. On a private corpus of 1,296 files
(569 MiB), version 1.0 took about 65 seconds on an 8 core machine, 8 to 9 MB/s, lossless JPEG
re-encoding included. See [Benchmarks](/benchmarks/).

## Many documents at once

For a batch, run documents in parallel and give each one thread rather than giving one document
every core. `pdfnetstack batch` does this by default: `--jobs` documents at once (default: all
cores), each with one thread unless `--threads` is given. In a service, set
`MaxDegreeOfParallelism` to bound the CPU and memory one request can take.

## Native AOT and trimming

On .NET 8 and .NET 10 the `PdfNetStack`, `PdfNetStack.Html` and `PdfNetStack.Ocr` libraries are
marked AOT compatible, which also enables the trim analyzers. CI publishes the command line tool
and a smoke test app that roots all three libraries with Native AOT for linux-x64 and linux-arm64,
fails on any trim or AOT warning, and runs the result. The core and HTML libraries have no native
code to ship per platform. For serverless hosts, see [AWS Lambda](/guides/aws-lambda/) and
[Azure Functions](/guides/azure-functions/).

## .NET Framework and .NET Standard 2.0

The `netstandard2.0` build runs on .NET Framework 4.6.2 to 4.8.1, Mono, Unity and .NET Core 2.0 and
later, with Microsoft's `System.Memory` as its only dependency. .NET Standard's zlib has no level
9, so `CompressionEffort.Maximum` compresses a little less there. The full test suite and the real
world corpus pass on .NET Framework 4.8. On .NET 9 and later, `System.IO.Compression` uses zlib-ng.

## Limits

- In memory, the default, the optimizer needs the whole document, and so do `ToArray` and a full
  `Save` with the optimizer on. A save with `Optimize = false` is written progressively, and the
  optimizer's `Streaming` mode (1.1) keeps memory bounded for files larger than memory; see
  [Large files](/guides/large-files/).
- Linearized output (`Linearize`, 1.1) writes every object on its own, without object streams,
  so it can be larger than the default output.
- Faster text extraction and HTML layout are roadmap items.

## Related

- [Resource limits and diagnostics](/concepts/resource-limits/): budgets for a whole document
- [Compress PDF](/guides/compress-pdf/): every optimizer option
- [Safety model](/concepts/safety-model/): the limits that bound hostile input
- [Architecture](/concepts/architecture/): the pipeline and its threading
- API: [PdfOptimizerOptions](/api/pdfnetstack/pdfoptimizeroptions/), [CompressionEffort](/api/pdfnetstack/compressioneffort/)
