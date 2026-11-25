using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfNetStack.Signing;

/// <summary>How the signature value is computed from the digest of the signed attributes.</summary>
public enum PdfSignatureAlgorithm
{
    /// <summary>RSA with PKCS #1 v1.5 padding (RSASSA-PKCS1-v1_5), what most signing certificates use.</summary>
    RsaPkcs1,

    /// <summary>
    /// RSA with PSS padding (RSASSA-PSS, RFC 4055): MGF1 with the signature's hash and a salt as
    /// long as the hash, as .NET, Azure Key Vault (PS256 to PS512), AWS KMS (RSASSA_PSS_SHA_*)
    /// and PKCS #11 (CKM_RSA_PKCS_PSS) produce it.
    /// </summary>
    RsaPss,

    /// <summary>ECDSA on the certificate's curve, such as P-256 or P-384.</summary>
    Ecdsa,
}

/// <summary>
/// Signs for <see cref="PdfSigner"/> with a key that does not have to be in the process: Azure
/// Key Vault or Managed HSM, AWS KMS, an HSM or smart card through PKCS #11, or a remote signing
/// service. The library builds the CAdES signed attributes, hashes them and asks the provider for
/// the raw signature of that hash; it never sees the key.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SignDigestAsync(byte[], PdfDigestAlgorithm, CancellationToken)"/> receives a hash,
/// not data: the operation Key Vault calls Sign, KMS calls Sign with MessageType DIGEST, and .NET
/// calls SignHash. A raw PKCS #11 CKM_RSA_PKCS mechanism expects the DigestInfo prefix of PKCS #1
/// in front of the hash; the provider adds it.
/// </para>
/// <para>
/// RSA signatures are returned as they are. ECDSA signatures may be in either common form: r
/// followed by s, each as long as the curve's field (IEEE P1363, what .NET, Key Vault and PKCS #11
/// return), or a DER SEQUENCE of two INTEGERs (what AWS KMS returns). The library tells them apart
/// by verifying, and writes DER.
/// </para>
/// <para>
/// Every signature is verified with <see cref="Certificate"/>'s public key before it is written.
/// A provider that signs with another key, another algorithm or another hash fails the call with
/// <see cref="CryptographicException"/> instead of producing a document that does not verify.
/// </para>
/// <para>
/// When the signature arrives later than one call can wait, for example after the signer approves
/// it on a phone (remote qualified signing, ETSI TS 119 432), sign in two steps with
/// <see cref="PdfSigner.PrepareSignatureAsync(Stream, Stream, X509Certificate2, PdfSignatureAlgorithm, PdfSignatureOptions?, CancellationToken)"/>
/// and <see cref="PdfSigner.CompleteSignatureAsync(Stream, Stream, PdfDeferredSignature, byte[], PdfTimestampClient?, CancellationToken)"/>.
/// </para>
/// </remarks>
/// <example>
/// A provider backed by an Azure Key Vault key (package Azure.Security.KeyVault.Keys), whose
/// certificate the application loaded beforehand. The samples/signing folder of the repository
/// has this one in full and an AWS KMS provider.
/// <code>
/// using KeyVaultAlgorithm = Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm;
///
/// sealed class KeyVaultSignatureProvider(CryptographyClient key, X509Certificate2 certificate) : IPdfSignatureProvider
/// {
///     public X509Certificate2 Certificate =&gt; certificate;
///
///     public IReadOnlyList&lt;X509Certificate2&gt; Chain =&gt; [];
///
///     public PdfSignatureAlgorithm SignatureAlgorithm =&gt; PdfSignatureAlgorithm.RsaPkcs1;
///
///     public async Task&lt;byte[]&gt; SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken)
///     {
///         KeyVaultAlgorithm algorithm = digestAlgorithm switch
///         {
///             PdfDigestAlgorithm.Sha384 =&gt; KeyVaultAlgorithm.RS384,
///             PdfDigestAlgorithm.Sha512 =&gt; KeyVaultAlgorithm.RS512,
///             _ =&gt; KeyVaultAlgorithm.RS256,
///         };
///         SignResult result = await key.SignAsync(algorithm, digest, cancellationToken);
///         return result.Signature;
///     }
/// }
///
/// byte[] signed = await PdfSigner.SignAsync(pdf, new KeyVaultSignatureProvider(key, certificate), options, cancellationToken);
/// </code>
/// </example>
public interface IPdfSignatureProvider
{
    /// <summary>The signer's certificate, without its private key; its public key must match the key that signs.</summary>
    X509Certificate2 Certificate { get; }

