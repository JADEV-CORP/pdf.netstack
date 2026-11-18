namespace PdfNetStack.Filters;

/// <summary>Parameters of the PNG and TIFF predictors attached to Flate or LZW.</summary>
internal readonly record struct PredictorParams(int Predictor, int Colors, int BitsPerComponent, int Columns)
{
    public int BytesPerPixel => Math.Max(1, (Colors * BitsPerComponent + 7) / 8);

    public int RowBytes => (int)(((long)Colors * BitsPerComponent * Columns + 7) / 8);

    public bool IsValid =>
        Predictor is 1 or 2 or >= 10 and <= 15 &&
        Colors is >= 1 and <= 32 &&
        BitsPerComponent is 1 or 2 or 4 or 8 or 16 &&
        Columns is >= 1 and <= 1 << 24;
}

internal static class Predictors
{
    /// <summary>
    /// Reverses a PNG (10-15) or TIFF (2) predictor into a pooled buffer owned by the caller.
    /// Returns false when the parameters are unsupported.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result, ResourceBudget? budget = null)
    {
        result = null;
        if (!p.IsValid || p.Predictor == 1) return false;
        if (p.Predictor == 2) return TryDecodeTiff(data, p, limit, out result, budget);
        return TryDecodePng(data, p, limit, out result, budget);
    }

    /// <summary>
    /// True when <paramref name="encodedLength"/> bytes of predicted data decoded to
    /// <paramref name="decodedLength"/> bytes of whole rows with nothing left over, so predicting
    /// the samples again gives data of the same shape.
    /// </summary>
    public static bool IsWholeRows(int encodedLength, int decodedLength, PredictorParams p)
    {
        int rowBytes = p.RowBytes;
        if (rowBytes <= 0 || decodedLength == 0 || decodedLength % rowBytes != 0) return false;
        return p.Predictor == 2 ? encodedLength == decodedLength : encodedLength == decodedLength / rowBytes * (rowBytes + 1L);
    }

    private static bool TryDecodePng(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result, ResourceBudget? budget)
    {
        result = null;

        // Row size comes from untrusted parameters. Data shorter than one row is a single
        // partial row, and only its first bytes can ever be output, so the row is clamped.
        int rowBytes = Math.Min(p.RowBytes, Math.Max(1, data.Length));
        int bpp = p.BytesPerPixel;
        int rows = data.Length / (rowBytes + 1);
        int tail = data.Length - rows * (rowBytes + 1);
        long outLength = (long)rows * rowBytes + Math.Max(0, tail - 1);
        if (outLength > limit) throw StreamDecodeException.OverLimit();

        var output = new BoundedBuffer((int)outLength, limit, budget, exact: true);
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        int src = 0;
        while (src < data.Length)
        {
            int filter = data[src++];
            int available = Math.Min(rowBytes, data.Length - src);
            Array.Clear(current, 0, current.Length);
            data.Slice(src, available).CopyTo(current);
            src += available;

            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < rowBytes; i++) current[i] += current[i - bpp];
                    break;
                case 2:
                    for (int i = 0; i < rowBytes; i++) current[i] += previous[i];
                    break;
                case 3:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= bpp ? current[i - bpp] : 0;
                        current[i] += (byte)((left + previous[i]) >> 1);
                    }

