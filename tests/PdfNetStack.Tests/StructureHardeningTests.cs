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
}
