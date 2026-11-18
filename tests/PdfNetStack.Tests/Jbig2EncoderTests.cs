using System.Diagnostics;
using PdfNetStack.Imaging;
using PdfNetStack.Optimization;

namespace PdfNetStack.Tests;

/// <summary>
/// The JBIG2 generic region encoder: round trips through the library's decoder at every width,
/// with jbig2dec where it is installed, the size against Group 4, the early give-up, and the form
/// the proof accepts from any writer.
/// </summary>
[TestClass]
public sealed class Jbig2EncoderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jbig2", name);

    /// <summary>Packed rows, 1 for black, from a seeded mix of noise, runs and repeated rows.</summary>
    private static byte[] Rows(int width, int height, int seed)
    {
        var random = new Random(seed);
        int stride = (width + 7) / 8;
        var rows = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = rows.AsSpan(y * stride, stride);
            switch (random.Next(4))
            {
                case 0 when y > 0:
                    // The row above again: typical prediction skips it.
                    rows.AsSpan((y - 1) * stride, stride).CopyTo(row);
                    break;
                case 1:
                    // Long runs, as text and rules make.
                    for (int x = random.Next(Math.Max(1, width)); x < width; x += 1 + random.Next(64))
                    {
                        for (int run = random.Next(40); run > 0 && x < width; run--, x++) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }

                    break;
                case 2:
                    random.NextBytes(row);
                    break;
                default:
                    // White, but for the padding bits, which the encoder must ignore.
                    row[^1] = (byte)(random.Next(256) & (0xFF >> (((width - 1) & 7) + 1)));
                    break;
            }
        }

        return rows;
    }

    private static bool SamePixels(byte[] a, byte[] b, int width, int height) => BilevelImages.SamePixels(a, b, width, height);

    [TestMethod]
    public void RoundTripsAtEveryWidth()
    {
        int seed = 0;
        foreach (int width in Enumerable.Range(1, 40).Concat([63, 64, 65, 127, 1000, 2551]))
        {
            foreach (int height in new[] { 1, 2, 3, 7, 33 })
            {
                byte[] rows = Rows(width, height, ++seed);
                byte[] data = Jbig2Encoder.EncodeGeneric(rows, width, height, 1 << 24)!;
                Assert.IsNotNull(data);
                byte[] decoded = Jbig2Decoder.Decode(data, null, width, height);
                Assert.IsTrue(SamePixels(rows, decoded, width, height), $"{width} by {height}");
                Assert.IsTrue(Jbig2Encoder.IsPlainGeneric(data, width, height), $"{width} by {height}");
            }
        }
    }

    [TestMethod]
    public void NominalTemplateDecodesAsTheGeneralCase()
    {
        // The decoder reads template 0 with nominal adaptive pixels through sliding windows; an
        // empty skip bitmap sends the same data through the general case, pixel by pixel.
        foreach ((int width, int height, int seed) in new[] { (1, 9, 1), (13, 40, 2), (300, 200, 3), (1001, 77, 4) })
        {
            byte[] rows = Rows(width, height, seed);
            byte[] data = Jbig2Encoder.EncodeGeneric(rows, width, height, 1 << 24)!;
            const int coded = 11 + 19 + 11 + 26;
            Jbig2Bitmap Decode(Jbig2Bitmap? skip) => Jbig2Generic.Decode(
                new Jbig2ArithmeticDecoder(data, coded, data.Length), Jbig2Generic.GenericContexts(), width, height, 0, true, skip, Jbig2Generic.NominalAt(0), new Jbig2Budget(1L << 30));
            Jbig2Bitmap fast = Decode(null), general = Decode(Jbig2Bitmap.Create(width, height, new Jbig2Budget(1L << 30)));
            CollectionAssert.AreEqual(general.Data, fast.Data, $"{width} by {height}");
            Assert.IsTrue(SamePixels(rows, fast.Data, width, height), $"{width} by {height}");
        }
    }

    [TestMethod]
    public void BlankAndFullPagesRoundTrip()
    {
        foreach (byte fill in new byte[] { 0x00, 0xFF })
        {
            var rows = new byte[((2550 + 7) / 8) * 3300];
            Array.Fill(rows, fill);
            byte[] data = Jbig2Encoder.EncodeGeneric(rows, 2550, 3300, 1 << 20)!;
            Assert.IsLessThan(200, data.Length, "typical prediction codes a uniform page in a few bytes");
            Assert.IsTrue(SamePixels(rows, Jbig2Decoder.Decode(data, null, 2550, 3300), 2550, 3300));
        }
    }

    [TestMethod]
    public void SamplesAreInvertedForTheFilter()
    {
        // PDF samples have 0 for black; JBIG2Decode gives the inverse of the T.88 bitmap.
        byte[] samples = BilevelOptimizationTests.ScanRows(500, 300);
        byte[] data = BilevelImages.Jbig2(samples, 500, 300, 1 << 20)!;
        byte[] bitmap = Jbig2Decoder.Decode(data, null, 500, 300);
        Assert.IsTrue(SamePixels([.. samples.Select(b => (byte)~b)], bitmap, 500, 300));
        Assert.IsTrue(SamePixels(samples, BilevelImages.Jbig2Pixels(data, 500, 300, 1 << 24)!, 500, 300));
    }

    [TestMethod]
    public void ScannedTextIsSmallerThanGroup4()
    {
        byte[] samples = BilevelOptimizationTests.ScanRows(1700, 2200);
        byte[] g4 = CcittEncoder.EncodeGroup4(samples, 1700, 2200, false, 1 << 24)!;
        byte[] jbig2 = BilevelImages.Jbig2(samples, 1700, 2200, 1 << 24)!;
        Assert.IsLessThan(g4.Length * 9 / 10, jbig2.Length, $"JBIG2 {jbig2.Length}, Group 4 {g4.Length}");
    }

    [TestMethod]
    public void GivesUpPastItsLimit()
    {
        byte[] rows = Rows(800, 600, 7);
        byte[] whole = Jbig2Encoder.EncodeGeneric(rows, 800, 600, 1 << 24)!;
        Assert.IsNull(Jbig2Encoder.EncodeGeneric(rows, 800, 600, whole.Length - 1));
        CollectionAssert.AreEqual(whole, Jbig2Encoder.EncodeGeneric(rows, 800, 600, whole.Length));
        Assert.IsNull(Jbig2Encoder.EncodeGeneric(rows, 800, 600, 40), "the headers alone do not fit");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Jbig2Encoder.EncodeGeneric(rows.AsSpan(0, 10), 800, 600, 1 << 24));
    }

    /// <summary>jbig2dec reads the embedded stream to the same pixels, where it is installed.</summary>
    [TestMethod]
    public void ReferenceDecoderReadsTheSamePixels()
    {
        string? jbig2dec = new[] { "/opt/homebrew/bin/jbig2dec", "/usr/local/bin/jbig2dec", "/usr/bin/jbig2dec" }.FirstOrDefault(File.Exists);
        if (jbig2dec is null) Assert.Inconclusive("jbig2dec is not installed.");

        string folder = Path.Combine(Path.GetTempPath(), "pdfnetstack-jbig2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach ((int width, int height, byte[] rows) in new[] { (1700, 2200, BilevelOptimizationTests.ScanRows(1700, 2200, seed: 9)), (97, 61, Rows(97, 61, 3)), (1, 5, Rows(1, 5, 4)) })
            {
                string input = Path.Combine(folder, $"{width}.jb2"), output = Path.Combine(folder, $"{width}.pbm");
                File.WriteAllBytes(input, Jbig2Encoder.EncodeGeneric(rows, width, height, 1 << 24)!);
                using var process = Process.Start(new ProcessStartInfo(jbig2dec, ["-q", "-e", "-t", "pbm", "-o", output, input]) { RedirectStandardError = true })!;
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, errors);

                // A binary PBM: "P4", the size, one white space byte, then rows with 1 for black.
                byte[] pbm = File.ReadAllBytes(output);
                int header = 0;
                for (int fields = 0; fields < 3; header++)
                {
                    if (char.IsWhiteSpace((char)pbm[header]) && !char.IsWhiteSpace((char)pbm[header - 1])) fields++;
                }

                Assert.IsTrue(SamePixels(rows, pbm.AsSpan(header).ToArray(), width, height), $"{width} by {height}");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void OnlyThePlainFormCountsAsReadAlike()
    {
        byte[] rows = Rows(120, 40, 11);
        byte[] good = Jbig2Encoder.EncodeGeneric(rows, 120, 40, 1 << 20)!;
        Assert.IsTrue(Jbig2Encoder.IsPlainGeneric(good, 120, 40));
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(good, 121, 40), "a page of another size");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(good, 120, 39), "a page of another size");

        // Offsets in what the encoder writes: two 11 byte headers, the page information, then the region.
        const int page = 11, region = 11 + 19 + 11;
        byte[] Mutated(int at, byte value)
        {
            byte[] copy = (byte[])good.Clone();
            copy[at] = value;
            return copy;
        }

        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(page + 16, 0x05), 120, 40), "black by default");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(page + 17, 0x80), 120, 40), "striped");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(region + 16, 0x02), 120, 40), "drawn by XOR");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(region + 17, 0x09), 120, 40), "MMR");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(region + 18, 4), 120, 40), "an adaptive pixel off its nominal place");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(region + 8, 1), 120, 40), "a region off the page origin");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(Mutated(region - 11 + 5, 0x20), 120, 40), "referred-to segments");
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(good.AsSpan(0, good.Length - 1), 120, 40), "cut short");

        // No end marker: some readers stop, others read on.
        byte[] unmarked = good.AsSpan(0, good.Length - 2).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(unmarked.AsSpan(region - 4), unmarked.Length - region);
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(unmarked, 120, 40));

        // A second region, and a file header.
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric([.. good, .. good.AsSpan(region - 11)], 120, 40));
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric([0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0, 0, 0, 1, .. good], 120, 40));

        // An end of page after the region is fine.
        Assert.IsTrue(Jbig2Encoder.IsPlainGeneric([.. good, 0, 0, 0, 2, 49, 0, 1, 0, 0, 0, 0], 120, 40));

        // Symbol coding and globals, as scanners write them, never count.
        Assert.IsFalse(Jbig2Encoder.IsPlainGeneric(File.ReadAllBytes(Fixture("annexh-p1.jb2")), 64, 56));
    }
}
