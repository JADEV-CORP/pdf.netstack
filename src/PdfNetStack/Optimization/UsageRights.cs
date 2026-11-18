using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Usage rights: the signature Adobe Reader checks before it enables features such as saving
/// filled forms or commenting (the catalog's /Perms /UR3, ISO 32000-1 12.8.2.3, or /UR in older
/// files). Any rewrite changes the signed bytes, so the signature stops verifying and Reader warns
/// that the extended features are no longer available. Removing the entry instead leaves a plain
/// PDF that opens without that warning.
/// </summary>
internal static class UsageRights
{
    private static readonly string[] Keys = ["UR3", "UR"];

    /// <summary>
    /// Removes /Perms from the catalog when it holds nothing but usage rights and those hold every
    /// signature the file's bytes show, so a certified or otherwise signed file is never touched.
    /// </summary>
    /// <param name="byteRanges">How many /ByteRange keys the raw file holds.</param>
    /// <returns>Whether /Perms was removed.</returns>
    public static bool Remove(PdfFile file, int byteRanges)
    {
        // A locked file's catalog is read as stored; an unlocked one is read decrypted.
        if (file.Trailer["Encrypt"] is not null and not PdfNull && file.Security is null) return false;
        if (file.ResolveDictionary(file.Trailer["Root"]) is not { } catalog) return false;
        if (file.ResolveDictionary(catalog["Perms"]) is not { Count: > 0 } perms) return false;

        int signatures = 0;
        for (int i = 0; i < perms.Count; i++)
        {
            (string key, PdfObject value) = perms.EntryAt(i);

            // /DocMDP is a certification signature: the file stays as it is.
            if (Array.IndexOf(Keys, key) < 0) return false;
            if (file.ResolveDictionary(value)?.ContainsKey("ByteRange") == true) signatures++;
        }

        if (byteRanges > signatures) return false;
        catalog.Remove("Perms");
        return true;
    }

    /// <summary>How many times /ByteRange appears in <paramref name="data"/>.</summary>
    public static int CountByteRanges(ReadOnlySpan<byte> data)
    {
        int count = 0;
        for (int at = data.IndexOf("/ByteRange"u8); at >= 0; at = data.IndexOf("/ByteRange"u8))
        {
            count++;
            data = data[(at + 1)..];
        }

        return count;
    }
}
