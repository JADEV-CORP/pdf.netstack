using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>The hybrid invoice specification an invoice's XMP metadata declares.</summary>
public enum PdfInvoiceSpecification
{
    /// <summary>The XMP metadata declares none: the invoice was recognised by its attachment name only.</summary>
    Unknown,

    /// <summary>
    /// Factur-X 1.0, and ZUGFeRD 2.1 and later, which share its metadata
    /// (urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#).
    /// </summary>
    FacturX,

    /// <summary>ZUGFeRD 2.0 (urn:zugferd:pdfa:CrossIndustryDocument:invoice:2p0#).</summary>
    Zugferd2,

    /// <summary>
    /// ZUGFeRD 1.0 (urn:ferd:pdfa:CrossIndustryDocument:invoice:1p0#). Its XML is an earlier
    /// Cross Industry Invoice version that predates EN 16931.
    /// </summary>
    Zugferd1,
}

/// <summary>
/// An invoice found in a hybrid PDF by <see cref="PdfInvoice.TryRead(PdfDocument, out PdfInvoiceData?, out string?)"/>:
/// the embedded XML as stored, and what the document says about it. The XML is checked to be well
/// formed and is otherwise not interpreted.
/// </summary>
public sealed class PdfInvoiceData
{
    private readonly byte[] _xml;

    internal PdfInvoiceData(byte[] xml, string fileName, PdfFileRelationship? relationship, InvoiceXmp xmp, IReadOnlyList<string> warnings)
    {
        _xml = xml;
        FileName = fileName;
        Relationship = relationship;
        Specification = xmp.Specification;
        ConformanceLevel = xmp.ConformanceLevel;
        Profile = xmp.Specification is PdfInvoiceSpecification.FacturX or PdfInvoiceSpecification.Zugferd2 ? PdfInvoice.ParseLevel(xmp.ConformanceLevel) : null;
        DocumentType = xmp.DocumentType;
        Version = xmp.Version;
        PdfAPart = xmp.PdfAPart;
        PdfAConformance = xmp.PdfAConformance;
        Warnings = warnings;
    }

    /// <summary>The invoice XML, byte for byte as embedded. Each call returns a new copy.</summary>
    public byte[] GetXml() => (byte[])_xml.Clone();

    /// <summary>The name of the attachment the XML was read from, such as factur-x.xml.</summary>
    public string FileName { get; }

    /// <summary>The attachment's /AFRelationship, or null when it has none or an unknown one.</summary>
    public PdfFileRelationship? Relationship { get; }

    /// <summary>The specification the XMP metadata declares.</summary>
    public PdfInvoiceSpecification Specification { get; }

    /// <summary>
    /// The profile, from the XMP conformance level of a Factur-X or ZUGFeRD 2 invoice. Null when the
    /// level is missing or unknown, and for ZUGFeRD 1.0, whose levels are not the EN 16931 profiles:
    /// read <see cref="ConformanceLevel"/> instead.
    /// </summary>
    public PdfInvoiceProfile? Profile { get; }

    /// <summary>The conformance level as the XMP metadata writes it, such as "EN 16931" or "COMFORT", or null.</summary>
    public string? ConformanceLevel { get; }

    /// <summary>The document type the XMP metadata declares, usually "INVOICE", or null.</summary>
    public string? DocumentType { get; }

    /// <summary>The specification version the XMP metadata declares, such as "1.0", or null.</summary>
    public string? Version { get; }

    /// <summary>The PDF/A part the XMP metadata declares (pdfaid:part), or null. Factur-X and ZUGFeRD need 3.</summary>
    public int? PdfAPart { get; }

    /// <summary>The PDF/A conformance letter the XMP metadata declares (pdfaid:conformance), such as "B", or null.</summary>
    public string? PdfAConformance { get; }

    /// <summary>
    /// What the file does differently from Factur-X and ZUGFeRD without stopping the XML from being
    /// read, such as an attachment that is not an associated file. Empty for a conforming file.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }
}

