using PdfNetStack.Content;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// What the content of a document uses: the resource names each resource dictionary is looked
/// up with, which streams are content streams and nothing else, and which codes each font shows:
/// single bytes for simple fonts, two byte codes for composite fonts with an identity encoding.
/// The optimizer runs it before it changes anything, and the verifier runs it again on its own
/// parse of the source, so every structural change is checked against an analysis of the original.
/// </summary>
/// <remarks>
/// Content is executed from the page tree and from annotation appearances, following form
/// XObjects, tiling patterns, Type 3 glyph procedures and soft mask groups. Resource names are
/// collected by scanning the decoded bytes for every name token, whatever operator it belongs
/// to and even inside strings or inline image data, so a name counts as used whenever any reader
/// could look it up. A name that content with its own resources uses but does not define also
/// counts against the resources of the content that invoked it, since some readers fall back to
/// them. Glyphs are collected by following the font through Tf, gs, q and Q. Work is
/// bounded; when a budget runs out the analysis reports itself incomplete and nothing may be
/// pruned or merged.
/// </remarks>
internal sealed class ContentAnalysis
{
    /// <summary>Resource categories whose entries are looked up by name from content (ISO 32000-1, 7.8.3).</summary>
    public static readonly string[] Categories = ["Font", "XObject", "ExtGState", "Pattern", "Shading", "ColorSpace", "Properties"];

    private const long MaxBytes = 1L << 31;
    private const long MaxOperations = 50_000_000;
    private const int MaxDepth = 32;
    private const int MaxOperands = 4096;
    private const int MaxStateDepth = 256;

    /// <summary>Bytes the programs of shared streams may hold together; past it, streams are read again on each run.</summary>
    private const long MaxProgramBytes = 64L << 20;

    private readonly PdfFile _file;
    private readonly ReferenceIndex _references;
    private readonly long _decodeLimit;
    private readonly CancellationToken _ct;

    private readonly Dictionary<PdfDictionary, HashSet<string>> _names = new();
    private readonly HashSet<PdfDictionary> _keepAll = [];
    private readonly HashSet<PdfDictionary> _holders = [];
    private readonly Dictionary<PdfDictionary, int> _indirect = new();
    private readonly HashSet<(PdfObject Holder, PdfDictionary? Resources, PdfDictionary? Font)> _executed = [];
    private readonly HashSet<(PdfObject Holder, PdfDictionary? Resources)> _reached = [];
    private readonly Queue<(PdfObject Holder, IReadOnlyList<PdfStream> Parts, PdfDictionary? Resources)> _deferred = new();
    private readonly Dictionary<PdfDictionary, HashSet<int>> _codes = new();
    private readonly Dictionary<PdfDictionary, HashSet<int>> _simpleCodes = new();
    private readonly HashSet<PdfDictionary> _unreliableFonts = [];
    private readonly Dictionary<PdfDictionary, HashSet<PdfDictionary>> _fallbacks = new();
    private readonly NameTable _nameTable = new();
    private readonly Dictionary<PdfStream, ContentProgram> _programs = new();
    private readonly HashSet<(PdfObject Holder, PdfDictionary? Resources)> _named = [];
    private readonly Operand[]?[] _operandBuffers = new Operand[]?[MaxDepth + 2];
    private readonly ContentProgram?[] _scratch = new ContentProgram?[MaxDepth + 2];
    private long _programBytes;
    private long _bytes;
    private long _operations;
    private bool _exhausted;

    private ContentAnalysis(PdfFile file, ReferenceIndex references, long decodeLimit, CancellationToken ct)
    {
        _file = file;
        _references = references;
        _decodeLimit = decodeLimit;
        _ct = ct;
    }

    /// <summary>False when a work budget ran out: no resource may then be pruned and no font merged.</summary>
    public bool Complete => !_exhausted;

    /// <summary>
    /// False when some text could not be attributed to a font, or content could not be read
    /// operator by operator. Glyph sets are then incomplete and no font may be merged.
    /// </summary>
    public bool GlyphUsageComplete { get; private set; } = true;

    public ReferenceIndex References => _references;

    /// <summary>
    /// Resource category dictionaries whose entries may be pruned, with the names content may
    /// look up in them. A category dictionary that is missing here must be kept whole.
    /// </summary>
    public Dictionary<PdfDictionary, HashSet<string>> PrunableCategories { get; } = new();

    /// <summary>Object numbers of streams that every referrer uses as a content stream.</summary>
    public HashSet<int> ContentStreams { get; } = [];

    /// <summary>Codes shown with each composite font whose encoding is Identity-H or Identity-V.</summary>
    public IReadOnlyDictionary<PdfDictionary, HashSet<int>> IdentityCodes => _codes;

    /// <summary>Single byte codes shown with each simple font (Type 1, TrueType, Type 3).</summary>
    public IReadOnlyDictionary<PdfDictionary, HashSet<int>> SimpleCodes => _simpleCodes;

    /// <summary>
    /// Fonts some of whose text could not be decoded into codes: composite fonts shown with
    /// strings of odd length, or with a CMap other than Identity-H or Identity-V.
    /// </summary>
    public IReadOnlyCollection<PdfDictionary> UnreliableFonts => _unreliableFonts;

