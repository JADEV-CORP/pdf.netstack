using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Text.Extraction;

namespace PdfNetStack.Optimization;

/// <summary>
/// Subsets fully embedded TrueType and CFF font programs to the glyphs the document can show.
/// Office software often embeds whole fonts, a megabyte or more each, of which a page uses a few
/// dozen glyphs. Glyph ids never change, so every way a reader selects a glyph (CIDToGIDMap,
/// cmap, post, the CFF charset and encoding) still finds the same one; unused glyphs only lose
/// their outlines. <see cref="Plan"/> decides what may change and is run again by the verifier
/// on its own parse of the source, so the proof never relies on this pass being right.
/// </summary>
internal static class FontSubsetter
{
    /// <summary>Names readers recognise and treat apart (standard 14 fonts, their aliases, symbol fonts): never given a subset tag.</summary>
    private static readonly string[] SymbolFamilies = ["Symbol", "ZapfDingbats", "Dingbats", "Wingdings", "Webdings"];

    private static readonly string[] Standard14 =
    [
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique", "Helvetica", "Helvetica-Bold", "Helvetica-Oblique",
        "Helvetica-BoldOblique", "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic", "Symbol", "ZapfDingbats",
    ];

    /// <summary>Families some readers map to a standard 14 font when a Type 1 font has their name.</summary>
    private static readonly string[] Type1Aliases = ["Arial", "Helvetica", "Times", "Courier"];

    /// <summary>One font program: whether and how it may be subset.</summary>
    internal sealed class Candidate(int program, bool cff, string name)
    {
        public int Program { get; } = program;

        /// <summary>FontFile3 (CFF) rather than FontFile2 (TrueType).</summary>
        public bool Cff { get; } = cff;

        /// <summary>A name for the report: the descriptor's /FontName.</summary>
        public string Name { get; } = name;

        /// <summary>Why the program must stay whole; null when it may be subset.</summary>
        public string? Skip { get; set; }

        /// <summary>Glyph ids that must keep their data: every glyph a reader can draw.</summary>
        public HashSet<int> Glyphs { get; } = [];

        /// <summary>For CFF, the local subroutines (per font dictionary) and global ones those glyphs call.</summary>
        public HashSet<int>[] LocalSubrs { get; set; } = [];

        public HashSet<int> GlobalSubrs { get; set; } = [];

        /// <summary>The decoded program.</summary>
        public byte[]? Data { get; set; }

        /// <summary>Font, CIDFont and descriptor dictionaries with their name key, for the subset tag.</summary>
        public List<(PdfDictionary Dictionary, string Key)> Names { get; } = [];

