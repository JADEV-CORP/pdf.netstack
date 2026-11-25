using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Content;

/// <summary>
/// Walks a page's content with its graphics state (ISO 32000-1, 8.4): the current transformation
/// matrix through q, Q and cm, form XObjects with their own matrix and resources, and inline
/// images. Subclasses see images, text operators with the text state they apply under, and
/// marked content sequences.
/// </summary>
/// <remarks>
/// Content comes from untrusted files, so the walk is bounded: forms nest at most
/// <see cref="MaxFormDepth"/> deep, a form never draws itself, and every page gets a budget of
/// decoded bytes, tokens and operators shared by everything it draws. Operators also count
/// against the document's <see cref="PdfLimits.MaxOperationsPerDocument"/>, and a run ends at
/// <see cref="PdfLimits.Timeout"/>. A budget that runs out ends the page's content there and is
/// reported to <see cref="PdfDocument.Diagnostics"/>.
/// </remarks>
internal abstract class ContentInterpreter
{
    public const int MaxFormDepth = 16;
    public const int MaxStateDepth = 256;
    public const long MaxOperations = 10_000_000;

    /// <summary>
    /// Tokens a page may read, re-reads of damaged operands included: a few per operator, so
    /// ordinary pages stay far below it while a page of damaged operands stays linear.
    /// </summary>
    public const long MaxTokens = 50_000_000;

    /// <summary>
    /// Bytes a page may parse. A form drawn again is parsed again, so a small form holding one
    /// huge operand would otherwise cost far more than its operators or tokens. Counted apart
    /// from the bytes decoded, which the decode ceiling bounds: one page read once counts once
    /// against each.
    /// </summary>
    public const long MaxParsedBytes = 1L << 30;

    // Operators are charged to the document's budget in batches, and the deadline read as often.
    private const int Batch = 1024;

    // Operators read between two looks at the cancellation token: often enough to stop within a
    // millisecond or so, rarely enough that the check never shows next to parsing.
    private const int CancellationInterval = 256;

    private readonly Stack<(PdfMatrix Ctm, TextState Text, PdfMatrix TextMatrix, PdfMatrix LineMatrix)> _saved = new();
    private readonly HashSet<PdfStream> _activeForms = [];

    // Decoded once per form however often it is drawn; null when it cannot be decoded.
    private readonly Dictionary<PdfStream, byte[]?> _forms = [];

    // The one string of Tj, ' and ", passed on without a list for each.
    private readonly List<PdfObject> _single = [];

    private readonly ContentBudget _tokens = new(MaxTokens);

    // The text line matrix Tlm: the text matrix at the start of the current line.
    private PdfMatrix _lineMatrix = PdfMatrix.Identity;
    private long _operations;

    // Operators taken from the document's budget so far: ahead of those run, the rest refunded.
    private long _reserved;
    private bool _stopped;
    private bool _decodeLimitReported;
    private long _bytes;
    private long _parsed;
    private int _marked;
    private int _markedOverflow;
    private Deadline _deadline;
    private PdfPage? _page;

    protected ContentInterpreter(PdfDocument document) => Document = document;

    protected PdfDocument Document { get; }

    /// <summary>The current transformation matrix: user space to the page's default user space.</summary>
    protected PdfMatrix Ctm { get; private set; } = PdfMatrix.Identity;

    /// <summary>The text state (ISO 32000-1, 9.3), current while text is shown.</summary>
    protected TextState Text { get; private set; } = new();

    /// <summary>
    /// The text matrix Tm (ISO 32000-1, 9.4.2), saved and restored with the graphics state. Kept
    /// apart from <see cref="Text"/>, which every glyph would otherwise copy.
    /// </summary>
    protected PdfMatrix TextMatrix { get; private set; } = PdfMatrix.Identity;

