# PdfNetStack

Shrink PDFs on a server without breaking them, in pure C#. Forms, tags, signatures and PDF/A
survive, and every output is proved equivalent to its input before you get it. MIT licensed, so
it is also a home for code leaving PDFsharp now that 6.5.0 is AGPL-3.0. No native binaries, no
dependencies on .NET 8 and later.

[![CI](https://github.com/JADEV-CORP/pdf.netstack/actions/workflows/ci.yml/badge.svg)](https://github.com/JADEV-CORP/pdf.netstack/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PdfNetStack.svg)](https://www.nuget.org/packages/PdfNetStack)
[![NuGet downloads](https://img.shields.io/nuget/dt/PdfNetStack.svg)](https://www.nuget.org/packages/PdfNetStack)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Documentation, guides and benchmarks: **[jadev-corp.com/pdfnetstack](https://www.jadev-corp.com/pdfnetstack/)**.
Built and maintained by [JADEV](https://www.jadev-corp.com/en), software engineering.

Two jobs come first.

**Shrink PDFs on a server without breaking them.** The optimizer makes PDFs smaller without
changing a single rendered pixel. It rewrites the file with compressed object streams and a
compressed cross-reference stream, drops objects nothing points to and resources no page uses,
recompresses streams, rewrites content streams compactly, adds PNG prediction to raw images, and
merges duplicate fonts, images and resources, including separately embedded subsets of one font.
Then it reads its own output back and proves, object by object, that the document is the same as
the one you gave it. If anything is off, you get your original bytes back. Forms, structure tags,
bookmarks and attachments come through; signed and encrypted files are returned untouched; PDF/A-1
files keep the classic layout their standard requires.

**Give PDFsharp users an MIT home.** PDFsharp and MigraDoc 6.5.0, published on 14 October 2026,
carry the license AGPL-3.0-only; 6.2.4 and earlier stay MIT. PdfNetStack draws, edits, merges,
splits, fills forms and encrypts under MIT, with drawing calls that follow `XGraphics`. The
[migration guide](https://www.jadev-corp.com/pdfnetstack/guides/migrate-from-pdfsharp/) maps the
common calls, with before and after code.

It also extracts text and images, converts HTML and CSS to PDF, signs to PAdES, writes PDF/A and
makes scans searchable. See [Usage](#usage).

```csharp
using PdfNetStack;

var result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: saved {result.SavingPercent:F1}%");
```

## Purpose

PdfNetStack gives .NET developers a PDF toolkit that is permissively licensed all the way down
and honest about what it changes. Its first job is shrinking PDFs on servers without breaking
them; its second is an MIT path for teams that cannot take the AGPL that came with PDFsharp 6.5.0.
Around those, it optimizes, creates, edits, merges, splits and protects PDFs, extracts their text
and images, and converts HTML and CSS to PDF. For files from untrusted sources, the
[safety model](#safety-model) says what it refuses and what is bounded today.

PdfNetStack is a greenfield project. Its code is original and written from the published
specifications: ISO 32000 for PDF, OpenType, Unicode, and the HTML and CSS standards. See
[Acknowledgements](#acknowledgements) for the tools that influenced it.

It was created by Jamal Abdelkhalek at JADEV and is maintained by Jamal Abdelkhalek and Ilyass
Krichi; see [GOVERNANCE.md](GOVERNANCE.md).

## Compatibility

| Target | Runs on | Dependencies |
|---|---|---|
| `net8.0`, `net10.0` | .NET 8, 9, 10 and later | None |
| `netstandard2.0` | .NET Framework 4.6.2 to 4.8.1, Mono, Unity, Xamarin, .NET Core 2.0+ | `System.Memory` (Microsoft, MIT) |

CI runs the full test suite on .NET Framework 4.8, .NET 8 and .NET 10, on Windows, Linux and
macOS. The whole real world corpus also passes on .NET Framework 4.8. One difference: .NET
Standard's zlib has no level 9, so `CompressionEffort.Maximum` compresses a little less there.

## Serverless and containers

The libraries run in AWS Lambda, Azure Functions and containers without changes:

- **Native AOT and trimming.** `PdfNetStack`, `PdfNetStack.Html`, `PdfNetStack.Hyphenation` and `PdfNetStack.Ocr` publish
  with Native AOT and trimming on linux-x64 and linux-arm64 with no warnings, checked in CI.
- **Globalization.** Parsing and output do not depend on the culture. The test suite passes
  under invariant globalization (`InvariantGlobalization`) and under tr-TR.
- **File system.** Nothing is written except to the paths and streams you pass, and to temporary
  files in two cases you choose: a stream that cannot seek opened on demand (in
  `PdfLoadOptions.TempDirectory`), and the streaming optimizer. Both use the system temporary
  folder by default, `/tmp` on Lambda, and delete their files when closed. Installed fonts are
  only read, when `PdfFontCollection.System` is used; a missing font directory, an unreadable one
  or no home directory gives an empty collection.
- **Memory and streams.** `PdfLoadOptions` caps decoded streams and image pixels and opens files
  on demand, and from 1.3 its `Limits` cap what a whole document may cost; `OpenAsync`, `SaveAsync` and `HtmlConverter.ConvertToPdfAsync` never block on the
  caller's stream; an unoptimized save is written progressively; and
  `PdfOptimizerOptions.Streaming` optimizes files larger than memory. See
  [the large files guide](website/src/content/docs/guides/large-files.md).
- **Long lived instances.** Static caches are thread safe and bounded, and variable font
  instances are released once nothing uses them, so one warm instance can serve concurrent
  requests indefinitely.

Package the fonts your HTML needs and set `UseSystemFonts = false`. On a development machine,
the first HTML conversion in a new Native AOT process took about 16 ms, against about 770 ms
under the JIT. The [serverless samples](samples/serverless/README.md) have a complete Lambda
function and an Azure Functions app, with a SAM template, sizing advice and cost estimates.

## Why another PDF tool

Most PDF compression in the .NET world shells out to Ghostscript, depends on a copyleft or
commercial library, or re-distills the document and quietly loses forms, tags or metadata.
PdfNetStack takes a narrower job and does it with proof:

- **Lossless by construction.** Text, vectors, fonts, images, annotations, forms, bookmarks,
  structure tags, metadata and attachments are carried over. Nothing is rasterized.
- **Verified, not assumed.** Every rewrite is re-parsed and compared to the source, with stream
  content compared after decoding. A mismatch means the original is returned.
- **Fail safe.** Encrypted, signed and damaged files are returned byte for byte. The output is
  always a valid PDF: the verified rewrite or your input.
- **No native code, no processes.** One managed assembly for .NET 8 and .NET 10, trim and AOT
  compatible, same behaviour on Windows, Linux and macOS.
- **MIT all the way down.** On .NET 8 and later the core package has no dependencies; on .NET
  Standard 2.0 it uses Microsoft's `System.Memory`. CI blocks any runtime package that is not on a
  reviewed license allowlist.

### Why C# and not a native library

Optimization is mostly parsing, hashing and zlib. Modern .NET does all three at close to native
speed (`Span<T>`, SIMD-backed hashing, zlib-ng under `System.IO.Compression` on .NET 9+), and the
work parallelizes across streams. A Rust or C++ core would mean shipping native binaries per
platform and a marshalling layer, for little measured gain. If profiling ever shows a hot spot
worth it, the pipeline stages are isolated enough to swap one out.

## Install

```bash
dotnet add package PdfNetStack            # library
dotnet tool install -g PdfNetStack.Tool   # command line: pdfnetstack
dotnet add package PdfNetStack.Html       # HTML and CSS to PDF
dotnet add package PdfNetStack.Hyphenation   # hyphenation patterns for 29 language sets
dotnet tool install -g PdfNetStack.Ocr.Tool   # OCR from the command line: pdfnetstack-ocr
dotnet add package PdfNetStack.Ocr.Tesseract  # OCR with an installed Tesseract
```

The command line tool also comes as a single native executable, with no .NET install needed:
each [GitHub release](https://github.com/JADEV-CORP/pdf.netstack/releases) carries
`pdfnetstack-<version>-<platform>` for linux-x64, linux-arm64, win-x64, osx-x64 and osx-arm64,
compiled ahead of time, with their SHA-256 checksums in `SHA256SUMS.txt`.

## Usage

### Library

```csharp
// In memory
(byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(File.ReadAllBytes("in.pdf"));

// Streams
await using var input = File.OpenRead("in.pdf");
await using var output = File.Create("out.pdf");
PdfOptimizationResult r = await PdfOptimizer.OptimizeAsync(input, output);

// Options
var options = new PdfOptimizerOptions
{
    Compression = CompressionEffort.Balanced,   // faster, slightly larger
    AllowIncrementalUpdates = true,             // flatten edit history into one revision
    MaxDegreeOfParallelism = 2,                 // cap CPU and memory per document
    JpegQuality = 60,                           // opt-in, lossy: requantize JPEG images
    MaxImageDpi = 150,                          // opt-in, lossy: downsample oversampled images
    ConvertImagesToJpeg = true,                 // opt-in, lossy: photographs to JPEG at JpegQuality
    QuantizeImages = true,                      // opt-in, lossy: drawings and screenshots to a palette
    MinImageSsim = 0.95,                        // keep any image a lossy change would hurt
};

// Files larger than memory: read on demand, written through a temporary file, proven there
var streaming = new PdfOptimizerOptions { Streaming = true, MaxInputBytes = 8L << 30 };
await PdfOptimizer.OptimizeFileAsync("archive.pdf", "archive-small.pdf", streaming);
```

With `Streaming` the optimizer runs the same passes and the same proof without holding the file:
the source is read on demand, the rewrite goes progressively to a temporary file (the
destination's own for `OptimizeFileAsync`), and the proof reads it back on demand. Memory then
follows the number of objects and the streams being recompressed, a batch of at most 32 MiB of
source data at a time, not the size of the file. On a generated 278 MB file it peaked at about
50 MB of live managed memory (a 300 to 460 MB working set) against 810 MB (1.4 GB) in memory,
and wrote the same bytes. Linearization is not available in this mode.

`PdfOptimizationResult` tells you what was written and why: `Disposition`, sizes, SHA-256 of
input and output, page count, a human readable `Reason`, and per pass statistics and timings.

### Editing

```csharp
using PdfDocument doc = PdfDocument.Open("contract.pdf");

doc.Pages.RemoveAt(0);                         // drop the cover
doc.Pages.Move(2, 0);                          // reorder
doc.Pages[0].Rotate(90);                       // rotate clockwise
doc.Pages.Add(PdfRectangle.A4);                // blank page

using PdfDocument annex = PdfDocument.Open("annex.pdf");
doc.Pages.Import(annex.Pages);                 // fonts and images shared between pages are copied once

doc.Info.Title = "Signed contract";
doc.Outlines.Add("Annex", doc.Pages[^1]);      // bookmarks, read and written

doc.Save("contract-final.pdf");                // optimized and verified by default
await doc.SaveAsync(response.Body);            // asynchronous writes only, for response bodies
```

Merge and split. From 1.3 tagged PDFs stay tagged: the structure elements of the copied pages
come along, with `/MarkInfo` and the language.

```csharp
using PdfDocument a = PdfDocument.Open("a.pdf");
using PdfDocument b = PdfDocument.Open("b.pdf");
using PdfDocument merged = PdfDocument.Merge([a, b]);
merged.Save("merged.pdf");

merged.SplitTo(i => File.Create($"page-{i + 1}.pdf"));     // 1.3: one file per page, one at a time
using PdfDocument firstTwo = merged.ExtractPages(0, 2);     // 1.3: a run of pages

// 1.3: merge streams holding one input at a time, straight into a response
await PdfDocument.MergeAsync(uploads.Select(f => f.OpenReadStream()), response.Body, cancellationToken);
```

Large files can be read without loading them, and changes saved as an incremental update that
leaves the original bytes, and any signatures over them, untouched:

```csharp
using var stream = new FileStream("scan.pdf", FileMode.Open, FileAccess.ReadWrite);
using PdfDocument big = PdfDocument.OpenOnDemand(stream);  // reads the xref and page tree, nothing else
string text = big.Pages[41].ExtractText();                 // reads that page's content and fonts only
big.Info.Title = "Scanned archive";
big.SaveIncremental(stream);                               // appends the changed objects in place
```

`PdfLoadOptions` sets how a document is opened and what it may cost: the password, the decode
ceilings (`MaxDecodedStreamBytes`, `MaxImagePixels`), in memory or on demand, and where a stream
that cannot seek is copied. `OpenAsync` reads a stream with asynchronous reads only, for request
bodies and downloads in hosts such as AWS Lambda and Azure Functions:

```csharp
var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = "/tmp", MaxImagePixels = 50_000_000 };
using PdfDocument doc = await PdfDocument.OpenAsync(request.Body, options, cancellationToken);
for (int i = 0; i < doc.Pages.Count; i++)
{
    Index(doc.Pages[i].ExtractText());
    if (i % 50 == 49) doc.ReleaseUnusedObjects();  // frees what earlier pages parsed; edits are kept
}
```

A full save with `Optimize = false` writes progressively: each object, or batch of objects for
an object stream, goes to the output as soon as it is serialized, and the cross-reference data
comes last, so memory follows the largest object rather than the file. For a document opened on
demand, stream data is copied from the source as it is written. `SaveAsync` does the same with
asynchronous writes only, as ASP.NET Core and Azure Functions require of response bodies. With
the optimizer on, the default, the file is built in memory first, because the optimizer proves its
rewrite against all of it.

Named destinations, the anchors bookmarks and links point at by name, list, resolve and rename
in one call; every bookmark, link and GoTo action follows the new name:

```csharp
foreach (PdfOutline bookmark in doc.Outlines) Console.WriteLine($"{bookmark.Title}: {bookmark.DestinationName}");
doc.NamedDestinations.Rename(new Dictionary<string, string> { ["_OPENTOPIC_TOC_PROCESSING_d64e6438"] = "GUID-1AAE" });
```

Inherited page attributes (resources, media box, rotation) are materialized on each page, so
pages survive reordering and moving between documents. Annotations travel with their page and
are copied fresh for every import; links to pages imported in the same call are remapped, links
to pages left behind become empty. Form fields come along with their widgets and join the target
form; a top level field whose name is already taken there is renamed `name_2`, `name_3` and so
on, so merged copies of one form keep their own values. Editing a signed document invalidates
its signatures.

### Drawing

```csharp
using PdfNetStack.Drawing;

using PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())          // origin top left, y down, points
{
    var header = new PdfLinearGradientBrush(new PdfPoint(0, 0), new PdfPoint(595, 0),
        PdfColor.FromHex("#1E3A8A"), PdfColor.FromHex("#2563EB"));
    g.DrawRectangle(null, header, 0, 0, 595, 140);
    g.DrawRoundedRectangle(new PdfPen(PdfColor.FromHex("#CBD5E1")), PdfColor.White, 40, 170, 515, 220, 16);
    g.DrawEllipse(null, PdfColor.FromHex("#14B8A6").WithAlpha(0.5), 60, 430, 180, 180);

    PdfFont bold = PdfStandardFont.HelveticaBold;                 // using PdfNetStack.Fonts
    g.DrawString("Invoice 2026-114", bold, 28, PdfColor.White, 40, 80);   // x, baseline y
    double width = bold.MeasureString("Total: €1,250.00", 14);
    g.DrawString("Total: €1,250.00", bold, 14, PdfColor.Black, 555 - width, 360);
}
doc.Save("drawing.pdf");
```

Paths with lines, Bezier curves, arcs, ellipses, polygons and rounded rectangles (elliptical
corners sized like CSS `border-radius`); pens with caps, joins and dashes; gray, RGB and CMYK
colors with opacity; linear and radial gradients; transforms, saved states and clipping.
`CreateGraphics(PdfGraphicsLayer.Background)` draws underneath a page's existing content, for
letterheads and watermarks. Method names (`DrawLine`, `DrawString`, `MeasureString`) will feel
familiar to System.Drawing users.

`DrawInvisibleText(text, x, y, width, height)` writes text that is not drawn but selects, copies
and searches, stretched to fill its box: put the words any OCR engine found over a scanned page
and the page becomes searchable. Any script works, without a font to embed beyond a tiny built in
one, and the result passes PDF/A-2u.

The 14 standard PDF fonts (Helvetica, Times, Courier with their bold and italic styles,
Symbol, ZapfDingbats) need no embedding, so a page of text weighs about 2 KB. They cover Western
and Central European languages (Windows-1252 plus Ł, ő, č and the like).

Any TrueType or OpenType font works too, embedded as a subset of the glyphs you draw:

```csharp
using PdfNetStack.Fonts;

PdfFont inter = PdfOpenTypeFont.Load("fonts/Inter-Regular.ttf");          // .ttf .otf .ttc .woff .woff2
PdfFont? title = PdfFontCollection.System.Find("Segoe UI", weight: 600);   // installed fonts

var brand = new PdfFontCollection();
brand.AddDirectory("fonts");
PdfFont heading = (PdfFont?)brand.Find("Inter", weight: 700) ?? PdfStandardFont.HelveticaBold;
```

Embedded text is kerned from the font's GPOS or kern table, stays searchable and copyable in
any script the font covers (a ToUnicode map is written), and the subset keeps only the outlines
used, so a page in Inter costs a few KB. `PdfFontCollection` matches family, width, style and
weight the way CSS does. Text can be filled with a gradient, outlined with a pen, or both;
`MeasureString`, `Ascent`, `Descent` and `GetLineHeight` give the metrics for layout. Fonts are
parsed defensively: a damaged file raises `FontFormatException`, and a font whose license
forbids embedding (OS/2 fsType) is refused.

Variable fonts, with TrueType or CFF2 outlines, draw at any named instance or axis value. The
instance is embedded as a static font, with outlines, advances, metrics and kerning that match
fontTools and HarfBuzz:

```csharp
var variable = PdfOpenTypeFont.Load("fonts/Inter[opsz,wght].ttf");
PdfFont semiBold = variable.GetInstance("SemiBold");                        // a named instance
PdfFont custom = variable.GetInstance(new Dictionary<string, double> { ["wght"] = 650 });
```

In a `PdfFontCollection`, a variable font counts for every weight, width and slant its axes
cover, and its named instances are faces of the family. `Find` returns the instance CSS font
matching selects, with the optical size and any axis values applied, made once per location so a
document embeds one program for each:

```csharp
PdfFont caption = brand.Find("Inter", new PdfFontQuery
{
    Weight = 650,                       // inside the wght axis: exactly 650
    Style = PdfFontStyle.Italic,        // ital = 1, or the slnt axis for oblique
    OpticalSize = 9,                    // opsz, when the font has it
    Variations = new Dictionary<string, double> { ["GRAD"] = -25 },
})!;
```

Paragraphs wrap, align and justify, with rich text and right to left scripts:

```csharp
var body = new PdfTextStyle(inter, 11) { Fallbacks = [notoArabic] };
var p = new PdfParagraph { Alignment = PdfTextAlignment.Justify, LineHeight = 1.5 }
    .Add("Payment is due within ", body)
    .Add("30 days", body with { Font = interBold })
    .Add(". المبلغ المستحق: 1,250.00 €", body);
PdfTextLayout layout = g.DrawText(p, 50, 120, 300);   // layout.Height is the room it took
```

Text goes through the Unicode line breaking (UAX #14) and bidirectional (UAX #9) algorithms,
both passing the full Unicode conformance suites, and an OpenType shaper that matches HarfBuzz
glyph for glyph: ligatures, kerning, Arabic joining and vowel marks, the nine Indic scripts
(Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam), Thai and
Lao, Khmer, Myanmar, Hangul, and through the Universal Shaping Engine Sinhala, Tibetan, Balinese,
Javanese, Mongolian and the other complex scripts, and Unicode normalization against the font,
so decomposed and precomposed text both find their glyphs.
Lines break greedily at break opportunities, overlong words break between characters, soft
hyphens show a hyphen when used, and line height follows CSS. `PdfParagraph.Hyphenator`, off by
default, hyphenates by Liang's algorithm with the patterns of `PdfNetStack.Hyphenation`, and
`hyphens: auto` does the same in HTML. `PdfParagraph.Kashida` justifies
Arabic lines by elongating letters with tatweel before widening spaces. `PdfTextLayout` measures before drawing and can draw a range
of lines, for text that continues on the next page.

Images and reusable drawings:

```csharp
PdfImage photo = PdfImage.Load("photo.jpg");      // JPEG copied as is, EXIF orientation honored
PdfImage logo = PdfImage.Load("logo.png");        // PNG with alpha becomes a soft mask
g.DrawImage(logo, 40, 30, 120, 40);
g.DrawImage(photo, 40, 300, 200, 150, PdfImageFit.Cover, PdfImageAlignment.Top, opacity: 0.8);

PdfImage scan = PdfImage.Load("fax.tif", page: 2);  // any page of a multi-page TIFF
PdfImage web = PdfImage.Load("banner.webp");        // lossy or lossless, with alpha

PdfSvg vector = PdfSvg.Load("logo.svg");          // SVG stays vector: sharp at any zoom
g.DrawSvg(vector, 40, 80, 120, 40);

PdfForm page = PdfForm.FromPage(doc, other.Pages[0]);   // any page of any document
g.DrawForm(page, 0, 0, g.Width / 2, g.Height / 2);      // n-up, stamps, templates
```

PNGs of every color type and bit depth are supported, interlaced or not; simple ones keep their
compressed data, so embedding costs no recompression. GIF, BMP, TIFF and WebP images are decoded
once and Flate compressed, except TIFF pages stored as one strip of CCITT fax or JPEG data, which
are copied as is. `PdfImageFit` places an image in a box as CSS object-fit does: fill, contain,
cover (clipped to the box), none or scale down. ICC profiles and pixel density are kept.
SVG drawings keep their paths, gradients, clipping and text as PDF vectors; filters, masks and
patterns are not drawn. Each image, drawing and font is written once per document however often
it is drawn.

Annotations use the same top left coordinates and carry their own appearance, so every viewer
shows them alike:

```csharp
page.Annotations.AddLink(72, 700, 120, 14, new Uri("https://example.com"));
page.Annotations.AddLink(72, 720, 120, 14, doc.Pages[3], top: 200);
page.Annotations.AddTextMarkup(PdfTextMarkup.Highlight, 72, 100, 300, 16, comment: "Check");
page.Annotations.AddNote(520, 40, "Please review", author: "Finance");
g.AddLink(0, 0, 100, 20, new Uri("https://example.com"));   // follows the canvas transform

page.Annotations.AddStamp(PdfStampName.Approved, 400, 40, 150, 45);
page.Annotations.AddInk([[new(100, 300), new(140, 280), new(180, 310)]]);
page.Annotations.AddSquare(72, 400, 120, 60, new PdfAnnotationOptions { InteriorColor = PdfColor.FromHex("#FEF3C7"), Opacity = 0.8 });
page.Annotations.AddLine(new(72, 500), new(250, 480), new PdfAnnotationOptions { EndEnding = PdfLineEnding.ClosedArrow });
page.Annotations.AddFileAttachment(520, 80, "figures.csv", csv, "text/csv");
doc.FlattenAnnotations();                            // optional: appearances become page content
```

Stamps (the fourteen standard ones, or any form or drawing), ink, squares, circles, lines with
their endings, polygons, polylines and file attachments come with colors, border width and
dashes, opacity, author and comment. Flattening draws an annotation's appearance where a viewer
shows it (fitted by its bounding box and matrix, upright when it asks not to rotate) and removes
it, for one annotation, a page or the document. Hidden annotations stay; links and form widgets
stay unless `PdfFlattenOptions` asks for them.

Fill and flatten forms:

```csharp
using PdfDocument doc = PdfDocument.Open("application.pdf");
((PdfTextField)doc.Form["name"]!).Text = "Jane Doe";
((PdfCheckBoxField)doc.Form["subscribe"]!).Checked = true;
((PdfRadioButtonField)doc.Form["plan"]!).Selected = "pro";
((PdfChoiceField)doc.Form["country"]!).Select("Morocco");
doc.Form.Flatten();                                  // optional: values become page content
doc.Save("filled.pdf");
```

From 1.3, `Fill` sets many fields in one call. It checks every name and value first and, when
any is wrong, throws one `ArgumentException` listing every problem without changing a field.
`TryGetField` finds a field of the kind you expect without a cast:

```csharp
doc.Form.Fill(new Dictionary<string, string> { ["name"] = "Jane Doe", ["subscribe"] = "yes", ["plan"] = "pro" });
if (doc.Form.TryGetField("country", out PdfChoiceField? country)) country.Select("Morocco");
```

Every field kind is read (text, check box, radio group, combo and list box, push button,
signature) with fully qualified names, inherited attributes and widget pages. Setting a value
redraws the field's appearance from its border, background, font and color settings, with
multiline wrapping, comb cells, password masking and automatic font size, so filled forms look
the same in every viewer. A font the form embeds whole is reused; others map to the closest
standard font. Format actions calling the Acrobat functions `AFNumber_Format`,
`AFPercent_Format`, `AFDate_FormatEx`, `AFDate_Format`, `AFTime_Format`, `AFTime_FormatEx` and
`AFSpecial_Format` are read and applied in C#, so "1234.5" shows as "$1,234.50" without running
any JavaScript (`PdfTextField.FormattedText` gives the shown text).

Create forms too, placed from the page's top left as drawing is:

```csharp
PdfAcroForm form = doc.Form;
form.AddTextField(page, "name", 72, 100, 200, 20, options: new PdfFieldOptions { Required = true });
form.AddCheckBox(page, "subscribe", 72, 130, 14, 14);
form.AddRadioGroup("plan", [(page, 72, 160, 14, 14, "free"), (page, 120, 160, 14, 14, "pro")], "free");
form.AddComboBox(page, "country", 72, 190, 150, 20, ["Belgium", "France", "Morocco"]);
form.AddListBox(page, "tags", 72, 220, 150, 60, ["red", "green", "blue"], options: new PdfFieldOptions { MultiSelect = true });
form.AddSignatureField(page, "signature", 72, 300, 200, 50);
form.AddPushButton(page, "help", 72, 370, 80, 24, "Help", new Uri("https://example.com/help"));
```

Each new field gets its appearance, border, background, font (one of the standard fonts, which
viewers type with), alignment, length limit, comb cells, tooltip, read only and required flags.

Document settings: `doc.Viewer` (layout, panels, opening page, right to left reading),
`doc.PageLabels` (i, ii, iii then 1, 2, 3), `doc.Attachments` (embedded files, as e-invoices
use) and `doc.Language`.

PDF/A for archiving and e-invoices:

```csharp
doc.Attachments.Add("terms.pdf", terms, "application/pdf");
doc.Save("archive.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
```

Saving as PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e or 4f first checks what cannot be repaired (fonts
that are not embedded, DeviceCMYK, encryption, scripts and forbidden actions, attachments where
the part forbids them) and lists every problem at once, then adds an sRGB output intent (a
profile generated by the library, so nothing third party ships), XMP metadata matching the
document information, and the associated file entries PDF/A-3 and PDF/A-4 require. CI validates
sample files of every part with veraPDF.

- PDF/A-1b is written as PDF 1.4 with a classic cross-reference table and no object streams.
  Transparency, JPEG 2000, optional content, embedded files and ICC profiles newer than version
  2 are refused; subset CID fonts get the CIDSet stream PDF/A-1 asks for.
- PDF/A-4 is written as PDF 2.0. The title, author and other values go to the XMP metadata only,
  and the information dictionary in the file keeps just the modification date. Plain PDF/A-4
  and 4e embed only files that are themselves PDF/A-1, 2 or 4; 4f embeds files of any type and
  needs at least one; 4e also allows 3D and RichMedia annotations and their actions.
- Repairs made on the way: print flags on annotations, image interpolation switched off,
  NeedAppearances removed, LZW streams re-encoded with Flate.

Factur-X and ZUGFeRD invoices (PDF/A-3 with the invoice XML as an associated file):

```csharp
PdfInvoice.Attach(doc, File.ReadAllBytes("invoice.xml"), PdfInvoiceProfile.EN16931);
doc.Save("invoice.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
```

`Attach` embeds the XML as factur-x.xml (xrechnung.xml is also accepted for the XRECHNUNG
profile) with the relationship the profile calls for (Data for MINIMUM and BASIC WL, Alternative
otherwise), and saving writes the Factur-X XMP properties with their PDF/A extension schema. The
XML must be well formed; its business rules are not validated.

Digital signatures (PAdES baseline B-B, B-T, B-LT and B-LTA):

```csharp
using PdfNetStack.Signing;

X509Certificate2 cert = /* from a .pfx, the certificate store, or a smart card */;
var tsa = new PdfTimestampClient(request => PostToTimestampAuthority(request)); // your HTTP call
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), cert, new PdfSignatureOptions
{
    Reason = "Approved",
    Location = "Geneva",
    Appearance = new PdfSignatureAppearance(PageIndex: 0, X: 340, Y: 700, Width: 210, Height: 60),
    Timestamp = tsa,                                                      // B-T
});
signed = PdfSigner.AddValidationData(signed, myRevocationSource);         // B-LT: DSS with OCSP and CRLs
signed = PdfSigner.AddDocumentTimestamp(signed, tsa);                      // B-LTA

var options = new PdfValidationOptions { TrustAnchors = [rootCa] };
foreach (PdfSignatureInfo s in PdfSigner.Verify(signed, options))
    Console.WriteLine($"{s.FieldName}: intact {s.IsIntact}, certificates {s.Validation?.Status}");
```

Signing appends an incremental update, so earlier signatures stay valid, and writes a CAdES
detached CMS signature (SHA-256, 384 or 512; RSA or ECDSA keys) built by the library, with no
extra package. OpenSSL verifies the result. The library never opens a network connection: the
time stamp authority is reached through a delegate you supply (the library builds the RFC 3161
request and checks the reply's hash, nonce, signature and time stamping certificate), and OCSP
responses and CRLs come from an `IPdfRevocationSource` you implement (each request carries the
certificate's OCSP and CRL addresses and a ready OCSP request). `Verify` checks every
signature's byte range, digest and cryptographic validity and any timestamp. With
`PdfValidationOptions` it also builds each chain to your trust anchors and checks validity at the
timestamp's time (or now), key usage and revocation from the document security store or data you
supply, and reports each check as passed, failed, indeterminate or not checked.

The X.509 checks of ETSI EN 319 102-1 come with it. Certificate policies are processed as RFC
5280 describes, with policy mappings, policy constraints and inhibit anyPolicy (on the policy
graph of RFC 9618, so hostile mappings stay cheap): set `AcceptablePolicies` and the chain must
be valid for one of them, and `ValidPolicies` says which. Name constraints are enforced on DNS
names, email addresses, URI hosts, directory names and IP addresses. Each certificate's
QCStatements (ETSI EN 319 412-5) are read, and `Qualification` reports what the signer's
certificate claims, such as QESig or AdESeal-QC.

```csharp
var etsi = new PdfValidationOptions
{
    TrustAnchors = [rootCa],
    AcceptablePolicies = ["0.4.0.194112.1.2"],  // QCP-n-qscd
};
PdfCertificateValidation v = PdfSigner.Verify(signed, etsi)[0].Validation!;
Console.WriteLine($"{v.Status} {v.Policy} {v.Qualification}");
```

That claim is confirmed against EU trusted lists (ETSI TS 119 612) that you supply. The library
never downloads one: fetch the EU list of trusted lists and the national lists it points to
yourself, and pass the certificates the Official Journal publishes for the list of lists.

```csharp
PdfTrustedList lotl = PdfTrustedList.Load(lotlXml, ojCertificates);   // checks the XML signature
PdfTrustedList be = PdfTrustedList.Load(beXml, lotl);                  // signer taken from the LOTL pointer
var eu = new PdfValidationOptions { TrustedLists = [lotl, be] };
PdfSignatureInfo s = PdfSigner.Verify(signed, eu)[0];
PdfTrustedListQualification q = s.Validation!.TrustedListQualification!;
Console.WriteLine($"{q.Qualification} via {q.Service?.Name}");          // QESig, AdES or Indeterminate
foreach (string reason in q.Reasons) Console.WriteLine(reason);
Console.WriteLine(s.Timestamp?.QualifiedTimestamp);                    // Passed for a granted TSA/QTST
```

Each list's enveloped XML signature is verified (Exclusive and inclusive Canonical XML, RSA,
RSA-PSS and ECDSA with SHA-2) against the certificates given for it, and only lists that pass
and have not expired are used. Qualification follows ETSI TS 119 615: the CA/QC service whose
identity is a CA of the chain, its status from the history at the certificate's issuance and at
the signing time, the qualifiers its criteria select (QCWithQSCD, QCForESeal, NotQualified and
the rest) and the certificate's QCStatements. Services are matched by public key, never by the
subject key identifier a certificate carries. Conflicting qualifiers or services, criteria the
library cannot evaluate, missing lists and certificates whose own checks did not pass give
Indeterminate, with the reasons. CA services with an approved status at the validation time
also count as trust anchors (`TrustedListsAsTrustAnchors`), and TSA services for time stamp
authorities only; OCSP, CRL, withdrawn and never approved services anchor nothing. A timestamp
is qualified only when its authority validated and a granted TSA/QTST service names its key (or
a CA of its chain as that TSA/QTST service). A
manual check that reads and verifies the live lists is in `tools/PdfNetStack.TrustedListCheck`.

### Encryption

```csharp
using PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Security.Encrypt(userPassword: "read", ownerPassword: "admin",
    permissions: PdfPermissions.Print | PdfPermissions.CopyContent);   // AES-256 by default
doc.Save("report-protected.pdf");

using PdfDocument opened = PdfDocument.Open("report-protected.pdf", password: "admin");
opened.Pages.RemoveAt(0);
opened.Save("trimmed.pdf");          // keeps the original passwords and permissions
opened.Security.RemoveEncryption();  // or save it unprotected
```

Opening supports every standard security handler revision: RC4 40 to 128 bit, AES-128 and
AES-256 (revisions 2 to 6), user and owner passwords, crypt filters and unencrypted metadata.
New protection is written as AES-256 (revision 6) or AES-128 (revision 4). A document opened with
only the user password can be read but not saved, and pages can be copied out of it only when
its permissions allow copying.

### Extracting images

```csharp
using PdfDocument doc = PdfDocument.Open("scan.pdf");
foreach (PdfPageImage image in doc.Pages[0].GetImages())
{
    Console.WriteLine($"{image.Width}x{image.Height} at {image.Bounds}, {image.HorizontalResolution:0} dpi");
    if (image.Encoding == "DCTDecode") File.WriteAllBytes("photo.jpg", image.GetEncodedData());  // the JPEG as is
    else image.Decode()?.SavePng("image.png");                                                    // pixels
}
```

`GetImages` walks the page content: image XObjects, inside forms too, and inline images, each
with the matrix that places it. `Decode` gives 8 bit gray or RGB pixels for raw samples of 1 to 16
bits, JPEGs (decoded as libjpeg does), JPEG 2000 (JP2 files and raw codestreams; lossless bit
exact and lossy within one step of OpenJPEG), CCITT fax (Group 3 and 4, bit exact with libtiff)
and JBIG2, through DeviceGray, RGB and CMYK, Cal and ICC based
spaces, Indexed, Lab, Separation and DeviceN, with the Decode array and stencil masks applied. An
image's pixels must fit the document's decode limit (`MaxDecodedStreamBytes`, 256 MiB by
default) and its pixel limit (`PdfLoadOptions.MaxImagePixels`, 2^28 by default); a larger image
is refused with `PdfFormatException` before its pixels are allocated. JPEG 2000 is decoded a band of rows at a time straight into 8 bit pixels, so a 600 dpi
color page of 4812 by 7541 pixels takes about 190 MB at peak, its 109 MB of pixels included.

### Extracting text

```csharp
using PdfDocument doc = PdfDocument.Open("report.pdf");
Console.WriteLine(doc.Pages[0].ExtractText());               // lines top to bottom, words spaced
foreach (PdfWord word in doc.Pages[0].GetWords())
{
    Console.WriteLine($"{word.Text} at {word.Bounds}");       // points, from the bottom left
}

foreach (PdfLetter letter in doc.Pages[0].GetLetters())       // every glyph, in content order
{
    Console.WriteLine($"{letter.Text} {letter.FontName} {letter.FontSize:0.#} pt");
}
```

Text comes from the font's ToUnicode map, else from its encoding and glyph names (the Adobe Glyph
List, with the built-in encoding of an embedded Type 1 or CFF program when the font names none),
else for CJK fonts from Adobe's predefined CMaps and the CID to Unicode tables of the Japan1, GB1,
CNS1, Korea1 and KR collections, else from an embedded TrueType cmap; spans marked with ActualText
read as their replacement, so shaped and right to left text reads in logical order. Positions are in default
user space, before the page's `/Rotate`. Lines follow each run of text's own baseline, so rotated
text reads in order too, and vertical writing reads its columns from right to left.

### Making scans searchable (OCR)

```csharp
using PdfNetStack.Ocr;   // PdfNetStack.Ocr, models in PdfNetStack.Ocr.Models

using var engine = OnnxOcrEngine.Load(new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
using PdfDocument doc = PdfDocument.Open("scan.pdf");
PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["latin", "arabic"] }).MakeSearchable(doc);
doc.Save("scan-searchable.pdf");             // looks the same, now selects, copies and searches
Console.WriteLine(report.Text);
```

`PdfNetStack.Ocr` finds each page's scan, turns it upright from its placement and the page's
`/Rotate`, reads it with the PP-OCRv5 mobile models (PaddleOCR, Apache-2.0) on ONNX Runtime, and
lays every word over the scan with `DrawInvisibleText`. A scan stored as several images (strips or
tiles side by side, or a text mask over a background) is put back together in page space first,
one piece at a time. Latin and Arabic script (Arabic also covers Persian and Urdu), upside down
lines turned by a line orientation model, word boxes in page space, pages that already have text
left alone. A page scanned sideways or upside down, with nothing in the PDF saying so, is found
from the way its lines run and read turned back (`DetectOrientation`, on by default;
`PdfOcrPage.Orientation` says how it stood). Pages with no scan to read, such as text drawn as
vector outlines, can be rendered by the application through `PdfOcrOptions.Rasterizer`
(`IPdfPageRasterizer`, for example on top of PDFium); PdfNetStack itself does not render. Detection boxes, text decoding and right to left ordering are managed code: the native
runtime only ever sees float tensors, never PDF bytes. The models come in their own package,
`PdfNetStack.Ocr.Models` (about 21 MB), which copies them next to the application where
`OnnxOcrEngine.Load()` finds them; `Load("folder")` takes a folder of your own instead
(`tools/fetch_ocr_models.py` downloads them). Model files are checked against pinned SHA-256
hashes when loaded. `IOcrEngine` takes any other engine. From the command line,
`pdfnetstack-ocr scan.pdf searchable.pdf --languages latin,arabic` does the same; it comes in its
own package, `PdfNetStack.Ocr.Tool`, with the models and ONNX Runtime, so `PdfNetStack.Tool`
stays small, and `pdfnetstack ocr` runs it when both are installed. The packages are on NuGet.

For languages beyond Latin and Arabic script, or platforms ONNX Runtime has no build for,
`PdfNetStack.Ocr.Tesseract` adds `TesseractOcrEngine`, which runs a `tesseract` you installed, one
process per image, with no shell and no temporary files. Tesseract and its language data are not
in the package. It does not detect pages scanned sideways. The
[OCR guide](https://www.jadev-corp.com/pdfnetstack/guides/ocr/#choosing-an-engine) compares the two.

### HTML to PDF

```csharp
using PdfNetStack.Html;

byte[] pdf = HtmlConverter.ConvertToPdf(html);                              // a string
await HtmlConverter.ConvertToPdfAsync(html, response.Body, cancellationToken: ct); // straight to a response
using PdfDocument doc = HtmlConverter.ConvertFileToDocument("invoice.html");      // a file and its assets
```

A layout engine in C#, no browser: the CSS cascade, block, inline, table, flex and grid layout
(named lines, dense packing, subgrid), floats and positioning, paged media with `@page` and
named pages on any element of the block flow, running headers and footers
with page counters and named strings, repeated table headers, tagged output for accessibility, web fonts and variable fonts, right to left and Arabic text, links and bookmarks.
Without a resource loader nothing is fetched from the network. `ConvertToPdf` optimizes the file
without re-proving it (`HtmlConverterOptions.SaveOptions` changes that). On 20 public invoice and
report templates from wkhtmltopdf and DinkToPdf projects, compared page by page with Chrome, 10
render the same, 7 with small differences and 3 cut wide tables: content wider than the page is
not yet shrunk to fit. No JavaScript runs. From 1.3, `MaxStyleWork` and `MaxLayoutWork` bound a
conversion's work, `HtmlConverter.MaxConcurrentConversions` bounds how many run at once, and every
entry point takes a cancellation token. Details, options and limits in the
[package readme](eng/nuget/PdfNetStack.Html.md).

### Servers and files from users

From 1.3, `PdfLimits` bounds what a whole document may cost, and long calls take a
`CancellationToken`:

```csharp
app.MapPost("/text", async (HttpContext context) =>
{
    CancellationToken cancellationToken = context.RequestAborted;    // stops when the client leaves
    var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, Limits = PdfLimits.Untrusted };
    using PdfDocument doc = await PdfDocument.OpenAsync(context.Request.Body, options, cancellationToken);

    var text = new StringBuilder();
    foreach (PdfPage page in doc.Pages) text.AppendLine(page.ExtractText(cancellationToken));
    foreach (PdfDiagnostic warning in doc.Diagnostics) app.Logger.LogWarning("{Warning}", warning.Message);
    return Results.Text(text.ToString());
});
```

`PdfLimits.Untrusted` allows 256 MiB of input, 1 GiB decoded and 2^29 image pixels in total, 25
million content operators, 250,000 characters a page and 60 seconds a call. The default limits
scale with the file and stop only what no ordinary document needs. When a total runs out, a page
reads as far as it got and `doc.Diagnostics` says so; an image raises
`PdfLimitExceededException`; the optimizer (`PdfOptimizerOptions.Limits`) returns the input with
`OptimizationDisposition.LimitExceeded`. Budgets last as long as the document: for one kept open
and read many times, raise `MaxTotalDecodedBytes`. See
[Resource limits](https://www.jadev-corp.com/pdfnetstack/concepts/resource-limits/) and
[Cancellation and progress](https://www.jadev-corp.com/pdfnetstack/guides/cancellation-and-progress/).

### Command line

```bash
pdfnetstack optimize in.pdf out.pdf
pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./corpus                  # measure only, writes nothing
pdfnetstack optimize in.pdf out.pdf --jpeg-quality 60   # lossy, JPEG images only
pdfnetstack optimize in.pdf out.pdf --max-dpi 150 --to-jpeg --jpeg-quality 75 --min-ssim 0.95
pdfnetstack optimize in.pdf out.pdf --exhaustive        # archival: smaller Flate, much slower
pdfnetstack optimize in.pdf out.pdf --linearize         # fast web view
pdfnetstack optimize huge.pdf out.pdf --streaming       # bounded memory, any input size
pdfnetstack inspect in.pdf                  # where the bytes go
pdfnetstack verify original.pdf other.pdf   # check any tool's output for equivalence

pdfnetstack merge out.pdf a.pdf b.pdf c.pdf
pdfnetstack split in.pdf ./pages
pdfnetstack extract in.pdf out.pdf 1-3,7,9-  # page ranges, any order, repeats allowed
pdfnetstack rotate in.pdf out.pdf 90 2-4
pdfnetstack meta in.pdf out.pdf --title "Annual report" --author "Finance"

pdfnetstack encrypt in.pdf out.pdf --user-password u --owner-password o --allow print,copy
pdfnetstack decrypt in.pdf out.pdf --password o

pdfnetstack ocr scan.pdf searchable.pdf --languages latin,arabic   # runs pdfnetstack-ocr (PdfNetStack.Ocr.Tool)
```

## What it does

| Pass | Effect | Lossless |
|---|---|---|
| Garbage collection | Drops objects unreachable from the catalog, including old revisions | Yes |
| Object streams | Packs non-stream objects into Flate compressed object streams (PDF 1.5) | Yes |
| Xref stream | Replaces the text xref table with a predictor encoded, compressed stream | Yes |
| Stream recompression | Removes ASCIIHex/ASCII85 layers, turns LZW and RunLength into Flate, recompresses Flate at zlib level 9, keeps a result only when smaller | Yes |
| Image prediction | Tries PNG predictors on raw images of 8 bits or less per component | Yes |
| Bilevel images | `OptimizeBilevelImages`: 1 bit images stored as samples (raw, Flate, LZW, RunLength) or as CCITT Group 3 become CCITT Group 4 (ITU-T T.6) when smaller. Group 3 data is taken only when it decodes without a flaw, and the proof compares every pixel | Yes |
| JPEG re-encoding | Rebuilds each JPEG's Huffman tables for that image, writes progressive and restart-marked JPEGs as plain baseline unless progressive coding is smaller, drops segments PDF readers ignore (Exif, XMP, embedded ICC, comments). Works on the DCT coefficients, which stay identical | Yes |
| Deduplication | Merges identical fonts, images, ICC profiles, resource dictionaries and arrays, streams compared by what they decode to, iterating until merges stop | Yes |
| Unused resources | Drops fonts, images, forms, graphics states, patterns, shadings, color spaces and property lists that no content using the resource dictionary names | Yes |
| Content streams | Rewrites page, form, pattern and appearance content with minimal white space, canonical numbers and no comments; the same operators and operands | Yes |
| Font subsets | Merges separately embedded subsets of one TrueType font (identity mapped composite fonts) into one program, when no glyph any page shows changes | Yes |
| Linearization | `Linearize`: first page first, two cross-reference sections and a hint stream (Annex F), for fast web view | Yes, opt-in |
| Compact syntax | Minimal separators, canonical numbers, null entries dropped | Yes |
| Thumbnails, PieceInfo | Optional removal of page thumbnails and application private data | No, opt-in |
| JPEG requantization | `JpegQuality`: coarser quantization at a libjpeg quality, never finer than the image's own; soft masks stay lossless | No, opt-in |
| Downsampling | `MaxImageDpi`: images drawn above 1.5 times the target resolution, measured at their largest placement, are resampled with a Lanczos filter; JPEG 2000 becomes JPEG at `JpegQuality` (or lossless samples without it). Bilevel images, raw, CCITT or JBIG2, stay bilevel: a box filter and a threshold that keeps strokes thinner than the new pixels, coded as Group 4 or Flate. Masks, indexed and color keyed images are left alone | No, opt-in |
| Photographs to JPEG | `ConvertImagesToJpeg`: raw gray and RGB images and gray and RGB JPEG 2000 images that look photographic become baseline JPEG at `JpegQuality`; line art, screenshots and palette images stay lossless | No, opt-in |
| Quality gate | `MinImageSsim`: any lossy image change below this luminance SSIM is dropped and the image kept as it was | Opt-in |
| Progressive JPEG | `ProgressiveJpeg`: every JPEG written is also tried progressive, kept when smaller | Yes, on by default from 1.3 |
| Exhaustive Flate | `Compression = Exhaustive`: optimal parsing and block splitting in the manner of Zopfli, zlib kept when smaller | Yes, opt-in |

Without the lossy image options, JPEG data is decoded to DCT coefficients and never to pixels, so
components, chroma sampling and color model pass through untouched: a CMYK image stays CMYK.
JPEG 2000 and JBIG2 data is then never decoded or touched, and CCITT data only by the Group 4 pass.

## Safety model

The optimizer refuses to rewrite, and returns the input unchanged, when the file is:

- encrypted,
- digitally signed or has usage rights (any `/ByteRange`, signature dictionary, `/Perms` or `/DSS`);
  from 1.3, `RemoveUsageRights` rewrites a form whose only signature is usage rights, removing them,
- incrementally updated, unless `AllowIncrementalUpdates` is set,
- damaged: xref reconstruction, stream length repair, unreadable objects, or references whose
  generation numbers disagree with the xref, unless `AllowRepairedFiles` is set,
- larger than `MaxInputBytes`,
- from 1.3, too costly for `PdfOptimizerOptions.Limits` (disposition `LimitExceeded`).

PDF/A-1 files are detected from their XMP metadata and written with a classic xref table and
no object streams, because PDF/A-1 predates both.

Hostile input meets two kinds of limits. Per object: nesting depth is capped, each decoded stream
is capped (`MaxDecodedStreamBytes`, default 256 MiB) as a zip bomb defence, each image's pixels are
capped before its buffer is allocated, `/Prev` loops terminate, wrong stream lengths are recovered
from `endstream`, and unreadable objects never crash the run. Per document, from 1.3:
`PdfOptimizerOptions.Limits` caps the input, the bytes and image pixels decoded in total, the
content operators and the time, and a run that reaches one returns the input with
`LimitExceeded`. Use `PdfLimits.Untrusted` for files from users of a service, and run the work in
a process or container with a memory limit as well.

The verifier walks the source and output graphs in lockstep from the trailer. Dictionaries
must have the same keys and values, arrays the same items, references must line up, and every
stream must decode to identical bytes with identical remaining filters. JPEG images are
compared by DCT coefficient, so a re-encoded image passes only if every coefficient is the
same. Encoding keys (`/Length`, `/Filter`, `/DecodeParms`) are the only other differences it
accepts, besides the structural passes, each checked against its own analysis of the source: a
resource entry may be missing only where no content can name it, a content stream may differ
only in layout and must read as the same tokens, and a merged font program must keep every glyph
the document shows byte for byte. With `JpegQuality` set, a JPEG image may also differ by
exactly the requantization the optimizer applies, checked coefficient by coefficient, and by
nothing else. A bilevel image coded again as Group 4 is decoded on both sides and every pixel
must match. A downsampled or converted image must keep its components and aspect, may not grow,
and is decoded and compared with the source resampled to its new size: mean color, a coarse
thumbnail and SSIM, at least `MinImageSsim` when set.
See [docs/FIDELITY.md](docs/FIDELITY.md) for the full method and its limits.

## Results

### Public corpus, against qpdf and Ghostscript

77 openly licensed and public domain PDFs that anyone can download: NIST and NASA publications,
arXiv papers, government gazettes and test files from other PDF projects, listed in
[eng/corpus/manifest.json](eng/corpus/manifest.json). [tools/compare_optimizers.py](tools/compare_optimizers.py)
runs PdfNetStack with the library defaults next to the other tools and scores every output on
size, on rendering in PDFium and on what it kept: tags, form fields, outlines, attachments,
signatures, usage rights and PDF/A conformance. CI runs it every week and for every release,
with MuPDF too. Measured on 18 November 2026 with qpdf 12.4.2 and Ghostscript 10.05, on the 76
files PDFium can open:

| Tool | Saved | Outputs that keep quality |
|---|---:|---:|
| PdfNetStack, lossless (defaults) | 11.2% | 76 of 76 |
| qpdf, lossless | 5.7% | 64 of 76 |
| PdfNetStack, `Balanced` preset | 21.8% | 76 of 76 |
| PdfNetStack, `Smallest` preset | 33.3% | 73 of 76 |
| Ghostscript, `/ebook` | 32.1% | 23 of 76 |

qpdf's 12 outputs below the bar broke the signatures of 6 files and the usage rights of 2
forms, wrote object streams into 3 PDF/A-1b files (PDF/A-1 is based on PDF 1.4, which has
none) and lost the outline of a damaged file. On the 64 files where both keep quality,
PdfNetStack's output is 8% smaller in total (154.1 MiB against 167.6 MiB). qpdf is smaller on
18 of them: 15 that PdfNetStack keeps as they are by default (7 with incremental updates, 2
encrypted, 1 that needed repair, 5 where the rewrite saved less than 1%) and 3 real gaps. With
`AllowIncrementalUpdates` the 7 files go from 7.2 MiB to 5.5 MiB. Ghostscript's output falls
below the bar on 53 files: changed text or page geometry, lost form fields, tags, annotations
or signatures.

### Private corpus

Measured for version 1.0 on a mixed real world corpus of 1,296 files (569 MiB) that is not
public: invoices, reports, manuals, scanned documents, presentations and a 631 page spare parts
catalogue, on an 8 core Windows machine with default options.

| | |
|---|---|
| Optimized | 1,003 files, total size 568.9 MiB to 469.0 MiB (17.6% saved) |
| Returned unchanged | 38 below the 1% threshold, 130 refused by the safety rules, 125 not PDFs (empty or resource fork files) |
| Verification failures | 0 |
| Throughput | about 65 s for the whole corpus, 8 to 9 MB/s, lossless JPEG re-encoding included |
| With `AllowIncrementalUpdates` and `AllowRepairedFiles` | 1,118 files optimized, 20.1% saved |

Independent render check: every optimized file was rendered with PDFium at 150 DPI and
compared with the original. 1,118 documents, 2,161 pages, zero differing pixels, identical
extracted text. The script is in [tools/render_compare.py](tools/render_compare.py).

The 631 page catalogue, 72.6 MiB of CAD drawings already Flate compressed: 6,075 duplicate
objects merged, 3.1% saved, about 5 seconds with all cores, peak working set about 690 MB.
Image heavy files gain the most, often 20 to 40%, mostly from PNG prediction on raw images.

Editing path: every document in the corpus was rebuilt by importing all of its pages into a new
document and saving it. 1,152 documents, 2,233 pages, zero differing pixels against the
originals at 150 DPI.

Encryption: AES-256 and AES-128 files written by PdfNetStack open in PDFium with either password
and render identically to their sources; real AES-128 contracts from other producers decrypt to
pixel identical output; RC4 files written for the legacy read path render identically in PDFium.

Your mileage depends on what produced the PDF. Files from well tuned producers that already use
object streams have little left to gain losslessly.

## Options

| Option | Default | Notes |
|---|---|---|
| `MinSavingPercent` | 1.0 | Keep the original unless the rewrite saves at least this much |
| `Compression` | `Maximum` | `Balanced` cut corpus time by about a third for about 2% larger output; `Exhaustive` saved 3 to 5% more on synthetic files, for 2 to 150 times the time |
| `UseObjectStreams` | true | Off writes a classic xref table |
| `ObjectsPerStream` | 200 | Larger compresses better, smaller loads single pages faster |
| `DeduplicateObjects` | true | |
| `PruneUnusedResources` | true | Resource entries no content names |
| `CompactContentStreams` | true | Same tokens, fewer bytes |
| `MergeFontSubsets` | true | Needs `DeduplicateObjects` |
| `Linearize` | false | Fast web view. No object streams, so often larger; kept anyway for input not yet linearized |
| `RecompressStreams` | true | |
| `UseImagePredictors` | true | |
| `OptimizeJpegImages` | true | Lossless JPEG re-encoding |
| `OptimizeBilevelImages` | true | 1 bit images to CCITT Group 4 when smaller, the same pixels |
| `JpegQuality` | null | 1 to 100, libjpeg scale. Lossy, JPEG images only |
| `QuantizeImages` | false | Raw RGB images to a palette when that stays faithful. Lossy, for drawings and screenshots |
| `MaxImageDpi` | null | Downsample images drawn above 1.5 times this resolution, bilevel, CCITT, JBIG2 and JPEG 2000 included. Lossy |
| `MaxBilevelImageDpi` | null | The resolution for bilevel images in place of `MaxImageDpi`; null uses `MaxImageDpi`. Lossy |
| `ConvertImagesToJpeg` | false | Photographic raw and JPEG 2000 images to JPEG at `JpegQuality`. Lossy |
| `MinImageSsim` | null | 0 to 1; around 0.95 keeps changes hard to see |
| `ProgressiveJpeg` | true | Progressive JPEG when smaller. Lossless. Off by default before 1.3 |
| `Verify` | true | Disable only for inputs you already trust |
| `AllowIncrementalUpdates` | false | Flattens edit history into one revision |
| `AllowRepairedFiles` | false | Rewrites damaged files into clean ones |
| `RemovePageThumbnails` | false | Not lossless |
| `RemovePieceInfo` | false | Not lossless for the authoring app, invisible to viewers |
| `RemoveUsageRights` | false | 1.3. Rewrites forms whose only signature is Adobe Reader usage rights, removing them |
| `Streaming` | false | Read on demand, write through a temporary file, prove it there: memory bounded by objects and batches, not the file. Not with `Linearize` |
| `MaxInputBytes` | 1 GiB | Raise it with `Streaming` for larger files |
| `MaxDecodedStreamBytes` | 256 MiB | Per stream decode ceiling |
| `MaxDegreeOfParallelism` | CPU count | From 1.3 also capped by the memory the runtime reports. Lower it to cap memory on image heavy files |
| `Limits` | `PdfLimits.Default` | 1.3. What one run may cost in total; `PdfLimits.Untrusted` for files from users |

## Feature status

Every feature ships with the same test and corpus discipline as the optimizer. What comes next is
in the [roadmap](docs/ROADMAP.md).

| Area | Scope | Status |
|---|---|---|
| Editing | Open, create, merge, split, import, reorder, rotate and remove pages; document info; bookmarks | 1.0 |
| Encryption | Open RC4, AES-128 and AES-256 files; write AES-128/256 with permissions | 1.0 |
| Object model | Public `PdfDictionary`, `PdfArray`, `PdfStream` and friends for anything the API lacks | 1.1 |
| Drawing | Canvas on new and existing pages: paths, colors, transforms, clipping, gradients | 1.1 |
| Fonts | Standard 14 fonts, TrueType/OpenType/WOFF/WOFF2 embedding with subsetting, kerning, font collections, variable font instances (TrueType and CFF2) | 1.1 |
| Text layout | Line breaking, alignment, justification (with kashida), rich text, Arabic, Indic, Southeast Asian and other complex scripts, RTL, Unicode normalization | 1.1 |
| Images and overlays | JPEG, PNG, GIF, BMP, TIFF, WebP and SVG placement with fit and opacity, pages of other PDFs as watermarks, letterheads, n-up | 1.1 |
| Annotations | Links, notes, text markup, free text, stamps, ink, shapes, lines, polygons, file attachments; flattening | 1.1 |
| Forms (AcroForm) | Creating, reading, filling with generated and formatted appearances, flattening, fields kept through import and merge | 1.1 |
| Signatures and PDF/A | PAdES B-B, B-T, B-LT and B-LTA signing and verification with chain, revocation, policy and name constraint checks and qualified status, PDF/A-1b, 2b, 2u, 3b, 3u, 4, 4e and 4f output, Factur-X and ZUGFeRD | 1.1 |
| Hyphenation | Liang's algorithm in the core, opt in; `PdfNetStack.Hyphenation` with permissively licensed patterns for English (US, UK), German, French, Dutch, Spanish, Italian, Portuguese and 21 more; `hyphens: auto` in HTML | 1.2 |
| HTML and CSS | `PdfNetStack.Html`: CSS cascade, block, inline, table, flex and grid layout, floats, positioning, paged media, web fonts | 1.1 |
| Image extraction | Images per page with placement and resolution; JPEG and raw samples to pixels; PNG export | 1.1 |
| Text extraction | Page text in reading order; letters and words with bounds, baselines, fonts and sizes | 1.1 |
| OCR | `PdfNetStack.Ocr` add-on and `PdfNetStack.Ocr.Models`: searchable scans, Latin and Arabic, scans in one image or several, pages scanned sideways, an application supplied rasterizer; the `pdfnetstack-ocr` tool | 1.1 |
| OCR with Tesseract | `PdfNetStack.Ocr.Tesseract`: an engine that runs an installed `tesseract`, any language it has data for | 1.2 |
| Large files and serverless | Documents opened on demand from files and streams that cannot seek, image and decode limits, progressive and asynchronous saving, a streaming optimizer for files larger than memory; Native AOT on linux-x64 and linux-arm64; AWS Lambda and Azure Functions samples | 1.1 |
| Resource limits | `PdfLimits` for a whole document, with an `Untrusted` preset; `PdfDocument.Diagnostics`; `PdfLimitExceededException`; `LimitExceeded` in the optimizer | 1.3, not yet on NuGet |
| Servers | Cancellation tokens and progress on long calls, `MergeAsync` one input at a time, `SplitTo`, `ExtractPages`; tagged PDFs stay tagged through merge and split; `Form.Fill` and `TryGetField` | 1.3, not yet on NuGet |

### Low level access

```csharp
using PdfNetStack.Objects;

using PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Catalog.Set("PageMode", new PdfName("UseOutlines"));        // open with bookmarks shown
var content = PdfStream.FromData("0 0 1 rg 72 72 144 144 re f"u8);
doc.Pages[0].Dictionary.Set("Contents", doc.AddObject(content));  // replace page content
```

## Limits

- Lossy image options handle 8 bit gray, RGB and CMYK images, raw or JPEG; gray and RGB JPEG
  2000 images their dictionary fully describes; and bilevel images, raw, CCITT or JBIG2. 16 bit,
  indexed, Lab, Separation and DeviceN images keep their resolution, masks and soft masks (the
  JBIG2 layer of a book scan is usually one) are never changed, and images inside Type 3 glyphs
  are never downsampled. The quality gate measures luminance only. On the public benchmark
  corpus (`eng/corpus/manifest.json`, see docs/FIDELITY.md), in the 1.1.0 release run, the lossy
  options took the saving from 12.1% to 29.5%, and on its nine scanned documents from 4.1% to
  24.0%: 37% on a 309 page JBIG2 report and 42% to 88% on CCITT pages. Book scans that split
  each page into a JPEG 2000 picture over a JBIG2 mask gain about 2%: the pictures are already
  smaller than a JPEG of them, and decoding them costs about a second each.
- Arithmetic coded, 12 bit, lossless and hierarchical JPEGs, JPEGs with more than 64 scans, and
  damaged JPEG data, are carried over untouched.
- The optimizer does not subset fonts; it merges subsets of one font only when they keep the
  original glyph ids.
- Image extraction decodes raw samples, JPEGs, JPEG 2000, CCITT fax and JBIG2. Soft masks
  (including JPEG 2000 SMaskInData alpha) are not applied, JPEG 2000 ICC profiles count only for
  their number of components, HTJ2K (JPEG 2000 part 15) is not supported, and Separation and
  DeviceN colors other than a Type 2 tint transform are approximated as gray.
- Text extraction reads columns of prose one after the other (gutters of an em or more between
  blocks of at least three lines of a dozen characters); narrower or shorter side by side text,
  such as tables, reads row by row, which is what tables want. Predefined CMaps outside Adobe's
  five CJK collections (such as the deprecated Hojo ones) give no CIDs, so their fonts use default
  widths. Text without a Unicode mapping reads as U+FFFD. At most 500,000 letters are read per
  page.
- OCR reads pages whose scan covers at least half the page and is decodable: one image, upright
  or turned by quarter turns, or several images each upright or turned by quarter turns (strips,
  tiles, a text mask over a background). Other pages, vector art among them, are read only
  through a rasterizer the application supplies. ONNX
  Runtime's official builds send telemetry to Microsoft by default: the engine turns it off
  before loading a model, and on Linux and macOS `ORT_DISABLE_TELEMETRY=1` must also be set
  before the process starts for the runtime to send nothing at all. On .NET Framework the app must
  run 64 bit and reference `Microsoft.ML.OnnxRuntime` itself, so its native runtime is copied next
  to the app.
- Linearization is opt-in (`Linearize`); without it, the linearization of an input is dropped.
- In memory, the default, the optimizer holds the whole document, and each worker about three
  times the largest decoded stream it works on: plan for about `workers x 3 x largest decoded
  stream` on top of the document, from the decoded size of the images, not the file size. From
  1.3 the default worker count follows the memory the runtime reports, a container's limit
  included, and large decode buffers no longer stay in the shared pool after a call (an image
  heavy run held 1.3 GB after returning, now 25 MB). Measured on 1.2.0, the peak working set was
  24 to 60 times the input; 1.3 lowers it by 8 to 20% on image heavy files. Lower
  `MaxDegreeOfParallelism` to cut the peak, and see
  [Memory and performance](https://www.jadev-corp.com/pdfnetstack/concepts/memory-and-performance/)
  for container sizing and GC settings. `Streaming`
  keeps the file itself out of memory, at the cost of a temporary file the size of the output and
  more reads of the source. Its output can differ slightly from the in-memory rewrite, because
  duplicates are found before recompression: two JPEG files that differ only in metadata stay two.
- A full save with the optimizer, the default, builds the file in memory; with `Optimize = false`
  it is written progressively. To read or edit a large file without loading it, open
  it with `PdfLoadMode.OnDemand`, then use `SaveIncremental` or a progressive save.
- A document read on demand keeps every object it parses (about 25 KB a page for pages with their
  own fonts) until `ReleaseUnusedObjects` frees those nothing uses, and decoded object streams up
  to `MaxDecodedStreamBytes`. In memory, a document holds the whole file, and a stream that cannot
  seek is read into pieces joined once, so about twice its size at peak.
- Encrypted output draws a random initialization vector for every string and stream, so two
  saves of an encrypted document never match byte for byte; neither do two PDF/A saves, whose
  metadata records a new instance id and the time of the save.

## Documentation

- [Documentation site](https://www.jadev-corp.com/pdfnetstack): guides, recipes and the API reference
- [Migrating from PDFsharp 6](https://www.jadev-corp.com/pdfnetstack/guides/migrate-from-pdfsharp/): common calls mapped, with before and after code
- [Resource limits](https://www.jadev-corp.com/pdfnetstack/concepts/resource-limits/) and [cancellation](https://www.jadev-corp.com/pdfnetstack/guides/cancellation-and-progress/): running on files from users
- [Architecture](docs/ARCHITECTURE.md)
- [Fidelity and verification](docs/FIDELITY.md)
- [Roadmap](docs/ROADMAP.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)
- [Governance](GOVERNANCE.md): maintainers, decisions and releases

## Acknowledgements

PdfNetStack is original work. Over years of building software, its author has used many PDF and
document tools, and their strengths shaped the feature set and the API: PdfSharp and MigraDoc,
PDFTron (now Apryse), iText, QuestPDF, wkhtmltopdf, WeasyPrint, PDFium, pdf.js, Poppler and qpdf.
HarfBuzz, libjpeg, libtiff and PDFium also serve as reference implementations in the test suite,
where outputs are compared.

No source code from any of these projects is included. The few third party data tables the
library does use, such as the Unicode character data and the Adobe Glyph List, are listed with
their licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Product names are trademarks
of their respective owners and are mentioned for reference only, without affiliation or
endorsement.

## License

MIT. See [LICENSE](LICENSE). Runtime dependency licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
