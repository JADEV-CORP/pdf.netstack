using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Security;

/// <summary>
/// The part of the proof that covers encryption. The object walk compares two encrypted files
/// decrypted; this check proves the output is protected exactly as the source was. A standard
/// security handler decides which passwords open a file, with which rights and under which file
/// key, from its encryption dictionary and, for revisions 2 to 4, the first /ID entry, and from
/// nothing else (ISO 32000-2, 7.6.4.3 and 7.6.4.4). So when those are identical, and the password
/// that opened the source opens the output in the same role with the same file key, every
/// password opens the output exactly as it opens the source and grants the same permissions.
/// </summary>
internal static class EncryptionEquivalence
{
    /// <summary>
    /// Null when the output is protected exactly as the source is, or neither is encrypted;
    /// otherwise what differs. Both files must have been unlocked with the same password. Two
    /// encrypted files that are both still locked are left to the object walk, which then
    /// compares them as stored.
    /// </summary>
    public static string? Find(PdfFile source, PdfFile output)
    {
        bool sourceEncrypted = IsEncrypted(source);
        bool outputEncrypted = IsEncrypted(output);
        if (!sourceEncrypted && !outputEncrypted) return null;
        if (!outputEncrypted) return "the output is not encrypted, the source is";
        if (!sourceEncrypted) return "the output is encrypted, the source is not";

        StandardSecurity? a = source.Security;
        StandardSecurity? b = output.Security;
        if (a is null && b is null) return null;
        if (a is null || b is null) return "only one of the two encrypted files was unlocked";

        if (!SameValue(a.Dictionary, b.Dictionary, 0)) return "the output's encryption dictionary differs from the source's";
        if (!a.DocumentId.AsSpan().SequenceEqual(b.DocumentId)) return "the output's first /ID entry differs from the source's";
        if (!StandardSecurity.FixedTimeEquals(a.FileKey, b.FileKey)) return "the output's file key differs from the source's";
        if (a.IsOwner != b.IsOwner || a.Permissions != b.Permissions) return "the password opens the output with other rights than the source";
        return null;
    }

    private static bool IsEncrypted(PdfFile file) => file.Trailer["Encrypt"] is not (null or PdfNull);

    /// <summary>
    /// Structural equality of two direct values, as encryption dictionaries hold them once read:
    /// the same names, numbers (by canonical text), strings (by bytes) and booleans, arrays item
    /// by item, dictionaries key by key in any order. Anything else, references included, differs.
    /// </summary>
    internal static bool SameValue(PdfObject? a, PdfObject? b, int depth) => depth <= 32 && (a, b) switch
    {
        (null or PdfNull, null or PdfNull) => true,
        (PdfName x, PdfName y) => x.Value == y.Value,
        (PdfNumber x, PdfNumber y) => x.Text.AsSpan().SequenceEqual(y.Text),
        (PdfString x, PdfString y) => x.Value.AsSpan().SequenceEqual(y.Value),
        (PdfBoolean x, PdfBoolean y) => x.Value == y.Value,
        (PdfArray x, PdfArray y) => SameArray(x, y, depth),
        (PdfDictionary x, PdfDictionary y) => SameDictionary(x, y, depth),
        _ => false,
    };

    private static bool SameArray(PdfArray a, PdfArray b, int depth)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!SameValue(a[i], b[i], depth + 1)) return false;
        }

        return true;
    }

    private static bool SameDictionary(PdfDictionary a, PdfDictionary b, int depth)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            (string key, PdfObject value) = a.EntryAt(i);
            if (!b.TryGetValue(key, out PdfObject other) || !SameValue(value, other, depth + 1)) return false;
        }

        return true;
    }
}
