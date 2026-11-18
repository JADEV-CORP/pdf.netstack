using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Security;
using PdfNetStack.Writing;

namespace PdfNetStack;

/// <summary>
/// Encrypted sources (<see cref="PdfOptimizerOptions.OptimizeEncryptedFiles"/>). The source is
/// unlocked, so every pass works on decrypted objects, and the rewrite is encrypted as it is
/// written with the source's own security handler: its encryption dictionary, unchanged, its
/// file key and its first /ID entry. The proof unlocks the output with the password that opened
/// the source, compares both decrypted, and checks the protection is the same
/// (<see cref="EncryptionEquivalence"/>). Nothing is ever written in the clear.
/// </summary>
public static partial class PdfOptimizer
{
    /// <summary>Why an encrypted source is written without linearization.</summary>
    private const string EncryptedNotLinearized = "the linearized writer does not encrypt, and an encrypted file is never written in the clear";

    private static bool IsEncrypted(PdfFile file) => file.Trailer["Encrypt"] is not (null or PdfNull);

    /// <summary>
    /// Unlocks an encrypted source for the passes, or says why it stays as it is. Every object read
    /// afterwards is decrypted. Nothing is done to a file that is not encrypted.
    /// </summary>
    /// <param name="password">The password that opened the source, which must open the output too.</param>
    private static string? UnlockSource(PdfFile file, PdfOptimizerOptions options, out string? password)
    {
        password = null;
        if (!IsEncrypted(file)) return null;
        if (!options.OptimizeEncryptedFiles) return "Encrypted PDF. Set OptimizeEncryptedFiles to rewrite it with the same encryption.";
        if (file.Resolve(file.Trailer["Encrypt"]) is not PdfDictionary dictionary) return "Encrypted PDF whose encryption dictionary cannot be read.";
        if (dictionary.GetName("Filter") != "Standard")
        {
            return dictionary.GetName("Filter") == "Adobe.PubSec" || dictionary.GetName("SubFilter")?.StartsWith("adbe.pkcs7", StringComparison.Ordinal) == true
                ? "Encrypted PDF with the public-key security handler, which only a recipient's private key opens."
                : "Encrypted PDF with a security handler other than the standard one.";
        }

        // The key of revisions 2 to 4 is derived from the first /ID entry as stored. An indirect
        // /ID would be read decrypted once the file is unlocked, and written back changed.
        if (file.Trailer["ID"] is PdfReference) return "Encrypted PDF whose /ID is an indirect object.";
        int v = Integer(file, dictionary["V"]), r = Integer(file, dictionary["R"]);
        bool known = (v is 1 or 2 && r is 2 or 3) || (v == 4 && r == 4) || (v == 5 && r is 5 or 6);
        if (!known) return Compat.Format("Encrypted PDF with security handler version {0} and revision {1}, which are not supported together.", v, r);

        try
        {
            password = Authenticate(file, options.Password);
        }
        catch (PdfPasswordException)
        {
            return options.Password is null
                ? "Encrypted PDF that needs a password to open. Pass it in PdfOptimizerOptions.Password."
                : "Encrypted PDF that neither the password given nor the empty user password opens.";
        }
        catch (Exception ex) when (ex is NotSupportedException or PdfFormatException)
        {
            return "Encrypted PDF that cannot be decrypted: " + ex.Message;
        }

        if (UnsupportedCryptFilters(file.Security!, v) is { } filters) return "Encrypted PDF " + filters + ".";
        if (ClaimsPdfA(file, options.MaxDecodedStreamBytes)) return "Encrypted PDF that claims PDF/A conformance, which forbids encryption.";
        return null;
    }

    /// <summary>
    /// Opens with <paramref name="password"/>, or failing that with the empty user password that
    /// opens most encrypted files, so one password can serve a batch.
    /// </summary>
    /// <returns>The password that opened the file; null for the empty one.</returns>
    /// <exception cref="PdfPasswordException">Neither opens it.</exception>
    private static string? Authenticate(PdfFile file, string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            try
            {
                file.Unlock(password);
                return password;
            }
            catch (PdfPasswordException)
            {
                // The empty user password may still open it.
            }
        }

