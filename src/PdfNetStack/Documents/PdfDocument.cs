using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using PdfNetStack.Documents;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;
using PdfNetStack.Security;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>Options for <see cref="PdfDocument.Save(Stream, PdfSaveOptions?)"/>.</summary>
public sealed record PdfSaveOptions
{
    /// <summary>Shared default instance.</summary>
    public static PdfSaveOptions Default { get; } = new();

    /// <summary>Pack objects into compressed object streams (PDF 1.5). Default true. PDF/A-1 output never uses them.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>
    /// Run the lossless optimizer on the saved file: stream recompression and deduplication,
    /// with verification. Merged documents often share fonts and images, so this pays off. Default true.
    /// </summary>
    public bool Optimize { get; init; } = true;

    /// <summary>
    /// Whether the optimizer proves its rewrite equivalent to the file as first written, by reading
    /// both back and comparing them, before it uses the rewrite. Default true. The proof takes about
    /// half the optimizer's time. Turn it off for documents whose content this library drew itself,
    /// as <c>HtmlConverter.ConvertToPdf</c> does: whenever the proof would pass, the bytes are the
    /// same either way. Applies only with <see cref="Optimize"/>.
    /// </summary>
    public bool VerifyOptimization { get; init; } = true;

    /// <summary>Flate effort for streams the writer compresses. Default <see cref="CompressionEffort.Maximum"/>.</summary>
    public CompressionEffort Compression { get; init; } = CompressionEffort.Maximum;

    /// <summary>
    /// A standard to conform to, such as PDF/A-3b for archiving and e-invoices. Saving checks what
    /// cannot be repaired and throws listing every problem, then adds the output intent and
    /// metadata the standard requires.
    /// </summary>
    public PdfConformance Conformance { get; init; }
}

/// <summary>
/// A PDF document opened for editing or created from scratch. Pages can be added, removed,
/// reordered, rotated and imported from other documents; metadata and bookmarks can be read
/// and changed. Saving writes a new, single revision file; an opened document can also save its
/// changes as an incremental update with <see cref="SaveIncremental(Stream)"/>.
/// </summary>
/// <remarks>
/// Editing a digitally signed document invalidates its signatures. Content of imported pages, including fonts, images, annotations and their
/// appearance streams, is copied; resources shared by several imported pages are copied once.
/// A document read on demand (<see cref="PdfLoadMode.OnDemand"/>) holds its file until it is
/// disposed; disposing any other document does nothing.
/// </remarks>
public sealed class PdfDocument : IDisposable
{
    /// <summary>
    /// Levels of /Pages nodes read. Real trees are a handful of levels deep; the bound keeps a
    /// tree of thousands of nested nodes from costing a walk per level everywhere pages are found.
    /// </summary>
    internal const int MaxPageTreeDepth = 64;

    private readonly HashSet<int> _pageTreeNodes = new();
    private readonly HashSet<int> _everPages = new();
    private readonly Dictionary<PdfDocument, PageImporter> _importers = new();
    private readonly List<Drawing.PdfGraphics> _openGraphics = new();
    private readonly Dictionary<Fonts.PdfFont, Fonts.FontResource> _fonts = new();
    private readonly Dictionary<Drawing.PdfImage, PdfReference> _images = new();
    private PdfAcroForm? _form;

    /// <summary>True once drawing or images used DeviceCMYK, which PDF/A with an RGB intent forbids.</summary>
    internal bool UsesDeviceCmyk { get; set; }

    /// <summary>The Factur-X invoice attached with <see cref="PdfInvoice.Attach"/>, written to the PDF/A-3 metadata on save.</summary>
    internal InvoiceMetadata? Invoice { get; set; }
    private PdfOutlineCollection? _outlines;

    private PdfDocument(string version, StandardSecurity? security = null)
    {
        Version = version;
        Pages = new PdfPageCollection(this);
        Info = new PdfDocumentInfo(this);
        Security = new PdfSecurity(this, security);
    }

    /// <summary>Encryption of the opened document and of the saved output.</summary>
    public PdfSecurity Security { get; }

    internal ObjectStore Objects { get; private set; } = new();

    // The file an opened document came from, the caller's stream it was read from when opened on
    // demand, and what the page tree looked like after opening: what an incremental save compares with.
    private PdfFile? _file;
    private Stream? _sourceStream;

    // A document read on demand stops working when disposed, and closes what it opened itself:
    // a file opened by path, or the temporary copy of a stream that cannot seek.
    private bool _closeOnDispose;
    private bool _disposed;
    private IDisposable? _owned;
    private List<int> _openedPages = [];
    private bool _pageTreeMatchesFile = true;
    private readonly Dictionary<int, List<(string Key, PdfObject Value)>> _inherited = new();

    // What the file holds, parsed afresh, for incremental saves to compare with.
    private PdfFile? _baseline;

    internal int NextNumber { get; set; } = 1;

    /// <summary>Objects of the file parsed and kept so far, for a document read on demand.</summary>
    internal int CachedObjectCount => _file?.CachedCount ?? 0;

    /// <summary>
    /// The document catalog (ISO 32000-1, 7.7.2), root of the object graph. Use it with
    /// <see cref="Resolve(PdfObject?)"/> and <see cref="AddObject(PdfObject)"/> for features the
    /// high level API does not cover, such as viewer preferences or page labels.
    /// </summary>
    public PdfDictionary Catalog { get; private set; } = new();

    /// <summary>Per stream decode ceiling for <see cref="GetStreamData(PdfStream)"/>.</summary>
    internal long DecodeLimit { get; private set; } = PdfLoadOptions.DefaultMaxDecodedStreamBytes;

    /// <summary>Most pixels an image of this document may decode to.</summary>
    internal long MaxImagePixels { get; private set; } = PdfLoadOptions.DefaultMaxImagePixels;

    /// <summary>
    /// What is left of the document's <see cref="PdfLimits"/>, spent by every decode, image and
    /// content operator over the document's life, and where its warnings are collected.
    /// </summary>
    internal ResourceBudget Budget { get; private set; } = new(Built, long.MaxValue, new PdfDiagnostics());

    /// <summary>
    /// The limits of a document built in code: it has no file to scale a budget from, and holds
    /// what the application put in, merged pages among them, so only the per page bounds apply.
    /// </summary>
    private static readonly PdfLimits Built = PdfLimits.Default with { MaxOperationsPerDocument = long.MaxValue };

    /// <summary>The limits the document was opened with.</summary>
    internal PdfLimits Limits => Budget.Limits;

    /// <summary>
    /// Warnings about the document, oldest first: limits that cut work short, damaged parts that
    /// were skipped, repairs made to read the file. The document stays usable; a warning says
    /// what a reader of it may be missing. Added as they are found, during opening and later
    /// calls such as <see cref="PdfPage.ExtractText()"/>; <see cref="PdfLoadOptions.OnWarning"/>
    /// hears each one as it comes. At most 1000 are kept.
    /// </summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics => Budget.Diagnostics.Snapshot();

    internal int CatalogNumber { get; private set; }

    internal int? InfoNumber { get; set; }

    internal PdfObject? Id { get; private set; }

    /// <summary>How viewers present the document: page layout, panels, window options, opening page.</summary>
    public PdfViewerSettings Viewer => new(this);

    /// <summary>The page numbers viewers show, such as a roman numbered preface.</summary>
    public PdfPageLabels PageLabels => new(this);

    /// <summary>Files embedded in the document.</summary>
    public PdfAttachmentCollection Attachments => new(this);

    /// <summary>The document's natural language as a BCP 47 tag, such as "en-US" or "ar", for screen readers and search.</summary>
    public string? Language
    {
        get => Resolve(Catalog["Lang"]) is PdfString s ? s.ToText() : null;
        set
        {
            if (value is null) Catalog.Remove("Lang");
            else Catalog.Set("Lang", PdfString.FromText(value));
        }
    }

    /// <summary>The interactive form: fields, values, flattening. Empty when the document has none.</summary>
    public PdfAcroForm Form => _form ??= new PdfAcroForm(this);

    /// <summary>PDF version from the file header, for example "1.7".</summary>
    public string Version { get; private set; }

    /// <summary>The pages, in order.</summary>
    public PdfPageCollection Pages { get; }

    /// <summary>Document information: title, author, dates and custom entries.</summary>
    public PdfDocumentInfo Info { get; }

    /// <summary>The bookmark tree. Loaded on first access.</summary>
    public PdfOutlineCollection Outlines => _outlines ??= PdfOutlineCollection.Load(this);

    /// <summary>The named destinations bookmarks and links point at, to list, resolve and rename.</summary>
    public PdfNamedDestinations NamedDestinations => new(this);

    /// <summary>Creates an empty document. Add pages before saving.</summary>
    public static PdfDocument Create()
    {
        var doc = new PdfDocument("1.7");
        doc.Catalog = new PdfDictionary();
        doc.Catalog.Set("Type", new PdfName("Catalog"));
        doc.CatalogNumber = doc.Add(doc.Catalog);
        return doc;
    }

    /// <summary>Opens a document from a file, read into memory.</summary>
    /// <param name="path">File to open.</param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <exception cref="PdfFormatException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    [OverloadResolutionPriority(1)]
    public static PdfDocument Open(string path, string? password = null) => Open(path, new PdfLoadOptions { Password = password });

    /// <summary>
    /// Opens a document from a file. In memory, the file is read in one pass into an array of its
    /// size and closed. On demand, the document opens the file for reading, shared with other
    /// readers, and keeps it open until it is disposed.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument doc = PdfDocument.Open("archive.pdf", new PdfLoadOptions { Mode = PdfLoadMode.OnDemand });
    /// foreach (PdfPage page in doc.Pages) Console.WriteLine(page.ExtractText());
    /// </code>
    /// </example>
    /// <param name="path">File to open.</param>
    /// <param name="options">Password, limits and mode.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="PdfFormatException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    public static PdfDocument Open(string path, PdfLoadOptions options)
    {
        Compat.ThrowIfNull(path, nameof(path));
        Compat.ThrowIfNull(options, nameof(options));
        options.Validate();
        InputReader.CheckLength(new FileInfo(path).Length, options.Limits.MaxInputBytes);
        if (options.Mode == PdfLoadMode.InMemory) return Load(File.ReadAllBytes(path), options);

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return OnStream(file, options, owned: file, appendable: false);
    }