    /// <summary>True when the font object <paramref name="fontNumber"/> is selectable only through category dictionaries this analysis saw completely.</summary>
    public bool FontFullyObserved(int fontNumber) =>
        Complete && GlyphUsageComplete &&
        _references.OnlyFrom(fontNumber, r => r.Container is PdfDictionary c && PrunableCategories.ContainsKey(c) && IsCategory(c, "Font"));

    private readonly Dictionary<PdfDictionary, string> _categoryOf = new();

    private bool IsCategory(PdfDictionary c, string category) => _categoryOf.TryGetValue(c, out string? k) && k == category;

    /// <summary>The prunable category dictionaries of one category, such as "Font".</summary>
    public IEnumerable<PdfDictionary> CategoryDictionaries(string category) => PrunableCategories.Keys.Where(c => IsCategory(c, category));

    public static ContentAnalysis Run(PdfFile file, IEnumerable<int> order, IEnumerable<string> ignoredKeys, long decodeLimit, CancellationToken ct)
    {
        List<int> reachable = order as List<int> ?? order.ToList();
        ReferenceIndex references = ReferenceIndex.Build(file, reachable, ignoredKeys, ct);
        var analysis = new ContentAnalysis(file, references, decodeLimit, ct);
        analysis.Execute();
        analysis.ClassifyContentStreams(reachable);
        return analysis;
    }

