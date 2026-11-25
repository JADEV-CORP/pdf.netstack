using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>
/// A signature prepared by
/// <see cref="PdfSigner.PrepareSignatureAsync(Stream, Stream, X509Certificate2, PdfSignatureAlgorithm, PdfSignatureOptions?, CancellationToken)"/>
/// and waiting for its value: the hash to have signed, and what
/// <see cref="PdfSigner.CompleteSignatureAsync(Stream, Stream, PdfDeferredSignature, byte[], PdfTimestampClient?, CancellationToken)"/>
/// needs to finish the prepared file.
/// </summary>
/// <remarks>
/// <see cref="Export"/> turns it into a few kilobytes to keep between the two steps, in a database
/// row or a cache entry, and <see cref="Import(byte[])"/> reads them back, in another process if
/// need be. The state holds the signer's certificates, the hash algorithm and scheme, the digest of
/// the prepared file and where its Contents string lies: public data only, no key and not the
/// document. Completing checks the prepared file against it and the signature against the
/// certificate, so a state that was altered or paired with another file fails instead of
/// producing an invalid signature.
/// </remarks>
public sealed class PdfDeferredSignature
{
    private const int Version = 1;

    /// <summary>
    /// The widest Contents string a state may name. The first step reserves tens of kilobytes; the
    /// bound keeps a damaged or hostile state from making the second step allocate gigabytes.
    /// </summary>
    private const long MaxContentsWidth = 4L << 20;
    private readonly byte[] _documentDigest;
    private readonly byte[] _signedAttributes;
    private readonly byte[] _digest;

    internal PdfDeferredSignature(X509Certificate2 certificate, byte[][] chain, PdfSignatureAlgorithm signatureAlgorithm, PdfDigestAlgorithm digestAlgorithm, byte[] documentDigest, long contentsStart, long contentsEnd, long length)
    {
        Certificate = certificate;
        Chain = chain;
        SignatureAlgorithm = signatureAlgorithm;
        HashAlgorithmName hash = PdfSigner.HashName(digestAlgorithm);

        // An undefined value signs with SHA-256, as everywhere in the library; say so, so an export and import keep it.
        DigestAlgorithm = hash == HashAlgorithmName.SHA512 ? PdfDigestAlgorithm.Sha512 : hash == HashAlgorithmName.SHA384 ? PdfDigestAlgorithm.Sha384 : PdfDigestAlgorithm.Sha256;
        _documentDigest = documentDigest;
        ContentsStart = contentsStart;
        ContentsEnd = contentsEnd;
        Length = length;
        _signedAttributes = Cms.SignedAttributes(documentDigest, hash, certificate);
        _digest = Pkix.Hash(hash, _signedAttributes);
    }

    /// <summary>The signer's certificate, without a private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The scheme the signature must use.</summary>
    public PdfSignatureAlgorithm SignatureAlgorithm { get; }

    /// <summary>The hash algorithm of <see cref="Digest"/> and of the signature.</summary>
    public PdfDigestAlgorithm DigestAlgorithm { get; }

    /// <summary>
    /// The hash to sign: of the CMS signed attributes, which carry the digest of the prepared file.
    /// Send it to the key's sign operation for a hash (Key Vault Sign, KMS Sign with MessageType
    /// DIGEST, the CSC API's signatures/signHash), and pass the signature it returns to
    /// <see cref="PdfSigner.CompleteSignatureAsync(Stream, Stream, PdfDeferredSignature, byte[], PdfTimestampClient?, CancellationToken)"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Digest => _digest;

    /// <summary>
    /// The DER encoded signed attributes that <see cref="Digest"/> is the hash of, for a signer
    /// that hashes what it signs, such as a PKCS #11 mechanism that includes the hash.
    /// </summary>
    public ReadOnlyMemory<byte> DataToSign => _signedAttributes;

    /// <summary>The intermediate certificates to embed, DER encoded.</summary>
    internal byte[][] Chain { get; }

    /// <summary>Where the empty Contents string starts in the prepared file, at its '&lt;'.</summary>
    internal long ContentsStart { get; }

    /// <summary>Where it ends, just after its '&gt;'.</summary>
    internal long ContentsEnd { get; }

