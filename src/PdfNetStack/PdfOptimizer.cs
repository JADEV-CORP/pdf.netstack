using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>
/// PDF optimizer, lossless by default. Every entry point writes a valid PDF to the output: either
/// a smaller rewrite that has been proven equivalent to the input, or the input bytes unchanged.
/// With <see cref="PdfOptimizerOptions.Linearize"/> set, a proven linearized rewrite of a file
/// that is not linearized yet is written even when it is larger.
/// With <see cref="PdfOptimizerOptions.JpegQuality"/> set, JPEG images may be requantized; the
/// proof then checks that each such image changed by exactly that requantization and nothing else.
/// By default the input and the rewrite are held in memory; with
/// <see cref="PdfOptimizerOptions.Streaming"/> neither is, for files larger than memory.
/// </summary>
public static partial class PdfOptimizer
{
    /// <summary>Optimizes a PDF held in memory and writes the result to <paramref name="output"/>.</summary>
    /// <param name="input">The complete source PDF. It is never modified.</param>
    /// <param name="output">Receives the optimized PDF, or the original bytes when no safe saving exists.</param>
    /// <param name="options">
    /// Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null. With
    /// <see cref="PdfOptimizerOptions.Streaming"/>, the rewrite goes through a temporary file
    /// instead of memory.
    /// </param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    /// <returns>What was written and why.</returns>
    public static PdfOptimizationResult Optimize(
        ReadOnlyMemory<byte> input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(output, nameof(output));
        options ??= PdfOptimizerOptions.Default;
        options.Validate();
        if (options.Streaming) return OptimizeStreaming(new Source(input), output, options, cancellationToken);

        var clock = Stopwatch.StartNew();
        (ReadOnlyMemory<byte> written, PdfOptimizationResult result) = OptimizeInMemory(input, options, cancellationToken);
        output.Write(written.Span);
        return WithElapsed(result, clock);
    }

    /// <summary>
    /// The in-memory run: the bytes to write, which are the input itself when it is kept, and the
    /// result. Nothing is copied; each entry point writes or copies the bytes once, as it needs them.
    /// </summary>
    private static (ReadOnlyMemory<byte> Written, PdfOptimizationResult Result) OptimizeInMemory(ReadOnlyMemory<byte> input, PdfOptimizerOptions options, CancellationToken ct)
    {
        string sourceHash = Sha256Hex(input.Span);
        Outcome outcome = RunWithin(input, options, ct);

        ReadOnlyMemory<byte> written = outcome.Candidate ?? input;
        string outputHash = outcome.Candidate is null ? sourceHash : Sha256Hex(written.Span);
        OptimizationStatistics stats = outcome.Statistics ?? OptimizationStatistics.Empty(TimeSpan.Zero);
        var result = new PdfOptimizationResult(outcome.Disposition, input.Length, written.Length, sourceHash, outputHash,
            outcome.PageCount, outcome.Reason, stats)
        {
            Diagnostics = outcome.Diagnostics,
        };
        return (written, result);
    }

    /// <summary>The result with the time since <paramref name="clock"/> started, writing the output included.</summary>
    private static PdfOptimizationResult WithElapsed(PdfOptimizationResult result, Stopwatch clock) =>
        result with { Statistics = result.Statistics with { Elapsed = clock.Elapsed } };

    /// <summary>
    /// <see cref="RunOnce"/> within <see cref="PdfLimits.Timeout"/>. Lossy work is the slow part, so
    /// with a time limit the lossless rewrite is made first and kept should time run out while
    /// images are changed lossily; without a time limit the passes run once, as before.
    /// </summary>
    private static Outcome RunWithin(ReadOnlyMemory<byte> input, PdfOptimizerOptions options, CancellationToken ct)
    {
        var deadline = Deadline.Start(options.Limits);
        if (!options.IsLossy || !options.Limits.HasTimeout) return RunOnce(input, options, deadline, ct);

        Outcome lossless = RunOnce(input, options.Lossless, deadline, ct);
        if (lossless.Disposition == OptimizationDisposition.LimitExceeded) return lossless;
        Outcome lossy = RunOnce(input, options, deadline, ct);
        if (lossy.Disposition == OptimizationDisposition.Optimized) return lossy;

        // Only running out of time falls back to the lossless rewrite. A lossy pass that reached
        // another limit says so, and the call reports it.
        if (lossy.Disposition == OptimizationDisposition.LimitExceeded && !lossy.TimedOut) return lossy;
        if (lossless.Disposition != OptimizationDisposition.Optimized && !lossy.TimedOut) return lossy;
        string why = lossy.TimedOut ? "ran out of time (PdfLimits.Timeout)." : "were not kept: " + lossy.Reason;
        return lossless with
        {
            Reason = lossless.Reason + " Lossy image changes " + why,
            Diagnostics = [.. lossless.Diagnostics, .. lossy.Diagnostics],
        };
    }