        /// <summary>Descriptors that reference the program.</summary>
        public HashSet<PdfDictionary> Descriptors { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A font that uses a program, and what it shows.</summary>
    private sealed record User(PdfDictionary Font, PdfDictionary? CidFont, PdfDictionary Descriptor, int DescriptorNumber, bool Composite, HashSet<int> Codes, Func<int, int>? CidToGid);

    /// <summary>
    /// Every TrueType and CFF program a font descriptor in the graph holds, with what may be done
    /// with it. A program may be subset only when every font using it is fully understood, no
    /// form can draw with it and its glyph usage is complete.
    /// </summary>
    /// <param name="order">The reachable objects.</param>
    /// <param name="pdfA1">The file claims PDF/A-1, which asks a subset for /CharSet or /CIDSet.</param>
    public static Dictionary<int, Candidate> Plan(PdfFile file, ContentAnalysis analysis, IEnumerable<int> order, bool pdfA1, long decodeLimit, CancellationToken ct)
    {
        var candidates = new Dictionary<int, Candidate>();
        ReferenceIndex refs = analysis.References;

        // Every program reachable through a descriptor, so that each one left whole has its reason.
        foreach (int number in order)
        {
            if (file.GetObject(number) is not PdfDictionary d || d.GetName("Type") != "FontDescriptor") continue;
            foreach ((string key, bool cff) in new[] { ("FontFile2", false), ("FontFile3", true) })
            {
                if (d[key] is not PdfReference r || candidates.ContainsKey(r.Number) || file.GetObject(r.Number) is not PdfStream program) continue;
                var c = new Candidate(r.Number, cff, d.GetName("FontName") ?? "?");
                if (cff && program.Dictionary.GetName("Subtype") is not ("Type1C" or "CIDFontType0C")) c.Skip = "OpenType programs are not subset yet";
                candidates[r.Number] = c;
            }
        }

        string? global =
            !analysis.Complete || !analysis.GlyphUsageComplete ? "the content could not be read in full" :
            refs.HasXfa ? "an XFA form may draw text with any font" :
            pdfA1 ? "PDF/A-1 asks subsets for a /CharSet or /CIDSet" : null;
        if (global is not null)
        {
            foreach (Candidate c in candidates.Values) c.Skip ??= global;
            return candidates;
        }

        HashSet<int> formFonts = FormFonts(file, refs);
        foreach (int number in formFonts)
        {
            if (file.GetObject(number) is PdfDictionary formFont && ProgramBehind(file, formFont) is { } p && candidates.TryGetValue(p, out Candidate? c))
            {
                c.Skip = "interactive forms can draw new text with the font";
            }
        }

        var users = new Dictionary<int, List<User>>();
        var seen = new HashSet<int>();
        foreach (PdfDictionary category in analysis.CategoryDictionaries("Font"))
        {
            foreach (KeyValuePair<string, PdfObject> entry in category)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Value is not PdfReference fontRef || !seen.Add(fontRef.Number) || file.GetObject(fontRef.Number) is not PdfDictionary font) continue;
                (User? user, int program, string? reason) = Classify(file, analysis, fontRef.Number, font, decodeLimit);
                if (program < 0 || !candidates.TryGetValue(program, out Candidate? candidate)) continue;
                if (formFonts.Contains(fontRef.Number)) reason = "interactive forms can draw new text with the font";
                if (reason is not null || user is null)
                {
                    candidate.Skip ??= reason;
                    continue;
                }

                if (!users.TryGetValue(program, out List<User>? list))
                {
                    list = [];
                    users[program] = list;
                }

                list.Add(user);
            }
        }

        foreach (Candidate c in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (c.Skip is not null) continue;
            if (!users.TryGetValue(c.Program, out List<User>? list))
            {
                c.Skip = "the font is used where content was not read";
                continue;
            }

            c.Skip = Prepare(file, refs, c, list, decodeLimit);
        }

        return candidates;
    }

    /// <summary>The program behind a simple or composite font, or null.</summary>
    private static int? ProgramBehind(PdfFile file, PdfDictionary font)
    {
        PdfDictionary? holder = font.GetName("Subtype") == "Type0" && file.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } kids
            ? file.ResolveDictionary(kids[0])
            : font;
        if (file.ResolveDictionary(holder?["FontDescriptor"]) is not { } descriptor) return null;
        foreach (string key in new[] { "FontFile2", "FontFile3" })
        {
            if (descriptor[key] is PdfReference r) return r.Number;
        }

