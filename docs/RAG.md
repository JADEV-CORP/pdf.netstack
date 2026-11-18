# Text for search and RAG

`PdfDocument.ReadText` and `ReadTextAsync` read a document's text page by page for search,
indexing and retrieval augmented generation. One content walk per page gives the text, the words
with their boxes, and the blocks (paragraphs, headings, list items, tables) together.

```csharp
using PdfDocument doc = PdfDocument.Open("handbook.pdf");
await foreach (PdfPageText page in doc.ReadTextAsync(new PdfTextOptions { Format = PdfTextFormat.Markdown }, ct))
{
    if (page.HiddenText.Count > 0) logger.LogWarning("Page {Page}: {Runs} hidden runs left out", page.PageNumber, page.HiddenText.Count);
    await index.AddAsync(page.PageNumber, page.Text, ct);
}
```

`PdfPage.ReadText` reads one page. The sync `ReadText` works on every target; `ReadTextAsync` needs
.NET 8 or later. For Microsoft.Extensions.DataIngestion pipelines, `PdfNetStackReader` in the
`PdfNetStack.DataIngestion` package wraps the same reading in an `IngestionDocumentReader`.

## Options

| `PdfTextOptions` | Default | What it does |
|---|---|---|
| `Format` | `Plain` | `Plain`: blocks separated by a blank line, lines by a line feed, table cells by a tab. `Markdown`: headings, lists, GitHub tables, links. |
| `UseStructureTree` | true | Tagged pages read in the order of the structure tree, with headings, list items and tables from the tags. A page where the tree reaches under 75 % of the kept text reads in layout order. `PdfPageText.ReadingOrder` says which. |
| `ExcludeArtifacts` | true | Leaves out content marked `/Artifact`: running headers and footers, page numbers, watermarks. On a page read in structure order, untagged text above or below all tagged text goes too (Chrome leaves its page margin boxes untagged). Headers of untagged documents stay. |
| `VisibleOnly` | true | Leaves out text a reader cannot see (below). Hidden text is always reported in `PdfPageText.HiddenText`, kept or not. |

## Hidden text

Text in a PDF can be present without being visible, and a language model reads it all the same:
it is a common way to plant instructions in a document (prompt injection, OWASP LLM01:2025).
`VisibleOnly` leaves out, with the reason in `PdfHiddenText.Reason`:

| Reason | Text that is |
|---|---|
| `InvisibleRenderMode` | drawn with rendering mode 3 or 7 and not over or under an image |
| `Transparent` | painted at zero opacity (`/ca` or `/CA` 0) |
| `NoContrast` | in the colour of what is under it: the white page, or a filled rectangle of that colour |
| `ClippedAway` | outside the clipping path (or a form's `/BBox`) |
| `OutsidePage` | entirely outside the crop box |
| `TooSmall` | under one point tall, or squeezed under one point wide (`Tz`) |
| `OptionalContentOff` | on a layer that is off in the default configuration |
| `Covered` | under an opaque filled rectangle drawn after it |

The checks err towards keeping text: the clip is judged by its bounding box, only rectangles of
one known colour count as a background or a cover, and a colour, pattern, soft mask or blend mode
they cannot judge counts as visible. Invisible text over or under an image is the text layer of a
scanned page: it is kept and `PdfPageText.HasOcrText` is set. Known limit: invisible text laid
over vector drawings (some equation and diagram exports do this) is reported as hidden.

## Markdown

- Headings: `H1` to `H6` tags, else font size against the page's body size (1.8 times or more is
  level 1, 1.4 level 2, 1.15 level 3) and single bold lines before body text (level 4).
- Lists: `LI` tags, else lines starting with a bullet or a number label; bullets become `- `,
  numbers stay, nesting from the tags or the indent.
- Tables: `Table`, `TR`, `TH`, `TD` tags, else ruled lines only. A grid of lines gives rows and
  columns; horizontal rules alone (top, middle, bottom) bound a region whose columns come from
  gaps down every line, and only when its cells are short. Text in columns without lines is never
  made into a table.
- Links: link annotations to http, https and mailto targets over the words they cover.
- Escaping: what would read as Markdown structure, brackets and `<` are escaped; raw HTML is never
  emitted. Emphasis characters are left as they are.

## Benchmark

`tools/PdfNetStack.TextBench` compares this reading with PdfPig 0.1.16 (the reader Microsoft's AI
chat template ships) on documents whose text is known: HTML printed by Chrome (tagged) and by
PdfNetStack.Html, untagged pages whose content draws lines in shuffled order, and hidden text
cases. Words are compared as multisets after NFKC normalization; order is the longest common
subsequence of words over the truth's length. Run it with:

```bash
dotnet run --project tools/PdfNetStack.TextBench -c Release -- --work /tmp/textbench --corpus artifacts/corpus/files
```
