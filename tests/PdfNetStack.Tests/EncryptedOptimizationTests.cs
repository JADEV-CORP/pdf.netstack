using System.Diagnostics;
using System.Reflection;
using System.Text;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Security;

namespace PdfNetStack.Tests;

/// <summary>
/// Encrypted sources: optimized decrypted and encrypted again exactly as they were, for every
/// security handler revision the library writes, and left alone wherever that cannot be done.
/// </summary>
[TestClass]
public sealed class EncryptedOptimizationTests
{
    // Print and copy, with the bits revision 3 and later reserve set.
    private const int Permissions = unchecked((int)0xFFFFF0C0) | 4 | 16;

    private const string Xmp =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>Metadata marker</dc:description>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private static readonly PdfSaveOptions Plain = new() { Optimize = false, UseObjectStreams = false };

    /// <summary>Every way the library writes encryption, with and without metadata in the clear.</summary>
    public static IEnumerable<object[]> Cases =>
        from name in new[] { "R2 RC4-40", "R3 RC4-40", "R3 RC4-128", "R4 AES-128", "R4 AES-128 clear metadata", "R6 AES-256", "R6 AES-256 clear metadata" }
        from objectStreams in new[] { false, true }
        select new object[] { name, objectStreams };

    private static StandardSecurity Security(string name, byte[] id0, string user = "") => name switch
    {
        "R2 RC4-40" => StandardSecurity.CreateLegacyRc4(user, "owner", Permissions, id0, keyBytes: 5, revision: 2),
        "R3 RC4-40" => StandardSecurity.CreateLegacyRc4(user, "owner", Permissions, id0, keyBytes: 5),
        "R3 RC4-128" => StandardSecurity.CreateLegacyRc4(user, "owner", Permissions, id0, keyBytes: 16),
        "R4 AES-128" => StandardSecurity.Create(user, "owner", Permissions, PdfEncryption.Aes128, id0),
        "R4 AES-128 clear metadata" => StandardSecurity.Create(user, "owner", Permissions, PdfEncryption.Aes128, id0, encryptMetadata: false),
        "R6 AES-256" => StandardSecurity.Create(user, "owner", Permissions, PdfEncryption.Aes256, id0),
        "R6 AES-256 clear metadata" => StandardSecurity.Create(user, "owner", Permissions, PdfEncryption.Aes256, id0, encryptMetadata: false),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// Two pages with compressible content, an annotation, an information dictionary holding
    /// strings in an array, a name tree whose keys are strings, and XMP metadata.
    /// </summary>
    private static PdfBuilder Source(string xmp = Xmp)
    {
        PdfBuilder b = PdfBuilder.Simple("Top secret", "Page two");
        b.SetStream(4, "", Repeat("BT /F1 24 Tf 72 700 Td (Top secret) Tj ET\n", 200));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/Annots[8 0 R]>>");
        b.Set(8, "<</Type/Annot/Subtype/Text/Rect[10 10 30 30]/Contents(A private note)/NM(note-1)>>");
        b.Set(9, "<</Title(Board minutes)/Reviewers[(Alice)(Bob)<FEFF00C9>]>>");
        b.SetStream(10, "/Type/Metadata/Subtype/XML", Encoding.UTF8.GetBytes(xmp));
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Metadata 10 0 R/Names<</Dests<</Names[(chapter-one)[5 0 R/Fit]]>>>>>>");
        return b;
    }

    private static byte[] Encrypted(string name, bool objectStreams, string user = "", string xmp = Xmp)
    {
        PdfDocument doc = PdfDocument.Open(Source(xmp).Build(trailerExtra: "/Info 9 0 R"));
        doc.Security.UseForTests(Security(name, doc.EnsureId(), user));
        return doc.ToArray(Plain with { UseObjectStreams = objectStreams });
    }

    private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

    private static PdfFile Unlocked(byte[] pdf, string? password = null)
    {
        PdfFile file = PdfFile.Open(pdf, 64 << 20);
        file.Unlock(password);
        return file;
    }

    /// <summary>The output opens with exactly the passwords of the source, in the same roles, with the same key and rights.</summary>
    private static void AssertProtectedAlike(byte[] input, byte[] output, string user = "")
    {
        PdfFile source = Unlocked(input, user);
        PdfFile written = Unlocked(output, user);
        Assert.IsNull(EncryptionEquivalence.Find(source, written));
        Assert.IsTrue(EncryptionEquivalence.SameValue(source.Security!.Dictionary, written.Security!.Dictionary, 0));
        CollectionAssert.AreEqual(source.Security.FileKey, written.Security.FileKey);
        CollectionAssert.AreEqual(source.Security.DocumentId, written.Security.DocumentId);
        Assert.IsFalse(written.Security.IsOwner);
        Assert.AreEqual(Permissions, written.Security.Permissions);

        StandardSecurity owner = Unlocked(output, "owner").Security!;
        Assert.IsTrue(owner.IsOwner);
        CollectionAssert.AreEqual(source.Security.FileKey, owner.FileKey);
        Assert.ThrowsExactly<PdfPasswordException>(() => Unlocked(output, "wrong"));
        if (user.Length > 0) Assert.ThrowsExactly<PdfPasswordException>(() => Unlocked(output));

        PdfDocument doc = PdfDocument.Open(output, user);
        Assert.AreEqual("Board minutes", doc.Info.Title);
        Assert.AreEqual(PdfPermissions.Print | PdfPermissions.CopyContent, doc.Security.Permissions);
    }

    private static int IndexOf(byte[] data, string text) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text));

    /// <summary>The metadata stream as stored, read without the key, then decoded: XMP when it is in the clear.</summary>
    private static string StoredMetadata(byte[] pdf)
    {
        PdfFile unlocked = Unlocked(pdf);
        var reference = (PdfReference)unlocked.ResolveDictionary(unlocked.Trailer["Root"])!["Metadata"]!;
        PdfFile locked = PdfFile.Open(pdf, 64 << 20);
        var stream = (PdfStream)locked.GetObject(reference.Number);
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, StreamCodec.GetFilters(stream.Dictionary, locked.Resolve), DecodeDepth.Full, 1 << 20, locked.Resolve);
            return Encoding.UTF8.GetString(decoded.Data.Span);
        }
        catch (StreamDecodeException)
        {
            return "";
        }
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void EncryptedFilesAreOptimizedAndEncryptedAgainAsTheyWere(string name, bool objectStreams)
    {
        byte[] input = Encrypted(name, objectStreams);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Reencrypted);
        StringAssert.Contains(result.Reason, "Encrypted again as the source was (");
        Assert.IsLessThan(input.Length, output.Length);
        Assert.IsGreaterThan(0, result.Statistics.ObjectsVerified);
        AssertProtectedAlike(input, output);

        // The public proof compares the pair decrypted, and finds them the same.
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));

        // Nothing is written in the clear but what the source kept in the clear.
        Assert.AreEqual(-1, IndexOf(output, "Board minutes"));
        Assert.AreEqual(-1, IndexOf(output, "private note"));
        Assert.AreEqual(-1, IndexOf(output, "chapter-one"));
        Assert.AreEqual(name.EndsWith("clear metadata", StringComparison.Ordinal), StoredMetadata(output) == Xmp);

        Assert.HasCount(2, PdfDocument.Open(output).Pages);
        PdfFile written = Unlocked(output);
        PdfDictionary pages = written.ResolveDictionary(written.ResolveDictionary(written.Trailer["Root"])!["Pages"])!;
        PdfDictionary page = written.ResolveDictionary(((PdfArray)written.Resolve(pages["Kids"])!)[0])!;
        PdfDictionary annotation = written.ResolveDictionary(((PdfArray)written.Resolve(page["Annots"])!)[0])!;
        Assert.AreEqual("A private note", ((PdfString)written.Resolve(annotation["Contents"])!).ToText());
        var reviewers = (PdfArray)written.Resolve(written.ResolveDictionary(written.Trailer["Info"])!["Reviewers"])!;
        CollectionAssert.AreEqual(new[] { "Alice", "Bob", "\u00C9" }, reviewers.Select(r => ((PdfString)r).ToText()).ToArray());
    }

    [TestMethod]
    [DataRow("R3 RC4-128")]
    [DataRow("R4 AES-128 clear metadata")]
    [DataRow("R6 AES-256")]
    public void StreamingModeEncryptsAsItWrites(string name)
    {
        byte[] input = Encrypted(name, objectStreams: true);
        using var source = new MemoryStream(input);
        using var sink = new MemoryStream();

        PdfOptimizationResult result = PdfOptimizer.OptimizeAsync(source, sink, new PdfOptimizerOptions { Streaming = true }).GetAwaiter().GetResult();

        byte[] output = sink.ToArray();
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Streamed);
        Assert.IsTrue(result.Statistics.Reencrypted);
        AssertProtectedAlike(input, output);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FilesThatNeedAPasswordAreOptimizedWithIt(bool streaming)
    {
        byte[] input = Encrypted("R6 AES-256", objectStreams: false, user: "user");
        var options = new PdfOptimizerOptions { Streaming = streaming };

        (byte[] output, PdfOptimizationResult result) = Optimize(input, options);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "needs a password");
        CollectionAssert.AreEqual(input, output);

        (output, result) = Optimize(input, options with { Password = "not it" });
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "neither the password given nor the empty user password");
        CollectionAssert.AreEqual(input, output);

        foreach (string password in new[] { "user", "owner" })
        {
            (output, result) = Optimize(input, options with { Password = password });
            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
            Assert.IsTrue(result.Statistics.Reencrypted);
            AssertProtectedAlike(input, output, user: "user");
        }
    }

    [TestMethod]
    public void APasswordThatDoesNotFitFallsBackToTheEmptyOne()
    {
        byte[] input = Encrypted("R4 AES-128", objectStreams: false);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Password = "meant for another file" });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        AssertProtectedAlike(input, output);
    }

    [TestMethod]
    public void EncryptedFilesAreLeftAloneWhenTurnedOff()
    {
        byte[] input = Encrypted("R6 AES-256", objectStreams: false);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { OptimizeEncryptedFiles = false });

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "OptimizeEncryptedFiles");
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void WithoutVerificationTheProtectionIsStillProven()
    {
        byte[] input = Encrypted("R4 AES-128", objectStreams: true);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Verify = false });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(0, result.Statistics.ObjectsVerified);
        AssertProtectedAlike(input, output);
    }

    [TestMethod]
    public void EncryptedFilesAreNotLinearized()
    {
        byte[] input = Encrypted("R6 AES-256", objectStreams: false);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { Linearize = true });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsFalse(result.Statistics.Linearized);
        StringAssert.Contains(result.Reason, "Not linearized: the linearized writer does not encrypt");
        AssertProtectedAlike(input, output);
    }

    [TestMethod]
    public void UsageRightsOfAnEncryptedFileAreRemovedOnlyOnRequest()
    {
        PdfBuilder b = Source();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Perms<</UR3 11 0 R>>>>");
        b.Set(11, "<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached/Contents<00>/ByteRange[0 1 2 3]" +
                  "/Reference[<</Type/SigRef/TransformMethod/UR3/TransformParams<</Type/TransformParams/V/2.2/Form[/FillIn]>>>>]>>");
        PdfDocument doc = PdfDocument.Open(b.Build(trailerExtra: "/Info 9 0 R"));
        doc.Security.UseForTests(Security("R4 AES-128", doc.EnsureId()));
        byte[] input = doc.ToArray(Plain);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "signed");
        CollectionAssert.AreEqual(input, output);

        (output, result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { RemoveUsageRights = true });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        StringAssert.Contains(result.Reason, "Usage rights removed");
        Assert.IsTrue(result.Statistics.Reencrypted);
        AssertProtectedAlike(input, output);
        PdfFile written = Unlocked(output);
        Assert.IsNull(written.ResolveDictionary(written.Trailer["Root"])!["Perms"]);
    }

    [TestMethod]
    public void SignedEncryptedFilesAreLeftAlone()
    {
        byte[] encrypted = Encrypted("R6 AES-256", objectStreams: false);
        byte[] input = [.. encrypted, .. "\n% /ByteRange [0 0 0 0]\n"u8.ToArray()];

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "signed");
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void EncryptedFilesClaimingPdfAAreLeftAlone()
    {
        string claim = Xmp.Replace("<dc:description>", "<pdfaid:part xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">2</pdfaid:part><dc:description>");
        byte[] input = Encrypted("R4 AES-128", objectStreams: false, xmp: claim);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "PDF/A");
        CollectionAssert.AreEqual(input, output);
    }

    /// <summary>A hand written file with an encryption dictionary given as PDF syntax, the rest stored as written.</summary>
    private static byte[] HandWritten(string encrypt, byte[] id0, Action<PdfBuilder>? edit = null)
    {
        PdfBuilder b = PdfBuilder.Simple("Top secret");
        edit?.Invoke(b);
        int number = b.Add(encrypt);
        string id = "<" + Compat.ToHex(id0) + ">";
        return b.Build(trailerExtra: $"/Encrypt {number} 0 R/ID[{id}{id}]");
    }

    /// <summary>The encryption dictionary of <paramref name="security"/> as PDF syntax, with entries added or replaced.</summary>
    private static string Syntax(StandardSecurity security, string extra = "", params string[] without)
    {
        var parts = new StringBuilder("<<");
        foreach (KeyValuePair<string, PdfObject> entry in security.Dictionary)
        {
            (string key, PdfObject value) = (entry.Key, entry.Value);
            if (without.Contains(key)) continue;
            parts.Append('/').Append(key).Append(' ').Append(value switch
            {
                PdfString s => "<" + Compat.ToHex(s.Bytes.ToArray()) + ">",
                PdfDictionary { } cf when key == "CF" => "<</StdCF<</CFM/" + ((PdfDictionary)cf["StdCF"]!).GetName("CFM") + "/AuthEvent/DocOpen/Length 16>>>>",
                PdfName n => "/" + n.Value,
                _ => value.ToString(),
            });
        }

        return parts.Append(extra).Append(">>").ToString();
    }

    [TestMethod]
    [DataRow("/Filter/Adobe.PubSec/SubFilter/adbe.pkcs7.s5/V 4/R 4/Recipients[<00>]", "public-key")]
    [DataRow("/Filter/MySecurity/V 2/R 3", "other than the standard one")]
    [DataRow("/Filter/Standard/V 3/R 3/O<00>/U<00>/P -4", "not supported together")]
    [DataRow("/Filter/Standard/V 2/R 3/O(x)/U(y)/P -4", "cannot be decrypted")]
    public void UnsupportedSecurityHandlersAreLeftAlone(string dictionary, string reason)
    {
        byte[] input = HandWritten("<<" + dictionary + ">>", [1, 2, 3]);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, reason);
        CollectionAssert.AreEqual(input, output);
    }

    /// <summary>Crypt filter settings that would be decrypted one way and encrypted again another are refused.</summary>
    [TestMethod]
    [DataRow("embedded files", "whose embedded files use a crypt filter of their own")]
    [DataRow("undefined filter", "names a crypt filter it does not define")]
    [DataRow("AESV3 in version 4", "crypt filter method its security handler version does not define")]
    [DataRow("clear metadata in version 2", "leaves metadata in the clear")]
    [DataRow("crypt filter on a stream", "choose a crypt filter of their own")]
    public void CryptFilterSettingsTheRewriteCannotKeepAreLeftAlone(string setting, string reason)
    {
        byte[] id0 = Compat.RandomBytes(16);
        StandardSecurity aes = StandardSecurity.Create("", "owner", Permissions, PdfEncryption.Aes128, id0);
        byte[] input = setting switch
        {
            "embedded files" => HandWritten(Syntax(aes, "/EFF/Identity"), id0),
            "undefined filter" => HandWritten(Syntax(aes, "/StmF/Missing", "StmF"), id0),
            "AESV3 in version 4" => HandWritten(Syntax(aes).Replace("/AESV2", "/AESV3"), id0),
            "clear metadata in version 2" => HandWritten(Syntax(StandardSecurity.CreateLegacyRc4("", "owner", Permissions, id0, 16), "/EncryptMetadata false"), id0),
            _ => HandWritten(Syntax(aes), id0, b => b.SetStream(4, "/Filter[/Crypt]/DecodeParms[<</Name/Identity>>]", Repeat("0 0 m 1 1 l S\n", 100))),
        };

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { AllowRepairedFiles = true });

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        StringAssert.Contains(result.Reason, reason);
        CollectionAssert.AreEqual(input, output);
    }

    /// <summary>
    /// Data stored unencrypted in an AES file has lengths and padding AES-CBC cannot produce. Like any
    /// repair, other readers may read it otherwise, so it is rewritten only on request.
    /// </summary>
    [TestMethod]
    public void DamagedAesDataIsRewrittenOnlyOnRequest()
    {
        byte[] id0 = Compat.RandomBytes(16);
        StandardSecurity aes = StandardSecurity.Create("", "owner", Permissions, PdfEncryption.Aes128, id0);
        byte[] input = HandWritten(Syntax(aes), id0, b =>
        {
            // Content of a length no AES data has, and an array of numbers, which is never encrypted, to save on.
            b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 101));
            b.Set(6, "[" + string.Concat(Enumerable.Repeat("0 ", 3000)) + "]");
            b.Set(1, "<</Type/Catalog/Pages 2 0 R/Extra 6 0 R>>");
        });

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        StringAssert.Contains(result.Reason, "AES data is damaged");
        CollectionAssert.AreEqual(input, output);

        (output, result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { AllowRepairedFiles = true });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        PdfFile source = Unlocked(input), written = Unlocked(output);
        Assert.IsNull(EncryptionEquivalence.Find(source, written));
        Assert.AreEqual(0, written.Security!.DamagedCiphertexts);
    }

    [TestMethod]
    public void TheProofCatchesProtectionThatChanged()
    {
        byte[] input = Encrypted("R6 AES-256", objectStreams: false);

        // Decrypted with the owner password and saved in the clear.
        PdfDocument owner = PdfDocument.Open(input, "owner");
        owner.Security.RemoveEncryption();
        byte[] clear = owner.ToArray(Plain);
        StringAssert.Contains(PdfEquivalence.FindDifference(input, clear), "the output is not encrypted");
        StringAssert.Contains(PdfEquivalence.FindDifference(clear, input), "the output is encrypted, the source is not");

        // Encrypted anew with the same passwords and rights: revision 6 draws a new file key and salts.
        PdfDocument again = PdfDocument.Open(input, "owner");
        again.Security.Encrypt("", "owner", PdfPermissions.Print | PdfPermissions.CopyContent);
        StringAssert.Contains(PdfEquivalence.FindDifference(input, again.ToArray(Plain)), "encryption dictionary differs");

        // Fewer rights for the user password.
        PdfDocument fewer = PdfDocument.Open(input, "owner");
        fewer.Security.Encrypt("", "owner", PdfPermissions.Print);
        StringAssert.Contains(PdfEquivalence.FindDifference(input, fewer.ToArray(Plain)), "encryption dictionary differs");

        // Another user password: the output is still encrypted, but the empty password no longer opens it.
        PdfDocument locked = PdfDocument.Open(input, "owner");
        locked.Security.Encrypt("user", "owner", PdfPermissions.Print | PdfPermissions.CopyContent);
        StringAssert.Contains(PdfEquivalence.FindDifference(input, locked.ToArray(Plain)), "only one of the two encrypted files was unlocked");

        // The same key and dictionary under another role: the user password against the owner password.
        PdfFile asUser = Unlocked(input), asOwner = Unlocked(input, "owner");
        StringAssert.Contains(EncryptionEquivalence.Find(asUser, asOwner), "other rights");
    }

    /// <summary>
    /// Mutated encrypted files: a rewrite must decrypt to the same document and be protected
    /// alike, anything else must come back as the exact input. RC4 data decrypts to something
    /// whatever is done to it; AES data of a broken length or padding is let through here.
    /// </summary>
    [TestMethod]
    [DataRow("R3 RC4-128", 1)]
    [DataRow("R4 AES-128", 2)]
    [DataRow("R6 AES-256 clear metadata", 3)]
    public void MutatedEncryptedInputIsRewrittenFaithfullyOrLeftAlone(string name, int seed)
    {
        byte[] baseline = Encrypted(name, objectStreams: seed % 2 == 0);
        var random = new Random(seed);
        var options = new PdfOptimizerOptions { MinSavingPercent = 0, AllowRepairedFiles = true, AllowIncrementalUpdates = true, MaxDegreeOfParallelism = 1 };
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("PDFNETSTACK_FUZZ_ITERATIONS"), out int n) ? n : 60;
        int rewritten = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            byte[] input = [.. baseline];
            for (int edits = random.Next(1, 4); edits > 0; edits--)
            {
                int at = random.Next(input.Length);
                input[at] = random.Next(3) == 0 ? (byte)"()<>[]/% "[random.Next(9)] : (byte)random.Next(256);
            }

            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, options);
            if (result.Disposition != OptimizationDisposition.Optimized)
            {
                CollectionAssert.AreEqual(input, output, $"iteration {iteration}: retained output must be the input");
                continue;
            }

            // A mutation in the trailer can take /Encrypt away: the file then reads as a plain one.
            Assert.IsNull(PdfEquivalence.FindDifference(input, output), $"iteration {iteration}: {result.Reason}");
            if (PdfFile.Open(input, 64 << 20).Trailer["Encrypt"] is null or PdfNull)
            {
                Assert.IsFalse(result.Statistics.Reencrypted, $"iteration {iteration}");
                continue;
            }

            rewritten++;
            Assert.IsTrue(result.Statistics.Reencrypted, $"iteration {iteration}");
            Assert.IsNull(EncryptionEquivalence.Find(Unlocked(input), Unlocked(output)), $"iteration {iteration}");
        }

        Assert.IsGreaterThan(0, rewritten);
    }

    [TestMethod]
    public void PasswordsStayOutOfTheOptionsText()
    {
        var options = new PdfOptimizerOptions { Password = "hunter2" };

        string text = options.ToString();

        Assert.IsFalse(text.Contains("hunter2"), text);
        StringAssert.Contains(text, "Password = (set)");
        StringAssert.Contains(new PdfOptimizerOptions().ToString(), "Password = null");
        foreach (PropertyInfo property in typeof(PdfOptimizerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            StringAssert.Contains(text, property.Name + " = ", "PrintMembers must list every public option.");
        }
    }

#if NET
    [TestMethod]
    public async Task TheCommandLineOpensEncryptedFilesWithThePasswordGiven()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-encrypted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "in.pdf"), output = Path.Combine(directory, "out.pdf");
            byte[] source = Encrypted("R6 AES-256", objectStreams: false, user: "user");
            await File.WriteAllBytesAsync(input, source);

            Assert.AreEqual(0, await PdfNetStack.Cli.Cli.RunAsync(["optimize", input, output, "--password", "user", "--json"]));
            AssertProtectedAlike(source, await File.ReadAllBytesAsync(output), user: "user");

            Assert.AreEqual(0, await PdfNetStack.Cli.Cli.RunAsync(["optimize", input, output, "--password", "user", "--no-encrypted"]));
            CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