    /// <summary>
    /// Intermediate certificates to embed with the signer's, so validators can build the chain to a
    /// trusted root; empty for none. <see cref="PdfSignatureOptions.Chain"/> adds to them.
    /// </summary>
    IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>The signature scheme the key signs with; it must suit the certificate's key.</summary>
    PdfSignatureAlgorithm SignatureAlgorithm { get; }

    /// <summary>Signs a hash and returns the raw signature value.</summary>
    /// <param name="digest">The hash of the signed attributes, made with <paramref name="digestAlgorithm"/>.</param>
    /// <param name="digestAlgorithm">The hash used, from <see cref="PdfSignatureOptions.Digest"/>.</param>
    /// <param name="cancellationToken">The caller's token: stop the request when it fires.</param>
    Task<byte[]> SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken);
}

/// <summary>
/// Signs with the private key of an <see cref="X509Certificate2"/>: a PFX file, the Windows
/// certificate store, a smart card its provider exposes. This is what
/// <see cref="PdfSigner.Sign(byte[], X509Certificate2, PdfSignatureOptions?)"/> uses; with
/// <see cref="PdfSigner.SignAsync(byte[], IPdfSignatureProvider, PdfSignatureOptions?, CancellationToken)"/>
/// it also signs with RSA-PSS.
/// </summary>
public sealed class PdfCertificateSignatureProvider : IPdfSignatureProvider
{
    /// <summary>Signs with the certificate's key: RSA keys with PKCS #1 v1.5 padding, EC keys with ECDSA.</summary>
    /// <exception cref="ArgumentException">The certificate has no private key, or its key is neither RSA nor EC.</exception>
    public PdfCertificateSignatureProvider(X509Certificate2 certificate)
        : this(certificate, SignatureValues.DefaultFor(certificate, nameof(certificate)))
    {
    }

    /// <summary>Signs with the certificate's key and the given scheme.</summary>
    /// <exception cref="ArgumentException">The certificate has no private key, or its key does not suit <paramref name="signatureAlgorithm"/>.</exception>
    public PdfCertificateSignatureProvider(X509Certificate2 certificate, PdfSignatureAlgorithm signatureAlgorithm)
    {
        Compat.ThrowIfNull(certificate, nameof(certificate));
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(certificate));
        SignatureValues.CheckKey(certificate, signatureAlgorithm, nameof(certificate), nameof(signatureAlgorithm));
        Certificate = certificate;
        SignatureAlgorithm = signatureAlgorithm;
    }

    /// <inheritdoc/>
    public X509Certificate2 Certificate { get; }

    /// <inheritdoc/>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    /// <inheritdoc/>
    public PdfSignatureAlgorithm SignatureAlgorithm { get; }

    /// <inheritdoc/>
    /// <remarks>Signs on the calling thread; the returned task is already complete.</remarks>
    public Task<byte[]> SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken)
    {
        Compat.ThrowIfNull(digest, nameof(digest));
        cancellationToken.ThrowIfCancellationRequested();
        HashAlgorithmName hash = PdfSigner.HashName(digestAlgorithm);
        if (SignatureAlgorithm == PdfSignatureAlgorithm.Ecdsa)
        {
            using ECDsa ec = Certificate.GetECDsaPrivateKey() ?? throw new CryptographicException("The certificate's private key cannot make ECDSA signatures.");
            return Task.FromResult(ec.SignHash(digest));
        }

        using RSA rsa = Certificate.GetRSAPrivateKey() ?? throw new CryptographicException("The certificate's private key cannot make RSA signatures.");
        return Task.FromResult(rsa.SignHash(digest, hash, SignatureAlgorithm == PdfSignatureAlgorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1));
    }
}

/// <summary>Checks on signature algorithms, keys and the signature values providers return.</summary>
internal static class SignatureValues
{
    /// <summary>The usual scheme for a certificate's key: PKCS #1 v1.5 for RSA, ECDSA for EC.</summary>
    /// <exception cref="ArgumentException">The key is neither RSA nor EC.</exception>
    public static PdfSignatureAlgorithm DefaultFor(X509Certificate2 certificate, string parameterName)
    {
        Compat.ThrowIfNull(certificate, parameterName);
        return KeyFamily(certificate) switch
        {
            PdfSignatureAlgorithm.Ecdsa => PdfSignatureAlgorithm.Ecdsa,
            PdfSignatureAlgorithm.RsaPkcs1 => PdfSignatureAlgorithm.RsaPkcs1,
            _ => throw new ArgumentException("The certificate's key must be RSA or ECDSA.", parameterName),
        };
    }