        return null;
    }

    /// <summary>Fonts that /DR resources hold or that a /DA string names in any font resource dictionary.</summary>
    private static HashSet<int> FormFonts(PdfFile file, ReferenceIndex refs)
    {
        var fonts = new HashSet<int>();
        foreach (PdfDictionary dr in refs.FormResourceDictionaries)
        {
            if (file.Resolve(dr["Font"]) is not PdfDictionary category) continue;
            foreach (KeyValuePair<string, PdfObject> e in category)
            {
                if (e.Value is PdfReference r) fonts.Add(r.Number);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfString da in refs.DefaultAppearances) ContentAnalysis.ScanNames(da.Value, names);
        if (names.Count == 0) return fonts;
        foreach (PdfDictionary resources in refs.ResourceDictionaries)
        {
            if (file.Resolve(resources["Font"]) is not PdfDictionary category) continue;
            foreach (string name in names)
            {
                if (category[name] is PdfReference r) fonts.Add(r.Number);
            }
        }

        return fonts;
    }

    /// <summary>The font's program and what it shows, or why it cannot be subset; program -1 when it has none.</summary>
    private static (User? User, int Program, string? Reason) Classify(PdfFile file, ContentAnalysis analysis, int number, PdfDictionary font, long decodeLimit)
    {
        ReferenceIndex refs = analysis.References;
        string? subtype = font.GetName("Subtype");
        if (subtype == "Type0")
        {
            if (file.Resolve(font["DescendantFonts"]) is not PdfArray { Count: 1 } array || array[0] is not PdfReference cidRef ||
                file.GetObject(cidRef.Number) is not PdfDictionary cid || cid["FontDescriptor"] is not PdfReference descriptorRef ||
                file.GetObject(descriptorRef.Number) is not PdfDictionary descriptor)
            {
                return (null, -1, null);
            }

            int program = ProgramOf(descriptor, cid.GetName("Subtype") == "CIDFontType0" ? "FontFile3" : "FontFile2");
            if (program < 0) return (null, -1, null);
            if (font.GetName("Encoding") is not ("Identity-H" or "Identity-V")) return (null, program, "the CMap is not Identity-H or Identity-V");
            if (font["DescendantFonts"] is PdfReference dr && !refs.OnlyFrom(dr.Number, r => ReferenceEquals(r.Container, font))) return (null, program, "the CIDFont is shared");
            if (!refs.OnlyFrom(cidRef.Number, r => ReferenceEquals(r.Container, array))) return (null, program, "the CIDFont is shared");
            if (!refs.OnlyFrom(descriptorRef.Number, r => ReferenceEquals(r.Container, cid) && r.Key == "FontDescriptor")) return (null, program, "the descriptor is shared");
            if (Observed(analysis, number, font) is { } unobserved) return (null, program, unobserved);
            if (descriptor["CIDSet"] is not null) return (null, program, "the descriptor has a /CIDSet");

            Func<int, int>? map = null;
            if (cid.GetName("Subtype") == "CIDFontType2" && cid["CIDToGIDMap"] is { } m && file.Resolve(m) is not PdfName { Value: "Identity" })
            {
                if (file.Resolve(m) is not PdfStream mapStream || Decode(file, mapStream, decodeLimit) is not { } bytes) return (null, program, "the /CIDToGIDMap cannot be read");
                map = cidValue => 2 * cidValue + 1 < bytes.Length ? bytes[2 * cidValue] << 8 | bytes[2 * cidValue + 1] : 0;
            }

            HashSet<int> codes = analysis.IdentityCodes.TryGetValue(font, out HashSet<int>? shown) ? shown : [];
            return (new User(font, cid, descriptor, descriptorRef.Number, Composite: true, codes, map), program, null);
        }

        if (subtype is not ("TrueType" or "Type1") || font["FontDescriptor"] is not PdfReference simpleRef ||
            file.GetObject(simpleRef.Number) is not PdfDictionary simpleDescriptor)
        {
            return (null, -1, null);
        }

        int simpleProgram = ProgramOf(simpleDescriptor, subtype == "TrueType" ? "FontFile2" : "FontFile3");
        if (simpleProgram < 0) return (null, -1, null);
        if (Observed(analysis, number, font) is { } reason) return (null, simpleProgram, reason);
        if (file.Resolve(font["Widths"]) is not PdfArray || file.Resolve(font["FirstChar"]) is not PdfNumber { IsInteger: true }) return (null, simpleProgram, "the font has no /Widths");
        if (simpleDescriptor["CharSet"] is not null) return (null, simpleProgram, "the descriptor has a /CharSet");
        HashSet<int> simpleCodes = analysis.SimpleCodes.TryGetValue(font, out HashSet<int>? bytesShown) ? bytesShown : [];
        return (new User(font, null, simpleDescriptor, simpleRef.Number, Composite: false, simpleCodes, null), simpleProgram, null);

        static int ProgramOf(PdfDictionary descriptor, string key) => descriptor[key] is PdfReference r ? r.Number : -1;
    }

    private static string? Observed(ContentAnalysis analysis, int number, PdfDictionary font)
    {
        if (!analysis.FontFullyObserved(number)) return "the font is used where content was not read";
        return analysis.UnreliableFonts.Contains(font) ? "some text shown with the font could not be decoded" : null;
    }

    /// <summary>Checks how the program is referenced, reads it and works out the glyphs to keep; returns why it cannot be subset.</summary>
    private static string? Prepare(PdfFile file, ReferenceIndex refs, Candidate c, List<User> users, long decodeLimit)
    {
        foreach (User u in users) c.Descriptors.Add(u.Descriptor);
        string key = c.Cff ? "FontFile3" : "FontFile2";
        if (!refs.OnlyFrom(c.Program, r => r.Key == key && r.Container is PdfDictionary d && c.Descriptors.Contains(d))) return "the program is shared with fonts whose glyphs cannot be listed";

        // A descriptor that a font outside the users names would keep the old name and the new program.
        var fonts = new HashSet<PdfDictionary>(users.Select(u => u.CidFont ?? u.Font), ReferenceEqualityComparer.Instance);
        foreach (int number in users.Select(u => u.DescriptorNumber).Distinct())
        {
            if (!refs.OnlyFrom(number, r => r.Key == "FontDescriptor" && r.Container is PdfDictionary f && fonts.Contains(f))) return "the descriptor is shared with fonts whose glyphs cannot be listed";
        }

        if (file.GetObject(c.Program) is not PdfStream stream || Decode(file, stream, decodeLimit) is not { } data) return "the program cannot be decoded";
        c.Data = data;

        // Names: a tag goes only where there is none yet, and never on a name readers treat apart.
        foreach (User u in users)
        {
            foreach ((PdfDictionary? dict, string nameKey) in new[] { (u.Font, "BaseFont"), (u.CidFont, "BaseFont"), (u.Descriptor, "FontName") })
            {
                if (dict is null || dict.GetName(nameKey) is not { } name || HasTag(name)) continue;
                if (IsSpecialName(name, c.Cff || u.Font.GetName("Subtype") == "Type1")) return "readers treat the name specially";
                if (!c.Names.Exists(n => ReferenceEquals(n.Dictionary, dict) && n.Key == nameKey)) c.Names.Add((dict, nameKey));
            }
        }

        try
        {
            return c.Cff ? PrepareCff(c, users, data) : PrepareTrueType(c, users, data);
        }
        catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return "the program cannot be read for certain: " + ex.Message;
        }
    }

    private static string? PrepareTrueType(Candidate c, List<User> users, byte[] data)
    {
        TrueTypeGlyphs glyphs = TrueTypeGlyphs.Parse(data) ?? throw new FontFormatException("not a readable TrueType program");
        var keep = new HashSet<int> { 0 };
        TrueTypeSelection? selection = null;
        foreach (User u in users)
        {
            if (u.Composite)
            {
                foreach (int cid in u.Codes) keep.Add(u.CidToGid is null ? cid : u.CidToGid(cid));
                continue;
            }

            selection ??= new TrueTypeSelection(glyphs.RawTable("cmap"), glyphs.RawTable("post"), glyphs.GlyphCount);
            foreach (int code in u.Codes) SimpleTrueTypeGlyphs(selection, u.Font, code, keep);
        }

        c.Glyphs.UnionWith(glyphs.Closure(new HashSet<int>(keep.Where(g => g >= 0 && g < glyphs.GlyphCount))));
        return null;
    }

    /// <summary>Every glyph some reader may pick for a simple TrueType font's code (ISO 32000-1, 9.6.6.4, and what readers do besides).</summary>
    private static void SimpleTrueTypeGlyphs(TrueTypeSelection selection, PdfDictionary font, int code, HashSet<int> keep)
    {
        keep.Add(code);
        foreach (int prefix in new[] { 0, 0xF000, 0xF100, 0xF200 }) keep.UnionWith(selection.Lookup(prefix | code));
        foreach (string name in CandidateNames(font, code))
        {
            keep.UnionWith(selection.ByName(name));
            if (CodePoint(GlyphList.ToUnicode(name)) is { } unicode) keep.UnionWith(selection.Lookup(unicode));

            int mac = Array.IndexOf(FontEncodings.MacRomanEncoding, name);
            if (mac >= 0) keep.UnionWith(selection.Lookup(mac, 1, 0));
            if (GlyphList.NumericName(name) is { } n)
            {
                keep.Add(n);
                keep.UnionWith(selection.Lookup(n));
            }
        }
    }

    /// <summary>The single code point a glyph name stands for; null for none or several.</summary>
    private static int? CodePoint(string? text) => text switch
    {
        { Length: 1 } when !char.IsSurrogate(text[0]) => text[0],
        { Length: 2 } when char.IsSurrogatePair(text[0], text[1]) => char.ConvertToUtf32(text[0], text[1]),
        _ => null,
    };

    /// <summary>The glyph names a reader may give a simple font's code: /Differences and every predefined encoding.</summary>
    private static IEnumerable<string> CandidateNames(PdfDictionary font, int code)
    {
        if (font["Encoding"] is PdfDictionary encoding && encoding["Differences"] is PdfArray differences)
        {
            int at = -1;
            foreach (PdfObject item in differences)
            {
                if (item is PdfNumber { IsInteger: true } n) at = (int)n.IntegerValue;
                else if (item is PdfName name && at >= 0)
                {
                    if (at == code) yield return name.Value;
                    at++;
                }
            }
        }

        foreach (string?[] table in new[] { FontEncodings.StandardEncoding, FontEncodings.WinAnsiEncoding, FontEncodings.MacRomanEncoding, FontEncodings.MacExpertEncoding })
        {
            if (table[code] is { } name) yield return name;
        }
    }

    private static string? PrepareCff(Candidate c, List<User> users, byte[] data)
    {
        var cff = new CffFont(data);
        var keep = new HashSet<int> { 0 };
        string?[]? names = cff.Names();
        Dictionary<string, int>? byName = null;
        if (names is not null)
        {
            byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int g = names.Length - 1; g >= 0; g--)
            {
                if (names[g] is { } n) byName[n] = g;
            }
        }

        foreach (User u in users)
        {
            if (u.Composite)
            {
                if (!cff.IsCidKeyed)
                {
                    foreach (int cid in u.Codes) keep.Add(cid);
                    continue;
                }

                var gidOfCid = new Dictionary<int, int>();
                for (int g = cff.GlyphToCid.Length - 1; g >= 0; g--) gidOfCid[cff.GlyphToCid[g]] = g;
                foreach (int cid in u.Codes)
                {
                    if (gidOfCid.TryGetValue(cid, out int g)) keep.Add(g);
                }

                continue;
            }

            if (byName is null) return "a CID-keyed program serves a simple font";
            string?[] builtIn = cff.BuiltInEncoding() ?? [];
            foreach (int code in u.Codes)
            {
                keep.Add(code);
                IEnumerable<string> candidatesNames = CandidateNames(u.Font, code);
                if (code < builtIn.Length && builtIn[code] is { } own) candidatesNames = candidatesNames.Append(own);
                foreach (string name in candidatesNames)
                {
                    if (byName.TryGetValue(name, out int g)) keep.Add(g);
                    if (GlyphList.NumericName(name) is { } n) keep.Add(n);
                }
            }
        }

        keep.RemoveWhere(g => g < 0 || g >= cff.GlyphCount);
        (HashSet<int> glyphs, HashSet<int>[] local, HashSet<int> global) = CffClosure(cff, keep);
        c.Glyphs.UnionWith(glyphs);
        c.LocalSubrs = local;
        c.GlobalSubrs = global;
        return null;
    }

    /// <summary>
    /// The glyphs with both parts of every accented one among them, transitively, and the
    /// subroutines they call, from running each charstring.
    /// </summary>
    /// <exception cref="FontFormatException">A charstring cannot be read exactly.</exception>
    public static (HashSet<int> Glyphs, HashSet<int>[] Local, HashSet<int> Global) CffClosure(CffFont cff, IEnumerable<int> glyphs)
    {
        var result = new HashSet<int>();
        var local = new HashSet<int>[cff.FontDictCount];
        for (int i = 0; i < local.Length; i++) local[i] = [];
        var global = new HashSet<int>();
        int[] fdOf = cff.FontDictOfGlyph();
        string?[]? names = cff.Names();
        var pending = new Stack<int>(glyphs);
        long budget = Type2Interpreter.MaxStepsPerFont;
        while (pending.Count > 0)
        {
            int g = pending.Pop();
            if (g < 0 || g >= cff.GlyphCount || !result.Add(g)) continue;
            Type2Interpreter run = cff.Interpret(g);
            if ((budget -= run.Steps) < 0) throw new FontFormatException("CFF font takes too much work to read.");
            local[fdOf[g]].UnionWith(run.LocalSubrs);
            global.UnionWith(run.GlobalSubrs);
            if (run.Outline.Seac is not { } seac) continue;
            if (names is null) throw new FontFormatException("A CID-keyed CFF font uses an accented character.");
            foreach (int code in new[] { seac.Base, seac.Accent })
            {
                string? name = FontEncodings.StandardEncoding[code];
                int part = name is null ? -1 : Array.IndexOf(names, name);
                if (part < 0) throw new FontFormatException("An accented character names a glyph the font lacks.");
                pending.Push(part);
            }
        }

        return (result, local, global);
    }

    /// <summary>
    /// Null when <paramref name="candidate"/> is a subset of the CFF program <paramref name="original"/>
    /// that draws every glyph in <paramref name="glyphs"/> as it does: the same names, strings,
    /// charset, encoding, font dictionaries and Private DICTs apart from offsets, the same
    /// subroutine counts, those glyphs' charstrings and every subroutine they call byte for byte.
    /// </summary>
    public static string? FindCffDifference(byte[] original, byte[] candidate, HashSet<int> glyphs)
    {
        try
        {
            var a = new CffFont(original);
            var b = new CffFont(candidate);
            if (a.GlyphCount != b.GlyphCount || a.IsCidKeyed != b.IsCidKeyed) return "has a different glyph count or kind";
            (byte[] namesA, byte[] stringsA) = a.RawIndexes();
            (byte[] namesB, byte[] stringsB) = b.RawIndexes();
            if (!namesA.AsSpan().SequenceEqual(namesB) || !stringsA.AsSpan().SequenceEqual(stringsB)) return "has different names or strings";
            if (!SameEntries(a.TopDict, b.TopDict, [15, 16, 17, 18, 1236, 1237])) return "has a different Top DICT";
            if (!a.GlyphToCid.AsSpan().SequenceEqual(b.GlyphToCid)) return "has a different charset";
            if (!a.IsCidKeyed)
            {
                if (!a.Names()!.SequenceEqual(b.Names()!)) return "has a different charset";
                if (!a.BuiltInEncoding()!.SequenceEqual(b.BuiltInEncoding()!)) return "has a different encoding";
            }

            if (a.FontDictCount != b.FontDictCount || !a.FontDictOfGlyph().AsSpan().SequenceEqual(b.FontDictOfGlyph())) return "has different font dictionaries";
            List<List<CffFont.DictEntry>> fontsA = a.FontDicts(), fontsB = b.FontDicts();
            for (int fd = 0; fd < a.FontDictCount; fd++)
            {
                if (a.IsCidKeyed && !SameEntries(fontsA[fd], fontsB[fd], [18])) return $"font dictionary {fd} differs";
                CffFont.PrivateData pa = a.Private(fd), pb = b.Private(fd);
                if (!SameEntries(pa.Entries, pb.Entries, [19]) || pa.Subrs.Length != pb.Subrs.Length) return $"Private DICT {fd} differs";
            }

            if (a.GlobalSubrs.Count != b.GlobalSubrs.Count) return "has a different number of global subroutines";
            (HashSet<int> shown, HashSet<int>[] local, HashSet<int> global) = CffClosure(a, glyphs);
            foreach (int g in shown)
            {
                if (!a.CharString(g).AsSpan().SequenceEqual(b.CharString(g))) return $"glyph {g} differs";
            }

            for (int fd = 0; fd < local.Length; fd++)
            {
                foreach (int i in local[fd])
                {
                    if (!a.Private(fd).Subrs[i].AsSpan().SequenceEqual(b.Private(fd).Subrs[i])) return $"local subroutine {i} differs";
                }
            }

            foreach (int i in global)
            {
                if (!a.GlobalSubrs[i].AsSpan().SequenceEqual(b.GlobalSubrs[i])) return $"global subroutine {i} differs";
            }

            return null;
        }
        catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return "is not a readable CFF program";
        }
    }

    /// <summary>The same DICT entries, in any order, leaving out <paramref name="offsets"/>.</summary>
    private static bool SameEntries(List<CffFont.DictEntry> a, List<CffFont.DictEntry> b, int[] offsets)
    {
        static List<string> Key(List<CffFont.DictEntry> d, int[] skip) =>
            [.. d.Where(e => Array.IndexOf(skip, e.Op) < 0).Select(e => e.Op.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Compat.ToHex(e.Operands)).OrderBy(k => k, StringComparer.Ordinal)];
        return Key(a, offsets).SequenceEqual(Key(b, offsets));
    }

    /// <summary>True for a tag of six capital letters and a plus sign (ISO 32000-1, 9.6.4).</summary>
    public static bool HasTag(string name)
    {
        if (name.Length < 8 || name[6] != '+') return false;
        for (int i = 0; i < 6; i++)
        {
            if (name[i] is < 'A' or > 'Z') return false;
        }

        return true;
    }

    /// <summary>Names some reader recognises and treats apart, so a tag could change how it renders the font.</summary>
    public static bool IsSpecialName(string name, bool type1)
    {
        foreach (string family in SymbolFamilies)
        {
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (Array.IndexOf(Standard14, name) >= 0) return true;
        if (!type1) return false;
        foreach (string family in Type1Aliases)
        {
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static byte[]? Decode(PdfFile file, PdfStream stream, long decodeLimit)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, file.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, decodeLimit, file.Resolve);
            return decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null ? null : decoded.Data.ToArray();
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces each program that may be subset by its subset, when that is smaller, and tags the
    /// names of the fonts using it. Programs with the same content become the same subset, built
    /// for all their fonts together, so deduplication still merges them.
    /// </summary>
    /// <returns>Programs rewritten, their object numbers, and every program left whole for a reason other than size, with that reason.</returns>
    public static (int Subset, HashSet<int> Changed, List<string> Kept) Run(PdfFile file, ContentAnalysis analysis, IEnumerable<int> order, bool pdfA1, CompressionLevel level, long decodeLimit, CancellationToken ct)
    {
        var changed = new HashSet<int>();
        List<int> reachable = order as List<int> ?? order.ToList();
        Dictionary<int, Candidate> plan = Plan(file, analysis, reachable, pdfA1, decodeLimit, ct);
        var kept = plan.Values.Where(c => c.Skip is not null).OrderBy(c => c.Program).Select(c => c.Name + ": " + c.Skip).ToList();

        // Group by content: a copy that must stay whole keeps all its copies whole.
        var groups = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (Candidate c in plan.Values.OrderBy(c => c.Program))
        {
            byte[]? data = c.Data ?? (file.GetObject(c.Program) is PdfStream s ? Decode(file, s, decodeLimit) : null);
            if (data is null) continue;
            string key = (c.Cff ? "C" : "T") + Compat.ToHex(Compat.Sha256(data));
            if (c.Skip is not null)
            {
                blocked.Add(key);
                continue;
            }

            c.Data = data;
            if (!groups.TryGetValue(key, out List<Candidate>? group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(c);
        }

        HashSet<string> tags = ExistingTags(file, reachable);
        int subset = 0;
        foreach ((string key, List<Candidate> group) in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (blocked.Contains(key)) continue;
            byte[]? program = Build(group);

            // The proof the verifier runs, for each font's glyphs: a subset it would reject is not used.
            if (program is null || group.Exists(c => (c.Cff ? FindCffDifference(c.Data!, program, c.Glyphs) : TrueTypeGlyphs.FindSubsetDifference(c.Data, program, c.Glyphs)) is not null)) continue;
            // The tags lengthen the font names, so a subset must save more than they cost.
            byte[] encoded = StreamCodec.Deflate(program, level);
            if (program.Length >= group[0].Data!.Length || encoded.Length + 64 >= ((PdfStream)file.GetObject(group[0].Program)).Data.Length) continue;

            string tag = NewTag(program, tags);
            foreach (Candidate c in group)
            {
                var stream = (PdfStream)file.GetObject(c.Program);
                stream.Data = encoded;
                stream.Dictionary.Set("Filter", new PdfName("FlateDecode"));
                stream.Dictionary.Remove("DecodeParms");
                stream.Dictionary.Remove("DL");
                if (!c.Cff) stream.Dictionary.Set("Length1", PdfNumber.FromInteger(program.Length));
                foreach ((PdfDictionary dict, string nameKey) in c.Names)
                {
                    if (dict.GetName(nameKey) is { } name && !HasTag(name)) dict.Set(nameKey, new PdfName(tag + "+" + name));
                }

                changed.Add(c.Program);
                subset++;
            }
        }

        return (subset, changed, kept);
    }

    /// <summary>One subset for programs of identical content, keeping every glyph any of their fonts can show.</summary>
    private static byte[]? Build(List<Candidate> group)
    {
        Candidate first = group[0];
        var glyphs = new HashSet<int>();
        foreach (Candidate c in group) glyphs.UnionWith(c.Glyphs);
        try
        {
            if (!first.Cff) return TrueTypeGlyphs.Parse(first.Data!)?.Subset(glyphs);
            var cff = new CffFont(first.Data!);
            var local = new HashSet<int>[cff.FontDictCount];
            for (int i = 0; i < local.Length; i++) local[i] = [.. group.SelectMany(c => i < c.LocalSubrs.Length ? c.LocalSubrs[i] : [])];
            var global = new HashSet<int>(group.SelectMany(c => c.GlobalSubrs));
            return cff.Subset(glyphs, local, global);
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>Subset tags already in use by the document's fonts.</summary>
    private static HashSet<string> ExistingTags(PdfFile file, List<int> order)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (int number in order)
        {
            if (file.GetObject(number) is not PdfDictionary d) continue;
            foreach (string key in new[] { "BaseFont", "FontName" })
            {
                if (d.GetName(key) is { } name && HasTag(name)) tags.Add(name[..6]);
            }
        }

        return tags;
    }

    /// <summary>Six capital letters from the subset's hash, different from every tag in use.</summary>
    private static string NewTag(byte[] program, HashSet<string> used)
    {
        byte[] hash = Compat.Sha256(program);
        for (int attempt = 0; ; attempt++)
        {
            var letters = new char[6];
            for (int i = 0; i < 6; i++) letters[i] = (char)('A' + (hash[(i + attempt) % hash.Length] + attempt) % 26);
            string tag = new(letters);
            if (used.Add(tag)) return tag;
        }
    }
}