    private void Execute()
    {
        PdfDictionary? catalog = _file.ResolveDictionary(_file.Trailer["Root"]);
        try
        {
            if (catalog?["Pages"] is { } pages) WalkPageTree(pages, null, 0, []);
            while (_deferred.Count > 0 && !_exhausted)
            {
                (PdfObject holder, IReadOnlyList<PdfStream> parts, PdfDictionary? resources) = _deferred.Dequeue();
                Run(holder, parts, resources, null, 0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfFormatException or StreamDecodeException)
        {
            _exhausted = true;
        }

        if (_exhausted)
        {
            GlyphUsageComplete = false;
            return;
        }

        Summarize();
    }

    private void WalkPageTree(PdfObject node, PdfDictionary? inherited, int depth, HashSet<PdfDictionary> path)
    {
        if (_file.Resolve(node) is not PdfDictionary dict || depth > 64 || !path.Add(dict)) return;
        try
        {
            PdfObject? own = dict["Resources"];
            PdfDictionary? resources = inherited;
            if (own is not null && _file.Resolve(own) is PdfDictionary r)
            {
                resources = r;
                Remember(r, own);
                _holders.Add(dict);
            }

            bool isPage = dict.GetName("Type") == "Page" || (dict.GetName("Type") != "Pages" && dict["Kids"] is null);
            if (!isPage)
            {
                if (_file.Resolve(dict["Kids"]) is PdfArray kids)
                {
                    foreach (PdfObject kid in kids) WalkPageTree(kid, resources, depth + 1, path);
                }

                return;
            }

            var parts = new List<PdfStream>();
            bool unreadable = false;
            switch (_file.Resolve(dict["Contents"]))
            {
                case PdfStream single:
                    parts.Add(single);
                    break;
                case PdfArray array:
                    foreach (PdfObject part in array)
                    {
                        PdfObject? resolved = _file.Resolve(part);
                        if (resolved is PdfStream s) parts.Add(s);
                        else if (resolved is not PdfNull) unreadable = true;
                    }

                    break;
            }

            if (unreadable && resources is not null) _keepAll.Add(resources);
            Run(dict, parts, resources, null, 0);

            if (_file.Resolve(dict["Annots"]) is PdfArray annotations)
            {
                foreach (PdfObject a in annotations)
                {
                    if (_file.Resolve(a) is not PdfDictionary annotation) continue;
                    if (_file.Resolve(annotation["AP"]) is not PdfDictionary ap) continue;
                    foreach (string state in new[] { "N", "R", "D" })
                    {
                        switch (_file.Resolve(ap[state]))
                        {
                            case PdfStream stream:
                                Defer(stream, resources);
                                break;
                            case PdfDictionary states:
                                foreach (KeyValuePair<string, PdfObject> entry in states)
                                {
                                    if (_file.Resolve(entry.Value) is PdfStream stateStream) Defer(stateStream, resources);
                                }

                                break;
                        }
                    }
                }
            }
        }
        finally
        {
            path.Remove(dict);
        }
    }

    /// <summary>Queues a form-like stream for execution with no font selected.</summary>
    private void Defer(PdfStream form, PdfDictionary? context)
    {
        PdfDictionary? resources = OwnResources(form.Dictionary, context) ?? context;
        if (_reached.Add((form, resources))) _deferred.Enqueue((form, [form], resources));
    }

    /// <summary>
    /// A holder's own /Resources, recording the holder as one whose resources this analysis owns.
    /// Some readers look a name the holder's resources lack up in the resources of the content
    /// that used it, so <paramref name="context"/> is remembered as a fallback.
    /// </summary>
    private PdfDictionary? OwnResources(PdfDictionary holder, PdfDictionary? context)
    {
        PdfObject? value = holder["Resources"];
        if (value is null || _file.Resolve(value) is not PdfDictionary resources) return null;
        Remember(resources, value);
        _holders.Add(holder);
        if (context is not null && !ReferenceEquals(context, resources))
        {
            if (!_fallbacks.TryGetValue(resources, out HashSet<PdfDictionary>? contexts))
            {
                contexts = [];
                _fallbacks[resources] = contexts;
            }

            contexts.Add(context);
        }

        return resources;
    }

    private void Remember(PdfDictionary resources, PdfObject value)
    {
        if (value is PdfReference r) _indirect[resources] = r.Number;
    }

    /// <summary>Executes content: one stream, or a page's parts read as one.</summary>
    private void Run(PdfObject holder, IReadOnlyList<PdfStream> parts, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        if (!_executed.Add((holder, resources, font))) return;
        _reached.Add((holder, resources));
        if (depth > MaxDepth)
        {
            GlyphUsageComplete = false;
            if (resources is not null) _keepAll.Add(resources);
            return;
        }

        // A form, pattern or glyph procedure runs once for each resources and font it is used
        // with. It is decoded and read once; later runs replay what it does.
        PdfStream? shared = holder as PdfStream;
        if (shared is not null && _programs.TryGetValue(shared, out ContentProgram? cached))
        {
            if (!Recharge(cached))
            {
                if (resources is not null) _keepAll.Add(resources);
                GlyphUsageComplete = false;
                return;
            }

            bool fresh = _named.Add((holder, resources));
            if (fresh) UseNames(cached.Names, resources, copy: true);
            Replay(cached, resources, font, depth);
            if (fresh && resources is not null) FollowNames(cached.Names, resources);
            return;
        }

        List<Decoded>? decoded = Decode(parts);
        if (decoded is null)
        {
            // Content nobody can read still names resources a reader might find; keep them all.
            if (resources is not null) _keepAll.Add(resources);
            GlyphUsageComplete = false;
            return;
        }

        try
        {
            Run(holder, shared, decoded, resources, font, depth);
        }
        finally
        {
            foreach (Decoded part in decoded) part.Stream.Dispose();
        }
    }

    /// <summary>Reads decoded content for the first time, keeping its program when the stream is shared.</summary>
    private void Run(PdfObject holder, PdfStream? shared, List<Decoded> decoded, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        ReadOnlyMemory<byte> content = Join(decoded, (byte)'\n');
        var found = new HashSet<string>(StringComparer.Ordinal);
        ScanNames(content.Span, found, _nameTable);

        // Readers that join the parts without a separator can see a name run across two parts.
        if (decoded.Count > 1) ScanNames(Join(decoded, null).Span, found, _nameTable);

        // The set handed to the resources grows with what other content adds to them; a kept
        // program holds a copy of its own.
        bool keep = shared is not null && decoded[0].Charged >= 0 && _programBytes < MaxProgramBytes;
        ContentProgram program = keep
            ? new ContentProgram(new HashSet<string>(found, StringComparer.Ordinal), decoded[0].Charged, decoded[0].Stream.Data.Length)
            : Scratch(depth, found);

        // The names content uses count once per resources: a run under another font adds none.
        bool first = _named.Add((holder, resources));
        if (first) UseNames(found, resources, copy: false);
        if (Interpret(content, program, resources, font, depth) && keep && program.Recording)
        {
            _programs[shared!] = program;
            _programBytes += program.Size;
        }

        if (first && resources is not null) FollowNames(found, resources);
    }

    /// <param name="copy">Hand over a copy, so <paramref name="found"/> never grows with other content's names.</param>
    private void UseNames(HashSet<string> found, PdfDictionary? resources, bool copy)
    {
        if (resources is null) return;
        if (_names.TryGetValue(resources, out HashSet<string>? names)) names.UnionWith(found);
        else _names[resources] = copy ? new HashSet<string>(found, StringComparer.Ordinal) : found;
    }

    /// <summary>
    /// Charges the budgets a replayed program would have charged decoding its stream again, so
    /// limits are reached exactly where reading the stream anew would reach them. False when the
    /// analysis' own byte budget ran out, as decoding would then have given up.
    /// </summary>
    private bool Recharge(ContentProgram program)
    {
        if (program.Charged > 0) _file.Budget?.ChargeDecodedBytes(program.Charged);
        _bytes += program.Length;
        if (_bytes > MaxBytes) _exhausted = true;
        return !_exhausted;
    }

    /// <summary>A decoded part, held until its run ends, with what decoding it charged to the document's budget.</summary>
    private readonly record struct Decoded(DecodedStream Stream, long Charged);

    private List<Decoded>? Decode(IReadOnlyList<PdfStream> parts)
    {
        var decoded = new List<Decoded>(parts.Count);
        foreach (PdfStream part in parts)
        {
            Decoded? data = DecodeOne(part);
            if (data is null)
            {
                foreach (Decoded done in decoded) done.Stream.Dispose();
                return null;
            }

            decoded.Add(data.Value);
        }

        return decoded;
    }

    private static ReadOnlyMemory<byte> Join(List<Decoded> parts, byte? separator)
    {
        if (parts.Count == 1) return parts[0].Stream.Data;
        using var joined = new MemoryStream();
        foreach (Decoded part in parts)
        {
            joined.Write(part.Stream.Data.Span);
            if (separator is { } b) joined.WriteByte(b);
        }

        return joined.ToArray();
    }

    private Decoded? DecodeOne(PdfStream stream)
    {
        DecodedStream? decoded = null;
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, _file.Resolve);
            decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, _decodeLimit, _file.Resolve, _file.Budget);
            if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) return null;
            _bytes += decoded.Data.Length;
            if (_bytes > MaxBytes) _exhausted = true;
            if (_exhausted) return null;

