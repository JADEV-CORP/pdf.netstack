namespace PdfNetStack;

/// <summary>
/// The base of the exceptions PdfNetStack raises for a document it cannot read or a limit the
/// document runs into, so one catch handles them all. Argument errors still raise the standard
/// <see cref="ArgumentException"/> family, and font files raise <see cref="Fonts.FontFormatException"/>,
/// which keeps deriving from <see cref="FormatException"/>.
/// </summary>
public class PdfException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public PdfException() { }
}

/// <summary>
/// A document ran into one of its <see cref="PdfLimits"/>. It derives from
/// <see cref="PdfFormatException"/>, which limits raised before this type existed, so code that
/// catches that keeps working; catch this type to tell a document that is too costly from one
/// that is damaged. The per object ceilings, <see cref="PdfLoadOptions.MaxDecodedStreamBytes"/>
/// and <see cref="PdfLoadOptions.MaxImagePixels"/>, still raise <see cref="PdfFormatException"/>
/// itself, as they always have.
/// </summary>
public sealed class PdfLimitExceededException : PdfFormatException
{
    /// <summary>Creates the exception for <paramref name="limit"/> with a message.</summary>
    public PdfLimitExceededException(PdfLimitKind limit, string message) : base(message) => Limit = limit;

    /// <summary>Creates the exception with a message.</summary>
    public PdfLimitExceededException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfLimitExceededException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public PdfLimitExceededException() { }

    /// <summary>The limit that was reached.</summary>
    public PdfLimitKind Limit { get; }
}
