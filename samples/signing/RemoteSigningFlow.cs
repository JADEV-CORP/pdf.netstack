using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Signing;

namespace SigningSamples;

/// <summary>
/// Remote signing in two steps, for signatures that arrive after the request has ended: a
/// qualified signature from a trust service provider (ETSI TS 119 432, the Cloud Signature
/// Consortium API) that the signer approves on a phone, or any queue based signing service.
/// </summary>
/// <remarks>
/// <para>
/// The first step writes the prepared file and keeps the exported state; nothing stays in memory
/// while the signer decides. The second step can run in another process: the state carries the
/// certificates, the digest and the positions in the file, and the library checks both the file
/// and the signature against it before writing anything.
/// </para>
/// <para>
/// With the CSC API v2, <see cref="IRemoteSigningService.RequestSignatureAsync"/> is the
/// credential authorization for this hash followed by signatures/signHash with the base64 hash,
/// its hashAlgorithmOID and the signAlgo; the first entry of the returned signatures, base64
/// decoded, is what <see cref="CompleteAsync"/> takes.
/// </para>
/// </remarks>
public sealed class RemoteSigningFlow(IDocumentStore store, IRemoteSigningService service, PdfTimestampClient timestamps)
{
    /// <summary>Prepares the upload, keeps the state and asks the service to sign. Returns the job's id.</summary>
    public async Task<string> StartAsync(Stream upload, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");

        // The timestamp client only sizes the space reserved here; the token is fetched when completing.
        var options = new PdfSignatureOptions { Reason = "Approved remotely", Timestamp = timestamps };
        PdfDeferredSignature pending;
        await using (Stream prepared = await store.CreateAsync(id + ".prepared.pdf", cancellationToken))
        {
            pending = await PdfSigner.PrepareSignatureAsync(upload, prepared, certificate, PdfSignatureAlgorithm.RsaPkcs1, options, cancellationToken);
        }

        await store.SaveStateAsync(id, pending.Export(), cancellationToken);
        await service.RequestSignatureAsync(id, pending.Digest.ToArray(), pending.DigestAlgorithm, cancellationToken);
        return id;
    }

    /// <summary>Called when the service delivers the signature: writes the signed, timestamped file.</summary>
    public async Task CompleteAsync(string id, byte[] signature, CancellationToken cancellationToken)
    {
        PdfDeferredSignature pending = PdfDeferredSignature.Import(await store.LoadStateAsync(id, cancellationToken));
        await using Stream prepared = await store.OpenReadAsync(id + ".prepared.pdf", cancellationToken);
        await using Stream signed = await store.CreateAsync(id + ".pdf", cancellationToken);
        await PdfSigner.CompleteSignatureAsync(prepared, signed, pending, signature, timestamps, cancellationToken);
    }
}

/// <summary>Where the application keeps files and job state: blob storage, a file share, a database.</summary>
public interface IDocumentStore
{
    Task<Stream> CreateAsync(string name, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string name, CancellationToken cancellationToken);

    Task SaveStateAsync(string id, byte[] state, CancellationToken cancellationToken);

    Task<byte[]> LoadStateAsync(string id, CancellationToken cancellationToken);
}

/// <summary>The trust service provider's API, as the application wraps it.</summary>
public interface IRemoteSigningService
{
    /// <summary>Asks for a signature of <paramref name="hash"/>; the service calls back with it once the signer approves.</summary>
    Task RequestSignatureAsync(string id, byte[] hash, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken);
}
