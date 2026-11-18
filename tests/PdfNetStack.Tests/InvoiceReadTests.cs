using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Reading Factur-X, ZUGFeRD and XRechnung invoices from received PDFs.</summary>
[TestClass]
public sealed class InvoiceReadTests
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>");

    private static PdfDocument Document()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = "Invoice 2026-001";
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString("AV To", PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf")), 18, PdfColor.Black, 72, 72);
        return doc;
    }

    /// <summary>
    /// A hybrid invoice as another producer writes it: the XML attached under <paramref name="name"/>,
    /// optionally declared as an associated file, with XMP metadata of its own.
    /// </summary>
    private static PdfDocument Foreign(string name, string? xmp, bool associated = true, string? relationship = "Alternative", byte[]? xml = null)
    {
        PdfDocument doc = Document();
        PdfAttachment attachment = doc.Attachments.Add(name, xml ?? Xml, "text/xml");
        if (relationship is not null) attachment.Dictionary.Set("AFRelationship", new PdfName(relationship));
        if (associated)
        {
            PdfObject entry = doc.Attachments.Entries().Single(e => e.Name == name).Spec;
            doc.Catalog.Set("AF", PdfArray.Of(entry));
        }

        if (xmp is not null)
        {
            PdfStream metadata = PdfStream.FromData(Encoding.UTF8.GetBytes(xmp), compress: false);
            metadata.Dictionary.Set("Type", new PdfName("Metadata"));
            metadata.Dictionary.Set("Subtype", new PdfName("XML"));
            doc.Catalog.Set("Metadata", doc.AddObject(metadata));
        }

        return PdfDocument.Open(doc.ToArray());
    }

    /// <summary>An XMP packet declaring PDF/A-3b and the given properties, as attributes of one description.</summary>
    private static string Xmp(string prefix, string ns, string properties) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"3\" pdfaid:conformance=\"B\"/>"
        + $"<rdf:Description rdf:about=\"\" xmlns:{prefix}=\"{ns}\" {properties}/>"
        + "</rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";

    [TestMethod]
    public void ReadsBackWhatAttachWrote()
    {
        foreach ((PdfInvoiceProfile profile, string level, PdfFileRelationship relationship) in new[]
        {
            (PdfInvoiceProfile.Minimum, "MINIMUM", PdfFileRelationship.Data),
            (PdfInvoiceProfile.BasicWL, "BASIC WL", PdfFileRelationship.Data),
            (PdfInvoiceProfile.Basic, "BASIC", PdfFileRelationship.Alternative),
            (PdfInvoiceProfile.EN16931, "EN 16931", PdfFileRelationship.Alternative),
            (PdfInvoiceProfile.Extended, "EXTENDED", PdfFileRelationship.Alternative),
        })
        {
            PdfDocument doc = Document();
            PdfInvoice.Attach(doc, Xml, profile);
            PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));

            Assert.IsTrue(PdfInvoice.TryRead(back, out PdfInvoiceData? invoice, out string? reason), reason);
            CollectionAssert.AreEqual(Xml, invoice.GetXml());
            Assert.AreEqual("factur-x.xml", invoice.FileName);
            Assert.AreEqual(PdfInvoiceSpecification.FacturX, invoice.Specification);
            Assert.AreEqual(profile, invoice.Profile);
            Assert.AreEqual(level, invoice.ConformanceLevel);
            Assert.AreEqual("INVOICE", invoice.DocumentType);
            Assert.AreEqual("1.0", invoice.Version);
            Assert.AreEqual(3, invoice.PdfAPart);
            Assert.AreEqual("B", invoice.PdfAConformance);
            Assert.AreEqual(relationship, invoice.Relationship);
            Assert.IsEmpty(invoice.Warnings, string.Join("\n", invoice.Warnings));
        }
    }

    [TestMethod]
    public void ReadsXRechnungUnderItsOwnName()
    {
        PdfDocument doc = Document();
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.XRechnung, new PdfInvoiceOptions { FileName = "xrechnung.xml", Version = "2.3" });
        PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U }));
        Assert.IsTrue(PdfInvoice.TryRead(back, out PdfInvoiceData? invoice));
        Assert.AreEqual("xrechnung.xml", invoice.FileName);
        Assert.AreEqual(PdfInvoiceProfile.XRechnung, invoice.Profile);
        Assert.AreEqual("2.3", invoice.Version);
        Assert.AreEqual("U", invoice.PdfAConformance);
    }

    [TestMethod]
    public void ReadsZugferd1Metadata()
    {
        // ZUGFeRD 1.0: its own namespace, prefix and file name, properties written as attributes.
        PdfDocument doc = Foreign("ZUGFeRD-invoice.xml", Xmp("zf", "urn:ferd:pdfa:CrossIndustryDocument:invoice:1p0#",
            "zf:DocumentType=\"INVOICE\" zf:DocumentFileName=\"ZUGFeRD-invoice.xml\" zf:Version=\"1.0\" zf:ConformanceLevel=\"COMFORT\""));
        Assert.IsTrue(PdfInvoice.TryRead(doc, out PdfInvoiceData? invoice, out string? reason), reason);
        Assert.AreEqual(PdfInvoiceSpecification.Zugferd1, invoice.Specification);
        Assert.AreEqual("ZUGFeRD-invoice.xml", invoice.FileName);
        Assert.AreEqual("COMFORT", invoice.ConformanceLevel);

        // Its levels predate EN 16931 and are not mapped onto the Factur-X profiles.
        Assert.IsNull(invoice.Profile);
        Assert.IsEmpty(invoice.Warnings, string.Join("\n", invoice.Warnings));
    }

    [TestMethod]
    public void ReadsZugferd20Metadata()
    {
        PdfDocument doc = Foreign("zugferd-invoice.xml", Xmp("zf", "urn:zugferd:pdfa:CrossIndustryDocument:invoice:2p0#",
            "zf:DocumentType=\"INVOICE\" zf:DocumentFileName=\"zugferd-invoice.xml\" zf:Version=\"2p0\" zf:ConformanceLevel=\"EN 16931\""));
        Assert.IsTrue(PdfInvoice.TryRead(doc, out PdfInvoiceData? invoice));
        Assert.AreEqual(PdfInvoiceSpecification.Zugferd2, invoice.Specification);
        Assert.AreEqual(PdfInvoiceProfile.EN16931, invoice.Profile);
        Assert.AreEqual("2p0", invoice.Version);
    }

    [TestMethod]
    public void FindsTheDeclaredFileIgnoringCase()
    {
        // The metadata and the attachment disagree on case only, and a prefix other than fx is used.
        PdfDocument doc = Foreign("Factur-X.xml", Xmp("facturx", "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#",
            "facturx:DocumentType=\"INVOICE\" facturx:DocumentFileName=\"factur-x.xml\" facturx:Version=\"1.0\" facturx:ConformanceLevel=\"basic wl\""));
        Assert.IsTrue(PdfInvoice.TryRead(doc, out PdfInvoiceData? invoice));
        Assert.AreEqual("Factur-X.xml", invoice.FileName);
        Assert.AreEqual(PdfInvoiceProfile.BasicWL, invoice.Profile);
    }

    [TestMethod]
    public void FindsAnInvoiceThatIsNotAnAssociatedFile()
    {
        // No /AF, no relationship, no XMP: found by name, with every departure reported.
        PdfDocument doc = Foreign("factur-x.xml", xmp: null, associated: false, relationship: null);
        Assert.IsTrue(PdfInvoice.TryRead(doc, out PdfInvoiceData? invoice));
        CollectionAssert.AreEqual(Xml, invoice.GetXml());
        Assert.AreEqual(PdfInvoiceSpecification.Unknown, invoice.Specification);
        Assert.IsNull(invoice.Profile);
        Assert.IsNull(invoice.Relationship);
        string warnings = string.Join("\n", invoice.Warnings);
        StringAssert.Contains(warnings, "associated file");
        StringAssert.Contains(warnings, "relationship is missing");
        StringAssert.Contains(warnings, "no XMP metadata");
        StringAssert.Contains(warnings, "declares no PDF/A part");
    }

    [TestMethod]
    public void ReportsWhyNothingWasRead()
    {
        PdfDocument plain = Document();
        plain.Attachments.Add("data.csv", "a,b\n"u8.ToArray(), "text/csv");
        Assert.IsFalse(PdfInvoice.TryRead(PdfDocument.Open(plain.ToArray()), out PdfInvoiceData? none, out string? reason));
        Assert.IsNull(none);
        StringAssert.Contains(reason, "no Factur-X, ZUGFeRD or XRechnung invoice");

        Assert.IsFalse(PdfInvoice.TryRead(Foreign("factur-x.xml", null, xml: "<invoice><open></invoice>"u8.ToArray()), out _, out reason));
        StringAssert.Contains(reason, "not well formed");

        Assert.IsFalse(PdfInvoice.TryRead(Foreign("factur-x.xml", null, xml: []), out _, out reason));
        StringAssert.Contains(reason, "empty");
    }

    [TestMethod]
    public void RefusesDtdsAndExternalEntities()
    {
        // An external entity would read a local file if it were resolved; a DTD is refused before that.
        string secret = Path.Combine(Path.GetTempPath(), "pdfnetstack-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "SECRET");
        try
        {
            byte[] xxe = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><a>&x;</a>");
            Assert.IsFalse(PdfInvoice.TryRead(Foreign("factur-x.xml", null, xml: xxe), out _, out string? reason));
            StringAssert.Contains(reason, "not well formed");

            // The same in the XMP metadata only costs the metadata, as a warning.
            string xmp = $"<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY s SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&s;</x:xmpmeta>";
            Assert.IsTrue(PdfInvoice.TryRead(Foreign("factur-x.xml", xmp), out PdfInvoiceData? invoice));
            StringAssert.Contains(string.Join("\n", invoice.Warnings), "XMP metadata cannot be read");
            Assert.AreEqual(PdfInvoiceSpecification.Unknown, invoice.Specification);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [TestMethod]
    public void DamagedFilesDoNotThrow()
    {
        // The attachment's data does not inflate.
        PdfDocument doc = Document();
        PdfAttachment attachment = doc.Attachments.Add("factur-x.xml", Xml, "text/xml");
        var ef = (PdfDictionary)doc.Resolve(attachment.Dictionary["EF"])!;
        var stream = (PdfStream)doc.Resolve(ef["F"])!;
        stream.Data = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0x00 };
        stream.Dictionary.Set("Filter", new PdfName("FlateDecode"));
        Assert.IsFalse(PdfInvoice.TryRead(PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Optimize = false })), out _, out string? reason));
        StringAssert.Contains(reason, "cannot be decoded");

        // A file specification without its embedded file.
        PdfDocument empty = Document();
        empty.Attachments.Add("factur-x.xml", Xml, "text/xml").Dictionary.Remove("EF");
        Assert.IsFalse(PdfInvoice.TryRead(PdfDocument.Open(empty.ToArray()), out _, out reason));
        StringAssert.Contains(reason, "no embedded data");

        // XMP that is not XML, and an /AF that is not an array.
        PdfDocument odd = Foreign("factur-x.xml", "<x:xmpmeta><unclosed>");
        odd.Catalog.Set("AF", new PdfName("Nonsense"));
        Assert.IsTrue(PdfInvoice.TryRead(odd, out PdfInvoiceData? invoice));
        StringAssert.Contains(string.Join("\n", invoice.Warnings), "XMP metadata cannot be read");
    }

    [TestMethod]
    public void WarnsAboutForbiddenRelationshipsAndUnknownLevels()
    {
        PdfDocument doc = Foreign("factur-x.xml", Xmp("fx", "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#",
            "fx:DocumentType=\"INVOICE\" fx:DocumentFileName=\"factur-x.xml\" fx:Version=\"1.0\" fx:ConformanceLevel=\"PREMIUM\""), relationship: "Supplement");
        Assert.IsTrue(PdfInvoice.TryRead(doc, out PdfInvoiceData? invoice));
        Assert.AreEqual(PdfFileRelationship.Supplement, invoice.Relationship);
        Assert.IsNull(invoice.Profile);
        Assert.AreEqual("PREMIUM", invoice.ConformanceLevel);
        string warnings = string.Join("\n", invoice.Warnings);
        StringAssert.Contains(warnings, "relationship is Supplement");
        StringAssert.Contains(warnings, "PREMIUM is not a known profile");
    }

    [TestMethod]
    public void ReturnedXmlIsACopy()
    {
        PdfDocument doc = Document();
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Basic);
        Assert.IsTrue(PdfInvoice.TryRead(PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B })), out PdfInvoiceData? invoice));
        byte[] first = invoice.GetXml();
        first[0] = 0;
        CollectionAssert.AreEqual(Xml, invoice.GetXml());
    }
}
