using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Page import and the tagged structure it carries: repeated pages, failed imports, shared and hostile structure.</summary>
[TestClass]
public sealed class StructureHardeningTests
{
    private const string FontObject = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>";

    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>One tagged page: a paragraph and a link annotation tagged through an object reference.</summary>
    private static byte[] LinkPage()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(3, FontObject);
        b.SetStream(4, "", Ascii("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (text) Tj ET EMC"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/StructParents 0/Annots[9 0 R]>>");
        b.Set(9, "<</Type/Annot/Subtype/Link/Rect[72 600 172 620]/Border[0 0 0]/StructParent 1/A<</S/URI/URI(https://example.com/)>>/P 5 0 R>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 2>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[22 0 R 24 0 R]>>");
        b.Set(22, "<</Type/StructElem/S/P/P 21 0 R/Pg 5 0 R/K 0>>");
        b.Set(24, "<</Type/StructElem/S/Link/P 21 0 R/Pg 5 0 R/K<</Type/OBJR/Obj 9 0 R>>>>");
        b.Set(30, "<</Nums[0[22 0 R] 1 24 0 R]>>");
        return b.Build();
    }

    /// <summary>A page with a table whose data cell names its header cell through an attribute object shared by reference.</summary>
    private static byte[] SharedAttributeTable()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(3, FontObject);
        b.SetStream(4, "", Ascii("/TH <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Name) Tj ET EMC /TD <</MCID 1>> BDC BT /F1 12 Tf 72 680 Td (Ada) Tj ET EMC"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/StructParents 0>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 1/IDTree<</Names[(th1) 23 0 R]>>>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K 22 0 R>>");
        b.Set(22, "<</Type/StructElem/S/Table/P 21 0 R/Pg 5 0 R/K[23 0 R 24 0 R]>>");
        b.Set(23, "<</Type/StructElem/S/TH/P 22 0 R/Pg 5 0 R/ID(th1)/K 0>>");
        b.Set(24, "<</Type/StructElem/S/TD/P 22 0 R/Pg 5 0 R/A 31 0 R/K 1>>");
        b.Set(30, "<</Nums[0[23 0 R 24 0 R]]>>");
        b.Set(31, "<</O/Table/Headers[(th1)]>>");
        return b.Build();
    }

    /// <summary>A paragraph with a /Ref to the table that follows it.</summary>
    private static byte[] ReferenceToLaterSibling()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(3, FontObject);
        b.SetStream(4, "", Ascii("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (See) Tj ET EMC /Table <</MCID 1>> BDC BT /F1 12 Tf 72 680 Td (Data) Tj ET EMC"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/StructParents 0>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 1>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[22 0 R 23 0 R]>>");
        b.Set(22, "<</Type/StructElem/S/P/P 21 0 R/Pg 5 0 R/Ref[23 0 R]/K 0>>");
        b.Set(23, "<</Type/StructElem/S/Table/P 21 0 R/Pg 5 0 R/K 1>>");
        b.Set(30, "<</Nums[0[22 0 R 23 0 R]]>>");
        return b.Build();
    }

    /// <summary>
    /// Two pages paint the same form. The first does not tag it; the second tags it through a
    /// marked content reference.
    /// </summary>
    private static byte[] FormTaggedOnSecondPage()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R 7 0 R]/Count 2>>");
        b.Set(3, FontObject);
        b.SetStream(4, "", Ascii("q 1 0 0 1 72 400 cm /X0 Do Q"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</X0 8 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(6, "", Ascii("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (text) Tj ET EMC q 1 0 0 1 72 400 cm /X0 Do Q"));
        b.Set(7, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>/XObject<</X0 8 0 R>>>>/Contents 6 0 R/StructParents 0>>");
        b.SetStream(8, "/Type/XObject/Subtype/Form/BBox[0 0 100 100]/StructParents 1/Resources<<>>", Ascii("/Figure <</MCID 0>> BDC 0 0 50 50 re f EMC"));
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 2>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[22 0 R 25 0 R]>>");
        b.Set(22, "<</Type/StructElem/S/P/P 21 0 R/Pg 7 0 R/K 0>>");
        b.Set(25, "<</Type/StructElem/S/Figure/P 21 0 R/Alt(Logo)/K<</Type/MCR/Pg 7 0 R/Stm 8 0 R/MCID 0>>>>");
        b.Set(30, "<</Nums[0[22 0 R] 1[25 0 R]]>>");
        return b.Build();
    }

    /// <summary>Two pages; the first has a link to the second and a link to itself.</summary>
    private static byte[] LinkedPages()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R 7 0 R]/Count 2>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Annots[9 0 R 10 0 R]>>");
        b.Set(7, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(9, "<</Type/Annot/Subtype/Link/Rect[72 600 172 620]/Border[0 0 0]/Dest[7 0 R/Fit]>>");
        b.Set(10, "<</Type/Annot/Subtype/Link/Rect[72 500 172 520]/Border[0 0 0]/Dest[5 0 R/Fit]>>");
        return b.Build();
    }

    /// <summary>The page each link annotation of <paramref name="page"/> goes to, null where it goes nowhere.</summary>
    private static int?[] LinkTargets(PdfDocument doc, PdfPage page) =>
        [.. ((PdfArray)doc.Resolve(page.Dictionary["Annots"])!).Select(a => ((PdfArray)doc.ResolveDictionary(a)!["Dest"]!)[0] is PdfReference r ? r.Number : (int?)null)];

    /// <summary>
    /// A tagged page with a text field, a custom paragraph type and an element ID, so importing it
    /// changes the target's form, role map, ID tree and parent tree.
    /// </summary>
    private static byte[] TaggedFieldPage()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R/MarkInfo<</Marked true>>/AcroForm<</Fields[11 0 R]>>>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(3, FontObject);
        b.SetStream(4, "", Ascii("/Para <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (text) Tj ET EMC"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/StructParents 0/Annots[11 0 R]>>");
        b.Set(11, "<</Type/Annot/Subtype/Widget/FT/Tx/T(name)/V(Ada)/Rect[72 600 272 620]/P 5 0 R/StructParent 1>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R/ParentTreeNextKey 2/RoleMap<</Para/P>>/IDTree<</Names[(p1) 22 0 R]>>>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[22 0 R 23 0 R]>>");
        b.Set(22, "<</Type/StructElem/S/Para/P 21 0 R/Pg 5 0 R/ID(p1)/K 0>>");
        b.Set(23, "<</Type/StructElem/S/Form/P 21 0 R/Pg 5 0 R/K<</Type/OBJR/Obj 11 0 R>>>>");
        b.Set(30, "<</Nums[0[22 0 R] 1 23 0 R]>>");
        return b.Build();
    }

    /// <summary>A tagged target whose parent tree already uses the last two keys an integer holds.</summary>
    private static byte[] TargetWithKeysNearTheLimit()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(20, "<</Type/StructTreeRoot/ParentTree 30 0 R>>");
        b.Set(30, "<</Nums[2147483646 null 2147483647 null]>>");
        return b.Build();
    }

    [TestMethod]
    public void AnImportedPageListedTwiceIsTaggedTwiceWithItsOwnAnnotations()
    {
        PdfDocument source = PdfDocument.Open(LinkPage());
        PdfDocument target = PdfDocument.Create();

        IReadOnlyList<PdfPage> copies = target.Pages.Import([source.Pages[0], source.Pages[0]]);
        Assert.HasCount(2, copies);

        PdfDocument saved = StructureImportTests.Saved(target);
        Assert.AreEqual("Document(P,Link),Document(P,Link)", StructureImportTests.CheckStructure(saved));

        var annotations = new List<int>();
        for (int i = 0; i < 2; i++)
        {
            PdfPage page = saved.Pages[i];
            Assert.IsNotNull(page.Dictionary["StructParents"], $"page {i} is tagged");
            var annots = (PdfArray)saved.Resolve(page.Dictionary["Annots"])!;
            annotations.Add(((PdfReference)annots[0]).Number);
            Assert.AreEqual(page.Number, ((PdfReference)saved.ResolveDictionary(annots[0])!["P"]!).Number, "the annotation names its own page");
        }

        Assert.AreNotEqual(annotations[0], annotations[1], "each page has annotations of its own");
    }

    [TestMethod]
    public void ARepeatedPageLinksToTheCopiesOfThePagesImportedWithIt()
    {
        PdfDocument source = PdfDocument.Open(LinkedPages());
        PdfDocument target = PdfDocument.Create();

        IReadOnlyList<PdfPage> copies = target.Pages.Import([source.Pages[0], source.Pages[0], source.Pages[1]]);

        CollectionAssert.AreEqual(new int?[] { copies[2].Number, copies[0].Number }, LinkTargets(target, copies[0]), "the first copy");
        CollectionAssert.AreEqual(new int?[] { copies[2].Number, copies[1].Number }, LinkTargets(target, copies[1]), "the second copy");
    }

    [TestMethod]
    public void ARepeatedPageDuplicatedInItsDocumentLinksToTheCopiesMadeWithIt()
    {
        PdfDocument doc = PdfDocument.Open(LinkedPages());

        IReadOnlyList<PdfPage> copies = doc.Pages.Import([doc.Pages[0], doc.Pages[0], doc.Pages[1]]);

        CollectionAssert.AreEqual(new int?[] { copies[2].Number, copies[0].Number }, LinkTargets(doc, copies[0]), "the first copy");
        CollectionAssert.AreEqual(new int?[] { copies[2].Number, copies[1].Number }, LinkTargets(doc, copies[1]), "the second copy");
    }

    [TestMethod]
    public void ACopyOfACopyWithinTheDocumentIsTagged()
    {
        PdfDocument doc = PdfDocument.Open(StructureImportTests.Tagged("A"));
        PdfPage copy = doc.Pages.Import([doc.Pages[0]])[0];
        doc.Pages.Import([copy]);

        PdfDocument saved = StructureImportTests.Saved(doc);
        Assert.AreEqual(
            "Document(P(Span),Table(TH,TD),Heading(Link),Figure),Document(P(Span),Table(TH,TD)),Document(P(Span),Table(TH,TD))",
            StructureImportTests.CheckStructure(saved));
        Assert.IsNotNull(saved.Pages[3].Dictionary["StructParents"], "the second copy is tagged");
    }

    [TestMethod]
    public void ACancelledImportOfARepeatedPageLeavesTheTargetAsItWas()
    {
        // The first copy's contents fire the token, so the call stops as it starts the second copy.
        using var cancel = new CancellationTokenSource();
        PdfDocument source = PdfDocument.Open(TaggedFieldPage());
        var contents = (PdfStream)source.Objects[4];
        ReadOnlyMemory<byte> data = contents.Data;
        source.Objects[4] = new PdfStream(contents.Dictionary, () =>
        {
            cancel.Cancel();
            return data;
        }, data.Length);

        // Saved once, the target has a file ID, so saving it again gives the same bytes.
        byte[] original = PdfDocument.Open(StructureImportTests.Tagged("T")).ToArray(Plain);
        byte[] untouched = PdfDocument.Open(original).ToArray(Plain);
        PdfDocument target = PdfDocument.Open(original);
        int next = target.NextNumber;

        Assert.ThrowsExactly<OperationCanceledException>(() => target.Pages.Import([source.Pages[0], source.Pages[0]], null, cancel.Token));
        Assert.AreEqual(next, target.NextNumber);
        CollectionAssert.AreEqual(untouched, target.ToArray(Plain), "the target saves as it did before the call");

        source.Objects[4] = contents;
        target.Pages.Import([source.Pages[0], source.Pages[0]]);
        PdfDocument saved = StructureImportTests.Saved(target);
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure),Document(Para,Form),Document(Para,Form)", StructureImportTests.CheckStructure(saved));
        CollectionAssert.AreEqual(new[] { "name", "name_2" }, saved.Form.Fields.Select(f => f.Name).ToArray());
    }

    [TestMethod]
    public void RoleRenamesOfACancelledImportDoNotOutliveIt()
    {
        PdfDocument target = PdfDocument.Open(StructureImportTests.Tagged("T", headingRole: "H2", id: "t1"));
        PdfDocument s = PdfDocument.Open(StructureImportTests.Tagged("S", headingRole: "H1", id: "s1"));
        PdfDocument u = PdfDocument.Open(StructureImportTests.Tagged("U", headingRole: "P", id: "u1"));

        // The second page's contents are copied after the structure, so failing there is a
        // failure after the renames were worked out.
        var contents = (PdfStream)s.Objects[6];
        s.Objects[6] = new PdfStream(contents.Dictionary, () => throw new OperationCanceledException(), 1);
        Assert.ThrowsExactly<OperationCanceledException>(() => target.Pages.Import(s.Pages));
        s.Objects[6] = contents;

        target.Pages.Import(u.Pages);
        target.Pages.Import(s.Pages);

        PdfDocument saved = StructureImportTests.Saved(target);
        StructureImportTests.CheckStructure(saved);
        PdfDictionary root = saved.ResolveDictionary(saved.Catalog["StructTreeRoot"])!;
        PdfDictionary roleMap = saved.ResolveDictionary(root["RoleMap"])!;
        string role = StructureImportTests.Elements(saved).Last(e => e.GetName("S")!.StartsWith("Heading", StringComparison.Ordinal)).GetName("S")!;
        for (int i = 0; i < 8 && roleMap.GetName(role) is string next; i++) role = next;
        Assert.AreEqual("H1", role, "the retried source's custom type keeps its own mapping");
    }

    [TestMethod]
    public void ACancelledImportLeavesNoCopiedObjectsInTheTarget()
    {
        PdfDocument s = PdfDocument.Open(StructureImportTests.Tagged("S"));
        var contents = (PdfStream)s.Objects[6];
        s.Objects[6] = new PdfStream(contents.Dictionary, () => throw new OperationCanceledException(), 1);

        PdfDocument target = PdfDocument.Create();
        target.Pages.Add(PdfRectangle.A4);
        int objects = target.Objects.Count;
        int next = target.NextNumber;

        Assert.ThrowsExactly<OperationCanceledException>(() => target.Pages.Import(s.Pages));
        Assert.AreEqual(objects, target.Objects.Count);
        Assert.AreEqual(next, target.NextNumber);

        s.Objects[6] = contents;
        target.Pages.Import(s.Pages);
        Assert.AreEqual("Document(P(Span),Table(TH,TD),Heading(Link),Figure)", StructureImportTests.CheckStructure(StructureImportTests.Saved(target)));
    }

    [TestMethod]
    public void ElementsSharingOneKidsArrayAreReadOnce()
    {
        const int Elements = 4000;
        const int Kids = 4000;
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[" + string.Join(" ", Enumerable.Range(300, Elements).Select(n => n + " 0 R")) + "]>>");
        b.Set(30, "<</Nums[]>>");
        b.Set(100, "[" + string.Join(" ", Enumerable.Range(0, Kids)) + "]");
        for (int i = 0; i < Elements; i++) b.Set(300 + i, "<</Type/StructElem/S/P/P 21 0 R/Pg 5 0 R/K 100 0 R>>");

        PdfDocument source = PdfDocument.Open(b.Build());
        PdfDocument target = PdfDocument.Create();

#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        target.Pages.Import(source.Pages);
#if NET
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(64L * 1024 * 1024, allocated, "a shared kids array is not copied once per element");
#endif
        Assert.IsTrue(source.Diagnostics.Any(d => d.Kind == PdfDiagnosticKind.Skipped), "the skipped sharing is reported");
    }

    [TestMethod]
    public void ElementsSharingOneRefArrayAreGivenOneCopyOfIt()
    {
        const int Elements = 4000;
        string elements = string.Join(" ", Enumerable.Range(300, Elements).Select(n => n + " 0 R"));
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R>>");
        b.Set(21, "<</Type/StructElem/S/Document/P 20 0 R/K[" + elements + "]>>");
        b.Set(30, "<</Nums[]>>");
        b.Set(100, "[" + elements + "]");
        for (int i = 0; i < Elements; i++) b.Set(300 + i, "<</Type/StructElem/S/P/P 21 0 R/Pg 5 0 R/Ref 100 0 R/K 0>>");

        PdfDocument source = PdfDocument.Open(b.Build());
        PdfDocument target = PdfDocument.Create();

#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        target.Pages.Import(source.Pages);
#if NET
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(64L * 1024 * 1024, allocated, "a shared /Ref array is not copied once per element");
#endif
        List<PdfDictionary> paragraphs = [.. StructureImportTests.Elements(target).Where(e => e.GetName("S") == "P")];
        Assert.HasCount(Elements, paragraphs);
        var references = (PdfArray)target.Resolve(paragraphs[0]["Ref"])!;
        Assert.HasCount(Elements, references);
        Assert.AreSame(references, target.Resolve(paragraphs[^1]["Ref"]), "the copies share one array, as the originals do");
        Assert.AreSame(paragraphs[^1], target.ResolveDictionary(references[^1]), "the array names the copies");
    }

    [TestMethod]
    public void AKidsArrayThatContainsItselfThroughDirectElementsIsWalkedOnce()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(20, "<</Type/StructTreeRoot/K 40 0 R>>");
        b.Set(40, "[<</Type/StructElem/S/P/Pg 5 0 R/K 40 0 R>> <</Type/StructElem/S/P/Pg 5 0 R/K 40 0 R>>]");

        PdfDocument source = PdfDocument.Open(b.Build());
        PdfDocument target = PdfDocument.Create();
        Task import = Task.Run(() => target.Pages.Import(source.Pages));
        Assert.IsTrue(import.Wait(TimeSpan.FromSeconds(10)), "the import finishes");
        Assert.HasCount(1, target.Pages);
    }

    [TestMethod]
    public void MergingASourceTwiceKeepsEachTablesHeaderReference()
    {
        PdfDocument a = PdfDocument.Open(SharedAttributeTable());
        PdfDocument merged = StructureImportTests.Saved(PdfDocument.Merge([a, a]));
        StructureImportTests.CheckStructure(merged);

        List<PdfDictionary> cells = StructureImportTests.Elements(merged).Where(e => e.GetName("S") == "TD").ToList();
        Assert.HasCount(2, cells);
        string[] headers = [.. cells.Select(c => ((PdfString)((PdfArray)merged.Resolve(merged.ResolveDictionary(c["A"])!["Headers"])!)[0]).ToText())];
        Assert.AreEqual("th1,th1_2", string.Join(",", headers));
    }

    [TestMethod]
    public void CellsSharingOneAttributeObjectRenameItsHeadersOnce()
    {
        const int Cells = 4000;
        const int Headers = 4000;
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 20 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>");
        b.Set(20, "<</Type/StructTreeRoot/K 21 0 R/ParentTree 30 0 R>>");
        string kids = string.Join(" ", Enumerable.Range(10_000, Headers).Concat(Enumerable.Range(300, Cells)).Select(n => n + " 0 R"));
        b.Set(21, "<</Type/StructElem/S/Table/P 20 0 R/K[" + kids + "]>>");
        b.Set(30, "<</Nums[]>>");
        b.Set(31, "<</O/Table/Headers[" + string.Join(" ", Enumerable.Range(0, Headers).Select(i => "(h" + i + ")")) + "]>>");
        for (int i = 0; i < Headers; i++) b.Set(10_000 + i, "<</Type/StructElem/S/TH/P 21 0 R/Pg 5 0 R/ID(h" + i + ")/K 0>>");
        for (int i = 0; i < Cells; i++) b.Set(300 + i, "<</Type/StructElem/S/TD/P 21 0 R/Pg 5 0 R/A 31 0 R/K 0>>");

        PdfDocument source = PdfDocument.Open(b.Build());
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source.Pages);

        // The second copy finds every header ID taken, so each is renamed.
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        target.Pages.Import(source.Pages);
#if NET
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(64L * 1024 * 1024, allocated, "a shared attribute object is renamed once, not once per cell");
#endif
        List<PdfDictionary> cells = [.. StructureImportTests.Elements(target).Where(e => e.GetName("S") == "TD")];
        Assert.HasCount(2 * Cells, cells);
        string FirstHeader(PdfDictionary cell) => ((PdfString)((PdfArray)target.Resolve(target.ResolveDictionary(cell["A"])!["Headers"])!)[0]).ToText();
        Assert.AreEqual("h0", FirstHeader(cells[0]));
        Assert.AreEqual("h0_2", FirstHeader(cells[Cells]));
        Assert.AreSame(target.ResolveDictionary(cells[Cells]["A"]), target.ResolveDictionary(cells[^1]["A"]), "the second copy's cells share one renamed attribute object");
    }

