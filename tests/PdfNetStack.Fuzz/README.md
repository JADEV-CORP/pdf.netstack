# PdfNetStack.Fuzz

Fuzzing harness for every entry point that reads untrusted bytes, built on
[SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer. Nothing here ships.

| Target | Entry point |
|---|---|
| `open` | `PdfDocument.Open` in memory, then every page's text, annotations and images, outlines, form fields, attachments |
| `open-on-demand` | `PdfDocument.OpenOnDemand` on a stream, then the same walk |
| `optimize` | `PdfOptimizer.Optimize`, incremental and repaired files allowed, one thread |
| `verify-signatures` | `PdfSigner.Verify` |
| `trusted-list` | `PdfTrustedList.Load` |
| `image` | `PdfImage.Load` |
| `opentype` | `PdfOpenTypeFont.Load`, then shaping and measuring text in many scripts |
| `html` | `HtmlConverter.ConvertToPdf`, no resource loaded |
| `jbig2`, `jpx`, `ccitt`, `cmap`, `content` | The internal decoders and parsers, through `InternalsVisibleTo` |

The oracle (`Oracle.cs`) reports an input that makes a target:

- throw an exception type its entry point does not document for bad input;
- allocate more than 64 times the input plus 64 MiB;
- run for more than 2 seconds.

## Run it

Replay files or folders through a target, on any platform:

```bash
dotnet run --project tests/PdfNetStack.Fuzz -c Release -- list
dotnet run --project tests/PdfNetStack.Fuzz -c Release -- seeds open tests/PdfNetStack.Tests/Fixtures seeds/open
dotnet run --project tests/PdfNetStack.Fuzz -c Release -- replay open seeds/open crash-1234
```

Fuzz with libFuzzer (Linux), as `.github/workflows/fuzz.yml` does:

```bash
dotnet publish tests/PdfNetStack.Fuzz -c Release -f net10.0 -o fuzz
dotnet fuzz/PdfNetStack.Fuzz.dll seeds open tests/PdfNetStack.Tests/Fixtures corpus
dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path .tools
.tools/sharpfuzz fuzz/PdfNetStack.dll && .tools/sharpfuzz fuzz/PdfNetStack.Html.dll
PDFNETSTACK_FUZZ_TARGET=open ./libfuzzer-dotnet --target_path=dotnet --target_arg=fuzz/PdfNetStack.Fuzz.dll \
  -max_total_time=300 -timeout=30 -max_len=262144 corpus
```

`libfuzzer-dotnet` is the release binary of [Metalnem/libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet).
Write the seeds before instrumenting: some targets build them through the library, and instrumented
code run outside libFuzzer has no coverage map and crashes with an access violation.

`FuzzHarnessTests` replays every target's seeds through the oracle on each test run, and every input
under `tests/PdfNetStack.Tests/Fixtures/Fuzz/<target>/`. Fix a finding by adding its input there
first, so the test fails, then fixing the library.