    /// <summary>Refuses a scheme the certificate's public key cannot verify.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The scheme is not one of <see cref="PdfSignatureAlgorithm"/>.</exception>
    /// <exception cref="ArgumentException">The key is neither RSA nor EC, or does not suit the scheme.</exception>
    public static void CheckKey(X509Certificate2 certificate, PdfSignatureAlgorithm algorithm, string certificateName, string algorithmName)
    {
        if (algorithm is not (PdfSignatureAlgorithm.RsaPkcs1 or PdfSignatureAlgorithm.RsaPss or PdfSignatureAlgorithm.Ecdsa))
        {
            throw new ArgumentOutOfRangeException(algorithmName, algorithm, "Unknown signature algorithm.");
        }

        PdfSignatureAlgorithm? family = KeyFamily(certificate);
        if (family is null) throw new ArgumentException("The certificate's key must be RSA or ECDSA.", certificateName);
        bool ec = algorithm == PdfSignatureAlgorithm.Ecdsa;
        if (ec != (family == PdfSignatureAlgorithm.Ecdsa))
        {
            throw new ArgumentException(ec ? "ECDSA needs a certificate with an EC key." : "RSA signatures need a certificate with an RSA key.", certificateName);
        }
    }

    /// <summary>
    /// The signature value as the CMS carries it, once it verifies with the certificate's public
    /// key over <paramref name="signedAttributes"/>. ECDSA values in r || s form are converted to
    /// DER, and DER values re-encoded minimally, so strict verifiers accept them.
    /// </summary>
    /// <exception cref="CryptographicException">The value is missing or does not verify.</exception>
    public static byte[] Accept(byte[]? signature, byte[] signedAttributes, PdfSignatureAlgorithm algorithm, HashAlgorithmName hash, X509Certificate2 certificate)
    {
        if (signature is null || signature.Length == 0) throw new CryptographicException("The signature provider returned no signature.");
        DerNode algorithmId = DerNode.Parse(Cms.SignatureAlgorithm(algorithm, hash));
        foreach (byte[] candidate in Encodings(signature, algorithm, certificate))
        {
            if (Pkix.Verify(signedAttributes, candidate, algorithmId, certificate, hash)) return candidate;
        }

        throw new CryptographicException(
            "The signature does not verify with the certificate's public key: the provider signed with another key, another algorithm or another hash than the certificate and options name.");
    }

    /// <summary>The DER forms a returned value may stand for: as it is for RSA; from r || s or from DER for ECDSA.</summary>
    private static List<byte[]> Encodings(byte[] signature, PdfSignatureAlgorithm algorithm, X509Certificate2 certificate)
    {
        if (algorithm != PdfSignatureAlgorithm.Ecdsa) return [signature];
        var encodings = new List<byte[]>(2);
        int size;
        using (ECDsa? key = certificate.GetECDsaPublicKey())
        {
            if (key is null) return encodings;
            size = (key.KeySize + 7) / 8;
        }

        // r || s, each as long as the field: what .NET, Azure Key Vault and PKCS #11 return.
        if (signature.Length == 2 * size) encodings.Add(Pkix.P1363ToDer(signature));

        // DER, what AWS KMS returns. A P1363 value that happens to parse as DER only adds a
        // candidate that fails to verify; the order above tries the likelier form first.
        try
        {
            if (Pkix.DerToP1363(signature, size) is byte[] raw) encodings.Add(Pkix.P1363ToDer(raw));
        }
        catch (FormatException)
        {
            // Not DER.
        }

        return encodings;
    }

    /// <summary>RsaPkcs1 for an RSA key, Ecdsa for an EC key, null for anything else; read from the public key, so it never touches the private key.</summary>
    private static PdfSignatureAlgorithm? KeyFamily(X509Certificate2 certificate) => certificate.PublicKey.Oid.Value switch
    {
        Pkix.RsaEncryption or Pkix.RsaPss => PdfSignatureAlgorithm.RsaPkcs1,
        Pkix.EcPublicKey => PdfSignatureAlgorithm.Ecdsa,
        _ => null,
    };
}
