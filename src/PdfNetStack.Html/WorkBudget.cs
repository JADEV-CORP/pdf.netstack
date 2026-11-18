using System.Globalization;

namespace PdfNetStack.Html;

/// <summary>
/// The work one conversion may do, counted where hostile input can multiply it: selector tests and
/// var() substitution while styling, boxes and lines while laying out. Past a limit the conversion
/// ends with <see cref="HtmlConversionException"/>. The counts depend only on the input, so a
/// document either always converts or never does, on every machine. The cancellation token is
/// observed at the same points, every few thousand selector tests and every few dozen boxes.
/// </summary>
/// <remarks>One conversion runs on one thread: the counters are not shared.</remarks>
internal sealed class WorkBudget
{
    // How often the limits and the token are checked: a selector test takes tens of nanoseconds
    // and a box layout microseconds, so both intervals come to well under a millisecond of work,
    // and the check itself costs nothing next to them.
    private const long StyleCheckInterval = 4096;
    private const long LayoutCheckInterval = 64;

    private readonly long _maxStyle;
    private readonly long _maxLayout;
    private long _style;
    private long _layout;
    private long _nextStyleCheck;
    private long _nextLayoutCheck;

    public WorkBudget(long maxStyle, long maxLayout, CancellationToken cancellationToken)
    {
        _maxStyle = maxStyle;
        _maxLayout = maxLayout;
        Token = cancellationToken;
        _nextStyleCheck = Math.Min(StyleCheckInterval, maxStyle);
        _nextLayoutCheck = Math.Min(LayoutCheckInterval, maxLayout);
    }

    /// <summary>A budget without limits or cancellation, for parts used on their own, as in tests.</summary>
    public static WorkBudget Unlimited() => new(long.MaxValue, long.MaxValue, CancellationToken.None);

    public CancellationToken Token { get; }

    /// <summary>Style work done so far: selector tests and values substituted.</summary>
    public long StyleWork => _style;

    /// <summary>Layout work done so far: boxes and lines laid out, measuring included.</summary>
    public long LayoutWork => _layout;

    /// <summary>Counts style work: one per compound selector tested, one per value var() copies.</summary>
    public void Style(long steps)
    {
        _style += steps;
        if (_style >= _nextStyleCheck) CheckStyle();
    }

    /// <summary>Counts layout work: one per box laid out or measured, one per line built.</summary>
    public void Layout(long steps)
    {
        _layout += steps;
        if (_layout >= _nextLayoutCheck) CheckLayout();
    }

    /// <summary>Observes the cancellation token, for phases without counted work such as painting.</summary>
    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();

    private void CheckStyle()
    {
        Token.ThrowIfCancellationRequested();
        if (_style > _maxStyle)
        {
            throw new HtmlConversionException(string.Format(
                CultureInfo.InvariantCulture,
                "Styling the document needs more than {0:N0} selector tests and substitutions (HtmlConverterOptions.MaxStyleWork). The style sheets or the markup are built to be slow to match, or the document is far larger than the limit expects.",
                _maxStyle));
        }

        _nextStyleCheck = Math.Min(_style + StyleCheckInterval, _maxStyle);
    }

    private void CheckLayout()
    {
        Token.ThrowIfCancellationRequested();
        if (_layout > _maxLayout)
        {
            throw new HtmlConversionException(string.Format(
                CultureInfo.InvariantCulture,
                "Laying out the document needs more than {0:N0} box and line layouts (HtmlConverterOptions.MaxLayoutWork). The markup is built to be laid out again and again, or the document is far larger than the limit expects.",
                _maxLayout));
        }

        _nextLayoutCheck = Math.Min(_layout + LayoutCheckInterval, _maxLayout);
    }
}
