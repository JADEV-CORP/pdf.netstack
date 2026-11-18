# Fidelity and verification

"Lossless" here means a conforming PDF reader cannot tell the output from the input: same pages,
same content, same fonts and images, same annotations, forms, bookmarks, structure, metadata and
attachments. Three layers of evidence back that claim.

## 1. Built-in equivalence proof

Runs on every optimization unless `Verify` is turned off.

The source bytes and the candidate bytes are parsed independently. Starting at the trailer's
`/Root`, `/Info` and `/ID`, the verifier walks both graphs together:

- names, strings, booleans and numbers must be identical (numbers compared by canonical value),
- arrays must have the same length and matching items,
- dictionaries must have the same keys with matching values; an entry whose value is null counts
  as absent, as the specification defines,
- references are followed in pairs, so cycles terminate; a source object must always map to the
  same output object, and two source objects may share one output object only when neither has
  identity of its own (annotations, form fields, pages, structure elements and tree nodes never do),
- every stream is decoded through all decodable filters and predictors on both sides and the
  bytes must match exactly; filters that stay encoded (DCT, JPX, JBIG2, CCITT) must be identical,
  including their parameters and any objects they reference such as `/JBIG2Globals`,
- JPEG data whose bytes changed is decoded to DCT coefficients on both sides (see below),
- a bilevel image whose output is CCITT or JBIG2 data is decoded to pixels on both sides (see below),
- streams that cannot be decoded must be byte identical with an identical filter chain.

Apart from the structural changes below, bilevel images coded again, and the opt-in image
changes described under JPEG images, the only accepted differences are the encoding entries of streams: `/Length`, `/Filter`, `/DecodeParms` and `/DL`. With opt-in stripping
enabled, `/Thumb` and `/PieceInfo` may be missing from the output.

### Structural changes

The lossless structural passes change more than encoding, so each has a rule of its own. None
is taken on trust: the verifier runs its own content analysis on its own parse of the source,
and accepts a difference only where that analysis proves it invisible. `PdfEquivalence` and
`pdfnetstack verify` apply the same rules.

**Unused resources.** A resource entry (font, XObject, graphics state, pattern, shading, color
space, property list) may be missing from the output only when no content that can use it names
it. The analysis reads every content stream from the page tree and from annotation appearances
(every state of /N, /R and /D), and follows form XObjects, tiling patterns, Type 3 glyph
procedures, soft mask groups and fonts selected through graphics states. A name counts as used
when it appears anywhere in the decoded content as a name token, whatever operator it belongs
to, and even inside strings, comments or inline image data. A page's content parts are scanned
joined with and without a separator between them, as readers differ. Content with resources of
its own that names something those resources lack also keeps that name in the resources of the
content that invoked it, since some readers look there. `DefaultGray`, `DefaultRGB` and
`DefaultCMYK` are used implicitly and always kept.

A category dictionary is pruned only when every resource dictionary holding it belongs to
content the analysis read in full: a dictionary also reached as form default resources (`/DR`),
from content that does not decode, from a holder that was never executed, or through any other
key keeps every entry. When a work budget runs out (2 GB of decoded content or 50 million
operators) nothing is pruned.

**Content streams.** A stream that is only ever used as content (page contents, forms, tiling
patterns, appearances, glyph procedures, soft mask groups, widget icons) may be rewritten when
it reads as the same tokens. The comparison tokenizes both versions: operators must be byte
identical, numbers equal by canonical value, names equal after `#xx` decoding, strings equal by
their bytes whether written literal or hex, and inline image dictionaries equal by value with
their data byte identical. Inline image data whose end is ambiguous (a filtered image without
/L whose data contains `EI`, or rules that disagree) stops the rewrite: everything from that
image on must be byte identical. Because readers join a page's parts in different ways, a
leading or trailing white space byte must be kept, and a first or last token that touches the
edge must keep its exact bytes. A stream that ends inside a comment is not changed.