                    break;
                case 4:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? current[i - bpp] : 0;
                        int b = previous[i];
                        int c = i >= bpp ? previous[i - bpp] : 0;
                        current[i] += Paeth(a, b, c);
                    }

                    break;
                default:
                    output.Dispose();
                    return false;
            }

            output.Write(current.AsSpan(0, available));
            (previous, current) = (current, previous);
        }

        result = output;
        return true;
    }

    private static bool TryDecodeTiff(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result, ResourceBudget? budget)
    {
        result = null;
        if (p.BitsPerComponent != 8 && p.BitsPerComponent != 16) return false;
        var output = new BoundedBuffer(data.Length, limit, budget);
        output.Write(data);
        Span<byte> r = output.WritableSpan;
        int rowBytes = p.RowBytes;
        int colors = p.Colors;
        for (int rowStart = 0; rowStart + rowBytes <= r.Length; rowStart += rowBytes)
        {
            if (p.BitsPerComponent == 8)
            {
                for (int i = colors; i < rowBytes; i++) r[rowStart + i] += r[rowStart + i - colors];
            }
            else
            {
                int stride = colors * 2;
                for (int i = stride; i + 1 < rowBytes; i += 2)
                {
                    int prev = (r[rowStart + i - stride] << 8) | r[rowStart + i - stride + 1];
                    int cur = (r[rowStart + i] << 8) | r[rowStart + i + 1];
                    int sum = (prev + cur) & 0xFFFF;
                    r[rowStart + i] = (byte)(sum >> 8);
                    r[rowStart + i + 1] = (byte)sum;
                }
            }
        }

        result = output;
        return true;
    }

    /// <summary>
    /// Applies PNG prediction choosing the filter per row with the minimum sum of absolute
    /// differences heuristic. Input length must be an exact multiple of the row size. On .NET 8
    /// and later the rows are filtered and scored 16 bytes at a time where the hardware has
    /// 128 bit vectors; the output is the same, byte for byte, as the scalar code's.
    /// </summary>
    /// <param name="pool">Where a large output array comes from, or null to allocate it.</param>
    /// <param name="vectorize">Use vector instructions when the hardware has them; false for the scalar code alone.</param>
    public static BoundedBuffer EncodePng(ReadOnlySpan<byte> data, PredictorParams p, System.Buffers.ArrayPool<byte>? pool = null, bool vectorize = true)
    {
        int rows = data.Length / p.RowBytes;
        var lease = new BoundedBuffer(rows * (p.RowBytes + 1), long.MaxValue, exact: true, pool: pool);
        EncodeRows(data, p, lease.Extend(rows * (p.RowBytes + 1)), null, vectorize);
        return lease;
    }

    /// <summary>
    /// Writes what <see cref="EncodePng"/> returns to <paramref name="output"/>, one row at a time,
    /// without holding it: for deflating the predicted samples straight from the samples.
    /// </summary>
    public static void WritePng(ReadOnlySpan<byte> data, PredictorParams p, Stream output, bool vectorize = true) =>
        EncodeRows(data, p, default, output, vectorize);

    /// <summary>The rows of PNG prediction, into <paramref name="output"/> or, when it is set, to <paramref name="sink"/>.</summary>
    private static void EncodeRows(ReadOnlySpan<byte> data, PredictorParams p, Span<byte> output, Stream? sink, bool vectorize)
    {
        int rowBytes = p.RowBytes;
        int bpp = p.BytesPerPixel;
        int rows = data.Length / rowBytes;

        // Sub, Up, Average and Paeth candidates of the current row (None is the row itself) and
        // a zero row above the first. Rows for a sink are gathered into chunks of about 64 KiB,
        // as a deflater given a few hundred bytes per call spends its time on the calls.
        byte[] work = Buffers.Rent(5 * rowBytes);
        byte[] chunk = sink is null ? [] : Buffers.Rent(Math.Max(1 << 16, rowBytes + 1));
        int pending = 0;
        try
        {
            Span<byte> zero = work.AsSpan(4 * rowBytes, rowBytes);
            zero.Clear();
            ReadOnlySpan<byte> previous = zero;
            for (int r = 0; r < rows; r++)
            {
                ReadOnlySpan<byte> row = data.Slice(r * rowBytes, rowBytes);
                Span<byte> sub = work.AsSpan(0, rowBytes), up = work.AsSpan(rowBytes, rowBytes),
                    avg = work.AsSpan(2 * rowBytes, rowBytes), paeth = work.AsSpan(3 * rowBytes, rowBytes);
                int done = Math.Min(bpp, rowBytes);
                FilterHead(row, previous, done, sub, up, avg, paeth);
#if NET8_0_OR_GREATER
                if (vectorize && System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) done = PredictorsVector.Filter(row, previous, bpp, done, sub, up, avg, paeth);
#endif
                FilterTail(row, previous, bpp, done, sub, up, avg, paeth);

                // Ties go to the earlier filter, as libpng breaks them.
                int best = 0;
                long bestScore = Score(row, vectorize);
                for (int f = 1; f < 5; f++)
                {
                    long score = Score(work.AsSpan((f - 1) * rowBytes, rowBytes), vectorize);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = f;
                    }
                }

                ReadOnlySpan<byte> chosen = best == 0 ? row : work.AsSpan((best - 1) * rowBytes, rowBytes);
                if (sink is not null && pending + rowBytes + 1 > chunk.Length)
                {
                    sink.Write(chunk, 0, pending);
                    pending = 0;
                }

                Span<byte> target = sink is null ? output.Slice(r * (rowBytes + 1), rowBytes + 1) : chunk.AsSpan(pending, rowBytes + 1);
                target[0] = (byte)best;
                chosen.CopyTo(target[1..]);
                if (sink is not null) pending += rowBytes + 1;

                // The zero row shares the work array; once past the first row it is not read again.
                previous = row;
            }

            if (pending > 0) sink!.Write(chunk, 0, pending);
        }
        finally
        {
            Buffers.Return(work);
            if (chunk.Length > 0) Buffers.Return(chunk);
        }
    }

    /// <summary>The first pixel of a row, whose left and upper left neighbors are zero.</summary>
    private static void FilterHead(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int count, Span<byte> sub, Span<byte> up, Span<byte> avg, Span<byte> paeth)
    {
        for (int i = 0; i < count; i++)
        {
            byte x = row[i];
            byte b = previous[i];
            sub[i] = x;
            up[i] = (byte)(x - b);
            avg[i] = (byte)(x - (b >> 1));
            paeth[i] = (byte)(x - b);
        }
    }

    /// <summary>Bytes from <paramref name="start"/> to the end of the row, one at a time.</summary>
    private static void FilterTail(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int bpp, int start, Span<byte> sub, Span<byte> up, Span<byte> avg, Span<byte> paeth)
    {
        for (int i = start; i < row.Length; i++)
        {
            byte x = row[i];
            byte a = row[i - bpp];
            byte b = previous[i];
            byte c = previous[i - bpp];
            sub[i] = (byte)(x - a);
            up[i] = (byte)(x - b);
            avg[i] = (byte)(x - ((a + b) >> 1));
            paeth[i] = (byte)(x - Paeth(a, b, c));
        }
    }

    // Sum of absolute values when bytes are read as signed, the heuristic libpng uses.
    private static long Score(ReadOnlySpan<byte> row, bool vectorize)
    {
        int i = 0;
        long score = 0;
#if NET8_0_OR_GREATER
        if (vectorize && System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) score = PredictorsVector.Score(row, out i);
#endif
        for (; i < row.Length; i++)
        {
            byte v = row[i];
            score += v < 128 ? v : 256 - v;
        }

        return score;
    }

    internal static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return (byte)a;
        return pb <= pc ? (byte)b : (byte)c;
    }
}
