using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.Type1;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Rewrites Type 1 font programs (FontFile) as CFF (FontFile3 /Type1C). The eexec and charstring
/// encryption makes Type 1 data incompressible, while the same glyphs in CFF compress well: TeX
/// output typically shrinks by half or more. Every glyph is kept, and each conversion is checked
/// glyph by glyph before it is used; the verifier repeats that check on its own parse.
/// </summary>
internal static class Type1Converter
{
    /// <summary>
    /// The Type 1 programs that may become CFF: referenced only from /FontFile of descriptors
    /// that only simple Type 1 fonts use, so that every reader reaching the program goes through
    /// a descriptor whose /FontFile3 can take its place.
    /// </summary>
    public static HashSet<int> Candidates(PdfFile file, ReferenceIndex refs, IEnumerable<int> order)
    {
        var descriptors = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var programs = new HashSet<int>();
        foreach (int number in order)
        {
            if (file.GetObject(number) is not PdfDictionary d || d.GetName("Type") != "FontDescriptor") continue;
            descriptors[d] = number;
            if (d["FontFile"] is PdfReference r && d["FontFile2"] is null && d["FontFile3"] is null && file.GetObject(r.Number) is PdfStream) programs.Add(r.Number);
        }

        bool UsedOnlyByType1(PdfDictionary descriptor) =>
            descriptors.TryGetValue(descriptor, out int n) &&
            refs.OnlyFrom(n, r => r.Key == "FontDescriptor" && r.Container is PdfDictionary f && f.GetName("Subtype") == "Type1");

        programs.RemoveWhere(p => !refs.OnlyFrom(p, r => r.Key == "FontFile" && r.Container is PdfDictionary d && UsedOnlyByType1(d)));
        return programs;
    }

    /// <summary>Converts every candidate whose CFF form proves equal and is smaller.</summary>
    /// <returns>Programs converted and their object numbers.</returns>
    public static (int Converted, HashSet<int> Changed) Run(PdfFile file, ContentAnalysis analysis, List<int> order, CompressionLevel level, long decodeLimit, int parallelism, CancellationToken ct)
    {
        var changed = new HashSet<int>();
        ReferenceIndex refs = analysis.References;

        // Objects are read on this thread; the conversions, pure work on bytes, run side by side.
        var jobs = new List<(int Program, byte[] Type1)>();
        foreach (int program in Candidates(file, refs, order).OrderBy(n => n))
        {
            if (Decode(file, (PdfStream)file.GetObject(program), decodeLimit) is { } type1) jobs.Add((program, type1));
        }

        var results = new byte[]?[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            try
            {
                byte[] cff = Type1ToCff.Convert(Type1Font.Parse(jobs[i].Type1));

                // The proof the verifier runs: a conversion it would reject is not made.
                if (Type1ToCff.FindDifference(jobs[i].Type1, cff) is null) results[i] = StreamCodec.Deflate(cff, level);
            }
            catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
            {
                // Left as it is: the font is damaged or uses something CFF cannot say exactly.
            }
        });

        for (int i = 0; i < jobs.Count; i++)
        {
            int program = jobs[i].Program;
            var stream = (PdfStream)file.GetObject(program);
            if (results[i] is not { } encoded || encoded.Length >= stream.Data.Length) continue;

            stream.Data = encoded;
            PdfDictionary dict = stream.Dictionary;
            foreach (string key in new[] { "DecodeParms", "DL", "Length1", "Length2", "Length3" }) dict.Remove(key);
            dict.Set("Filter", new PdfName("FlateDecode"));
            dict.Set("Subtype", new PdfName("Type1C"));
            foreach (Referrer r in refs.Of(program))
            {
                var descriptor = (PdfDictionary)r.Container;
                if (descriptor["FontFile"] is not PdfReference reference) continue;
                descriptor.Remove("FontFile");
                descriptor.Set("FontFile3", reference);
            }

            changed.Add(program);
        }

        return (changed.Count, changed);
    }

    private static byte[]? Decode(PdfFile file, PdfStream stream, long decodeLimit)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, file.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, decodeLimit, file.Resolve);
            return decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null ? null : decoded.Data.ToArray();
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }
}