    /// <summary>Opens a document from a stream, read into memory from its position to its end.</summary>
    /// <param name="stream">The stream to read. It is not disposed.</param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <exception cref="PdfFormatException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    [OverloadResolutionPriority(1)]
    public static PdfDocument Open(Stream stream, string? password = null) => Open(stream, new PdfLoadOptions { Password = password });

    /// <summary>Opens a document from a stream, from its position. The document never disposes the stream.</summary>
    /// <remarks>
    /// <para>
    /// In memory, the stream is read to its end once: a <see cref="MemoryStream"/> whose buffer is
    /// exposed is used without copying (do not change it while the document is in use), a stream
    /// that can seek is read into an array of the size it reports, and any other stream into a
    /// buffer that grows as it reads. The stream is not used again.
    /// </para>
    /// <para>
    /// On demand, a stream that can seek is read in place each time the document needs data, so
    /// it must stay open and unchanged while the document is in use, and nothing else may read it
    /// meanwhile; <see cref="SaveIncremental(Stream)"/> given the same stream appends to it. A
    /// stream that cannot seek is copied first, to memory up to
    /// <see cref="PdfLoadOptions.SpoolThreshold"/> and to a temporary file past it. The copy is
    /// the document's own and is deleted when the document is disposed.
    /// </para>
    /// </remarks>
    /// <param name="stream">A readable stream.</param>
    /// <param name="options">Password, limits and mode.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="PdfFormatException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">In memory, the stream holds 2 GiB or more (open it on demand), or the file is encrypted with a security handler other than the standard password one.</exception>
    public static PdfDocument Open(Stream stream, PdfLoadOptions options)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        Compat.ThrowIfNull(options, nameof(options));
        options.Validate();
        if (!stream.CanRead) throw new ArgumentException("The stream must be readable.", nameof(stream));
        long maxBytes = options.Limits.MaxInputBytes;
        if (options.Mode == PdfLoadMode.InMemory) return Load(InputReader.ReadToEnd(stream, maxBytes), options);
        if (stream.CanSeek)
        {
            InputReader.CheckLength(stream.Length - stream.Position, maxBytes);
            return OnStream(stream, options, owned: null, appendable: true);
        }

