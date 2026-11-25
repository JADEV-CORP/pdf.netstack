using SharpFuzz;

namespace PdfNetStack.Fuzz;

/// <summary>
/// The fuzzing harness. Under libFuzzer (libfuzzer-dotnet, Linux), with the target named by the
/// PDFNETSTACK_FUZZ_TARGET variable, it runs every input through the target and the oracle and
/// lets a finding escape as a crash, which libFuzzer saves. Without libFuzzer it replays files,
/// so findings can be reproduced, and the same check runs in the unit tests, on any platform.
/// </summary>
internal static class Program
{
    private const string Usage = """
        PdfNetStack.Fuzz: fuzzing harness

        Under libFuzzer (Linux, after instrumenting the libraries with sharpfuzz):
          PDFNETSTACK_FUZZ_TARGET=<target> libfuzzer-dotnet --target_path=dotnet --target_arg=PdfNetStack.Fuzz.dll <corpus>
          PDFNETSTACK_FUZZ_TARGET=<target> dotnet PdfNetStack.Fuzz.dll <input>   one input, as libFuzzer runs it

        Without it:
          PdfNetStack.Fuzz replay <target> <file or folder>...   run inputs through the oracle; exit 1 on a finding
          PdfNetStack.Fuzz seeds <target> <fixtures> <out>      write the target's starting inputs
          PdfNetStack.Fuzz list                                 list the targets
        """;

    public static int Main(string[] args)
    {
        // libFuzzer starts the harness without arguments. With one file it runs that input once,
        // through the instrumented libraries, which reproduces a finding as libFuzzer saw it.
        if (Environment.GetEnvironmentVariable("PDFNETSTACK_FUZZ_TARGET") is { Length: > 0 } name && (args.Length == 0 || File.Exists(args[0])))
        {
            FuzzTarget target = FuzzTargets.Find(name) ?? throw new ArgumentException("Unknown target: " + name);

            // Instrumented code needs the coverage map SharpFuzz sets up inside Run, so the warm up
            // happens on the first input rather than before.
            bool warm = false;
            Oracle.HangLimit = Timeout.InfiniteTimeSpan;
            Fuzzer.LibFuzzer.Run(input =>
            {
                byte[] data = input.ToArray();
                if (!warm)
                {
                    Oracle.WarmUp(target, data);
                    warm = true;
                }

                Oracle.Check(target, data);
            });
            return 0;
        }

        switch (args.FirstOrDefault())
        {
            case "list":
                foreach (FuzzTarget t in FuzzTargets.All) Console.WriteLine(t.Name);
                return 0;
            case "seeds" when args.Length == 4 && FuzzTargets.Find(args[1]) is { } t:
                Directory.CreateDirectory(args[3]);
                int written = 0;
                foreach ((string file, byte[] data) in t.Seeds(args[2]))
                {
                    File.WriteAllBytes(Path.Combine(args[3], file), data);
                    written++;
                }

                Console.WriteLine($"{t.Name}: {written} seed(s) in {args[3]}");
                return 0;
            case "replay" when args.Length >= 3 && FuzzTargets.Find(args[1]) is { } t:
                return Replay(t, args[2..]);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>Runs every file through the oracle and prints one line per finding.</summary>
    private static int Replay(FuzzTarget target, string[] paths)
    {
        var files = paths.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p).Order(StringComparer.Ordinal) : [p]).ToList();

        // The same warm up as under libFuzzer, so a replay measures what the fuzzer measured.
        Oracle.WarmUp(target, files.Count > 0 ? File.ReadAllBytes(files[0]) : null);
        int inputs = 0, findings = 0;
        foreach (string path in files)
        {
            inputs++;
            try
            {
                Oracle.Check(target, File.ReadAllBytes(path));
            }
            catch (FuzzFinding finding)
            {
                findings++;
                Console.WriteLine($"FINDING {path}: {finding.Message}");
                if (finding.InnerException is { } inner) Console.WriteLine(inner.StackTrace);
            }
        }

        Console.WriteLine($"{target.Name}: {inputs} input(s), {findings} finding(s)");
        return findings == 0 ? 0 : 1;
    }
}
