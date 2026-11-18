using System.IO.Compression;

namespace PdfNetStack;

/// <summary>How hard Flate compression works. Higher effort trades CPU for smaller output.</summary>
public enum CompressionEffort
{
    /// <summary>zlib level 1. Use for throughput-bound batch jobs.</summary>
    Fast,

    /// <summary>zlib level 6.</summary>
    Balanced,

    /// <summary>zlib level 9. The default.</summary>
    Maximum,

    /// <summary>
    /// An exhaustive Deflate search for archival jobs: iterated optimal parsing over every match
    /// length with a cost model refined from each pass, block splitting, and the smallest of
    /// dynamic, fixed and stored blocks, in the manner of Zopfli. Output stays standard Flate and
    /// is never larger than zlib level 9, which is kept whenever it wins. Expect a few percent
    /// smaller streams for 2 to 150 times the CPU time, the most on long repetitive streams.
    /// </summary>
    Exhaustive,
}

/// <summary>
/// Options for <see cref="PdfOptimizer"/>. Defaults are lossless and conservative: anything the
/// engine cannot rewrite with certainty is returned byte for byte unchanged.
/// </summary>
public sealed record PdfOptimizerOptions
{
    /// <summary>Shared default instance.</summary>
    public static PdfOptimizerOptions Default { get; } = new();

    /// <summary>
    /// Much smaller files with no change most readers can see: drawings and screenshots get a
    /// palette, photographs become JPEG at quality 75, existing JPEG images are requantized to it,
    /// and every image keeps an SSIM of at least 0.95 or stays as it was. Not lossless. Start from
    /// this with a <c>with</c> expression to change one thing.
    /// </summary>
    public static PdfOptimizerOptions Balanced { get; } = new()
    {
        QuantizeImages = true,
        ConvertImagesToJpeg = true,
        JpegQuality = 75,
        ProgressiveJpeg = true,
        MinImageSsim = 0.95,
    };

    /// <summary>
    /// As small as a document stays useful on screen: <see cref="Balanced"/> with JPEG quality 50,
    /// images downsampled to 150 dpi (bilevel scans to 300, where text stays legible), and an SSIM
    /// of at least 0.90. Fine texture softens where you look for it; check it suits your documents,
    /// and do not use it for print. Not lossless.
    /// </summary>
    public static PdfOptimizerOptions Smallest { get; } = Balanced with
    {
        JpegQuality = 50,
        MaxImageDpi = 150,
        MaxBilevelImageDpi = 300,
        MinImageSsim = 0.90,
    };

    /// <summary>Minimum saving, in percent of the input size, required to keep the rewrite. Default 1.</summary>
    public double MinSavingPercent { get; init; } = 1.0;

    /// <summary>
    /// Largest input accepted, in bytes. Larger files are returned unchanged. Default 1 GiB. In
    /// memory, plan for several times the input size; with <see cref="Streaming"/> the input is
    /// never held in memory, and the limit can be raised to the largest file to accept.
    /// </summary>
    public long MaxInputBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Optimize without holding the file in memory, for files larger than memory or a small
    /// serverless instance. The input is read on demand, the way
    /// <see cref="PdfDocument.OpenOnDemand"/> reads it, and the rewrite is written progressively
    /// to a temporary file: the destination's own temporary file for
    /// <see cref="PdfOptimizer.OptimizeFileAsync"/>, otherwise one in <see cref="Path.GetTempPath"/>,
    /// which needs room for the output. The proof then reads that file back on demand and
    /// compares it with the source exactly as in memory: every reachable object, and every stream
    /// by its decoded content. Only a proven rewrite, or else the input, reaches the output.
    /// Memory follows the number of objects and the largest streams being worked on, not the
    /// size of the file: object dictionaries and the cross-reference data are kept, stream data
    /// is not, and streams are recompressed in batches of at most 32 MiB of source data (a larger
    /// stream on its own), each written before the next starts.
    /// The output may differ from the in-memory rewrite: duplicates are found from what streams
    /// decode to before they are recompressed, so two JPEG images whose files differ only in
    /// metadata stay two, and a JBIG2Globals stream that a re-encoded image no longer needs is
    /// still written. Cannot be combined with <see cref="Linearize"/>, which lays out the whole
    /// file at once. Default false.
    /// </summary>
    public bool Streaming { get; init; }

    /// <summary>
    /// Ceiling for any single decoded stream, the guard against decompression bombs. Streams over the
    /// limit are left untouched. Default 256 MiB.
    /// </summary>
    public long MaxDecodedStreamBytes { get; init; } = 256L << 20;

