using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Page import and the tagged structure it carries: repeated pages, failed imports, shared and hostile structure.</summary>
[TestClass]
public sealed class StructureHardeningTests
{
    private const string FontObject = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>";

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
}
