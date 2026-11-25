using System.Diagnostics;

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
/// </summary>
internal static class Oracle
{
    public const int AllocationFactor = 64;

    public const long AllocationBase = 64L << 20;

    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(2);

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
                target.Run(input);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Only the side effects of the call matter here.
            }
        }
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
            target.Run(input);
        }
        catch (Exception ex) when (target.Documents(ex))
        {
            // Bad input reported the way the entry point says it is.
        }
        catch (Exception ex)
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
