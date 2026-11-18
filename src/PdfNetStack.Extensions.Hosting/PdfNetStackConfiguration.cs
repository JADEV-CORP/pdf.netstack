using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace PdfNetStack.Extensions.Hosting;

/// <summary>
/// Binds <see cref="PdfNetStackOptions"/> from configuration. <see cref="PdfOptimizerOptions"/> and
/// <see cref="PdfLimits"/> are records with init-only properties, which the configuration binding
/// generator leaves unset, so each key is read here, explicitly: no reflection, safe under trimming
/// and Native AOT.
/// </summary>
/// <remarks>
/// Keys match the property names, case-insensitively. Under <c>Optimizer</c>, <c>Preset</c> picks
/// the starting point (Default, Balanced or Smallest) the other keys change. A key that names no
/// property, or a value that does not parse, throws <see cref="InvalidOperationException"/> naming
/// the key, so a typo in configuration fails at startup instead of being ignored.
/// </remarks>
internal static class PdfNetStackConfiguration
{
    private delegate T Setter<T>(T options, string value, string path);

    private static readonly Dictionary<string, Setter<PdfOptimizerOptions>> OptimizerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(PdfOptimizerOptions.MinSavingPercent)] = (o, v, p) => o with { MinSavingPercent = Double(v, p) },
        [nameof(PdfOptimizerOptions.MaxInputBytes)] = (o, v, p) => o with { MaxInputBytes = Long(v, p) },
        [nameof(PdfOptimizerOptions.Streaming)] = (o, v, p) => o with { Streaming = Bool(v, p) },
        [nameof(PdfOptimizerOptions.MaxDecodedStreamBytes)] = (o, v, p) => o with { MaxDecodedStreamBytes = Long(v, p) },
        [nameof(PdfOptimizerOptions.Compression)] = (o, v, p) => o with { Compression = Enum<CompressionEffort>(v, p) },
        [nameof(PdfOptimizerOptions.UseObjectStreams)] = (o, v, p) => o with { UseObjectStreams = Bool(v, p) },
        [nameof(PdfOptimizerOptions.ObjectsPerStream)] = (o, v, p) => o with { ObjectsPerStream = Int(v, p) },
        [nameof(PdfOptimizerOptions.DeduplicateObjects)] = (o, v, p) => o with { DeduplicateObjects = Bool(v, p) },
        [nameof(PdfOptimizerOptions.PruneUnusedResources)] = (o, v, p) => o with { PruneUnusedResources = Bool(v, p) },
        [nameof(PdfOptimizerOptions.CompactContentStreams)] = (o, v, p) => o with { CompactContentStreams = Bool(v, p) },
        [nameof(PdfOptimizerOptions.MergeFontSubsets)] = (o, v, p) => o with { MergeFontSubsets = Bool(v, p) },
        [nameof(PdfOptimizerOptions.Linearize)] = (o, v, p) => o with { Linearize = Bool(v, p) },
        [nameof(PdfOptimizerOptions.RecompressStreams)] = (o, v, p) => o with { RecompressStreams = Bool(v, p) },
        [nameof(PdfOptimizerOptions.UseImagePredictors)] = (o, v, p) => o with { UseImagePredictors = Bool(v, p) },
        [nameof(PdfOptimizerOptions.OptimizeJpegImages)] = (o, v, p) => o with { OptimizeJpegImages = Bool(v, p) },
        [nameof(PdfOptimizerOptions.OptimizeBilevelImages)] = (o, v, p) => o with { OptimizeBilevelImages = Bool(v, p) },
        [nameof(PdfOptimizerOptions.JpegQuality)] = (o, v, p) => o with { JpegQuality = Nullable(v, p, Int) },
        [nameof(PdfOptimizerOptions.MaxImageDpi)] = (o, v, p) => o with { MaxImageDpi = Nullable(v, p, Int) },
        [nameof(PdfOptimizerOptions.MaxBilevelImageDpi)] = (o, v, p) => o with { MaxBilevelImageDpi = Nullable(v, p, Int) },
        [nameof(PdfOptimizerOptions.ConvertImagesToJpeg)] = (o, v, p) => o with { ConvertImagesToJpeg = Bool(v, p) },
        [nameof(PdfOptimizerOptions.QuantizeImages)] = (o, v, p) => o with { QuantizeImages = Bool(v, p) },
        [nameof(PdfOptimizerOptions.MinImageSsim)] = (o, v, p) => o with { MinImageSsim = Nullable(v, p, Double) },
        [nameof(PdfOptimizerOptions.ProgressiveJpeg)] = (o, v, p) => o with { ProgressiveJpeg = Bool(v, p) },
        [nameof(PdfOptimizerOptions.UseJbig2)] = (o, v, p) => o with { UseJbig2 = Bool(v, p) },
        [nameof(PdfOptimizerOptions.SubsetFonts)] = (o, v, p) => o with { SubsetFonts = Bool(v, p) },
        [nameof(PdfOptimizerOptions.ConvertType1Fonts)] = (o, v, p) => o with { ConvertType1Fonts = Bool(v, p) },
        [nameof(PdfOptimizerOptions.Verify)] = (o, v, p) => o with { Verify = Bool(v, p) },
        [nameof(PdfOptimizerOptions.AllowIncrementalUpdates)] = (o, v, p) => o with { AllowIncrementalUpdates = Bool(v, p) },
        [nameof(PdfOptimizerOptions.AllowRepairedFiles)] = (o, v, p) => o with { AllowRepairedFiles = Bool(v, p) },
        [nameof(PdfOptimizerOptions.RemovePageThumbnails)] = (o, v, p) => o with { RemovePageThumbnails = Bool(v, p) },
        [nameof(PdfOptimizerOptions.RemovePieceInfo)] = (o, v, p) => o with { RemovePieceInfo = Bool(v, p) },
        [nameof(PdfOptimizerOptions.RemoveUsageRights)] = (o, v, p) => o with { RemoveUsageRights = Bool(v, p) },
        [nameof(PdfOptimizerOptions.OptimizeEncryptedFiles)] = (o, v, p) => o with { OptimizeEncryptedFiles = Bool(v, p) },
        [nameof(PdfOptimizerOptions.Password)] = (o, v, p) => o with { Password = v },
        [nameof(PdfOptimizerOptions.MaxDegreeOfParallelism)] = (o, v, p) => o with { MaxDegreeOfParallelism = Int(v, p) },
    };

    private static readonly Dictionary<string, Setter<PdfLimits>> LimitKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(PdfLimits.MaxInputBytes)] = (o, v, p) => o with { MaxInputBytes = Long(v, p) },
        [nameof(PdfLimits.MaxTotalDecodedBytes)] = (o, v, p) => o with { MaxTotalDecodedBytes = Nullable(v, p, Long) },
        [nameof(PdfLimits.MaxTotalImagePixels)] = (o, v, p) => o with { MaxTotalImagePixels = Nullable(v, p, Long) },
        [nameof(PdfLimits.MaxOperationsPerDocument)] = (o, v, p) => o with { MaxOperationsPerDocument = Long(v, p) },
        [nameof(PdfLimits.MaxTextCharsPerPage)] = (o, v, p) => o with { MaxTextCharsPerPage = Int(v, p) },
        [nameof(PdfLimits.Timeout)] = (o, v, p) => o with { Timeout = Duration(v, p) },
    };

    /// <summary>The property names bound under <c>Optimizer</c> and <c>Limits</c>, for the test that keeps them complete.</summary>
    internal static IEnumerable<string> OptimizerProperties => OptimizerKeys.Keys;

    internal static IEnumerable<string> LimitProperties => LimitKeys.Keys;

    /// <summary>Applies <paramref name="configuration"/> to <paramref name="options"/>: its <c>Optimizer</c> and <c>Limits</c> sections.</summary>
    public static void Bind(IConfiguration configuration, PdfNetStackOptions options)
    {
        foreach (IConfigurationSection section in configuration.GetChildren())
        {
            if (section.Key.Equals("Optimizer", StringComparison.OrdinalIgnoreCase)) options.Optimizer = Optimizer(section, options.Optimizer);
            else if (section.Key.Equals("Limits", StringComparison.OrdinalIgnoreCase)) options.Limits = Apply(section, options.Limits, LimitKeys);
            else throw Unknown(section);
        }
    }

    private static PdfOptimizerOptions Optimizer(IConfigurationSection section, PdfOptimizerOptions options)
    {
        // A preset is the starting point, whatever order the keys come in.
        if (section["Preset"] is { } preset)
        {
            options = preset.Trim().ToUpperInvariant() switch
            {
                "DEFAULT" => PdfOptimizerOptions.Default,
                "BALANCED" => PdfOptimizerOptions.Balanced,
                "SMALLEST" => PdfOptimizerOptions.Smallest,
                _ => throw new InvalidOperationException($"{section.Path}:Preset is '{preset}'; expected Default, Balanced or Smallest."),
            };
        }

        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (child.Key.Equals("Preset", StringComparison.OrdinalIgnoreCase)) continue;
            if (child.Key.Equals("Limits", StringComparison.OrdinalIgnoreCase)) options = options with { Limits = Apply(child, options.Limits, LimitKeys) };
            else options = Set(child, options, OptimizerKeys);
        }

        return options;
    }

    private static T Apply<T>(IConfigurationSection section, T options, Dictionary<string, Setter<T>> keys)
    {
        foreach (IConfigurationSection child in section.GetChildren()) options = Set(child, options, keys);
        return options;
    }

    private static T Set<T>(IConfigurationSection child, T options, Dictionary<string, Setter<T>> keys)
    {
        if (!keys.TryGetValue(child.Key, out Setter<T>? set)) throw Unknown(child);
        return child.Value is { } value ? set(options, value, child.Path) : options;
    }

    private static InvalidOperationException Unknown(IConfigurationSection section) =>
        new($"Configuration key {section.Path} is not a PdfNetStack option.");

    private static bool Bool(string v, string path) => bool.TryParse(v, out bool b) ? b : throw Invalid(path, v, "true or false");

    private static int Int(string v, string path) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : throw Invalid(path, v, "an integer");

    private static long Long(string v, string path) => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : throw Invalid(path, v, "an integer");

    private static double Double(string v, string path) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : throw Invalid(path, v, "a number");

    private static TimeSpan Duration(string v, string path) =>
        v.Trim().Equals("Infinite", StringComparison.OrdinalIgnoreCase) ? System.Threading.Timeout.InfiniteTimeSpan
        : TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out TimeSpan t) ? t : throw Invalid(path, v, "a duration such as 00:00:30, or Infinite");

    private static TEnum Enum<TEnum>(string v, string path)
        where TEnum : struct, System.Enum =>
        System.Enum.TryParse(v, ignoreCase: true, out TEnum e) && System.Enum.IsDefined(e) ? e : throw Invalid(path, v, string.Join(", ", System.Enum.GetNames<TEnum>()));

    /// <summary>An empty value or "null" clears a nullable option.</summary>
    private static T? Nullable<T>(string v, string path, Func<string, string, T> parse)
        where T : struct =>
        v.Length == 0 || v.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : parse(v, path);

    private static InvalidOperationException Invalid(string path, string value, string expected) =>
        new($"Configuration key {path} is '{value}'; expected {expected}.");
}