    /// <summary>Optimizes a PDF and returns the output bytes together with the result.</summary>
    public static (byte[] Output, PdfOptimizationResult Result) Optimize(
        byte[] input,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(input, nameof(input));
        options ??= PdfOptimizerOptions.Default;
        if (options.Streaming)
        {
            using var output = new MemoryStream();
            PdfOptimizationResult streamed = Optimize(input, output, options, cancellationToken);
            return (output.ToArray(), streamed);
        }

        // The rewrite is copied once, to an array of its exact size; a kept input is copied too, so
        // the caller never holds the array it passed in.
        options.Validate();
        var clock = Stopwatch.StartNew();
        (ReadOnlyMemory<byte> written, PdfOptimizationResult result) = OptimizeInMemory(input, options, cancellationToken);
        return (written.ToArray(), WithElapsed(result, clock));
    }

    /// <summary>Reads <paramref name="input"/> to the end, optimizes it and writes to <paramref name="output"/>.</summary>
    /// <remarks>
    /// <paramref name="output"/> is written only asynchronously. By default the input is read
    /// asynchronously into memory. With <see cref="PdfOptimizerOptions.Streaming"/>, a
    /// <see cref="FileStream"/> or <see cref="MemoryStream"/> positioned at its start is read on
    /// demand where it is, and any other input is first copied, asynchronously, to a temporary file.
    /// </remarks>
    /// <param name="input">The source PDF, read from its current position to its end.</param>
    /// <param name="output">Receives the optimized PDF, or the input unchanged.</param>
    /// <param name="options">Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes.</param>
    public static async Task<PdfOptimizationResult> OptimizeAsync(
        Stream input,
        Stream output,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(input, nameof(input));
        Compat.ThrowIfNull(output, nameof(output));
        options ??= PdfOptimizerOptions.Default;
        options.Validate();
        if (options.Streaming)
        {
            return await OptimizeStreamingAsync(input, output, options, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
        // Buffer at most the smaller of the two input limits. Anything larger is streamed through
        // untouched without ever being held in memory, as the in-memory path would return it
        // unchanged anyway. A seekable input of known length gets its buffer at once, rather than
        // by doubling, or is passed through at once when it is known to be over the limit.
        long cap = InputCap(options);
        long known = input.CanSeek ? input.Length - input.Position : 0;
        if (known > cap)
        {
            long length = await PassThroughAsync(null, input, output, cancellationToken).ConfigureAwait(false);
            return OverLimit(length, options, cancellationToken);
        }

        using var buffer = new MemoryStream(known > 0 && known <= Compat.MaxArrayLength ? (int)known : 0);
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > cap)
            {
                long length = await PassThroughAsync(buffer, input, output, cancellationToken).ConfigureAwait(false);
                return OverLimit(length, options, cancellationToken);
            }
        }

#pragma warning restore CA1835

        // The rewrite goes to the output as it lies in memory, without a copy into another stream.
        var clock = Stopwatch.StartNew();
        (ReadOnlyMemory<byte> written, PdfOptimizationResult r) = OptimizeInMemory(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), options, cancellationToken);
        await Compat.WriteAsync(output, written, cancellationToken).ConfigureAwait(false);
        return WithElapsed(r, clock);
    }

    /// <summary>The most input either limit lets a run take in: <see cref="PdfOptimizerOptions.MaxInputBytes"/> and <see cref="PdfLimits.MaxInputBytes"/>.</summary>
    private static long InputCap(PdfOptimizerOptions options) => Math.Min(options.MaxInputBytes, options.Limits.MaxInputBytes);

    /// <summary>
    /// The result for an input over <see cref="InputCap"/> that was passed on unchanged and never
    /// parsed. The option's limit is checked first, as in a run: the input is unsupported, not a
    /// limit of the library's untrusted-input policy.
    /// </summary>
    private static PdfOptimizationResult OverLimit(long length, PdfOptimizerOptions options, CancellationToken ct)
    {
        if (length > options.MaxInputBytes)
        {
            return new PdfOptimizationResult(OptimizationDisposition.UnsupportedRetained, length, length, "", "", 0,
                "Input exceeds the configured size limit.", OptimizationStatistics.Empty(TimeSpan.Zero));
        }

        using var guard = new RunGuard(options.Limits, length, default, ct);
        string reason = guard.InputTooLarge();
        return new PdfOptimizationResult(OptimizationDisposition.LimitExceeded, length, length, "", "", 0, reason, OptimizationStatistics.Empty(TimeSpan.Zero))
        {
            Diagnostics = guard.Diagnostics,
        };
    }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
    /// <summary>Writes what was taken in so far, if anything, then the rest of <paramref name="input"/>, to <paramref name="output"/>; returns the input's length.</summary>
    private static async Task<long> PassThroughAsync(Stream? taken, Stream input, Stream output, CancellationToken ct)
    {
        long total = 0;
        if (taken is not null)
        {
            total = taken.Length;
            taken.Position = 0;
            await taken.CopyToAsync(output, OutputStreams.CopyBufferSize, ct).ConfigureAwait(false);
        }

        byte[] chunk = new byte[OutputStreams.CopyBufferSize];
        int read;
        while ((read = await input.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(chunk, 0, read, ct).ConfigureAwait(false);
            total += read;
        }

        return total;
    }
#pragma warning restore CA1835

    /// <summary>
    /// Optimizes a file. The destination is written through a temporary file and moved into place,
    /// so readers never observe a partial file. Source and destination must differ.
    /// </summary>
    /// <remarks>
    /// By default the source is read into memory. With <see cref="PdfOptimizerOptions.Streaming"/>
    /// it is read on demand, and the rewrite is written to the destination's temporary file and
    /// proven there before it is moved into place; when it is not used, the source is copied there
    /// instead.
    /// </remarks>
    /// <param name="sourcePath">The PDF to optimize. It is never modified.</param>
    /// <param name="destinationPath">Receives the optimized PDF, or a copy of the source.</param>
    /// <param name="options">Optimization options; <see cref="PdfOptimizerOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels between and within passes; the destination is then left as it was.</param>
    public static async Task<PdfOptimizationResult> OptimizeFileAsync(
        string sourcePath,
        string destinationPath,
        PdfOptimizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination must be different files.", nameof(destinationPath));
        }

        options ??= PdfOptimizerOptions.Default;
        options.Validate();

        // A file over either input limit is copied through without being read into memory.
        var info = new FileInfo(source);
        if (info.Exists && info.Length > InputCap(options))
        {
            return await CopyOverLimitFileAsync(source, destination, options, cancellationToken).ConfigureAwait(false);
        }

        if (options.Streaming)
        {
            return await OptimizeFileStreamingAsync(source, destination, options, cancellationToken).ConfigureAwait(false);
        }

        byte[] data = await Compat.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PdfOptimizationResult result;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                result = Optimize(data, stream, options, cancellationToken);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Compat.MoveOverwrite(temp, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Copies a file over the input limits to the destination, through a temporary file like any other result.</summary>
    private static async Task<PdfOptimizationResult> CopyOverLimitFileAsync(string source, string destination, PdfOptimizerOptions options, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            long length;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
            using (var copy = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                length = await PassThroughAsync(null, input, copy, ct).ConfigureAwait(false);
                await copy.FlushAsync(ct).ConfigureAwait(false);
            }

            Compat.MoveOverwrite(temp, destination);
            return OverLimit(length, options, ct);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record Outcome(
        OptimizationDisposition Disposition,
        ReadOnlyMemory<byte>? Candidate,
        string Reason,
        int PageCount,
        OptimizationStatistics? Statistics)
    {
        public IReadOnlyList<PdfDiagnostic> Diagnostics { get; init; } = [];

        /// <summary>The run ended at the time limit.</summary>
        public bool TimedOut { get; init; }
    }

    /// <summary>One run of the passes with its own budget.</summary>
    private static Outcome RunOnce(ReadOnlyMemory<byte> input, PdfOptimizerOptions options, Deadline deadline, CancellationToken caller)
    {
        if (input.Length > options.MaxInputBytes)
        {
            return new Outcome(OptimizationDisposition.UnsupportedRetained, null, "Input exceeds the configured size limit.", 0, null);
        }

        using var guard = new RunGuard(options.Limits, input.Length, deadline, caller, options.Workers);
        Outcome outcome = Run(input, options, guard);
        return outcome with { Diagnostics = guard.Diagnostics, TimedOut = guard.TimedOut };
    }

    private static Outcome Run(ReadOnlyMemory<byte> input, PdfOptimizerOptions options, RunGuard guard)
    {
        if (input.Length > options.Limits.MaxInputBytes)
        {
            return new Outcome(OptimizationDisposition.LimitExceeded, null, guard.InputTooLarge(), 0, null);
        }

        CancellationToken ct = guard.Token;
        PdfFile file;
        try
        {
            file = PdfFile.Open(input, options.MaxDecodedStreamBytes, guard.Budget);
        }
        catch (Exception ex) when (guard.Failure(ex) is { } failure)
        {
            return new Outcome(failure.Disposition == OptimizationDisposition.OriginalRetained ? OptimizationDisposition.UnsupportedRetained : failure.Disposition, null,
                failure.Disposition == OptimizationDisposition.OriginalRetained ? "Not a readable PDF: " + ex.Message : failure.Reason, 0, null);
        }

        int pageCount = 0;
        var phase = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        try
        {
            pageCount = CountPages(file, guard.Budget.Diagnostics);
            int byteRanges = UsageRights.CountByteRanges(input.Span);
            bool usageRightsRemoved = options.RemoveUsageRights && UsageRights.Remove(file, byteRanges);
            if (Unsupported(file, byteRanges > 0 && !usageRightsRemoved, options) is { } unsupported)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, unsupported);
            }

            ObjectGraph graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
            if (Unsupported(graph, file, options) is { } unsupportedGraph)
            {
                return Retained(OptimizationDisposition.UnsupportedRetained, unsupportedGraph);
            }

            bool pdfA1 = IsPdfA1(file, options.MaxDecodedStreamBytes);
            bool objectStreams = options.UseObjectStreams && !pdfA1;
            string version = OutputVersion(file.HeaderVersion, objectStreams);
            bool progressive = options.ProgressiveJpeg && AllowsProgressiveJpeg(version) && (!options.Linearize || AllowsProgressiveJpeg(LinearizedVersion(file.HeaderVersion)));
            int stripped = graph.StrippedEntries + (usageRightsRemoved ? 1 : 0);
            timings = timings with { Analyze = phase.Elapsed };
            phase.Restart();

            // Structural passes, all decided by one analysis of the content before anything changes.
            int pruned = 0, fontsMerged = 0;
            HashSet<int> compact = [], encoded = [];
            bool mergeFonts = options.MergeFontSubsets && options.DeduplicateObjects;
            if (options.PruneUnusedResources || options.CompactContentStreams || mergeFonts)
            {
                ContentAnalysis analysis = ContentAnalysis.Run(file, graph.Order, [], options.MaxDecodedStreamBytes, ct);
                if (options.PruneUnusedResources && (pruned = ResourcePruner.Run(analysis)) > 0)
                {
                    graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
                }

                if (mergeFonts) (fontsMerged, encoded) = FontProgramMerger.Run(file, analysis, options.ZlibLevel, options.MaxDecodedStreamBytes, ct);
                if (options.CompactContentStreams) compact = analysis.ContentStreams;
            }

            // A linearized file describes each page by what the page itself refers to.
            string? notLinearized = options.Linearize ? InheritedAttributes.PushToPages(file) : null;
            bool pushedDown = options.Linearize && notLinearized is null;

            timings = timings with { Content = phase.Elapsed };
            phase.Restart();

            RecompressionCounts recompressed = new(0, 0, []);
            if (options.RecompressStreams || compact.Count > 0)
            {
                JpegSettings? jpeg = options.OptimizeJpegImages ? new JpegSettings(options.JpegQuality, progressive, options.MinImageSsim) : null;
                ImageSettings? images = options.MaxImageDpi is not null || options.MaxBilevelImageDpi is not null || options.ConvertImagesToJpeg || options.QuantizeImages
                    ? new ImageSettings(options.MaxImageDpi, options.ConvertImagesToJpeg, options.JpegQuality, options.MinImageSsim, progressive) { Quantize = options.QuantizeImages, MaxBilevelDpi = options.MaxBilevelImageDpi }
                    : null;
                var scope = new RecompressionScope(options.RecompressStreams, compact, encoded, options.DeduplicateObjects) { KeepMetadataUnfiltered = pdfA1 || options.KeepMetadataUnfiltered };
                recompressed = StreamRecompressor.Run(file, graph, scope, options.ZlibLevel, options.Compression == CompressionEffort.Exhaustive, options.UseImagePredictors,
                    options.OptimizeBilevelImages, jpeg, images, options.MaxDecodedStreamBytes, options.Workers, ct);

                // A JBIG2 image coded anew no longer needs its JBIG2Globals, which may now be unused.
                if (recompressed.ResampledObjects.Count > 0) graph = ObjectGraph.Collect(file, options.RemovePageThumbnails, options.RemovePieceInfo, ct);
            }

            timings = timings with { Recompress = phase.Elapsed };
            phase.Restart();

            // The obsolete XObject /Name is ignored when merging, unless the output stays PDF 1.0.
            Dictionary<int, int> representative;
            int merged = 0, namesRemoved = 0;
            if (options.DeduplicateObjects)
            {
                (representative, merged, namesRemoved) = Deduplicator.Run(file, graph, recompressed.ContentHashes, ignoreXObjectNames: version != "1.0", ct);
            }
            else
            {
                representative = graph.Order.ToDictionary(n => n, n => n);
            }

            timings = timings with { Deduplicate = phase.Elapsed };
            phase.Restart();

            ct.ThrowIfCancellationRequested();
            var outputNumber = new Dictionary<int, int>(graph.Order.Count);
            var objects = new List<PdfObject>(graph.Order.Count);
            foreach (int number in graph.Order)
            {
                if (representative[number] != number) continue;
                objects.Add(file.GetObject(number));
                outputNumber[number] = objects.Count;
            }

            int Map(PdfReference r) =>
                representative.TryGetValue(r.Number, out int rep) && outputNumber.TryGetValue(rep, out int n) ? n : -1;

            var trailer = new PdfDictionary();
            foreach (string key in new[] { "Root", "Info" })
            {
                if (file.Trailer[key] is { } v) trailer.Set(key, v);
            }

            if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);

            var settings = new WriterSettings(objectStreams, options.ObjectsPerStream, version, options.ZlibLevel)
            {
                Exhaustive = options.Compression == CompressionEffort.Exhaustive,
                Parallelism = options.Workers,
            };
            ReadOnlyMemory<byte> candidate;
            bool linearized = false;
            if (pushedDown && LinearizedWriter.Write(objects, trailer, Map, settings with { UseObjectStreams = false, Version = LinearizedVersion(file.HeaderVersion) }) is { } lin)
            {
                candidate = lin;
                linearized = true;
            }
            else
            {
                if (pushedDown) notLinearized = "the page tree cannot be laid out page by page";
                candidate = PdfDocumentWriter.Write(objects, trailer, Map, settings);
            }

            timings = timings with { Write = phase.Elapsed };
            phase.Restart();

            var stats = new OptimizationStatistics(
                file.Xref.Count(e => e.Value.Type != XrefEntryType.Free),
                graph.Order.Count,
                objects.Count,
                recompressed.Streams,
                merged,
                stripped,
                0,
                TimeSpan.Zero,
                timings)
            {
                JpegImagesRecompressed = recompressed.JpegImages,
                JpegImagesRequantized = recompressed.JpegRequantized,
                ImagesDownsampled = recompressed.Downsampled,
                ImagesConvertedToJpeg = recompressed.ConvertedToJpeg,
                ImagesQuantized = recompressed.Quantized,
                ImagesKeptByQualityGate = recompressed.KeptByGate,
                ImagesRecodedAsGroup4 = recompressed.Group4,
                ResourcesPruned = pruned,
                ContentStreamsCompacted = recompressed.Compacted,
                FontProgramsMerged = fontsMerged,
                Linearized = linearized,
            };

            // Linearizing a file that is not linearized yet is worth keeping even when it costs bytes.
            double saving = 100.0 * (input.Length - candidate.Length) / input.Length;
            if (saving < options.MinSavingPercent && !(linearized && !file.IsLinearized))
            {
                return new Outcome(OptimizationDisposition.OriginalRetained, null,
                    Compat.Format("Rewrite saved {0:F2}%, below the {1}% threshold.", saving, options.MinSavingPercent),
                    pageCount, stats);
            }

            if (options.Verify)
            {
                ct.ThrowIfCancellationRequested();
                PdfFile reference = PdfFile.Open(input, options.MaxDecodedStreamBytes, guard.Budget);
                PdfFile written = PdfFile.Open(candidate, options.MaxDecodedStreamBytes, guard.Budget);
                if (written.WasReconstructed || written.LengthsRepaired > 0)
                {
                    return new Outcome(OptimizationDisposition.OriginalRetained, null, "Verification failed: output is not well formed.", pageCount, stats);
                }

                var ignored = new List<string>();
                if (options.RemovePageThumbnails) ignored.Add("Thumb");
                if (options.RemovePieceInfo) ignored.Add("PieceInfo");
                if (usageRightsRemoved) ignored.Add("Perms");
                var rules = new VerificationRules
                {
                    IgnoredSourceKeys = ignored,
                    Images = new ImageAllowance(recompressed.RequantizedObjects.Contains, recompressed.ResampledObjects.Contains, options.MinImageSsim),
                    AllowResourcePruning = pruned > 0,
                    AllowContentCompaction = recompressed.Compacted > 0,
                    AllowFontMerging = fontsMerged > 0,
                    AllowInheritedAttributes = pushedDown,
                    AllowBilevelRecoding = recompressed.Group4 > 0,
                    AllowXObjectNameRemoval = namesRemoved > 0,
                };
                string? difference = EquivalenceVerifier.Verify(reference, written, rules, options.MaxDecodedStreamBytes,
                    options.Workers, ct, out int compared);
                if (difference is not null)
                {
                    return new Outcome(OptimizationDisposition.OriginalRetained, null, "Verification failed: " + difference, pageCount, stats);
                }

                stats = stats with { ObjectsVerified = compared, Phases = timings with { Verify = phase.Elapsed } };
            }

            string reason = Reason(options, recompressed);
            if (usageRightsRemoved) reason += " Usage rights removed.";
            if (linearized) reason += " The output is linearized.";
            else if (notLinearized is not null) reason += " Not linearized: " + notLinearized + ".";
            return new Outcome(OptimizationDisposition.Optimized, candidate, reason, pageCount, stats);
        }
        catch (Exception ex) when (guard.Failure(ex) is { } failure)
        {
            return Retained(failure.Disposition, failure.Reason);
        }

        Outcome Retained(OptimizationDisposition disposition, string reason) => new(disposition, null, reason, pageCount, null);
    }

    /// <summary>
    /// The limits of one run: its budget, a token that also ends at the time limit, and how an
    /// exception thrown by a pass ends the run.
    /// </summary>
    internal sealed class RunGuard : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource? _timer;
        private readonly CancellationTokenSource? _linked;

        public RunGuard(PdfLimits limits, long inputBytes, Deadline deadline, CancellationToken caller, int workers = 1)
        {
            // Each worker holds a few buffers of a size at once: the source, a filter layer, the result.
            Budget = new ResourceBudget(limits, inputBytes, new PdfDiagnostics())
            {
                Pool = System.Buffers.ArrayPool<byte>.Create(1 << 30, Math.Max(4, 3 * workers)),
            };
            _caller = caller;
            _timer = deadline.NewTimer();
            _linked = _timer is null ? null : CancellationTokenSource.CreateLinkedTokenSource(caller, _timer.Token);
            Token = _linked?.Token ?? caller;
        }

        public ResourceBudget Budget { get; }

        /// <summary>The caller's token, cancelled as well at the time limit.</summary>
        public CancellationToken Token { get; }

        /// <summary>The time limit ended the run, not the caller.</summary>
        public bool TimedOut => _timer is { IsCancellationRequested: true } && !_caller.IsCancellationRequested;

        public IReadOnlyList<PdfDiagnostic> Diagnostics => Budget.Diagnostics.Snapshot();

        public string InputTooLarge()
        {
            string message = Compat.Format("Input exceeds its limit of {0} bytes (PdfLimits.MaxInputBytes).", Budget.Limits.MaxInputBytes);
            Budget.Report(PdfLimitKind.InputBytes, message);
            return message;
        }

        /// <summary>
        /// How a pass that threw ends the run, or null for an exception that must reach the
        /// caller: the caller's cancellation, and running out of memory or a corrupted process,
        /// after which the process may be in no state to go on and the caller must know the input
        /// was not processed. Exceptions from parallel workers arrive wrapped, so every inner one
        /// is looked at.
        /// </summary>
        public (OptimizationDisposition Disposition, string Reason)? Failure(Exception ex)
        {
            if (Find<OutOfMemoryException>(ex) is not null || Find<AccessViolationException>(ex) is not null) return null;
            if (Find<OperationCanceledException>(ex) is not null)
            {
                if (!TimedOut) return null;
                const string message = "The run ran past its time limit (PdfLimits.Timeout).";
                Budget.Report(PdfLimitKind.Timeout, message);
                return (OptimizationDisposition.LimitExceeded, message);
            }

            if (Find<PdfLimitExceededException>(ex) is { } limit) return (OptimizationDisposition.LimitExceeded, "Limit reached: " + limit.Message);
            return (OptimizationDisposition.OriginalRetained, $"Rewrite failed ({ex.GetType().Name}): {ex.Message}");
        }

        public void Dispose()
        {
            _linked?.Dispose();
            _timer?.Dispose();
        }

        private static T? Find<T>(Exception ex)
            where T : Exception => ex switch
        {
            T match => match,
            AggregateException all => all.InnerExceptions.Select(Find<T>).FirstOrDefault(found => found is not null),
            _ => ex.InnerException is { } inner ? Find<T>(inner) : null,
        };
    }

    /// <summary>Why a file is left as it is, from what opening it showed; null when it may be rewritten.</summary>
    /// <param name="signed">The file's bytes hold a signature's /ByteRange.</param>
    private static string? Unsupported(PdfFile file, bool signed, PdfOptimizerOptions options)
    {
        if (file.Trailer["Encrypt"] is not null and not PdfNull) return "Encrypted PDF.";
        if (signed) return "Digitally signed PDF.";
        if (file.RevisionCount > 1 && !options.AllowIncrementalUpdates)
        {
            return $"PDF has {file.RevisionCount} revisions (incremental updates). Set AllowIncrementalUpdates to flatten.";
        }

        if ((file.WasReconstructed || file.LengthsRepaired > 0) && !options.AllowRepairedFiles)
        {
            return "PDF is damaged and needed repair. Set AllowRepairedFiles to rewrite it.";
        }

        return null;
    }

    /// <summary>Why a file is left as it is, from what walking its object graph showed; null when it may be rewritten.</summary>
    private static string? Unsupported(ObjectGraph graph, PdfFile file, PdfOptimizerOptions options)
    {
        if (graph.HasSignatureMarkers) return "Digitally signed or usage-rights enabled PDF.";
        if (graph.HasGenerationMismatch && !options.AllowRepairedFiles)
        {
            return "References name generations the cross-reference data does not record. Set AllowRepairedFiles to rewrite it.";
        }

        if (file.UnreadableObjects > 0 && !options.AllowRepairedFiles)
        {
            return $"{file.UnreadableObjects} object(s) could not be parsed. Set AllowRepairedFiles to rewrite it.";
        }

        return null;
    }

    /// <summary>The resolutions images were reduced to, as the reason names them.</summary>
    private static string DownsampledTo(PdfOptimizerOptions options) => (options.MaxImageDpi, options.MaxBilevelImageDpi) switch
    {
        ({ } dpi, { } bilevel) when bilevel != dpi => Compat.Format("{0} dpi, bilevel images to {1} dpi", dpi, bilevel),
        ({ } dpi, _) => Compat.Format("{0} dpi", dpi),
        (null, { } bilevel) => Compat.Format("{0} dpi (bilevel images)", bilevel),
        _ => "their limit",
    };

    /// <summary>What was written, naming every lossy image change.</summary>
    private static string Reason(PdfOptimizerOptions options, RecompressionCounts recompressed)
    {
        var lossy = new List<string>();
        if (recompressed.JpegRequantized > 0) lossy.Add(Compat.Format("{0} JPEG image(s) requantized to quality {1}", recompressed.JpegRequantized, options.JpegQuality!.Value));
        if (recompressed.Downsampled > 0) lossy.Add(Compat.Format("{0} image(s) downsampled to {1}", recompressed.Downsampled, DownsampledTo(options)));
        if (recompressed.ConvertedToJpeg > 0) lossy.Add(Compat.Format("{0} image(s) converted to JPEG at quality {1}", recompressed.ConvertedToJpeg, options.JpegQuality!.Value));
        if (recompressed.Quantized > 0) lossy.Add(Compat.Format("{0} image(s) reduced to a palette", recompressed.Quantized));
        if (lossy.Count == 0)
        {
            return options.Verify
                ? "Rewritten and verified equivalent to the source."
                : "Rewritten. Verification was disabled.";
        }

        string changes = string.Join(", ", lossy);
        return options.Verify
            ? "Rewritten and verified. " + changes + "; everything else is equivalent to the source."
            : "Rewritten. " + changes + ". Verification was disabled.";
    }

    /// <summary>
    /// The pages the page tree holds, counted leaf by leaf as <see cref="PdfDocument"/> reads them:
    /// /Count is only the file's claim, and a hostile file claims a billion. Nodes listed twice
    /// are read once and the tree is read <see cref="PdfDocument.MaxPageTreeDepth"/> levels deep,
    /// so the walk is linear in the file.
    /// </summary>
    internal static int CountPages(PdfFile file, PdfDiagnostics? diagnostics = null)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (catalog?["Pages"] is not PdfReference root) return 0;
        var seen = new HashSet<int>();
        var stack = new Stack<(int Number, int Depth)>();
        stack.Push((root.Number, 0));
        int pages = 0, looped = 0, tooDeep = 0;
        while (stack.Count > 0)
        {
            (int number, int depth) = stack.Pop();
            if (file.ResolveDictionary(new PdfReference(number, 0)) is not { } node) continue;
            if (node.GetName("Type") == "Page" || (node.GetName("Type") != "Pages" && node["Kids"] is null))
            {
                pages++;
                continue;
            }

            if (!seen.Add(number))
            {
                looped++;
                continue;
            }

            if (depth > PdfDocument.MaxPageTreeDepth)
            {
                tooDeep++;
                continue;
            }

            if (file.Resolve(node["Kids"]) is not PdfArray kids) continue;
            for (int i = kids.Count - 1; i >= 0; i--)
            {
                if (kids[i] is PdfReference kid) stack.Push((kid.Number, depth + 1));
            }
        }

        if (diagnostics is not null)
        {
            if (looped > 0) diagnostics.Skipped(Compat.Format("The page tree lists {0} of its nodes more than once (a loop or a shared branch); they were read once.", looped));
            if (tooDeep > 0) diagnostics.Skipped(Compat.Format("The page tree nests deeper than {0} levels; {1} branch(es) below that were not read, with their pages.", PdfDocument.MaxPageTreeDepth, tooDeep));
            if (file.Resolve(file.ResolveDictionary(root)?["Count"]) is PdfNumber { IsInteger: true } count && count.IntegerValue != pages)
            {
                diagnostics.Repaired(Compat.Format("The page tree says it has {0} page(s) but {1} were found; the pages found are used.", count.IntegerValue, pages));
            }
        }

        return pages;
    }

    private static string OutputVersion(string header, bool objectStreams)
    {
        if (!objectStreams) return header;
        return decimal.TryParse(header, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.5m
            ? header
            : "1.5";
    }

    /// <summary>Progressive JPEG (DCTDecode) arrived with PDF 1.3; output at an earlier version keeps sequential coding.</summary>
    private static bool AllowsProgressiveJpeg(string version) =>
        decimal.TryParse(version, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.3m;

    /// <summary>Linearization arrived with PDF 1.2.</summary>
    private static string LinearizedVersion(string header) =>
        decimal.TryParse(header, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.2m ? header : "1.2";

    /// <summary>PDF/A-1 is based on PDF 1.4 and forbids object and xref streams.</summary>
    internal static bool IsPdfA1(PdfFile file, long decodeLimit)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (file.Resolve(catalog?["Metadata"]) is not PdfStream metadata) return false;
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(metadata.Data, StreamCodec.GetFilters(metadata.Dictionary, file.Resolve),
                DecodeDepth.Full, decodeLimit, file.Resolve, file.Budget);
            return PdfAPartOne().IsMatch(Compat.Latin1.GetString(decoded.Data.Span));
        }
        catch (StreamDecodeException)
        {
            // Unknown conformance: stay on the safe side.
            return true;
        }
    }

#if NET8_0_OR_GREATER
    [GeneratedRegex("""pdfaid:part\s*(=\s*["']\s*1\s*["']|>\s*1\s*<)""")]
    private static partial Regex PdfAPartOne();
#else
    private static readonly Regex PdfAPartOneRegex = new("""pdfaid:part\s*(=\s*["']\s*1\s*["']|>\s*1\s*<)""", RegexOptions.CultureInvariant);

    private static Regex PdfAPartOne() => PdfAPartOneRegex;
#endif

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Compat.ToHex(Compat.Sha256(data)).ToLowerInvariant();
}