#endif

#if NET
    /// <summary>
    /// qpdf, an independent implementation, reads every output with the source's passwords and
    /// reports the same security handler, permissions and methods for both files.
    /// </summary>
    [TestMethod]
    public void QpdfReadsEveryOutputLikeItsSource()
    {
        string? qpdf = FindOnPath("qpdf");
        if (qpdf is null) Assert.Inconclusive("qpdf is not installed.");

        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-qpdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (object[] testCase in Cases)
            {
                (string name, bool objectStreams) = ((string)testCase[0], (bool)testCase[1]);
                byte[] input = Encrypted(name, objectStreams);
                (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input);
                Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, name + ": " + result.Reason);
                string inPath = Path.Combine(directory, "in.pdf"), outPath = Path.Combine(directory, "out.pdf");
                File.WriteAllBytes(inPath, input);
                File.WriteAllBytes(outPath, output);

                (int check, string checkText) = Run(qpdf!, "--password=", "--check", outPath);
                Assert.AreEqual(0, check, name + ": " + checkText);
                StringAssert.Contains(checkText, "No syntax or stream encoding errors found", name);
                Assert.AreEqual(Run(qpdf!, "--password=", "--show-encryption", inPath).Output, Run(qpdf!, "--password=", "--show-encryption", outPath).Output, name);
                string ownerOut = Run(qpdf!, "--password=owner", "--show-encryption", outPath).Output;
                Assert.AreEqual(Run(qpdf!, "--password=owner", "--show-encryption", inPath).Output, ownerOut, name);

                // qpdf derives the revision 3 owner key from the first n bytes of each MD5 round,
                // where ISO 32000 and pdf.js hash all 16; only 40-bit keys tell the two apart, and
                // there qpdf refuses the owner password of the source and the output alike.
                if (name != "R3 RC4-40") StringAssert.Contains(ownerOut, "Supplied password is owner password", name);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindOnPath(string tool)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (string name in new[] { tool, tool + ".exe" })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private static (int Exit, string Output) Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
#endif

    private static (byte[] Output, PdfOptimizationResult Result) Optimize(byte[] input, PdfOptimizerOptions options)
    {
        if (!options.Streaming) return PdfOptimizer.Optimize(input, options);
        using var source = new MemoryStream(input);
        using var sink = new MemoryStream();
        PdfOptimizationResult result = PdfOptimizer.OptimizeAsync(source, sink, options).GetAwaiter().GetResult();
        return (sink.ToArray(), result);
    }
}