    /// <summary>Flate effort for streams the optimizer re-encodes. Default <see cref="CompressionEffort.Maximum"/>.</summary>
    public CompressionEffort Compression { get; init; } = CompressionEffort.Maximum;

    /// <summary>Pack non-stream objects into compressed object streams (PDF 1.5). Default true.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>Objects per object stream. Larger compresses better; smaller loads pages faster. Default 200.</summary>
    public int ObjectsPerStream { get; init; } = 200;

    /// <summary>
    /// Merge identical fonts, images and other resources. Streams are compared by what they decode
    /// to, so copies encoded differently merge too, and so do images and forms that differ only by
    /// the /Name entry readers have ignored since PDF 1.0. Default true.
    /// </summary>
    public bool DeduplicateObjects { get; init; } = true;

    /// <summary>
    /// Remove resource entries (fonts, XObjects, graphics states, patterns, shadings, color spaces,
    /// property lists) that no content stream using the resource dictionary can name. Lossless:
    /// pages, forms, patterns, Type 3 glyphs and annotation appearances are all read, and anything
    /// that cannot be read for certain keeps its resources. Default true.
    /// </summary>
    public bool PruneUnusedResources { get; init; } = true;

    /// <summary>
    /// Rewrite content streams with minimal white space, canonical numbers and no comments. Every
    /// operator and operand stays the same; verification compares the streams token by token.
    /// Default true.
    /// </summary>
    public bool CompactContentStreams { get; init; } = true;

    /// <summary>
    /// Merge subsets of one TrueType font that keep the original glyph ids (composite fonts with an
    /// identity CIDToGIDMap) into a single program, when every glyph each font shows stays exactly
    /// the same. Common in merged documents. Requires <see cref="DeduplicateObjects"/>. Default true.
    /// </summary>
    public bool MergeFontSubsets { get; init; } = true;

    /// <summary>
    /// Subset fully embedded TrueType and CFF fonts to the glyphs the document can show, and leave
    /// out the layout tables (GSUB, GPOS, GDEF, kern and the like) PDF readers never apply. Glyph
    /// ids do not change, every glyph any reader could pick for a code shown keeps its outline,
    /// metrics and hints, and the font's name gains a subset tag. Fonts that interactive forms can
    /// draw new text with, fonts whose use cannot be listed in full, and names readers treat
    /// specially stay whole; <see cref="OptimizationStatistics.FontsLeftWhole"/> says why for each.
    /// Lossless. Default true.
    /// </summary>
    public bool SubsetFonts { get; init; } = true;

    /// <summary>
    /// Rewrite Type 1 font programs (FontFile) as CFF (FontFile3 /Type1C), which holds the same
    /// glyphs and hints in far fewer bytes: Type 1 data is encrypted and does not compress.
    /// Every glyph is kept; the proof interprets both programs and requires the same paths,
    /// widths, hints and encoding. Fonts using features CFF cannot express exactly stay as they
    /// are. Needs PDF 1.2 output. Rasterizers that hint the two formats with separate code can
    /// draw a few small glyphs a pixel apart (FreeType: 401 of 62,748 hinted glyph images on 220
    /// TeX fonts, as with Adobe's own conversion; PDFium, which draws them unhinted, shows no
    /// change), so this is off until asked for. Default false.
    /// </summary>
    public bool ConvertType1Fonts { get; init; }

    /// <summary>
    /// Write a linearized file (ISO 32000-1, Annex F), which a viewer can start showing before the
    /// whole file has arrived ("fast web view"). Inherited page attributes are moved onto the
    /// pages, and every object is written on its own without object streams, so the output can
    /// be larger than the input; a linearized rewrite of a file that is not linearized yet is kept
    /// even then, provided it verifies. A page tree that cannot be laid out page by page, such as
    /// one listing a page twice, is written as usual and the result says why. Default false.
    /// </summary>
    public bool Linearize { get; init; }

    /// <summary>Re-encode streams with better lossless compression. Default true.</summary>
    public bool RecompressStreams { get; init; } = true;

    /// <summary>Try PNG prediction on raw images of 8 bits or fewer per component. Lossless. Default true.</summary>
    public bool UseImagePredictors { get; init; } = true;

    /// <summary>
    /// Re-encode JPEG images (DCTDecode) with Huffman tables built for each image, and without the
    /// segments a PDF reader ignores (Exif, XMP, embedded ICC profiles, comments; the image's color
    /// space comes from the PDF). The DCT coefficients, and so the decoded pixels, stay the same.
    /// Part of stream recompression. Default true.
    /// </summary>
    public bool OptimizeJpegImages { get; init; } = true;