        file.Unlock(null);
        return null;
    }

    /// <summary>
    /// Crypt filter settings the rewrite could not keep exactly as they are, read from the
    /// resolved encryption dictionary; null when every one is kept. The reader decrypts embedded
    /// files with the stream filter and knows each method only with the key its version defines,
    /// so anything else would be decrypted one way and encrypted again another.
    /// </summary>
    private static string? UnsupportedCryptFilters(StandardSecurity security, int v)
    {
        PdfDictionary enc = security.Dictionary;
        if (v < 4)
        {
            // /EncryptMetadata means something from version 4 on. Before, metadata is encrypted
            // like any stream, while the reader would take it as stored.
            return security.EncryptMetadata ? null : "that leaves metadata in the clear with security handler version " + v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string streams = enc.GetName("StmF") ?? "Identity";
        if (enc["EFF"] is { } eff && (eff as PdfName)?.Value != streams) return "whose embedded files use a crypt filter of their own";

        var filters = enc["CF"] as PdfDictionary;
        foreach (string key in new[] { "StmF", "StrF" })
        {
            string name = enc.GetName(key) ?? "Identity";
            if (name == "Identity") continue;
            if (filters?[name] is not PdfDictionary filter) return "whose " + key + " names a crypt filter it does not define";
            string method = filter.GetName("CFM") ?? "None";
            bool fits = method == "None" || (v == 4 ? method is "V2" or "AESV2" : method == "AESV3");
            if (!fits) return "with a crypt filter method its security handler version does not define";
        }

        return null;
    }

    /// <summary>
    /// Why an unlocked source stays as it is, from what walking its object graph showed: streams
    /// that choose a crypt filter of their own lose that choice when decrypted, and would be
    /// written with the default filter.
    /// </summary>
    private static string? UnsupportedEncryptedContent(PdfFile file) =>
        file.CryptFilterStreams > 0
            ? Compat.Format("Encrypted PDF with {0} stream(s) that choose a crypt filter of their own.", file.CryptFilterStreams)
            : null;

    /// <summary>
    /// AES data of a wrong length or padding was decrypted as far as it goes, as viewers do. Others
    /// may recover it differently, so like any repaired file it is rewritten only on request.
    /// </summary>
    private static string? DamagedCiphertext(StandardSecurity security, PdfOptimizerOptions options) =>
        security.DamagedCiphertexts > 0 && !options.AllowRepairedFiles
            ? "Encrypted PDF whose AES data is damaged in places (wrong length or padding). Set AllowRepairedFiles to rewrite it."
            : null;

    /// <summary>
    /// Sets the rewrite up to be encrypted as the source is: the source's encryption dictionary,
    /// unchanged, as the last object, outside object streams and never encrypted itself, named by
    /// the trailer; every other string and stream encrypted with the source's file key under its
    /// output number. The trailer keeps the source's /ID, from which revisions 2 to 4 derive the key.
    /// </summary>
    /// <returns>The reference map, which also maps the trailer's /Encrypt.</returns>
    private static Func<PdfReference, int> EncryptLikeSource(StandardSecurity security, List<PdfObject> objects, PdfDictionary trailer, Func<PdfReference, int> map, ref WriterSettings settings)
    {
        objects.Add(security.Dictionary);
        int number = objects.Count;
        var encrypt = new PdfReference(int.MaxValue, 0);
        trailer.Set("Encrypt", encrypt);
        settings = settings with { Encryption = security, EncryptionDictionaryNumber = number };
        return r => ReferenceEquals(r, encrypt) ? number : map(r);
    }

    /// <summary>
    /// Unlocks both sides of the proof with the password that opened the source. That the output
    /// opens with it is part of what is proven; the rest is <see cref="EncryptionEquivalence"/>.
    /// </summary>
    private static string? UnlockForProof(PdfFile reference, PdfFile written, string? password)
    {
        try
        {
            if (reference.Security is null) reference.Unlock(password);
        }
        catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException or PdfFormatException)
        {
            return "the source does not open again with the password that opened it";
        }

        try
        {
            written.Unlock(password);
        }
        catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException or PdfFormatException)
        {
            return "the output does not open with the password that opened the source";
        }

        return null;
    }

    /// <summary>
    /// With <see cref="PdfOptimizerOptions.Verify"/> off the content is taken on trust, but the
    /// protection is still proven: a mistake there would lock every reader out of the file.
    /// </summary>
    private static string? ProveProtection(PdfFile source, PdfFile written, string? password) =>
        UnlockForProof(source, written, password) ?? EncryptionEquivalence.Find(source, written);

    /// <summary>What the reason adds for a file encrypted again.</summary>
    private static string Reencrypted(StandardSecurity security) => " Encrypted again as the source was (" + security.Describe() + ").";

    /// <summary>The catalog's XMP names a PDF/A part. Unreadable metadata claims nothing.</summary>
    private static bool ClaimsPdfA(PdfFile file, long decodeLimit)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (file.Resolve(catalog?["Metadata"]) is not PdfStream metadata) return false;
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(metadata.Data, StreamCodec.GetFilters(metadata.Dictionary, file.Resolve),
                DecodeDepth.Full, decodeLimit, file.Resolve, file.Budget);
            return decoded.Data.Span.IndexOf("pdfaid:part"u8) >= 0;
        }
        catch (StreamDecodeException)
        {
            return false;
        }
    }

    private static int Integer(PdfFile file, PdfObject? value) =>
        file.Resolve(value) is PdfNumber { IsInteger: true } n && n.IntegerValue is >= int.MinValue and <= int.MaxValue ? (int)n.IntegerValue : 0;
}