    /// <summary>
    /// Stops the walk with <see cref="OperationCanceledException"/> once cancelled: looked at
    /// before each stream is decoded and every few hundred operators.
    /// </summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Walks a page's content streams, in order, with the page's resources.</summary>
    /// <exception cref="PdfLimitExceededException">The walk ran past <see cref="PdfLimits.Timeout"/>.</exception>
    public void Run(PdfPage page)
    {
        Compat.ThrowIfNull(page, nameof(page));
        _page = page;
        _deadline = Deadline.Start(Document.Limits);
        PdfDictionary? resources = Document.Resolve(page.Dictionary["Resources"]) as PdfDictionary;
        PdfObject? contents = Document.Resolve(page.Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents switch
        {
            PdfArray array => array,
            PdfStream stream => [stream],
            _ => [],
        };

        try
        {
            if (Joined(parts) is { } content) Execute(content, resources, 0);
            _deadline.ThrowIfPassed(Document.Budget);
        }
        finally
        {
            Document.Budget.RefundOperations(_reserved - _operations);
            _reserved = _operations;
        }
    }

    /// <summary>
    /// Stops the call with <see cref="OperationCanceledException"/> or, past the time limit, a
    /// <see cref="PdfLimitExceededException"/>. For loops inside one operator, which the walk's own checks do not reach.
    /// </summary>
    protected void CheckLimits()
    {
        Cancellation.ThrowIfCancellationRequested();
        _deadline.ThrowIfPassed(Document.Budget);
    }

    /// <summary>
    /// Reports a bound that ended the page's content early, with the page, so each page that
    /// reaches one is named.
    /// </summary>
    protected void ReportPageLimit(PdfLimitKind limit, string message) =>
        Document.Budget.Diagnostics.LimitReached(limit, message, _page?.Index);

    /// <summary>
    /// The page's content: split across streams it is one stream (ISO 32000-1, 7.8.2), so the
    /// parts are joined, in one array of the final size; a single part is used as decoded.
    /// </summary>
    private byte[]? Joined(IEnumerable<PdfObject> parts)
    {
        List<byte[]> pieces = [];
        long total = 0;
        foreach (PdfObject part in parts)
        {
            if (Document.Resolve(part) is not PdfStream stream) continue;
            if (Data(stream, (part as PdfReference)?.Number) is not { } data) continue;
            if (total + data.Length + 1 > Compat.MaxArrayLength)
            {
                ReportPageLimit(PdfLimitKind.PageContent, "The page's content is longer than one array holds; the rest is not read.");
                break;
            }

            pieces.Add(data);
            total += data.Length + 1;
        }

        if (pieces.Count <= 1) return pieces.Count == 0 ? null : pieces[0];
        var joined = new byte[total];
        int at = 0;
        foreach (byte[] piece in pieces)
        {
            Buffer.BlockCopy(piece, 0, joined, at, piece.Length);
            at += piece.Length;
            joined[at++] = (byte)'\n';
        }

        return joined;
    }

    /// <summary>An image XObject was drawn; the unit square maps to the page through <see cref="Ctm"/>.</summary>
    protected virtual void OnImage(PdfStream image, PdfDictionary? resources)
    {
    }

    /// <summary>An inline image was drawn.</summary>
    protected virtual void OnInlineImage(PdfDictionary image, ReadOnlyMemory<byte> data, PdfDictionary? resources)
    {
    }

    /// <summary>
    /// A text showing operator: Tj, ', " (one string) or TJ (strings and numbers). The text
    /// state is already updated for ' and ".
    /// </summary>
    protected virtual void OnShowText(List<PdfObject> items, PdfDictionary? resources)
    {
    }

    /// <summary>
    /// BMC or BDC (ISO 32000-1, 14.6): a marked content sequence begins. <paramref name="properties"/>
    /// is the BDC property list, inline or from the resources' /Properties, or null.
    /// </summary>
    protected virtual void OnBeginMarkedContent(string tag, PdfDictionary? properties)
    {
    }

    /// <summary>EMC: the innermost sequence ends. A sequence its stream leaves open ends with the stream.</summary>
    protected virtual void OnEndMarkedContent()
    {
    }

    /// <summary>
    /// A stream's decoded data, or null when it cannot be decoded. What the page has left of its
    /// byte budget is the ceiling given to the decoder, so a page over it stops before allocating.
    /// </summary>
    /// <param name="number">The stream's object number, for the diagnostics, when known.</param>
    private byte[]? Data(PdfStream stream, int? number)
    {
        long left = Document.DecodeLimit - _bytes;
        if (_stopped) return null;
        if (left <= 0)
        {
            ReportDecodeLimit("The page's content is longer than its decoded bytes allow; the rest is not read.");
            return null;
        }

        CheckLimits();
        try
        {
            byte[] data = Document.GetStreamData(stream, left);
            _bytes += data.Length;
            return data;
        }
        catch (PdfLimitExceededException e) when (e.Limit == PdfLimitKind.PageContent)
        {
            _bytes = Document.DecodeLimit;
            ReportDecodeLimit(e.Message);
            return null;
        }
        catch (PdfLimitExceededException)
        {
            // A document budget, reported where it ran out; what needs it draws nothing.
            return null;
        }
        catch (PdfFormatException e)
        {
            // A damaged stream draws nothing, as in viewers; the rest of the page still counts.
            Document.Budget.Diagnostics.Skipped("A content stream could not be decoded and draws nothing: " + e.Message, number, _page?.Index);
            return null;
        }
    }

    private void ReportDecodeLimit(string message)
    {
        if (_decodeLimitReported) return;
        _decodeLimitReported = true;
        ReportPageLimit(PdfLimitKind.PageContent, message);
    }

    private void Execute(byte[] content, PdfDictionary? resources, int depth)
    {
        if ((_parsed += content.Length) > MaxParsedBytes && !_stopped)
        {
            _stopped = true;
            ReportPageLimit(PdfLimitKind.PageContent, Compat.Format("The page's content and the forms it draws come to more than {0} bytes to read; the rest is not read.", MaxParsedBytes));
        }

        if (_stopped) return;
        var reader = new ContentReader(content, _tokens);
        var op = new ContentOperation();
        int baseDepth = _saved.Count;
        int markedDepth = _marked;
        while (!_stopped && reader.Next(op))
        {
            if (!Count()) break;
            if (_operations % CancellationInterval == 0) Cancellation.ThrowIfCancellationRequested();
            List<PdfObject> o = op.Operands;
            switch (op.Operator)
            {
                case "q":
                    if (_saved.Count < MaxStateDepth) _saved.Push((Ctm, Text, TextMatrix, _lineMatrix));
                    break;
                case "Q":
                    // A stream cannot restore state saved outside it (ISO 32000-1, 7.8.2).
                    if (_saved.Count > baseDepth) (Ctm, Text, TextMatrix, _lineMatrix) = _saved.Pop();
                    break;
                case "cm" when Numbers(o, 6) is { } m:
                    Ctm = PdfMatrix.Multiply(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), Ctm);
                    break;
                case "Do" when o.Count > 0 && o[^1] is PdfName name:
                    Draw(name.Value, resources, depth);
                    break;
                case "BI" when op.InlineImage is not null:
                    OnInlineImage(op.InlineImage, op.InlineData, resources);
                    break;
                case "BMC" when o.Count > 0 && o[^1] is PdfName tag:
                    BeginMarked(tag.Value, null);
                    break;
                case "BDC" when o.Count >= 2 && o[^2] is PdfName tag:
                    BeginMarked(tag.Value, o[^1] switch
                    {
                        PdfDictionary inline => inline,
                        PdfName name when Document.Resolve(resources?["Properties"]) is PdfDictionary properties => Document.Resolve(properties[name.Value]) as PdfDictionary,
                        _ => null,
                    });
                    break;
                case "EMC":
                    // An EMC without its BMC or BDC in the same stream is ignored (ISO 32000-1, 14.6).
                    if (_markedOverflow > 0) _markedOverflow--;
                    else if (_marked > markedDepth) EndMarked();
                    break;
                default:
                    TextOperator(op.Operator, o, resources);
                    break;
            }
        }

        if (reader.BudgetExhausted && !_stopped)
        {
            _stopped = true;
            ReportPageLimit(PdfLimitKind.PageContent, Compat.Format("The page's content takes more than {0} tokens to read; the rest is not read.", MaxTokens));
        }

        // Unbalanced q and marked content inside a form or page are closed at its end.
        while (_saved.Count > baseDepth) (Ctm, Text, TextMatrix, _lineMatrix) = _saved.Pop();
        while (_marked > markedDepth) EndMarked();
        if (depth == 0) _markedOverflow = 0;
    }

