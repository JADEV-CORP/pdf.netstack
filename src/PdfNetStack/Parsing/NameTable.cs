using PdfNetStack.Objects;

namespace PdfNetStack.Parsing;

/// <summary>
/// Names by the bytes they are written with, so a name read over and over (dictionary keys,
/// resource names in content) is decoded once and shared as one string and one
/// <see cref="PdfName"/>. A PDF has a few hundred distinct names and hundreds of thousands of
/// occurrences. Thread safe, with a lock nobody else waits on in practice: a file parses its
/// objects under its own lock. Bounded: past its size, or for long names and crowded slots, a
/// name is decoded anew, so hostile input costs no more than without the table.
/// </summary>
internal sealed class NameTable
{
    private const int MaxEntries = 1 << 15;
    private const int MaxRawLength = 64;
    private const int MaxProbes = 16;

    private readonly object _lock = new();
    private Entry[] _entries = new Entry[64];
    private int _count;

    private struct Entry
    {
        public byte[]? Raw;
        public int Hash;
        public string Value;
        public PdfName? Name;
    }

    /// <summary>The decoded name for its raw bytes, without the leading slash.</summary>
    public string Get(ReadOnlySpan<byte> raw)
    {
        lock (_lock)
        {
            int slot = Find(raw);
            if (slot >= 0) return _entries[slot].Value;
        }

        return PdfParser.DecodeName(raw);
    }

    /// <summary>The shared name object for its raw bytes, without the leading slash.</summary>
    public PdfName GetName(ReadOnlySpan<byte> raw)
    {
        lock (_lock)
        {
            int slot = Find(raw);
            if (slot >= 0)
            {
                ref Entry e = ref _entries[slot];
                return e.Name ??= new PdfName(e.Value);
            }
        }

        return new PdfName(PdfParser.DecodeName(raw));
    }

    /// <summary>The slot holding <paramref name="raw"/>, added if need be; -1 when it is not kept.</summary>
    private int Find(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaxRawLength) return -1;

        // FNV-1a: names are short, and a probe limit bounds the cost of any collisions.
        uint h = 2166136261;
        foreach (byte b in raw) h = (h ^ b) * 16777619;
        int hash = (int)h;
        int mask = _entries.Length - 1;
        int i = hash & mask;
        for (int probe = 0; probe < MaxProbes; probe++, i = (i + 1) & mask)
        {
            ref Entry e = ref _entries[i];
            if (e.Raw is null)
            {
                if (_count >= MaxEntries) return -1;
                e.Raw = raw.ToArray();
                e.Hash = hash;
                e.Value = PdfParser.DecodeName(raw);
                if (++_count * 2 <= _entries.Length) return i;
                Grow();
                return Find(raw);
            }

            if (e.Hash == hash && raw.SequenceEqual(e.Raw)) return i;
        }

        return -1;
    }

    private void Grow()
    {
        Entry[] old = _entries;
        _entries = new Entry[old.Length * 2];
        int mask = _entries.Length - 1;
        foreach (Entry e in old)
        {
            if (e.Raw is null) continue;
            int i = e.Hash & mask;
            while (_entries[i].Raw is not null) i = (i + 1) & mask;
            _entries[i] = e;
        }
    }
}