**Merged font programs.** The TrueType program behind a composite font may be replaced by a
program shared with other subsets of the same font when: the font is Type0 with Identity-H or
Identity-V, its descendant is a CIDFontType2 with an identity /CIDToGIDMap and no /CIDSet, the
program is only reached through such fonts, and the analysis decoded every string shown with
them. The glyphs a program must keep are the codes those fonts show, glyph 0 and every component
of a composite glyph among them. For a merged program the verifier requires: every table other
than glyf, loca, hmtx, hhea, maxp, head, post, name and cmap byte identical; the same version,
revision, flags, units per em, style, recommended size, direction hint and glyph data format in
head; the same ascender, descender, line gap, caret slope and metric format in hhea; the same maxp
version and hinting limits at least as wide; and for each of those glyphs the same outline bytes
(up to trailing zero padding), the same advance width and the same left side bearing. Glyph names
(post) are dropped and the name and cmap tables come from one of the merged programs; a
CIDFontType2 with an identity map selects glyphs by id and reads none of them. Subsets made by
the pass below may be merged too, so the tables readers never apply may be missing.

**Font subsets.** A TrueType program (FontFile2) or a CFF program (FontFile3 /Type1C or
/CIDFontType0C) may be replaced by a subset of itself only when every glyph a reader can draw
from it is known. The verifier finds that out from its own analysis of the source and requires
all of this:

- the analysis read in full every content stream that can use the program (pages, forms,
  tiling patterns, Type 3 glyph procedures, soft masks, every state of every appearance) and
  tied every string shown to its font, and each font using the program is selectable only
  through resource dictionaries it read in full;
- no interactive form can draw new text with it: the program is not reached from the AcroForm
  /DR, no /DA string anywhere names a font that uses it, and the document has no XFA;
- each font using it is a simple TrueType or Type 1 font with /Widths, or a Type0 font with
  Identity-H or Identity-V over a CIDFontType2 (identity or stream /CIDToGIDMap) or a
  CIDFontType0; its descriptor has no /CharSet or /CIDSet; the file is not PDF/A-1.

The glyphs that must survive are worked out on the source. For a composite font, the glyph of
each CID shown, through /CIDToGIDMap or the CFF charset. For a simple font, every glyph some
reader could pick for each code shown: through every cmap subtable (the code, the code plus
0xF000, 0xF100 and 0xF200, and the Unicode value of each candidate name), the glyph names of
/Differences and of every predefined encoding at that code looked up in post or the CFF
charset, the program's built-in encoding, and the glyph whose id is the code. Then .notdef,
every component of a composite glyph and both parts of an accented (seac) glyph, transitively.

Each of those glyphs must keep its outline byte for byte (TrueType glyf data up to zero padding,
CFF charstrings exactly), its advance width and its left side bearing. Glyph ids never change,
so /CIDToGIDMap, cmap, post and the charset select the same glyphs as before. Everything else
that steers rendering must be byte identical: for TrueType every table but glyf, loca and hmtx,
with head differing only in its checksum and loca format, and only tables readers never apply
(GSUB, GPOS, GDEF, BASE, JSTF, MATH, kern, morx, mort, kerx, feat, DSIG) allowed to go; for CFF
the names, strings, charset, encoding, FDSelect, every Top, Font and Private DICT entry other
than offsets, the number of subroutines, and byte for byte every subroutine those glyphs call,
directly or through other subroutines, found by running their charstrings. A subroutine nothing
calls becomes a bare return, so every subroutine keeps its number. Hinting instructions (fpgm,
prep, cvt, glyph instructions, CFF hints) stay as they were, so hinted rendering matches too.
Font dictionaries,
/Widths, /W, /W2, /DW, /Encoding and /ToUnicode go through the graph walk unchanged. The one
change allowed there is the subset tag of ISO 32000-1, 9.6.4: /BaseFont, the CIDFont's
/BaseFont and /FontName may gain six capital letters and a plus sign, only when they had no
tag and the name is not one readers treat specially (the standard 14 names, the Arial, Times,
Helvetica and Courier aliases some readers map Type 1 fonts to, Symbol and dingbat fonts). A new
tag is never one the source already uses, and two different output programs never share one.
Programs with the same content are subset together for all their fonts, so they stay one.

