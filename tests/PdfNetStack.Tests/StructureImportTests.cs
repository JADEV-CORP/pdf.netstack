using System.Diagnostics;
using System.Text;
using PdfNetStack.Html;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Tagged PDFs keep their structure tree through merge, split, extraction and page import.</summary>
[TestClass]
public sealed class StructureImportTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

    /// <summary>
    /// A tagged two page document written by hand. Page 1: a paragraph with a span, and a table
    /// whose data cell names its header cell by ID. Page 2: a custom Heading (role mapped) holding
    /// a link annotation through an object reference, and a figure drawn by a form XObject,
    /// tagged through a marked content reference.
    /// </summary>
    private static byte[] Tagged(string text, string language = "en-US", string headingRole = "H1", string id = "p1")
    {
        var b = new PdfBuilder();
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>/Lang({language})>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R 7 0 R]/Count 2>>");
        b.Set(3, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(
            $"/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td ({text} one) Tj ET EMC " +
            "/Span <</MCID 1>> BDC BT /F1 12 Tf 72 680 Td (span) Tj ET EMC " +
            "/TH <</MCID 2>> BDC BT /F1 12 Tf 72 640 Td (Name) Tj ET EMC " +
            "/TD <</MCID 3>> BDC BT /F1 12 Tf 72 620 Td (Ada) Tj ET EMC"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/StructParents 0>>");
        b.SetStream(6, "", Encoding.ASCII.GetBytes($"/Heading <</MCID 0>> BDC BT /F1 18 Tf 72 700 Td ({text} two) Tj ET EMC q 1 0 0 1 72 400 cm /X0 Do Q"));
        b.Set(7, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>/XObject<</X0 8 0 R>>>>/Contents 6 0 R/StructParents 1/Annots[9 0 R]>>");
        b.SetStream(8, "/Type/XObject/Subtype/Form/BBox[0 0 100 100]/StructParents 3/Resources<<>>", Encoding.ASCII.GetBytes("/Figure <</MCID 0>> BDC 0 0 50 50 re f EMC"));
        b.Set(9, "<</Type/Annot/Subtype/Link/Rect[72 600 172 620]/Border[0 0 0]/StructParent 2/A<</S/URI/URI(https://example.com/)>>/P 7 0 R>>");

        b.Set(20, $"<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 4/RoleMap<</Heading/{headingRole}>>/ClassMap<</Bold<</O/Layout/TextAlign/Center>>>>/IDTree<</Names[({id}) 22 0 R(th1) 28 0 R]>>>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[22 0 R 27 0 R 24 0 R 25 0 R]>>");
        b.Set(22, $"<</Type/StructElem/S/P/P 21 0 R/Pg 5 0 R/ID({id})/C/Bold/K[0 23 0 R]>>");
        b.Set(23, "<</Type/StructElem/S/Span/P 22 0 R/Pg 5 0 R/K 1>>");
        b.Set(24, "<</Type/StructElem/S/Heading/P 21 0 R/Pg 7 0 R/K[0 26 0 R]>>");
        b.Set(25, "<</Type/StructElem/S/Figure/P 21 0 R/Alt(Logo)/K<</Type/MCR/Pg 7 0 R/Stm 8 0 R/MCID 0>>>>");
        b.Set(26, "<</Type/StructElem/S/Link/P 24 0 R/Pg 7 0 R/K<</Type/OBJR/Obj 9 0 R>>>>");
        b.Set(27, "<</Type/StructElem/S/Table/P 21 0 R/Pg 5 0 R/K[28 0 R 29 0 R]>>");
        b.Set(28, "<</Type/StructElem/S/TH/P 27 0 R/Pg 5 0 R/ID(th1)/K 2>>");
        b.Set(29, "<</Type/StructElem/S/TD/P 27 0 R/Pg 5 0 R/A<</O/Table/Headers[(th1)]>>/K 3>>");
        b.Set(30, "<</Nums[0[22 0 R 23 0 R 28 0 R 29 0 R] 1[24 0 R] 2 26 0 R 3[25 0 R]]>>");
        return b.Build();
    }

    private static PdfDocument Saved(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(Plain);
        QpdfCheck(bytes);
        return PdfDocument.Open(bytes);
    }

    /// <summary>
    /// Reads the structure tree back and checks it as a validator does: every element names its
    /// parent, its IDs are in the ID tree and unique, its type is standard or role mapped, and
    /// every marked content sequence, annotation and XObject the tree holds maps back to the same
    /// element through the parent tree, which holds nothing else. Returns the tree as types and
    /// children, such as Document(P(Span),H1).
    /// </summary>
    private static string CheckStructure(PdfDocument doc)
    {
        var rootReference = (PdfReference)doc.Catalog["StructTreeRoot"]!;
        PdfDictionary root = doc.ResolveDictionary(rootReference)!;
        PdfDictionary roleMap = doc.ResolveDictionary(root["RoleMap"]) ?? new PdfDictionary();

        var parentTree = new Dictionary<long, PdfObject>();
        void Numbers(PdfDictionary node)
        {
            if (doc.Resolve(node["Nums"]) is PdfArray nums)
            {
                for (int i = 0; i + 1 < nums.Count; i += 2) parentTree.Add(((PdfNumber)nums[i]).IntegerValue, nums[i + 1]);
            }

            if (doc.Resolve(node["Kids"]) is PdfArray kids)
            {
                foreach (PdfObject kid in kids) Numbers(doc.ResolveDictionary(kid)!);
            }
        }

        if (doc.ResolveDictionary(root["ParentTree"]) is PdfDictionary tree) Numbers(tree);
        long nextKey = doc.Resolve(root["ParentTreeNextKey"]) is PdfNumber n ? n.IntegerValue : 0;
        Assert.IsTrue(parentTree.Keys.All(k => k < nextKey), "ParentTreeNextKey is above every key");

        var content = new Dictionary<(int Owner, long Mcid), int>();
        var objects = new Dictionary<int, int>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);

        string Walk(PdfReference reference, PdfReference parent, PdfReference? page, int depth)
        {
            Assert.IsLessThan(64, depth);
            PdfDictionary e = doc.ResolveDictionary(reference)!;
            Assert.AreEqual(parent.Number, ((PdfReference)e["P"]!).Number, "an element names its parent");
            string type = e.GetName("S")!;
            Assert.IsTrue(StandardTypes.Contains(type) || roleMap.ContainsKey(type), $"{type} is standard or role mapped");
            if (doc.Resolve(e["ID"]) is PdfString id)
            {
                Assert.IsTrue(ids.TryAdd(id.ToText(), reference.Number), $"ID {id.ToText()} is unique");
            }

            page = e["Pg"] as PdfReference ?? page;
            IEnumerable<PdfObject> kids = e["K"] is PdfArray a ? a : e["K"] is PdfObject k ? [k] : [];
            var children = new List<string>();
            foreach (PdfObject kid in kids)
            {
                switch (kid)
                {
                    case PdfNumber mcid:
                        Assert.IsNotNull(page, "an MCID has a page");
                        content.Add((page.Number, mcid.IntegerValue), reference.Number);
                        break;
                    case PdfReference child when doc.ResolveDictionary(child)?.GetName("Type") is not ("MCR" or "OBJR"):
                        children.Add(Walk(child, reference, page, depth + 1));
                        break;
                    default:
                        PdfDictionary item = doc.ResolveDictionary(kid)!;
                        if (item.GetName("Type") == "MCR")
                        {
                            var owner = (PdfReference?)item["Stm"] ?? (PdfReference?)item["Pg"] ?? page!;
                            content.Add((owner.Number, ((PdfNumber)item["MCID"]!).IntegerValue), reference.Number);
                        }
                        else
                        {
                            objects.Add(((PdfReference)item["Obj"]!).Number, reference.Number);
                        }

                        break;
                }
            }

            return children.Count == 0 ? type : $"{type}({string.Join(",", children)})";
        }

        IEnumerable<PdfObject> tops = root["K"] is PdfArray t ? t : doc.Resolve(root["K"]) is PdfArray ta ? ta : [root["K"]!];
        string shape = string.Join(",", tops.Select(top => Walk((PdfReference)top, rootReference, null, 0)));

        // Every content owner's parent tree array matches the tree, entry for entry.
        var owners = new Dictionary<int, long>();
        var claimed = new HashSet<long>();
        foreach (PdfPage page in doc.Pages)
        {
            if (doc.Resolve(page.Dictionary["StructParents"]) is PdfNumber k) owners[page.Number] = k.IntegerValue;
            if (doc.ResolveDictionary(doc.ResolveDictionary(page.Dictionary["Resources"])?["XObject"]) is PdfDictionary xobjects)
            {
                foreach (KeyValuePair<string, PdfObject> x in xobjects)
                {
                    if (doc.ResolveDictionary(x.Value) is PdfDictionary xd && doc.Resolve(xd["StructParents"]) is PdfNumber xk) owners[((PdfReference)x.Value).Number] = xk.IntegerValue;
                }
            }

            if (doc.Resolve(page.Dictionary["Annots"]) is PdfArray annots)
            {
                foreach (PdfObject annot in annots)
                {
                    if (doc.ResolveDictionary(annot)?["StructParent"] is not PdfNumber ak) continue;
                    Assert.IsTrue(claimed.Add(ak.IntegerValue), "parent tree keys are not shared");
                    Assert.AreEqual(objects[((PdfReference)annot).Number], ((PdfReference)parentTree[ak.IntegerValue]).Number, "the annotation's element");
                }
            }
        }

        foreach ((int owner, long key) in owners)
        {
            Assert.IsTrue(claimed.Add(key), "parent tree keys are not shared");
            var array = (PdfArray)doc.Resolve(parentTree[key])!;
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is PdfReference r) Assert.AreEqual(content[(owner, i)], r.Number, $"MCID {i} of {owner}");
            }
        }

        foreach (((int owner, long mcid), int element) in content)
        {
            Assert.IsTrue(owners.TryGetValue(owner, out long key), $"content owner {owner} has a parent tree key");
            Assert.AreEqual(element, ((PdfReference)((PdfArray)doc.Resolve(parentTree[key])!)[(int)mcid]).Number);
        }

        Assert.AreEqual(objects.Count, doc.Pages.Sum(p => doc.Resolve(p.Dictionary["Annots"]) is PdfArray a ? a.Count(x => doc.ResolveDictionary(x)?["StructParent"] is not null) : 0));

        // The ID tree lists exactly the elements' IDs.
        var listed = new Dictionary<string, int>(StringComparer.Ordinal);
        if (doc.ResolveDictionary(root["IDTree"]) is PdfDictionary idTree && doc.Resolve(idTree["Names"]) is PdfArray names)
        {
            for (int i = 0; i + 1 < names.Count; i += 2) listed.Add(((PdfString)names[i]).ToText(), ((PdfReference)names[i + 1]).Number);
            for (int i = 2; i + 1 < names.Count; i += 2) Assert.IsLessThan(0, string.CompareOrdinal(((PdfString)names[i - 2]).ToText(), ((PdfString)names[i]).ToText()), "ID tree keys are sorted");
        }

        CollectionAssert.AreEquivalent(ids.ToList(), listed.ToList());
        return shape;
    }

    private static PdfDictionary Element(PdfDocument doc, string type) => Elements(doc).First(e => e.GetName("S") == type);

    private static List<PdfDictionary> Elements(PdfDocument doc)
    {
        var all = new List<PdfDictionary>();
        void Walk(PdfObject? node)
        {
            if (doc.ResolveDictionary(node) is not PdfDictionary e || e.GetName("Type") is "MCR" or "OBJR") return;
            if (e.GetName("S") is not null) all.Add(e);
            if (doc.Resolve(e["K"]) is PdfArray kids) foreach (PdfObject k in kids) Walk(k);
            else if (e["K"] is PdfReference single) Walk(single);
        }

        Walk(doc.Catalog["StructTreeRoot"]);
        return all;
    }

    private static string? Qpdf { get; } = new[] { "/opt/homebrew/bin/qpdf", "/usr/local/bin/qpdf", "/usr/bin/qpdf" }.FirstOrDefault(File.Exists);

    /// <summary>qpdf --check, where qpdf is installed: syntax, cross-reference and object streams.</summary>
    private static void QpdfCheck(byte[] pdf)
    {
        if (Qpdf is null) return;
        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-structure-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            using var process = Process.Start(new ProcessStartInfo(Qpdf, ["--check", path]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TheHandWrittenSourceIsConsistent()
    {
        PdfDocument doc = PdfDocument.Open(Tagged("A"));
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure)", CheckStructure(doc));
    }

    [TestMethod]
    public void MergeKeepsTheTagsOfEverySource()
    {
        PdfDocument a = PdfDocument.Open(Tagged("A"));
        PdfDocument b = PdfDocument.Open(Tagged("B", language: "fr-FR", headingRole: "P"));

        PdfDocument merged = Saved(PdfDocument.Merge([a, b]));
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure),Document(P(Span),Table(TH,TD),Heading_2(Link),Figure)", CheckStructure(merged));
        CollectionAssert.AreEqual(new[] { "A one\nspan\nName\nAda", "A two", "B one\nspan\nName\nAda", "B two" }, merged.Pages.Select(p => p.ExtractText()).ToArray());

        // The second source's custom type maps where it did, under a name of its own.
        PdfDictionary root = merged.ResolveDictionary(merged.Catalog["StructTreeRoot"])!;
        PdfDictionary roles = merged.ResolveDictionary(root["RoleMap"])!;
        Assert.AreEqual("H1", roles.GetName("Heading"));
        Assert.AreEqual("P", roles.GetName("Heading_2"));

        // Classes clash by name, so the second's is renamed and its elements follow.
        PdfDictionary classes = merged.ResolveDictionary(root["ClassMap"])!;
        Assert.IsTrue(classes.ContainsKey("Bold") && classes.ContainsKey("Bold_2"));
        List<PdfDictionary> paragraphs = [.. Elements(merged).Where(e => e.GetName("S") == "P")];
        CollectionAssert.AreEqual(new[] { "Bold", "Bold_2" }, paragraphs.Select(p => p.GetName("C")).ToArray());

        // IDs stay unique, and the second table's data cell follows its header's new ID.
        CollectionAssert.AreEqual(new[] { "p1", "p1_2" }, paragraphs.Select(p => ((PdfString)merged.Resolve(p["ID"])!).ToText()).ToArray());
        List<PdfDictionary> headers = [.. Elements(merged).Where(e => e.GetName("S") == "TH")];
        List<PdfDictionary> cells = [.. Elements(merged).Where(e => e.GetName("S") == "TD")];
        Assert.AreEqual("th1_2", ((PdfString)merged.Resolve(headers[1]["ID"])!).ToText());
        Assert.AreEqual("th1_2", ((PdfString)((PdfArray)merged.ResolveDictionary(cells[1]["A"])!["Headers"]!)[0]).ToText());
        Assert.AreEqual("th1", ((PdfString)((PdfArray)merged.ResolveDictionary(cells[0]["A"])!["Headers"]!)[0]).ToText());

        // The first language is the document's; the second's is on its own top element.
        Assert.AreEqual("en-US", merged.Language);
        var tops = (PdfArray)merged.Resolve(root["K"])!;
        Assert.IsNull(merged.ResolveDictionary(tops[0])!["Lang"]);
        Assert.AreEqual("fr-FR", ((PdfString)merged.ResolveDictionary(tops[1])!["Lang"]!).ToText());
        Assert.IsTrue(merged.ResolveDictionary(merged.Catalog["MarkInfo"])!["Marked"] is PdfBoolean { Value: true });
        Assert.AreEqual("Logo", ((PdfString)Elements(merged).Last(e => e.GetName("S") == "Figure")["Alt"]!).ToText());
    }

    [TestMethod]
    public void SplitGivesEachPartTheStructureOfItsPage()
    {
        PdfDocument doc = PdfDocument.Open(Tagged("A"));
        List<PdfDocument> parts = [.. doc.Split().Select(Saved)];

        Assert.AreEqual("Document(P(Span),Table(TH,TD))", CheckStructure(parts[0]));
        Assert.AreEqual("Document(Heading(Link),Figure)", CheckStructure(parts[1]));
        Assert.AreEqual("en-US", parts[1].Language);

        // The form XObject and the link keep their place in the part's parent tree.
        PdfDictionary root = parts[1].ResolveDictionary(parts[1].Catalog["StructTreeRoot"])!;
        Assert.AreEqual(3, ((PdfNumber)root["ParentTreeNextKey"]!).IntegerValue);
        Assert.AreEqual("H1", parts[1].ResolveDictionary(root["RoleMap"])!.GetName("Heading"));
        Assert.IsNull(parts[1].ResolveDictionary(root["IDTree"]), "page 2 holds no element with an ID");
    }

    [TestMethod]
    public void UntaggedSourcesDoNotBreakTheTree()
    {
        PdfDocument plain = PdfDocument.Open(PdfBuilder.Simple("Plain").Build());
        PdfDocument tagged = PdfDocument.Open(Tagged("A"));

        PdfDocument merged = Saved(PdfDocument.Merge([plain, tagged, plain]));
        Assert.HasCount(4, merged.Pages);
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure)", CheckStructure(merged));
        Assert.IsNull(merged.Pages[0].Dictionary["StructParents"]);
        Assert.IsNull(merged.Pages[3].Dictionary["StructParents"]);

        // An XObject that names a parent tree key of a source whose tree stays behind loses the key.
        var b = PdfBuilder.Simple("Form");
        b.SetStream(30, "/Type/XObject/Subtype/Form/BBox[0 0 10 10]/StructParents 7", Encoding.ASCII.GetBytes("0 0 5 5 re f"));
        b.SetStream(4, "", Encoding.ASCII.GetBytes("/X0 Do"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</X0 30 0 R>>>>/Contents 4 0 R>>");
        PdfDocument stray = PdfDocument.Open(b.Build());
        PdfDocument mixed = Saved(PdfDocument.Merge([tagged, stray]));
        CheckStructure(mixed);
        var xobject = (PdfStream)mixed.Resolve(mixed.ResolveDictionary(mixed.ResolveDictionary(mixed.Pages[2].Dictionary["Resources"])!["XObject"])!["X0"])!;
        Assert.IsNull(xobject.Dictionary["StructParents"]);
    }

    [TestMethod]
    public void ExtractedAndImportedPagesJoinTheTargetsTree()
    {
        PdfDocument source = PdfDocument.Open(Tagged("S", headingRole: "H2"));
        using PdfDocument second = source.ExtractPages(1, 1);
        Assert.AreEqual("Document(Heading(Link),Figure)", CheckStructure(Saved(second)));

        // Into a tagged document: its tree gains a second top element, keys above its own.
        PdfDocument target = PdfDocument.Open(Tagged("T"));
        target.Pages.Import([source.Pages[1]]);
        PdfDocument back = Saved(target);
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure),Document(Heading_2(Link),Figure)", CheckStructure(back));
        PdfDictionary root = back.ResolveDictionary(back.Catalog["StructTreeRoot"])!;
        Assert.AreEqual(7, ((PdfNumber)root["ParentTreeNextKey"]!).IntegerValue);
        Assert.AreEqual("en-US", back.Language);
    }

    [TestMethod]
    public void DuplicatingATaggedPageTagsTheCopy()
    {
        PdfDocument doc = PdfDocument.Open(Tagged("A"));
        doc.Pages.Import([doc.Pages[0]]);
        PdfDocument back = Saved(doc);

        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure),Document(P(Span),Table(TH,TD))", CheckStructure(back));
        List<PdfDictionary> paragraphs = [.. Elements(back).Where(e => e.GetName("S") == "P")];
        Assert.IsNull(paragraphs[1]["ID"], "a copy within the document does not repeat its original's ID");
    }

    [TestMethod]
    public void MergedHtmlDocumentsStayTagged()
    {
        static PdfDocument Render(string html) => PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false, Tagged = true }));

        PdfDocument first = Render("""
            <html lang=en><body><h1>Report</h1><p>Read <a href="https://example.com/">the site</a> now.</p>
            <ul><li>one</li><li>two</li></ul></body></html>
            """);
        PdfDocument second = Render("""
            <html lang=nl><body><h2>Bijlage</h2>
            <table><thead><tr><th>Naam</th></tr></thead><tbody><tr><td>Ada</td></tr></tbody></table></body></html>
            """);

        string before = CheckStructure(first) + "," + CheckStructure(second);
        PdfDocument merged = Saved(PdfDocument.Merge([first, second]));
        Assert.AreEqual(before, CheckStructure(merged));
        Assert.AreEqual("en", merged.Language);
        var tops = (PdfArray)merged.Resolve(merged.ResolveDictionary(merged.Catalog["StructTreeRoot"])!["K"])!;
        Assert.AreEqual("nl", ((PdfString)merged.ResolveDictionary(tops[1])!["Lang"]!).ToText());
    }
}
