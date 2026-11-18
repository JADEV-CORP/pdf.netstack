# PdfNetStack

Shrink PDFs on a server without breaking them, in pure C#. Forms, tags, signatures and PDF/A
survive, and every output is proved equivalent to its input before it is returned. MIT licensed,
no native binaries, no Ghostscript. Created and maintained by Jamal Abdelkhalek at
[JADEV](https://github.com/JADEV-CORP).

PdfNetStack makes PDFs smaller without changing a single rendered pixel, then proves it: the
output is re-read and compared to the source object by object before it is returned. If anything
differs, you get your original bytes back. Signed and encrypted files are returned untouched.

For teams leaving PDFsharp, whose 6.5.0 release is AGPL-3.0-only, PdfNetStack draws, edits,
merges, splits, fills forms and encrypts under MIT. The
[migration guide](https://www.jadev-corp.com/pdfnetstack/guides/migrate-from-pdfsharp/) maps the
common PDFsharp 6 calls, with before and after code.

```csharp
using PdfNetStack;

var result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");
```

## Features

- **Lossless PDF compression:** object streams, compressed cross-reference streams, unused object
  and resource removal, compact content streams, Flate recompression at level 9, PNG prediction
  for raw images, duplicate font, image and resource merging, merging of separately embedded
  subsets of one font. Optional linearized output for fast web view.
- **Lossless JPEG optimization:** Huffman tables rebuilt per image, progressive JPEGs rewritten as
  baseline, metadata segments dropped. DCT coefficients stay identical.
- **Optional lossy JPEG compression:** `JpegQuality` requantizes JPEG images at a libjpeg quality,
  never finer than the original.
- **Optional lossy image options:** `MaxImageDpi` downsamples oversampled images,
  `ConvertImagesToJpeg` turns photographs into JPEG, and `MinImageSsim` keeps any image a change
  would visibly hurt. `CompressionEffort.Exhaustive` squeezes Flate streams further for archives.
- **Built-in equivalence proof** on every rewrite, also available on its own as
  `PdfEquivalence.FindDifference` to check the output of any PDF tool.
- **Editing:** open, create, merge, split, reorder, rotate, remove and import pages; document
  info; bookmarks. Large files open on demand from a stream and save as incremental updates.
- **Encryption:** open RC4, AES-128 and AES-256 protected PDFs; write AES-256 or AES-128 with
  user and owner passwords and permissions.
- **Creating documents:** a drawing canvas on new and existing pages; standard 14, TrueType,
  OpenType, WOFF and WOFF2 fonts embedded as subsets, variable font instances; text layout with
  justification, right to left and complex scripts; JPEG, PNG, GIF, BMP, TIFF, WebP and SVG images;
  pages of other PDFs as watermarks and letterheads.
- **Text and images out:** page text in reading order, letters and words with bounds and fonts;
  images per page decoded to pixels, including JPEG 2000, JBIG2 and CCITT scans.
- **Forms and annotations:** create, read and fill AcroForm fields with generated appearances;
  links, notes, markup, stamps, ink and shapes; flattening.
- **Signatures and archiving:** PAdES B-B, B-T, B-LT and B-LTA signing and verification with
  chain, revocation and policy checks; PDF/A-1b to PDF/A-4 output; Factur-X and ZUGFeRD invoices.
- **Fails safe:** signed and encrypted files are never rewritten by the optimizer, damaged files
  only when you allow it. Nesting, each decoded stream and each image are capped, and the
  optimizer is fuzz tested. Limits for whole documents from untrusted sources are being added in
  1.3.

## Editing

```csharp
PdfDocument doc = PdfDocument.Open("contract.pdf");
doc.Pages.RemoveAt(0);
doc.Pages[0].Rotate(90);
doc.Pages.Import(PdfDocument.Open("annex.pdf").Pages);
doc.Info.Title = "Signed contract";
doc.Security.Encrypt(userPassword: "read", ownerPassword: "admin");
doc.Save("contract-final.pdf");   // optimized and verified by default
```

## Compatibility

| Target | Runs on | Dependencies |
|---|---|---|
| `net8.0`, `net10.0` | .NET 8, 9, 10 and later. Trim and Native AOT compatible | None |
| `netstandard2.0` | .NET Framework 4.6.2+, Mono, Unity, .NET Core 2.0+ | `System.Memory` (Microsoft, MIT) |

Tested on Windows, Linux and macOS, on .NET Framework 4.8, .NET 8 and .NET 10.

## Results

On the public corpus of 77 openly licensed PDFs, with default options, PdfNetStack saved 11.2%
losslessly and all 76 files PDFium can open kept their pages, text, rendering, tags, forms and
signatures. qpdf 12.4.2 saved 5.7% and, on 12 of them, broke signatures, usage rights or PDF/A-1
conformance or lost an outline; on the 64 files where both kept quality, PdfNetStack's output was
8% smaller. Method, corpus and how to run it: [benchmarks](https://www.jadev-corp.com/pdfnetstack/benchmarks/).

On a private corpus of 1,296 real world PDFs (569 MiB), every optimized file was rendered with
PDFium and compared with its original: zero differing pixels, zero verification failures.

## Links

- [Documentation and source](https://github.com/JADEV-CORP/pdf.netstack)
- [Command line tool: PdfNetStack.Tool](https://www.nuget.org/packages/PdfNetStack.Tool)
- [Fidelity and verification method](https://github.com/JADEV-CORP/pdf.netstack/blob/main/docs/FIDELITY.md)
- [Changelog](https://github.com/JADEV-CORP/pdf.netstack/blob/main/CHANGELOG.md)
- [Report an issue](https://github.com/JADEV-CORP/pdf.netstack/issues)