    /// <summary>
    /// Re-encode bilevel (1 bit) images stored as raw samples, under Flate, LZW or RunLength, or as
    /// CCITT Group 3, as CCITT Group 4, or JBIG2 with <see cref="UseJbig2"/>, when that is smaller.
    /// The encoders follow ITU-T T.6 and T.88, and the proof decodes both versions and requires
    /// every pixel to match, so the image stays exactly the same. Group 3 data is only re-encoded
    /// when it decodes without a flaw and its parameters leave nothing to a reader's defaults. Part
    /// of stream recompression. Default true.
    /// </summary>
    public bool OptimizeBilevelImages { get; init; } = true;

    /// <summary>
    /// Also code bilevel images as JBIG2 (ITU-T T.88) wherever they are coded again: by
    /// <see cref="OptimizeBilevelImages"/>, and when <see cref="MaxImageDpi"/> or
    /// <see cref="MaxBilevelImageDpi"/> reduces them. Each image keeps whichever of CCITT Group 4
    /// and JBIG2 is smaller; on scanned text JBIG2 is usually 15 to 30% smaller. Only generic
    /// region coding is written, which is lossless, with no shared JBIG2Globals: symbol coding,
    /// which matches glyphs by likeness, could show one character in place of another. Every
    /// JBIG2 image is decoded again before it is kept, and the proof checks every pixel. Needs PDF
    /// 1.4; output that stays at an earlier version gets Group 4 only. Default true.
    /// </summary>
    public bool UseJbig2 { get; init; } = true;

    /// <summary>
    /// Requantize JPEG images to this quality, 1 to 100, on the scale libjpeg and most image tools
    /// use. Not lossless: fine detail is traded for size. A table is never made finer than the
    /// image's own, so images already at or below this quality only get the lossless pass, and
    /// images used as soft masks always stay lossless. Null, the default, keeps every image lossless.
    /// Requires <see cref="RecompressStreams"/> and <see cref="OptimizeJpegImages"/>.
    /// </summary>
    public int? JpegQuality { get; init; }

    /// <summary>
    /// Downsample images drawn at more than 1.5 times this resolution, in pixels per inch, to
    /// this resolution. The resolution is measured from the largest size at which the image is
    /// drawn anywhere in the document: pages, forms, patterns, soft masks and annotation
    /// appearances. Images drawn in a way the measurement cannot follow, masks, images whose
    /// soft mask must match their size, and images other than 8 bit gray, RGB or CMYK, gray or
    /// RGB JPEG 2000, and bilevel images are left alone. The filter is Lanczos with three lobes.
    /// A downsampled JPEG is encoded again, at <see cref="JpegQuality"/> but never finer than it
    /// was; raw samples stay lossless after resampling unless <see cref="ConvertImagesToJpeg"/> is
    /// set. A downsampled JPEG 2000 image, which the optimizer does not write, becomes a JPEG at
    /// <see cref="JpegQuality"/>, or without it lossless samples. Bilevel images (raw, CCITT or
    /// JBIG2) stay bilevel: each new pixel takes the color that covers at least half of it, and
    /// strokes thinner than a new pixel are kept, so text stays legible; the result is CCITT Group
    /// 4, JBIG2 (see <see cref="UseJbig2"/>) or Flate, whichever is smaller. JPEG sources need
    /// <see cref="OptimizeJpegImages"/>. Not lossless. Null, the default, keeps every image's
    /// size. Requires <see cref="RecompressStreams"/>.
    /// </summary>
    public int? MaxImageDpi { get; init; }

    /// <summary>
    /// The resolution for bilevel images (raw, CCITT or JBIG2), in place of
    /// <see cref="MaxImageDpi"/>: those drawn above 1.5 times it are reduced to it, as
    /// <see cref="MaxImageDpi"/> describes. One bit per pixel loses a stroke's shape sooner than
    /// gray does, so scanned text wants more pixels than a photograph: 300 keeps it clean where 150
    /// makes it ragged. Null, the default, uses <see cref="MaxImageDpi"/> for them too. Not
    /// lossless. Requires <see cref="RecompressStreams"/>.
    /// </summary>
    public int? MaxBilevelImageDpi { get; init; }

