using System.IO.Compression;

namespace PdfNetStack.Filters;

/// <summary>Raised when a stream cannot be decoded or exceeds the decoded size ceiling.</summary>
internal sealed class StreamDecodeException : Exception
{
    public StreamDecodeException(string message) : base(message) { }

    public StreamDecodeException(string message, Exception inner) : base(message, inner) { }

    public StreamDecodeException() { }

    /// <summary>True when the data may be sound but decodes past the size ceiling.</summary>
    public bool IsLimit { get; init; }

    /// <summary>The exception for output past the ceiling given to the decoder.</summary>
    public static StreamDecodeException OverLimit() => new("Decoded stream exceeds the configured size limit.") { IsLimit = true };
}

/// <summary>
/// Growable output buffer with a hard ceiling, the defence against decompression bombs, and an
/// optional document budget that every allocation is charged to before it is made. Small arrays
/// come from <see cref="System.Buffers.ArrayPool{T}.Shared"/>; larger ones are allocated for the
/// call and left to the collector, as the shared pool keeps what it is given, per core, for the
/// life of the process: a few large decodes would otherwise stay in memory after the call.
/// </summary>
internal sealed class BoundedBuffer : IDisposable
{
    /// <summary>Largest array taken from and returned to the shared pool.</summary>
    internal const int PooledMax = 1 << 20;

    private readonly long _limit;
    private readonly ResourceBudget? _budget;
    private readonly System.Buffers.ArrayPool<byte>? _pool;
    private byte[] _buffer;
    private int _length;

    // Bytes charged to the budget: the capacity, until Settle gives back what was not written.
    private long _charged;

    /// <param name="initialCapacity">The expected size, a guess unless <paramref name="exact"/>.</param>
    /// <param name="budget">The document's budget, or null for data the library made itself.</param>
    /// <param name="exact">
    /// The size is known, as for predictors or an image's samples: it is allocated whole, up to
    /// the limit. A guess is capped at 64 MiB, as a compression ratio says little about one stream.
    /// </param>
    /// <param name="pool">Where arrays past <see cref="PooledMax"/> come from; null takes the budget's.</param>
    public BoundedBuffer(int initialCapacity, long limit, ResourceBudget? budget = null, bool exact = false, System.Buffers.ArrayPool<byte>? pool = null)
    {
        _limit = limit;
        _budget = budget;
        _pool = pool ?? budget?.Pool;
        int size = Math.Max(256, Math.Min(initialCapacity, (int)Math.Min(limit, exact ? Compat.MaxArrayLength : 1 << 26)));
        Charge(size);
        _buffer = Buffers.Rent(size, _pool);
    }

    public int Length => _length;