**Type 1 programs as CFF.** With `ConvertType1Fonts` (off by default), a Type 1 program
(FontFile) may be replaced by a CFF program
(FontFile3 /Subtype /Type1C) holding every one of its glyphs, when every font using the
descriptor is a simple Type 1 font and the output is PDF 1.2 or later; the descriptor's /FontFile
becomes /FontFile3 and the stream drops /Length1 to /Length3. The verifier decrypts the source
(eexec, binary or hex, and charstrings) and interprets both programs glyph by glyph. Each glyph
must have the same name, the same advance width and the same path: moves, lines and curves at
exactly the same coordinates, Type 1 flex against CFF flex with the same depth. Type 1 division
is taken in 16.16 fixed point, rounded to nearest, as FreeType and pdf.js compute it, so CFF
holds the result exactly. Hints must match as well: the same stems, grouped the same way at the
same points of the path (Type 1 hint replacement against CFF hint masks), stem3 hints as the
same counter groups, and an accented glyph must name the same base and accent at the same
offset. The font matrix, bounding box, built-in encoding (the glyph named by each code), paint
type and every Private value that steers hinting (blue zones, blue scale, shift and fuzz,
standard and snap stem widths, ForceBold, language group, expansion factor) must be equal, and
the CFF DICTs may hold nothing else that changes rendering. A font that uses anything CFF
cannot say exactly is left as it is: counter control and other OtherSubrs, dotsection, a
vertical advance, coordinates out of the 16.16 range, stroked (PaintType 2) or multiple master
fonts. Rasterizers hint the two formats with separate code: on 220 Type 1 programs from the
public corpus, FreeType draws 62,347 of 62,748 hinted glyph images alike in both forms, and
the other 401 differ in the same way for Adobe's own converter (tx), whose output FreeType draws
identically to this one; PDFium, which draws these fonts without hinting, shows no difference.

**Streams equal after decoding.** Deduplication compares streams by what they decode to, so two
copies of a font program or image merge even when they were encoded differently. The comparison
covers the decoded bytes, any predictor and filter still applied, and the stream dictionary
without its encoding entries.

**Obsolete XObject names.** An image, form or PostScript XObject's `/Name` was required only in
PDF 1.0; later readers ignore it (ISO 32000-1, tables 89 and 95). Unless the output stays PDF
1.0, deduplication leaves it out of the comparison, and a merged XObject whose copies were named
differently is written without it. The verifier then lets `/Name` be missing from such an
XObject in the output, and from nothing else; a name may never appear or change.

**Inherited page attributes.** With `Linearize`, `/Resources`, `/MediaBox`, `/CropBox` and
`/Rotate` move from intermediate page tree nodes onto the pages that inherit them. The verifier
then compares each page by the value it ends up with, its own or its nearest ancestor's, and does
not compare those keys on intermediate nodes, whose values matter only through the pages.

### Streaming mode

With `PdfOptimizerOptions.Streaming` the proof is the same one, not a sampled or per page check.
The rewrite is written to a temporary file, and the verifier runs on two readers that load
objects on demand: a fresh reader over the source, which has parsed nothing the passes touched,
and a reader over that file. It walks both graphs from the trailer and compares every reachable
object, and every stream by its decoded content, under the same rules as above. Each stream is
decoded only while it is compared. The output must also parse with no repaired stream length,
checked again after the walk has read every stream. A merged object is allowed exactly the
image change its representative got, which is what the output holds for it. Only a proven
rewrite reaches the destination; otherwise the input is copied there unchanged.

### Linearized output

A linearized file holds the same objects as any other rewrite, so the proof above covers its
content. Its layout is checked separately. The tests read every linearized file back and check
the linearization dictionary against the file (`/L`, `/O`, `/E`, `/N`, `/T`, `/H`), both
cross-reference sections, and every entry of the page offset, shared object and outline hint
tables against the real offsets and lengths of the objects. `tools/measure_optimizer.py
--linearize` also runs qpdf's `--check-linearization` on each output when qpdf, or pikepdf with
its bundled libqpdf, is installed. The objects are assigned to the parts of Annex F the way qpdf
assigns them, so its check computes the same layout.

