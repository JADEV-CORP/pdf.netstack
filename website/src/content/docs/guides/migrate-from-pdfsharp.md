---
title: Migrate from PDFsharp 6 to PdfNetStack
description: Move .NET code from PDFsharp 6.x to the MIT licensed PdfNetStack, with a table of common calls, before and after code, and what has no equivalent.
since: '1.1'
sidebar:
  label: Migrate from PDFsharp
faq:
  - q: Is PDFsharp still MIT licensed?
    a: PDFsharp 6.2.4 and every 6.x release before it are listed on NuGet under MIT, and a published version keeps its license. PDFsharp 6.5.0, published on 14 October 2026, and 7.0.0-preview-2 carry the license expression AGPL-3.0-only, and the publisher offers a commercial license as the alternative.
  - q: Can PDFsharp and PdfNetStack be used in the same project?
    a: Yes. Both are managed .NET libraries without native code. Some type names exist in both, such as PdfDocument and PdfPage, so give one of them an alias while both are referenced.
  - q: Does PdfNetStack have an equivalent of MigraDoc?
    a: No. PdfNetStack has no document object model with automatic page breaks. For documents laid out from content, PdfNetStack.Html converts HTML and CSS to PDF with paged media, tables with repeated headers, and running headers and footers.
---

This guide moves code written for PDFsharp 6.x to PdfNetStack. It maps the calls most PDFsharp
code makes, shows each change as before and after code, and says plainly what PdfNetStack does
not do. The PdfNetStack code on this page is compiled and run in CI. The PDFsharp code was
compiled against PDFsharp 6.2.4.

## Short answer

