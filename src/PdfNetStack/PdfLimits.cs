namespace PdfNetStack;

/// <summary>Which limit a <see cref="PdfLimitExceededException"/> or a <see cref="PdfDiagnostic"/> names.</summary>
public enum PdfLimitKind
{
    /// <summary>A bound that is not one of the <see cref="PdfLimits"/>, such as a structure nested too deep.</summary>
    Other,

    /// <summary><see cref="PdfLimits.MaxInputBytes"/>.</summary>
    InputBytes,

    /// <summary><see cref="PdfLimits.MaxTotalDecodedBytes"/>.</summary>
    TotalDecodedBytes,

    /// <summary><see cref="PdfLimits.MaxTotalImagePixels"/>.</summary>
    TotalImagePixels,

    /// <summary><see cref="PdfLimits.MaxOperationsPerDocument"/>.</summary>
    OperationsPerDocument,

    /// <summary><see cref="PdfLimits.MaxTextCharsPerPage"/>.</summary>
    TextCharsPerPage,

    /// <summary><see cref="PdfLimits.Timeout"/>.</summary>
    Timeout,

    /// <summary>
    /// The fixed bounds on one page's content: its decoded bytes (at most
    /// <see cref="PdfLoadOptions.MaxDecodedStreamBytes"/>, all its streams and forms together),
    /// the tokens and the operators read from it.
    /// </summary>
    PageContent,
}

/// <summary>
/// Ceilings on the work one document may cause, counted across the whole document rather than per
/// object: a small file can name the same costly stream from every page, or hold thousands of
/// streams each just under the per stream ceiling. Set them with <see cref="PdfLoadOptions.Limits"/>
/// for opening, reading text and images, and <see cref="PdfInspector"/>, and with
/// <see cref="PdfOptimizerOptions.Limits"/> for the optimizer. The per object ceilings stay where
/// they were: <see cref="PdfLoadOptions.MaxDecodedStreamBytes"/> and <see cref="PdfLoadOptions.MaxImagePixels"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every limit is checked before the memory it guards is allocated. A document opened with
/// <see cref="PdfDocument.Open(Stream, PdfLoadOptions)"/> spends its budgets over its whole life:
/// text read twice is decoded twice. A document built with <see cref="PdfDocument.Create"/> has
/// no file to scale from and only the per page bounds apply to it. When a budget runs out, what needs it is skipped as a damaged
/// object would be (a page draws nothing more, an image raises
/// <see cref="PdfLimitExceededException"/>), and <see cref="PdfDocument.Diagnostics"/> says so. The
/// optimizer writes the input unchanged with <see cref="OptimizationDisposition.LimitExceeded"/>.
/// </para>
/// <para>
/// <see cref="Default"/> stops only what no ordinary document needs; for files from users of a
/// service use <see cref="Untrusted"/>, or start from it with a <c>with</c> expression.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var options = new PdfLoadOptions { Limits = PdfLimits.Untrusted with { Timeout = TimeSpan.FromSeconds(10) } };
/// using PdfDocument doc = PdfDocument.Open(upload, options);
/// string text = doc.Pages[0].ExtractText();
/// foreach (PdfDiagnostic warning in doc.Diagnostics) logger.LogWarning("{Warning}", warning.Message);
/// </code>
/// </example>
public sealed record PdfLimits
{
    // The scaled budgets: what any file may spend, plus an amount per byte of the file. Decoding
    // amplifies a few hundred times in real documents (an all white page image, repeated forms),
    // and the optimizer decodes each stream in several passes; a decompression bomb amplifies
    // millions of times, so a budget that grows with the file stops bombs without refusing large
    // ordinary files.
    private const long DecodedAllowance = 1L << 30;
    private const long DecodedPerInputByte = 4096;
    private const long PixelAllowance = 1L << 30;
    private const long PixelsPerInputByte = 4096;

    /// <summary>
    /// Limits that stop only what no ordinary document needs: no ceiling on the input, decoded
    /// bytes and image pixels scaled with the size of the file, 500 million content operators,
    /// 4 million characters a page and no time limit.
    /// </summary>
    public static PdfLimits Default { get; } = new();

