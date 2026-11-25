#if NET
using PdfNetStack.Fuzz;

namespace PdfNetStack.Tests;

/// <summary>
/// The fuzzing harness (tests/PdfNetStack.Fuzz) without libFuzzer: every target's seeds pass the
/// oracle, and the oracle reports each kind of finding. The oracle counts the allocations of the
/// whole process and wall clock time, so these tests do not run beside others.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FuzzHarnessTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<object[]> Targets => FuzzTargets.All.Select(t => new object[] { t.Name });

    [TestMethod]
    [DynamicData(nameof(Targets))]
    public void SeedsPassTheOracle(string name)
    {
        FuzzTarget target = FuzzTargets.Find(name)!;
        var seeds = target.Seeds(Fixtures).ToList();
        Assert.IsNotEmpty(seeds, name + " has no seeds");

        Oracle.WarmUp(target, seeds[0].Data);
        foreach ((string file, byte[] data) in seeds)
        {
            Assert.IsLessThanOrEqualTo(Seeds.MaxBytes, data.Length, file);
            Oracle.Check(target, data);
        }
    }

    /// <summary>Inputs the fuzzer found, saved as Fixtures/Fuzz/&lt;target&gt;/&lt;file&gt; once fixed.</summary>
    public static IEnumerable<object[]> Findings =>
        Directory.GetDirectories(Path.Combine(Fixtures, "Fuzz"))
            .SelectMany(dir => Directory.GetFiles(dir).Select(file => new object[] { Path.GetFileName(dir), Path.GetFileName(file) }))
            .OrderBy(row => (string)row[0], StringComparer.Ordinal)
            .ThenBy(row => (string)row[1], StringComparer.Ordinal);

    [TestMethod]
    [DynamicData(nameof(Findings))]
    public void PastFindingsPassTheOracle(string name, string file)
    {
        FuzzTarget target = FuzzTargets.Find(name)!;
        Assert.IsNotNull(target, "Fixtures/Fuzz/" + name + " is not a target");
        byte[] input = File.ReadAllBytes(Path.Combine(Fixtures, "Fuzz", name, file));

        // The input warms up first, as under libFuzzer, so loading code is not counted.
        Oracle.WarmUp(target, input);
        Oracle.Check(target, input);
    }

    [TestMethod]
    public void TargetNamesAreUnique()
    {
        var names = FuzzTargets.All.Select(t => t.Name).ToList();
        CollectionAssert.AllItemsAreUnique(names);
        Assert.IsNotNull(FuzzTargets.Find("optimize"));
    }

    [TestMethod]
    public void DocumentedExceptionsAreNotFindings()
    {
        FuzzTarget open = FuzzTargets.Find("open")!;
        Oracle.Check(open, "not a PDF"u8.ToArray());
        Oracle.Check(open, []);
    }

    [TestMethod]
    public void UndocumentedExceptionIsAFinding()
    {
        FuzzTarget target = Synthetic(_ => throw new IndexOutOfRangeException("boom"), typeof(FormatException));
        FuzzFinding finding = Assert.ThrowsExactly<FuzzFinding>(() => Oracle.Check(target, [1, 2, 3]));
        StringAssert.Contains(finding.Message, "IndexOutOfRangeException");
        Assert.IsInstanceOfType<IndexOutOfRangeException>(finding.InnerException);

        // A documented type, or one derived from it, is how bad input is reported.
        Oracle.Check(Synthetic(_ => throw new PdfNetStack.Fonts.FontFormatException("bad"), typeof(FormatException)), [1]);
    }

    [TestMethod]
    public void AllocationOverTheBudgetIsAFinding()
    {
        byte[] input = new byte[1024];
        long budget = Oracle.Budget(input.Length);
        Assert.AreEqual((64 * 1024) + (64L << 20), budget);

        FuzzTarget target = Synthetic(data =>
        {
            // Twice the budget, in chunks, as a decoder that inflates without a limit would.
            for (long total = 0; total < 2 * budget; total += 1 << 20) GC.KeepAlive(new byte[1 << 20]);
        });
        FuzzFinding finding = Assert.ThrowsExactly<FuzzFinding>(() => Oracle.Check(target, input));
        StringAssert.Contains(finding.Message, "budget");
    }

    [TestMethod]
    public void SlowInputIsAFinding()
    {
        FuzzTarget target = Synthetic(_ => Thread.Sleep(Oracle.TimeLimit + TimeSpan.FromMilliseconds(200)));
        FuzzFinding finding = Assert.ThrowsExactly<FuzzFinding>(() => Oracle.Check(target, [0]));
        StringAssert.Contains(finding.Message, "limit");
    }

    [TestMethod]
    public void HangingInputIsAFinding()
    {
        using var release = new ManualResetEventSlim();
        FuzzTarget target = Synthetic(_ => release.Wait());
        TimeSpan saved = Oracle.HangLimit;
        Oracle.HangLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            FuzzFinding finding = Assert.ThrowsExactly<FuzzFinding>(() => Oracle.Check(target, [0]));
            StringAssert.Contains(finding.Message, "did not finish within");
            Assert.ThrowsExactly<FuzzFinding>(() => Oracle.WarmUp(target));
        }
        finally
        {
            Oracle.HangLimit = saved;
            release.Set();
        }
    }

    private static FuzzTarget Synthetic(Action<byte[]> run, params Type[] documented) => new()
    {
        Name = "synthetic",
        Run = run,
        Documented = documented,
        Seeds = _ => [],
    };
}
#endif