    /// <summary>
    /// Re-encode raw 8 bit gray and RGB images, and gray and RGB JPEG 2000 images, as JPEG at
    /// <see cref="JpegQuality"/> when they look photographic. Bilevel, indexed, line art and screenshot-like images are recognized from
    /// their pixels and left lossless, and a JPEG is kept only when it is at least 10% smaller
    /// than the best lossless encoding. An image already stored in fewer bytes than the smallest
    /// JPEG of its size could take, at about two bits per 8 by 8 block, is not even decoded. Not
    /// lossless. Default false. Requires <see cref="JpegQuality"/> and <see cref="RecompressStreams"/>.
    /// </summary>
    public bool ConvertImagesToJpeg { get; init; }

    /// <summary>
    /// Reduce raw 8 bit RGB images to a palette of at most 256 colors, in their own color space, when
    /// that stays faithful and is at least 10% smaller than the best lossless encoding. This is what
    /// suits line art, technical drawings and screenshots, where JPEG rings around fine labels and
    /// barely saves anything: text and edges stay sharp. Each image gets the smallest palette that
    /// keeps <see cref="MinImageSsim"/>, or an SSIM of 0.97 without a gate, moves pixels 8 sample steps
    /// at most on average, and moves at most 1% of them by more than 32. With
    /// <see cref="ConvertImagesToJpeg"/> as well, the smaller of the two wins image by image, so set
    /// <see cref="MinImageSsim"/> to keep JPEG to images where it looks right. Images with /Decode,
    /// /Interpolate or a /Matte soft mask stay as they are. The proof expands the palette and checks
    /// the same bounds again. Not lossless unless an image already has that few colors. Default
    /// false. Requires <see cref="RecompressStreams"/>.
    /// </summary>
    public bool QuantizeImages { get; init; }

    /// <summary>
    /// The perceptual gate for every lossy image change: the minimum structural similarity
    /// (SSIM, on luminance, 0 to 1) a changed image must keep, or it stays as it was. A requantized
    /// JPEG is compared with the original; a downsampled image with the exact resample of the
    /// original, so the gate judges the encoding at the size the image is delivered rather than
    /// the resolution target. Around 0.95 keeps changes hard to see; 0.90 allows visible
    /// softening of fine texture. The proof checks the gate again on the output. Null, the
    /// default, sets no gate.
    /// </summary>
    public double? MinImageSsim { get; init; }

    /// <summary>
    /// Also write JPEG images progressively (spectral selection and successive approximation, as
    /// libjpeg's default progression) and keep that coding when it is smaller than optimized
    /// sequential coding, as it is for most photographs. Applies to every JPEG the optimizer
    /// writes, lossless or not. The decoded image is the same either way, and the proof compares
    /// DCT coefficients. Progressive JPEG needs PDF 1.3, so output that stays at an earlier version
    /// gets sequential coding only; arithmetic coding, which some readers lack, is never used.
    /// Has no effect without <see cref="RecompressStreams"/>. Default true.
    /// </summary>
    public bool ProgressiveJpeg { get; init; } = true;

    /// <summary>
    /// Re-read the candidate and prove it equivalent to the source before returning it.
    /// Disable only for trusted, already validated pipelines. Default true.
    /// </summary>
    public bool Verify { get; init; } = true;

    /// <summary>
    /// Allow flattening files with incremental updates into one revision. The visible document is
    /// unchanged, but earlier revisions are discarded. Default false.
    /// </summary>
    public bool AllowIncrementalUpdates { get; init; }

    /// <summary>
    /// Allow rewriting files whose cross-reference data or stream lengths had to be repaired.
    /// The output is a clean file, but another reader may have repaired the input differently. Default false.
    /// </summary>
    public bool AllowRepairedFiles { get; init; }

    /// <summary>Remove page thumbnail images (/Thumb). Viewers regenerate them. Not lossless. Default false.</summary>
    public bool RemovePageThumbnails { get; init; }

    /// <summary>
    /// Remove application private data (/PieceInfo), such as Illustrator editing state. Rendering is
    /// unaffected, but the authoring application loses its round-trip data. Default false.
    /// </summary>
    public bool RemovePieceInfo { get; init; }

    /// <summary>
    /// Remove usage rights (/Perms /UR3, or /UR in older files): the signature with which Adobe
    /// Reader enables features such as saving filled forms. A rewrite changes the signed bytes, so
    /// such a file is otherwise left as it is; with this option it is rewritten without them, and
    /// Reader opens it as a plain PDF instead of warning that the features are no longer available.
    /// Files that also carry a certification or another signature are still left as they are.
    /// Default false.
    /// </summary>
    public bool RemoveUsageRights { get; init; }

