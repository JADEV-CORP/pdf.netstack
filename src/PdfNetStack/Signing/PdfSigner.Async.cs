using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Signing;

/// <summary>
/// Signing with keys outside the process, in one step or two, and the asynchronous forms of the
/// timestamp and validation data steps.
/// </summary>
public static partial class PdfSigner
{
    /// <summary>
    /// Signs <paramref name="pdf"/> with a key the provider holds (PAdES B-B, or B-T with
    /// <see cref="PdfSignatureOptions.Timestamp"/>) and returns the signed file.
    /// </summary>
    /// <param name="pdf">The document to sign.</param>
    /// <param name="signer">Supplies the certificate and signs the hash of the signed attributes.</param>
    /// <param name="options">Field, appearance, hash, timestamp and the rest; null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Observed once the document is read, before the provider and the time stamp authority are
    /// asked, and passed to both.
    /// </param>
    /// <exception cref="ArgumentException">The provider's certificate is missing or its key does not suit the provider's algorithm.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">The provider's signature does not verify with its certificate, or the time stamp authority refused the request or replied with an invalid token.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was signed.</exception>
    public static async Task<byte[]> SignAsync(byte[] pdf, IPdfSignatureProvider signer, PdfSignatureOptions? options = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(signer, nameof(signer));
        cancellationToken.ThrowIfCancellationRequested();
        UpdatedFile file = await SignCoreAsync(pdf, signer, options ?? new PdfSignatureOptions(), cancellationToken).ConfigureAwait(false);
        return file.ToArray();
    }