    /// <summary>
    /// Counts one operator against the page's and the document's budgets, and reads the clock
    /// every batch. False when a budget has run out, which ends the page's content.
    /// </summary>
    private bool Count()
    {
        if (_operations >= MaxOperations)
        {
            _stopped = true;
            ReportPageLimit(PdfLimitKind.PageContent, Compat.Format("The page runs more than {0} content operators; the rest is not read.", MaxOperations));
            return false;
        }

        // Taken before the operator runs, so a spent budget stops the next page at once.
        if (_operations >= _reserved)
        {
            _deadline.ThrowIfPassed(Document.Budget);
            long taken = Document.Budget.ReserveOperations(Batch);
            if (taken == 0)
            {
                _stopped = true;
                return false;
            }

            _reserved += taken;
        }

        _operations++;
        return true;
    }

    private void BeginMarked(string tag, PdfDictionary? properties)
    {
        // Sequences nest as deep as saved states; deeper ones are counted, not reported.
        if (_marked >= MaxStateDepth || _markedOverflow > 0)
        {
            _markedOverflow++;
            return;
        }

        _marked++;
        OnBeginMarkedContent(tag, properties);
    }

    private void EndMarked()
    {
        _marked--;
        OnEndMarkedContent();
    }

    private void Draw(string name, PdfDictionary? resources, int depth)
    {
        if (Document.Resolve(resources?["XObject"]) is not PdfDictionary xobjects || Document.Resolve(xobjects[name]) is not PdfStream xobject) return;
        switch (xobject.Dictionary.GetName("Subtype"))
        {
            case "Image":
                OnImage(xobject, resources);
                break;
            case "Form" when depth < MaxFormDepth && _activeForms.Add(xobject):
                try
                {
                    PdfMatrix saved = Ctm;
                    TextState text = Text;
                    PdfMatrix textMatrix = TextMatrix, lineMatrix = _lineMatrix;
                    if (Numbers(Document.Resolve(xobject.Dictionary["Matrix"]) as PdfArray, 6) is { } m)
                    {
                        Ctm = PdfMatrix.Multiply(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), Ctm);
                    }

                    // A form without resources uses its parent's (ISO 32000-1, 7.8.3, for old files).
                    PdfDictionary? own = Document.Resolve(xobject.Dictionary["Resources"]) as PdfDictionary;
                    if (!_forms.TryGetValue(xobject, out byte[]? data)) _forms[xobject] = data = Data(xobject, (xobjects[name] as PdfReference)?.Number);
                    if (data is not null) Execute(data, own ?? resources, depth + 1);
                    Ctm = saved;
                    Text = text;
                    TextMatrix = textMatrix;
                    _lineMatrix = lineMatrix;
                }
                finally
                {
                    _activeForms.Remove(xobject);
                }

                break;
        }
    }

    /// <summary>Text state and positioning operators (ISO 32000-1, 9.3 and 9.4).</summary>
    private void TextOperator(string op, List<PdfObject> o, PdfDictionary? resources)
    {
        switch (op)
        {
            case "BT":
                TextMatrix = _lineMatrix = PdfMatrix.Identity;
                break;
            case "Tc" when Number(o, 1) is double tc:
                Text = Text with { CharacterSpacing = tc };
                break;
            case "Tw" when Number(o, 1) is double tw:
                Text = Text with { WordSpacing = tw };
                break;
            case "Tz" when Number(o, 1) is double tz:
                Text = Text with { HorizontalScaling = tz / 100 };
                break;
            case "TL" when Number(o, 1) is double tl:
                Text = Text with { Leading = tl };
                break;
            case "Ts" when Number(o, 1) is double ts:
                Text = Text with { Rise = ts };
                break;
            case "Tr" when Number(o, 1) is double tr && tr is >= 0 and <= 7:
                Text = Text with { RenderMode = (int)tr };
                break;
            case "Tf" when o.Count >= 2 && o[^2] is PdfName font && Number(o, 1) is double size:
                // Tf takes a name then a size: the size is the last operand.
                Text = Text with { FontName = font.Value, FontSize = size };
                break;
            case "Td" when Numbers(o, 2) is { } d:
                NextLine(d[0], d[1]);
                break;
            case "TD" when Numbers(o, 2) is { } d:
                Text = Text with { Leading = -d[1] };
                NextLine(d[0], d[1]);
                break;
            case "Tm" when Numbers(o, 6) is { } m:
                var matrix = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                TextMatrix = _lineMatrix = matrix;
                break;
            case "T*":
                NextLine(0, -Text.Leading);
                break;
            case "Tj" when o.Count > 0 && o[^1] is PdfString:
                ShowOne(o[^1], resources);
                break;
            case "'" when o.Count > 0 && o[^1] is PdfString:
                NextLine(0, -Text.Leading);
                ShowOne(o[^1], resources);
                break;
            case "\"" when o.Count >= 3 && o[^1] is PdfString && Number(o, 3) is double aw && Number(o, 2) is double ac:
                Text = Text with { WordSpacing = aw, CharacterSpacing = ac };
                NextLine(0, -Text.Leading);
                ShowOne(o[^1], resources);
                break;
            case "TJ" when o.Count > 0 && o[^1] is PdfArray items:
                // The array's own list: handlers only read it.
                OnShowText(items.Items, resources);
                break;
        }
    }

    private void ShowOne(PdfObject text, PdfDictionary? resources)
    {
        _single.Clear();
        _single.Add(text);
        OnShowText(_single, resources);
    }

    private void NextLine(double tx, double ty) => TextMatrix = _lineMatrix = PdfMatrix.Multiply(PdfMatrix.CreateTranslation(tx, ty), _lineMatrix);

    /// <summary>Moves the text matrix after a glyph or adjustment, in unscaled text space units.</summary>
    protected void AdvanceText(double tx, double ty) => TextMatrix = PdfMatrix.Multiply(PdfMatrix.CreateTranslation(tx, ty), TextMatrix);

    /// <summary>
    /// The operand <paramref name="fromEnd"/> places from the end (1 is the last) as a finite
    /// number. Operators read their operands from the end, so a stray value before them is ignored.
    /// </summary>
    private static double? Number(List<PdfObject> o, int fromEnd)
    {
        if (o.Count < fromEnd || o[o.Count - fromEnd] is not PdfNumber n) return null;
        double v = n.ToDouble();
        return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    /// <summary>The last <paramref name="count"/> operands as numbers, or null when they are not.</summary>
    private static double[]? Numbers(IReadOnlyList<PdfObject>? o, int count)
    {
        if (o is null || o.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (o[o.Count - count + i] is not PdfNumber n) return null;
            double v = n.ToDouble();
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            result[i] = v;
        }

        return result;
    }
}

/// <summary>The text state parameters (ISO 32000-1, 9.3).</summary>
internal sealed record TextState
{
    public double CharacterSpacing { get; init; }

    public double WordSpacing { get; init; }

    /// <summary>Tz as a fraction: 1 is 100 percent.</summary>
    public double HorizontalScaling { get; init; } = 1;

    public double Leading { get; init; }

    public double Rise { get; init; }

    public int RenderMode { get; init; }

    public string? FontName { get; init; }

    public double FontSize { get; init; }
}
