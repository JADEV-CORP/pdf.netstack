namespace PdfNetStack.Parsing;

/// <summary>
/// Reads a stream from its position to its end into memory with as few copies as the stream
/// allows: none for a <see cref="MemoryStream"/> whose buffer is exposed, one into an array of
/// the reported size for a stream that can seek, and otherwise pieces joined once at the end, so
/// at most twice the data is ever held instead of the three times a doubling buffer reaches.
/// </summary>
internal static class InputReader
{
    private const int FirstPiece = 81920;
    private const int LargestPiece = 4 << 20;

    /// <exception cref="NotSupportedException">The stream holds more than one array can.</exception>
    public static ReadOnlyMemory<byte> ReadToEnd(Stream stream, long maxBytes = long.MaxValue)
    {
        if (Exposed(stream, maxBytes) is { } exposed) return exposed;

        var pieces = new Pieces(maxBytes);
        if (stream.CanSeek)
        {
            byte[] sized = new byte[Remaining(stream, maxBytes)];
            int total = 0, read;
            while (total < sized.Length && (read = stream.Read(sized, total, sized.Length - total)) > 0) total += read;
            if (total < sized.Length) return sized.AsMemory(0, total);

            // The stream ended where it said it would, unless it grew meanwhile.
            pieces.Add(sized);
            if (!pieces.Fill(stream.Read(pieces.Next(), pieces.Used, pieces.Room))) return sized;
        }

        while (pieces.Fill(stream.Read(pieces.Next(), pieces.Used, pieces.Room)))
        {
        }

        return pieces.Join();
    }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
    /// <exception cref="NotSupportedException">The stream holds more than one array can.</exception>
    public static async Task<ReadOnlyMemory<byte>> ReadToEndAsync(Stream stream, CancellationToken cancellationToken, long maxBytes = long.MaxValue)
    {
        if (Exposed(stream, maxBytes) is { } exposed) return exposed;

        var pieces = new Pieces(maxBytes);
        if (stream.CanSeek)
        {
            byte[] sized = new byte[Remaining(stream, maxBytes)];
            int total = 0, read;
            while (total < sized.Length && (read = await stream.ReadAsync(sized, total, sized.Length - total, cancellationToken).ConfigureAwait(false)) > 0) total += read;
            if (total < sized.Length) return sized.AsMemory(0, total);

            // The stream ended where it said it would, unless it grew meanwhile.
            pieces.Add(sized);
            if (!pieces.Fill(await stream.ReadAsync(pieces.Next(), pieces.Used, pieces.Room, cancellationToken).ConfigureAwait(false))) return sized;
        }

        while (pieces.Fill(await stream.ReadAsync(pieces.Next(), pieces.Used, pieces.Room, cancellationToken).ConfigureAwait(false)))
        {
        }

        return pieces.Join();
    }
#pragma warning restore CA1835

    /// <summary>The unread part of a memory stream's own buffer, which then reads as at its end.</summary>
    private static ReadOnlyMemory<byte>? Exposed(Stream stream, long maxBytes)
    {
        if (stream is not MemoryStream memory || !memory.TryGetBuffer(out ArraySegment<byte> buffer)) return null;
        int at = (int)Math.Min(memory.Position, buffer.Count);
        if (buffer.Count - at > maxBytes) throw InputTooLarge(maxBytes);
        memory.Position = Math.Max(memory.Position, buffer.Count);
        return new ReadOnlyMemory<byte>(buffer.Array, buffer.Offset + at, buffer.Count - at);
    }

    private static int Remaining(Stream stream, long maxBytes)
    {
        long remaining = Math.Max(0, stream.Length - stream.Position);
        if (remaining > maxBytes) throw InputTooLarge(maxBytes);
        if (remaining > Compat.MaxArrayLength) throw TooLarge();
        return (int)remaining;
    }

    private static NotSupportedException TooLarge() => new("A PDF of 2 GiB or more cannot be held in memory; open it on demand.");

    /// <summary>The exception for an input longer than <see cref="PdfLimits.MaxInputBytes"/>.</summary>
    public static PdfLimitExceededException InputTooLarge(long maxBytes) =>
        new(PdfLimitKind.InputBytes, Compat.Format("The input is longer than its limit of {0} bytes (PdfLimits.MaxInputBytes).", maxBytes));

    /// <exception cref="PdfLimitExceededException"><paramref name="length"/> is over the limit.</exception>
    public static void CheckLength(long length, long maxBytes)
    {
        if (length > maxBytes) throw InputTooLarge(maxBytes);
    }

    /// <summary>
    /// Full pieces and the one being filled. Each new piece is as large as the pieces made before
    /// it, from 80 KiB to 4 MiB, so a small stream takes one piece and a large one few.
    /// </summary>
    private sealed class Pieces
    {
        private readonly long _maxBytes;
        private readonly List<byte[]> _full = [];
        private byte[] _current = [];
        private long _total;
        private long _made;

        /// <summary>Bytes read into the piece being filled.</summary>
        public Pieces(long maxBytes) => _maxBytes = maxBytes;

        public int Used { get; private set; }

        /// <summary>Free bytes in the piece being filled.</summary>
        public int Room => _current.Length - Used;

        public void Add(byte[] piece)
        {
            _full.Add(piece);
            _total += piece.Length;
        }

        /// <summary>The piece to read into at <see cref="Used"/>; a new one once the last is full.</summary>
        public byte[] Next()
        {
            if (Room == 0)
            {
                if (Used > 0) Add(_current);
                _current = new byte[(int)Math.Max(FirstPiece, Math.Min(LargestPiece, _made))];
                _made += _current.Length;
                Used = 0;
            }

            return _current;
        }

        /// <summary>Records <paramref name="read"/> bytes read; false at the end of the stream.</summary>
        public bool Fill(int read)
        {
            if (read <= 0) return false;
            if (_total + Used + read > _maxBytes) throw InputTooLarge(_maxBytes);
            if (_total + Used + read > Compat.MaxArrayLength) throw TooLarge();
            Used += read;
            return true;
        }

        public ReadOnlyMemory<byte> Join()
        {
            if (_full.Count == 0) return _current.AsMemory(0, Used);
            var data = new byte[_total + Used];
            long at = 0;
            foreach (byte[] piece in _full)
            {
                Array.Copy(piece, 0, data, at, piece.Length);
                at += piece.Length;
            }

            Array.Copy(_current, 0, data, at, Used);
            return data;
        }
    }
}