public static partial class PdfInvoice
{
    /// <summary>Attachment names Factur-X, ZUGFeRD 1 and 2 and XRechnung hybrids use, in the order they are looked for.</summary>
    private static readonly string[] KnownFileNames = [FacturXFileName, "zugferd-invoice.xml", "ZUGFeRD-invoice.xml", XRechnungFileName];

    /// <summary>
    /// Reads the invoice of a Factur-X, ZUGFeRD or XRechnung hybrid PDF: the embedded XML and the
    /// profile the XMP metadata declares. Never throws for a damaged or unusual file.
    /// </summary>
    /// <param name="document">The document, opened from the received file.</param>
    /// <param name="invoice">The invoice when found and well formed, otherwise null.</param>
    /// <returns>True when an invoice XML was found and is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static bool TryRead(PdfDocument document, [NotNullWhen(true)] out PdfInvoiceData? invoice) => TryRead(document, out invoice, out _);

    /// <summary>
    /// Reads the invoice of a Factur-X, ZUGFeRD or XRechnung hybrid PDF: the embedded XML and the
    /// profile the XMP metadata declares. Never throws for a damaged or unusual file.
    /// </summary>
    /// <remarks>
    /// The XML is found by the file name the XMP metadata declares, then by the names Factur-X,
    /// ZUGFeRD and XRechnung use (factur-x.xml, zugferd-invoice.xml, ZUGFeRD-invoice.xml,
    /// xrechnung.xml), among the document's associated files (/AF) first and its attachments
    /// second. It is parsed only to check it is well formed, with DTDs refused and no external
    /// entity resolved; its business content is not validated. Departures from the specifications
    /// that do not stop the XML from being read are listed in <see cref="PdfInvoiceData.Warnings"/>.
    /// </remarks>
    /// <param name="document">The document, opened from the received file.</param>
    /// <param name="invoice">The invoice when found and well formed, otherwise null.</param>
    /// <param name="reason">Why no invoice was returned, otherwise null.</param>
    /// <returns>True when an invoice XML was found and is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("received.pdf");
    /// if (PdfInvoice.TryRead(pdf, out PdfInvoiceData? invoice, out string? reason))
    /// {
    ///     byte[] xml = invoice.GetXml();   // hand to an EN 16931 parser or validator
    ///     Console.WriteLine($"{invoice.FileName}: {invoice.Profile}");
    /// }
    /// else
    /// {
    ///     Console.WriteLine(reason);
    /// }
    /// </code>
    /// </example>
    public static bool TryRead(PdfDocument document, [NotNullWhen(true)] out PdfInvoiceData? invoice, [NotNullWhen(false)] out string? reason)
    {
        Compat.ThrowIfNull(document, nameof(document));
        invoice = null;
        try
        {
            return Read(document, out invoice, out reason);
        }
        catch (PdfFormatException e)
        {
            // An object the search reaches cannot be parsed, as can happen with files read on demand.
            reason = "The file is damaged where the invoice would be: " + e.Message;
            return false;
        }
    }

