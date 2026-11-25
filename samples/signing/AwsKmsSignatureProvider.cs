using System.Security.Cryptography.X509Certificates;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using PdfNetStack.Signing;

namespace SigningSamples;

/// <summary>
/// Signs with an asymmetric AWS KMS key (key usage SIGN_VERIFY). The private key never leaves
/// KMS: the library sends the hash of the signed attributes with MessageType DIGEST, KMS returns
/// the signature.
/// </summary>
/// <remarks>
/// <para>
/// The role needs kms:Sign on the key. KMS stores no certificate: the application supplies the
/// one its CA issued for the key's public key (kms:GetPublicKey gives it for the request), from
/// AWS Private CA or any other CA.
/// </para>
/// <para>
/// KMS returns ECDSA signatures DER encoded; the library accepts that form as it is. RSASSA-PSS
/// in KMS uses MGF1 with the same hash and a salt as long as the hash, the parameters the library
/// writes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var kms = new AmazonKeyManagementServiceClient();
/// var signer = new AwsKmsSignatureProvider(kms, "alias/pdf-signing", certificate, PdfSignatureAlgorithm.Ecdsa);
/// byte[] signed = await PdfSigner.SignAsync(pdf, signer, new PdfSignatureOptions { Digest = PdfDigestAlgorithm.Sha384 }, cancellationToken);
/// </code>
/// </example>
public sealed class AwsKmsSignatureProvider(IAmazonKeyManagementService kms, string keyId, X509Certificate2 certificate, PdfSignatureAlgorithm signatureAlgorithm) : IPdfSignatureProvider
{
    public X509Certificate2 Certificate => certificate;

    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    public PdfSignatureAlgorithm SignatureAlgorithm => signatureAlgorithm;

    public async Task<byte[]> SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream(digest, writable: false);
        SignResponse response = await kms.SignAsync(
            new SignRequest
            {
                KeyId = keyId,
                Message = message,
                MessageType = MessageType.DIGEST,
                SigningAlgorithm = Algorithm(signatureAlgorithm, digestAlgorithm),
            },
            cancellationToken);
        return response.Signature.ToArray();
    }

    private static SigningAlgorithmSpec Algorithm(PdfSignatureAlgorithm scheme, PdfDigestAlgorithm digest) => (scheme, digest) switch
    {
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha256) => SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_256,
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha384) => SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_384,
        (PdfSignatureAlgorithm.RsaPkcs1, PdfDigestAlgorithm.Sha512) => SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_512,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha256) => SigningAlgorithmSpec.RSASSA_PSS_SHA_256,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha384) => SigningAlgorithmSpec.RSASSA_PSS_SHA_384,
        (PdfSignatureAlgorithm.RsaPss, PdfDigestAlgorithm.Sha512) => SigningAlgorithmSpec.RSASSA_PSS_SHA_512,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha256) => SigningAlgorithmSpec.ECDSA_SHA_256,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha384) => SigningAlgorithmSpec.ECDSA_SHA_384,
        (PdfSignatureAlgorithm.Ecdsa, PdfDigestAlgorithm.Sha512) => SigningAlgorithmSpec.ECDSA_SHA_512,
        _ => throw new NotSupportedException($"KMS has no algorithm for {scheme} with {digest}."),
    };
}