    /// <summary>The prepared file's length.</summary>
    internal long Length { get; }

    /// <summary>The hash of the prepared file outside the Contents string; the message digest the signed attributes carry.</summary>
    internal ReadOnlySpan<byte> DocumentDigest => _documentDigest;

    internal byte[] SignedAttributes => _signedAttributes;

    /// <summary>The state as bytes, to keep until the signature arrives; <see cref="Import(byte[])"/> reads it back.</summary>
    /// <remarks>
    /// The format is DER: a version, the hash and scheme, the prepared file's digest, the
    /// certificates (signer first), and the Contents string's start and end and the file's length.
    /// </remarks>
    public byte[] Export()
    {
        byte[][] certificates = [Certificate.RawData, .. Chain];
        return Der.Seq(
            Der.Int(Version),
            Der.Oid(Pkix.HashOid(PdfSigner.HashName(DigestAlgorithm))),
            Der.Tlv(Der.Enumerated, [(byte)SignatureAlgorithm]),
            Der.Octets(_documentDigest),
            Der.Seq(certificates),
            Der.Int(ContentsStart),
            Der.Int(ContentsEnd),
            Der.Int(Length));
    }

    /// <summary>Reads a state written by <see cref="Export"/>.</summary>
    /// <param name="state">The exported bytes.</param>
    /// <exception cref="FormatException">The bytes are not a state this version of the library wrote, or they are inconsistent.</exception>
    public static PdfDeferredSignature Import(byte[] state)
    {
        Compat.ThrowIfNull(state, nameof(state));
        try
        {
            List<DerNode> parts = DerNode.Parse(state).Children();
            if (parts.Count != 8 || parts[0].SmallInteger() != Version) throw new FormatException("The deferred signature state has an unknown version.");
            HashAlgorithmName hash = Pkix.HashFromOid(parts[1].OidValue());
            PdfDigestAlgorithm digestAlgorithm = hash == HashAlgorithmName.SHA512 ? PdfDigestAlgorithm.Sha512 : hash == HashAlgorithmName.SHA384 ? PdfDigestAlgorithm.Sha384 : PdfDigestAlgorithm.Sha256;

            if (parts[2].Tag != Der.Enumerated) throw new FormatException("The signature algorithm is not an enumeration.");
            var algorithm = (PdfSignatureAlgorithm)parts[2].SmallInteger();
            if (parts[3].Tag != Der.OctetString || parts[3].Content.Length != Pkix.HashLength(hash)) throw new FormatException("The document digest does not match its algorithm.");
            byte[] documentDigest = parts[3].Content.ToArray();
            List<DerNode> certificates = parts[4].Children();
            if (parts[4].Tag != Der.Sequence || certificates.Count == 0) throw new FormatException("The state names no certificate.");
            X509Certificate2 certificate = Pkix.Load(certificates[0].Encoded.ToArray());
            var chain = new byte[certificates.Count - 1][];
            for (int i = 1; i < certificates.Count; i++)
            {
                // Loaded only to refuse bytes that are not a certificate; the DER is what the signature embeds.
                using X509Certificate2 intermediate = Pkix.Load(certificates[i].Encoded.ToArray());
                chain[i - 1] = intermediate.RawData;
            }

            long start = parts[5].SmallInteger(), end = parts[6].SmallInteger(), length = parts[7].SmallInteger();

            // The Contents string is '<', hex digit pairs and '>', of a sane width, inside the file.
            long width = end - start;
            if (start <= 0 || width < 2 || width % 2 != 0 || width > MaxContentsWidth || end > length) throw new FormatException("The positions of the Contents string are invalid.");
            SignatureValues.CheckKey(certificate, algorithm, nameof(state), nameof(state));
            return new PdfDeferredSignature(certificate, chain, algorithm, digestAlgorithm, documentDigest, start, end, length);
        }
        catch (ArgumentException e)
        {
            throw new FormatException("The deferred signature state cannot be read: " + e.Message, e);
        }
        catch (FormatException e)
        {
            throw new FormatException("The deferred signature state cannot be read: " + e.Message, e);
        }
    }
}