            // Read in place: the content is only tokenized, and what a program keeps is copied out.
            var result = new Decoded(decoded, Charge(filters, decoded.Data.Length));
            decoded = null;
            return result;
        }
        catch (StreamDecodeException)
        {
            return null;
        }
        finally
        {
            decoded?.Dispose();
        }
    }

    /// <summary>
    /// What decoding charged to the document's budget, which settles each filter's output at the
    /// bytes it wrote: nothing without a filter, the decoded length with one and no predictor,
    /// as content streams have. -1 for anything else, whose program is then not kept.
    /// </summary>
    private long Charge(List<FilterSpec> filters, int length) => filters.Count switch
    {
        0 => 0,
        1 when StreamCodec.ReadPredictor(_file.Resolve(filters[0].Parms) as PdfDictionary, _file.Resolve).Predictor <= 1 => length,
        _ => -1,
    };

    /// <summary>Every '/' followed by regular characters is a name some tokenization may produce.</summary>
    internal static void ScanNames(ReadOnlySpan<byte> s, HashSet<string> names) => ScanNames(s, names, null);

    private static void ScanNames(ReadOnlySpan<byte> s, HashSet<string> names, NameTable? table)
    {
        int i = 0;
        while (true)
        {
            int slash = s[i..].IndexOf((byte)'/');
            if (slash < 0) return;
            int start = i + slash + 1;
            int end = start;
            while (end < s.Length && PdfSyntax.IsRegular(s[end])) end++;
            names.Add(table is null ? PdfParser.DecodeName(s[start..end]) : table.Get(s[start..end]));
            i = end;
            if (i >= s.Length) return;
        }
    }

    /// <summary>
    /// Holders named anywhere in the content, whatever the operator: every one of them is
    /// executed, so the resources they own or share are seen in full.
    /// </summary>
    private void FollowNames(HashSet<string> names, PdfDictionary resources)
    {
        PdfDictionary? xobjects = _file.Resolve(resources["XObject"]) as PdfDictionary;
        PdfDictionary? patterns = _file.Resolve(resources["Pattern"]) as PdfDictionary;
        PdfDictionary? fonts = _file.Resolve(resources["Font"]) as PdfDictionary;
        PdfDictionary? states = _file.Resolve(resources["ExtGState"]) as PdfDictionary;
        foreach (string name in names)
        {
            if (xobjects?[name] is { } x && _file.Resolve(x) is PdfStream form && IsForm(form.Dictionary)) Defer(form, resources);
            if (patterns?[name] is { } p && _file.Resolve(p) is PdfStream tiling) Defer(tiling, resources);
            if (fonts?[name] is { } f && _file.Resolve(f) is PdfDictionary font) DeferGlyphProcedures(font, resources);
            if (states?[name] is { } g && _file.Resolve(g) is PdfDictionary state) DeferGraphicsState(state, resources);
        }
    }

    private void DeferGlyphProcedures(PdfDictionary font, PdfDictionary context)
    {
        if (font.GetName("Subtype") != "Type3" || _file.Resolve(font["CharProcs"]) is not PdfDictionary procs) return;
        PdfDictionary resources = OwnResources(font, context) ?? context;
        foreach (KeyValuePair<string, PdfObject> entry in procs)
        {
            if (_file.Resolve(entry.Value) is PdfStream proc && _reached.Add((proc, resources))) _deferred.Enqueue((proc, [proc], resources));
        }
    }

    private void DeferGraphicsState(PdfDictionary state, PdfDictionary context)
    {
        if (_file.Resolve(state["SMask"]) is PdfDictionary mask && _file.Resolve(mask["G"]) is PdfStream group) Defer(group, context);
        if (_file.Resolve(state["Font"]) is PdfArray { Count: > 0 } selection && _file.Resolve(selection[0]) is PdfDictionary font)
        {
            DeferGlyphProcedures(font, context);
        }
    }

    private static bool IsForm(PdfDictionary d) => d.GetName("Subtype") is "Form" or null;

    /// <summary>
    /// Follows the font through the content and records the codes shown with it. Form XObjects
    /// run from Do inherit the current font; anything the walk cannot read for certain makes the
    /// glyph usage incomplete. What the content does is written to <paramref name="program"/>
    /// as it is applied, so a stream used again elsewhere can be replayed without reading it.
    /// </summary>
    /// <returns>False when the run ended for a reason other than its content, so its program is partial.</returns>
    private bool Interpret(ReadOnlyMemory<byte> content, ContentProgram program, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        var parser = new PdfParser(content);
        var state = new RunState(program, resources, font, depth);
        Operand[] operands = OperandBuffer(depth);
        int count = 0;
        int pending = 0;
        bool applying = false;
        try
        {
            while (true)
            {
                int start = parser.Position;
                Token t = parser.NextToken();
                if (t.Kind == TokenKind.EndOfInput)
                {
                    Emit(new Effect(Step.End, pending));
                    return !_exhausted;
                }

                if (t.Kind != TokenKind.Keyword || IsLiteral(parser.Span[t.Start..t.End]))
                {
                    parser.Position = start;

                    // Operands are kept as spans of the content and read only for the operators
                    // below that need them. Half go at once past the limit: dropping one at a
                    // time moves the whole list per operand.
                    if (count >= MaxOperands)
                    {
                        Array.Copy(operands, MaxOperands / 2, operands, 0, count - (MaxOperands / 2));
                        count -= MaxOperands / 2;
                    }

                    TokenKind kind = parser.SkipObject();
                    operands[count++] = new Operand(kind, t.Start, parser.Position);
                    continue;
                }

                pending++;
                bool go = true;
                switch (Operator(parser.Span[t.Start..t.End]))
                {
                    case "q":
                        go = Emit(new Effect(Step.Save, pending));
                        break;
                    case "Q":
                        go = Emit(new Effect(Step.Restore, pending));
                        break;
                    case "Tf":
                        go = count >= 2 && operands[count - 2].Kind == TokenKind.Name
                            ? Emit(new Effect(Step.SelectFont, pending, NameOf(parser, operands[count - 2])))
                            : Emit(new Effect(Step.Stop, pending));
                        break;
                    case "gs" when count > 0 && operands[count - 1].Kind == TokenKind.Name:
                        go = Emit(new Effect(Step.SelectState, pending, NameOf(parser, operands[count - 1])));
                        break;
                    case "Tj" or "'" or "\"" or "TJ":
                    {
                        int first = program.StringCount;
                        int resume = parser.Position;
                        for (int i = 0; i < count; i++) CollectStrings(parser, operands[i], program);
                        parser.Position = resume;

                        // Showing nothing does nothing: its operators wait for the next effect.
                        if (program.StringCount > first) go = Emit(new Effect(Step.Show, pending, null, first, program.StringCount - first));
                        break;
                    }

                    case "Do" when count > 0 && operands[count - 1].Kind == TokenKind.Name:
                        go = Emit(new Effect(Step.Draw, pending, NameOf(parser, operands[count - 1])));
                        break;
                    case "BI":
                        if (!SkipInlineImage(parser)) go = Emit(new Effect(Step.Stop, pending));
                        break;
                    case ")" or "{" or "}":
                        // Stray delimiters: readers recover in different ways.
                        go = Emit(new Effect(Step.Stop, pending));
                        break;
                }

                if (!go) return !_exhausted;
                count = 0;
            }
        }
        catch (PdfFormatException)
        {
            if (applying)
            {
                // Failed in content this one drew, which another context may draw differently.
                program.Recording = false;
                GlyphUsageComplete = false;
                return !_exhausted;
            }

            Emit(new Effect(Step.Stop, pending));
            return !_exhausted;
        }

        // Records the effect, then applies it. The operators read since the last effect are
        // charged first, as reading them charged them one by one.
        bool Emit(Effect effect)
        {
            pending = 0;
            program.Add(effect);
            applying = true;
            bool go = Apply(effect, ref state);
            applying = false;
            if (!program.Recording) program.ClearStrings();
            return go;
        }
    }

    /// <summary>Runs a program read before, in a new context: the same steps, budgets and failures as reading its stream again.</summary>
    private void Replay(ContentProgram program, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        var state = new RunState(program, resources, font, depth);
        try
        {
            foreach (Effect effect in program.Effects)
            {
                if (!Apply(effect, ref state)) return;
            }
        }
        catch (PdfFormatException)
        {
            GlyphUsageComplete = false;
        }
    }

    /// <summary>Applies one effect in the run's context; false when the run must stop.</summary>
    private bool Apply(in Effect effect, ref RunState state)
    {
        for (int i = 0; i < effect.Operators; i++)
        {
            // The run's operators also count against the document's budget, in batches.
            if (++_operations > MaxOperations || (_operations % 1024 == 0 && _file.Budget?.TryChargeOperations(1024) == false))
            {
                _exhausted = true;
                return false;
            }
        }

        switch (effect.Step)
        {
            case Step.Save:
                if (state.Saved.Count >= MaxStateDepth)
                {
                    GlyphUsageComplete = false;
                    return false;
                }

                state.Saved.Push(state.Font);
                return true;
            case Step.Restore:
                if (state.Saved.Count > 0) state.Font = state.Saved.Pop();
                return true;
            case Step.SelectFont:
                state.Font = Lookup(state.Resources, "Font", effect.Name!) as PdfDictionary ?? Missing;
                return true;
            case Step.SelectState:
                if (Lookup(state.Resources, "ExtGState", effect.Name!) is PdfDictionary graphicsState &&
                    _file.Resolve(graphicsState["Font"]) is PdfArray { Count: > 0 } selection)
                {
                    state.Font = _file.Resolve(selection[0]) as PdfDictionary ?? Missing;
                }

                return true;
            case Step.Show:
                for (int i = 0; i < effect.Count; i++) Show(state.Font, state.Program.String(effect.First + i));
                return true;
            case Step.Draw:
                if (Lookup(state.Resources, "XObject", effect.Name!) is PdfStream form && IsForm(form.Dictionary) && form.Dictionary.GetName("Subtype") == "Form")
                {
                    Run(form, [form], OwnResources(form.Dictionary, state.Resources) ?? state.Resources, state.Font, state.Depth + 1);
                }

                return true;
            case Step.Stop:
                GlyphUsageComplete = false;
                return false;
            default:
                return false;
        }
    }

    private static readonly PdfDictionary Missing = new();

    /// <summary>The operators <see cref="Interpret"/> acts on, without a string per operator read; empty for any other.</summary>
    private static string Operator(ReadOnlySpan<byte> k) => k.Length switch
    {
        1 => k[0] switch
        {
            (byte)'q' => "q",
            (byte)'Q' => "Q",
            (byte)'\'' => "'",
            (byte)'"' => "\"",
            (byte)')' => ")",
            (byte)'{' => "{",
            (byte)'}' => "}",
            _ => "",
        },
        2 => (k[0], k[1]) switch
        {
            ((byte)'T', (byte)'f') => "Tf",
            ((byte)'g', (byte)'s') => "gs",
            ((byte)'T', (byte)'j') => "Tj",
            ((byte)'T', (byte)'J') => "TJ",
            ((byte)'D', (byte)'o') => "Do",
            ((byte)'B', (byte)'I') => "BI",
            _ => "",
        },
        _ => "",
    };

    private static bool IsLiteral(ReadOnlySpan<byte> k) => k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8) || k.SequenceEqual("null"u8);

    private PdfObject? Lookup(PdfDictionary? resources, string category, string name) =>
        resources is not null && _file.Resolve(resources[category]) is PdfDictionary c ? _file.Resolve(c[name]) : null;

    private string NameOf(PdfParser parser, Operand name) => _nameTable.Get(parser.Span[(name.Start + 1)..name.End]);

    /// <summary>
    /// Adds the non-empty strings an operand shows to the program: the operand itself, or the
    /// strings anywhere in an array, in order. Numbers, names and dictionaries show nothing.
    /// </summary>
    private static void CollectStrings(PdfParser parser, Operand operand, ContentProgram program)
    {
        switch (operand.Kind)
        {
            case TokenKind.LiteralString or TokenKind.HexString:
                program.AddString(parser.Span[operand.Start..operand.End], operand.Kind);
                break;
            case TokenKind.ArrayStart:
                parser.Position = operand.Start + 1;
                CollectArray(parser, program);
                break;
        }
    }

    /// <summary>Strings of an array the operand read already proved well formed, from after its '['.</summary>
    private static void CollectArray(PdfParser parser, ContentProgram program)
    {
        while (true)
        {
            int at = parser.Position;
            Token t = parser.NextToken();
            switch (t.Kind)
            {
                case TokenKind.ArrayEnd or TokenKind.EndOfInput:
                    return;
                case TokenKind.LiteralString or TokenKind.HexString:
                    program.AddString(parser.Span[t.Start..t.End], t.Kind);
                    break;
                case TokenKind.ArrayStart:
                    CollectArray(parser, program);
                    break;
                case TokenKind.DictStart:
                    // A dictionary in a text array shows nothing, whatever strings it holds.
                    parser.Position = at;
                    parser.SkipObject();
                    break;
            }
        }
    }

    private void Show(PdfDictionary? font, ReadOnlySpan<byte> s)
    {
        if (font is null)
        {
            GlyphUsageComplete = false;
            return;
        }

        if (font.GetName("Subtype") != "Type0")
        {
            if (!_simpleCodes.TryGetValue(font, out HashSet<int>? bytes))
            {
                bytes = [];
                _simpleCodes[font] = bytes;
            }

            foreach (byte b in s) bytes.Add(b);
            return;
        }

        // Codes of other CMaps need the CMap to split the string; such fonts are never subset or merged.
        if (font.GetName("Encoding") is not ("Identity-H" or "Identity-V"))
        {
            _unreliableFonts.Add(font);
            return;
        }

        if (!_codes.TryGetValue(font, out HashSet<int>? codes))
        {
            codes = [];
            _codes[font] = codes;
        }

        if (s.Length % 2 != 0) _unreliableFonts.Add(font);
        for (int i = 0; i + 1 < s.Length; i += 2) codes.Add((s[i] << 8) | s[i + 1]);
    }

    /// <summary>Operand buffers, one per level of form nesting, reused from run to run.</summary>
    private Operand[] OperandBuffer(int depth) => _operandBuffers[depth] ??= new Operand[MaxOperands];

    /// <summary>A program that is applied but not kept, for page content; one per level of nesting.</summary>
    private ContentProgram Scratch(int depth, HashSet<string> names)
    {
        ContentProgram program = _scratch[depth] ??= new ContentProgram(names, 0, 0) { Recording = false };
        program.Reset(names);
        return program;
    }

    /// <summary>An operand: the kind of its first token and where it lies in the content.</summary>
    private readonly record struct Operand(TokenKind Kind, int Start, int End);

    private enum Step : byte
    {
        Save,
        Restore,
        SelectFont,
        SelectState,
        Show,
        Draw,
        Stop,
        End,
    }

    /// <summary>
    /// One thing content does that the analysis follows. <see cref="Operators"/> counts the
    /// operators read since the previous effect, this one's own included.
    /// </summary>
    private readonly record struct Effect(Step Step, int Operators, string? Name = null, int First = 0, int Count = 0);

    /// <summary>The state of one run of a program: its context, the current font and the saved fonts.</summary>
    private struct RunState(ContentProgram program, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        public readonly ContentProgram Program = program;
        public readonly PdfDictionary? Resources = resources;
        public readonly int Depth = depth;
        public readonly Stack<PdfDictionary?> Saved = new();
        public PdfDictionary? Font = font;
    }

    /// <summary>
    /// What one content stream does, in order, independent of the resources and font it runs
    /// with: font and state selections by name, strings shown, forms drawn, saves and restores,
    /// and where reading stopped. Kept for streams run in more than one context.
    /// </summary>
    private sealed class ContentProgram(HashSet<string> names, long charged, int length)
    {
        private readonly List<int> _ends = [];
        private byte[] _bytes = new byte[256];
        private int _used;

        /// <summary>Every name the stream's bytes hold.</summary>
        public HashSet<string> Names { get; private set; } = names;

        /// <summary>What decoding the stream charged to the document's budget.</summary>
        public long Charged { get; } = charged;

        /// <summary>The stream's decoded length.</summary>
        public int Length { get; } = length;

        public List<Effect> Effects { get; } = [];

        /// <summary>False once the program is not kept: effects are applied and dropped.</summary>
        public bool Recording { get; set; } = true;

        public int StringCount => _ends.Count;

        /// <summary>Bytes the program holds, about.</summary>
        public long Size => (Effects.Count * 32L) + (_ends.Count * 4L) + _used + (Names.Count * 48L);

        public ReadOnlySpan<byte> String(int index) => _bytes.AsSpan(index == 0 ? 0 : _ends[index - 1], _ends[index] - (index == 0 ? 0 : _ends[index - 1]));

        public void Add(Effect effect)
        {
            if (!Recording) return;
            Effects.Add(effect);
            if (Size > MaxProgramBytes / 4)
            {
                // Too big to keep: what is applied stays right, nothing more is recorded.
                Recording = false;
                Effects.Clear();
            }
        }

        /// <summary>Decodes a string token and keeps it when it is not empty.</summary>
        public void AddString(ReadOnlySpan<byte> token, TokenKind kind)
        {
            int room = token.Length;
            if (_used + room > _bytes.Length) Array.Resize(ref _bytes, (int)Math.Min(Compat.MaxArrayLength, Math.Max((long)_bytes.Length * 2, (long)_used + room)));
            Span<byte> target = _bytes.AsSpan(_used);
            int length = kind == TokenKind.LiteralString ? PdfParser.DecodeLiteralString(token[1..], target) : PdfParser.DecodeHexString(token[1..], target);
            if (length == 0) return;
            _used += length;
            _ends.Add(_used);
        }

        public void ClearStrings()
        {
            _ends.Clear();
            _used = 0;
        }

        public void Reset(HashSet<string> names)
        {
            Names = names;
            ClearStrings();
            Effects.Clear();
        }
    }

    /// <summary>Steps over BI ... ID data EI. False when the end of the data is ambiguous.</summary>
    private static bool SkipInlineImage(PdfParser parser)
    {
        var image = new PdfDictionary();
        while (true)
        {
            Token t = parser.NextToken();
            if (t.Kind == TokenKind.EndOfInput) return false;
            if (t.Kind == TokenKind.Keyword && parser.Span[t.Start..t.End].SequenceEqual("ID"u8)) break;
            if (t.Kind != TokenKind.Name) return false;
            image.Set(PdfParser.DecodeName(parser.Span[(t.Start + 1)..t.End]), parser.ParseObject());
        }

        (_, int keywordEnd, bool unambiguous) = InlineImageBounds.Find(parser.Span, parser.Position + 1, image);
        if (!unambiguous) return false;
        parser.Position = keywordEnd;
        return true;
    }

    /// <summary>Turns the names seen per resource dictionary into prunable category dictionaries.</summary>
    private void Summarize()
    {
        PropagateFallbacks();
        var prunable = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in _names.Keys)
        {
            if (_keepAll.Contains(resources)) continue;
            if (_indirect.TryGetValue(resources, out int number) &&
                !_references.OnlyFrom(number, r => r.Key == "Resources" && r.Container is PdfDictionary h && _holders.Contains(h)))
            {
                continue;
            }

            prunable.Add(resources);
        }

        var rejected = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in prunable)
        {
            HashSet<string> names = _names[resources];
            foreach (string category in Categories)
            {
                PdfObject? value = resources[category];
                if (value is null || _file.Resolve(value) is not PdfDictionary c) continue;
                if (_categoryOf.TryGetValue(c, out string? seen) && seen != category) rejected.Add(c);
                _categoryOf[c] = category;
                if (value is PdfReference r &&
                    !_references.OnlyFrom(r.Number, x => x.Key == category && x.Container is PdfDictionary owner && prunable.Contains(owner)))
                {
                    rejected.Add(c);
                    continue;
                }

                if (!PrunableCategories.TryGetValue(c, out HashSet<string>? used))
                {
                    used = new HashSet<string>(StringComparer.Ordinal);
                    PrunableCategories[c] = used;
                }

                used.UnionWith(names);
            }
        }

        foreach (PdfDictionary c in rejected) PrunableCategories.Remove(c);
    }

    /// <summary>
    /// Names a form, pattern, glyph procedure or appearance uses but its own resources do not
    /// define count against the resources of the content that used it, to a fixpoint.
    /// </summary>
    private void PropagateFallbacks()
    {
        bool changed = true;
        for (int round = 0; changed && round < MaxDepth; round++)
        {
            changed = false;
            foreach ((PdfDictionary own, HashSet<PdfDictionary> contexts) in _fallbacks)
            {
                if (!_names.TryGetValue(own, out HashSet<string>? names)) continue;
                List<string> missing = names.Where(name => !Defines(own, name)).ToList();
                if (missing.Count == 0) continue;
                foreach (PdfDictionary context in contexts)
                {
                    // A context that ran no content of its own is never pruned; leave it alone.
                    if (!_names.TryGetValue(context, out HashSet<string>? outer)) continue;
                    foreach (string name in missing) changed |= outer.Add(name);
                }
            }
        }

        // Chains longer than the content nesting limit are not worth resolving; keep them whole.
        if (changed)
        {
            foreach (HashSet<PdfDictionary> contexts in _fallbacks.Values) _keepAll.UnionWith(contexts);
        }
    }

    private bool Defines(PdfDictionary resources, string name)
    {
        foreach (string category in Categories)
        {
            if (_file.Resolve(resources[category]) is PdfDictionary c && c[name] is not null) return true;
        }

        return false;
    }

    /// <summary>
    /// A stream is a content stream when every reference to it is in a content role: page
    /// contents, a form XObject or tiling pattern in resources, an appearance, a glyph
    /// procedure, a soft mask group or a widget icon.
    /// </summary>
    private void ClassifyContentStreams(List<int> reachable)
    {
        var xobjectCategories = new HashSet<PdfDictionary>();
        var patternCategories = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in _references.ResourceDictionaries)
        {
            if (_file.Resolve(resources["XObject"]) is PdfDictionary x) xobjectCategories.Add(x);
            if (_file.Resolve(resources["Pattern"]) is PdfDictionary p) patternCategories.Add(p);
        }

        var appearanceStates = new HashSet<PdfDictionary>();
        foreach (PdfDictionary ap in _references.AppearanceDictionaries)
        {
            foreach (string state in new[] { "N", "R", "D" })
            {
                if (_file.Resolve(ap[state]) is PdfDictionary states) appearanceStates.Add(states);
            }
        }

        foreach (int number in reachable)
        {
            if (_file.GetObject(number) is not PdfStream stream) continue;
            PdfDictionary d = stream.Dictionary;
            string? type = d.GetName("Type");
            if (type is not (null or "XObject" or "Pattern")) continue;
            bool tiling = _file.Resolve(d["PatternType"]) is PdfNumber { IsInteger: true, IntegerValue: 1 };
            bool formLike = d.GetName("Subtype") is null or "Form";
            if (!formLike) continue;

            bool content = _references.OnlyFrom(number, r => r switch
            {
                { Container: PdfDictionary c, Key: not null } when xobjectCategories.Contains(c) => !tiling && d.GetName("Subtype") == "Form",
                { Container: PdfDictionary c, Key: not null } when patternCategories.Contains(c) => tiling,
                { Container: PdfDictionary c, Key: "Contents" } => c.GetName("Type") == "Page" && !tiling,
                { Container: PdfArray a } => _references.ContentArrays.Contains(a) && !tiling,
                { Container: PdfDictionary c, Key: "N" or "R" or "D" } when _references.AppearanceDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: not null } when appearanceStates.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: not null } when _references.GlyphProcedureDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: "G" } when _references.SoftMaskDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: "I" or "RI" or "IX" } when _references.WidgetCharacteristics.Contains(c) => !tiling,
                _ => false,
            });
            if (content) ContentStreams.Add(number);
        }
    }
}
