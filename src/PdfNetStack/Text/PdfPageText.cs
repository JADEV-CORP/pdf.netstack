namespace PdfNetStack;

/// <summary>
/// The text of one page as <see cref="PdfDocument.ReadText(PdfTextOptions?, CancellationToken)"/>
/// reads it: the page's text in the chosen format, its blocks and words in reading order, and the
/// text that was found hidden.
/// </summary>
/// <remarks>
/// Instances are immutable and safe to share across threads once returned.
/// </remarks>
public sealed class PdfPageText
{
    internal PdfPageText(int pageIndex, PdfTextFormat format, string text, IReadOnlyList<PdfTextBlock> blocks, IReadOnlyList<PdfWord> words, PdfReadingOrder readingOrder, IReadOnlyList<PdfHiddenText> hiddenText, bool hasOcrText)
    {
        PageIndex = pageIndex;
        Format = format;
        Text = text;
        Blocks = blocks;
        Words = words;
        ReadingOrder = readingOrder;
        HiddenText = hiddenText;
        HasOcrText = hasOcrText;
    }

    /// <summary>Zero based position of the page in the document, as <see cref="PdfPage.Index"/>.</summary>
    public int PageIndex { get; }

    /// <summary>One based page number, for citations: <see cref="PageIndex"/> plus one.</summary>
    public int PageNumber => PageIndex + 1;

    /// <summary>The format of <see cref="Text"/>.</summary>
    public PdfTextFormat Format { get; }

    /// <summary>
    /// The page's text in reading order, in <see cref="Format"/>: the blocks' text, or their
    /// Markdown, separated by a blank line. Empty when the page has no text.
    /// </summary>
    public string Text { get; }

    /// <summary>The page's paragraphs, headings, list items and tables in reading order.</summary>
    public IReadOnlyList<PdfTextBlock> Blocks { get; }

    /// <summary>
    /// The words of <see cref="Blocks"/> in reading order, with their letters and boxes; a table's
    /// words row by row.
    /// </summary>
    public IReadOnlyList<PdfWord> Words { get; }

    /// <summary>Whether the page was read in the order of the structure tree or of the layout.</summary>
    public PdfReadingOrder ReadingOrder { get; }

    /// <summary>
    /// Runs of text a reader cannot see, with why: those the graphics state hides in content order,
    /// then oversized replacement texts. Left out of
    /// <see cref="Text"/>, <see cref="Blocks"/> and <see cref="Words"/> when
    /// <see cref="PdfTextOptions.VisibleOnly"/> is set (the default), kept otherwise. Hidden text
    /// in a document that goes to a language model is a common way to plant instructions
    /// (prompt injection), so a page with any is worth a log line.
    /// </summary>
    public IReadOnlyList<PdfHiddenText> HiddenText { get; }

    /// <summary>
    /// True when the page has invisible text over or under an image, as a scanned page made
    /// searchable by OCR does. That text is kept: it is what the image shows, as well as the OCR
    /// engine read it.
    /// </summary>
    public bool HasOcrText { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>Where the reading order of a page came from.</summary>
public enum PdfReadingOrder
{
    /// <summary>The positions of the text on the page: lines top down, columns of prose one after the other.</summary>
    Layout,

    /// <summary>The document's structure tree: the order its author tagged.</summary>
    StructureTree,
}

/// <summary>What a <see cref="PdfTextBlock"/> is.</summary>
public enum PdfTextBlockKind
{
    /// <summary>A paragraph, or any other run of text.</summary>
    Paragraph,

    /// <summary>A heading; <see cref="PdfTextBlock.Level"/> is 1 to 6.</summary>
    Heading,

    /// <summary>One item of a list, its label included; <see cref="PdfTextBlock.Level"/> is the nesting depth from 1.</summary>
    ListItem,

    /// <summary>A table; <see cref="PdfTextBlock.Rows"/> holds its cells.</summary>
    Table,
}

/// <summary>A paragraph, heading, list item or table of a page.</summary>
public sealed class PdfTextBlock
{
    internal PdfTextBlock(PdfTextBlockKind kind, int level, string text, string markdown, PdfRectangle bounds, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Kind = kind;
        Level = level;
        Text = text;
        Markdown = markdown;
        Bounds = bounds;
        Rows = rows;
    }

    /// <summary>What the block is.</summary>
    public PdfTextBlockKind Kind { get; }

    /// <summary>A heading's level, 1 to 6; a list item's nesting depth, from 1; 0 for other blocks.</summary>
    public int Level { get; }

    /// <summary>The block's plain text: lines separated by a line feed; a table's rows by a line feed and its cells by a tab.</summary>
    public string Text { get; }

    /// <summary>The block as Markdown.</summary>
    public string Markdown { get; }

    /// <summary>The smallest rectangle around the block's letters, in default user space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>A table's cells, row by row, the first row first; empty for other blocks. Rows may differ in length.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>Why a run of text counts as hidden.</summary>
public enum PdfHiddenTextReason
{
    /// <summary>Drawn with text rendering mode 3 or 7, neither filled nor stroked, and not over or under an image.</summary>
    InvisibleRenderMode,

    /// <summary>Painted with an opacity of zero (/ca or /CA in the graphics state).</summary>
    Transparent,

    /// <summary>In the colour of what lies under it: the white page, or a filled rectangle of the same colour.</summary>
    NoContrast,

    /// <summary>Outside the clipping path.</summary>
    ClippedAway,

    /// <summary>Outside the page's crop box.</summary>
    OutsidePage,

    /// <summary>Under one point tall or wide as it appears on the page.</summary>
    TooSmall,

    /// <summary>In optional content (a layer) that is off in the document's default configuration.</summary>
    OptionalContentOff,

    /// <summary>Under an opaque filled rectangle drawn after it.</summary>
    Covered,

    /// <summary>
    /// Replacement text (/ActualText) far longer than the glyphs it stands for: what the page shows
    /// is the glyphs, so their own text is read instead.
    /// </summary>
    ReplacementText,
}

/// <summary>A run of text that a reader of the page cannot see.</summary>
public sealed class PdfHiddenText
{
    internal PdfHiddenText(PdfHiddenTextReason reason, string text, PdfRectangle bounds)
    {
        Reason = reason;
        Text = text;
        Bounds = bounds;
    }

    /// <summary>Why the text cannot be seen.</summary>
    public PdfHiddenTextReason Reason { get; }

    /// <summary>The run's text, its letters laid out as <see cref="PdfPage.ExtractText()"/> lays out a page.</summary>
    public string Text { get; }

    /// <summary>The smallest rectangle around the run's letters, in default user space.</summary>
    public PdfRectangle Bounds { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Reason}: {Text}";
}
