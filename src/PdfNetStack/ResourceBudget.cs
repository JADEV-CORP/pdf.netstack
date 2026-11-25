using System.Diagnostics;

namespace PdfNetStack;

/// <summary>
/// The document wide budgets of <see cref="PdfLimits"/> as one document, or one optimization,
/// spends them. Work is charged before the memory it needs is allocated, from any thread: the
/// optimizer decodes in parallel. Each limit is reported to the diagnostics once.
/// </summary>
internal sealed class ResourceBudget
{
    private long _decodedLeft;
    private long _pixelsLeft;
    private long _operationsLeft;
    private int _reported;

    public ResourceBudget(PdfLimits limits, long inputBytes, PdfDiagnostics diagnostics)
    {
        Limits = limits;
        Diagnostics = diagnostics;
        _decodedLeft = limits.DecodedBytesFor(inputBytes);
        _pixelsLeft = limits.ImagePixelsFor(inputBytes);
        _operationsLeft = limits.MaxOperationsPerDocument;
    }

    public PdfLimits Limits { get; }

    public PdfDiagnostics Diagnostics { get; }

    /// <summary>
    /// Large working arrays for this run, reused while it lasts and collected with it; null
    /// allocates each one. An optimization decodes the same sizes over and over, which a pool
    /// serves without new allocations, and the process-wide pool would keep them after the call.
    /// </summary>
    public System.Buffers.ArrayPool<byte>? Pool { get; init; }

    /// <summary>Decoded bytes still allowed; negative once the budget is spent.</summary>
    public long DecodedBytesLeft => Interlocked.Read(ref _decodedLeft);

    /// <summary>Charges decoded bytes about to be allocated.</summary>
    /// <exception cref="PdfLimitExceededException">The document has decoded its budget.</exception>
    public void ChargeDecodedBytes(long bytes)
    {
        if (bytes <= 0 || Interlocked.Add(ref _decodedLeft, -bytes) >= 0) return;
        throw Exceeded(PdfLimitKind.TotalDecodedBytes, "The document decodes more data than its limit allows (PdfLimits.MaxTotalDecodedBytes); the rest is not decoded.");
    }

    /// <summary>Gives back bytes charged ahead for capacity that was never written.</summary>
    public void Refund(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _decodedLeft, bytes);
    }

    /// <summary>Charges the pixels of an image about to be decoded.</summary>
    /// <exception cref="PdfLimitExceededException">The document's images have decoded their budget.</exception>
    public void ChargeImagePixels(long pixels)
    {
        if (pixels <= 0 || Interlocked.Add(ref _pixelsLeft, -pixels) >= 0) return;
        throw Exceeded(PdfLimitKind.TotalImagePixels, "The document's images decode to more pixels than its limit allows (PdfLimits.MaxTotalImagePixels); the rest are not decoded.");
    }

    /// <summary>Charges content operators about to run; false, reported once, when the budget is spent.</summary>
    public bool TryChargeOperations(long count)
    {
        if (Interlocked.Add(ref _operationsLeft, -count) >= 0) return true;
        Report(PdfLimitKind.OperationsPerDocument, "The document runs more content operators than its limit allows (PdfLimits.MaxOperationsPerDocument); content past it is not read.");
        return false;
    }

    /// <summary>
    /// Takes up to <paramref name="count"/> content operators before they run; returns how many it
    /// got, none (reported once) when the budget is spent. Give back what was not used with
    /// <see cref="RefundOperations"/>.
    /// </summary>
    public long ReserveOperations(long count)
    {
        while (true)
        {
            long left = Interlocked.Read(ref _operationsLeft);
            if (left <= 0)
            {
                Report(PdfLimitKind.OperationsPerDocument, "The document runs more content operators than its limit allows (PdfLimits.MaxOperationsPerDocument); content past it is not read.");
                return 0;
            }

            long taken = Math.Min(count, left);
            if (Interlocked.CompareExchange(ref _operationsLeft, left - taken, left) == left) return taken;
        }
    }

    /// <summary>Gives back operators reserved ahead that never ran.</summary>
    public void RefundOperations(long count)
    {
        if (count > 0) Interlocked.Add(ref _operationsLeft, count);
    }

    /// <summary>The exception for a limit reached, reported to the diagnostics first.</summary>
    public PdfLimitExceededException Exceeded(PdfLimitKind limit, string message)
    {
        Report(limit, message);
        return new PdfLimitExceededException(limit, message);
    }

    /// <summary>Reports <paramref name="limit"/> to the diagnostics, the first time only.</summary>
    public void Report(PdfLimitKind limit, string message)
    {
        int flag = 1 << (int)limit;
        int seen;
        do
        {
            seen = Volatile.Read(ref _reported);
            if ((seen & flag) != 0) return;
        }
        while (Interlocked.CompareExchange(ref _reported, seen | flag, seen) != seen);
        Diagnostics.LimitReached(limit, message);
    }
}

/// <summary>When one call must end, from <see cref="PdfLimits.Timeout"/>; the default never passes.</summary>
internal readonly struct Deadline
{
    private readonly long _end;

    private Deadline(long end) => _end = end;

    public static Deadline Start(PdfLimits limits)
    {
        if (!limits.HasTimeout) return default;
        long ticks = (long)Math.Min(long.MaxValue / 2.0, limits.Timeout.TotalSeconds * Stopwatch.Frequency);
        return new Deadline(Stopwatch.GetTimestamp() + Math.Max(1, ticks));
    }

    public bool Passed => _end != 0 && Stopwatch.GetTimestamp() > _end;

    /// <summary>Time left, for a <see cref="CancellationTokenSource"/>; infinite when there is no deadline.</summary>
    public TimeSpan Remaining => _end == 0
        ? Timeout.InfiniteTimeSpan
        : TimeSpan.FromSeconds(Math.Max(0, _end - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    /// <summary>
    /// A source the deadline cancels, for work that takes a token; null without a deadline, and
    /// when it is further off than a timer can count, about 24.8 days on .NET Framework.
    /// </summary>
    public CancellationTokenSource? NewTimer()
    {
        if (_end == 0) return null;
        TimeSpan left = Remaining;
        if (left.TotalMilliseconds > int.MaxValue) return null;
        if (left >= TimeSpan.FromMilliseconds(1)) return new CancellationTokenSource(left);

        // Under a millisecond left: .NET cancels such a source at once, but .NET Framework starts a
        // timer that fires later, so short work would finish past its deadline. Cancel it here.
        var spent = new CancellationTokenSource();
        spent.Cancel();
        return spent;
    }

    /// <exception cref="PdfLimitExceededException">The deadline has passed.</exception>
    public void ThrowIfPassed(ResourceBudget? budget)
    {
        if (!Passed) return;
        const string message = "The call ran past its time limit (PdfLimits.Timeout).";
        throw budget?.Exceeded(PdfLimitKind.Timeout, message) ?? new PdfLimitExceededException(PdfLimitKind.Timeout, message);
    }
}
