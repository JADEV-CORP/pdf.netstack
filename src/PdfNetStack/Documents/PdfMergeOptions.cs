namespace PdfNetStack;

/// <summary>
/// Options for <see cref="PdfDocument.MergeAsync(IEnumerable{Stream}, Stream, PdfMergeOptions?, IProgress{PdfProgress}?, CancellationToken)"/>:
/// how each input is opened and handed back, and how the merged document is saved.
/// </summary>
public sealed record PdfMergeOptions
{
    /// <summary>Shared default instance.</summary>
    public static PdfMergeOptions Default { get; } = new();

    /// <summary>How inputs open when <see cref="LoadOptions"/> is null.</summary>
    internal static PdfLoadOptions OnDemand { get; } = new() { Mode = PdfLoadMode.OnDemand };

    /// <summary>
    /// Password, limits and mode for every input. Null opens each on demand with the default
    /// limits: only the objects its pages use are parsed, and an input over
    /// <see cref="PdfLoadOptions.SpoolThreshold"/> is copied to a temporary file deleted once its
    /// pages are merged.
    /// </summary>
    public PdfLoadOptions? LoadOptions { get; init; }

    /// <summary>How the merged document is saved; null for <see cref="PdfSaveOptions.Default"/>.</summary>
    public PdfSaveOptions? SaveOptions { get; init; }

    /// <summary>
    /// Leave each input stream open once it is read, for the caller to dispose. Default false: each
    /// input is disposed as soon as it is read, so inputs opened as the enumeration reaches them
    /// are open one at a time.
    /// </summary>
    public bool LeaveInputsOpen { get; init; }
}
