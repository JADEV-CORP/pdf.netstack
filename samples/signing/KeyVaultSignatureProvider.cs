using System.Security.Cryptography.X509Certificates;
using Azure.Security.KeyVault.Keys.Cryptography;
using PdfNetStack.Signing;
using KeyVaultAlgorithm = Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm;

namespace SigningSamples;

/// <summary>
/// Signs with a key in Azure Key Vault or Managed HSM. The private key never leaves the vault:
/// the library sends the hash of the signed attributes, the vault returns the signature.
/// </summary>
/// <remarks>
/// <para>
/// The identity needs the "Key Vault Crypto User" role (or the sign permission in an access
/// policy) on the key. The certificate is the public half the application already holds, for
/// example the Cer property of the Key Vault certificate whose key this is.
/// </para>
/// <para>
/// Key Vault names the hash in the algorithm: RS256 to RS512 and PS256 to PS512 for RSA, ES256
/// for P-256 and ES384 for P-384. Pick <see cref="PdfSignatureOptions.Digest"/> to match the
/// curve: SHA-256 with P-256, SHA-384 with P-384. Key Vault returns ECDSA signatures as r || s,
/// which the library accepts as is.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var key = new CryptographyClient(new Uri("https://contoso.vault.azure.net/keys/signing/0123456789abcdef"), new DefaultAzureCredential());
/// var signer = new KeyVaultSignatureProvider(key, certificate, PdfSignatureAlgorithm.RsaPss) { Chain = [issuingCa] };
/// await PdfSigner.SignAsync(request.Body, response.Body, signer, new PdfSignatureOptions { Reason = "Approved" }, cancellationToken);
/// </code>
/// </example>
public sealed class KeyVaultSignatureProvider(CryptographyClient key, X509Certificate2 certificate, PdfSignatureAlgorithm signatureAlgorithm) : IPdfSignatureProvider
{
    public X509Certificate2 Certificate => certificate;

    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    public PdfSignatureAlgorithm SignatureAlgorithm => signatureAlgorithm;

    public async Task<byte[]> SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken)
    {
        SignResult result = await key.SignAsync(Algorithm(signatureAlgorithm, digestAlgorithm), digest, cancellationToken);
        return result.Signature;
    }

    private static KeyVaultAlgorithm Algorithm(PdfSignatureAlgorithm scheme, PdfDigestAlgorithm digest) => (scheme, digest) switch
    {
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha256) => KeyVaultAlgorithm.RS256,
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha384) => KeyVaultAlgorithm.RS384,
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha512) => KeyVaultAlgorithm.RS512,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha256) => KeyVaultAlgorithm.PS256,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha384) => KeyVaultAlgorithm.PS384,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha512) => KeyVaultAlgorithm.PS512,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha256) => KeyVaultAlgorithm.ES256,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha384) => KeyVaultAlgorithm.ES384,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha512) => KeyVaultAlgorithm.ES512,
        _ => throw new NotSupportedException($"Key Vault has no algorithm for {scheme} with {digest}."),
    };
}