    private static bool Read(PdfDocument doc, out PdfInvoiceData? invoice, out string? reason)
    {
        invoice = null;
        var warnings = new List<string>();
        InvoiceXmp xmp = InvoiceXmp.Read(doc, warnings);

        // Associated files first: the specifications declare the invoice there, and its name in the
        // attachment list may differ from the file name.
        var candidates = new List<(string Name, PdfDictionary Spec, bool Associated)>();
        var associated = new HashSet<PdfDictionary>();
        if (doc.Resolve(doc.Catalog["AF"]) is PdfArray af)
        {
            foreach (PdfObject entry in af.Items.Take(10_000))
            {
                if (doc.Resolve(entry) is PdfDictionary spec && associated.Add(spec)) candidates.Add((SpecName(doc, spec) ?? "", spec, true));
            }
        }

        foreach ((string name, PdfObject entry) in doc.Attachments.Entries())
        {
            if (doc.Resolve(entry) is PdfDictionary spec && !associated.Contains(spec)) candidates.Add((SpecName(doc, spec) ?? name, spec, false));
        }

        (string Name, PdfDictionary Spec, bool Associated)? found = null;
        if (xmp.DocumentFileName is string declared)
        {
            found = Find(candidates, declared);
            if (found is null) warnings.Add($"The XMP metadata names the invoice {declared}, but no attachment has that name.");
        }

        foreach (string known in KnownFileNames)
        {
            found ??= Find(candidates, known);
        }

        if (found is not { } chosen)
        {
            reason = "The document has no Factur-X, ZUGFeRD or XRechnung invoice attachment.";
            return false;
        }

        if (doc.Resolve(chosen.Spec["EF"]) is not PdfDictionary ef || doc.Resolve(ef["UF"] ?? ef["F"]) is not PdfStream stream)
        {
            reason = $"The invoice attachment {chosen.Name} has no embedded data.";
            return false;
        }

        byte[] xml;
        try
        {
            xml = doc.GetStreamData(stream);
        }
        catch (PdfFormatException e)
        {
            reason = $"The invoice attachment {chosen.Name} cannot be decoded: {e.Message}";
            return false;
        }

        if (WellFormedError(xml) is string error)
        {
            reason = $"The invoice attachment {chosen.Name} is not well formed XML: {error}";
            return false;
        }

        string? relationshipName = doc.Resolve(chosen.Spec["AFRelationship"]) is PdfName r ? r.Value : null;
        PdfFileRelationship? relationship = relationshipName switch
        {
            "Data" => PdfFileRelationship.Data,
            "Source" => PdfFileRelationship.Source,
            "Alternative" => PdfFileRelationship.Alternative,
            "Supplement" => PdfFileRelationship.Supplement,
            "Unspecified" => PdfFileRelationship.Unspecified,
            _ => null,
        };

        if (!chosen.Associated) warnings.Add($"The invoice attachment {chosen.Name} is not declared as an associated file (/AF) of the document.");
        if (relationship is not (PdfFileRelationship.Data or PdfFileRelationship.Source or PdfFileRelationship.Alternative))
        {
            warnings.Add($"The invoice attachment's relationship is {relationshipName ?? "missing"}; Factur-X and ZUGFeRD expect Data, Source or Alternative.");
        }

        if (xmp.Specification == PdfInvoiceSpecification.Unknown) warnings.Add("The XMP metadata does not describe the invoice; its profile is unknown.");
        else if (xmp.ConformanceLevel is null) warnings.Add("The XMP metadata declares no conformance level.");
        else if (ParseLevel(xmp.ConformanceLevel) is null && xmp.Specification != PdfInvoiceSpecification.Zugferd1) warnings.Add($"The conformance level {xmp.ConformanceLevel} is not a known profile.");
        if (xmp.PdfAPart != 3) warnings.Add(xmp.PdfAPart is int part ? $"The document declares PDF/A-{part}; hybrid invoices are PDF/A-3 files." : "The document declares no PDF/A part; hybrid invoices are PDF/A-3 files.");

        invoice = new PdfInvoiceData(xml, chosen.Name, relationship, xmp, warnings);
        reason = null;
        return true;
    }

    /// <summary>A candidate whose file name matches, ignoring case and any folder part.</summary>
    private static (string Name, PdfDictionary Spec, bool Associated)? Find(List<(string Name, PdfDictionary Spec, bool Associated)> candidates, string fileName)
    {
        foreach ((string Name, PdfDictionary Spec, bool Associated) c in candidates)
        {
            string name = c.Name.Substring(c.Name.LastIndexOfAny(['/', '\\']) + 1);
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return c;
        }

        return null;
    }

    /// <summary>A file specification's file name: the Unicode one first.</summary>
    private static string? SpecName(PdfDocument doc, PdfDictionary spec) =>
        doc.Resolve(spec["UF"]) is PdfString uf ? uf.ToText() : doc.Resolve(spec["F"]) is PdfString f ? f.ToText() : null;

    /// <summary>The parser's message when the XML is not well formed, with DTDs refused and nothing resolved; null when it is.</summary>
    private static string? WellFormedError(byte[] xml)
    {
        if (xml.Length == 0) return "the attachment is empty.";
        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, InvoiceXmp.SafeSettings);
            while (reader.Read())
            {
            }

