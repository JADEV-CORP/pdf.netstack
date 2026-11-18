using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Security;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class SecurityTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static PdfDocument Sample()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("Top secret", "Page two").Build());
        doc.Info.Title = "Quarterly figures";
        return doc;
    }

    private static string Text(PdfPage page) =>
        page.Document.Resolve(page.Dictionary["Contents"]) is PdfStream s ? Encoding.ASCII.GetString(s.Data.Span) : "";

    [TestMethod]
    public void Rc4MatchesTheReferenceVector() =>
        Assert.AreEqual("BBF316E8D940AF0AD3", Compat.ToHex(Rc4.Apply("Key"u8.ToArray(), "Plaintext"u8)));

    [TestMethod]
    [DataRow(PdfEncryption.Aes256)]
    [DataRow(PdfEncryption.Aes128)]
    public void EncryptedOutputNeedsThePasswordAndHidesContent(PdfEncryption algorithm)
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("user", "owner", PdfPermissions.Print | PdfPermissions.CopyContent, algorithm);
        byte[] bytes = doc.ToArray();

        string raw = Compat.Latin1.GetString(bytes);
        Assert.AreEqual(-1, raw.IndexOf("Top secret", StringComparison.Ordinal));
        Assert.AreEqual(-1, raw.IndexOf("Quarterly", StringComparison.Ordinal));
        StringAssert.Contains(raw, "/Encrypt");

        Assert.ThrowsExactly<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.ThrowsExactly<PdfPasswordException>(() => PdfDocument.Open(bytes, "wrong"));

        PdfDocument asUser = PdfDocument.Open(bytes, "user");
        Assert.IsTrue(asUser.Security.WasEncrypted);
        Assert.IsFalse(asUser.Security.HasOwnerAccess);
        Assert.AreEqual(PdfPermissions.Print | PdfPermissions.CopyContent, asUser.Security.Permissions);
        StringAssert.Contains(Text(asUser.Pages[0]), "(Top secret)");
        Assert.AreEqual("Quarterly figures", asUser.Info.Title);
        Assert.ThrowsExactly<InvalidOperationException>(() => asUser.ToArray());

        PdfDocument asOwner = PdfDocument.Open(bytes, "owner");
        Assert.IsTrue(asOwner.Security.HasOwnerAccess);
        StringAssert.Contains(Text(asOwner.Pages[1]), "(Page two)");
    }

    [TestMethod]
    [DataRow(PdfEncryption.Aes256)]
    [DataRow(PdfEncryption.Aes128)]
    public void OwnerEditsKeepTheOriginalPasswords(PdfEncryption algorithm)
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("user", "owner", PdfPermissions.Print, algorithm);
        byte[] encrypted = doc.ToArray();

        PdfDocument edit = PdfDocument.Open(encrypted, "owner");
        edit.Pages.RemoveAt(1);
        edit.Info.Author = "Finance";
        byte[] saved = edit.ToArray();

        Assert.ThrowsExactly<PdfPasswordException>(() => PdfDocument.Open(saved));
        PdfDocument back = PdfDocument.Open(saved, "user");
        Assert.HasCount(1, back.Pages);
        Assert.AreEqual("Finance", back.Info.Author);
        Assert.AreEqual(PdfPermissions.Print, back.Security.Permissions);
        Assert.IsTrue(PdfDocument.Open(saved, "owner").Security.HasOwnerAccess);
    }

    [TestMethod]
    public void LegacyRc4FilesAreReadWithEitherPassword()
    {
        PdfDocument doc = Sample();
        byte[] id0 = doc.EnsureId();
        doc.Security.UseForTests(StandardSecurity.CreateLegacyRc4("u", "o", unchecked((int)0xFFFFF0C4), id0, keyBytes: 16));
        byte[] bytes = doc.ToArray(Plain);

        Assert.ThrowsExactly<PdfPasswordException>(() => PdfDocument.Open(bytes));
        StringAssert.Contains(Text(PdfDocument.Open(bytes, "u").Pages[0]), "(Top secret)");
        PdfDocument owner = PdfDocument.Open(bytes, "o");
        Assert.IsTrue(owner.Security.HasOwnerAccess);
        Assert.AreEqual("Quarterly figures", owner.Info.Title);
    }

    [TestMethod]
    public void EmptyUserPasswordOpensWithoutAPassword()
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("", "owner", PdfPermissions.Print);
        byte[] bytes = doc.ToArray();

        PdfDocument opened = PdfDocument.Open(bytes);
        Assert.IsFalse(opened.Security.HasOwnerAccess);
        StringAssert.Contains(Text(opened.Pages[0]), "(Top secret)");
        Assert.ThrowsExactly<InvalidOperationException>(() => opened.Security.RemoveEncryption());
    }

    [TestMethod]
    public void RemovingEncryptionWritesAPlainFile()
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("user", "owner");
        PdfDocument owner = PdfDocument.Open(doc.ToArray(), "owner");
        owner.Security.RemoveEncryption();
        byte[] plain = owner.ToArray(Plain);

        Assert.IsNull(PdfFile.Open(plain, 1 << 20).Trailer["Encrypt"]);
        PdfDocument back = PdfDocument.Open(plain);
        Assert.IsFalse(back.Security.WasEncrypted);
        StringAssert.Contains(Text(back.Pages[0]), "(Top secret)");
    }

    [TestMethod]
    public void UnicodePasswordsWorkWithAes256()
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("pässwörd-مرحبا", "öwner");
        byte[] bytes = doc.ToArray();
        Assert.AreEqual("Quarterly figures", PdfDocument.Open(bytes, "pässwörd-مرحبا").Info.Title);
    }

    [TestMethod]
    public void RestrictedSourcesCannotBeCopiedFrom()
    {
        PdfDocument doc = Sample();
        doc.Security.Encrypt("", "owner", PdfPermissions.Print);
        PdfDocument restricted = PdfDocument.Open(doc.ToArray());

        PdfDocument target = PdfDocument.Create();
        Assert.ThrowsExactly<InvalidOperationException>(() => target.Pages.Import(restricted.Pages));
    }

    [TestMethod]
    public void OptimizerKeepsTheEncryptionOrLeavesTheFileAlone()
    {
        // Content written long hand, so the rewrite saves something.
        PdfBuilder b = PdfBuilder.Simple("Top secret");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 24 Tf 72 700 Td (Top secret) Tj ET\n", 100))));
        PdfDocument doc = PdfDocument.Open(b.Build());
        doc.Info.Title = "Quarterly figures";
        doc.Security.Encrypt("", "owner");
        byte[] bytes = doc.ToArray(Plain);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(bytes);
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Reencrypted);
        string raw = Compat.Latin1.GetString(output);
        Assert.AreEqual(-1, raw.IndexOf("Quarterly", StringComparison.Ordinal));
        PdfDocument opened = PdfDocument.Open(output);
        Assert.IsFalse(opened.Security.HasOwnerAccess);
        Assert.AreEqual("Quarterly figures", opened.Info.Title);
        Assert.IsTrue(PdfDocument.Open(output, "owner").Security.HasOwnerAccess);

        (output, result) = PdfOptimizer.Optimize(bytes, new PdfOptimizerOptions { OptimizeEncryptedFiles = false });
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(bytes, output);
    }
}
