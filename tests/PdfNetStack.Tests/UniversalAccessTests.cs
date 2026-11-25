using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Tagged content authoring and PDF/UA-1 output.</summary>
[TestClass]
public sealed class UniversalAccessTests
{
    private static readonly PdfSaveOptions Ua = new() { Accessibility = PdfAccessibility.PdfUA1 };

    // The test font covers " AHTVox" only: every string drawn here uses those letters.
    private static PdfFont Font() => PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));

    private static PdfDocument Empty(string? title = "Tax Vox", string? language = "en")
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = title;
        doc.Language = language;
        doc.Pages.Add(PdfRectangle.A4);
        return doc;
    }

    /// <summary>A page with a header artifact, a heading, a paragraph holding a link, a figure, a table and a list.</summary>
    private static PdfDocument Tagged()
    {
        PdfDocument doc = Empty();
        PdfPage page = doc.Pages[0];
        PdfFont font = Font();
        using PdfGraphics g = page.CreateGraphics();
        g.BeginArtifact(PdfArtifactType.Header);
        g.DrawString("Vox", font, 9, PdfColor.Gray(0.5), 72, 30);
        g.EndArtifact();

        g.BeginTag(PdfStructureType.H1);
        g.DrawString("Ho Vox", font, 24, PdfColor.Black, 72, 72);
        g.EndTag();

        g.BeginTag(PdfStructureType.P);
        g.DrawString("To ", font, 11, PdfColor.Black, 72, 110);
        PdfStructureElement link = g.BeginTag(PdfStructureType.Link);
        g.DrawString("Vox", font, 11, PdfColor.Rgb(0, 0, 0.8), 100, 110);
        g.EndTag();
        g.DrawString(" Ho", font, 11, PdfColor.Black, 130, 110);
        g.EndTag();
        PdfAnnotation annotation = page.Annotations.AddLink(100, 100, 30, 14, new Uri("https://example.com/"));
        annotation.Contents = "Vox home page";
        link.AddAnnotation(annotation);

        g.BeginTag(PdfStructureType.Figure).AlternateText = "A blue square";
        g.DrawRectangle(null, PdfColor.Rgb(0.1, 0.3, 0.6), 72, 140, 60, 60);
        g.EndTag();

        g.BeginTag(PdfStructureType.Table);
        g.BeginTag(PdfStructureType.TR);
        g.BeginTag(PdfStructureType.TH).Scope = PdfTableHeaderScope.Column;
        g.DrawString("To", font, 11, PdfColor.Black, 72, 230);
        g.EndTag();
        g.EndTag();
        g.BeginTag(PdfStructureType.TR);
        g.BeginTag(PdfStructureType.TD);
        g.DrawString("Ho", font, 11, PdfColor.Black, 72, 250);
        g.EndTag();
        g.EndTag();
        g.EndTag();

        PdfStructureElement list = g.BeginTag(PdfStructureType.L);
        list.ListNumbering = PdfListNumbering.UpperAlpha;
        g.BeginTag(PdfStructureType.LI);
        g.BeginTag(PdfStructureType.Lbl);
        g.DrawString("A", font, 11, PdfColor.Black, 72, 280);
        g.EndTag();
        g.BeginTag(PdfStructureType.LBody);
        g.DrawString("Vox", font, 11, PdfColor.Black, 90, 280);
        g.EndTag();
        g.EndTag();
        g.EndTag();

        g.BeginArtifact(PdfArtifactType.Layout);
        g.DrawLine(new PdfPen(PdfColor.Gray(0.5), 0.5), 72, 300, 500, 300);
        g.EndArtifact();
        return doc;
    }

    private static string Content(PdfDocument doc, int page)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfDictionary Root(PdfDocument doc) => (PdfDictionary)doc.Resolve(doc.Catalog["StructTreeRoot"])!;

    private static PdfDictionary Document(PdfDocument doc) => (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(Root(doc)["K"])!)[0])!;

    /// <summary>The structure tree as types and children, MCIDs as numbers and annotations as OBJR, e.g. P(0,Link(1,OBJR),2).</summary>
    private static string Tree(PdfDocument doc, PdfObject? node, bool mcids = false)
    {
        switch (doc.Resolve(node))
        {
            case PdfNumber n:
                return mcids ? n.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            case PdfDictionary d when d.GetName("Type") == "OBJR":
                return "OBJR";
            case PdfDictionary d when d.GetName("Type") == "MCR":
                return mcids ? "MCR" + ((PdfNumber)d["MCID"]!).IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            case PdfDictionary element when element.GetName("S") is string type:
                IEnumerable<PdfObject> kids = doc.Resolve(element["K"]) switch
                {
                    PdfArray a => a,
                    PdfObject single => [single],
                    _ => [],
                };
                string[] children = [.. kids.Select(k => Tree(doc, k, mcids)).Where(t => t.Length > 0)];
                return children.Length == 0 ? type : $"{type}({string.Join(",", children)})";
            default:
                return "";
        }
    }

    private static string Xmp(PdfDocument doc) => Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Resolve(doc.Catalog["Metadata"])!));

    private static string Failures(PdfDocument doc, PdfSaveOptions? options = null) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(options ?? Ua)).Message;

    [TestMethod]
    public void SavesATaggedDocumentAsPdfUA()
    {
        PdfDocument back = PdfDocument.Open(Tagged().ToArray(Ua));

        Assert.AreEqual("Document(H1,P(Link(OBJR)),Figure,Table(TR(TH),TR(TD)),L(LI(Lbl,LBody)))", Tree(back, Document(back)));
        Assert.IsTrue(((PdfDictionary)back.Resolve(back.Catalog["MarkInfo"])!)["Marked"] is PdfBoolean { Value: true });
        Assert.IsTrue(back.Viewer.DisplayDocumentTitle);
        Assert.AreEqual("S", back.Pages[0].Dictionary.GetName("Tabs"));

        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "<pdfuaid:part>1</pdfuaid:part>");
        StringAssert.Contains(xmp, "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"");
        StringAssert.Contains(xmp, "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Tax Vox</rdf:li></rdf:Alt></dc:title>");
        Assert.DoesNotContain("pdfaid:part", xmp);
        Assert.DoesNotContain("pdfaExtension", xmp);

        // Every MCID of the page, numbered from 0, has its element in the parent tree, and the link annotation too.
        string content = Content(back, 0);
        int[] mcids = [.. Regex.Matches(content, @"/MCID\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        CollectionAssert.AreEqual(Enumerable.Range(0, mcids.Length).ToArray(), mcids);
        var nums = (PdfArray)back.Resolve(((PdfDictionary)back.Resolve(Root(back)["ParentTree"])!)["Nums"])!;
        var parents = new Dictionary<long, PdfObject>();
        for (int i = 0; i + 1 < nums.Count; i += 2) parents[((PdfNumber)nums[i]).IntegerValue] = nums[i + 1];
        var pageParents = (PdfArray)back.Resolve(parents[((PdfNumber)back.Pages[0].Dictionary["StructParents"]!).IntegerValue])!;
        Assert.HasCount(mcids.Length, pageParents);
        Assert.IsTrue(pageParents.All(p => back.Resolve(p) is PdfDictionary { } e && e.GetName("Type") == "StructElem"));
        PdfDictionary annotation = back.Pages[0].Annotations[0].Dictionary;
        Assert.AreEqual("Link", ((PdfDictionary)back.Resolve(parents[((PdfNumber)annotation["StructParent"]!).IntegerValue])!).GetName("S"));
        Assert.AreEqual(((PdfNumber)Root(back)["ParentTreeNextKey"]!).IntegerValue, parents.Keys.Max() + 1);

        // Marked content is balanced, artifacts carry their kind, and the attributes are written.
        Assert.AreEqual(Regex.Count(content, @"\b(BDC|BMC)\b"), Regex.Count(content, @"\bEMC\b"));
        StringAssert.Matches(content, new Regex(@"/Artifact\s*<<\s*/Type\s*/Pagination\s*/Subtype\s*/Header\s*>>\s*BDC"));
        StringAssert.Matches(content, new Regex(@"/Artifact\s*<<\s*/Type\s*/Layout\s*>>\s*BDC"));
        PdfDictionary th = Find(back, "TH");
        Assert.AreEqual("Column", ((PdfDictionary)back.Resolve(th["A"])!).GetName("Scope"));
        Assert.AreEqual("UpperAlpha", ((PdfDictionary)back.Resolve(Find(back, "L")["A"])!).GetName("ListNumbering"));
        Assert.AreEqual("A blue square", ((PdfString)back.Resolve(Find(back, "Figure")["Alt"])!).ToText());
        StringAssert.Contains(back.Pages[0].ExtractText(), "Ho Vox");
    }

    private static PdfDictionary Find(PdfDocument doc, string type)
    {
        var pending = new Stack<PdfObject?>([Root(doc)["K"]]);
        while (pending.Count > 0)
        {
            switch (doc.Resolve(pending.Pop()))
            {
                case PdfArray a:
                    foreach (PdfObject item in a) pending.Push(item);
                    break;
                case PdfDictionary d when d.GetName("S") == type:
                    return d;
                case PdfDictionary d:
                    pending.Push(d["K"]);
                    break;
            }
        }

        throw new AssertFailedException("No " + type + " element.");
    }

    [TestMethod]
    public void NestedTagsReadInPlace()
    {
        PdfDocument doc = Tagged();
        doc.ToArray();
        PdfDocument back = PdfDocument.Open(doc.ToArray());

        // The paragraph pauses around its link: Tax, then the link, then Ho, each its own sequence.
        Assert.AreEqual("P(1,Link(2,OBJR),3)", Tree(back, Find(back, "P"), mcids: true));
        StringAssert.Matches(Content(back, 0), new Regex(@"/P\s*<</MCID 1>>\s*BDC.*?EMC\s*/Link\s*<</MCID 2>>\s*BDC.*?EMC\s*/P\s*<</MCID 3>>\s*BDC", RegexOptions.Singleline));
    }

    [TestMethod]
    public void EmptySequencesAreTakenBack()
    {
        PdfDocument doc = Empty();
        PdfFont font = Font();
        using (PdfGraphics g = doc.Pages[0].CreateGraphics())
        {
            // The table holds only rows, and the paragraph ends with its link: neither leaves an empty sequence.
            g.BeginTag(PdfStructureType.Table);
            g.BeginTag(PdfStructureType.TR);
            g.BeginTag(PdfStructureType.TD);
            g.DrawString("Ho", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
            g.EndTag();
            g.EndTag();
            g.BeginTag(PdfStructureType.P);
            g.DrawString("To", font, 11, PdfColor.Black, 72, 100);
            g.BeginTag(PdfStructureType.Span);
            g.DrawString("Vox", font, 11, PdfColor.Black, 100, 100);
            g.EndTag();
            g.EndTag();
        }

        PdfDocument back = PdfDocument.Open(doc.ToArray());
        Assert.AreEqual("Document(Table(TR(TD(0))),P(1,Span(2)))", Tree(back, Document(back), mcids: true));
        Assert.IsNull(Find(back, "Table")["Pg"]);
        Assert.AreEqual(3, Regex.Count(Content(back, 0), "BDC"));
    }

    [TestMethod]
    public void ArtifactsPauseTheOpenTag()
    {
        PdfDocument doc = Empty();
        PdfFont font = Font();
        using (PdfGraphics g = doc.Pages[0].CreateGraphics())
        {
            g.BeginTag(PdfStructureType.P);
            g.DrawString("To", font, 11, PdfColor.Black, 72, 72);
            g.BeginArtifact();
            g.DrawLine(new PdfPen(PdfColor.Black, 1), 72, 80, 200, 80);
            g.EndArtifact();
            g.DrawString("Vox", font, 11, PdfColor.Black, 100, 72);
            g.EndTag();
        }

        PdfDocument back = PdfDocument.Open(doc.ToArray(Ua));
        Assert.AreEqual("P(0,1)", Tree(back, Find(back, "P"), mcids: true));
        StringAssert.Matches(Content(back, 0), new Regex(@"/P\s*<</MCID 0>>\s*BDC.*?EMC\s*/Artifact\s*BMC.*?EMC\s*/P\s*<</MCID 1>>\s*BDC.*?EMC", RegexOptions.Singleline));
    }

    [TestMethod]
    public void MisuseIsRefusedWhileDrawing()
    {
        PdfDocument doc = Empty();
        using PdfGraphics g = doc.Pages[0].CreateGraphics();
        Assert.ThrowsExactly<InvalidOperationException>(g.EndTag);
        Assert.ThrowsExactly<InvalidOperationException>(g.EndArtifact);
        g.BeginArtifact();
        Assert.ThrowsExactly<InvalidOperationException>(() => g.BeginTag(PdfStructureType.P));
        g.EndArtifact();

        // A refused tag leaves nothing behind in the tree.
        Assert.IsNull(doc.Structure.Root.Dictionary["K"]);
        g.BeginTag(PdfStructureType.P);
        g.BeginArtifact();
        Assert.ThrowsExactly<InvalidOperationException>(g.EndTag);
        g.EndArtifact();
        Assert.ThrowsExactly<InvalidOperationException>(g.EndArtifact);
        g.EndTag();

        // Tags belong on pages; elements belong to their own document; names are plain.
        PdfForm form = PdfForm.Create(doc, 50, 50);
        using (PdfGraphics f = form.CreateGraphics())
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => f.BeginTag(PdfStructureType.Figure));
            f.BeginArtifact();
            f.EndArtifact();
        }

        PdfStructureElement foreign = Empty().Structure.Root.Add(PdfStructureType.P);
        Assert.ThrowsExactly<ArgumentException>(() => g.BeginTag(foreign));
        Assert.ThrowsExactly<ArgumentException>(() => g.BeginTag("Two words"));
        Assert.ThrowsExactly<ArgumentException>(() => doc.Structure.MapRole("P", PdfStructureType.Span));
    }

    [TestMethod]
    public void OpenSequencesCloseOnDispose()
    {
        PdfDocument doc = Empty();
        using (PdfGraphics g = doc.Pages[0].CreateGraphics())
        {
            g.BeginTag(PdfStructureType.P);
            g.DrawString("To", Font(), 11, PdfColor.Black, 72, 72);
            g.BeginArtifact();
            g.DrawLine(new PdfPen(PdfColor.Black, 1), 72, 80, 200, 80);
        }

        string content = Content(PdfDocument.Open(doc.ToArray()), 0);
        Assert.AreEqual(Regex.Count(content, @"\b(BDC|BMC)\b"), Regex.Count(content, @"\bEMC\b"));
    }

    [TestMethod]
    public void ElementsContinueOnTheNextPage()
    {
        PdfDocument doc = Empty();
        doc.Pages.Add(PdfRectangle.A4);
        PdfFont font = Font();
        PdfStructureElement paragraph = doc.Structure.Root.Add(PdfStructureType.P);
        foreach (PdfPage page in doc.Pages)
        {
            using PdfGraphics g = page.CreateGraphics();
            g.BeginTag(paragraph);
            g.DrawString("To Vox", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        PdfDocument back = PdfDocument.Open(doc.ToArray(Ua));
        Assert.AreEqual("P(0,MCR0)", Tree(back, Find(back, "P"), mcids: true));
        PdfDictionary p = Find(back, "P");
        var mcr = (PdfDictionary)back.Resolve(((PdfArray)back.Resolve(p["K"])!)[1])!;
        Assert.AreSame(back.Pages[1].Dictionary, back.Resolve(mcr["Pg"]));
        Assert.AreSame(back.Pages[0].Dictionary, back.Resolve(p["Pg"]));
    }

    [TestMethod]
    public void RefusesWhatItCanCheck()
    {
        PdfDocument doc = Empty(title: null, language: null);
        PdfPage page = doc.Pages[0];
        PdfFont font = Font();
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("To", font, 11, PdfColor.Black, 72, 40);
            g.BeginTag(PdfStructureType.H2);
            g.DrawString("Ho", font, 11, PdfColor.Black, 72, 60);
            g.EndTag();
            g.BeginTag(PdfStructureType.H4);
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 80);
            g.EndTag();
            g.BeginTag(PdfStructureType.Figure);
            g.DrawRectangle(null, PdfColor.Black, 72, 100, 10, 10);
            g.EndTag();
            g.BeginTag(PdfStructureType.P);
            g.BeginTag(PdfStructureType.TD);
            g.DrawString("Ho", font, 11, PdfColor.Black, 72, 130);
            g.EndTag();
            g.EndTag();
            g.BeginTag(PdfStructureType.Table);
            g.BeginTag(PdfStructureType.TR);
            g.BeginTag(PdfStructureType.TH);
            g.DrawString("To", font, 11, PdfColor.Black, 72, 150);
            g.EndTag();
            g.EndTag();
            g.EndTag();
            g.BeginTag("Subtitle");
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 170);
            g.EndTag();
            g.BeginTag(PdfStructureType.Span);
            g.DrawString("Ho", PdfStandardFont.Helvetica, 11, PdfColor.Black, 72, 190);
            g.EndTag();
        }

        page.Annotations.AddLink(72, 200, 40, 12, new Uri("https://example.com/"));
        string message = Failures(doc);
        foreach (string checkpoint in new[] { "06-003", "11-001", "01-005", "14-002", "14-003", "13-004", "09-004", "15-003", "02-001", "28-011", "28-012", "31-009" })
        {
            StringAssert.Contains(message, "Matterhorn " + checkpoint, message);
        }

        StringAssert.Contains(message, "Content on page 1 (1 drawing operation) is neither tagged nor marked as an artifact");
        StringAssert.Contains(message, "TD on page 1 must be inside TR, not P");
    }

    [TestMethod]
    public void RefusesAnUntaggedDocument()
    {
        PdfDocument doc = Empty();
        using (PdfGraphics g = doc.Pages[0].CreateGraphics()) g.DrawString("To", Font(), 11, PdfColor.Black, 72, 72);
        string message = Failures(doc);
        StringAssert.Contains(message, "not tagged");
        StringAssert.Contains(message, "01-005");
    }

    [TestMethod]
    public void MappedRolesNotesAndIdsAreWritten()
    {
        PdfDocument doc = Empty();
        doc.Structure.MapRole("Subtitle", PdfStructureType.P);
        PdfFont font = Font();
        PdfStructureElement note;
        using (PdfGraphics g = doc.Pages[0].CreateGraphics())
        {
            PdfStructureElement subtitle = g.BeginTag("Subtitle");
            subtitle.Language = "fr";
            subtitle.Id = "subtitle";
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
            note = g.BeginTag(PdfStructureType.Note);
            g.DrawString("A", font, 8, PdfColor.Black, 72, 800);
            g.EndTag();
        }

        Assert.AreEqual("note-1", note.Id);
        Assert.ThrowsExactly<ArgumentException>(() => note.Id = "subtitle");
        PdfDocument back = PdfDocument.Open(doc.ToArray(Ua));
        Assert.AreEqual("P", ((PdfDictionary)back.Resolve(Root(back)["RoleMap"])!).GetName("Subtitle"));
        Assert.AreEqual("fr", ((PdfString)Find(back, "Subtitle")["Lang"]!).ToText());
        var ids = (PdfArray)((PdfDictionary)back.Resolve(Root(back)["IDTree"])!)["Names"]!;
        CollectionAssert.AreEqual(new[] { "note-1", "subtitle" }, ids.Items.Where((_, i) => i % 2 == 0).Select(s => ((PdfString)s).ToText()).ToArray());

        // An element without its mapping is refused.
        PdfDocument unmapped = Empty();
        using (PdfGraphics g = unmapped.Pages[0].CreateGraphics())
        {
            g.BeginTag("Aside");
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        StringAssert.Contains(Failures(unmapped), "Aside is not mapped");
    }

    [TestMethod]
    public void CombinesWithPdfAAndFacturX()
    {
        PdfDocument doc = Tagged();
        byte[] xml = "<?xml version=\"1.0\"?><rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"/>"u8.ToArray();
        PdfInvoice.Attach(doc, xml, PdfInvoiceProfile.EN16931);
        PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U, Accessibility = PdfAccessibility.PdfUA1 }));

        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "<pdfaid:part>3</pdfaid:part>");
        StringAssert.Contains(xmp, "<pdfuaid:part>1</pdfuaid:part>");
        Assert.AreEqual(1, Regex.Count(xmp, "<pdfaExtension:schemas>"));
        StringAssert.Contains(xmp, "<pdfaSchema:prefix>fx</pdfaSchema:prefix>");
        StringAssert.Contains(xmp, "<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>");
        var parsed = new System.Xml.XmlDocument { XmlResolver = null };
        parsed.LoadXml(xmp.Substring(xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal), xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12 - xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal)));

        Assert.IsTrue(PdfInvoice.TryRead(back, out PdfInvoiceData? invoice));
        Assert.AreEqual(PdfInvoiceProfile.EN16931, invoice.Profile);
        Assert.IsEmpty(invoice.Warnings);

        // PDF/A-4 needs no extension schema for pdfuaid.
        string four = Xmp(PdfDocument.Open(Tagged().ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4, Accessibility = PdfAccessibility.PdfUA1 })));
        StringAssert.Contains(four, "<pdfuaid:part>1</pdfuaid:part>");
        Assert.DoesNotContain("pdfaExtension", four);
    }

    [TestMethod]
    public void AnOpenedTreeIsKeptAndExtended()
    {
        PdfDocument first = PdfDocument.Open(Tagged().ToArray(Ua));
        PdfFont font = Font();

        // More content on the tagged page continues its MCIDs; a new page gets its own parent tree entry.
        using (PdfGraphics g = first.Pages[0].CreateGraphics())
        {
            g.BeginTag(PdfStructureType.P);
            g.DrawString("To", font, 11, PdfColor.Black, 72, 400);
            g.EndTag();
        }

        PdfPage added = first.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = added.CreateGraphics())
        {
            g.BeginTag(PdfStructureType.H2);
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        PdfDocument back = PdfDocument.Open(first.ToArray(Ua));
        Assert.HasCount(1, (PdfArray)back.Resolve(Root(back)["K"])!);
        Assert.AreEqual("Document(H1,P(Link(OBJR)),Figure,Table(TR(TH),TR(TD)),L(LI(Lbl,LBody)),P,H2)", Tree(back, Document(back)));
        int[] mcids = [.. Regex.Matches(Content(back, 0), @"/MCID\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.HasCount(mcids.Length, mcids.Distinct());
        Assert.AreEqual(mcids.Length - 1, mcids.Max());
        Assert.AreNotEqual(((PdfNumber)back.Pages[0].Dictionary["StructParents"]!).IntegerValue, ((PdfNumber)back.Pages[1].Dictionary["StructParents"]!).IntegerValue);
    }

    [TestMethod]
    public void JoinsATreeAnotherWriterAddedMeanwhile()
    {
        // Tagging starts on an empty document; then a tree arrives with a tagged page, as a page
        // import that keeps tags would bring, its parent tree key colliding with the one given here.
        PdfDocument doc = Empty();
        PdfFont font = Font();
        using (PdfGraphics g = doc.Pages[0].CreateGraphics())
        {
            g.BeginTag(PdfStructureType.P);
            g.DrawString("To", font, 11, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        PdfPage imported = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = imported.CreateGraphics())
        {
            g.BeginMarkedContent("P", 0);
            g.DrawString("Vox", font, 11, PdfColor.Black, 72, 72);
            g.EndMarkedContent();
        }

        imported.Dictionary.Set("StructParents", PdfNumber.FromInteger(0));
        var root = new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot") };
        PdfReference rootReference = doc.AddObject(root);
        var document = new PdfDictionary { ["Type"] = new PdfName("StructElem"), ["S"] = new PdfName("Document"), ["P"] = rootReference };
        PdfReference documentReference = doc.AddObject(document);
        var paragraph = new PdfDictionary { ["Type"] = new PdfName("StructElem"), ["S"] = new PdfName("P"), ["P"] = documentReference, ["Pg"] = new PdfReference(imported.Number, 0), ["K"] = PdfNumber.FromInteger(0) };
        PdfReference paragraphReference = doc.AddObject(paragraph);
        document.Set("K", PdfArray.Of(paragraphReference));
        root.Set("K", PdfArray.Of(documentReference));
        root.Set("ParentTree", new PdfDictionary { ["Nums"] = PdfArray.Of(PdfNumber.FromInteger(0), doc.AddObject(PdfArray.Of(paragraphReference))) });
        root.Set("ParentTreeNextKey", PdfNumber.FromInteger(1));
        doc.Catalog.Set("StructTreeRoot", rootReference);

        PdfDocument back = PdfDocument.Open(doc.ToArray(Ua));
        Assert.AreEqual("Document(P),Document(P)", string.Join(",", ((PdfArray)back.Resolve(Root(back)["K"])!).Select(k => Tree(back, k))));
        Assert.AreEqual(0, ((PdfNumber)back.Pages[1].Dictionary["StructParents"]!).IntegerValue);
        Assert.AreEqual(1, ((PdfNumber)back.Pages[0].Dictionary["StructParents"]!).IntegerValue);
        Assert.AreEqual(2, ((PdfNumber)Root(back)["ParentTreeNextKey"]!).IntegerValue);
    }

    [TestMethod]
    public void NewEntriesAreAppendedToALayeredParentTree()
    {
        // A file whose parent tree is a root with kids, as large trees are written.
        PdfDocument source = PdfDocument.Open(Tagged().ToArray(Ua));
        var tree = (PdfDictionary)source.Resolve(Root(source)["ParentTree"])!;
        var nums = (PdfArray)source.Resolve(tree["Nums"])!;
        PdfReference leaf = source.AddObject(new PdfDictionary { ["Limits"] = PdfArray.Of(nums[0], nums[nums.Count - 2]), ["Nums"] = nums });
        Root(source).Set("ParentTree", new PdfDictionary { ["Kids"] = PdfArray.Of(leaf) });
        PdfDocument layered = PdfDocument.Open(source.ToArray());

        PdfPage added = layered.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = added.CreateGraphics())
        {
            g.BeginTag(PdfStructureType.P);
            g.DrawString("Vox", Font(), 11, PdfColor.Black, 72, 72);
            g.EndTag();
        }

        PdfDocument back = PdfDocument.Open(layered.ToArray(Ua));
        var kids = (PdfArray)back.Resolve(((PdfDictionary)back.Resolve(Root(back)["ParentTree"])!)["Kids"])!;
        Assert.HasCount(2, kids);
        var appended = (PdfDictionary)back.Resolve(kids[1])!;
        long key = ((PdfNumber)back.Pages[1].Dictionary["StructParents"]!).IntegerValue;
        Assert.AreEqual(key, ((PdfNumber)((PdfArray)appended["Limits"]!)[0]).IntegerValue);
        Assert.AreEqual(key + 1, ((PdfNumber)Root(back)["ParentTreeNextKey"]!).IntegerValue);
    }

    [TestMethod]
    public void TagsAreWrittenWithoutPdfUA()
    {
        PdfDocument back = PdfDocument.Open(Tagged().ToArray());
        Assert.IsNotNull(back.Catalog["StructTreeRoot"]);
        Assert.IsTrue(((PdfDictionary)back.Resolve(back.Catalog["MarkInfo"])!)["Marked"] is PdfBoolean { Value: true });
        Assert.IsNull(back.Catalog["Metadata"]);

        // A document that never tags writes no tree, even when the structure was looked at.
        PdfDocument plain = Empty();
        _ = plain.Structure.Root;
        PdfDocument untouched = PdfDocument.Open(plain.ToArray());
        Assert.IsNull(untouched.Catalog["StructTreeRoot"]);
        Assert.IsNull(untouched.Catalog["MarkInfo"]);
    }

    [TestMethod]
    public void FormFieldsBelongInFormElements()
    {
        PdfDocument doc = Tagged();
        PdfTextField tagged = doc.Form.AddTextField(doc.Pages[0], "name", 300, 72, 150, 20, options: new PdfFieldOptions { ToolTip = "Your name" });
        doc.Structure.Root.Add(PdfStructureType.Form).AddAnnotation(tagged.Widgets[0]);
        doc.Form.AddTextField(doc.Pages[0], "city", 300, 100, 150, 20);

        // Only the untagged field fails, once for its tag and once for its description.
        string message = Failures(doc);
        Assert.AreEqual(1, Regex.Count(message, "28-010"), message);
        Assert.AreEqual(1, Regex.Count(message, "28-005"), message);
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Structure.Root.Add(PdfStructureType.Form).AddAnnotation(tagged.Widgets[0]));
    }

    [TestMethod]
    public void EncryptionMustAllowAccessibility()
    {
        PdfDocument doc = Tagged();
        doc.Security.Encrypt("user", "owner", PdfPermissions.Print);
        StringAssert.Contains(Failures(doc), "26-002");

        PdfDocument allowed = Tagged();
        allowed.Security.Encrypt("user", "owner", PdfPermissions.Print | PdfPermissions.ExtractForAccessibility);
        Assert.IsNotEmpty(allowed.ToArray(Ua));
    }
}
