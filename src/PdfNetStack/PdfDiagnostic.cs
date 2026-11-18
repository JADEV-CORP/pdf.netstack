namespace PdfNetStack;

/// <summary>What a <see cref="PdfDiagnostic"/> reports.</summary>
public enum PdfDiagnosticKind
{
    /// <summary>A limit stopped work early; <see cref="PdfDiagnostic.Limit"/> says which.</summary>
    LimitReached,

    /// <summary>
    /// Something damaged or unreadable was left out and the rest went on: a stream that cannot be
    /// decoded, a branch of the page tree that loops or nests too deep.
    /// </summary>
    Skipped,

    /// <summary>The file needed repair to be read: rebuilt cross-reference data, corrected stream lengths, a page count that disagrees with the pages.</summary>
    Repaired,
}

/// <summary>
/// A warning about a document that was still read: a limit that cut work short, something
/// skipped, a repair. Collected in <see cref="PdfDocument.Diagnostics"/>,
/// <see cref="PdfInspection.Diagnostics"/> and <see cref="PdfOptimizationResult.Diagnostics"/>,
/// and passed to <see cref="PdfLoadOptions.OnWarning"/> as they happen.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">A sentence for a log.</param>
public sealed record PdfDiagnostic(PdfDiagnosticKind Kind, string Message)
{
    /// <summary>The limit reached, for <see cref="PdfDiagnosticKind.LimitReached"/>; null otherwise.</summary>
    public PdfLimitKind? Limit { get; init; }

    /// <summary>The object concerned, when there is one.</summary>
    public int? ObjectNumber { get; init; }

    /// <summary>The page concerned, from 0, when there is one.</summary>
    public int? PageIndex { get; init; }
}

/// <summary>
/// Collects a document's diagnostics. Bounded, as hostile input can damage a million objects:
/// past <see cref="MaxEntries"/> one last entry says how many more were dropped. The same
/// message for the same object and page is kept once. Thread safe: the optimizer reports from workers.
/// </summary>
internal sealed class PdfDiagnostics
{
    public const int MaxEntries = 1000;

    private readonly object _gate = new();
    private readonly List<PdfDiagnostic> _entries = [];
    private readonly HashSet<(string, int?, int?)> _seen = [];
    private readonly Action<PdfDiagnostic>? _onWarning;
    private int _dropped;

    public PdfDiagnostics(Action<PdfDiagnostic>? onWarning = null) => _onWarning = onWarning;

    /// <summary>A copy of the entries so far.</summary>
    public IReadOnlyList<PdfDiagnostic> Snapshot()
    {
        lock (_gate)
        {
            if (_dropped == 0) return [.. _entries];
            return [.. _entries, new PdfDiagnostic(PdfDiagnosticKind.Skipped, Compat.Format("{0} further warning(s) were dropped.", _dropped))];
        }
    }

    public void Add(PdfDiagnostic diagnostic)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                _dropped++;
                return;
            }

            if (!_seen.Add((diagnostic.Message, diagnostic.ObjectNumber, diagnostic.PageIndex))) return;

            _entries.Add(diagnostic);
        }

        // Outside the lock: the callback may log slowly, or read the document.
        _onWarning?.Invoke(diagnostic);
    }

    public void LimitReached(PdfLimitKind limit, string message, int? pageIndex = null) =>
        Add(new PdfDiagnostic(PdfDiagnosticKind.LimitReached, message) { Limit = limit, PageIndex = pageIndex });

    public void Skipped(string message, int? objectNumber = null, int? pageIndex = null) =>
        Add(new PdfDiagnostic(PdfDiagnosticKind.Skipped, message) { ObjectNumber = objectNumber, PageIndex = pageIndex });

    public void Repaired(string message) => Add(new PdfDiagnostic(PdfDiagnosticKind.Repaired, message));
}