    /// <summary>
    /// Limits for files from users of a service: 256 MiB of input, 1 GiB decoded and 2^29 image
    /// pixels in total, 25 million content operators, 250,000 characters a page and 60 seconds a
    /// call. Each is at most what <see cref="Default"/> allows for any file.
    /// </summary>
    public static PdfLimits Untrusted { get; } = new()
    {
        MaxInputBytes = 256L << 20,
        MaxTotalDecodedBytes = 1L << 30,
        MaxTotalImagePixels = 1L << 29,
        MaxOperationsPerDocument = 25_000_000,
        MaxTextCharsPerPage = 250_000,
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Largest file accepted, in bytes. A longer file or stream is refused with
    /// <see cref="PdfLimitExceededException"/> before it is read into memory; the optimizer returns
    /// it unchanged. <see cref="PdfOptimizerOptions.MaxInputBytes"/> applies as well. Default: no limit.
    /// </summary>
    public long MaxInputBytes { get; init; } = long.MaxValue;

    /// <summary>
    /// Most bytes decoding may produce for one document, all streams and filter layers together:
    /// page content however often it is drawn, fonts, images, object streams, and in the optimizer
    /// every pass. Null, the default, scales with the file: 1 GiB plus 4096 bytes per byte of it.
    /// </summary>
    public long? MaxTotalDecodedBytes { get; init; }

    /// <summary>
    /// Most pixels the images of one document may decode to, all images together, counted before
    /// each is allocated. Null, the default, scales with the file: 2^30 (four images at the
    /// largest size one may have) plus 4096 pixels per byte of it.
    /// <see cref="PdfLoadOptions.MaxImagePixels"/> still bounds each image.
    /// </summary>
    public long? MaxTotalImagePixels { get; init; }

    /// <summary>
    /// Most content operators one document may run, all pages, forms and calls together. Each
    /// page is also bounded on its own. Default 500 million, a few tens of seconds of work.
    /// </summary>
    public long MaxOperationsPerDocument { get; init; } = 500_000_000;

    /// <summary>
    /// Most characters text extraction returns for one page; letters past it are left out.
    /// Default 4 million, far beyond any printed page.
    /// </summary>
    public int MaxTextCharsPerPage { get; init; } = 4_000_000;

    /// <summary>
    /// Longest one call may run: opening a document, one extraction (<see cref="PdfPage.ExtractText"/>,
    /// <see cref="PdfPage.GetWords"/>, <see cref="PdfPage.GetLetters"/>, <see cref="PdfPage.GetImages"/>),
    /// one inspection or one optimization. Past it the call raises
    /// <see cref="PdfLimitExceededException"/>, except the optimizer, which writes its lossless
    /// result when time runs out during lossy work and otherwise the input unchanged.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, the default, sets no limit.
    /// </summary>
    public TimeSpan Timeout { get; init; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>The decoded byte budget for a file of <paramref name="inputBytes"/> bytes.</summary>
    internal long DecodedBytesFor(long inputBytes) => MaxTotalDecodedBytes ?? Scaled(DecodedAllowance, DecodedPerInputByte, inputBytes);

    /// <summary>The image pixel budget for a file of <paramref name="inputBytes"/> bytes.</summary>
    internal long ImagePixelsFor(long inputBytes) => MaxTotalImagePixels ?? Scaled(PixelAllowance, PixelsPerInputByte, inputBytes);

    internal bool HasTimeout => Timeout != System.Threading.Timeout.InfiniteTimeSpan;

    internal void Validate()
    {
        if (MaxInputBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        if (MaxTotalDecodedBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxTotalDecodedBytes));
        if (MaxTotalImagePixels < 1) throw new ArgumentOutOfRangeException(nameof(MaxTotalImagePixels));
        if (MaxOperationsPerDocument < 1) throw new ArgumentOutOfRangeException(nameof(MaxOperationsPerDocument));
        if (MaxTextCharsPerPage < 1) throw new ArgumentOutOfRangeException(nameof(MaxTextCharsPerPage));
        if (Timeout <= TimeSpan.Zero && HasTimeout) throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "The timeout must be positive, or Timeout.InfiniteTimeSpan for none.");
    }

    private static long Scaled(long allowance, long perByte, long inputBytes) =>
        inputBytes >= (long.MaxValue - allowance) / perByte ? long.MaxValue : allowance + (perByte * Math.Max(0, inputBytes));
}