Most drawing code moves line by line: `XGraphics` and `PdfGraphics` share the coordinate system
and most method names. Opening, fonts, forms and encryption change shape, as the
[table](#common-calls) shows. MigraDoc has no equivalent.

```bash
dotnet add package PdfNetStack
```

## The license change

PDFsharp and MigraDoc were published under MIT up to 6.2.4. From 6.5.0 they are published under a
dual license, the AGPL or a commercial license from the publisher
([license](https://docs.pdfsharp.net/General/License/License.html)). The NuGet registration shows
the change:

| Version | Published on NuGet | License expression |
|---|---|---|
| [6.2.4](https://www.nuget.org/packages/PDFsharp/6.2.4) | 6 January 2026 | MIT |
| [7.0.0-preview-1](https://www.nuget.org/packages/PDFsharp/7.0.0-preview-1) | 24 March 2026 | MIT |
| [6.5.0](https://www.nuget.org/packages/PDFsharp/6.5.0) | 14 October 2026 | AGPL-3.0-only |
| [7.0.0-preview-2](https://www.nuget.org/packages/PDFsharp/7.0.0-preview-2) | 14 October 2026 | AGPL-3.0-only |

`PDFsharp-MigraDoc` follows the same versions and licenses. A published version keeps its
license, so staying on 6.2.4 under MIT is a valid choice. This guide is for teams that want to
keep an MIT license and move on from 6.2.4, without adopting the AGPL or buying a license.

Read each license yourself, or ask your counsel, before you decide. This page is not legal
advice. Checked against the NuGet registration on 18 November 2026.

## What carries over

- **Coordinates.** Both libraries draw in points from the top left of the page, y down. In both,
  `DrawString` takes the baseline as its y coordinate (PDFsharp's default string format aligns
  left on the baseline). Positions carry over unchanged.
- **Method names.** `DrawLine`, `DrawRectangle`, `DrawEllipse`, `DrawArc`, `DrawPie`,
  `DrawPolygon`, `DrawBezier`, `DrawPath`, `DrawImage`, `TranslateTransform`, `RotateTransform`
  and `ScaleTransform` exist on `PdfGraphics` with the same argument order, taking PdfNetStack's
  pen, brush, point and path types. Positive angles turn clockwise in both.
- **Document properties.** `doc.Info.Title` and the other information fields, and `doc.Outlines`
  for bookmarks, keep their names.

## What changes

- **No open modes.** `PdfReader.Open(path, mode)` becomes `PdfDocument.Open(path)`. Every document
  can be edited, saved and used as a source of pages. For large files, `PdfLoadMode.OnDemand`
  reads objects only when they are used, see [Large files](/guides/large-files/).
- **Fonts.** There is no font resolver to register. Load a font file, find an installed font, or
  use one of the 14 standard fonts. A `PdfFont` has no size: the size goes with each call.
- **Saving.** `Save` runs the optimizer by default and proves the result equivalent to what you
  built. `new PdfSaveOptions { Optimize = false }` writes without it, faster and progressively.
- **Forms.** Setting a field value draws its appearance at once, so `NeedAppearances` is not
  needed.
- **Encryption.** One call encrypts, with AES-256 by default. RC4 files open but are never
  written.
- **Type names.** `PdfDocument`, `PdfPage`, `PdfRectangle`, `PdfTextField` and a few others exist
  in both libraries. See [running both](#running-both-during-the-move).

## Common calls

| PDFsharp 6.x | PdfNetStack | Notes |
|---|---|---|
| `PdfReader.Open(path, PdfDocumentOpenMode.Modify)`, or with a password | `PdfDocument.Open(path)`, `PdfDocument.Open(path, password)` | User or owner password |
| `new PdfDocument()` | `PdfDocument.Create()` | |
| `doc.AddPage()`, `page.Size = PageSize.A4`, `doc.PageCount` | `doc.Pages.Add(PdfRectangle.A4)`, `doc.Pages.Count` | `PdfRectangle.FromSize(width, height)` in points for other sizes |
| `doc.Pages.RemoveAt(i)`, `doc.InsertPage(i)` | `doc.Pages.RemoveAt(i)`, `doc.Pages.Insert(i, PdfRectangle.A4)` | `doc.Pages.Move(from, to)` reorders |
| `page.Rotate = 90` | `page.Rotation = 90` | `page.Rotate(90)` adds 90 to the current rotation |
| `doc.Save(path)`, `doc.Save(stream)` | `doc.Save(path)`, `doc.Save(stream)`, `await doc.SaveAsync(stream)` | Optimized and verified by default |
| `PdfReader.Open(path, PdfDocumentOpenMode.Import)`, then `output.AddPage(page)` | `output.Pages.Import(source.Pages)`, `PdfDocument.Merge(documents)`, `doc.Split()` | Fonts and images shared by pages are copied once |
| `XGraphics.FromPdfPage(page)` | `page.CreateGraphics()` | Dispose it, as with `XGraphics` |
| `XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend)` | `page.CreateGraphics(PdfGraphicsLayer.Background)` | Draws underneath the existing content |
| `new XFont("Arial", 12)` and `GlobalFontSettings.FontResolver` | `PdfOpenTypeFont.Load(path)`, `PdfFontCollection.System.Find("Arial")`, `PdfStandardFont.Helvetica` | Embedded as a subset of the glyphs drawn |
| `gfx.DrawString(text, font, XBrushes.Black, x, y)` | `g.DrawString(text, font, 12, PdfColor.Black, x, y)` | Size per call; y is the baseline in both |
| `gfx.MeasureString(text, font).Width` | `font.MeasureString(text, 12)` | Returns the width in points |
| `new XTextFormatter(gfx).DrawString(text, font, brush, rect)` | `g.DrawText(text, new PdfTextStyle(font, 12), x, y, width)` | `PdfParagraph` adds rich text, justification and right to left text |
| `XPen`, `XBrushes`, `XColor.FromArgb(r, g, b)`, `XLinearGradientBrush` | `PdfPen`, `PdfColor`, `PdfColor.FromRgb(r, g, b)`, `PdfLinearGradientBrush` | A `PdfColor` can be passed where a brush is expected |
| `XGraphicsState state = gfx.Save()`, `gfx.Restore(state)` | `using (g.Save()) { ... }`, or `g.Save()` and `g.Restore()` | The state is restored when disposed |
| `XImage.FromFile(path)`, `gfx.DrawImage(image, x, y, w, h)` | `PdfImage.Load(path)`, `g.DrawImage(image, x, y, w, h)` | JPEG, PNG, GIF, BMP, TIFF and WebP; `PdfSvg` keeps SVG as vectors |
| `XPdfForm.FromFile(path)`, `gfx.DrawImage(form, x, y, w, h)` | `PdfForm.FromPage(doc, other.Pages[i])`, `g.DrawForm(form, x, y, w, h)` | Any page of any open document |
| `doc.Outlines.Add(title, page, true)` | `doc.Outlines.Add(title, page)` | |
| `(PdfTextField)doc.AcroForm.Fields[name]`, `field.Text`, `NeedAppearances` | `(PdfTextField)doc.Form[name]`, `field.Text` | `doc.Form.Flatten()` turns values into page content |
| `doc.SecuritySettings.UserPassword`, `OwnerPassword`, `Permit...`, `SecurityHandler.SetEncryptionToV5()` | `doc.Security.Encrypt(user, owner, permissions)` | AES-256 by default, AES-128 on request |
| `page.AddWebLink(rect, url)`, `page.AddDocumentLink(rect, pageNumber)` | `page.Annotations.AddLink(x, y, w, h, uri)`, `AddLink(x, y, w, h, page)` | Top left coordinates, no transformation needed |
| `doc.Internals.Catalog`, `page.Elements` | `doc.Catalog`, `page.Dictionary` | See [Low level objects](/guides/low-level-objects/) |

## Open, edit and save

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = PdfReader.Open("contract.pdf", PdfDocumentOpenMode.Modify);
doc.Pages.RemoveAt(0);
doc.Pages[0].Rotate = 90;
doc.Info.Title = "Signed contract";
doc.Outlines.Add("Annex", doc.Pages[doc.PageCount - 1], true);
doc.Save("contract-final.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="open" title="PdfNetStack"
```

## Draw a page

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = new PdfDocument();
PdfPage page = doc.AddPage();
page.Size = PageSize.A4;
using (XGraphics gfx = XGraphics.FromPdfPage(page))
{
    var bold = new XFont("Arial", 28, XFontStyleEx.Bold);   // found through the font resolver
    gfx.DrawRectangle(null, new XSolidBrush(XColor.FromArgb(30, 58, 138)), 0, 0, page.Width.Point, 140);
    gfx.DrawString("Invoice 2026-114", bold, XBrushes.White, 40, 80);
    gfx.DrawLine(new XPen(XColors.Gray, 0.5), 40, 160, 555, 160);

    var body = new XFont("Arial", 14);
    double width = gfx.MeasureString("Total: 1,250.00 EUR", body).Width;
    gfx.DrawString("Total: 1,250.00 EUR", body, XBrushes.Black, 555 - width, 200);

    XImage logo = XImage.FromFile("logo.png");
    gfx.DrawImage(logo, 40, 220, 120, 40);

    var formatter = new XTextFormatter(gfx);
    formatter.DrawString("Payment is due within 30 days of the invoice date.", body, XBrushes.Black, new XRect(40, 280, 300, 100));
}
doc.Save("invoice.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="create" title="PdfNetStack"
```

`PdfRectangle.A4` is 595 by 842 points. Every coordinate in the PDFsharp code stays the same. See
[Drawing](/guides/drawing/) for paths, gradients and clipping, and
[Coordinates and units](/concepts/coordinates-and-units/) for millimetres and inches: there is no
`XUnit`, a millimetre is `72 / 25.4` points.

## Fonts

PDFsharp 6 finds fonts through the `IFontResolver` registered in `GlobalFontSettings`.
PdfNetStack loads font files directly, finds installed fonts through `PdfFontCollection.System`,
or uses one of the 14 standard fonts, which need no embedding:

```csharp sample="migrate-pdfsharp.cs" region="fonts" title="PdfNetStack"
```

A font collection matches family, weight, width and style the way CSS does. A font whose license
forbids embedding is refused. Containers and serverless hosts often have no installed fonts, so
ship the files you use. See [Fonts and text](/guides/fonts-and-text/).

## Merge and split

```csharp title="PDFsharp 6.2.4"
using PdfDocument output = new PdfDocument();
foreach (string file in files)
{
    using PdfDocument input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
    foreach (PdfPage page in input.Pages)
    {
        output.AddPage(page);
    }
}
output.Save("merged.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="merge" title="PdfNetStack"
```

```csharp title="PDFsharp 6.2.4"
using PdfDocument input = PdfReader.Open("report.pdf", PdfDocumentOpenMode.Import);
for (int i = 0; i < input.PageCount; i++)
{
    using PdfDocument single = new PdfDocument();
    single.AddPage(input.Pages[i]);
    single.Save($"report-page-{i + 1}.pdf");
}
```

```csharp sample="migrate-pdfsharp.cs" region="split" title="PdfNetStack"
```

Annotations, links between the imported pages and form fields come along. See
[Edit pages](/guides/edit-pages/).

## Letterheads and stamps

`XPdfForm` draws a page of another PDF as an image. `PdfForm.FromPage` does the same, and the
background layer replaces `XGraphicsPdfPageOptions.Prepend`:

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = PdfReader.Open("report.pdf", PdfDocumentOpenMode.Modify);
XPdfForm letterhead = XPdfForm.FromFile("letterhead.pdf");
foreach (PdfPage page in doc.Pages)
{
    using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
    gfx.DrawImage(letterhead, 0, 0, page.Width.Point, page.Height.Point);
}
doc.Save("report-letterhead.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="stamp" title="PdfNetStack"
```

## Forms

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = PdfReader.Open("application.pdf", PdfDocumentOpenMode.Modify);
var name = (PdfTextField)doc.AcroForm.Fields["name"]!;
name.Text = "Jane Doe";
var subscribe = (PdfCheckBoxField)doc.AcroForm.Fields["subscribe"]!;
subscribe.Checked = true;
doc.AcroForm.Elements.SetBoolean("/NeedAppearances", true);   // let the viewer draw the values
doc.Save("application-filled.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="forms" title="PdfNetStack"
```

The appearance is drawn from the field's own border, background, font and alignment, so the
value looks the same in every viewer. From 1.3, `doc.Form.Fill` sets many fields from a dictionary
in one call, checking every name and value first, and `TryGetField` replaces the cast. See
[Forms](/guides/forms/).

## Encryption

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = PdfReader.Open("report.pdf", PdfDocumentOpenMode.Modify);
doc.SecuritySettings.UserPassword = "read";
doc.SecuritySettings.OwnerPassword = "admin";
doc.SecuritySettings.PermitPrint = true;
doc.SecuritySettings.PermitExtractContent = true;
doc.SecuritySettings.PermitModifyDocument = false;
doc.SecurityHandler.SetEncryptionToV5();   // AES-256
doc.Save("report-protected.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="encrypt" title="PdfNetStack"
```

Permissions left out of the flags are not granted. See [Encryption](/guides/encryption/).

## File size

PDFsharp compresses what it writes, with the level set in `doc.Options`. PdfNetStack optimizes on
`Save`, and `PdfOptimizer` shrinks files that any other tool wrote. Each rewrite is read back and
compared with its source before it is returned:

```csharp title="PDFsharp 6.2.4"
PdfDocument doc = PdfReader.Open("manual.pdf", PdfDocumentOpenMode.Modify);
doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
doc.Options.CompressContentStreams = true;
doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
doc.Save("manual-small.pdf");
```

```csharp sample="migrate-pdfsharp.cs" region="compression" title="PdfNetStack"
```

See [Compress PDF](/guides/compress-pdf/).

## Links, low level objects and text

```csharp sample="migrate-pdfsharp.cs" region="details" title="PdfNetStack"
```

PDFsharp's link rectangles are in PDF page coordinates, from the bottom left, so its code usually
converts drawing positions with `gfx.Transformer.WorldToDefaultPage`. PdfNetStack takes the same
top left coordinates as drawing.
PDFsharp reads content as operators (`ContentReader.ReadContent`); PdfNetStack also gives the text
in reading order, and words and letters with their positions. See
[Extract text](/guides/extract-text/).

## No direct equivalent

- **MigraDoc.** PdfNetStack has no document object model of sections, paragraphs and tables that
  breaks pages for you, and no RTF output. For documents laid out from content, use
  [HTML to PDF](/guides/html-to-pdf/): `PdfNetStack.Html` has paged media, tables with repeated
  headers, and running headers and footers. For text that runs over several pages in code,
  `PdfTextLayout` measures a paragraph and draws a range of its lines on each page.
- **Charts.** There is no counterpart to the charting library of PDFsharp and MigraDoc. Draw the
  chart, or place an SVG produced elsewhere with `PdfSvg`, which keeps it vector.
- **Drawing on screen or to bitmaps.** `PdfGraphics` draws only on PDF pages and forms. PdfNetStack
  does not render pages to images.
- **RC4 output.** Files encrypted with RC4 open, but new encryption is AES-256 or AES-128 only.
- **Tagged PDF across documents, before 1.3.** In 1.2, `Merge`, `Split` and `Pages.Import` do not
  carry the structure tree, so pages taken from a tagged document come out untagged. From 1.3
  they keep the structure elements of the pages they copy.

Signatures and PDF/A have their own APIs: see [Signatures](/guides/signatures/), which signs to
PAdES B-B up to B-LTA, and [PDF/A](/guides/pdf-a/), where `PdfSaveOptions.Conformance` sets the
part and level.

## Running both during the move

Both libraries are managed code and can be referenced together. Give the PDFsharp type an alias
where a file uses both:

```csharp title="PDFsharp 6.2.4 and PdfNetStack together"
using PdfNetStack;
using PdfSharp.Pdf.IO;
using SharpDocument = PdfSharp.Pdf.PdfDocument;   // the PDFsharp type, under another name

SharpDocument legacy = PdfReader.Open("report.pdf", PdfDocumentOpenMode.Import);
PdfDocument doc = PdfDocument.Open("report.pdf");
```

Each library reads what the other writes, so a job can move one step at a time.

## Limits

- A full `Save` with the optimizer, the default, builds the file in memory. See
  [Memory and performance](/concepts/memory-and-performance/) for sizing.
- PdfNetStack has no MigraDoc, charting or rendering counterpart, and writes no RC4.
- Before 1.3, pages merged or imported from tagged documents lose their tags.

## Related

- [PdfNetStack and PDFsharp compared](/compare/pdfsharp/): licenses and features side by side, with sources
- [Drawing](/guides/drawing/) and [Fonts and text](/guides/fonts-and-text/)
- [Edit pages](/guides/edit-pages/)
- [HTML to PDF](/guides/html-to-pdf/)
- API: [PdfDocument](/api/pdfnetstack/pdfdocument/), [PdfGraphics](/api/pdfnetstack-drawing/pdfgraphics/), [PdfFontCollection](/api/pdfnetstack-fonts/pdffontcollection/)
