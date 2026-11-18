namespace PdfNetStack.Imaging;

/// <summary>
/// The MQ arithmetic encoder (T.88, Annex E.2), the inverse of <see cref="Jbig2ArithmeticDecoder"/>
/// with the same contexts: one byte each, the probability state index shifted left once, then the
/// MPS. Output stops growing at a limit, so a caller comparing encodings gives up on a loser early.
/// </summary>
internal sealed class Jbig2ArithmeticEncoder(int maxBytes)
{
    private byte[] _output = new byte[Math.Max(16, Math.Min(maxBytes, 1 << 16))];
    private int _length;
    private uint _c;
    private uint _a = 0x8000;

    // INITENC (E.2.8): the byte before the first one is 0, so twelve bits pass before the first is
    // written, which keeps a carry from ever reaching that byte.
    private int _ct = 12;
    private int _b;
    private bool _started;

    /// <summary>True once the output would have grown past the limit; it is then incomplete.</summary>
    public bool Overflow { get; private set; }

    /// <summary>ENCODE (E.2.2): codes <paramref name="bit"/> in the context at <paramref name="index"/>.</summary>
    public void Encode(byte[] contexts, int index, int bit)
    {
        int state = contexts[index], i = state >> 1, mps = state & 1;
        uint qe = Jbig2ArithmeticDecoder.Qe[i];
        _a -= qe;
        if (bit == mps)
        {
            // CODEMPS (E.2.5): nothing to renormalize while the interval stays large.
            if ((_a & 0x8000) != 0)
            {
                _c += qe;
                return;
            }

            if (_a < qe) _a = qe;
            else _c += qe;
            contexts[index] = (byte)((Jbig2ArithmeticDecoder.NextMps[i] << 1) | mps);
        }
        else
        {
            // CODELPS (E.2.4), with the conditional exchange.
            if (_a < qe) _c += qe;
            else _a = qe;
            if (i < Jbig2ArithmeticDecoder.Switch.Length && Jbig2ArithmeticDecoder.Switch[i]) mps ^= 1;
            contexts[index] = (byte)((Jbig2ArithmeticDecoder.NextLps[i] << 1) | mps);
        }

        // RENORME (E.2.6).
        do
        {
            _a <<= 1;
            _c <<= 1;
            if (--_ct == 0) ByteOut();
        }
        while ((_a & 0x8000) == 0);
    }

    /// <summary>
    /// FLUSH (E.2.9): sets the remaining bits so the decoder lands inside the final interval, then
    /// ends the data with the 0xFFAC marker, after which every decoder reads only 1 bits.
    /// </summary>
    /// <returns>The coded data, or null when it outgrew the limit.</returns>
    public byte[]? Finish()
    {
        // SETBITS (E.2.10).
        uint top = _c + _a;
        _c |= 0xFFFF;
        if (_c >= top) _c -= 0x8000;

        _c <<= _ct;
        ByteOut();
        _c <<= _ct;
        ByteOut();
        Emit(_b);
        if (_b != 0xFF) Emit(0xFF);
        Emit(0xAC);
        if (Overflow) return null;
        Array.Resize(ref _output, _length);
        return _output;
    }

    /// <summary>BYTEOUT (E.2.7): moves to the next byte, carrying into this one or stuffing a bit after 0xFF.</summary>
    private void ByteOut()
    {
        if (_b != 0xFF && _c >= 0x8000000)
        {
            _b++;
            if (_b != 0xFF)
            {
                Next(19);
                return;
            }

            _c &= 0x7FFFFFF;
        }

        Next(_b == 0xFF ? 20 : 19);
    }

    /// <summary>
    /// Writes the finished byte and starts the next from the top bits of the code register; a
    /// carry those bits held went into the finished byte already.
    /// </summary>
    private void Next(int shift)
    {
        if (_started) Emit(_b);
        _started = true;
        _b = (int)(_c >> shift) & 0xFF;
        _c &= shift == 20 ? 0xFFFFFu : 0x7FFFFu;
        _ct = shift == 20 ? 7 : 8;
    }

    private void Emit(int value)
    {
        if (Overflow) return;
        if (_length == _output.Length)
        {
            if (_length >= maxBytes)
            {
                Overflow = true;
                return;
            }

            Array.Resize(ref _output, (int)Math.Min(maxBytes, Math.Max(16L, 2L * _length)));
        }

        _output[_length++] = (byte)value;
    }
}

/// <summary>
/// Codes a bilevel image as JBIG2 (ITU-T T.88) for a PDF JBIG2Decode stream: the embedded
/// organisation without a file header or end of page (ISO 32000-1, 7.4.7), one page information
/// segment and one immediate generic region over the whole page, arithmetic coded with template 0,
/// its adaptive pixels at their nominal places, and typical prediction (TPGDON), as jbig2enc does in
/// generic mode. Generic coding only, which is lossless: symbol coding matches glyphs by likeness,
/// which can put one character in place of another.
/// </summary>
internal static class Jbig2Encoder
{
    private const int PageInformation = 48;
    private const int ImmediateGenericRegion = 38;
    private const int HeaderBytes = 11;
    private const int PageInformationBytes = 19;
    private const int RegionHeaderBytes = 17 + 1 + 8;

