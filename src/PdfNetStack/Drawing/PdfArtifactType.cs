namespace PdfNetStack.Drawing;

/// <summary>
/// What an artifact is (ISO 32000-1, Table 330): content that is not part of the document's
/// text, which assistive technology skips. Opened with <see cref="PdfGraphics.BeginArtifact(PdfArtifactType)"/>.
/// </summary>
public enum PdfArtifactType
{
    /// <summary>Not said: a plain /Artifact sequence.</summary>
    Unspecified,

    /// <summary>Page numbers, running heads and other content repeated for pagination.</summary>
    Pagination,

    /// <summary>A running header (Pagination with the Header subtype).</summary>
    Header,

    /// <summary>A running footer (Pagination with the Footer subtype).</summary>
    Footer,

    /// <summary>A watermark (Pagination with the Watermark subtype).</summary>
    Watermark,

    /// <summary>Typographic or design elements, such as rules and table cell borders.</summary>
    Layout,

    /// <summary>Production marks, such as cut and registration marks.</summary>
    Page,

    /// <summary>A background image or colour.</summary>
    Background,
}