            return null;
        }
        catch (XmlException e)
        {
            return e.Message;
        }
    }

    /// <summary>The profile a conformance level names, ignoring case, spaces and hyphens: "BASIC WL", "EN 16931" or "COMFORT".</summary>
    internal static PdfInvoiceProfile? ParseLevel(string? level)
    {
        if (level is null) return null;
        string key = string.Concat(level.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_')).Select(char.ToUpperInvariant));
        return key switch
        {
            "MINIMUM" => PdfInvoiceProfile.Minimum,
            "BASICWL" => PdfInvoiceProfile.BasicWL,
            "BASIC" => PdfInvoiceProfile.Basic,
            "EN16931" or "COMFORT" => PdfInvoiceProfile.EN16931,
            "EXTENDED" => PdfInvoiceProfile.Extended,
            "XRECHNUNG" => PdfInvoiceProfile.XRechnung,
            _ => null,
        };
    }
}

/// <summary>The PDF/A identification and hybrid invoice properties of a document's XMP metadata.</summary>
internal sealed class InvoiceXmp
{
    private const string PdfAId = "http://www.aiim.org/pdfa/ns/id/";

    /// <summary>The invoice schemas, most recent first: a file declaring several is read by the newest.</summary>
    private static readonly (string Namespace, PdfInvoiceSpecification Specification)[] Schemas =
    [
        (PdfInvoice.Namespace, PdfInvoiceSpecification.FacturX),
        ("urn:zugferd:pdfa:CrossIndustryDocument:invoice:2p0#", PdfInvoiceSpecification.Zugferd2),
        ("urn:ferd:pdfa:CrossIndustryDocument:invoice:1p0#", PdfInvoiceSpecification.Zugferd1),
    ];

    /// <summary>XML reading for untrusted input: no DTD, no external resources, bounded size.</summary>
    internal static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 64L << 20,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public PdfInvoiceSpecification Specification { get; private set; }

    public string? DocumentFileName { get; private set; }

    public string? DocumentType { get; private set; }

    public string? Version { get; private set; }

    public string? ConformanceLevel { get; private set; }

    public int? PdfAPart { get; private set; }

    public string? PdfAConformance { get; private set; }

    /// <summary>The catalog's XMP metadata, read namespace aware; problems reading it become warnings and an empty result.</summary>
    public static InvoiceXmp Read(PdfDocument doc, List<string> warnings)
    {
        var result = new InvoiceXmp();
        if (doc.Resolve(doc.Catalog["Metadata"]) is not PdfStream stream)
        {
            warnings.Add("The document has no XMP metadata.");
            return result;
        }

        XDocument xmp;
        try
        {
            using var data = new MemoryStream(doc.GetStreamData(stream), writable: false);
            using var reader = XmlReader.Create(data, SafeSettings);
            xmp = XDocument.Load(reader);
        }
        catch (Exception e) when (e is XmlException or PdfFormatException)
        {
            warnings.Add("The XMP metadata cannot be read: " + e.Message);
            return result;
        }

        string? Property(string ns, string name)
        {
            // XMP writes a simple property as an element or as an attribute of its description.
            XName key = XName.Get(name, ns);
            string? value = xmp.Descendants(key).Select(e => e.Value).FirstOrDefault()
                ?? xmp.Descendants().Select(e => e.Attribute(key)?.Value).FirstOrDefault(v => v is not null);
            return value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }

        if (int.TryParse(Property(PdfAId, "part"), NumberStyles.None, CultureInfo.InvariantCulture, out int part)) result.PdfAPart = part;
        result.PdfAConformance = Property(PdfAId, "conformance");
        foreach ((string ns, PdfInvoiceSpecification specification) in Schemas)
        {
            string? level = Property(ns, "ConformanceLevel"), file = Property(ns, "DocumentFileName");
            if (level is null && file is null) continue;
            result.Specification = specification;
            result.ConformanceLevel = level;
            result.DocumentFileName = file;
            result.DocumentType = Property(ns, "DocumentType");
            result.Version = Property(ns, "Version");
            break;
        }

        return result;
    }
}