    [TestMethod]
    public void AReferenceToALaterSiblingFollowsItsCopy()
    {
        PdfDocument source = PdfDocument.Open(ReferenceToLaterSibling());
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source.Pages);

        PdfDocument saved = StructureImportTests.Saved(target);
        StructureImportTests.CheckStructure(saved);
        var references = (PdfArray)saved.Resolve(StructureImportTests.Element(saved, "P")["Ref"])!;
        Assert.HasCount(1, references);
        Assert.AreSame(StructureImportTests.Element(saved, "Table"), saved.ResolveDictionary(references[0]));
    }

    [TestMethod]
    public void AFormTaggedByALaterImportGetsItsParentTreeKeyBack()
    {
        PdfDocument source = PdfDocument.Open(FormTaggedOnSecondPage());
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import([source.Pages[0]]);
        target.Pages.Import([source.Pages[1]]);

        PdfDocument saved = StructureImportTests.Saved(target);
        Assert.AreEqual("Document(P,Figure)", StructureImportTests.CheckStructure(saved));
    }

    [TestMethod]
    public void ParentTreeKeysNeverPassTheLargestInteger()
    {
        PdfDocument target = PdfDocument.Open(TargetWithKeysNearTheLimit());
        PdfDocument source = PdfDocument.Open(StructureImportTests.Tagged("A"));

        target.Pages.Import(source.Pages);

        for (int i = 1; i < target.Pages.Count; i++)
        {
            if (target.Pages[i].Dictionary["StructParents"] is not PdfNumber key) continue;
            Assert.IsTrue(key.IntegerValue is >= 0 and < int.MaxValue, "the key fits an integer");
            Assert.AreNotEqual(2147483646L, key.IntegerValue, "the key is not one the target already uses");
        }

        Assert.IsTrue(target.Diagnostics.Any(d => d.Kind == PdfDiagnosticKind.Skipped), "the untagged copy is reported");
    }
}
