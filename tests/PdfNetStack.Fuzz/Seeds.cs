using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Signing;

namespace PdfNetStack.Fuzz;

/// <summary>
/// Starting inputs for each target, from the test fixtures (tests/PdfNetStack.Tests/Fixtures) and
/// from documents the library writes, so the fuzzer starts from files that reach deep paths.
/// </summary>
internal static class Seeds
{
    /// <summary>Larger files slow the fuzzer more than they teach it.</summary>
    public const int MaxBytes = 256 << 10;

    public static IEnumerable<(string Name, byte[] Data)> Pdfs(string fixtures)
    {
        foreach ((string name, byte[] data) in Files(fixtures, ["Forms"], [".pdf"])) yield return (name, data);

        byte[] text = TextDocument(fixtures);
        yield return ("text.pdf", text);
        byte[] images = ImageDocument(fixtures);
        yield return ("images.pdf", images);
        yield return ("images-optimized.pdf", PdfOptimizer.Optimize(images, new PdfOptimizerOptions { MinSavingPercent = 0, MaxDegreeOfParallelism = 1 }).Output);
        yield return ("form.pdf", FormDocument());
        yield return ("html.pdf", HtmlConverter.ConvertToPdf(Page, new HtmlConverterOptions { ResourceLoader = _ => null }));

        using (PdfDocument doc = PdfDocument.Open(text))
        {
            doc.Security.Encrypt("", "owner");
            yield return ("encrypted.pdf", doc.ToArray());
        }

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fuzz Seed", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        yield return ("signed.pdf", PdfSigner.Sign(text, certificate, new PdfSignatureOptions { Reason = "Seed" }));
    }

    public static IEnumerable<(string Name, byte[] Data)> Images(string fixtures) =>
        Files(fixtures, ["Images", "Jpeg", "Gif", "Bmp", "Tiff", "WebP"], [".png", ".jpg", ".gif", ".bmp", ".tif", ".webp"]);

    public static IEnumerable<(string Name, byte[] Data)> Fonts(string fixtures) =>
        Files(fixtures, ["Fonts"], [".ttf", ".otf", ".ttc", ".woff", ".woff2"]);

    public static IEnumerable<(string Name, byte[] Data)> Jpx(string fixtures) => Files(fixtures, ["Jpx"], [".jp2", ".j2k"]);

    public static IEnumerable<(string Name, byte[] Data)> Html(string fixtures)
    {
        foreach ((string name, byte[] data) in Files(fixtures, ["Brotli"], [".html"])) yield return (name, data);
        yield return ("page.html", Encoding.UTF8.GetBytes(Page));
    }