    // T.88, 6.2.5.7 and Figure 8: the context that codes SLTP with template 0.
    private const int TypicalContext = 0x9B25;

    /// <summary>The nominal adaptive pixels of template 0 (T.88, Table 5), as x, y pairs.</summary>
    private static readonly sbyte[] NominalAt = [3, -1, -3, -1, 2, -2, -2, -2];

    /// <summary>
    /// The data for a page of <paramref name="height"/> rows of <paramref name="width"/> pixels,
    /// packed most significant bit first and padded to a byte, 1 for black as T.88 codes it, or
    /// with <paramref name="invert"/> 0 for black. Padding bits are ignored.
    /// </summary>
    /// <param name="maxBytes">Gives up, returning null, once the data would grow past this many bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is out of range or the rows are too short.</exception>
    public static byte[]? EncodeGeneric(ReadOnlySpan<byte> rows, int width, int height, int maxBytes, bool invert = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The size is out of range.");
        int stride = (width + 7) / 8;
        if (rows.Length < (long)stride * height) throw new ArgumentOutOfRangeException(nameof(rows), "The rows are shorter than the size.");
        int fixedBytes = (2 * HeaderBytes) + PageInformationBytes + RegionHeaderBytes;
        if (maxBytes <= fixedBytes) return null;

        // Rows with their padding cleared and a zero byte past each, under two zero rows: the
        // template reads up to four pixels right of the one coded and two rows up, and those read 0
        // outside the bitmap (T.88, 6.2.5.2).
        int pitch = stride + 1;
        var bitmap = new byte[(long)pitch * (height + 2)];
        byte lastMask = (byte)(0xFF << ((8 - (width & 7)) & 7));
        for (int y = 0; y < height; y++)
        {
            int at = (y + 2) * pitch;
            Span<byte> target = bitmap.AsSpan(at, stride);
            rows.Slice(y * stride, stride).CopyTo(target);
            if (invert)
            {
                for (int i = 0; i < target.Length; i++) target[i] = (byte)~target[i];
            }

            target[stride - 1] &= lastMask;
        }

        byte[]? coded = Generic(bitmap, pitch, width, height, maxBytes - fixedBytes);
        if (coded is null) return null;

        var output = new byte[fixedBytes + coded.Length];
        int o = SegmentHeader(output, 0, 0, PageInformation, PageInformationBytes);
        o = Int32(output, o, width);
        o = Int32(output, o, height);

        // No resolution; flags: eventually lossless, default pixel 0, default operator OR; not striped.
        o = Int32(output, o, 0);
        o = Int32(output, o, 0);
        output[o++] = 0x01;
        output[o++] = 0;
        output[o++] = 0;

        o = SegmentHeader(output, o, 1, ImmediateGenericRegion, RegionHeaderBytes + coded.Length);
        o = Int32(output, o, width);
        o = Int32(output, o, height);
        o = Int32(output, o, 0);
        o = Int32(output, o, 0);

        // External combination operator OR; then MMR off, template 0, TPGDON on.
        output[o++] = 0;
        output[o++] = 0x08;
        foreach (sbyte v in NominalAt) output[o++] = (byte)v;
        coded.CopyTo(output, o);
        return output;
    }

    /// <summary>The arithmetic coded rows (T.88, 6.2.5), or null past <paramref name="maxBytes"/>.</summary>
    private static byte[]? Generic(byte[] bitmap, int pitch, int width, int height, int maxBytes)
    {
        var encoder = new Jbig2ArithmeticEncoder(maxBytes);
        var contexts = new byte[1 << 16];
        int stride = pitch - 1;
        int ltp = 0;
        for (int y = 0; y < height; y++)
        {
            int row = (y + 2) * pitch, up1 = row - pitch, up2 = up1 - pitch;

            // TPGDON (6.2.5.7): a row the same as the one above is skipped, its sameness coded as a
            // change of LTP. The first row is compared with the white row above the bitmap.
            int same = bitmap.AsSpan(row, stride).SequenceEqual(bitmap.AsSpan(up1, stride)) ? 1 : 0;
            encoder.Encode(contexts, TypicalContext, same ^ ltp);
            ltp = same;
            if (same != 0) continue;

            // Template 0 with its adaptive pixels at their nominal places reads 16 pixels that slide
            // one column per pixel: 5 two rows up (x - 2 to x + 2), 7 one row up (x - 3 to x + 3)
            // and the 4 before x. In the decoder's context order they are those windows side by side.
            int w1 = (Pixel(bitmap, up2, 0) << 2) | (Pixel(bitmap, up2, 1) << 1) | Pixel(bitmap, up2, 2);
            int w2 = (Pixel(bitmap, up1, 0) << 3) | (Pixel(bitmap, up1, 1) << 2) | (Pixel(bitmap, up1, 2) << 1) | Pixel(bitmap, up1, 3);
            int w3 = 0;
            for (int x = 0; x < width; x++)
            {
                int bit = Pixel(bitmap, row, x);
                encoder.Encode(contexts, w3 | (w2 << 4) | (w1 << 11), bit);
                w1 = ((w1 << 1) | Pixel(bitmap, up2, x + 3)) & 0x1F;
                w2 = ((w2 << 1) | Pixel(bitmap, up1, x + 4)) & 0x7F;
                w3 = ((w3 << 1) | bit) & 0x0F;
            }

            if (encoder.Overflow) return null;
        }

        return encoder.Finish();
    }