    /// <summary>
    /// Signs the document read from <paramref name="input"/> as
    /// <see cref="SignAsync(byte[], IPdfSignatureProvider, PdfSignatureOptions?, CancellationToken)"/>
    /// does and writes the signed file to <paramref name="output"/>, asynchronously, then flushes it.
    /// Suits request and response bodies and blob streams.
    /// </summary>
    /// <remarks>
    /// The input is read into memory once (a <see cref="MemoryStream"/> whose buffer is exposed is
    /// used without copying), because the update is built from the parsed document. The output is
    /// the input's bytes followed by the update, written without a second copy of the file.
    /// </remarks>
    /// <param name="input">The document, read from its position to its end. It is not disposed.</param>
    /// <param name="output">Receives the signed file from its position. It is not disposed.</param>
    /// <param name="signer">Supplies the certificate and signs the hash of the signed attributes.</param>
    /// <param name="options">Field, appearance, hash, timestamp and the rest; null for the defaults.</param>
    /// <param name="cancellationToken">Observed while reading, before the provider and the time stamp authority are asked, and by each write.</param>
    /// <exception cref="ArgumentException">A stream cannot be read or written, or the provider's certificate does not suit its algorithm.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">The provider's signature does not verify with its certificate, or the time stamp authority failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static async Task SignAsync(Stream input, Stream output, IPdfSignatureProvider signer, PdfSignatureOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(input, nameof(input), output);
        Compat.ThrowIfNull(signer, nameof(signer));
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> pdf = await InputReader.ReadToEndAsync(input, cancellationToken).ConfigureAwait(false);
        UpdatedFile file = await SignCoreAsync(pdf, signer, options ?? new PdfSignatureOptions(), cancellationToken).ConfigureAwait(false);
        await file.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// First step of a deferred signature: writes the document to <paramref name="output"/> with
    /// the signature field and an empty, sized signature, and returns what to sign and what the
    /// second step needs. For signatures that arrive after the call has ended, such as remote
    /// qualified signatures a signer approves on a phone (ETSI TS 119 432, the CSC API).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keep the prepared file and <see cref="PdfDeferredSignature.Export"/> until the signature of
    /// <see cref="PdfDeferredSignature.Digest"/> arrives, then call
    /// <see cref="CompleteSignatureAsync(Stream, Stream, PdfDeferredSignature, byte[], PdfTimestampClient?, CancellationToken)"/>.
    /// Nothing is held in memory in between.
    /// </para>
    /// <para>
    /// The signing time (/M) is the time of this step. <see cref="PdfSignatureOptions.Timestamp"/>
    /// only sizes the space reserved for a timestamp here; pass the client to the second step to
    /// have the signature timestamped (PAdES B-T).
    /// </para>
    /// </remarks>
    /// <param name="input">The document, read from its position to its end. It is not disposed.</param>
    /// <param name="output">Receives the prepared file from its position. It is not disposed.</param>
    /// <param name="certificate">The signer's certificate; a private key, if it carries one, is neither used nor kept.</param>
    /// <param name="signatureAlgorithm">The scheme the signature will use; it must suit the certificate's key.</param>
    /// <param name="options">Field, appearance, hash, chain and the rest; null for the defaults.</param>
    /// <param name="cancellationToken">Observed while reading and by each write.</param>
    /// <exception cref="ArgumentException">A stream cannot be read or written, or the certificate's key does not suit <paramref name="signatureAlgorithm"/>.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    /// <example>
    /// <code>
    /// // Request: prepare, keep the state, send the hash to the signing service.
    /// PdfDeferredSignature pending = await PdfSigner.PrepareSignatureAsync(upload, preparedBlob, certificate, PdfSignatureAlgorithm.RsaPkcs1, options, ct);
    /// await store.SaveAsync(id, pending.Export(), ct);
    /// await signingService.RequestSignatureAsync(id, pending.Digest.ToArray(), ct);
    ///
    /// // Callback, minutes later, maybe in another process: finish the file.
    /// PdfDeferredSignature state = PdfDeferredSignature.Import(await store.LoadAsync(id, ct));
    /// await PdfSigner.CompleteSignatureAsync(preparedBlob, signedBlob, state, signatureFromService, timestampClient, ct);
    /// </code>
    /// </example>
    public static async Task<PdfDeferredSignature> PrepareSignatureAsync(Stream input, Stream output, X509Certificate2 certificate, PdfSignatureAlgorithm signatureAlgorithm, PdfSignatureOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(input, nameof(input), output);
        Compat.ThrowIfNull(certificate, nameof(certificate));
        SignatureValues.CheckKey(certificate, signatureAlgorithm, nameof(certificate), nameof(signatureAlgorithm));
        options ??= new PdfSignatureOptions();
        cancellationToken.ThrowIfCancellationRequested();

        // The state keeps a copy of the public certificate only, whatever key handle the caller's carries.
        X509Certificate2 signerCertificate = Pkix.Load(certificate.RawData);
        byte[][] chain = [.. options.Chain.Select(c => c.RawData)];
        ReadOnlyMemory<byte> pdf = await InputReader.ReadToEndAsync(input, cancellationToken).ConfigureAwait(false);
        UpdatedFile file = PrepareUpdate(pdf, signerCertificate, chain, options, cancellationToken);
        await file.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
        return new PdfDeferredSignature(signerCertificate, chain, signatureAlgorithm, options.Digest, file.Digest!, file.ContentsStart, file.ContentsEnd, file.Length);
    }

