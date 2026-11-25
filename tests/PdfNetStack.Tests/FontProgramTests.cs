using System.Text;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Fonts.Type1;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Subsetting of fully embedded fonts and conversion of Type 1 programs to CFF, with their proofs.</summary>
[TestClass]
public sealed class FontProgramTests
{
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    /// <summary>Type 1 conversion is opt-in.</summary>
    private static readonly PdfOptimizerOptions Convert1 = NoThreshold with { ConvertType1Fonts = true };

    private static (byte[] Output, PdfOptimizationResult Result) Run(byte[] input, PdfOptimizerOptions? options = null) =>
        PdfOptimizer.Optimize(input, options ?? NoThreshold);

    private static PdfFile Open(byte[] pdf) => PdfFile.Open(pdf, 64 << 20);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name));

    private static void AssertVerified(byte[] input, byte[] output, PdfOptimizationResult result)
    {
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    private static byte[] Decoded(PdfFile file, PdfStream stream)
    {
        using Filters.DecodedStream d = Filters.StreamCodec.Decode(stream.Data, Filters.StreamCodec.GetFilters(stream.Dictionary, file.Resolve),
            Filters.DecodeDepth.Full, 64 << 20, file.Resolve);
        return d.Data.ToArray();
    }

    /// <summary>Every font descriptor of a file with its program key and decoded program.</summary>
    private static List<(PdfDictionary Descriptor, string Key, byte[] Program)> Programs(byte[] pdf)
    {
        PdfFile file = Open(pdf);
        var programs = new List<(PdfDictionary, string, byte[])>();
        foreach ((int number, XrefEntry e) in file.Xref)
        {
            if (e.Type == XrefEntryType.Free || file.GetObject(number) is not PdfDictionary d || d.GetName("Type") != "FontDescriptor") continue;
            foreach (string key in new[] { "FontFile", "FontFile2", "FontFile3" })
            {
                if (file.Resolve(d[key]) is PdfStream s) programs.Add((d, key, Decoded(file, s)));
            }
        }

        return programs;
    }

    private static string Widths(int count, int width = 600) => string.Join(" ", Enumerable.Repeat(width.ToString(System.Globalization.CultureInfo.InvariantCulture), count));

    /// <summary>One page showing <paramref name="content"/>; font 5 is <paramref name="font"/>, descriptor 6, program 7.</summary>
    private static PdfBuilder Page(string content, string font, string descriptor, byte[] program, string programDictionary, string catalogExtra = "", string pageExtra = "")
    {
        var b = new PdfBuilder();
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R{catalogExtra}>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>{pageExtra}>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(content));
        b.Set(5, font);
        b.Set(6, descriptor);
        b.SetStream(7, programDictionary + "/Filter/FlateDecode", PdfBuilder.Deflate(program));
        return b;
    }

    private static PdfBuilder SimpleTrueType(string text, string name = "TestSans", string catalogExtra = "", string pageExtra = "") => Page(
        $"BT /F1 24 Tf 10 100 Td ({text}) Tj ET",
        $"<</Type/Font/Subtype/TrueType/BaseFont/{name}/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>",
        $"<</Type/FontDescriptor/FontName/{name}/Flags 32/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 7 0 R>>",
        Fixture("Test-Regular.ttf"), $"/Length1 {Fixture("Test-Regular.ttf").Length}", catalogExtra, pageExtra);

    private static int Glyph(string text)
    {
        OpenTypeFile font = OpenTypeFile.Parse(Fixture("Test-Regular.ttf"), 0);
        Assert.IsTrue(font.TryGetGlyph(text[0], out int glyph));
        return glyph;
    }

    // --- Subsets ------------------------------------------------------------------------------

    [TestMethod]
    public void FullyEmbeddedTrueTypeFontIsSubset()
    {
        byte[] input = SimpleTrueType("AVo").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.FontProgramsSubset);
        (PdfDictionary descriptor, _, byte[] program) = Programs(output).Single();
        string? name = descriptor.GetName("FontName");
        Assert.IsNotNull(name);
        Assert.IsTrue(FontSubsetter.HasTag(name) && name.EndsWith("+TestSans", StringComparison.Ordinal), name);

        TrueTypeGlyphs before = TrueTypeGlyphs.Parse(Fixture("Test-Regular.ttf"))!, after = TrueTypeGlyphs.Parse(program)!;
        Assert.AreEqual(before.GlyphCount, after.GlyphCount, "Glyph ids never change.");
        foreach (char c in "AVo") Assert.IsTrue(TrueTypeGlyphs.SameSlot(before.Slot(Glyph(c.ToString())), after.Slot(Glyph(c.ToString()))), c.ToString());
        Assert.IsTrue(after.Slot(Glyph("H")).Data.IsEmpty, "A glyph no reader can pick loses its outline.");
        Assert.IsTrue(after.RawTable("GPOS").IsEmpty, "Layout tables readers never apply are left out.");
        Assert.IsFalse(after.RawTable("cmap").IsEmpty, "A simple font keeps its cmap.");
    }

    [TestMethod]
    public void SimpleFontKeepsEveryGlyphAReaderCouldPick()
    {
        // Code 0x41 is renamed to /H by /Differences: one reader takes the post name H, another
        // the cmap entry of the code itself (A). Both must survive.
        PdfBuilder b = SimpleTrueType("A");
        b.Set(5, $"<</Type/Font/Subtype/TrueType/BaseFont/TestSans/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding<</BaseEncoding/WinAnsiEncoding/Differences[65/H]>>/FontDescriptor 6 0 R>>");
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        TrueTypeGlyphs after = TrueTypeGlyphs.Parse(Programs(output).Single().Program)!;
        Assert.IsFalse(after.Slot(Glyph("H")).Data.IsEmpty);
        Assert.IsFalse(after.Slot(Glyph("A")).Data.IsEmpty);
        Assert.IsTrue(after.Slot(Glyph("x")).Data.IsEmpty);
    }

    [TestMethod]
    public void CompositeTrueTypeFontIsSubsetWithItsComponents()
    {
        int aacute = 9;
        byte[] program = Fixture("Test-Regular.ttf");
        PdfBuilder b = Page(
            $"BT /F1 24 Tf 10 100 Td <{aacute:X4}> Tj ET",
            "<</Type/Font/Subtype/Type0/BaseFont/TestSans/Encoding/Identity-H/DescendantFonts[8 0 R]>>",
            "<</Type/FontDescriptor/FontName/TestSans/Flags 32/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 7 0 R>>",
            program, $"/Length1 {program.Length}");
        b.Set(8, "<</Type/Font/Subtype/CIDFontType2/BaseFont/TestSans/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 6 0 R/DW 600/CIDToGIDMap/Identity>>");
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.FontProgramsSubset);
        TrueTypeGlyphs before = TrueTypeGlyphs.Parse(program)!, after = TrueTypeGlyphs.Parse(Programs(output).Single().Program)!;
        foreach (int g in before.Closure([aacute])) Assert.IsTrue(TrueTypeGlyphs.SameSlot(before.Slot(g), after.Slot(g)), $"glyph {g}");
        Assert.IsTrue(after.Slot(Glyph("x")).Data.IsEmpty);

        // The Type0 font, its CIDFont and the descriptor carry one tag.
        PdfFile file = Open(output);
        var names = new List<string>();
        foreach ((int number, XrefEntry e) in file.Xref)
        {
            if (e.Type != XrefEntryType.Free && file.GetObject(number) is PdfDictionary d && (d.GetName("BaseFont") ?? d.GetName("FontName")) is { } n) names.Add(n);
        }

        Assert.HasCount(3, names);
        Assert.HasCount(1, names.Select(n => n[..7]).Distinct().ToList());
    }

    [TestMethod]
    public void CffFontsAreSubsetKeepingTheirEncodingAndCharset()
    {
        byte[] cff = OpenTypeFile.Parse(Fixture("Test-Regular.otf"), 0).Table("CFF ").ToArray();
        byte[] input = Page(
            "BT /F1 24 Tf 10 100 Td (AV) Tj ET",
            $"<</Type/Font/Subtype/Type1/BaseFont/TestSerif/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/FontDescriptor 6 0 R>>",
            "<</Type/FontDescriptor/FontName/TestSerif/Flags 34/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile3 7 0 R>>",
            cff, "/Subtype/Type1C").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.FontProgramsSubset);
        var before = new CffFont(cff);
        var after = new CffFont(Programs(output).Single().Program);
        Assert.AreEqual(before.GlyphCount, after.GlyphCount);
        CollectionAssert.AreEqual(before.Names(), after.Names());
        CollectionAssert.AreEqual(before.BuiltInEncoding(), after.BuiltInEncoding());
        string?[] names = before.Names()!;
        foreach (string kept in new[] { "A", "V" }) CollectionAssert.AreEqual(before.CharString(Array.IndexOf(names, kept)), after.CharString(Array.IndexOf(names, kept)));
        CollectionAssert.AreEqual(new byte[] { 14 }, after.CharString(Array.IndexOf(names, "x")), "Unused glyphs become a bare endchar.");
    }

    [TestMethod]
    public void CidKeyedCffFontIsSubset()
    {
        byte[] cff = OpenTypeFile.Parse(Fixture("Test-CID.otf"), 0).Table("CFF ").ToArray();
        var font = new CffFont(cff);
        int cid = font.GlyphToCid[3];
        PdfBuilder b = Page(
            $"BT /F1 24 Tf 10 100 Td <{cid:X4}> Tj ET",
            "<</Type/Font/Subtype/Type0/BaseFont/TestCid-Identity-H/Encoding/Identity-H/DescendantFonts[8 0 R]>>",
            "<</Type/FontDescriptor/FontName/TestCid/Flags 4/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile3 7 0 R>>",
            cff, "/Subtype/CIDFontType0C");
        b.Set(8, "<</Type/Font/Subtype/CIDFontType0/BaseFont/TestCid/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 6 0 R/DW 1000>>");
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.FontProgramsSubset);
        var after = new CffFont(Programs(output).Single().Program);
        CollectionAssert.AreEqual(font.CharString(3), after.CharString(3));
        CollectionAssert.AreEqual(new byte[] { 14 }, after.CharString(4));
    }

    [TestMethod]
    public void IdenticalProgramsBecomeOneSubsetForAllTheirFonts()
    {
        byte[] program = Fixture("Test-Regular.ttf");
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 5 0 R/Resources<</Font<</F1 7 0 R>>>>>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 6 0 R/Resources<</Font<</F1 8 0 R>>>>>>");
        b.SetStream(5, "", "BT /F1 24 Tf 10 100 Td (AV) Tj ET"u8.ToArray());
        b.SetStream(6, "", "BT /F1 24 Tf 10 100 Td (To) Tj ET"u8.ToArray());
        for (int i = 0; i < 2; i++)
        {
            int font = 7 + i, descriptor = 9 + i, file = 11 + i;
            b.Set(font, $"<</Type/Font/Subtype/TrueType/BaseFont/TestSans/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding/WinAnsiEncoding/FontDescriptor {descriptor} 0 R>>");
            b.Set(descriptor, $"<</Type/FontDescriptor/FontName/TestSans/Flags 32/FontBBox[0 -200 1000 800]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 {file} 0 R>>");
            b.SetStream(file, $"/Length1 {program.Length}", program);
        }

        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(2, result.Statistics.FontProgramsSubset);
        var programs = Programs(output);
        Assert.HasCount(1, programs.Select(p => Convert.ToBase64String(p.Program)).Distinct().ToList(), "One subset serves both fonts.");
        TrueTypeGlyphs after = TrueTypeGlyphs.Parse(programs[0].Program)!;
        foreach (char c in "AVTo") Assert.IsFalse(after.Slot(Glyph(c.ToString())).Data.IsEmpty, c.ToString());
    }

    [TestMethod]
    public void FontsFormsCanDrawWithStayWhole()
    {
        byte[] input = SimpleTrueType("AV", catalogExtra: "/AcroForm<</Fields[]/DR<</Font<</F1 5 0 R>>>>/DA(/F1 0 Tf 0 g)>>").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.FontProgramsSubset);
        Assert.HasCount(1, result.Statistics.FontsLeftWhole);
        StringAssert.Contains(result.Statistics.FontsLeftWhole[0], "interactive forms");
        StringAssert.Contains(result.Reason, "Fonts left whole: TestSans");
    }

    [TestMethod]
    public void FontsNamedByADefaultAppearanceStayWhole()
    {
        // No /DR: a widget's /DA still names F1, which a viewer may look up in the page's resources.
        PdfBuilder b = SimpleTrueType("AV", pageExtra: "/Annots[8 0 R]");
        b.Set(8, "<</Type/Annot/Subtype/Widget/Rect[0 0 100 20]/FT/Tx/T(name)/DA(/F1 12 Tf 0 g)/P 3 0 R>>");
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.FontProgramsSubset);
        StringAssert.Contains(result.Statistics.FontsLeftWhole.Single(), "interactive forms");
    }

    [TestMethod]
    public void NamesReadersTreatSpeciallyAreNotTagged()
    {
        byte[] input = SimpleTrueType("AV", name: "Symbol").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.FontProgramsSubset);
        StringAssert.Contains(result.Statistics.FontsLeftWhole.Single(), "readers treat the name specially");
        Assert.IsTrue(FontSubsetter.IsSpecialName("Helvetica-Bold", type1: false));
        Assert.IsTrue(FontSubsetter.IsSpecialName("ArialMT", type1: true));
        Assert.IsFalse(FontSubsetter.IsSpecialName("ArialMT", type1: false), "Aliases matter only for Type 1 fonts.");
    }

    [TestMethod]
    public void ContentThatCannotBeReadKeepsEveryFontWhole()
    {
        PdfBuilder b = SimpleTrueType("AV");
        b.SetStream(4, "/Filter/FlateDecode", "not deflate data"u8.ToArray());
        byte[] input = b.Build();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(0, result.Statistics.FontProgramsSubset);
        if (result.Disposition == OptimizationDisposition.Optimized) Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    [TestMethod]
    public void SubsettingCanBeTurnedOff()
    {
        byte[] input = SimpleTrueType("AV").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { SubsetFonts = false });

        Assert.AreEqual(0, result.Statistics.FontProgramsSubset);
        CollectionAssert.AreEqual(Fixture("Test-Regular.ttf"), Programs(output).Single().Program);
    }

    [TestMethod]
    public void StreamingModeSubsetsAndProvesToo()
    {
        byte[] input = SimpleTrueType("AVo").Build();
        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { Streaming = true });

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.FontProgramsSubset);
    }

    [TestMethod]
    public void TrueTypeSubsetProofCatchesEveryChangeThatMatters()
    {
        byte[] font = Fixture("Test-Regular.ttf");
        TrueTypeGlyphs glyphs = TrueTypeGlyphs.Parse(font)!;
        int a = Glyph("A"), v = Glyph("V");
        HashSet<int> shown = [0, a, v];
        byte[] subset = glyphs.Subset(shown);
        Assert.IsNull(TrueTypeGlyphs.FindSubsetDifference(font, subset, shown));

        // A shown glyph left out, or a glyph changed.
        Assert.IsNotNull(TrueTypeGlyphs.FindSubsetDifference(font, glyphs.Subset([0, a]), shown));
        byte[] changed = (byte[])subset.Clone();
        GlyphSlot slot = TrueTypeGlyphs.Parse(subset)!.Slot(a);
        changed[subset.AsSpan().IndexOf(slot.Data.Span) + slot.Data.Length - 1] ^= 1;
        Assert.IsNotNull(TrueTypeGlyphs.FindSubsetDifference(font, changed, shown));

        // A table readers use may not change; one they never apply may go.
        Assert.IsNotNull(TrueTypeGlyphs.FindSubsetDifference(font, WithoutTable(subset, "cmap"), shown));
        Assert.IsNotNull(TrueTypeGlyphs.FindSubsetDifference(font, WithoutTable(subset, "post"), shown));
        Assert.IsNull(TrueTypeGlyphs.FindSubsetDifference(font, WithoutTable(font, "GPOS"), shown));
    }

    /// <summary>The program rebuilt without one table.</summary>
    private static byte[] WithoutTable(byte[] font, string tag)
    {
        OpenTypeFile f = OpenTypeFile.ParseSubset(font);
        var tables = f.TableTags.Where(t => t != tag).Select(t => (t, 0u, f.Table(t).ToArray())).ToList();
        return SfntBuilder.Build(0x00010000, tables);
    }

    [TestMethod]
    public void CffSubsetKeepsSubroutineNumbersAndItsProofCatchesChanges()
    {
        // Glyph 1 calls local subroutine 0 and global subroutine 0; glyph 2 calls local 1.
        byte[] cff = CffWithSubroutines();
        var font = new CffFont(cff);
        (HashSet<int> kept, HashSet<int>[] local, HashSet<int> global) = FontSubsetter.CffClosure(font, [0, 1]);
        CollectionAssert.AreEquivalent(new[] { 0, 1 }, kept.ToArray());
        CollectionAssert.AreEquivalent(new[] { 0 }, local[0].ToArray());
        CollectionAssert.AreEquivalent(new[] { 0 }, global.ToArray());

        byte[] subset = font.Subset(kept, local, global);
        var after = new CffFont(subset);
        Assert.HasCount(2, after.Private(0).Subrs, "Subroutines keep their numbers.");
        CollectionAssert.AreEqual(new byte[] { 11 }, after.Private(0).Subrs[1], "An unused subroutine is a bare return.");
        Assert.IsNull(FontSubsetter.FindCffDifference(cff, subset, [0, 1]));
        Assert.IsNotNull(FontSubsetter.FindCffDifference(cff, subset, [0, 1, 2]), "Glyph 2 lost its outline.");

        byte[] broken = font.Subset(kept, [[]], global);
        Assert.IsNotNull(FontSubsetter.FindCffDifference(cff, broken, [0, 1]), "A subroutine glyph 1 calls was blanked.");
    }

    /// <summary>A name-keyed CFF with two local and one global subroutine (Technical Note 5176), assembled byte by byte.</summary>
    private static byte[] CffWithSubroutines()
    {
        static byte[] Index(params byte[][] items)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)(items.Length >> 8));
            ms.WriteByte((byte)items.Length);
            if (items.Length == 0) return ms.ToArray();
            ms.WriteByte(4);
            int offset = 1;
            foreach (byte[] item in items.Prepend([]))
            {
                offset += item.Length;
                ms.Write([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
            }

            foreach (byte[] item in items) ms.Write(item);
            return ms.ToArray();
        }

        static byte[] Offset(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        // Type 2: subroutine numbers are biased by -107 below 1240 subroutines.
        byte[] notdef = [14];
        byte[] one = [139, 139, 21, 32, 10, 32, 29, 14];   // 0 0 rmoveto -107 callsubr -107 callgsubr endchar
        byte[] two = [139, 139, 21, 33, 10, 14];           // 0 0 rmoveto -106 callsubr endchar
        byte[] local0 = [239, 139, 5, 11];                 // 100 0 rlineto return
        byte[] local1 = [139, 239, 5, 11];                 // 0 100 rlineto return
        byte[] global0 = [39, 139, 5, 11];                 // -100 0 rlineto return

        byte[] names = Index("T"u8.ToArray()), strings = Index(), globals = Index(global0);
        byte[] charStrings = Index(notdef, one, two);
        byte[] subrs = Index(local0, local1);
        byte[] privateDict = [.. Offset(8), 19, 139, 20];     // Subrs right after these 8 bytes; defaultWidthX 0
        int topLength = Index(new byte[17]).Length;
        int charStringsAt = 4 + names.Length + topLength + strings.Length + globals.Length;
        int privateAt = charStringsAt + charStrings.Length;
        byte[] top = [.. Offset(charStringsAt), 17, .. Offset(privateDict.Length), .. Offset(privateAt), 18];
        Assert.HasCount(17, top);
        return [1, 0, 4, 4, .. names, .. Index(top), .. strings, .. globals, .. charStrings, .. privateDict, .. subrs];
    }

    [TestMethod]
    public void VerifierRejectsATagOnAFontItMayNotSubset()
    {
        // The font is in /DR, so forms may draw with it: a tagged name cannot be a subset's.
        byte[] input = SimpleTrueType("AV", catalogExtra: "/AcroForm<</Fields[]/DR<</Font<</F1 5 0 R>>>>>>").Build();
        PdfBuilder b = SimpleTrueType("AV", catalogExtra: "/AcroForm<</Fields[]/DR<</Font<</F1 5 0 R>>>>>>");
        b.Set(5, $"<</Type/Font/Subtype/TrueType/BaseFont/ABCDEF+TestSans/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>");
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, b.Build()));

        // Where a subset is allowed, a tag is accepted, but never one with lowercase letters.
        byte[] open = SimpleTrueType("AV").Build();
        PdfBuilder tagged = SimpleTrueType("AV");
        tagged.Set(5, $"<</Type/Font/Subtype/TrueType/BaseFont/ABCDEF+TestSans/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>");
        Assert.IsNull(PdfEquivalence.FindDifference(open, tagged.Build()));
        tagged.Set(5, $"<</Type/Font/Subtype/TrueType/BaseFont/ABCdEF+TestSans/FirstChar 32/LastChar 126/Widths[{Widths(95)}]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>");
        Assert.IsNotNull(PdfEquivalence.FindDifference(open, tagged.Build()));
    }

    [TestMethod]
    public void SubsetAnalysisNeverThrowsOnMutatedInput()
    {
        byte[] seed = SimpleTrueType("AVTo").Build();
        var random = new Random(11);
        for (int i = 0; i < 60; i++)
        {
            byte[] mutated = (byte[])seed.Clone();
            for (int k = 0; k < 6; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            (byte[] output, PdfOptimizationResult result) = Run(mutated, NoThreshold with { AllowRepairedFiles = true });
            if (result.Disposition == OptimizationDisposition.Optimized) Assert.IsNull(PdfEquivalence.FindDifference(mutated, output), result.Reason);
            else CollectionAssert.AreEqual(mutated, output);
            if (result.Reason.StartsWith("Rewrite failed", StringComparison.Ordinal)) StringAssert.Contains(result.Reason, "(PdfFormatException)");
        }
    }

    // --- Type 1 as CFF --------------------------------------------------------------------------

    [TestMethod]
    public void Type1ProgramBecomesCffWithEveryGlyph()
    {
        (byte[] program, int length1, int length2) = Type1Fixture.Rich().Build();
        byte[] input = Type1Fixture.Document(program, length1, length2, "ABCDEFGHa");
        (byte[] output, PdfOptimizationResult result) = Run(input, Convert1);

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.Type1FontsConverted);
        (PdfDictionary descriptor, string key, byte[] cff) = Programs(output).Single();
        Assert.AreEqual("FontFile3", key);
        Assert.IsNull(descriptor["FontFile"]);
        Assert.IsNull(Type1ToCff.FindDifference(program, cff));
        Assert.HasCount(Type1Font.Parse(program).Glyphs.Count, new CffFont(cff).Names()!);
    }

    [TestMethod]
    public void ConversionKeepsHintReplacementFlexAccentsCountersAndWidths()
    {
        (byte[] program, _, _) = Type1Fixture.Rich().Build();
        Type1Font type1 = Type1Font.Parse(program);
        var cff = new CffFont(Type1ToCff.Convert(type1));
        string?[] names = cff.Names()!;
        GlyphOutline Outline(string name) => cff.Interpret(Array.IndexOf(names, name)).Outline;

        // A: vstem is relative to the side bearing; the path starts there.
        GlyphOutline a = Outline("A");
        CollectionAssert.Contains(a.Hints[0].Stems, new Stem(false, 30, 80));
        CollectionAssert.AreEqual(new double[] { 20, 0 }, a.Path[0].Points);

        // B: two hint sets, the second from the second subpath on.
        GlyphOutline b = Outline("B");
        Assert.HasCount(2, b.Hints);
        Assert.AreEqual(2, b.Hints[1].Start);

        // C: one flex, depth 50, ending where the Type 1 program says.
        PathElement flex = Outline("C").Path.Single(e => e.Kind == 'F');
        Assert.AreEqual(50, flex.Depth);
        CollectionAssert.AreEqual(new double[] { 370, 100 }, flex.Points[10..]);

        // D: widths and coordinates from div, rounded to 16.16 as readers compute them.
        GlyphOutline d = Outline("D");
        Assert.AreEqual(500.5, d.Width);
        Assert.AreEqual(20 + Math.Round(1000 * 65536 / 3.0) / 65536, d.Path[1].Points[0]);

        // E: stem3 hints become counter groups; Aacute stays an accented character.
        Assert.HasCount(2, Outline("E").Counters);
        Assert.AreEqual((65, 194, 170.0, 100.0), Outline("Aacute").Seac);

        // F: a line after closepath starts a new subpath where the last one ended.
        GlyphOutline f = Outline("F");
        Assert.AreEqual('M', f.Path[2].Kind);
        CollectionAssert.AreEqual(new double[] { 420, 300 }, f.Path[2].Points);

        // Code 97 names A as well: a second code for one glyph survives as a supplement.
        Assert.AreEqual("A", cff.BuiltInEncoding()![97]);
    }

    [TestMethod]
    public void HexEexecReadsAsBinaryDoes()
    {
        Type1Fixture fixture = Type1Fixture.Rich();
        Type1Font binary = Type1Font.Parse(fixture.Build().Program);
        fixture.HexEexec = true;
        Type1Font hex = Type1Font.Parse(fixture.Build().Program);
        Assert.HasCount(binary.Glyphs.Count, hex.Glyphs);
        for (int i = 0; i < binary.Glyphs.Count; i++) CollectionAssert.AreEqual(binary.Glyphs[i].CharString, hex.Glyphs[i].CharString);
    }

    [TestMethod]
    public void DivisionRoundsToFixedPointLikeReaders()
    {
        Assert.AreEqual(21845 / 65536.0, Type1Interpreter.Divide(1, 3));
        Assert.AreEqual(-21845 / 65536.0, Type1Interpreter.Divide(-1, 3));

        // Exactly half a step: away from zero, as FreeType's FT_DivFix does.
        Assert.AreEqual(1 / 65536.0, Type1Interpreter.Divide(1, 131072));
        Assert.AreEqual(-1 / 65536.0, Type1Interpreter.Divide(-1, 131072));
    }

    [TestMethod]
    public void FontsUsingWhatCffCannotSayStayType1()
    {
        foreach (object[] glyph in new object[][]
        {
            [20, 600, "hsbw", "dotsection", 0, 0, "rmoveto", 10, 10, "rlineto", "endchar"],
            [20, 600, "hsbw", 1, 2, 3, 3, 12, "callothersubr", "endchar"],
            [20, 0, 600, 100, "sbw", "endchar"],
        })
        {
            Type1Fixture fixture = Type1Fixture.Rich();
            fixture.Glyphs.Add(("Z", glyph));
            (byte[] program, int length1, int length2) = fixture.Build();
            Assert.ThrowsExactly<FontFormatException>(() => Type1ToCff.Convert(Type1Font.Parse(program)));

            byte[] input = Type1Fixture.Document(program, length1, length2, "AB");
            (byte[] output, PdfOptimizationResult result) = Run(input, Convert1);
            Assert.AreEqual(0, result.Statistics.Type1FontsConverted);
            if (result.Disposition == OptimizationDisposition.Optimized) Assert.IsNull(PdfEquivalence.FindDifference(input, output));
        }

        Type1Fixture stroked = Type1Fixture.Rich();
        stroked.PaintType = 2;
        Assert.ThrowsExactly<FontFormatException>(() => Type1ToCff.Convert(Type1Font.Parse(stroked.Build().Program)));
    }

    [TestMethod]
    public void ConversionProofCatchesEveryChangeThatMatters()
    {
        Type1Fixture original = Type1Fixture.Rich();
        byte[] program = original.Build().Program;
        Assert.IsNull(Type1ToCff.FindDifference(program, Type1ToCff.Convert(Type1Font.Parse(program))));

        // A moved point, a lost hint, a different Private value, encoding or flex depth.
        var variants = new List<Action<Type1Fixture>>
        {
            f => f.Glyphs[1] = ("A", [20, 600, "hsbw", 0, 50, "hstem", 10, 80, "vstem", 0, 1, "rmoveto", 6, "callsubr", 0, 700, "rlineto", -560, 0, "rlineto", "closepath", "endchar"]),
            f => f.Glyphs[1] = ("A", [20, 600, "hsbw", 10, 80, "vstem", 0, 0, "rmoveto", 6, "callsubr", 0, 700, "rlineto", -560, 0, "rlineto", "closepath", "endchar"]),
            f => f.Private[1] = "/BlueScale 0.04",
            f => f.Encoding![97] = "B",
            f =>
            {
                // The flex depth is the operand before the end point (370, 100).
                object[] tokens = f.Glyphs[3].Tokens;
                tokens[Array.IndexOf(tokens, 370) - 1] = 40;
            },
        };
        foreach (Action<Type1Fixture> change in variants)
        {
            Type1Fixture variant = Type1Fixture.Rich();
            change(variant);
            byte[] other = Type1ToCff.Convert(Type1Font.Parse(variant.Build().Program));
            Assert.IsNotNull(Type1ToCff.FindDifference(program, other));
        }
    }

    [TestMethod]
    public void StreamingModeConvertsAndProvesToo()
    {
        (byte[] program, int length1, int length2) = Type1Fixture.Rich().Build();
        byte[] input = Type1Fixture.Document(program, length1, length2, "ABC");
        (byte[] output, PdfOptimizationResult result) = Run(input, Convert1 with { Streaming = true });

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.Type1FontsConverted);
        Assert.AreEqual("FontFile3", Programs(output).Single().Key);
    }

    [TestMethod]
    public void Type1ConversionIsOffByDefault()
    {
        (byte[] program, int length1, int length2) = Type1Fixture.Rich().Build();
        byte[] input = Type1Fixture.Document(program, length1, length2, "AB");
        Assert.IsFalse(PdfOptimizerOptions.Default.ConvertType1Fonts);
        Assert.IsTrue(PdfOptimizerOptions.Default.SubsetFonts);
        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(0, result.Statistics.Type1FontsConverted);
        Assert.AreEqual("FontFile", Programs(output).Single().Key);
    }

    [TestMethod]
    public void MutatedType1ProgramsFailOnlyAsFontErrors()
    {
        byte[] seed = Type1Fixture.Rich().Build().Program;
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] mutated = (byte[])seed.Clone();
            for (int k = 0; k < 3; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            try
            {
                byte[] cff = Type1ToCff.Convert(Type1Font.Parse(mutated));

                // Whatever the converter makes of a damaged font, the proof judges it without throwing.
                _ = Type1ToCff.FindDifference(mutated, cff);
            }
            catch (FontFormatException)
            {
            }
        }
    }

    [TestMethod]
    public void Type2InterpreterReadsWidthsTheFontsDeclare()
    {
        // The width in each charstring of the CFF fixtures matches the hmtx advance of the same glyph.
        foreach (string name in new[] { "Test-Regular.otf", "Test-CID.otf" })
        {
            OpenTypeFile otf = OpenTypeFile.Parse(Fixture(name), 0);
            var cff = new CffFont(otf.Table("CFF "));
            for (int g = 0; g < cff.GlyphCount; g++) Assert.AreEqual(otf.Advance(g), cff.Interpret(g).Outline.Width, $"{name} glyph {g}");
        }
    }
}