    /// <summary>Each JBIG2 fixture with the header the target reads: size, then the globals' length.</summary>
    public static IEnumerable<(string Name, byte[] Data)> Jbig2(string fixtures)
    {
        string folder = Path.Combine(fixtures, "Jbig2");
        foreach (string path in Directory.GetFiles(folder, "*.jb2").Where(p => !p.EndsWith(".globals.jb2", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(path));
            if (!File.Exists(stem + ".png")) continue;
            byte[] png = File.ReadAllBytes(stem + ".png");
            int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            byte[] globals = File.Exists(stem + ".globals.jb2") ? File.ReadAllBytes(stem + ".globals.jb2") : [];
            byte[] data = File.ReadAllBytes(path);
            var seed = new MemoryStream();
            seed.Write([(byte)((width - 1) >> 8), (byte)(width - 1), (byte)((height - 1) >> 8), (byte)(height - 1)]);
            seed.Write([(byte)(globals.Length >> 24), (byte)(globals.Length >> 16), (byte)(globals.Length >> 8), (byte)globals.Length]);
            seed.Write(globals);
            seed.Write(data);
            yield return (Path.GetFileName(path), seed.ToArray());
        }
    }

    /// <summary>The CCITT fixtures (301 by 97 pixels) with the parameters they were coded with.</summary>
    public static IEnumerable<(string Name, byte[] Data)> Ccitt(string fixtures)
    {
        (string Name, sbyte K, byte Flags)[] coded = [("g4", -1, 0), ("g3-1d", 0, 1), ("g3-2d", 4, 1), ("g3-1d-aligned", 0, 3), ("mh", 0, 2)];
        foreach ((string name, sbyte k, byte flags) in coded)
        {
            string path = Path.Combine(fixtures, "Ccitt", name + ".bin");
            if (!File.Exists(path)) continue;
            const int Columns = 301, Rows = 97;
            byte[] header = [(byte)k, flags, (Columns - 1) >> 8, (Columns - 1) & 0xFF, Rows >> 8, Rows & 0xFF];
            yield return (name + ".bin", [.. header, .. File.ReadAllBytes(path)]);
        }
    }

    public static IEnumerable<(string Name, byte[] Data)> TrustedLists(string fixtures)
    {
        yield return ("list.xml", Encoding.UTF8.GetBytes(TrustedList));
    }

    public static IEnumerable<(string Name, byte[] Data)> CMaps(string fixtures)
    {
        yield return ("tounicode.cmap", Encoding.ASCII.GetBytes("""
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            /CMapName /Adobe-Identity-UCS def /CMapType 2 def
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            2 beginbfchar <0003> <0020> <0024> <00410042> endbfchar
            2 beginbfrange <0010> <001F> <0030> <0020> <0022> [<0061> <0062> <D83DDE00>] endbfrange
            endcmap CMapName currentdict /CMap defineresource pop end end
            """));
        yield return ("cid.cmap", Encoding.ASCII.GetBytes("""
            %!PS-Adobe-3.0 Resource-CMap
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> def
            /CMapName /Seed-H def /CMapType 1 def /WMode 0 def
            /UniJIS-UTF16-H usecmap
            2 begincodespacerange <00> <80> <8140> <FCFC> endcodespacerange
            1 begincidrange <20> <7e> 1 endcidrange
            1 begincidchar <8140> 633 endcidchar
            1 beginnotdefrange <00> <1f> 1 endnotdefrange
            endcmap CMapName currentdict /CMap defineresource pop end end
            """));
    }

    public static IEnumerable<(string Name, byte[] Data)> Content(string fixtures)
    {
        yield return ("text.content", Encoding.ASCII.GetBytes("""
            q 1 0 0 1 72 720 cm BT /F1 12 Tf 14 TL (Hello) Tj T* [(W) 120 (orld)] TJ 0 -14 Td <48656c6c6f> Tj ET Q
            /Span << /ActualText (fi) /MCID 0 >> BDC BT /F2 9 Tf 2 Tr 1 0 0 1 10 10 Tm (x) ' 1 2 (y) " ET EMC
            """));
        yield return ("paths.content", Encoding.ASCII.GetBytes("""
            0.5 g 1 0 0 RG 2 w [3 2] 0 d 0 J 1 j 10 M /GS1 gs
            10 10 m 100 10 l 100 100 50 150 10 100 c h B* 20 20 80 80 re W n /Sh1 sh /Im1 Do
            /CS0 cs 0.1 0.2 0.3 /P1 scn
            """));
        yield return ("inline-image.content", Encoding.ASCII.GetBytes("q 8 0 0 8 0 0 cm BI /W 4 /H 4 /BPC 8 /CS /G /F /AHx ID\n00112233445566778899AABBCCDDEEFF>\nEI Q\n"));
    }

    private static IEnumerable<(string Name, byte[] Data)> Files(string fixtures, string[] folders, string[] extensions)
    {
        foreach (string folder in folders)
        {
            string path = Path.Combine(fixtures, folder);
            if (!Directory.Exists(path)) continue;
            foreach (string file in Directory.GetFiles(path).Order(StringComparer.Ordinal))
            {
                if (!extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) || new FileInfo(file).Length > MaxBytes) continue;
                yield return ($"{folder}-{Path.GetFileName(file)}", File.ReadAllBytes(file));
            }
        }
    }

    private static byte[] TextDocument(string fixtures)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfFont embedded = PdfOpenTypeFont.Load(Path.Combine(fixtures, "Fonts", "Test-Regular.ttf"));

        // The test font covers only part of the alphabet.
        string covered = new([.. "Embedded TrueType subset 0123 abc".Where(c => embedded.HasGlyph(c))]);
        for (int i = 0; i < 2; i++)
        {
            PdfPage page = doc.Pages.Add(PdfRectangle.A4);
            using PdfGraphics g = page.CreateGraphics();
            g.DrawString($"Page {i + 1}: standard font", PdfStandardFont.Helvetica, 14, PdfColor.Black, 72, 72);
            if (covered.Trim().Length > 0) g.DrawString(covered, embedded, 12, PdfColor.FromHex("#1E3A8A"), 72, 100);
            g.DrawRectangle(null, PdfColor.FromHex("#DBEAFE"), 72, 130, 200, 60);
            page.Annotations.AddNote(300, 100, "A note");
            doc.Outlines.Add($"Page {i + 1}", page);
        }