    /// <summary>
    /// Second step of a deferred signature: checks <paramref name="signature"/> and the prepared
    /// file against <paramref name="pending"/>, then writes the file to <paramref name="output"/>
    /// with the CMS signature in place, timestamped when <paramref name="timestamp"/> is given.
    /// </summary>
    /// <remarks>
    /// A seekable <paramref name="prepared"/> stream is read twice and never held in memory: once to
    /// check it is the prepared file (its length, its empty Contents string, its digest), once to
    /// copy it with the signature in place, its digest checked again so a file that changed in
    /// between fails. A stream that cannot seek is read into memory first.
    /// </remarks>
    /// <param name="prepared">The file the first step wrote, from its position. It is not disposed.</param>
    /// <param name="output">Receives the signed file from its position. It is not disposed.</param>
    /// <param name="pending">The state the first step returned, or <see cref="PdfDeferredSignature.Import(byte[])"/> of its export.</param>
    /// <param name="signature">
    /// The signature of <see cref="PdfDeferredSignature.Digest"/>: raw RSA, or ECDSA as r || s or DER.
    /// </param>
    /// <param name="timestamp">A time stamp authority to timestamp the signature (PAdES B-T); null for none.</param>
    /// <param name="cancellationToken">Observed while reading, passed to the time stamp authority, and by each write.</param>
    /// <exception cref="ArgumentException">A stream cannot be read or written, or <paramref name="prepared"/> is not the file <paramref name="pending"/> describes.</exception>
    /// <exception cref="CryptographicException"><paramref name="signature"/> does not verify with the certificate over the signed attributes, or the time stamp authority failed.</exception>
    /// <exception cref="InvalidOperationException">The signature and timestamp exceed the space reserved, or the prepared file changed while it was copied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static async Task CompleteSignatureAsync(Stream prepared, Stream output, PdfDeferredSignature pending, byte[] signature, PdfTimestampClient? timestamp = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(prepared, nameof(prepared), output);
        Compat.ThrowIfNull(pending, nameof(pending));
        Compat.ThrowIfNull(signature, nameof(signature));
        cancellationToken.ThrowIfCancellationRequested();
        HashAlgorithmName hash = HashName(pending.DigestAlgorithm);
        byte[] value = SignatureValues.Accept(signature, pending.SignedAttributes, pending.SignatureAlgorithm, hash, pending.Certificate);

        using MemoryStream? buffered = prepared.CanSeek ? null : await BufferAsync(prepared, cancellationToken).ConfigureAwait(false);
        Stream source = buffered ?? prepared;
        long origin = source.Position;
        if (source.Length - origin != pending.Length) throw new ArgumentException("The prepared file is not the one this signature was prepared for: its length differs.", nameof(prepared));
        byte[]? digest = await CopyPreparedAsync(source, origin, pending, hash, null, cancellationToken).ConfigureAwait(false);
        if (digest is null || !Pkix.FixedTimeEquals(digest, pending.DocumentDigest)) throw new ArgumentException("The prepared file is not the one this signature was prepared for: its content differs.", nameof(prepared));

        byte[]? token = null;
        if (timestamp is not null) token = await timestamp.GetTokenForDigestAsync(Pkix.Hash(HashName(timestamp.Digest), value), cancellationToken).ConfigureAwait(false);
        byte[] cms = Cms.Build(pending.SignedAttributes, hash, pending.Certificate, pending.Chain, Cms.SignatureAlgorithm(pending.SignatureAlgorithm, hash), value, token);

        // The Contents string as written: '<', the hex, zeros up to the reserved size, '>'.
        var contents = new byte[pending.ContentsEnd - pending.ContentsStart];
        if (2 * cms.Length > contents.Length - 2) throw new InvalidOperationException("The signature is larger than the space reserved for it.");
#if NET8_0_OR_GREATER
        contents.AsSpan().Fill((byte)'0');
#else
        for (int i = 0; i < contents.Length; i++) contents[i] = (byte)'0';
#endif
        contents[0] = (byte)'<';
        contents[contents.Length - 1] = (byte)'>';
        System.Text.Encoding.ASCII.GetBytes(Compat.ToHex(cms)).CopyTo(contents, 1);