### Bilevel images

The Group 4 pass (`OptimizeBilevelImages`) codes 1 bit images again as CCITT Group 4: stencil
masks and 1 bit gray, palette and single colorant images, stored as samples under any general
filters (predictors included) or as CCITT Group 3. The encoder follows ITU-T T.6 and chooses its
modes by the coding procedure of T.4, so it writes the same bytes as libtiff (a test checks this
on libtiff's own output). /BlackIs1 is set when the 0 bits are the more common value, which keeps
the samples as they were while long runs use the short white codes.

The proof accepts an output coded as CCITT data in place of samples or CCITT data only by its
pixels. Both sides are decoded, every pixel of every row must match, and the padding bits at
the end of a row, which are not pixels, are ignored. Readers recover from flawed fax data in
different ways, so only data every reader takes alike counts, on both sides:

- parameters of their own type (integers, booleans), /Columns equal to the width (left out it
  means 1728 to some readers and the width to others), /Rows unset or equal to the height,
- data that decodes without a flaw: every code exists, no run overshoots its row, the changes
  on a row are strictly ordered (an empty run only where a row starts black or ends), no pass
  reaches past the reference row, and every row is there before the data ends or stops,
- samples at least as long as the image.

Anything else must match as it was, encoded bytes and parameters included. Group 3 data with a
flaw, or with parameters a reader could take differently, is never coded again.

With `UseJbig2` (on by default) each such image is also coded as JBIG2 (ITU-T T.88) and keeps
whichever of Group 4 and JBIG2 is smaller; output that stays below PDF 1.4, which has no
JBIG2Decode, gets Group 4 only. Only generic region coding is written, which is lossless, never
symbol coding, which matches glyphs by likeness and can show one character in place of another.
The stream is the embedded organisation PDF uses (ISO 32000-1, 7.4.7), with no file header and
no JBIG2Globals: one page information segment and one immediate generic region over the whole
page, arithmetic coded with template 0, its adaptive pixels at their nominal places, typical
prediction on, and the coded data ended by the 0xFFAC marker. JBIG2Decode gives a reader the
inverse of the T.88 bitmap, so the bitmap coded is the samples inverted, whatever the samples
mean. The optimizer decodes every JBIG2 image it would keep before keeping it. jbig2dec reads the
same pixels (a test checks this where it is installed), and the coded data matches what jbig2enc
writes in generic mode to a few bytes.

The proof accepts JBIG2 data in place of samples or CCITT data, again only by its pixels, and
only in the form every reader decodes alike, the form written here: the page information segment
of exactly the image's size, white by default and not striped; one immediate generic region over
the whole page at its origin, drawn by OR or REPLACE, arithmetic coded with a standard template
and its adaptive pixels at their nominal places, its data ended by the 0xFFAC marker so that no
reader reads past it; nothing after but an end of page or end of file; and no JBIG2Globals.
JBIG2 data in any other form must match as it was.

### JPEG images

The JPEG pass re-encodes DCTDecode data without ever producing pixels, so the proof works at the
same level. Both streams are read to their quantized coefficients, and must agree on width,
height, component ids and order, chroma sampling, and the JFIF and Adobe color transform
signalling. Every coefficient of every visible block must then be identical. Entropy coding,
progressive or baseline layout, restart markers and the segments readers ignore (Exif, XMP,
embedded ICC profiles, comments) may differ. Equal coefficients decode to equal pixels in any
conforming decoder, which is why this counts as lossless.

With `JpegQuality` set, the proof accepts one more change and checks it exactly: each output
quantization step must be at least the source step, and each output coefficient must equal the
source coefficient requantized with the integer rounding the optimizer uses. Any other change,
including one coefficient off by one, fails verification. `pdfnetstack verify --jpeg-quality`
and `PdfEquivalence.FindDifference(..., allowJpegRequantization: true)` apply the same rule.

The lossy image options (`MaxImageDpi`, `ConvertImagesToJpeg`) change pixels, so their proof
works on pixels and is a bound, not an identity. Only images the optimizer changed get it, and
`PdfEquivalence` grants it only to images the optimizer could have changed: soft masks, masks,
stencil and color keyed images must match exactly, and so must the size of images with a /Mask
or a /Matte soft mask. An image whose data is unchanged must keep its size, and so must a
bilevel image, a JPEG 2000 image that stays JPEG 2000 and a JBIG2 image that stays JBIG2: the
optimizer changes those only by reducing them. Every other dictionary entry must still match,
except that a JPEG 2000 image without /BitsPerComponent gains /BitsPerComponent 8 as samples or
a JPEG; the output must have the same components and bit depth, no larger a size, the same
aspect within a pixel, and decode as its dictionary says (raw samples, or one JPEG, JPEG 2000,
JBIG2 or CCITT image). Both images are decoded and the source is resampled to the output's size
with the same filter: Lanczos, or for bilevel images the reduction described below. Then each
channel's mean may move by at most 4 steps, 32 pixel thumbnails may differ by at most 12 steps
on average, which catches mirrored, shifted or swapped pictures, and luminance SSIM must reach
`MinImageSsim`, or 0.5 when no gate is set. With a gate, requantized JPEGs must also reach it.
The optimizer checks the same bounds on every JPEG it makes before choosing it, so it never
writes an image the proof would reject. `pdfnetstack verify --min-ssim` and
`PdfEquivalence.FindDifference(..., minImageSsim)` apply the same checks to any two files.

A JPEG 2000 image counts only when its dictionary describes it fully: a gray or RGB /ColorSpace,
no /Decode, no /SMaskInData, /BitsPerComponent 8 or none, and data with exactly that many color
channels, no opacity channel and colors that are not sYCC. A bilevel image (raw, CCITT data
every reader takes alike, or JBIG2) stays bilevel when reduced. Each new pixel takes the share
of its area that the 1 bits cover in the source, an exact box filter. The less common value
wins a pixel it covers more than half of, and also a pixel it covers at least a quarter of that
is a ridge across or along the image (covered more than the neighbour before it and at least as
much as the one after it): the middle of a stroke thinner than the new pixels, which plain
thresholding would erase. The arithmetic is exact and in a fixed order, so the proof reproduces
the reduction bit for bit and the optimizer's own output reaches SSIM 1 against it.

### Lossy changes proven when written

The bounds above take decoding both images, resampling the source and comparing them, which on
a scanned book is as much work as making the change. When the optimizer proves its own output,
it does not do that work twice. As it writes a changed image it checks that image against the
same bounds, on the same images, with the same functions, and records a key for the check: a
SHA-256 over both images after their general filters (samples only up to the size the
dictionary gives them, which is all a decoder reads; coded data whole), how each is coded and
sized (width, height, components, bits, coding, JPEG color transform, CCITT parameters, palette),
the JBIG2Globals of each, and the gate. A requantized JPEG gets a key over both images and its
color transform and the gate, once its SSIM met the gate. The proof then reads both images as
always, undoes their general filters, makes every check that needs no pixels (same components
and bit depth, no larger, the same aspect, coded as the dictionary says, and for JPEG the exact
requantization of every coefficient), computes the key from what it read and looks it up. A
changed image whose key was not recorded is refused, whatever its pixels: the output holds
something other than what was checked. A key only skips work: anything that differs in any
input gives another key. Outputs whose pixels are by construction the reference itself (samples
under Flate, a palette under Flate, bilevel samples, Group 4 or JBIG2 data of the reduced
bitmap) count as checked once their data is known to decode to it: under Flate the key's hash
is of the samples the proof inflates, and Group 4 and JBIG2 data is decoded before it is kept.
`PdfEquivalence` and `pdfnetstack verify` have no keys and check every changed image in full.

Images that cannot gain are not decoded in the first place. A lossy candidate must cost less
than 90% of the best lossless encoding, which is never more than the stored one; the JPEG
encoder writes at least two bits for every 8 by 8 block (a DC code and an end of block, or in
progressive coding a DC code and its refinement bit), so an image stored in fewer bytes than
that for its size can never become a smaller JPEG. Such images, and images under 256 bytes,
are left out before their data is read, unless they are downsampled and could still win as
samples.

The coefficient reader is itself checked against libjpeg: test fixtures written by libjpeg-turbo
in baseline, optimized, progressive and restart marker layouts of the same image must decode to
identical coefficients, and scaled tables must match libjpeg's own for the same quality.

What it does not check: the trailer keys that describe file layout (`/Size`, `/Prev`,
`/XRefStm`), object numbers and generations, and byte offsets. None of these affect rendering.

The checker is public as `PdfEquivalence.FindDifference` and on the command line as
`pdfnetstack verify`, so you can use it to validate any other tool's output.

## 2. Independent render comparison

`tools/render_compare.py` renders both files with PDFium (through `pypdfium2`, a development
tool only, not a dependency of any package) and compares page count, page geometry, extracted
text per page and every RGB pixel of the first pages and the last page.

```bash
pip install pypdfium2 numpy
pdfnetstack batch ./corpus ./out
python tools/render_compare.py ./corpus ./out --dpi 150 --pages 6
```

Latest run: 1,068 optimized documents, 2,085 pages at 150 DPI, zero differing pixels.

Structural passes, on synthetic samples from `tools/make_optimizer_samples.py` (unused
resources, verbose content, separately embedded subsets of one font, inherited attributes with
bookmarks, and all of them in one file): 88 pages rendered at 100 DPI from the default output
and from the linearized output, zero differing pixels, identical text. Every linearized output
passed qpdf 12.4's `--check-linearization`.

### Public benchmark corpus

The documents above are private. `eng/corpus/manifest.json` lists a public corpus anyone can
download and check: 77 public domain and openly licensed PDFs, each with its URL, SHA-256, size,
license and what it exercises (scans with JBIG2, JPEG 2000 and CCITT images, Chinese, Japanese
and Korean text, forms, XFA, tagged, signed, encrypted, linearized, damaged and PDF 2.0 files).
`tools/corpus_bench.py` optimizes each one with a lossless and a lossy profile, proves every
rewrite again with `PdfEquivalence`, compares the text and decoded images the library reads
before and after, and renders the first pages and the last page of both with PDFium:

```bash
python tools/fetch_corpus.py
dotnet build tools/PdfNetStack.CorpusRunner -c Release
python tools/corpus_bench.py --dpi 100 --pages 6
```

First run: 65 of 77 documents rewritten (the other 12 are signed or encrypted), 217 pages at
100 DPI identical after the lossless rewrites, the same text and decoded images before and
after, no rewrite failing its proof. Lossy rewrites lost at least 37.6 dB PSNR on any page but
one: a 1.8 KiB ImageMagick test file of tiny images, at 30.4 dB.

Run with bilevel, JBIG2 and JPEG 2000 images in the lossy profile: again no failure, lossless
rewrites identical, lossy saving 29.5% (27.2% before). Pages of CCITT scans reduced to 150 dpi
bilevel measure 22.8 to 22.9 dB PSNR against the source at 100 DPI, and a 1.8 KiB ImageMagick
fax test at 17.2 dB: a bilevel page differs from its source along every stroke edge, so its
PSNR says little. Read side by side, the reduced pages keep every stroke, rule and serif.

## 3. Tests

- Syntax: literal and hex string escapes, names with `#xx`, number canonicalization, references,
  nesting limits, and a randomized round trip of strings and names through the serializer.
- Filters: known vectors for ASCII85, ASCIIHex and RunLength, a reference LZW encoder for both
  EarlyChange modes, Flate with raw deflate fallback, corrupt data, a decompression bomb, PNG
  predictor round trips across bit depths, TIFF predictor.
- Reader: classic and stream xrefs, object streams, incremental updates, `/Prev` loops, shifted
  offsets, missing `startxref`, wrong stream lengths, missing objects, garbage input.
- Optimizer: every pass, every refusal rule, dedup identity rules, PDF/A-1 layout, stripping,
  cancellation, atomic file output.
- Structural passes: pruning through forms, patterns, Type 3 fonts, appearances, inherited and
  shared resources, `/DR`, unreadable content and names split across parts; compaction of
  numbers, names, strings, comments and inline images, a randomized round trip, and every kind
  of token change caught; font merging with glyph conflicts, glyphs past the end of a subset and
  changed outlines; mutated inputs that must never throw.
- Font subsets and Type 1 conversion: simple and composite TrueType and CFF fonts subset with
  every glyph a reader could pick, composite components and subroutine numbers kept; fonts that
  forms can draw with, fonts a /DA string names, special names and unreadable content left whole;
  every change the subset proof must catch (a shown glyph, a used table or subroutine); Type 1
  programs written in code with hint replacement, flex, seac, stem3, division, sbw and hex eexec,
  converted and proven, and each feature CFF cannot express refused; mutated Type 1 programs
  that may only fail as font errors.
- Linearization: the layout checks above on documents with one and many pages, shared fonts,
  bookmarks in and out of the first page, form fields, an open action and inherited attributes;
  page trees it cannot lay out; mutated inputs.
- JPEG: libjpeg-turbo fixtures (4:2:0, 4:2:2, 4:4:4, gray, Adobe CMYK, untransformed RGB, odd
  sizes) in baseline, optimized, progressive and restart marker layouts; lossless and requantized
  round trips; soft masks, Flate wrapped JPEG, color space mismatches, palettes, damaged and
  truncated data, unsupported coding processes, and mutated inputs that must never throw.
- JBIG2: the generic region encoder round tripped at every width from 1 to 40 pixels and up to
  2551, blank and full pages, the inversion of PDF samples, jbig2dec reading the same pixels,
  scanned text at least 10% under Group 4, the encoder giving up past its limit, the decoder's
  fast path for nominal template 0 against its general case, and every departure from the plain
  form (another size, black by default, stripes, XOR, MMR, moved adaptive pixels, referred-to
  segments, no end marker, a second region, a file header, symbol coding) refused by the proof.
- Lossy proof keys: a resampled image and a requantized JPEG each pass with the key recorded
  when they were written, and are refused with one sample off, another quality, another gate or
  no key, though the full check would let the first two through. The JPEG size floor holds for
  flat and photographic images in gray, RGB and CMYK at qualities 5 to 95; images under it are
  never planned for decoding. JPEG 2000 decoded straight to samples, and with one workspace
  serving image after image in either order, gives the same samples as a fresh decode to planes.
- Bilevel images: the Group 4 encoder byte for byte against libtiff, round trips of random rows
  at widths from 1 to 9000 pixels with runs past 2560 in both polarities, the libtiff Group 3
  fixtures coded again as Group 4, stencil masks, palettes, /Decode and predictors; Group 3 with
  flaws or parameters readers take differently left alone; a changed pixel, an inverted
  reading, short data and ambiguous parameters each caught by the proof; reductions of CCITT,
  JBIG2 and raw scans with thin strokes kept, and bad reductions (another page, too dark, plain
  subsampling, gray output) rejected; JPEG 2000 converted and reduced, and the JPEG 2000 images
  the dictionary cannot describe left alone; mutated fax and JBIG2 data that must never throw.
- Fuzzing: mutated inputs (byte flips, deletions, truncation, injected tokens, duplicated
  regions) must never throw and must produce either the exact input or a verified rewrite.
  CI runs 600 cases; set `PDFNETSTACK_FUZZ_ITERATIONS` for a long soak. 20,000 cases pass locally.

## Reporting a fidelity bug

If an optimized file looks different from its source in any viewer, that is the most serious
class of bug in this project. Please open an issue with the viewer and version, and if you can
share it, the source file. `pdfnetstack verify` output for the pair helps triage.