    /// <summary>
    /// Rewrite encrypted files that open with the empty user password, or with <see cref="Password"/>.
    /// The file is decrypted in memory, optimized and proven like any other, and written encrypted
    /// again exactly as it was: the same encryption dictionary (security handler revision, cipher,
    /// key length, permissions, crypt filters, /EncryptMetadata), the same file key and the same
    /// first /ID entry, so every password opens it as before and grants the same rights. The proof
    /// compares both files decrypted and checks that protection too. Files that need a password
    /// not given, use another security handler than the standard one (public key, custom), choose
    /// crypt filters per stream, are signed, or claim PDF/A conformance (which forbids encryption)
    /// are left as they are. False leaves every encrypted file as it is. Default true.
    /// </summary>
    public bool OptimizeEncryptedFiles { get; init; } = true;

    /// <summary>
    /// User or owner password for encrypted files, for <see cref="OptimizeEncryptedFiles"/>. When it
    /// does not open a file, or is null, the empty user password is tried. The output is protected
    /// with the source's own passwords whichever one opened it. Kept out of <see cref="ToString"/>.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Worker threads for compression and verification, at most. Default: processor count. Fewer
    /// run when memory is short: each worker can hold several streams of up to
    /// <see cref="MaxDecodedStreamBytes"/>, so the count is also capped by half the memory the
    /// runtime reports available (a container's limit included) divided by three such streams.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Ceilings on the work one optimization may cause, all passes together: input size, decoded
    /// bytes, image pixels, content operators and time. A run that reaches one writes the input
    /// unchanged with <see cref="OptimizationDisposition.LimitExceeded"/>; when time runs out
    /// during lossy image work, the lossless rewrite is written instead (in memory; streaming mode
    /// writes the input). <see cref="MaxInputBytes"/> applies as well. Default <see cref="PdfLimits.Default"/>.
    /// </summary>
    public PdfLimits Limits { get; init; } = PdfLimits.Default;

    /// <summary>
    /// Leave every unfiltered metadata stream unfiltered, not only in PDF/A-1. Set when a document
    /// is saved as PDF/A, whose XMP packet the archive writer leaves uncompressed on purpose so
    /// archival tools read it without decoding.
    /// </summary>
    internal bool KeepMetadataUnfiltered { get; init; }

    /// <summary>True when some option may change images lossily.</summary>
    internal bool IsLossy => JpegQuality is not null || MaxImageDpi is not null || MaxBilevelImageDpi is not null || ConvertImagesToJpeg || QuantizeImages;

    /// <summary>These options with every lossy image change turned off.</summary>
    internal PdfOptimizerOptions Lossless => this with
    {
        JpegQuality = null,
        MaxImageDpi = null,
        MaxBilevelImageDpi = null,
        ConvertImagesToJpeg = false,
        QuantizeImages = false,
        MinImageSsim = null,
    };