        byte[]? copied = await CopyPreparedAsync(source, origin, pending, hash, (output, contents), cancellationToken).ConfigureAwait(false);
        if (copied is null || !Pkix.FixedTimeEquals(copied, pending.DocumentDigest)) throw new InvalidOperationException("The prepared file changed while it was being completed; the output is not valid.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a document timestamp as <see cref="AddDocumentTimestamp(byte[], PdfTimestampClient, string?)"/>
    /// does, with a client made with either kind of delegate.
    /// </summary>
    /// <param name="pdf">The document, signed or not.</param>
    /// <param name="timestampAuthority">The authority to ask.</param>
    /// <param name="fieldName">The timestamp field's name; null picks Timestamp1, Timestamp2 and so on.</param>
    /// <param name="cancellationToken">Observed once the document is read and passed to the authority's delegate.</param>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">The authority refused the request or replied with an invalid token.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was added.</exception>
    public static async Task<byte[]> AddDocumentTimestampAsync(byte[] pdf, PdfTimestampClient timestampAuthority, string? fieldName = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(timestampAuthority, nameof(timestampAuthority));
        UpdatedFile file = await DocumentTimestampCoreAsync(pdf, timestampAuthority, fieldName, cancellationToken).ConfigureAwait(false);
        return file.ToArray();
    }

    /// <summary>Adds a document timestamp to the document read from <paramref name="input"/> and writes the result to <paramref name="output"/>, then flushes it.</summary>
    /// <param name="input">The document, read from its position to its end, into memory. It is not disposed.</param>
    /// <param name="output">Receives the timestamped file from its position. It is not disposed.</param>
    /// <param name="timestampAuthority">The authority to ask.</param>
    /// <param name="fieldName">The timestamp field's name; null picks Timestamp1, Timestamp2 and so on.</param>
    /// <param name="cancellationToken">Observed while reading, passed to the authority's delegate, and by each write.</param>
    /// <exception cref="ArgumentException">A stream cannot be read or written.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="CryptographicException">The authority refused the request or replied with an invalid token.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static async Task AddDocumentTimestampAsync(Stream input, Stream output, PdfTimestampClient timestampAuthority, string? fieldName = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(input, nameof(input), output);
        Compat.ThrowIfNull(timestampAuthority, nameof(timestampAuthority));
        ReadOnlyMemory<byte> pdf = await InputReader.ReadToEndAsync(input, cancellationToken).ConfigureAwait(false);
        UpdatedFile file = await DocumentTimestampCoreAsync(pdf, timestampAuthority, fieldName, cancellationToken).ConfigureAwait(false);
        await file.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds long term validation data (PAdES B-LT) as
    /// <see cref="AddValidationData(byte[], IPdfRevocationSource, IReadOnlyList{X509Certificate2}?, CancellationToken)"/>
    /// does, asking an asynchronous source, so OCSP and CRL requests do not hold a thread.
    /// </summary>
    /// <param name="pdf">A signed or timestamped document.</param>
    /// <param name="revocation">Supplies OCSP responses and CRLs; <see cref="PdfRevocationValues.None"/> to embed certificates only.</param>
    /// <param name="certificates">Extra certificates for the chains, such as intermediate and root CAs.</param>
    /// <param name="cancellationToken">Observed before each signature's chains are built and before each certificate's revocation data is asked for, and passed to the source.</param>
    /// <exception cref="InvalidOperationException">The document has no readable signature or timestamp.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="PdfFormatException">The document's certificates and validation data need an unreasonable number of signature checks.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was added.</exception>
    public static async Task<byte[]> AddValidationDataAsync(byte[] pdf, IAsyncPdfRevocationSource revocation, IReadOnlyList<X509Certificate2>? certificates = null, CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNull(pdf, nameof(pdf));
        Compat.ThrowIfNull(revocation, nameof(revocation));
        UpdatedFile file = await ValidationDataAsync(pdf, revocation, certificates, cancellationToken).ConfigureAwait(false);
        return file.ToArray();
    }

    /// <summary>Adds long term validation data to the document read from <paramref name="input"/> and writes the result to <paramref name="output"/>, then flushes it.</summary>
    /// <param name="input">A signed or timestamped document, read from its position to its end, into memory. It is not disposed.</param>
    /// <param name="output">Receives the updated file from its position. It is not disposed.</param>
    /// <param name="revocation">Supplies OCSP responses and CRLs; <see cref="PdfRevocationValues.None"/> to embed certificates only.</param>
    /// <param name="certificates">Extra certificates for the chains, such as intermediate and root CAs.</param>
    /// <param name="cancellationToken">Observed while reading, before each revocation request, by the source, and by each write.</param>
    /// <exception cref="ArgumentException">A stream cannot be read or written.</exception>
    /// <exception cref="InvalidOperationException">The document has no readable signature or timestamp.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="PdfFormatException">The document's certificates and validation data need an unreasonable number of signature checks.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what reached <paramref name="output"/> is not a complete file.</exception>
    public static async Task AddValidationDataAsync(Stream input, Stream output, IAsyncPdfRevocationSource revocation, IReadOnlyList<X509Certificate2>? certificates = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(input, nameof(input), output);
        Compat.ThrowIfNull(revocation, nameof(revocation));
        ReadOnlyMemory<byte> pdf = await InputReader.ReadToEndAsync(input, cancellationToken).ConfigureAwait(false);
        UpdatedFile file = await ValidationDataAsync(pdf, revocation, certificates, cancellationToken).ConfigureAwait(false);
        await file.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UpdatedFile> ValidationDataAsync(ReadOnlyMemory<byte> pdf, IAsyncPdfRevocationSource revocation, IReadOnlyList<X509Certificate2>? certificates, CancellationToken ct)
    {
        try
        {
            return await CollectValidationDataAsync(pdf, revocation, certificates, new SignatureChecks(), ct).ConfigureAwait(false);
        }
        catch (SignatureChecks.ExhaustedException e)
        {
            throw new PdfFormatException(e.Message);
        }
    }

    private static void CheckStreams(Stream input, string inputName, Stream output)
    {
        Compat.ThrowIfNull(input, inputName);
        Compat.ThrowIfNull(output, nameof(output));
        if (!input.CanRead) throw new ArgumentException("The stream must be readable.", inputName);
        if (!output.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(output));
    }

    /// <summary>A stream that cannot seek, read into memory so it can be read twice.</summary>
    private static async Task<MemoryStream> BufferAsync(Stream stream, CancellationToken ct)
    {
        ReadOnlyMemory<byte> data = await InputReader.ReadToEndAsync(stream, ct).ConfigureAwait(false);
        return MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
    }

    /// <summary>
    /// Reads the prepared file from its start and returns the hash of everything outside the
    /// Contents string, or null when that string is no longer '&lt;', zeros and '&gt;'. With a
    /// target, also writes the file there with the target's contents in place of that string.
    /// </summary>
    private static async Task<byte[]?> CopyPreparedAsync(Stream source, long origin, PdfDeferredSignature pending, HashAlgorithmName hash, (Stream Output, byte[] Contents)? target, CancellationToken ct)
    {
        source.Position = origin;
        long start = pending.ContentsStart, end = pending.ContentsEnd, length = pending.Length;
        using IncrementalHash digest = IncrementalHash.CreateHash(hash);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(OutputStreams.CopyBufferSize);
        try
        {
            for (long position = 0; position < length;)
            {
                // Each read stays on one side of the Contents string's boundaries.
                long limit = position < start ? start : position < end ? end : length;
                int count = (int)Math.Min(buffer.Length, limit - position);
                await ReadExactlyAsync(source, buffer, count, ct).ConfigureAwait(false);
                if (position >= start && position < end)
                {
                    int offset = (int)(position - start);
                    if (!IsEmptyContents(buffer, count, offset, (int)(end - start))) return null;
                    if (target is (Stream output, byte[] contents)) await OutputStreams.WriteAsync(output, contents.AsMemory(offset, count), ct).ConfigureAwait(false);
                }
                else
                {
                    digest.AppendData(buffer, 0, count);
                    if (target is (Stream output, _)) await OutputStreams.WriteAsync(output, buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }

                position += count;
            }

            return digest.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Whether a piece of the Contents string, starting <paramref name="offset"/> bytes into it, is what the first step wrote.</summary>
    private static bool IsEmptyContents(byte[] piece, int count, int offset, int width)
    {
        for (int i = 0; i < count; i++)
        {
            int at = offset + i;
            byte expected = at == 0 ? (byte)'<' : at == width - 1 ? (byte)'>' : (byte)'0';
            if (piece[i] != expected) return false;
        }

        return true;
    }

#pragma warning disable CA1835 // Array overloads keep one code path for .NET Standard 2.0.
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        for (int total = 0; total < count;)
        {
            int read = await stream.ReadAsync(buffer, total, count - total, ct).ConfigureAwait(false);
            if (read == 0) throw new InvalidOperationException("The prepared file ended early; it changed while it was being read.");
            total += read;
        }
    }
#pragma warning restore CA1835
}