    /// <summary>The pixel in column <paramref name="x"/> of the row at <paramref name="row"/>; columns past the width read the zero padding.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Pixel(byte[] bitmap, int row, int x) => (bitmap[row + (x >> 3)] >> (7 - (x & 7))) & 1;

    /// <summary>A segment header (7.2): its number, type, no referred-to segments, page 1 and the data length.</summary>
    private static int SegmentHeader(byte[] output, int at, int number, int type, int length)
    {
        at = Int32(output, at, number);
        output[at++] = (byte)type;
        output[at++] = 0;
        output[at++] = 1;
        return Int32(output, at, length);
    }

    private static int Int32(byte[] output, int at, int value)
    {
        output[at] = (byte)(value >> 24);
        output[at + 1] = (byte)(value >> 16);
        output[at + 2] = (byte)(value >> 8);
        output[at + 3] = (byte)value;
        return at + 4;
    }

    /// <summary>
    /// True when <paramref name="data"/> has the form every reader decodes alike, the form this
    /// encoder writes: a page information segment of exactly this size, white by default and not
    /// striped; one immediate generic region over the whole page, arithmetic coded with a standard
    /// template and its adaptive pixels at their nominal places, drawn by OR or REPLACE, its coded
    /// data ended by the 0xFFAC marker, so no reader ever reads past it; then nothing but an end of
    /// page or end of file. Anything else could leave a reader to its own defaults.
    /// </summary>
    public static bool IsPlainGeneric(ReadOnlySpan<byte> data, int width, int height)
    {
        int at = 0;
        bool page = false, region = false, ended = false;
        while (at < data.Length)
        {
            if (ended || data.Length - at < HeaderBytes) return false;
            int flags = data[at + 4], type = flags & 0x3F;

            // No referred-to segments, a one byte page association to page 1.
            if ((flags & 0x40) != 0 || data[at + 5] >> 5 != 0 || data[at + 6] != 1) return false;
            long length = UInt32(data, at + 7);
            at += HeaderBytes;
            if (length > data.Length - at) return false;
            ReadOnlySpan<byte> body = data.Slice(at, (int)length);
            at += (int)length;
            switch (type)
            {
                case PageInformation when !page && !region:
                    // Size, no stripes, and only the lossless, default pixel and operator flags as written here.
                    if (body.Length != PageInformationBytes || UInt32(body, 0) != (uint)width || UInt32(body, 4) != (uint)height) return false;
                    if ((body[16] & ~0x01) != 0 || body[17] != 0 || body[18] != 0) return false;
                    page = true;
                    break;
                case ImmediateGenericRegion or 39 when page && !region:
                    if (!PlainRegion(body, width, height)) return false;
                    region = true;
                    break;
                case 49 or 51 when region:
                    if (body.Length != 0) return false;
                    ended = true;
                    break;
                default:
                    return false;
            }
        }

        return page && region;
    }

    /// <summary>The generic region of <see cref="IsPlainGeneric"/>.</summary>
    private static bool PlainRegion(ReadOnlySpan<byte> body, int width, int height)
    {
        if (body.Length < 18 + 2) return false;
        if (UInt32(body, 0) != (uint)width || UInt32(body, 4) != (uint)height || UInt32(body, 8) != 0 || UInt32(body, 12) != 0) return false;
        if (body[16] is not (0 or 4)) return false;

        // Arithmetic coding, no extended template; any of the four templates, with or without TPGDON.
        int flags = body[17];
        if ((flags & ~0x0E) != 0) return false;
        int template = (flags >> 1) & 3, pairs = template == 0 ? 4 : 1;
        if (body.Length < 18 + (2 * pairs) + 2) return false;
        int[] nominal = Jbig2Generic.NominalAt(template);
        for (int i = 0; i < nominal.Length; i++)
        {
            if ((sbyte)body[18 + i] != nominal[i]) return false;
        }

        return body[^2] == 0xFF && body[^1] == 0xAC;
    }

    private static uint UInt32(ReadOnlySpan<byte> data, int at) => ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
}