    public void Write(byte b)
    {
        if (_length == _buffer.Length) Grow(1);
        _buffer[_length++] = b;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length) Grow(data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Appends <paramref name="count"/> bytes and returns them for the caller to fill.</summary>
    public Span<byte> Extend(int count)
    {
        if (_length + count > _buffer.Length) Grow(count);
        Span<byte> span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    /// <summary>Copies an earlier slice of the output to the end, as used by LZW.</summary>
    public void CopyWithin(int start, int count)
    {
        if (_length + count > _buffer.Length) Grow(count);
        Buffer.BlockCopy(_buffer, start, _buffer, _length, count);
        _length += count;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    /// <summary>
    /// The written bytes as an array the caller owns: the buffer itself when it holds exactly
    /// them and is not pooled, which saves a copy of a large decode; a copy otherwise. The
    /// buffer is empty afterwards.
    /// </summary>
    public byte[] DetachArray()
    {
        if (_length != _buffer.Length || _buffer.Length <= PooledMax || _pool is not null) return ToArray();
        Settle();
        byte[] whole = _buffer;
        _buffer = [];
        _length = 0;
        return whole;
    }

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    /// <summary>Mutable view of the written bytes, for in-place transforms.</summary>
    public Span<byte> WritableSpan => _buffer.AsSpan(0, _length);

    /// <summary>
    /// Gives back to the budget the capacity that was charged but not written, once the output is
    /// complete, so the budget counts the bytes decoded rather than the arrays that held them.
    /// </summary>
    public void Settle()
    {
        if (_budget is null || _charged <= _length) return;
        _budget.Refund(_charged - _length);
        _charged = _length;
    }

    public void Dispose()
    {
        Settle();
        byte[] buffer = _buffer;
        _buffer = [];
        Buffers.Return(buffer, _pool);
    }

    private void Grow(int needed)
    {
        long required = (long)_length + needed;
        if (required > _limit) throw StreamDecodeException.OverLimit();
        long next = Math.Max(required, Math.Min(_limit, (long)_buffer.Length * 2));
        if (next > Compat.MaxArrayLength) throw new StreamDecodeException("Decoded stream is too large.");
        Charge(next - _buffer.Length);
        byte[] grown = Buffers.Rent((int)next, _pool);
        Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
        Buffers.Return(_buffer, _pool);
        _buffer = grown;
    }

    private void Charge(long bytes)
    {
        if (_budget is null) return;
        _budget.ChargeDecodedBytes(bytes);
        _charged += bytes;
    }

}

/// <summary>
/// Working arrays for decoding and encoding. Up to <see cref="BoundedBuffer.PooledMax"/> they come
/// from <see cref="System.Buffers.ArrayPool{T}.Shared"/>; larger ones from the caller's own pool,
/// one an optimization run drops when it ends, or else are allocated and left to the collector:
/// the shared pool keeps what it is given, per core, for the life of the process.
/// </summary>
internal static class Buffers
{
    public static byte[] Rent(int size, System.Buffers.ArrayPool<byte>? large = null) =>
        size <= BoundedBuffer.PooledMax ? System.Buffers.ArrayPool<byte>.Shared.Rent(size)
        : large?.Rent(size) ?? Compat.AllocateUninitialized(size);

    /// <param name="large">The pool <paramref name="buffer"/> was rented from when it is large: the same as given to <see cref="Rent"/>.</param>
    public static void Return(byte[] buffer, System.Buffers.ArrayPool<byte>? large = null)
    {
        // Rent hands out shared arrays only up to PooledMax, so the length tells them apart.
        if (buffer.Length is > 0 and <= BoundedBuffer.PooledMax) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        else if (buffer.Length > BoundedBuffer.PooledMax) large?.Return(buffer);
    }
}

internal static class Decoders
{
    /// <summary>
    /// Inflates zlib data. Raw deflate without the zlib header is deliberately not accepted:
    /// arbitrary bytes can inflate into garbage without error, and re-encoding that garbage as a
    /// valid stream would make other readers draw something they currently ignore.
    /// </summary>
    /// <param name="size">The decoded size when it is known, else 0.</param>
    public static BoundedBuffer Flate(ReadOnlyMemory<byte> data, long limit, ResourceBudget? budget = null, int size = 0) => InflateWith(data, limit, raw: false, budget, size);

    private static BoundedBuffer InflateWith(ReadOnlyMemory<byte> data, long limit, bool raw, ResourceBudget? budget, int size)
    {
        var output = size > 0
            ? new BoundedBuffer(size, limit, budget, exact: true)
            : new BoundedBuffer((int)Math.Min(int.MaxValue, data.Length * 4L), limit, budget);
        byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var input = new ReadOnlyMemoryStream(data);
            using Stream inflater = raw
                ? new DeflateStream(input, CompressionMode.Decompress)
                : Compat.ZlibDecompress(input);
            int read;
            while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk.AsSpan(0, read));
            }

#if !NET8_0_OR_GREATER
            // .NET Standard's DeflateStream ignores the zlib trailer; check it as modern .NET does.
            if (!raw && !Compat.AdlerMatches(data.Span, output.Span)) throw new InvalidDataException("zlib checksum mismatch.");
#endif
            return output;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Most runtimes raise InvalidDataException; newer zlib-ng builds raise ZLibException,
            // an IOException. Either way the data is not valid Flate.
            output.Dispose();
            throw new StreamDecodeException("Invalid Flate data.", ex);
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public static BoundedBuffer Lzw(ReadOnlySpan<byte> data, int earlyChange, long limit, ResourceBudget? budget = null, int size = 0)
    {
        var output = size > 0
            ? new BoundedBuffer(size, limit, budget, exact: true)
            : new BoundedBuffer((int)Math.Min(int.MaxValue, data.Length * 3L), limit, budget);
        try
        {
            LzwCore(output, data, earlyChange, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void LzwCore(BoundedBuffer output, ReadOnlySpan<byte> data, int earlyChange, long limit)
    {
        var prefixStart = new int[4096];
        var prefixLength = new int[4096];
        int bitBuffer = 0, bitCount = 0, pos = 0;
        int codeLength = 9;
        int nextCode = 258;
        int previousStart = -1, previousLength = 0;

        while (true)
        {
            while (bitCount < codeLength)
            {
                if (pos >= data.Length) return;
                bitBuffer = (bitBuffer << 8) | data[pos++];
                bitCount += 8;
            }

            int code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
            bitCount -= codeLength;

            if (code == 256)
            {
                codeLength = 9;
                nextCode = 258;
                previousStart = -1;
                continue;
            }

            if (code == 257) break;

            int start = output.Length;
            if (code < 256)
            {
                output.Write((byte)code);
                if (previousStart >= 0) AddEntry(previousStart, previousLength + 1);
                previousStart = start;
                previousLength = 1;
            }
            else if (code < nextCode)
            {
                output.CopyWithin(prefixStart[code], prefixLength[code]);
                if (previousStart >= 0) AddEntry(previousStart, previousLength + 1);
                previousStart = start;
                previousLength = prefixLength[code];
            }
            else if (code == nextCode && previousStart >= 0)
            {
                // KwKwK case: previous string plus its own first byte.
                output.CopyWithin(previousStart, previousLength);
                output.CopyWithin(previousStart, 1);
                AddEntry(start, previousLength + 1);
                previousStart = start;
                previousLength = previousLength + 1;
            }
            else
            {
                throw new StreamDecodeException("Invalid LZW code.");
            }

            int threshold = (1 << codeLength) - earlyChange;
            if (nextCode >= threshold && codeLength < 12) codeLength++;
        }

        return;

        void AddEntry(int start, int length)
        {
            if (nextCode >= 4096) return;
            prefixStart[nextCode] = start;
            prefixLength[nextCode] = length;
            nextCode++;
        }
    }

    public static BoundedBuffer AsciiHex(ReadOnlySpan<byte> data, long limit, ResourceBudget? budget = null)
    {
        var output = new BoundedBuffer(data.Length / 2 + 1, limit, budget);
        try
        {
            AsciiHexCore(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void AsciiHexCore(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        int high = -1;
        foreach (byte b in data)
        {
            if (b == (byte)'>') break;
            int v = Parsing.PdfSyntax.HexValue(b);
            if (v < 0)
            {
                if (Parsing.PdfSyntax.IsWhitespace(b)) continue;
                throw new StreamDecodeException("Invalid character in ASCIIHex data.");
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                output.Write((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0) output.Write((byte)(high << 4));
    }

    public static BoundedBuffer Ascii85(ReadOnlySpan<byte> data, long limit, ResourceBudget? budget = null)
    {
        var output = new BoundedBuffer(data.Length, limit, budget);
        try
        {
            Ascii85Core(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Ascii85Core(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        Span<byte> group = stackalloc byte[5];
        int n = 0;
        int i = 0;
        if (data.Length >= 2 && data[0] == (byte)'<' && data[1] == (byte)'~') i = 2;
        for (; i < data.Length; i++)
        {
            byte b = data[i];
            if (Parsing.PdfSyntax.IsWhitespace(b)) continue;
            if (b == (byte)'~') break;
            if (b == (byte)'z' && n == 0)
            {
                output.Write([0, 0, 0, 0]);
                continue;
            }

            if (b < (byte)'!' || b > (byte)'u') throw new StreamDecodeException("Invalid character in ASCII85 data.");
            group[n++] = b;
            if (n == 5)
            {
                WriteGroup(output, group, 4);
                n = 0;
            }
        }

        if (n == 1) throw new StreamDecodeException("Truncated ASCII85 group.");
        if (n > 1)
        {
            for (int k = n; k < 5; k++) group[k] = (byte)'u';
            WriteGroup(output, group, n - 1);
        }
    }

    private static void WriteGroup(BoundedBuffer output, ReadOnlySpan<byte> group, int count)
    {
        ulong value = 0;
        for (int k = 0; k < 5; k++) value = value * 85 + (ulong)(group[k] - '!');
        if (value > uint.MaxValue) throw new StreamDecodeException("ASCII85 group overflow.");
        Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        output.Write(bytes[..count]);
    }

    public static BoundedBuffer RunLength(ReadOnlySpan<byte> data, long limit, ResourceBudget? budget = null)
    {
        var output = new BoundedBuffer((int)Math.Min(int.MaxValue, data.Length * 2L), limit, budget);
        try
        {
            RunLengthCore(output, data, limit);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void RunLengthCore(BoundedBuffer output, ReadOnlySpan<byte> data, long limit)
    {
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128) break;
            if (length < 128)
            {
                int count = length + 1;
                if (i + count > data.Length) count = data.Length - i;
                output.Write(data.Slice(i, count));
                i += count;
            }
            else
            {
                if (i >= data.Length) break;
                byte value = data[i++];
                for (int k = 0; k < 257 - length; k++) output.Write(value);
            }
        }
    }
}

/// <summary>Read-only stream over a <see cref="ReadOnlyMemory{T}"/> without copying.</summary>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) => _data = data;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _data.Length;

    public override long Position
    {
        get => _position;
        set => _position = (int)Math.Min(Math.Max(value, 0), _data.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

#if NET8_0_OR_GREATER
    public override int Read(Span<byte> buffer)
#else
    public int Read(Span<byte> buffer)
#endif
    {
        int n = Math.Min(buffer.Length, _data.Length - _position);
        if (n <= 0) return 0;
        _data.Span.Slice(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _data.Length + offset,
        };
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
