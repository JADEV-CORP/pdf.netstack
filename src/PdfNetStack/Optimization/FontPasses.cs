using System.Globalization;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>What the font program passes did, for the statistics, the proof and the reason.</summary>
/// <param name="Subset">Programs replaced by a subset.</param>
/// <param name="Converted">Type 1 programs rewritten as CFF.</param>
/// <param name="Kept">Fully embedded programs left whole for a reason other than size, as "name: reason".</param>
internal sealed record FontPassResult(int Subset, int Converted, IReadOnlyList<string> Kept)
{
    public static FontPassResult None => new(0, 0, []);

    /// <summary>A sentence for the result's reason naming the fully embedded fonts left whole and why; empty when there are none.</summary>
    public string Note()
    {
        if (Kept.Count == 0) return "";
        IEnumerable<string> reasons = Kept
            .Select(k => (Name: k[..k.IndexOf(": ", StringComparison.Ordinal)], Reason: k[(k.IndexOf(": ", StringComparison.Ordinal) + 2)..]))
            .GroupBy(k => k.Reason, StringComparer.Ordinal)
            .Select(g => string.Join(", ", g.Select(k => k.Name).Distinct(StringComparer.Ordinal)) + " (" + g.Key + ")");
        return " Fonts left whole: " + string.Join("; ", reasons) + ".";
    }
}

/// <summary>
/// Runs the font program passes that rewrite programs in place, before merging: subsetting of
/// fully embedded TrueType and CFF programs, then conversion of Type 1 programs to CFF. Kept apart
/// so the optimizer's in-memory and streaming paths share one hook.
/// </summary>
internal static class FontPasses
{
    public static FontPassResult Run(PdfFile file, ContentAnalysis analysis, List<int> order, PdfOptimizerOptions options, bool pdfA1, CancellationToken ct)
    {
        if (!options.SubsetFonts && !options.ConvertType1Fonts) return FontPassResult.None;
        int subset = 0, converted = 0;
        List<string> kept = [];
        if (options.SubsetFonts)
        {
            (subset, _, List<string> notes) = FontSubsetter.Run(file, analysis, order, pdfA1, options.ZlibLevel, options.MaxDecodedStreamBytes, ct);

            // Only fonts that claim to be whole (no subset tag) are worth naming.
            kept = notes.Where(n => !FontSubsetter.HasTag(n)).ToList();
        }

        if (options.ConvertType1Fonts && CffAllowed(file, options, pdfA1))
        {
            (converted, _) = Type1Converter.Run(file, analysis, order, options.ZlibLevel, options.MaxDecodedStreamBytes, options.MaxDegreeOfParallelism, ct);
        }

        return new FontPassResult(subset, converted, kept);
    }

    /// <summary>CFF in a Type 1 font (FontFile3 /Type1C) needs PDF 1.2; object streams make the output 1.5.</summary>
    private static bool CffAllowed(PdfFile file, PdfOptimizerOptions options, bool pdfA1) =>
        (options.UseObjectStreams && !pdfA1) || options.Linearize ||
        (decimal.TryParse(file.HeaderVersion, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v) && v >= 1.2m);
}