        return OnSpool(Spool.Copy(stream, options.SpoolThreshold, options.TempDirectory, maxBytes), options);
    }

    /// <summary>
    /// Opens a document from memory. Stream data is referenced, not copied, so the buffer must
    /// not change while the document is in use.
    /// </summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <param name="maxDecodedStreamBytes">Ceiling for any single decoded stream.</param>
    /// <exception cref="PdfFormatException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    [OverloadResolutionPriority(1)]
    public static PdfDocument Open(ReadOnlyMemory<byte> data, string? password = null, long maxDecodedStreamBytes = PdfLoadOptions.DefaultMaxDecodedStreamBytes) =>
        Open(data, new PdfLoadOptions { Password = password, MaxDecodedStreamBytes = maxDecodedStreamBytes });

    /// <summary>
    /// Opens a document from memory. Stream data is referenced, not copied, so the buffer must
    /// not change while the document is in use. On demand, objects are parsed only when first
    /// used, which saves the memory and time of parsing what the application never reads.
    /// Disposing the document does nothing: the memory is the caller's.
    /// </summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="options">Password, limits and mode. The spool settings do not apply.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="PdfFormatException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    public static PdfDocument Open(ReadOnlyMemory<byte> data, PdfLoadOptions options)
    {
        Compat.ThrowIfNull(options, nameof(options));
        options.Validate();
        if (options.Mode == PdfLoadMode.InMemory) return Load(data, options);
        InputReader.CheckLength(data.Length, options.Limits.MaxInputBytes);
        return OnDemand(PdfFile.Open(data, options.MaxDecodedStreamBytes, NewBudget(options, data.Length)), options, source: null, owned: null, closeOnDispose: false);
    }

    /// <summary>
    /// Opens a document from a stream without reading it synchronously, for network streams and
    /// hosts that forbid synchronous I/O, such as ASP.NET Core request bodies. The stream is read
    /// from its position to its end, asynchronously, and not used again; parsing then runs on the
    /// calling thread. In memory, the data is held as <see cref="Open(Stream, PdfLoadOptions)"/>
    /// holds it. On demand, it is copied to memory up to <see cref="PdfLoadOptions.SpoolThreshold"/>
    /// and to a temporary file past it, deleted when the document is disposed. To read a file on
    /// demand without copying it, open it by path.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PdfLoadOptions { Mode = PdfLoadMode.OnDemand, TempDirectory = "/tmp" };
    /// using PdfDocument doc = await PdfDocument.OpenAsync(request.Body, options, cancellationToken);
    /// string first = doc.Pages[0].ExtractText();
    /// </code>
    /// </example>
    /// <param name="stream">A readable stream. It is not disposed.</param>
    /// <param name="options">Password, limits and mode; null for the defaults (in memory).</param>
    /// <param name="cancellationToken">Stops reading; any temporary copy is deleted.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the document was open.</exception>
    /// <exception cref="PdfFormatException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">In memory, the stream holds 2 GiB or more (open it on demand), or the file is encrypted with a security handler other than the standard password one.</exception>
    public static async Task<PdfDocument> OpenAsync(Stream stream, PdfLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        options ??= PdfLoadOptions.Default;
        options.Validate();
        if (!stream.CanRead) throw new ArgumentException("The stream must be readable.", nameof(stream));
        if (options.Mode == PdfLoadMode.InMemory)
        {
            ReadOnlyMemory<byte> data = await InputReader.ReadToEndAsync(stream, cancellationToken, options.Limits.MaxInputBytes).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Load(data, options);
        }

        Spool spool = await Spool.CopyAsync(stream, options.SpoolThreshold, options.TempDirectory, cancellationToken, options.Limits.MaxInputBytes).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            spool.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return OnSpool(spool, options);
    }

    /// <summary>
    /// Opens a document from a stream without reading the file into memory. The
    /// cross-reference data and the page tree are read when opening; every other object is
    /// parsed when first used, and stream data (page content, images, fonts) is read from
    /// <paramref name="stream"/> each time it is needed and not kept. Memory use therefore
    /// follows what the application touches, not the size of the file: a document of any size
    /// can be read page by page, and <see cref="SaveIncremental(Stream)"/> writes only what
    /// changed. <see cref="ToArray(PdfSaveOptions?)"/> still builds the whole output in memory.
    /// The same as <see cref="Open(Stream, PdfLoadOptions)"/> with <see cref="PdfLoadMode.OnDemand"/>.
    /// </summary>
    /// <param name="stream">
    /// A readable stream positioned anywhere. One that can seek must stay open and unchanged while
    /// the document is in use, and must not be read by anyone else meanwhile. One that cannot seek
    /// is copied to memory up to 16 MiB and to a file in the system's temporary folder past it,
    /// deleted when the document is disposed. The document never disposes the stream.
    /// </param>
    /// <param name="password">User or owner password for encrypted files. Null tries the empty user password.</param>
    /// <param name="maxDecodedStreamBytes">Ceiling for any single decoded stream.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="PdfFormatException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted with a security handler other than the standard password one.</exception>
    public static PdfDocument OpenOnDemand(Stream stream, string? password = null, long maxDecodedStreamBytes = PdfLoadOptions.DefaultMaxDecodedStreamBytes) =>
        Open(stream, new PdfLoadOptions { Password = password, MaxDecodedStreamBytes = maxDecodedStreamBytes, Mode = PdfLoadMode.OnDemand });

    /// <summary>Parses a file held in memory: every object reachable from the trailer.</summary>
    private static PdfDocument Load(ReadOnlyMemory<byte> data, PdfLoadOptions options)
    {
        InputReader.CheckLength(data.Length, options.Limits.MaxInputBytes);
        var deadline = Deadline.Start(options.Limits);
        long limit = options.MaxDecodedStreamBytes;
        ResourceBudget budget = NewBudget(options, data.Length);
        PdfFile file = PdfFile.Open(data, limit, budget);
        if (file.Trailer["Encrypt"] is not null and not PdfNull) file.Unlock(options.Password);

        var doc = new PdfDocument(file.HeaderVersion, file.Security) { DecodeLimit = limit, MaxImagePixels = options.MaxImagePixels, _file = file, Budget = budget };
        ObjectGraph graph;
        using (CancellationTokenSource? timer = deadline.NewTimer())
        {
            try
            {
                graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, timer?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) when (timer is { IsCancellationRequested: true })
            {
                throw budget.Exceeded(PdfLimitKind.Timeout, "Opening the document ran past its time limit (PdfLimits.Timeout).");
            }
        }

        foreach (int number in graph.Order) doc.Objects[number] = file.GetObject(number);
        doc.NextNumber = (doc.Objects.Count == 0 ? 0 : doc.Objects.Keys.Max()) + 1;
        doc.NextNumber = Math.Max(doc.NextNumber, FirstFreeNumber(file));
        doc.Attach(file);
        return doc;
    }

    /// <summary>
    /// Opens on demand from a seekable stream. <paramref name="owned"/> closes with the document,
    /// or at once if opening fails. Only the caller's own stream is <paramref name="appendable"/>:
    /// an incremental save given that stream appends to it.
    /// </summary>
    private static PdfDocument OnStream(Stream stream, PdfLoadOptions options, IDisposable? owned, bool appendable)
    {
        try
        {
            ResourceBudget budget = NewBudget(options, stream.Length - stream.Position);
            return OnDemand(PdfFile.Open(stream, options.MaxDecodedStreamBytes, budget), options, appendable ? stream : null, owned, closeOnDispose: true);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>Opens on demand from a private copy, which the document owns from then on.</summary>
    private static PdfDocument OnSpool(Spool spool, PdfLoadOptions options)
    {
        if (spool.File is { } file) return OnStream(file, options, owned: spool, appendable: false);
        return OnDemand(PdfFile.Open(spool.Memory, options.MaxDecodedStreamBytes, NewBudget(options, spool.Memory.Length)), options, source: null, owned: null, closeOnDispose: true);
    }

    private static PdfDocument OnDemand(PdfFile file, PdfLoadOptions options, Stream? source, IDisposable? owned, bool closeOnDispose)
    {
        if (file.Trailer["Encrypt"] is not null and not PdfNull) file.Unlock(options.Password);

        var doc = new PdfDocument(file.HeaderVersion, file.Security)
        {
            DecodeLimit = options.MaxDecodedStreamBytes,
            MaxImagePixels = options.MaxImagePixels,
            Budget = file.Budget ?? NewBudget(options, file.Length),
            _file = file,
            _sourceStream = source,
            _owned = owned,
            _closeOnDispose = closeOnDispose,
        };
        doc.Objects = new ObjectStore(file);
        doc.NextNumber = FirstFreeNumber(file);
        doc.Attach(file);
        return doc;
    }

    /// <summary>
    /// Releases the file a document read on demand reads from: a file it opened itself is
    /// closed, and a temporary copy of a stream is deleted. The caller's stream is never
    /// disposed. Afterwards the document can no longer be used: anything that needs its objects
    /// or its file throws <see cref="ObjectDisposedException"/>. A document read into memory, or
    /// created, holds nothing to release, and disposing it does nothing. Calling it again does
    /// nothing either.
    /// </summary>
    public void Dispose()
    {
        if (_disposed || !_closeOnDispose) return;
        _disposed = true;
        Objects.Close();
        (_file?.Source as StreamSource)?.Close();
        (_baseline?.Source as StreamSource)?.Close();
        _owned?.Dispose();
        _owned = null;
    }

    private void ThrowIfDisposed() => Compat.ThrowIfDisposed(_disposed, this);

    private static ResourceBudget NewBudget(PdfLoadOptions options, long inputBytes) =>
        new(options.Limits, inputBytes, new PdfDiagnostics(options.OnWarning));

    /// <summary>
    /// The first number no object of the file uses, so objects added later never take the number
    /// of one the file still lists, such as an object stream an incremental update must not hide.
    /// </summary>
    private static int FirstFreeNumber(PdfFile file)
    {
        long highest = file.Xref.Count == 0 ? 0 : file.Xref.Keys.Max();
        long size = file.Trailer["Size"] is PdfNumber { IsInteger: true } n && n.IntegerValue <= highest + 1_000_000 ? n.IntegerValue : 0;
        return (int)Math.Min(int.MaxValue / 2, Math.Max(size, highest + 1));
    }

    /// <summary>Reads the catalog, document information and page tree of an opened file.</summary>
    private void Attach(PdfFile file)
    {
        if (file.Trailer["Root"] is not PdfReference root || Resolve(root) is not PdfDictionary catalog)
        {
            throw new PdfFormatException("Document catalog is missing or not a dictionary.");
        }

        Catalog = catalog;
        CatalogNumber = root.Number;
        if (file.Trailer["Info"] is PdfReference info && Resolve(info) is PdfDictionary) InfoNumber = info.Number;
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) Id = id;
        LoadPageTree();
        _openedPages = Pages.Select(p => p.Number).ToList();
        ReportRepairs(file);
    }

    /// <summary>Tells the diagnostics what opening had to repair or could not read.</summary>
    private void ReportRepairs(PdfFile file)
    {
        PdfDiagnostics diagnostics = Budget.Diagnostics;
        if (file.WasReconstructed) diagnostics.Repaired("The cross-reference data was damaged; objects were found by scanning the file.");
        if (file.LengthsRepaired > 0) diagnostics.Repaired(Compat.Format("{0} stream length(s) were wrong and were corrected from the data.", file.LengthsRepaired));
        if (file.UnreadableObjects > 0) diagnostics.Skipped(Compat.Format("{0} object(s) could not be parsed and read as null.", file.UnreadableObjects));
    }

    /// <summary>Creates a document holding all pages of the given documents, in order.</summary>
    /// <remarks>
    /// Form fields come along with their widgets. A tagged source keeps its tags: the part of its
    /// structure tree that tags the pages is copied with them, its role map, class map and IDs
    /// merged and renamed where they clash, and its language kept, on the merged document when it
    /// is the first, else on its top level structure elements. Untagged sources add untagged
    /// pages without breaking the tree of tagged ones. Outlines, named destinations and other
    /// document level entries of the sources are not merged. The sources are not changed and must
    /// stay open until the merge returns.
    /// </remarks>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents) => Merge(documents, null, CancellationToken.None);

    /// <summary>Creates a document holding all pages of the given documents, in order, as <see cref="Merge(IEnumerable{PdfDocument})"/> does.</summary>
    /// <param name="documents">The documents whose pages to copy.</param>
    /// <param name="cancellationToken">Observed before each document and while its pages and their objects are copied.</param>
    /// <exception cref="OperationCanceledException">Cancelled before the merge completed; nothing is returned.</exception>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents, CancellationToken cancellationToken) => Merge(documents, null, cancellationToken);

    /// <summary>
    /// Creates a document holding all pages of the given documents, in order, as
    /// <see cref="Merge(IEnumerable{PdfDocument})"/> does, reporting progress after each document.
    /// </summary>
    /// <param name="documents">The documents whose pages to copy.</param>
    /// <param name="progress">Receives the pages merged so far out of all pages, once per document. May be null.</param>
    /// <param name="cancellationToken">Observed before each document and while its pages and their objects are copied.</param>
    /// <exception cref="OperationCanceledException">Cancelled before the merge completed; nothing is returned.</exception>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents, IProgress<PdfProgress>? progress, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(documents, nameof(documents));
        cancellationToken.ThrowIfCancellationRequested();

        // The total is the only reason to read the whole sequence first; without progress it is
        // taken one document at a time, so a lazy or huge sequence stops where the token does.
        List<PdfDocument>? all = progress is null ? null : [.. documents];
        int total = all?.Sum(d => d.Pages.Count) ?? 0, done = 0;
        PdfDocument result = Create();
        foreach (PdfDocument source in all ?? documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Pages.Import(source.Pages, null, cancellationToken);
            done += source.Pages.Count;
            progress?.Report(new PdfProgress(done, total));
        }

        return result;
    }

    /// <summary>
    /// Merges PDFs read from streams into one written to <paramref name="output"/>, as
    /// <see cref="Merge(IEnumerable{PdfDocument})"/> merges documents, holding one input at a time.
    /// Each input stream is disposed as soon as it is read.
    /// </summary>
    /// <param name="inputs">The PDFs to merge, in order, each read from its position to its end.</param>
    /// <param name="output">Receives the merged PDF from its position. It is flushed, not disposed.</param>
    /// <param name="cancellationToken">Observed while each input is read and copied and while the output is written.</param>
    /// <exception cref="PdfFormatException">An input is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened without a password.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static Task MergeAsync(IEnumerable<Stream> inputs, Stream output, CancellationToken cancellationToken = default) =>
        MergeAsync(inputs, output, null, null, cancellationToken);

    /// <summary>
    /// Merges PDFs read from streams into one written to <paramref name="output"/>, as
    /// <see cref="Merge(IEnumerable{PdfDocument})"/> merges documents, holding one input at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inputs are taken from <paramref name="inputs"/> one by one, so an enumeration that opens
    /// each stream as it is reached, such as <c>paths.Select(File.OpenRead)</c>, has one input
    /// open at a time. Each is read to its end, asynchronously, and disposed straight away unless
    /// <see cref="PdfMergeOptions.LeaveInputsOpen"/> is set; its pages are copied and the input
    /// document is released before the next input is read. By default inputs are opened on demand
    /// (<see cref="PdfLoadMode.OnDemand"/>), so only the objects the pages use are parsed, and a
    /// large input is spooled to a temporary file that is deleted once its pages are copied.
    /// </para>
    /// <para>
    /// The merged document is then saved as <see cref="SaveAsync(Stream, PdfSaveOptions?, CancellationToken)"/>
    /// saves it. It holds a copy of every page's objects until it is written. With the default
    /// save options the whole output is also built and optimized in memory; set
    /// <see cref="PdfSaveOptions.Optimize"/> to false in <see cref="PdfMergeOptions.SaveOptions"/>
    /// to write it progressively.
    /// </para>
    /// <para>
    /// When an input fails to open, the exception propagates; inputs not reached yet are not
    /// enumerated, and nothing is written to <paramref name="output"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// string[] paths = ["a.pdf", "b.pdf", "c.pdf"];
    /// await using FileStream output = File.Create("merged.pdf");
    /// await PdfDocument.MergeAsync(paths.Select(File.OpenRead), output, cancellationToken);
    /// </code>
    /// </example>
    /// <param name="inputs">The PDFs to merge, in order, each read from its position to its end.</param>
    /// <param name="output">Receives the merged PDF from its position. It is flushed, not disposed.</param>
    /// <param name="options">How inputs are opened and handed back and how the output is saved; <see cref="PdfMergeOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Receives the inputs merged so far after each one, out of how many there are when
    /// <paramref name="inputs"/> is a collection, else out of an unknown total. May be null.
    /// </param>
    /// <param name="cancellationToken">Observed while each input is read and copied and while the output is written.</param>
    /// <exception cref="ArgumentException"><paramref name="inputs"/> holds a null stream or one that cannot be read.</exception>
    /// <exception cref="PdfFormatException">An input is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and the load options' password does not open it.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static async Task MergeAsync(IEnumerable<Stream> inputs, Stream output, PdfMergeOptions? options, IProgress<PdfProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(inputs, nameof(inputs));
        Compat.ThrowIfNull(output, nameof(output));
        options ??= PdfMergeOptions.Default;
        PdfLoadOptions load = options.LoadOptions ?? PdfMergeOptions.OnDemand;
        int? total = inputs switch
        {
            IReadOnlyCollection<Stream> c => c.Count,
            ICollection<Stream> c => c.Count,
            _ => null,
        };

        PdfDocument result = Create();
        int done = 0;
        foreach (Stream input in inputs)
        {
            if (input is null) throw new ArgumentException("The inputs hold a null stream.", nameof(inputs));
            PdfDocument source = await OpenInputAsync(input, load, options.LeaveInputsOpen, cancellationToken).ConfigureAwait(false);

            using (source)
            {
                result.Pages.Import(source.Pages, null, cancellationToken);

                // The copies hold their own data; the source and its parsed objects can go.
                result.ForgetImporter(source);
            }

            done++;
            progress?.Report(new PdfProgress(done, total));
            cancellationToken.ThrowIfCancellationRequested();
        }

        await result.SaveAsync(output, options.SaveOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens one merge input and disposes its stream, whether opening succeeds, fails or is cancelled.</summary>
    private static async Task<PdfDocument> OpenInputAsync(Stream input, PdfLoadOptions load, bool leaveOpen, CancellationToken cancellationToken)
    {
        PdfDocument source;
        try
        {
            // Inside the try so a cancelled merge still disposes the input it was handed.
            cancellationToken.ThrowIfCancellationRequested();
            source = await OpenAsync(input, load, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!leaveOpen) await DisposeAsync(input).ConfigureAwait(false);
            throw;
        }

        // Opening reads the input to its end; it is not needed after that.
        if (leaveOpen) return source;
        try
        {
            await DisposeAsync(input).ConfigureAwait(false);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        return source;
    }

    private static Task DisposeAsync(Stream stream)
    {
#if NET8_0_OR_GREATER
        return stream.DisposeAsync().AsTask();
#else
        stream.Dispose();
        return Task.CompletedTask;
#endif
    }

    /// <summary>
    /// Copies <paramref name="count"/> pages from <paramref name="start"/> into a new document,
    /// with their resources, annotations, form fields and structure tags; this document is not
    /// changed. Dispose the result when done with it.
    /// </summary>
    /// <param name="start">Zero based index of the first page.</param>
    /// <param name="count">How many pages to copy.</param>
    /// <param name="cancellationToken">Observed while the pages and their objects are copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">The pages are not all in the document.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the copy completed.</exception>
    public PdfDocument ExtractPages(int start, int count, CancellationToken cancellationToken = default)
    {
        if (start < 0 || start > Pages.Count) throw new ArgumentOutOfRangeException(nameof(start), start, "The start is not a page of the document.");
        if (count < 0 || count > Pages.Count - start) throw new ArgumentOutOfRangeException(nameof(count), count, "The pages run past the end of the document.");
        PdfDocument result = Create();
        result.Pages.Import([.. Pages.Skip(start).Take(count)], null, cancellationToken);
        return result;
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Copies the pages in <paramref name="range"/> into a new document, as
    /// <see cref="ExtractPages(int, int, CancellationToken)"/> does: <c>doc.ExtractPages(..3)</c>
    /// copies the first three pages, <c>doc.ExtractPages(^2..)</c> the last two.
    /// </summary>
    /// <param name="range">The pages, by zero based index.</param>
    /// <param name="cancellationToken">Observed while the pages and their objects are copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the document.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the copy completed.</exception>
    public PdfDocument ExtractPages(Range range, CancellationToken cancellationToken = default)
    {
        (int offset, int length) = range.GetOffsetAndLength(Pages.Count);
        return ExtractPages(offset, length, cancellationToken);
    }
#endif

    /// <summary>
    /// Flattens the annotations of every page: each one's appearance becomes page content and the
    /// annotation goes. Links and form widgets stay unless <paramref name="options"/> asks for
    /// them; hidden annotations stay. Returns how many were flattened.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfDocument doc = PdfDocument.Open("reviewed.pdf");
    /// doc.FlattenAnnotations();
    /// doc.Save("reviewed-flat.pdf");
    /// </code>
    /// </example>
    public int FlattenAnnotations(PdfFlattenOptions? options = null) => FlattenAnnotations(options, null, CancellationToken.None);

    /// <summary>Flattens the annotations of every page as <see cref="FlattenAnnotations(PdfFlattenOptions?)"/> does, stopping when cancelled.</summary>
    /// <param name="options">Which annotations to flatten besides markup; null for the defaults.</param>
    /// <param name="cancellationToken">Observed before each page. Pages done before it fires stay flattened.</param>
    /// <exception cref="OperationCanceledException">Cancelled before every page was flattened.</exception>
    public int FlattenAnnotations(PdfFlattenOptions? options, CancellationToken cancellationToken) => FlattenAnnotations(options, null, cancellationToken);

    /// <summary>
    /// Flattens the annotations of every page as <see cref="FlattenAnnotations(PdfFlattenOptions?)"/>
    /// does, reporting progress after each page.
    /// </summary>
    /// <param name="options">Which annotations to flatten besides markup; null for the defaults.</param>
    /// <param name="progress">Receives the pages done out of all pages, after each page. May be null.</param>
    /// <param name="cancellationToken">Observed before each page. Pages done before it fires stay flattened.</param>
    /// <exception cref="OperationCanceledException">Cancelled before every page was flattened.</exception>
    public int FlattenAnnotations(PdfFlattenOptions? options, IProgress<PdfProgress>? progress, CancellationToken cancellationToken = default)
    {
        int flattened = 0, done = 0, total = Pages.Count;
        foreach (PdfPage page in Pages.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            flattened += page.Annotations.Flatten(options);
            done++;
            progress?.Report(new PdfProgress(done, total));
        }

        return flattened;
    }

    /// <summary>Returns one new document per page, made as the enumeration reaches it.</summary>
    /// <remarks>
    /// <para>
    /// Each part is a new document the caller owns: dispose every one, for instance with a
    /// <c>using</c> declaration in the loop. Parts are made lazily, one per step of the
    /// enumeration, so a loop that saves and disposes each part holds one at a time; collecting
    /// them with <c>ToList()</c> holds them all. Each part has its own copy of the resources its
    /// page uses, and the page's form fields and structure tags. This document is not changed and
    /// must stay open until the enumeration ends.
    /// </para>
    /// <para>
    /// To write the parts straight to files or other streams, use
    /// <see cref="SplitTo(Func{int, Stream}, PdfSaveOptions?, IProgress{PdfProgress}?, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// int number = 1;
    /// foreach (PdfDocument part in doc.Split())
    /// {
    ///     using (part) part.Save($"page-{number++}.pdf");
    /// }
    /// </code>
    /// </example>
    public IEnumerable<PdfDocument> Split() => Split(null, CancellationToken.None);

    /// <summary>Returns one new document per page, as <see cref="Split()"/> does, stopping when cancelled.</summary>
    /// <param name="cancellationToken">Observed before each part and while its page is copied.</param>
    /// <exception cref="OperationCanceledException">Cancelled; parts already returned stay the caller's to dispose.</exception>
    public IEnumerable<PdfDocument> Split(CancellationToken cancellationToken) => Split(null, cancellationToken);

    /// <summary>Returns one new document per page, as <see cref="Split()"/> does, reporting each part as it is made.</summary>
    /// <param name="progress">Receives the parts made so far out of the page count, as each is made. May be null.</param>
    /// <param name="cancellationToken">Observed before each part and while its page is copied.</param>
    /// <exception cref="OperationCanceledException">Cancelled; parts already returned stay the caller's to dispose.</exception>
    public IEnumerable<PdfDocument> Split(IProgress<PdfProgress>? progress, CancellationToken cancellationToken = default)
    {
        int done = 0, total = Pages.Count;
        foreach (PdfDocument part in SplitParts(cancellationToken))
        {
            done++;
            progress?.Report(new PdfProgress(done, total));
            yield return part;
        }
    }

    /// <summary>
    /// Writes each page as a PDF of its own to the stream <paramref name="createOutput"/> returns
    /// for it, holding one part at a time. Returns how many parts were written.
    /// </summary>
    /// <remarks>
    /// Each part is saved as <see cref="Save(Stream, PdfSaveOptions?)"/> saves, then its stream is
    /// disposed before the next one is asked for, which commits it for streams such as zip
    /// entries, files and blob uploads. A <see cref="MemoryStream"/> still returns its bytes
    /// through <see cref="MemoryStream.ToArray"/> once disposed. <see cref="SplitTo"/> and
    /// <see cref="SplitToAsync"/> keep the parts written before. When the token fires while a part
    /// is being written, that part's stream is disposed and holds no complete file; progress
    /// reports count only complete parts.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var zip = new ZipArchive(File.Create("pages.zip"), ZipArchiveMode.Create);
    /// doc.SplitTo(i => zip.CreateEntry($"page-{i + 1}.pdf").Open());
    /// </code>
    /// </example>
    /// <param name="createOutput">Called with each zero based page index; returns a writable stream for that page's PDF.</param>
    /// <param name="options">Save options for every part; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="progress">Receives the parts written so far out of the page count, after each part. May be null.</param>
    /// <param name="cancellationToken">Observed before each part, while its page is copied and while it is saved.</param>
    /// <exception cref="InvalidOperationException"><paramref name="createOutput"/> returned null.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; parts written before stay written.</exception>
    public int SplitTo(Func<int, Stream> createOutput, PdfSaveOptions? options = null, IProgress<PdfProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(createOutput, nameof(createOutput));
        int written = 0, total = Pages.Count;
        foreach (PdfDocument part in SplitParts(cancellationToken))
        {
            using (part)
            {
                using Stream output = createOutput(written) ?? throw new InvalidOperationException("The output factory returned no stream.");
                part.Save(output, options, cancellationToken);
            }

            written++;
            progress?.Report(new PdfProgress(written, total));
        }

        return written;
    }

    /// <summary>
    /// Writes each page as a PDF of its own to the stream <paramref name="createOutput"/> returns
    /// for it, as <see cref="SplitTo(Func{int, Stream}, PdfSaveOptions?, IProgress{PdfProgress}?, CancellationToken)"/>
    /// does, writing and disposing each stream asynchronously.
    /// </summary>
    /// <param name="createOutput">Called with each zero based page index; returns a writable stream for that page's PDF.</param>
    /// <param name="options">Save options for every part; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="progress">Receives the parts written so far out of the page count, after each part. May be null.</param>
    /// <param name="cancellationToken">Observed before each part, while its page is copied and while it is saved.</param>
    /// <exception cref="InvalidOperationException"><paramref name="createOutput"/> returned null.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; parts written before stay written.</exception>
    public async Task<int> SplitToAsync(Func<int, Stream> createOutput, PdfSaveOptions? options = null, IProgress<PdfProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(createOutput, nameof(createOutput));
        int written = 0, total = Pages.Count;
        foreach (PdfDocument part in SplitParts(cancellationToken))
        {
            using (part)
            {
                Stream output = createOutput(written) ?? throw new InvalidOperationException("The output factory returned no stream.");
                try
                {
                    await part.SaveAsync(output, options, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeAsync(output).ConfigureAwait(false);
                }
            }

            written++;
            progress?.Report(new PdfProgress(written, total));
        }

        return written;
    }

    /// <summary>
    /// The parts of a split, one per page, made lazily. The structure tree is read once for all
    /// parts, and no part keeps a link to this document once made.
    /// </summary>
    private IEnumerable<PdfDocument> SplitParts(CancellationToken ct)
    {
        StructureIndex? structure = null;
        bool structureRead = false;
        foreach (PdfPage page in Pages.ToList())
        {
            ct.ThrowIfCancellationRequested();
            PdfDocument part = Create();
            PageImporter importer = part.ImporterFor(this);
            if (structureRead) importer.UseSourceStructure(structure);
            else structure = importer.SourceStructure(ct);
            structureRead = true;
            part.Pages.Import([page], null, ct);
            part.ForgetImporter(this);
            yield return part;
        }
    }

    /// <summary>Saves to a file, replacing it atomically.</summary>
    /// <remarks>Written as <see cref="Save(Stream, PdfSaveOptions?)"/> writes, to a temporary file next to it that is then moved into place.</remarks>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                Save(stream, options);
            }

            Compat.MoveOverwrite(temp, full);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Saves the document as a new single revision PDF, the same bytes <see cref="ToArray(PdfSaveOptions?)"/>
    /// returns for the same options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see cref="PdfSaveOptions.Optimize"/> off, the file is written progressively: each
    /// object, or batch of objects packed into an object stream, goes to <paramref name="stream"/>
    /// as soon as it is serialized, and the cross-reference data comes last. Beyond the document
    /// itself, memory then follows the largest object or batch, not the file; stream data of a
    /// document opened with <see cref="OpenOnDemand"/> is read from its source as it is written.
    /// </para>
    /// <para>
    /// With <see cref="PdfSaveOptions.Optimize"/> on, the default, the whole file is built in
    /// memory first: the optimizer reads all of it and proves its rewrite equivalent before any
    /// of it is used, and encryption, when set, is a last pass over the optimized file. To
    /// optimize a document too large for that, save it with <c>Optimize = false</c> to a file and
    /// run <see cref="PdfOptimizer"/> on that file with <see cref="PdfOptimizerOptions.Streaming"/>.
    /// </para>
    /// <para>
    /// When writing fails, the exception propagates and what reached <paramref name="stream"/> is
    /// not a complete file.
    /// </para>
    /// </remarks>
    public void Save(Stream stream, PdfSaveOptions? options = null) => Save(stream, options, CancellationToken.None);

    /// <summary>
    /// Saves the document as <see cref="Save(Stream, PdfSaveOptions?)"/> does, stopping when
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <param name="stream">Receives the PDF from its current position.</param>
    /// <param name="options">Save options; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">
    /// Observed before each object is written and, when optimizing, between and within the
    /// optimizer's passes. When it fires, what reached the stream is not a complete file.
    /// </param>
    /// <exception cref="OperationCanceledException">Cancelled before the file was written.</exception>
    public void Save(Stream stream, PdfSaveOptions? options, CancellationToken cancellationToken)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        options ??= PdfSaveOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Optimize)
        {
            stream.Write(Build(options, cancellationToken));
            return;
        }

        SavePlan plan = PlanSave(options);
        foreach (ReadOnlyMemory<byte> chunk in PdfDocumentWriter.WriteChunks(plan.Objects, plan.Trailer, plan.Map, plan.Settings, ct: cancellationToken))
        {
            stream.Write(chunk.Span);
        }
    }

    /// <summary>
    /// Saves the document as <see cref="Save(Stream, PdfSaveOptions?)"/> does, writing to
    /// <paramref name="stream"/> only asynchronously, and flushes it. Suits response bodies and
    /// other streams that forbid synchronous writes, such as those of ASP.NET Core and Azure
    /// Functions.
    /// </summary>
    /// <remarks>
    /// Serializing, compressing and optimizing run on the calling thread between writes; they
    /// are work for the processor, not waits. Memory use is as described for
    /// <see cref="Save(Stream, PdfSaveOptions?)"/>.
    /// </remarks>
    /// <param name="stream">Receives the PDF from its current position.</param>
    /// <param name="options">Save options; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">
    /// Observed before each object is written and by each write. When it fires, the save stops
    /// with <see cref="OperationCanceledException"/> and what reached the stream is not a complete file.
    /// </param>
    public async Task SaveAsync(Stream stream, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        options ??= PdfSaveOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Optimize)
        {
            byte[] data = Build(options, cancellationToken);
            await OutputStreams.WriteAsync(stream, data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SavePlan plan = PlanSave(options);
            foreach (ReadOnlyMemory<byte> chunk in PdfDocumentWriter.WriteChunks(plan.Objects, plan.Trailer, plan.Map, plan.Settings, ct: cancellationToken))
            {
                await OutputStreams.WriteAsync(stream, chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves to a file as <see cref="Save(string, PdfSaveOptions?)"/> does, writing
    /// asynchronously. The file is replaced only once the whole output is written; on failure or
    /// cancellation it is left as it was.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="options">Save options; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed before each object is written and by each write.</param>
    public async Task SaveAsync(string path, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(path, nameof(path));
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await SaveAsync(stream, options, cancellationToken).ConfigureAwait(false);
            }

            Compat.MoveOverwrite(temp, full);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Saves the changes made since the document was opened as an incremental update (ISO
    /// 32000-1, 7.5.6): the original file byte for byte, followed by the objects that changed or
    /// were added and a cross-reference section that points back to the original's. Signatures
    /// over the original bytes stay valid, and for a large file only the update is new data.
    /// </summary>
    /// <remarks>
    /// Each object the document has read is compared with the file: it is written when anything
    /// in it differs. New objects are written when a written object refers to them. When pages
    /// were added, removed or reordered, the page tree is written again as one node. Nothing is
    /// recompressed or optimized, and <see cref="PdfSaveOptions"/> does not apply.
    /// </remarks>
    /// <param name="output">
    /// Receives the original file followed by the update, written from its current position.
    /// When it is the stream given to <see cref="OpenOnDemand"/>, only the update is written, at
    /// the end of that stream, and later saves build on the updated file.
    /// </param>
    /// <exception cref="InvalidOperationException">The document was created rather than opened, or has no pages.</exception>
    /// <exception cref="NotSupportedException">
    /// The document is encrypted or set to be encrypted, or it was damaged and had to be rebuilt
    /// when opened. Save such documents in full with <see cref="Save(Stream, PdfSaveOptions?)"/>.
    /// </exception>
    public void SaveIncremental(Stream output)
    {
        Compat.ThrowIfNull(output, nameof(output));
        ThrowIfDisposed();
        if (_file is null) throw new InvalidOperationException("Only an opened document can be saved incrementally.");
        if (Security.Source is not null || Security.Target is not null) throw new NotSupportedException("Incremental saving of encrypted documents is not supported.");
        if (_file.WasReconstructed) throw new NotSupportedException("The document was damaged and rebuilt when opened; save it in full instead.");

        FinishFonts();
        if (Pages.Count == 0) throw new InvalidOperationException("A PDF must have at least one page.");
        _outlines?.WriteTo(this);

        PdfFile baseline = _baseline ??= _file.Fork();
        if (!PageTreeUnchanged(baseline))
        {
            var pagesNode = new PdfDictionary { ["Type"] = new PdfName("Pages") };
            int pagesNumber = Add(pagesNode);
            var kids = new PdfArray();
            foreach (PdfPage page in Pages)
            {
                page.Dictionary.Set("Parent", new PdfReference(pagesNumber, 0));
                kids.Items.Add(new PdfReference(page.Number, baseline.GenerationOf(page.Number)));
            }

            pagesNode.Set("Kids", kids);
            pagesNode.Set("Count", PdfNumber.FromInteger(Pages.Count));
            Catalog.Set("Pages", new PdfReference(pagesNumber, 0));
        }

        Dictionary<int, (PdfObject Object, int Generation)> update = ChangedObjects(baseline);
        var trailer = new PdfDictionary();
        trailer.Set("Root", new PdfReference(CatalogNumber, baseline.GenerationOf(CatalogNumber)));
        if (InfoNumber is { } info && Objects.ContainsKey(info)) trailer.Set("Info", new PdfReference(info, baseline.GenerationOf(info)));
        if (Id is not null) trailer.Set("ID", Id);

        long previousSize = baseline.Trailer["Size"] is PdfNumber { IsInteger: true } size ? size.IntegerValue : 0;
        int xrefNumber = (int)Math.Min(int.MaxValue - 1, Math.Max(Math.Max(previousSize, NextNumber), update.Count == 0 ? 0 : update.Keys.Max() + 1L));
        NextNumber = xrefNumber + 1;

        bool inPlace = ReferenceEquals(output, _sourceStream);
        if (!inPlace)
        {
            const int chunk = 1 << 20;
            for (long at = 0; at < baseline.Length; at += chunk)
            {
                output.Write(baseline.Source.Read(at, Math.Min(baseline.Length, at + chunk)).Span);
            }
        }

        // Nothing changed: the file as it is already says everything.
        if (update.Count == 0)
        {
            output.Flush();
            return;
        }

        ReadOnlySpan<byte> tail = baseline.Source.Read(baseline.Length - 1, baseline.Length).Span;
        bool needsBreak = tail.Length == 1 && tail[0] is not ((byte)'\n' or (byte)'\r');
        long start = baseline.Length + (needsBreak ? 1 : 0);
        byte[] section = IncrementalWriter.Write(update, trailer, start, baseline.OffsetBase, previousSize,
            baseline.LastStartXref(), baseline.UsesXrefStreams, xrefNumber);

        if (inPlace) output.Seek(0, SeekOrigin.End);
        if (needsBreak) output.WriteByte((byte)'\n');
        output.Write(section);
        output.Flush();

        if (inPlace)
        {
            // The stream now holds the update; later saves compare with it and append to it.
            _baseline = PdfFile.Open(output, DecodeLimit, Budget);
            foreach (int number in update.Keys) _inherited.Remove(number);
            _openedPages = Pages.Select(p => p.Number).ToList();
            _pageTreeMatchesFile = true;
        }
    }

    /// <summary>True when the pages are those the file's page tree lists, in its order, under its root.</summary>
    private bool PageTreeUnchanged(PdfFile baseline) =>
        _pageTreeMatchesFile
        && Pages.Select(p => p.Number).SequenceEqual(_openedPages)
        && Catalog["Pages"] is PdfReference pages
        && baseline.ResolveDictionary(baseline.Trailer["Root"])?["Pages"] is PdfReference original
        && pages.Number == original.Number
        && Pages.All(p => p.Dictionary["Parent"] is PdfReference parent && baseline.Exists(parent.Number));

    /// <summary>
    /// Objects that differ from <paramref name="baseline"/>, and the objects new to the file that
    /// they or the trailer reach, with the generation each is written under.
    /// </summary>
    private Dictionary<int, (PdfObject Object, int Generation)> ChangedObjects(PdfFile baseline)
    {
        var update = new Dictionary<int, (PdfObject, int)>();
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var x = new ByteSink(1024);
        var y = new ByteSink(1024);
        var candidates = new HashSet<int>(Objects.Held);
        candidates.UnionWith(_file!.CachedNumbers);
        var pending = new Queue<PdfObject>();
        foreach (int number in candidates.OrderBy(n => n))
        {
            if (!baseline.Exists(number) || !Objects.TryGetValue(number, out PdfObject current)) continue;
            if (!Differs(number, current, baseline.GetObject(number))) continue;
            update[number] = (current, baseline.GenerationOf(number));
            pending.Enqueue(current);
        }

        foreach (int number in new[] { CatalogNumber, InfoNumber ?? -1 })
        {
            if (number >= 0 && !baseline.Exists(number) && Objects.TryGetValue(number, out PdfObject root) && update.TryAdd(number, (root, 0))) pending.Enqueue(root);
        }

        while (pending.Count > 0)
        {
            foreach (int number in References(pending.Dequeue()))
            {
                if (baseline.Exists(number) || update.ContainsKey(number) || !Objects.TryGetValue(number, out PdfObject added)) continue;
                update[number] = (added, 0);
                pending.Enqueue(added);
            }
        }

        return update;

        bool Differs(int number, PdfObject current, PdfObject original)
        {
            if (current is PdfStream cs)
            {
                if (original is not PdfStream os || cs.Version != os.Version || cs.IsDeferred != os.IsDeferred) return true;
                return !Same(cs.Dictionary, os.Dictionary);
            }

            if (original is PdfStream) return true;

            if (current is PdfDictionary page && _inherited.TryGetValue(number, out List<(string Key, PdfObject Value)>? pushed))
            {
                // Inherited attributes were copied onto the page when it was opened; that is no change.
                page = Clone(page);
                foreach ((string key, PdfObject value) in pushed)
                {
                    if (ReferenceEquals(page[key], value)) page.Remove(key);
                }

                current = page;
            }

            return !Same(current, original);
        }

        bool Same(PdfObject a, PdfObject b)
        {
            x.Clear();
            y.Clear();
            serializer.Write(x, a);
            serializer.Write(y, b);
            return x.WrittenSpan.SequenceEqual(y.WrittenSpan);
        }
    }

    /// <summary>
    /// Frees the memory of objects that a document read on demand has parsed and nothing uses any
    /// more, such as the fonts, resources and annotations of pages already processed. They are
    /// parsed again from the file if they are needed later. No edit is lost: an object that
    /// differs from the file is kept, and so is every object still referenced from anywhere else,
    /// by the document or by the application, which goes on working on the same instance.
    /// </summary>
    /// <remarks>
    /// Without it, a document read on demand keeps every object it has parsed, so memory grows with
    /// the pages processed (about 24 KB a page for pages with their own font). To find the objects
    /// still in use, this runs a full garbage collection, which takes time on a large heap: call it
    /// every few dozen pages rather than after each one. A document read into memory holds all its
    /// objects by design, and this does nothing for it. Decoded object streams are kept apart, up
    /// to <see cref="PdfLoadOptions.MaxDecodedStreamBytes"/> in total.
    /// </remarks>
    /// <example>
    /// <code>
    /// using PdfDocument doc = PdfDocument.Open("archive.pdf", new PdfLoadOptions { Mode = PdfLoadMode.OnDemand });
    /// for (int i = 0; i &lt; doc.Pages.Count; i++)
    /// {
    ///     Index(doc.Pages[i].ExtractText());
    ///     if (i % 50 == 49) doc.ReleaseUnusedObjects();
    /// }
    /// </code>
    /// </example>
    /// <returns>How many objects were released.</returns>
    /// <exception cref="ObjectDisposedException">The document was disposed.</exception>
    public int ReleaseUnusedObjects()
    {
        ThrowIfDisposed();
        if (_file is null || !Objects.ReadsFile) return 0;

        // The copy an incremental save compares with never hands its objects out.
        _baseline?.ReleaseAll();
        return _file.Release(Unchanged());
    }

    /// <summary>Parsed objects of the file that still read as the file has them.</summary>
    private List<int> Unchanged()
    {
        var unchanged = new List<int>();
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        var x = new ByteSink(1024);
        var y = new ByteSink(1024);
        foreach (int number in _file!.CachedNumbers)
        {
            if (!_file.TryGetCached(number, out PdfObject current) || current is PdfNull) continue;
            if (_file.ReadAgain(number) is not { } original) continue;
            bool same = current switch
            {
                PdfStream cs => original is PdfStream os && cs.Version == os.Version && cs.IsDeferred == os.IsDeferred && Same(cs.Dictionary, os.Dictionary),
                _ => original is not PdfStream && Same(current, original),
            };
            if (same) unchanged.Add(number);
        }

        return unchanged;

        bool Same(PdfObject a, PdfObject b)
        {
            x.Clear();
            y.Clear();
            serializer.Write(x, a);
            serializer.Write(y, b);
            return x.WrittenSpan.SequenceEqual(y.WrittenSpan);
        }
    }

    /// <summary>Numbers of the objects <paramref name="obj"/> refers to directly.</summary>
    private static List<int> References(PdfObject obj)
    {
        var found = new List<int>();
        var stack = new Stack<(PdfObject Value, int Depth)>();
        stack.Push((obj, 0));
        while (stack.Count > 0)
        {
            (PdfObject value, int depth) = stack.Pop();
            if (depth > PdfParser.MaxNesting) continue;
            switch (value)
            {
                case PdfReference r:
                    found.Add(r.Number);
                    break;
                case PdfStream s:
                    stack.Push((s.Dictionary, depth + 1));
                    break;
                case PdfDictionary d:
                    for (int i = 0; i < d.Count; i++) stack.Push((d.ValueAt(i), depth + 1));
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array.Items) stack.Push((item, depth + 1));
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Saves the document to a byte array. The output is the same, byte for byte, as what
    /// <see cref="Save(Stream, PdfSaveOptions?)"/> writes with the same options. Two saves of an
    /// encrypted document still differ in their random initialization vectors, and two PDF/A saves
    /// in the instance id and time their metadata records.
    /// </summary>
    public byte[] ToArray(PdfSaveOptions? options = null) => Build(options ?? PdfSaveOptions.Default, CancellationToken.None);

    /// <summary>The saved document as bytes, as <see cref="ToArray(PdfSaveOptions?)"/> returns them, stopping when cancelled.</summary>
    /// <param name="options">Save options; <see cref="PdfSaveOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed before each object is written and, when optimizing, between and within the optimizer's passes.</param>
    /// <exception cref="OperationCanceledException">Cancelled before the file was built.</exception>
    public byte[] ToArray(PdfSaveOptions? options, CancellationToken cancellationToken) => Build(options ?? PdfSaveOptions.Default, cancellationToken);

    /// <summary>The whole file in memory: written, then optimized and encrypted as the options say.</summary>
    private byte[] Build(PdfSaveOptions options, CancellationToken ct)
    {
        SavePlan plan = PlanSave(options);
        byte[] written = PdfDocumentWriter.Write(plan.Objects, plan.Trailer, plan.Map, plan.Settings).ToArray();
        if (!options.Optimize) return written;

        var optimizerOptions = new PdfOptimizerOptions
        {
            MinSavingPercent = 0,
            Compression = options.Compression,
            UseObjectStreams = plan.Settings.UseObjectStreams,
            Verify = options.VerifyOptimization,
            KeepMetadataUnfiltered = options.Conformance != PdfConformance.None,
        };
        (written, _) = PdfOptimizer.Optimize(written, optimizerOptions, ct);
        return Security.Target is { } encryption ? Encrypted(written, encryption, plan.Settings.UseObjectStreams, plan.Settings.CompressionLevel) : written;
    }

    /// <summary>What a full save writes: the objects in output order, the trailer, and how to write them.</summary>
    /// <param name="Objects">Object i is written with number i + 1.</param>
    private sealed record SavePlan(List<PdfObject> Objects, PdfDictionary Trailer, Func<PdfReference, int> Map, WriterSettings Settings);

    /// <summary>
    /// Finishes everything drawing and editing left open and lays out the file a full save writes.
    /// Without the optimizer the plan already encrypts; with it, encryption is a last pass over the
    /// optimized file (see <see cref="Encrypted"/>).
    /// </summary>
    private SavePlan PlanSave(PdfSaveOptions options)
    {
        ThrowIfDisposed();
        // Drawing still open is written now, so a missing Dispose does not lose it.
        foreach (Drawing.PdfGraphics g in _openGraphics.ToArray()) g.Dispose();

        // Font dictionaries were reserved on first use; widths, encodings and subsets depend on
        // everything drawn, so they are written now. Subsetting and compressing each program is
        // the font's own work, so fonts do it side by side, then write in order.
        Fonts.FontResource[] fonts = [.. _fonts.Values];
        var finish = new Action?[fonts.Length];
        var failures = new ExceptionDispatchInfo?[fonts.Length];
        if (fonts.Length > 1)
        {
            Parallel.For(0, fonts.Length, i =>
            {
                try
                {
                    finish[i] = fonts[i].Prepare();
                }
                catch (Exception e)
                {
                    failures[i] = ExceptionDispatchInfo.Capture(e);
                }
            });
        }

        for (int i = 0; i < fonts.Length; i++)
        {
            failures[i]?.Throw();
            (finish[i] ?? fonts[i].Prepare())();
        }

        if (Pages.Count == 0) throw new InvalidOperationException("A PDF must have at least one page.");
        if (Invoice is not null && options.Conformance == PdfConformance.None)
        {
            throw new InvalidOperationException("A Factur-X or ZUGFeRD invoice must be saved as PDF/A-3: set PdfSaveOptions.Conformance to PdfA3B or PdfA3U.");
        }

        // Bookmarks are written first, so PDF/A sees their actions as they will be saved.
        _outlines?.WriteTo(this);
        int archive = PdfArchive.Part(options.Conformance);
        if (archive != 0) PdfArchive.Prepare(this, options.Conformance);
        Security.RequireOwner();

        CompressionLevel level = options.Compression switch
        {
            CompressionEffort.Fast => CompressionLevel.Fastest,
            CompressionEffort.Balanced => CompressionLevel.Optimal,
            _ => Compat.MaximumCompression,
        };

        // Rebuild the page tree as one flat node. Old intermediate nodes and removed pages are
        // dead: references to them, from destinations or annotations, are written as null.
        var pagesNode = new PdfDictionary();
        pagesNode.Set("Type", new PdfName("Pages"));
        int pagesNumber = Add(pagesNode);
        var kids = new PdfArray();
        foreach (PdfPage page in Pages)
        {
            page.Dictionary.Set("Parent", new PdfReference(pagesNumber, 0));
            kids.Items.Add(new PdfReference(page.Number, 0));
        }

        pagesNode.Set("Kids", kids);
        pagesNode.Set("Count", PdfNumber.FromInteger(Pages.Count));
        Catalog.Set("Pages", new PdfReference(pagesNumber, 0));

        var dead = new HashSet<int>(_pageTreeNodes);
        var live = new HashSet<int>(Pages.Select(p => p.Number));
        foreach (int n in _everPages)
        {
            if (!live.Contains(n)) dead.Add(n);
        }

        dead.Remove(pagesNumber);

        var trailer = new PdfDictionary();
        trailer.Set("Root", new PdfReference(CatalogNumber, 0));
        int? archiveInfo = null;
        if (InfoNumber is { } info && Objects.ContainsKey(info))
        {
            // PDF/A-4 keeps the document information in XMP. The trailer has no /Info at all unless the
            // catalog has /PieceInfo, and then one holding only the modification date (ISO 19005-4, 6.1.3).
            if (archive == 4 && Resolve(new PdfReference(info, 0)) is PdfDictionary full)
            {
                if (Resolve(Catalog["PieceInfo"]) is not (null or PdfNull))
                {
                    var dates = new PdfDictionary();
                    if (full["ModDate"] is { } modified) dates.Set("ModDate", modified);
                    archiveInfo = Add(dates);
                    trailer.Set("Info", new PdfReference(archiveInfo.Value, 0));
                }
            }
            else
            {
                trailer.Set("Info", new PdfReference(info, 0));
            }
        }

        Id ??= NewId();
        trailer.Set("ID", Id);

        // Reachability over the object store; unreachable objects are simply not written.
        var order = new List<int>();
        var index = new Dictionary<int, int>();
        var queue = new Queue<int>();
        void Visit(PdfObject? o, int depth)
        {
            switch (o)
            {
                case PdfReference r when !dead.Contains(r.Number) && Objects.ContainsKey(r.Number) && !index.ContainsKey(r.Number):
                    index[r.Number] = order.Count + 1;
                    order.Add(r.Number);
                    queue.Enqueue(r.Number);
                    break;
                case PdfArray a when depth < PdfParser.MaxNesting:
                    foreach (PdfObject item in a.Items) Visit(item, depth + 1);
                    break;
                case PdfDictionary d when depth < PdfParser.MaxNesting:
                    for (int i = 0; i < d.Count; i++) Visit(d.ValueAt(i), depth + 1);
                    break;
                case PdfStream s:
                    Visit(s.Dictionary, depth + 1);
                    break;
            }
        }

        Visit(trailer, 0);
        while (queue.Count > 0) Visit(Objects[queue.Dequeue()], 0);

        // The pages node and the PDF/A-4 dates are rebuilt on every save; once listed for
        // writing they leave the store, so a second save starts clean.
        var objects = order.Select(n => Objects[n]).ToList();
        Objects.Remove(pagesNumber);
        if (archiveInfo is int infoCopy) Objects.Remove(infoCopy);

        // PDF/A-1 is PDF 1.4, which predates object and xref streams; PDF/A-4 is PDF 2.0.
        bool objectStreams = options.UseObjectStreams && archive != 1;
        string version = PdfArchive.HeaderVersion(archive) ?? (objectStreams && string.CompareOrdinal(Version, "1.5") < 0 ? "1.5" : Version);
        // The optimizer pass does the exhaustive search; without it, the writer does.
        var settings = new WriterSettings(objectStreams, 200, version, level)
        {
            Exhaustive = options.Compression == CompressionEffort.Exhaustive && !options.Optimize,
        };

        int Map(PdfReference r) => index.TryGetValue(r.Number, out int n) ? n : -1;
        if (options.Optimize || Security.Target is not { } encryption) return new SavePlan(objects, trailer, Map, settings);

        // Without the optimizer nothing needs the plain file, so the one pass encrypts.
        objects.Add(encryption.Dictionary);
        int encryptNumber = objects.Count;
        var encrypt = new PdfReference(encryptNumber, 0);
        trailer.Set("Encrypt", encrypt);
        version = EncryptedVersion(encryption, version, objectStreams);
        if (NeedsAdobeExtension(encryption, version))
        {
            // The catalog written gets the extension; the document's own stays as it is.
            PdfDictionary catalog = Clone(Catalog);
            AddAdobeExtension(catalog, Resolve);
            objects[index[CatalogNumber] - 1] = catalog;
        }

        int MapEncrypted(PdfReference r) => ReferenceEquals(r, encrypt) ? encryptNumber : Map(r);
        return new SavePlan(objects, trailer, MapEncrypted, settings with
        {
            Version = version,
            Encryption = encryption,
            EncryptionDictionaryNumber = encryptNumber,
        });
    }

    /// <summary>The lowest version that has <paramref name="encryption"/>'s method, and object streams when used.</summary>
    private static string EncryptedVersion(StandardSecurity encryption, string version, bool objectStreams)
    {
        string minimum = encryption.StreamMethod switch
        {
            CryptMethod.AesV3 => "1.7",
            CryptMethod.AesV2 => "1.6",
            _ => "1.4",
        };
        if (string.CompareOrdinal(version, minimum) < 0) version = minimum;
        if (objectStreams && string.CompareOrdinal(version, "1.5") < 0) version = "1.5";
        return version;
    }

    /// <summary>AES-256 belongs to PDF 2.0; a 1.7 file announces it through Adobe extension level 8.</summary>
    private static bool NeedsAdobeExtension(StandardSecurity encryption, string version) =>
        encryption.Revision >= 5 && string.CompareOrdinal(version, "2.0") < 0;

    private static void AddAdobeExtension(PdfDictionary catalog, Func<PdfObject?, PdfObject?> resolve)
    {
        var adbe = new PdfDictionary();
        adbe.Set("BaseVersion", new PdfName("1.7"));
        adbe.Set("ExtensionLevel", PdfNumber.FromInteger(8));
        PdfDictionary extensions = resolve(catalog["Extensions"]) is PdfDictionary existing ? Clone(existing) : new PdfDictionary();
        extensions.Set("ADBE", adbe);
        catalog.Set("Extensions", extensions);
    }

    /// <summary>
    /// Encryption is the last step: the finished, optimized and verified plain file is re-read and
    /// written once more with every string and stream encrypted.
    /// </summary>
    private static byte[] Encrypted(byte[] plain, StandardSecurity encryption, bool objectStreams, CompressionLevel level)
    {
        PdfFile file = PdfFile.Open(plain, 1L << 30);
        ObjectGraph graph = ObjectGraph.Collect(file, stripThumbnails: false, stripPieceInfo: false, CancellationToken.None);
        var objects = graph.Order.Select(file.GetObject).ToList();
        var index = new Dictionary<int, int>();
        for (int i = 0; i < graph.Order.Count; i++) index[graph.Order[i]] = i + 1;

        objects.Add(encryption.Dictionary);
        int encryptNumber = objects.Count;
        const int encryptMarker = int.MaxValue;
        int Map(PdfReference r) => r.Number == encryptMarker ? encryptNumber : index.TryGetValue(r.Number, out int n) ? n : -1;

        string version = EncryptedVersion(encryption, file.HeaderVersion, objectStreams);
        if (NeedsAdobeExtension(encryption, version) && file.ResolveDictionary(file.Trailer["Root"]) is { } catalog) AddAdobeExtension(catalog, file.Resolve);

        var trailer = new PdfDictionary();
        if (file.Trailer["Root"] is { } root) trailer.Set("Root", root);
        if (file.Trailer["Info"] is { } info) trailer.Set("Info", info);
        if (file.Resolve(file.Trailer["ID"]) is PdfArray id) trailer.Set("ID", id);
        trailer.Set("Encrypt", new PdfReference(encryptMarker, 0));

        var settings = new WriterSettings(objectStreams, 200, version, level)
        {
            Encryption = encryption,
            EncryptionDictionaryNumber = encryptNumber,
        };
        return PdfDocumentWriter.Write(objects, trailer, Map, settings).ToArray();
    }

    /// <summary>The first /ID entry, creating the ID when the document has none. Key derivation for AES-128 needs it.</summary>
    internal byte[] EnsureId()
    {
        Id ??= NewId();
        return Id is PdfArray { Count: > 0 } a && a[0] is PdfString s ? s.Value : [];
    }

    private static PdfArray NewId()
    {
        byte[] id = Compat.RandomBytes(16);
        return new PdfArray([new PdfString(id, wasHex: true), new PdfString(id, wasHex: true)]);
    }

    /// <summary>
    /// Adds <paramref name="obj"/> as a new indirect object and returns a reference to it. Streams
    /// must be added this way. The object is written on save only if something references it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is a reference.</exception>
    public PdfReference AddObject(PdfObject obj)
    {
        Compat.ThrowIfNull(obj, nameof(obj));
        ThrowIfDisposed();
        if (obj is PdfReference) throw new ArgumentException("A reference cannot be an indirect object.", nameof(obj));
        return new PdfReference(Add(obj), 0);
    }

    /// <summary>
    /// Decodes a stream's data: Flate, LZW, ASCIIHex, ASCII85 and RunLength filters and PNG or TIFF
    /// predictors are undone. Image codecs (DCTDecode, JPXDecode, JBIG2Decode, CCITTFaxDecode) are
    /// left in place, so an image comes back as its JPEG or JPEG 2000 file.
    /// </summary>
    /// <exception cref="PdfFormatException">The data is damaged or exceeds the decode limit.</exception>
    /// <exception cref="PdfLimitExceededException">The document has decoded as much as its <see cref="PdfLoadOptions.Limits"/> allow.</exception>
    public byte[] GetStreamData(PdfStream stream) => GetStreamData(stream, DecodeLimit);

    /// <summary>
    /// The decoded data, at most <paramref name="limit"/> bytes, charged to the document's budget.
    /// A limit below the document's ceiling is what is left of a page's budget.
    /// </summary>
    /// <exception cref="PdfFormatException">The data is damaged or exceeds the document's ceiling.</exception>
    /// <exception cref="PdfLimitExceededException">The data exceeds a page's <paramref name="limit"/>, or the document's budget.</exception>
    internal byte[] GetStreamData(PdfStream stream, long limit)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        ThrowIfDisposed();
        try
        {
            List<Filters.FilterSpec> filters = Filters.StreamCodec.GetFilters(stream.Dictionary, Resolve);

            // /DL, the decoded length (ISO 32000-1, Table 5), is a hint: it only sizes the buffer.
            int size = Resolve(stream.Dictionary["DL"]) is PdfNumber { IsInteger: true } dl && dl.IntegerValue > 0 && dl.IntegerValue <= limit
                ? (int)Math.Min(dl.IntegerValue, Compat.MaxArrayLength)
                : 0;
            using Filters.DecodedStream decoded = Filters.StreamCodec.Decode(stream.Data, filters, Filters.DecodeDepth.Full, limit, Resolve, Budget, size);
            return decoded.ToArray();
        }
        catch (Filters.StreamDecodeException e) when (e.IsLimit && limit < DecodeLimit)
        {
            throw new PdfLimitExceededException(PdfLimitKind.PageContent, "The page's content decodes past its limit (PdfLoadOptions.MaxDecodedStreamBytes, all its streams and forms together); the rest is not read.");
        }
        catch (Filters.StreamDecodeException e)
        {
            throw new PdfFormatException("Stream data cannot be decoded: " + e.Message, e);
        }
    }

    internal int Add(PdfObject obj)
    {
        int number = NextNumber++;
        Objects[number] = obj;
        return number;
    }

    internal void RegisterGraphics(Drawing.PdfGraphics graphics) => _openGraphics.Add(graphics);

    internal void UnregisterGraphics(Drawing.PdfGraphics graphics) => _openGraphics.Remove(graphics);

    /// <summary>The image XObject for <paramref name="image"/> in this document, written on first use.</summary>
    internal PdfReference GetImageReference(Drawing.PdfImage image)
    {
        if (!_images.TryGetValue(image, out PdfReference? reference))
        {
            reference = image.AddTo(this);
            _images[image] = reference;
        }

        return reference;
    }

    /// <summary>Writes the widths, encodings and subsets of the fonts used so far.</summary>
    internal void FinishFonts()
    {
        foreach (Drawing.PdfGraphics g in _openGraphics.ToArray()) g.Dispose();
        foreach (Fonts.FontResource font in _fonts.Values) font.Finish();
    }

    /// <summary>The document's resource for <paramref name="font"/>, created on first use.</summary>
    internal Fonts.FontResource GetFontResource(Fonts.PdfFont font)
    {
        if (!_fonts.TryGetValue(font, out Fonts.FontResource? resource))
        {
            resource = font.CreateResource(this);
            _fonts[font] = resource;
        }

        return resource;
    }

    internal void RegisterPage(int number) => _everPages.Add(number);

    internal bool IsPage(int number) => _everPages.Contains(number);

    internal bool IsPageTreeNode(int number) => _pageTreeNodes.Contains(number);

    /// <summary>
    /// Follows references to the object they point to. Returns <paramref name="obj"/> itself when
    /// it is not a reference, and <see cref="PdfNull.Instance"/> for a reference to a missing object.
    /// </summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        ThrowIfDisposed();
        for (int guard = 0; obj is PdfReference r && guard < 32; guard++)
        {
            obj = Objects.TryGetValue(r.Number, out PdfObject? target) ? target : PdfNull.Instance;
        }

        return obj is PdfReference ? PdfNull.Instance : obj;
    }

    internal PdfDictionary? ResolveDictionary(PdfObject? obj) => Resolve(obj) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    /// <summary>Drops the importer for <paramref name="source"/>, so this document no longer keeps it reachable.</summary>
    internal void ForgetImporter(PdfDocument source) => _importers.Remove(source);

    internal PageImporter ImporterFor(PdfDocument source)
    {
        if (!_importers.TryGetValue(source, out PageImporter? importer))
        {
            importer = new PageImporter(source, this);
            _importers[source] = importer;
        }

        return importer;
    }

    private static readonly string[] InheritableKeys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    /// <summary>
    /// Flattens the page tree. Inheritable attributes are copied onto each page, so pages stay
    /// correct when the tree is rebuilt or the page is moved to another document.
    /// </summary>
    private void LoadPageTree()
    {
        if (Catalog["Pages"] is not PdfReference rootRef) return;
        var seenNodes = new HashSet<int>();
        var stack = new Stack<(int Number, Dictionary<string, PdfObject> Inherited, int Depth)>();
        stack.Push((rootRef.Number, new Dictionary<string, PdfObject>(), 0));
        var ordered = new List<(int Number, Dictionary<string, PdfObject> Inherited)>();
        int looped = 0, tooDeep = 0;

        // Depth first, children in order: push in reverse.
        while (stack.Count > 0)
        {
            (int number, Dictionary<string, PdfObject> inherited, int depth) = stack.Pop();
            if (Resolve(new PdfReference(number, 0)) is not PdfDictionary node) continue;
            bool isPage = node.GetName("Type") == "Page" || (node.GetName("Type") != "Pages" && node["Kids"] is null);
            if (isPage)
            {
                ordered.Add((number, inherited));
                continue;
            }

            if (!seenNodes.Add(number))
            {
                looped++;
                continue;
            }

            if (depth > MaxPageTreeDepth)
            {
                tooDeep++;
                continue;
            }

            _pageTreeNodes.Add(number);
            var next = new Dictionary<string, PdfObject>(inherited);
            foreach (string key in InheritableKeys)
            {
                if (node[key] is { } value) next[key] = value;
            }

            if (Resolve(node["Kids"]) is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (kids[i] is PdfReference kid) stack.Push((kid.Number, next, depth + 1));
                }
            }
        }

        var used = new HashSet<int>();
        foreach ((int number, Dictionary<string, PdfObject> inherited) in ordered)
        {
            var dict = (PdfDictionary)Resolve(new PdfReference(number, 0))!;
            int pageNumber = number;
            if (!used.Add(number))
            {
                // The same page object listed twice: give the second listing its own object. A
                // file can list one page tens of thousands of times, so each copy is charged.
                Budget.ChargeDecodedBytes(CloneCost(dict));
                dict = Clone(dict);
                pageNumber = Add(dict);
                _pageTreeMatchesFile = false;
            }

            foreach ((string key, PdfObject value) in inherited)
            {
                if (dict[key] is not null) continue;
                dict.Set(key, value);
                if (!_inherited.TryGetValue(pageNumber, out List<(string Key, PdfObject Value)>? keys))
                {
                    keys = [];
                    _inherited[pageNumber] = keys;
                }

                keys.Add((key, value));
            }

            Pages.Attach(new PdfPage(this, pageNumber, dict));
        }

        ReportPageTree(rootRef, looped, tooDeep);
    }

    /// <summary>Tells the diagnostics about branches of the page tree that were not read, and a /Count that disagrees.</summary>
    private void ReportPageTree(PdfReference root, int looped, int tooDeep)
    {
        PdfDiagnostics diagnostics = Budget.Diagnostics;
        if (looped > 0) diagnostics.Skipped(Compat.Format("The page tree lists {0} of its nodes more than once (a loop or a shared branch); they were read once.", looped));
        if (tooDeep > 0)
        {
            diagnostics.Skipped(Compat.Format("The page tree nests deeper than {0} levels; {1} branch(es) below that were not read, with their pages.", MaxPageTreeDepth, tooDeep));
        }

        if (Resolve(Resolve(root) is PdfDictionary node ? node["Count"] : null) is PdfNumber { IsInteger: true } count && count.IntegerValue != Pages.Count)
        {
            diagnostics.Repaired(Compat.Format("The page tree says it has {0} page(s) but {1} were found; the pages found are used.", count.IntegerValue, Pages.Count));
        }
    }

    /// <summary>What a copy of <paramref name="d"/> holds, roughly: the dictionary and an entry for each key.</summary>
    private static long CloneCost(PdfDictionary d) => 64L * (d.Count + 1);

    internal static PdfDictionary Clone(PdfDictionary d)
    {
        var copy = new PdfDictionary(d.Count);
        for (int i = 0; i < d.Count; i++)
        {
            (string key, PdfObject value) = d.EntryAt(i);
            copy.Set(key, value);
        }

        return copy;
    }
}
