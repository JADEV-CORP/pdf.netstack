using PdfNetStack.Writing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Keys that bind the check of one lossy image change to exactly what it checked. The optimizer
/// records a key once the check has passed, when it writes the image; the proof of its own output
/// then finds the key from the two images as it reads them instead of decoding, resampling and
/// comparing them again. A key covers every input the check reads: both images after their
/// general filters (SHA-256), how each is coded and sized, the JBIG2Globals of each, and the gate.
/// Any difference in any of them gives another key.
/// </summary>
internal static class ImageProof
{
    /// <summary>
    /// SHA-256 of an image after its general filters, as far as its decoding reads it: samples
    /// only up to the size the dictionary gives them, which is all a decoder takes, and coded data
    /// whole.
    /// </summary>
    public static byte[] Digest(ReadOnlySpan<byte> data, ImageGeometry g)
    {
        if (g.Coding == ImageCoding.Raw)
        {
            long size = g.Palette is not null ? ((((long)g.Width * g.PaletteBits) + 7) / 8) * g.Height
                : g.Bits == 1 ? (long)((g.Width + 7) / 8) * g.Height
                : (long)g.Width * g.Height * g.Components;
            if (size < data.Length) data = data.Slice(0, (int)size);
        }

        return Compat.Sha256(data);
    }

    /// <summary>The key of the check that a resampled, converted or quantized image keeps to its source (<see cref="LossyImages.Verify"/>).</summary>
    public static string Resampled(ImageGeometry a, byte[] sourceDigest, byte[]? globalsA, ImageGeometry b, byte[] outputDigest, byte[]? globalsB, double? gate)
    {
        var key = new ByteSink(256);
        key.Write((byte)'R');
        Write(key, a, sourceDigest, globalsA);
        Write(key, b, outputDigest, globalsB);
        Write(key, gate);
        return Convert.ToBase64String(Compat.Sha256(key.WrittenSpan));
    }

    /// <summary>The key of the check that a requantized JPEG keeps the gate (<see cref="LossyImages.RequantizationPasses"/>).</summary>
    public static string Requantized(ReadOnlySpan<byte> source, int? colorTransform, ReadOnlySpan<byte> output, double gate)
    {
        var key = new ByteSink(96);
        key.Write((byte)'Q');
        key.Write(Compat.Sha256(source));
        Write(key, colorTransform ?? -1);
        key.Write(Compat.Sha256(output));
        Write(key, gate);
        return Convert.ToBase64String(Compat.Sha256(key.WrittenSpan));
    }

    private static void Write(ByteSink key, ImageGeometry g, byte[] digest, byte[]? globals)
    {
        Write(key, g.Width);
        Write(key, g.Height);
        Write(key, g.Components);
        Write(key, (int)g.Coding);
        Write(key, g.ColorTransform ?? -1);
        Write(key, g.Bits);
        if (g.Ccitt is { } c)
        {
            key.Write((byte)'C');
            Write(key, c.K);
            Write(key, c.Columns);
            Write(key, c.Rows);
            key.Write((byte)((c.EndOfLine ? 1 : 0) | (c.EncodedByteAlign ? 2 : 0) | (c.EndOfBlock ? 4 : 0) | (c.BlackIs1 ? 8 : 0)));
        }

        if (g.Palette is { } palette)
        {
            key.Write((byte)'P');
            Write(key, g.PaletteBits);
            Write(key, palette.Length);
            key.Write(palette);
        }

        key.Write((byte)'|');
        key.Write(digest);
        key.Write((byte)(globals is null ? 0 : 1));
        if (globals is not null) key.Write(Compat.Sha256(globals));
    }

    private static void Write(ByteSink key, double? gate)
    {
        key.Write((byte)(gate is null ? 0 : 1));
        long bits = BitConverter.DoubleToInt64Bits(gate ?? 0);
        for (int i = 0; i < 8; i++) key.Write((byte)(bits >> (8 * i)));
    }

    private static void Write(ByteSink key, int value)
    {
        for (int i = 0; i < 4; i++) key.Write((byte)(value >> (8 * i)));
    }
}