        doc.Attachments.Add("data.txt", Encoding.ASCII.GetBytes("attached"));
        return doc.ToArray();
    }

    private static byte[] ImageDocument(string fixtures)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            string[] images = ["Jpeg/rgb-baseline.jpg", "Images/gray8.png", "Images/palette1-alpha.png", "Tiff/g4.tif", "Tiff/lzw-gray.tif"];
            double y = 40;
            foreach (string image in images)
            {
                string path = Path.Combine(fixtures, image);
                if (!File.Exists(path)) continue;
                g.DrawImage(PdfImage.Load(path), 40, y, 120, 100);
                y += 110;
            }
        }

        return doc.ToArray();
    }

    private static byte[] FormDocument()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAcroForm form = doc.Form;
        form.AddTextField(page, "name", 160, 100, 220, 20);
        form.AddCheckBox(page, "subscribe", 72, 134, 14, 14, isChecked: true);
        form.AddComboBox(page, "country", 160, 190, 150, 20, ["Belgium", "France", "Morocco"], selected: "France");
        form.AddSignatureField(page, "signature", 160, 340, 220, 50);
        page.Annotations.AddLink(72, 400, 200, 20, new Uri("https://example.com/"));
        return doc.ToArray();
    }

    private const string Page = """
        <!DOCTYPE html><html><head><style>
        body { font-family: Helvetica; margin: 20mm } h1 { color: #1E3A8A }
        table { border-collapse: collapse } td { border: 1px solid #999; padding: 4px }
        .row { display: flex; gap: 8px } .row div { flex: 1; background: #EFF6FF }
        @page { size: A4; @bottom-center { content: counter(page) } }
        </style></head><body>
        <h1>Invoice</h1><p dir="rtl">مرحبا</p><ul><li>One</li><li>Two</li></ul>
        <table><tr><td>Item</td><td>12.00</td></tr><tr><td colspan="2">Total</td></tr></table>
        <div class="row"><div>A</div><div>B</div></div>
        </body></html>
        """;

    private const string TrustedList = """
        <?xml version="1.0" encoding="UTF-8"?>
        <tsl:TrustServiceStatusList xmlns:tsl="http://uri.etsi.org/02231/v2#" xmlns:ecc="http://uri.etsi.org/TrstSvc/SvcInfoExt/eSigDir-1999-93-EC-TrustedList/#" Id="TSL-XX" TSLTag="http://uri.etsi.org/19612/TSLTag">
          <tsl:SchemeInformation>
            <tsl:TSLVersionIdentifier>6</tsl:TSLVersionIdentifier><tsl:TSLSequenceNumber>1</tsl:TSLSequenceNumber>
            <tsl:TSLType>http://uri.etsi.org/TrstSvc/TrustedList/TSLType/EUgeneric</tsl:TSLType>
            <tsl:SchemeOperatorName><tsl:Name xml:lang="en">Seed Operator</tsl:Name></tsl:SchemeOperatorName>
            <tsl:SchemeTerritory>XX</tsl:SchemeTerritory>
            <tsl:ListIssueDateTime>2026-01-01T00:00:00Z</tsl:ListIssueDateTime>
            <tsl:NextUpdate><tsl:dateTime>2026-07-01T00:00:00Z</tsl:dateTime></tsl:NextUpdate>
          </tsl:SchemeInformation>
          <tsl:TrustServiceProviderList><tsl:TrustServiceProvider>
            <tsl:TSPInformation><tsl:TSPName><tsl:Name xml:lang="en">Seed Provider</tsl:Name></tsl:TSPName></tsl:TSPInformation>
            <tsl:TSPServices><tsl:TSPService><tsl:ServiceInformation>
              <tsl:ServiceTypeIdentifier>http://uri.etsi.org/TrstSvc/Svctype/CA/QC</tsl:ServiceTypeIdentifier>
              <tsl:ServiceName><tsl:Name xml:lang="en">Seed CA</tsl:Name></tsl:ServiceName>
              <tsl:ServiceDigitalIdentity><tsl:DigitalId><tsl:X509SubjectName>CN=Seed CA</tsl:X509SubjectName></tsl:DigitalId></tsl:ServiceDigitalIdentity>
              <tsl:ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted</tsl:ServiceStatus>
              <tsl:StatusStartingTime>2020-01-01T00:00:00Z</tsl:StatusStartingTime>
              <tsl:ServiceInformationExtensions><tsl:Extension Critical="true"><ecc:Qualifications><ecc:QualificationElement>
                <ecc:Qualifiers><ecc:Qualifier uri="http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/QCWithQSCD"/></ecc:Qualifiers>
                <ecc:CriteriaList assert="atLeastOne"><ecc:KeyUsage><ecc:KeyUsageBit name="nonRepudiation">true</ecc:KeyUsageBit></ecc:KeyUsage></ecc:CriteriaList>
              </ecc:QualificationElement></ecc:Qualifications></tsl:Extension></tsl:ServiceInformationExtensions>
            </tsl:ServiceInformation></tsl:TSPService></tsl:TSPServices>
          </tsl:TrustServiceProvider></tsl:TrustServiceProviderList>
        </tsl:TrustServiceStatusList>
        """;
}
