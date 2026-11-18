namespace PdfNetStack;

/// <summary>
/// Raised for input that cannot be interpreted as PDF syntax, and for a document over a decoding
/// limit; <see cref="PdfLimitExceededException"/> marks the second case.
/// </summary>
public class PdfFormatException : PdfException
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfFormatException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public PdfFormatException() { }
}
