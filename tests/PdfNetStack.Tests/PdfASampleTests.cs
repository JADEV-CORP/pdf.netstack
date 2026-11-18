using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// PDF/A and PDF/UA files made by the library, for veraPDF in CI: set PDFNETSTACK_PDFA_SAMPLES to
/// a folder and the files are written there, named after the flavour veraPDF should check them
/// against (ua1 for PDF/UA-1). Without it they are only built and read back.
/// </summary>
[TestClass]
public sealed class PdfASampleTests
{
    public const string FolderVariable = "PDFNETSTACK_PDFA_SAMPLES";

    private static readonly byte[] InvoiceXml = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>");

    private static PdfDocument Page(string title)
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = title;
        doc.Info.Author = "PdfNetStack";
        doc.Language = "en";
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("AV To", PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf")), 24, PdfColor.Black, 72, 72);
            g.DrawRectangle(new PdfPen(PdfColor.Rgb(0.1, 0.3, 0.6), 1), PdfColor.Gray(0.9), 72, 120, 200, 80);
        }

        page.Annotations.AddLink(72, 72, 100, 30, new Uri("https://example.com/"));
        return doc;
    }

    /// <summary>
    /// A tagged page for PDF/UA-1: a running header artifact, a heading, a paragraph with a link,
    /// a figure, a table and a list. The test font covers " AHTVox" only.
    /// </summary>
    private static PdfDocument Tagged(string title)
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = title;
        doc.Info.Author = "PdfNetStack";
        doc.Language = "en";
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfFont font = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));
        PdfStructureElement link;
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.BeginArtifact(PdfArtifactType.Header);
            g.DrawString("Vox", font, 9, PdfColor.Gray(0.4), 72, 40);
            g.EndArtifact();

            g.BeginTag(PdfStructureType.H1);
            g.DrawString("AV To", font, 24, PdfColor.Black, 72, 72);
            g.EndTag();

            g.BeginTag(PdfStructureType.P);
            g.DrawString("To ", font, 12, PdfColor.Black, 72, 110);
            link = g.BeginTag(PdfStructureType.Link);
            g.DrawString("Vox", font, 12, PdfColor.Rgb(0, 0, 0.7), 96, 110);
            g.EndTag();
            g.EndTag();

            g.BeginTag(PdfStructureType.Figure).AlternateText = "A grey box with a blue border";
            g.DrawRectangle(new PdfPen(PdfColor.Rgb(0.1, 0.3, 0.6), 1), PdfColor.Gray(0.9), 72, 140, 200, 80);
            g.EndTag();

            g.BeginTag(PdfStructureType.Table);
            g.BeginTag(PdfStructureType.TR);
            g.BeginTag(PdfStructureType.TH).Scope = PdfTableHeaderScope.Column;
            g.DrawString("To", font, 12, PdfColor.Black, 72, 250);
            g.EndTag();
            g.EndTag();
            g.BeginTag(PdfStructureType.TR);
            g.BeginTag(PdfStructureType.TD);
            g.DrawString("Ho", font, 12, PdfColor.Black, 72, 270);
            g.EndTag();
            g.EndTag();
            g.EndTag();

            g.BeginTag(PdfStructureType.L).ListNumbering = PdfListNumbering.UpperAlpha;
            g.BeginTag(PdfStructureType.LI);
            g.BeginTag(PdfStructureType.Lbl);
            g.DrawString("A", font, 12, PdfColor.Black, 72, 300);
            g.EndTag();
            g.BeginTag(PdfStructureType.LBody);
            g.DrawString("Vox", font, 12, PdfColor.Black, 90, 300);
            g.EndTag();
            g.EndTag();
            g.EndTag();

            g.BeginArtifact(PdfArtifactType.Layout);
            g.DrawLine(new PdfPen(PdfColor.Gray(0.5), 0.5), 72, 320, 520, 320);
            g.EndArtifact();
        }

        PdfAnnotation annotation = page.Annotations.AddLink(96, 100, 30, 14, new Uri("https://example.com/"));
        annotation.Contents = "Example site";
        link.AddAnnotation(annotation);
        return doc;
    }

    /// <summary>
    /// The rest of the tagging API on two pages: a paragraph that continues on the next page, a
    /// link and an artifact in the middle of a paragraph, a custom role, a note, and figures drawn
    /// as an image and as a form XObject.
    /// </summary>
    private static PdfDocument TaggedFeatures()
    {
        PdfDocument doc = Tagged("PDF/UA-1 tagging features");
        doc.Structure.MapRole("Subhead", PdfStructureType.H2);
        PdfFont font = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));
        PdfImage image = PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "gray8.png"));
        PdfForm badge = PdfForm.Create(doc, 40, 40);
        using (PdfGraphics f = badge.CreateGraphics()) f.DrawEllipse(null, PdfColor.Rgb(0.8, 0.2, 0.2), 0, 0, 40, 40);

        PdfStructureElement paragraph;
        PdfPage first = doc.Pages[0], second = doc.Pages.Add(PdfRectangle.A4);
        PdfStructureElement link;
        using (PdfGraphics g = first.CreateGraphics())
        {
            g.BeginTag("Subhead");
            g.DrawString("Vox", font, 16, PdfColor.Black, 72, 360);
            g.EndTag();

            paragraph = g.BeginTag(PdfStructureType.P);
            g.DrawString("To ", font, 12, PdfColor.Black, 72, 390);
            link = g.BeginTag(PdfStructureType.Link);
            g.DrawString("AV", font, 12, PdfColor.Rgb(0, 0, 0.7), 92, 390);
            g.EndTag();
            g.BeginArtifact();
            g.DrawLine(new PdfPen(PdfColor.Gray(0.6), 0.5), 72, 396, 200, 396);
            g.EndArtifact();
            g.DrawString(" Ho", font, 12, PdfColor.Black, 112, 390);
            g.BeginTag(PdfStructureType.Note);
            g.DrawString("A", font, 7, PdfColor.Black, 132, 386);
            g.EndTag();
            g.EndTag();

            g.BeginTag(PdfStructureType.Figure).AlternateText = "A grey gradient";
            g.DrawImage(image, 72, 420, 64, 64);
            g.EndTag();
            g.BeginTag(PdfStructureType.Figure).AlternateText = "A red disc";
            g.DrawForm(badge, 160, 420);
            g.EndTag();
        }

        using (PdfGraphics g = second.CreateGraphics())
        {
            g.BeginTag(paragraph);
            g.DrawString("Vox To", font, 12, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        PdfAnnotation annotation = first.Annotations.AddLink(92, 380, 20, 14, second);
        annotation.Contents = "Next page";
        link.AddAnnotation(annotation);
        return doc;
    }

    /// <summary>Every sample: its file name (flavour first) and bytes.</summary>
    private static IEnumerable<(string Name, byte[] Data)> Samples()
    {
        yield return ("1b-plain.pdf", Page("PDF/A-1b").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA1B }));
        yield return ("1b-unoptimized.pdf", Page("PDF/A-1b, not optimized").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA1B, Optimize = false }));
        byte[] twoB = Page("PDF/A-2b").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2B });
        yield return ("2b-plain.pdf", twoB);
        yield return ("2u-plain.pdf", Page("PDF/A-2u").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2U }));
        yield return ("3u-plain.pdf", Page("PDF/A-3u").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U, UseObjectStreams = false }));

        PdfDocument attached = Page("PDF/A-3b with an attachment");
        attached.Attachments.Add("data.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        yield return ("3b-attachment.pdf", attached.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));

        PdfDocument facturX = Page("Factur-X EN 16931");
        PdfInvoice.Attach(facturX, InvoiceXml, PdfInvoiceProfile.EN16931);
        byte[] invoice = facturX.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
        yield return ("3b-facturx-en16931.pdf", invoice);

        PdfDocument xrechnung = Page("ZUGFeRD XRECHNUNG");
        PdfInvoice.Attach(xrechnung, InvoiceXml, PdfInvoiceProfile.XRechnung);
        yield return ("3u-zugferd-xrechnung.pdf", xrechnung.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U }));

        yield return ("4-plain.pdf", Page("PDF/A-4").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4 }));
        yield return ("4e-plain.pdf", Page("PDF/A-4e").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4E }));

        PdfDocument nested = Page("PDF/A-4 with an embedded PDF/A-2b file");
        nested.Attachments.Add("earlier.pdf", twoB, "application/pdf");
        yield return ("4-embedded-pdfa.pdf", nested.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4 }));

        PdfDocument files = Page("PDF/A-4f with an attachment");
        files.Attachments.Add("data.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        yield return ("4f-attachment.pdf", files.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4F }));

        // A signed invoice with a signature timestamp, validation data and a document timestamp (PAdES B-LTA).
        var pki = new TestPki();
        byte[] signed = PdfSigner.Sign(invoice, pki.Signer, new PdfSignatureOptions { Chain = [pki.Intermediate], Timestamp = pki.TimestampClient(), Reason = "Issued" });
        byte[] lt = PdfSigner.AddValidationData(signed, pki.GoodRevocation(), [pki.Root]);
        byte[] lta = PdfSigner.AddDocumentTimestamp(lt, pki.TimestampClient());
        yield return ("3b-facturx-signed-lta.pdf", PdfSigner.AddValidationData(lta, pki.GoodRevocation(), [pki.Root]));

        // PDF/UA-1, alone and with PDF/A: a file that is both is written twice, once under each flavour.
        yield return ("ua1-tagged.pdf", Tagged("PDF/UA-1").ToArray(new PdfSaveOptions { Accessibility = PdfAccessibility.PdfUA1 }));
        yield return ("ua1-features.pdf", TaggedFeatures().ToArray(new PdfSaveOptions { Accessibility = PdfAccessibility.PdfUA1 }));
        byte[] twoU = Tagged("PDF/A-2u and PDF/UA-1").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2U, Accessibility = PdfAccessibility.PdfUA1 });
        yield return ("ua1-tagged-2u.pdf", twoU);
        yield return ("2u-tagged-ua1.pdf", twoU);
        PdfDocument accessibleInvoice = Tagged("Factur-X on PDF/A-3u and PDF/UA-1");
        PdfInvoice.Attach(accessibleInvoice, InvoiceXml, PdfInvoiceProfile.EN16931);
        byte[] accessible = accessibleInvoice.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U, Accessibility = PdfAccessibility.PdfUA1 });
        yield return ("ua1-facturx-3u.pdf", accessible);
        yield return ("3u-facturx-ua1.pdf", accessible);
    }

    [TestMethod]
    public void SamplesAreReadable()
    {
        string? folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        foreach ((string name, byte[] data) in Samples())
        {
            PdfDocument back = PdfDocument.Open(data);
            Assert.IsNotNull(back.Catalog["Metadata"], name);
            if (!string.IsNullOrEmpty(folder)) File.WriteAllBytes(Path.Combine(folder, name), data);
        }
    }
}
