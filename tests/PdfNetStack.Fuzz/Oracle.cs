using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace PdfNetStack.Fuzz;

/// <summary>An input that broke one of the oracle's rules.</summary>
internal sealed class FuzzFinding(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Decides whether one input is a finding. Crashes alone miss the bugs that matter for a parser of
/// untrusted files, which are resource bugs, so three rules apply to every target:
/// <list type="bullet">
/// <item>it throws only the exception types its entry point documents for bad input;</item>
/// <item>it allocates at most <see cref="AllocationFactor"/> times the input plus <see cref="AllocationBase"/>;</item>
/// <item>it finishes within <see cref="TimeLimit"/>.</item>
/// </list>
/// A target that does not finish within <see cref="HangLimit"/> is a finding too, so a replay or a
/// test ends where libFuzzer's own -timeout would end the run.
/// </summary>
internal static class Oracle
{
    public const int AllocationFactor = 64;

    public const long AllocationBase = 64L << 20;

    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>How long a target may run before it counts as hung; infinite under libFuzzer, settable for the tests.</summary>
    public static TimeSpan HangLimit { get; internal set; } = TimeSpan.FromSeconds(30);

    // The converters run their own deep-stack thread; this one gets room for the targets that do not.
    private const int StackBytes = 64 << 20;

    /// <summary>The bytes one input may allocate.</summary>
    public static long Budget(int inputLength) => (AllocationFactor * (long)inputLength) + AllocationBase;

    /// <summary>
    /// Runs the target on an empty input, then on <paramref name="first"/> when given, and ignores
    /// the results, so that loading code and static tables is not charged to a real input. An
    /// empty input does not reach fonts, shaping tables or layout code: on a slow runner the first
    /// real HTML input took 2.7 s for that alone. A real hang still shows when it is measured.
    /// </summary>
    public static void WarmUp(FuzzTarget target, byte[]? first = null)
    {
        Run([]);
        if (first is not null) Run(first);

        void Run(byte[] input)
        {
            try
            {
                RunBounded(target, input);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not FuzzFinding)
            {
                // Only the side effects of the call matter here.
            }
        }
    }

    /// <summary>
    /// Runs the target on a background thread and waits for <see cref="HangLimit"/>. A hung target
    /// cannot be stopped, so its thread is left behind; being a background thread it does not keep the process alive.
    /// </summary>
    private static void RunBounded(FuzzTarget target, byte[] input)
    {
        // Under libFuzzer its own -timeout ends a hung run, and a thread per input would slow it.
        if (HangLimit == Timeout.InfiniteTimeSpan)
        {
            target.Run(input);
            return;
        }

        Exception? error = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    target.Run(input);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            },
            StackBytes)
        { IsBackground = true };
        worker.Start();
        if (!worker.Join(HangLimit)) throw new FuzzFinding($"{target.Name}: did not finish within {HangLimit.TotalSeconds:F1} s");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Runs one input and throws <see cref="FuzzFinding"/> when it breaks a rule.</summary>
    public static void Check(FuzzTarget target, byte[] input)
    {
        // All threads count: the optimizer and the decoders may use the thread pool.
        long before = GC.GetTotalAllocatedBytes(precise: false);
        var clock = Stopwatch.StartNew();
        Exception? undocumented = null;
        try
        {
            RunBounded(target, input);
        }
        catch (Exception ex) when (ex is not FuzzFinding && target.Documents(ex))
        {
            // Bad input reported the way the entry point says it is.
        }
        catch (Exception ex) when (ex is not FuzzFinding)
        {
            undocumented = ex;
        }

        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: false) - before;
        if (undocumented is not null)
        {
            throw new FuzzFinding($"{target.Name}: undocumented {undocumented.GetType().FullName}: {undocumented.Message}", undocumented);
        }

        long budget = Budget(input.Length);
        if (allocated > budget)
        {
            throw new FuzzFinding($"{target.Name}: allocated {allocated:N0} bytes for {input.Length:N0} bytes of input, over the {budget:N0} byte budget");
        }

        if (clock.Elapsed > TimeLimit)
        {
            throw new FuzzFinding($"{target.Name}: took {clock.Elapsed.TotalSeconds:F2} s, over the {TimeLimit.TotalSeconds:F0} s limit");
        }
    }
}