    /// <summary>
    /// Workers that fit in memory: half of what the runtime reports available, three streams at
    /// the decode ceiling each, and never more than asked.
    /// </summary>
    internal int Workers
    {
        get
        {
#if NET8_0_OR_GREATER
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0) return MaxDegreeOfParallelism;
            long perWorker = Math.Max(1, 3 * MaxDecodedStreamBytes);
            return (int)Math.Max(1, Math.Min(MaxDegreeOfParallelism, available / 2 / perWorker));
#else
            return MaxDegreeOfParallelism;
#endif
        }
    }

    internal CompressionLevel ZlibLevel => Compression switch
    {
        CompressionEffort.Fast => CompressionLevel.Fastest,
        CompressionEffort.Balanced => CompressionLevel.Optimal,
        _ => Compat.MaximumCompression,
    };

    internal void Validate()
    {
        if (MinSavingPercent is < 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(MinSavingPercent));
        if (Streaming && Linearize) throw new ArgumentException("Linearize cannot be combined with Streaming: a linearized file is laid out whole, in memory.", nameof(Streaming));
        if (MaxInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        if (MaxDecodedStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDecodedStreamBytes));
        if (ObjectsPerStream is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(ObjectsPerStream));
        if (MaxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        if (JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
        if (JpegQuality is not null && !(RecompressStreams && OptimizeJpegImages))
        {
            throw new ArgumentException("JpegQuality needs RecompressStreams and OptimizeJpegImages enabled.", nameof(JpegQuality));
        }

        if (MaxImageDpi is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxImageDpi));
        if (MaxBilevelImageDpi is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxBilevelImageDpi));
        if (MinImageSsim is { } ssim && (double.IsNaN(ssim) || ssim is <= 0 or > 1)) throw new ArgumentOutOfRangeException(nameof(MinImageSsim));
        if (MaxImageDpi is not null && !RecompressStreams) throw new ArgumentException("MaxImageDpi needs RecompressStreams enabled.", nameof(MaxImageDpi));
        if (MaxBilevelImageDpi is not null && !RecompressStreams) throw new ArgumentException("MaxBilevelImageDpi needs RecompressStreams enabled.", nameof(MaxBilevelImageDpi));
        if (QuantizeImages && !RecompressStreams) throw new ArgumentException("QuantizeImages needs RecompressStreams enabled.", nameof(QuantizeImages));
        if (ConvertImagesToJpeg && (JpegQuality is null || !RecompressStreams))
        {
            throw new ArgumentException("ConvertImagesToJpeg needs JpegQuality set and RecompressStreams enabled.", nameof(ConvertImagesToJpeg));
        }

        if (Limits is null) throw new ArgumentNullException(nameof(Limits));
        Limits.Validate();
    }

    // The password stays out of logs that print the options. Every other public property is
    // listed, as the compiler would; a test checks none is missing.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        var p = System.Globalization.CultureInfo.InvariantCulture;
        builder.Append("MinSavingPercent = ").Append(MinSavingPercent.ToString(p));
        builder.Append(", MaxInputBytes = ").Append(MaxInputBytes);
        builder.Append(", Streaming = ").Append(Streaming);
        builder.Append(", MaxDecodedStreamBytes = ").Append(MaxDecodedStreamBytes);
        builder.Append(", Compression = ").Append(Compression);
        builder.Append(", UseObjectStreams = ").Append(UseObjectStreams);
        builder.Append(", ObjectsPerStream = ").Append(ObjectsPerStream);
        builder.Append(", DeduplicateObjects = ").Append(DeduplicateObjects);
        builder.Append(", PruneUnusedResources = ").Append(PruneUnusedResources);
        builder.Append(", CompactContentStreams = ").Append(CompactContentStreams);
        builder.Append(", MergeFontSubsets = ").Append(MergeFontSubsets);
        builder.Append(", SubsetFonts = ").Append(SubsetFonts);
        builder.Append(", ConvertType1Fonts = ").Append(ConvertType1Fonts);
        builder.Append(", Linearize = ").Append(Linearize);
        builder.Append(", RecompressStreams = ").Append(RecompressStreams);
        builder.Append(", UseImagePredictors = ").Append(UseImagePredictors);
        builder.Append(", OptimizeJpegImages = ").Append(OptimizeJpegImages);
        builder.Append(", OptimizeBilevelImages = ").Append(OptimizeBilevelImages);
        builder.Append(", UseJbig2 = ").Append(UseJbig2);
        builder.Append(", JpegQuality = ").Append(JpegQuality?.ToString(p) ?? "null");
        builder.Append(", MaxImageDpi = ").Append(MaxImageDpi?.ToString(p) ?? "null");
        builder.Append(", MaxBilevelImageDpi = ").Append(MaxBilevelImageDpi?.ToString(p) ?? "null");
        builder.Append(", ConvertImagesToJpeg = ").Append(ConvertImagesToJpeg);
        builder.Append(", QuantizeImages = ").Append(QuantizeImages);
        builder.Append(", MinImageSsim = ").Append(MinImageSsim?.ToString(p) ?? "null");
        builder.Append(", ProgressiveJpeg = ").Append(ProgressiveJpeg);
        builder.Append(", Verify = ").Append(Verify);
        builder.Append(", AllowIncrementalUpdates = ").Append(AllowIncrementalUpdates);
        builder.Append(", AllowRepairedFiles = ").Append(AllowRepairedFiles);
        builder.Append(", RemovePageThumbnails = ").Append(RemovePageThumbnails);
        builder.Append(", RemovePieceInfo = ").Append(RemovePieceInfo);
        builder.Append(", RemoveUsageRights = ").Append(RemoveUsageRights);
        builder.Append(", OptimizeEncryptedFiles = ").Append(OptimizeEncryptedFiles);
        builder.Append(", Password = ").Append(Password is null ? "null" : "(set)");
        builder.Append(", MaxDegreeOfParallelism = ").Append(MaxDegreeOfParallelism);
        builder.Append(", Limits = ").Append(Limits);
        return true;
    }
}
