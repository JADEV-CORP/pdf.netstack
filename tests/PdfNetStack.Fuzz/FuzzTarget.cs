using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Content;
using PdfNetStack.Filters;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Imaging;
using PdfNetStack.Signing;
using PdfNetStack.Text;
using PdfNetStack.Text.Extraction;

namespace PdfNetStack.Fuzz;

/// <summary>One entry point to fuzz: what it runs, what it may throw, and where its seeds come from.</summary>
internal sealed class FuzzTarget
{
    public required string Name { get; init; }

    /// <summary>Runs the entry point on one input. Inputs are never larger than libFuzzer's -max_len.</summary>
    public required Action<byte[]> Run { get; init; }

    /// <summary>The exception types the entry point documents for bad input; derived types count.</summary>
    public required Type[] Documented { get; init; }

    /// <summary>Starting inputs, by file name, from the test fixtures folder.</summary>
    public required Func<string, IEnumerable<(string Name, byte[] Data)>> Seeds { get; init; }

    public bool Documents(Exception ex) => Documented.Any(t => t.IsInstanceOfType(ex));

    public override string ToString() => Name;
}

/// <summary>Every target, one per entry point that reads untrusted bytes.</summary>
internal static class FuzzTargets
{
    // Text in many scripts, shaped with whatever part of it a fuzzed font covers.
    private const string Sample = "Hello, world! fi ffi 0123 Été مرحبا שלום नमस्ते ไทย 한국어 日本語 😀";

