using System.Collections;
using System.Text;
using PdfNetStack.Documents;

namespace PdfNetStack.Objects;

/// <summary>
/// Base type of the PDF object model (ISO 32000-1, 7.3): the values a PDF file is made of. Use it
/// to read or change anything the high level API does not cover, through
/// <see cref="PdfDocument.Catalog"/>, <see cref="PdfPage.Dictionary"/> and
/// <see cref="PdfDocument.Resolve(PdfObject?)"/>.
/// </summary>
public abstract class PdfObject
{
    private protected PdfObject()
    {
    }
}

/// <summary>The PDF null object.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The only instance.</summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    /// <inheritdoc/>
    public override string ToString() => "null";
}

/// <summary>A PDF boolean.</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The true value.</summary>
    public static readonly PdfBoolean True = new(true);

    /// <summary>The false value.</summary>
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <summary>Returns the shared instance for <paramref name="value"/>.</summary>
    public static PdfBoolean Get(bool value) => value ? True : False;

    /// <inheritdoc/>
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>
/// A PDF integer or real. The value is kept as canonical ASCII text so that reals read from a
/// file survive a round trip exactly, with no floating point conversion involved.
/// </summary>
public sealed class PdfNumber : PdfObject
{
    // Integers read from files are mostly small: page counts, generations, MCIDs, kerning, widths.
    private const int CachedMin = -1;
    private const int CachedMax = 1023;
    private static readonly PdfNumber[] Cached = MakeCached();

    // Reals and malformed tokens keep their text; an integer's is its value, made on first use.
    private byte[]? _text;

    private PdfNumber(byte[]? text, bool isInteger, long integerValue)
    {
        _text = text;
        IsInteger = isInteger;
        IntegerValue = integerValue;
    }