    // Opening documents NotSupportedException for a security handler other than the standard
    // password one, and PdfSigner.Verify for an encrypted document.
    private static readonly Type[] DocumentErrors = [typeof(PdfFormatException), typeof(PdfPasswordException), typeof(NotSupportedException)];

    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new()
        {
            Name = "open",
            Run = data =>
            {
                using PdfDocument doc = PdfDocument.Open(data);
                Walk(doc);
            },
            Documented = DocumentErrors,
            Seeds = Seeds.Pdfs,
        },
        new()
        {
            Name = "open-on-demand",
            Run = data =>
            {
                using var stream = new MemoryStream(data, writable: false);
                using PdfDocument doc = PdfDocument.OpenOnDemand(stream);
                Walk(doc);
            },
            Documented = [typeof(ArgumentException), .. DocumentErrors],
            Seeds = Seeds.Pdfs,
        },
        new()
        {
            Name = "optimize",

            // Every pass, on files with incremental updates and repaired ones too, on this thread.
            Run = data => PdfOptimizer.Optimize(data, new PdfOptimizerOptions
            {
                MinSavingPercent = 0,
                AllowIncrementalUpdates = true,
                AllowRepairedFiles = true,
                MaxDegreeOfParallelism = 1,
            }),
            Documented = [],
            Seeds = Seeds.Pdfs,
        },
        new()
        {
            Name = "verify-signatures",
            Run = data =>
            {
                foreach (PdfSignatureInfo info in PdfSigner.Verify(data)) _ = info.Problem;
            },
            Documented = DocumentErrors,
            Seeds = Seeds.Pdfs,
        },
        new()
        {
            Name = "trusted-list",
            Run = data => _ = PdfTrustedList.Load(data, Array.Empty<X509Certificate2>()).Services.Count(),
            Documented = [typeof(FormatException)],
            Seeds = Seeds.TrustedLists,
        },
        new()
        {
            Name = "image",

            // With the pixel ceiling the API asks for on untrusted images: 2^22 pixels decode within
            // the oracle's budget. The default (2^28) allows a canvas that no budget of 64 MiB holds,
            // and a tiny animated WebP may declare one legitimately.
            Run = data =>
            {
                PdfImage image = PdfImage.Load(data, 0, 1L << 22);
                _ = image.PixelWidth + image.PixelHeight;
            },
            Documented = [typeof(FormatException)],
            Seeds = Seeds.Images,
        },
        new()
        {
            Name = "opentype",
            Run = data =>
            {
                PdfOpenTypeFont font = PdfOpenTypeFont.Load(data);
                var covered = new StringBuilder();
                for (int i = 0; i < Sample.Length; i += char.IsSurrogatePair(Sample, i) ? 2 : 1)
                {
                    int length = char.IsSurrogatePair(Sample, i) ? 2 : 1;
                    if (PdfFont.IsInvisible(Sample, i) || font.Covers(Sample, i)) covered.Append(Sample, i, length);
                }

                string text = covered.ToString();
                _ = LineShaper.Shape(font, text, null);
                _ = font.MeasureString(text, 12);
            },
            Documented = [typeof(FontFormatException)],
            Seeds = Seeds.Fonts,
        },
        new()
        {
            Name = "html",

            // No resource is ever loaded: the input is the whole document.
            Run = data => HtmlConverter.ConvertToPdf(Encoding.UTF8.GetString(data), new HtmlConverterOptions { ResourceLoader = _ => null }),
            Documented = [typeof(HtmlConversionException)],
            Seeds = Seeds.Html,
        },
        new()
        {
            Name = "jbig2",

            // Width and height, then the length of the globals, then globals and page segments.
            Run = data =>
            {
                if (data.Length < 8) return;
                int width = 1 + ((data[0] << 8) | data[1]) % 2048;
                int height = 1 + ((data[2] << 8) | data[3]) % 2048;
                int globals = Math.Min(data.Length - 8, (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
                byte[]? shared = globals > 0 ? data[8..(8 + globals)] : null;
                _ = Jbig2Decoder.Decode(data[(8 + Math.Max(0, globals))..], shared, width, height);
            },
            Documented = [typeof(Jbig2FormatException)],
            Seeds = Seeds.Jbig2,
        },
        new()
        {
            Name = "jpx",
            // The byte limit caps the decoder's buffers at their peak; the oracle counts every byte
            // allocated, temporaries included, so the cap sits at half the oracle's 64 MiB.
            Run = data => _ = Jpeg2000Decoder.Decode(data, 32L << 20, 1L << 23),
            Documented = [typeof(Jpeg2000FormatException)],
            Seeds = Seeds.Jpx,
        },
        new()
        {
            Name = "ccitt",

            // K, flags, columns and rows, then the coded data.
            Run = data =>
            {
                if (data.Length < 6) return;
                var parameters = new CcittParameters
                {
                    K = (sbyte)data[0],
                    EndOfLine = (data[1] & 1) != 0,
                    EncodedByteAlign = (data[1] & 2) != 0,
                    EndOfBlock = (data[1] & 4) == 0,
                    BlackIs1 = (data[1] & 8) != 0,
                    Columns = 1 + ((data[2] << 8) | data[3]) % 4096,
                    Rows = ((data[4] << 8) | data[5]) % 4096,
                };
                _ = CcittDecoder.Decode(data.AsSpan(6), parameters, Math.Max(1, parameters.Rows));
            },
            Documented = [typeof(StreamDecodeException)],
            Seeds = Seeds.Ccitt,
        },
        new()
        {
            Name = "cmap",
            Run = data =>
            {
                CMap cmap = CMap.Parse(data, (name, _) => CMap.Predefined(name));
                for (int at = 0; at < data.Length && at < 4096;)
                {
                    int length = cmap.NextCode(data, at, out uint code);
                    _ = cmap.Unicode(code, length);
                    _ = cmap.Cid(code);
                    at += Math.Max(1, length);
                }
            },
            Documented = [typeof(PdfFormatException)],
            Seeds = Seeds.CMaps,
        },
        new()
        {
            Name = "content",
            Run = data =>
            {
                var reader = new ContentReader(data);
                var op = new ContentOperation();
                while (reader.Next(op))
                {
                    // Reading is the work under test.
                }
            },
            Documented = [typeof(PdfFormatException)],
            Seeds = Seeds.Content,
        },
    ];

    public static FuzzTarget? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>What an application reading a document does: every page's text, annotations and images, and the document's parts.</summary>
    private static void Walk(PdfDocument doc)
    {
        _ = doc.Info.Title;
        _ = doc.Attachments.Count;
        _ = doc.Form.Fields.Count;
        Visit(doc.Outlines);
        foreach (PdfPage page in doc.Pages)
        {
            _ = page.ExtractText();
            _ = page.Annotations.Count;
            foreach (PdfPageImage image in page.GetImages()) _ = image.Decode();
        }

        static void Visit(IEnumerable<PdfOutline> outlines)
        {
            foreach (PdfOutline outline in outlines) Visit(outline.Children);
        }
    }
}