    /// <summary>Canonical ASCII representation, ready to serialize.</summary>
    internal byte[] Text => _text ??= Encoding.ASCII.GetBytes(IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>True for integers, false for reals.</summary>
    public bool IsInteger { get; }

    /// <summary>The integer value. Valid only when <see cref="IsInteger"/> is true.</summary>
    public long IntegerValue { get; }

    /// <summary>Creates an integer.</summary>
    public static PdfNumber FromInteger(long value) => new(null, true, value);

    /// <summary>
    /// Creates a real, rounded to 6 decimal places and written without an exponent, as PDF
    /// requires. Whole values become integers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static PdfNumber FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "PDF numbers must be finite.");
        string text = Math.Round(value, 6).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return Parse(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// Parses a numeric token and canonicalizes it: no leading plus, no redundant zeros,
    /// no trailing decimal point. The numeric value never changes. Tokens that do not look
    /// like a well formed number are kept verbatim. Small integers are shared instances.
    /// </summary>
    internal static PdfNumber Parse(ReadOnlySpan<byte> raw)
    {
        Span<byte> text = raw.Length <= 64 ? stackalloc byte[raw.Length] : new byte[raw.Length];
        int length = Canonicalize(raw, text, out bool isInteger, out long value);
        if (!isInteger) return new PdfNumber(text[..length].ToArray(), false, 0);
        return value is >= CachedMin and <= CachedMax ? Cached[value - CachedMin] : new PdfNumber(null, true, value);
    }

    /// <summary>
    /// Writes the canonical form of a numeric token, the text <see cref="Parse"/> keeps, into
    /// <paramref name="text"/>, which must hold at least the token's length, and returns its
    /// length. It is never longer than the token.
    /// </summary>
    internal static int Canonicalize(ReadOnlySpan<byte> raw, Span<byte> text, out bool isInteger, out long integerValue)
    {
        isInteger = false;
        integerValue = 0;
        int i = 0;
        bool negative = false;
        if (i < raw.Length && (raw[i] == (byte)'+' || raw[i] == (byte)'-'))
        {
            negative = raw[i] == (byte)'-';
            i++;
        }

        int intStart = i;
        while (i < raw.Length && IsDigit(raw[i])) i++;
        int intEnd = i;
        int fracStart = -1, fracEnd = -1;
        if (i < raw.Length && raw[i] == (byte)'.')
        {
            i++;
            fracStart = i;
            while (i < raw.Length && IsDigit(raw[i])) i++;
            fracEnd = i;
        }

        bool wellFormed = i == raw.Length && (intEnd > intStart || fracEnd > fracStart);
        if (!wellFormed)
        {
            raw.CopyTo(text);
            return raw.Length;
        }

        ReadOnlySpan<byte> intDigits = raw[intStart..intEnd];
        while (!intDigits.IsEmpty && intDigits[0] == (byte)'0') intDigits = intDigits[1..];
        ReadOnlySpan<byte> fracDigits = fracStart >= 0 ? raw[fracStart..fracEnd] : default;
        while (!fracDigits.IsEmpty && fracDigits[^1] == (byte)'0') fracDigits = fracDigits[..^1];

        if (intDigits.IsEmpty && fracDigits.IsEmpty)
        {
            text[0] = (byte)'0';
            isInteger = true;
            return 1;
        }

        int p = 0;
        if (negative) text[p++] = (byte)'-';
        intDigits.CopyTo(text[p..]);
        p += intDigits.Length;
        if (!fracDigits.IsEmpty)
        {
            text[p++] = (byte)'.';
            fracDigits.CopyTo(text[p..]);
            return p + fracDigits.Length;
        }

        if (intDigits.Length <= 18)
        {
            long value = 0;
            foreach (byte b in intDigits) value = value * 10 + (b - '0');
            isInteger = true;
            integerValue = negative ? -value : value;
        }

        return p;
    }

    /// <summary>True when both numbers have the same canonical text, without making an integer's.</summary>
    internal bool SameValue(PdfNumber other) =>
        IsInteger && other.IsInteger ? IntegerValue == other.IntegerValue : Text.AsSpan().SequenceEqual(other.Text);

    /// <summary>The value as a double. Exact for integers up to 2^53.</summary>
    public double ToDouble() =>
        IsInteger
            ? IntegerValue
            : double.TryParse(Encoding.ASCII.GetString(Text), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static PdfNumber[] MakeCached()
    {
        var cached = new PdfNumber[CachedMax - CachedMin + 1];
        for (int v = CachedMin; v <= CachedMax; v++)
        {
            cached[v - CachedMin] = new PdfNumber(Encoding.ASCII.GetBytes(v.ToString(System.Globalization.CultureInfo.InvariantCulture)), true, v);
        }

        return cached;
    }

    /// <summary>The number as written in the file.</summary>
    public override string ToString() => Encoding.ASCII.GetString(Text);
}

/// <summary>A PDF name such as /Type. Bytes map one to one onto chars (Latin-1) after #xx unescaping.</summary>
public sealed class PdfName : PdfObject
{
    /// <summary>Creates a name.</summary>
    /// <param name="value">The name without its leading slash, for example "Page".</param>
    public PdfName(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The name without its leading slash.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;
}

/// <summary>A PDF string: a sequence of bytes, which may hold text or binary data.</summary>
public sealed class PdfString : PdfObject
{
    internal PdfString(byte[] value, bool wasHex = false)
    {
        Value = value;
        WasHex = wasHex;
    }

    /// <summary>Creates a string holding a copy of <paramref name="bytes"/>.</summary>
    public PdfString(ReadOnlySpan<byte> bytes)
        : this(bytes.ToArray())
    {
    }

    internal byte[] Value { get; }

    /// <summary>Source syntax hint only. Hex and literal strings are semantically identical.</summary>
    internal bool WasHex { get; }

    /// <summary>The raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => Value;

    /// <summary>
    /// Creates a text string (ISO 32000-1, 7.9.2.2): PDFDocEncoding when every character fits,
    /// otherwise UTF-16 with a byte order mark.
    /// </summary>
    public static PdfString FromText(string text) => PdfText.Encode(text ?? throw new ArgumentNullException(nameof(text)));

    /// <summary>Decodes the bytes as a text string: UTF-16 or UTF-8 with a byte order mark, otherwise PDFDocEncoding.</summary>
    public string ToText() => PdfText.Decode(Value);

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>A PDF array.</summary>
public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    /// <summary>Creates an empty array.</summary>
    public PdfArray() => Items = [];

    internal PdfArray(List<PdfObject> items) => Items = items;

    internal List<PdfObject> Items { get; }

    /// <summary>Number of items.</summary>
    public int Count => Items.Count;

    /// <summary>The item at <paramref name="index"/>. Items may be references; resolve them through the document.</summary>
    public PdfObject this[int index]
    {
        get => Items[index];
        set => Items[index] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Creates an array of the given items.</summary>
    public static PdfArray Of(params PdfObject[] items)
    {
        Compat.ThrowIfNull(items, nameof(items));
        if (Array.IndexOf(items, null) >= 0) throw new ArgumentException("Items cannot be null; use PdfNull.Instance.", nameof(items));
        return new PdfArray([.. items]);
    }

    /// <summary>Creates an array of numbers.</summary>
    public static PdfArray Of(params double[] numbers)
    {
        Compat.ThrowIfNull(numbers, nameof(numbers));
        var items = new List<PdfObject>(numbers.Length);
        foreach (double n in numbers) items.Add(PdfNumber.FromDouble(n));
        return new PdfArray(items);
    }

    /// <summary>Appends an item.</summary>
    public void Add(PdfObject item) => Items.Add(item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>Inserts an item at <paramref name="index"/>.</summary>
    public void Insert(int index, PdfObject item) => Items.Insert(index, item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>Removes the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index) => Items.RemoveAt(index);

    /// <summary>Removes every item.</summary>
    public void Clear() => Items.Clear();

    /// <inheritdoc/>
    public IEnumerator<PdfObject> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A PDF dictionary with keys in insertion order. Keys are names without the leading slash.
/// Most PDF dictionaries hold a handful of keys, so lookups are linear until the dictionary
/// grows, then switch to a hash index.
/// </summary>
public sealed class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<string, PdfObject>>
{
    private const int IndexThreshold = 12;

    // Entries in one array: a parsed document holds hundreds of thousands of small dictionaries,
    // and two lists per dictionary cost more than their entries.
    private KeyValuePair<string, PdfObject>[] _entries;
    private int _count;
    private Dictionary<string, int>? _index;
    private KeyList? _keys;

    // Counts added and removed keys, so enumerating Keys while they change fails as a list would.
    private int _version;

    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary()
        : this(4)
    {
    }

    internal PdfDictionary(int capacity) => _entries = capacity > 0 ? new KeyValuePair<string, PdfObject>[capacity] : [];

    /// <summary>Number of entries.</summary>
    public int Count => _count;

    /// <summary>The keys, in insertion order.</summary>
    public IReadOnlyList<string> Keys => _keys ??= new KeyList(this);

    /// <summary>
    /// The value for <paramref name="key"/>, or null when absent. The value may be a reference;
    /// resolve it through the document. Setting null removes the entry.
    /// </summary>
    public PdfObject? this[string key]
    {
        get => TryGetValue(key, out PdfObject? value) ? value : null;
        set
        {
            if (value is null) Remove(key);
            else Set(key, value);
        }
    }

    /// <summary>True when the key is present.</summary>
    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <summary>Gets the value for <paramref name="key"/> when present.</summary>
    public bool TryGetValue(string key, out PdfObject value)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            value = null!;
            return false;
        }

        value = _entries[i].Value;
        return true;
    }

    /// <summary>Adds or replaces an entry. Replacing keeps the key's position.</summary>
    public void Set(string key, PdfObject value)
    {
        Compat.ThrowIfNull(key, nameof(key));
        Compat.ThrowIfNull(value, nameof(value));
        int i = IndexOf(key);
        if (i >= 0)
        {
            _entries[i] = new KeyValuePair<string, PdfObject>(_entries[i].Key, value);
            return;
        }

        if (_count == _entries.Length) Array.Resize(ref _entries, Math.Max(4, _entries.Length * 2));
        _entries[_count++] = new KeyValuePair<string, PdfObject>(key, value);
        _version++;
        if (_index is not null) _index[key] = _count - 1;
        else if (_count > IndexThreshold) BuildIndex();
    }

    /// <summary>Removes an entry; returns false when it was absent.</summary>
    public bool Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _count--;
        if (i < _count) Array.Copy(_entries, i + 1, _entries, i, _count - i);
        _entries[_count] = default;
        _version++;
        if (_index is not null) BuildIndex();
        return true;
    }

    /// <summary>The value of <paramref name="key"/> when it is a direct name, without the slash.</summary>
    public string? GetName(string key) => this[key] is PdfName n ? n.Value : null;

    internal KeyValuePair<string, PdfObject> EntryAt(int i) => _entries[Check(i)];

    internal void SetValueAt(int i, PdfObject value) => _entries[Check(i)] = new KeyValuePair<string, PdfObject>(_entries[i].Key, value);

    internal PdfObject ValueAt(int i) => _entries[Check(i)].Value;

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, PdfObject>> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return _entries[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int Check(int i) => (uint)i < (uint)_count ? i : throw new ArgumentOutOfRangeException(nameof(i));

    private int IndexOf(string key)
    {
        if (_index is not null) return _index.TryGetValue(key, out int i) ? i : -1;
        for (int i = 0; i < _count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private void BuildIndex()
    {
        _index = new Dictionary<string, int>(_count * 2, StringComparer.Ordinal);
        for (int i = 0; i < _count; i++) _index[_entries[i].Key] = i;
    }

    /// <summary>A live view of the keys, as the list of keys was before entries shared one array.</summary>
    private sealed class KeyList(PdfDictionary owner) : IReadOnlyList<string>
    {
        public int Count => owner._count;

        public string this[int index] => owner._entries[owner.Check(index)].Key;

        public IEnumerator<string> GetEnumerator()
        {
            int version = owner._version;
            for (int i = 0; i < owner._count; i++)
            {
                if (version != owner._version) throw new InvalidOperationException("The dictionary's keys changed during enumeration.");
                yield return owner._entries[i].Key;
            }

            if (version != owner._version) throw new InvalidOperationException("The dictionary's keys changed during enumeration.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// A PDF stream: a dictionary plus encoded data. Streams are always indirect objects; add new
/// ones with <see cref="PdfDocument.AddObject(PdfObject)"/> and refer to the returned reference.
/// </summary>
public sealed class PdfStream : PdfObject
{
    /// <summary>Creates a stream from data already encoded as the dictionary's /Filter says.</summary>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        Data = data;
    }

    /// <summary>
    /// Creates a stream from plain data, Flate compressed unless <paramref name="compress"/> is
    /// false. /Filter and /DecodeParms of <paramref name="dictionary"/> are replaced to match.
    /// </summary>
    public static PdfStream FromData(ReadOnlySpan<byte> data, PdfDictionary? dictionary = null, bool compress = true)
    {
        dictionary ??= new PdfDictionary();
        dictionary.Remove("DecodeParms");
        dictionary.Remove("DL");
        if (!compress)
        {
            dictionary.Remove("Filter");
            return new PdfStream(dictionary, data.ToArray());
        }

        dictionary.Set("Filter", new PdfName("FlateDecode"));
        return new PdfStream(dictionary, Filters.StreamCodec.Deflate(data, System.IO.Compression.CompressionLevel.Optimal));
    }

    /// <summary>A stream whose data stays in the file until it is asked for.</summary>
    /// <param name="dictionary">The stream dictionary.</param>
    /// <param name="load">Reads the encoded data; called on every access until the data is replaced.</param>
    /// <param name="length">Length of the encoded data in the file.</param>
    internal PdfStream(PdfDictionary dictionary, Func<ReadOnlyMemory<byte>> load, int length)
    {
        Dictionary = dictionary;
        _load = load;
        _loadLength = length;
    }

    private ReadOnlyMemory<byte> _data;
    private Func<ReadOnlyMemory<byte>>? _load;
    private int _loadLength;

    /// <summary>The stream dictionary. /Length is written on save and need not be kept current.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// Encoded stream bytes, exactly as stored between stream and endstream. For a document
    /// opened from a stream without loading it, the bytes are read from the file on each access
    /// and not kept, until new data is assigned.
    /// </summary>
    public ReadOnlyMemory<byte> Data
    {
        get => _load is { } load ? load() : _data;
        set
        {
            _data = value;
            _load = null;
            Version++;
        }
    }

    /// <summary>True while the data still lives in the file.</summary>
    internal bool IsDeferred => _load is not null;

    /// <summary>Length of the encoded data, without reading deferred data.</summary>
    internal int EncodedLength => _load is null ? _data.Length : _loadLength;

    /// <summary>Counts assignments to <see cref="Data"/>, so a writer can tell data that changed.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Applies <paramref name="transform"/> to the data: now when it is in memory, on every read
    /// while it is deferred. Used for decryption, which must not force deferred data into memory.
    /// </summary>
    internal void Transform(Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> transform)
    {
        if (_load is { } load) _load = () => transform(load());
        else _data = transform(_data);
    }
}

/// <summary>
/// A reference to an indirect object. References belong to one document: obtain them from
/// <see cref="PdfDocument.AddObject(PdfObject)"/> or from that document's objects, and resolve
/// them with <see cref="PdfDocument.Resolve(PdfObject?)"/>.
/// </summary>
public sealed class PdfReference : PdfObject
{
    internal PdfReference(int number, int generation)
    {
        Number = number;
        Generation = generation;
    }

    /// <summary>Object number.</summary>
    public int Number { get; }

    /// <summary>Generation number.</summary>
    public int Generation { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Number} {Generation} R";
}
