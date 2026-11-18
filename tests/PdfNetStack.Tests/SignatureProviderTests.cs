using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// Signing with keys outside the certificate (key vaults, KMS, HSMs, remote services), deferred
/// signing in two steps, the stream overloads, and the asynchronous timestamp and revocation hooks.
/// Where OpenSSL is installed, every signature is also checked by openssl cms -verify.
/// </summary>
[TestClass]
public sealed class SignatureProviderTests
{
    private static readonly Lazy<TestPki> SharedPki = new(() => new TestPki());
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    private static byte[] Document()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Contract", PdfStandardFont.Helvetica, 20, PdfColor.Black, 72, 72);
        return doc.ToArray(new PdfSaveOptions { Optimize = false });
    }

    private static (RSA Key, X509Certificate2 Certificate) RsaKey(string name = "Vault Signer")
    {
        RSA key = RSA.Create(2048);
        X509Certificate2 withKey = new CertificateRequest($"CN={name}, O=JADEV", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // The certificate the application holds carries no private key, as with a key vault.
        return (key, Compat.LoadCertificate(withKey.RawData));
    }

    private static (ECDsa Key, X509Certificate2 Certificate) EcKey(ECCurve curve, HashAlgorithmName hash)
    {
        ECDsa key = ECDsa.Create(curve);
        X509Certificate2 withKey = new CertificateRequest("CN=EC Vault Signer", key, hash).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (key, Compat.LoadCertificate(withKey.RawData));
    }

    [TestMethod]
    public async Task RsaProviderSignsWithoutThePrivateKeyInTheCertificate()
    {
        (RSA key, X509Certificate2 certificate) = RsaKey();
        using (key)
        {
            Assert.IsFalse(certificate.HasPrivateKey);
            var provider = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1);
            byte[] original = Document();
            byte[] signed = await PdfSigner.SignAsync(original, provider, new PdfSignatureOptions { Reason = "Approval" });

            CollectionAssert.AreEqual(original, signed.AsSpan(0, original.Length).ToArray());
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(32, provider.LastDigest!.Length);
            PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
            Assert.IsTrue(info.IsIntact, info.Problem);
            Assert.IsTrue(info.CoversWholeDocument);
            Assert.AreEqual("Vault Signer", info.SignerName);
            Assert.AreEqual("Approval", info.Reason);
            OpenSslVerify(signed);
        }
    }

    [TestMethod]
    public async Task RsaPssSignaturesVerify()
    {
        (RSA key, X509Certificate2 certificate) = RsaKey("PSS Signer");
        using (key)
        {
            foreach (PdfDigestAlgorithm digest in new[] { PdfDigestAlgorithm.Sha256, PdfDigestAlgorithm.Sha384, PdfDigestAlgorithm.Sha512 })
            {
                byte[] signed = await PdfSigner.SignAsync(Document(), new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPss), new PdfSignatureOptions { Digest = digest });
                PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
                Assert.IsTrue(info.IsIntact, digest + ": " + info.Problem);

                // The SignerInfo names RSASSA-PSS with explicit parameters.
                CmsSignedData cms = CmsSignedData.Parse(SignatureContents(signed));
                StringAssert.Contains(Compat.ToHex(cms.Encoded), Compat.ToHex(Der.Oid("1.2.840.113549.1.1.10")));
                OpenSslVerify(signed);
            }
        }

        // The certificate's own key can sign with PSS too, through the built-in provider.
        using RSA local = RSA.Create(2048);
        X509Certificate2 withKey = new CertificateRequest("CN=Local PSS", local, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] own = await PdfSigner.SignAsync(Document(), new PdfCertificateSignatureProvider(withKey, PdfSignatureAlgorithm.RsaPss));
        Assert.IsTrue(PdfSigner.Verify(own).Single().IsIntact);
    }

    [TestMethod]
    public async Task EcdsaProvidersMayReturnEitherSignatureForm()
    {
        // P-256 returning r || s, as Key Vault and PKCS #11 do; P-384 returning DER, as AWS KMS does.
        foreach ((ECCurve curve, HashAlgorithmName hash, PdfDigestAlgorithm digest, bool der) in new[]
        {
            (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, PdfDigestAlgorithm.Sha256, false),
            (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, PdfDigestAlgorithm.Sha256, true),
            (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, PdfDigestAlgorithm.Sha384, false),
            (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, PdfDigestAlgorithm.Sha384, true),
        })
        {
            (ECDsa key, X509Certificate2 certificate) = EcKey(curve, hash);
            using (key)
            {
                var provider = new KeyProvider(key, certificate, PdfSignatureAlgorithm.Ecdsa) { DerEcdsa = der };
                byte[] signed = await PdfSigner.SignAsync(Document(), provider, new PdfSignatureOptions { Digest = digest });
                PdfSignatureInfo info = PdfSigner.Verify(signed).Single();
                Assert.IsTrue(info.IsIntact, $"{curve.Oid.FriendlyName} der={der}: {info.Problem}");
                OpenSslVerify(signed);
            }
        }
    }

    [TestMethod]
    public async Task CertificateProviderWritesWhatTheSynchronousSignerWrites()
    {
        // PKCS #1 v1.5 is deterministic: with the same time the two paths must agree to the byte.
        using RSA key = RSA.Create(2048);
        X509Certificate2 certificate = new CertificateRequest("CN=Same Bytes", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] document = Document();
        var options = new PdfSignatureOptions { SigningTime = FixedTime, Reason = "Same", Appearance = new PdfSignatureAppearance(0, 72, 600, 200, 50) };

        byte[] sync = PdfSigner.Sign(document, certificate, options);
        byte[] async = await PdfSigner.SignAsync(document, new PdfCertificateSignatureProvider(certificate), options);
        CollectionAssert.AreEqual(sync, async);

        using var input = new NonSeekableStream(document);
        using var output = new MemoryStream();
        output.Write([1, 2, 3], 0, 3);
        await PdfSigner.SignAsync(input, output, new PdfCertificateSignatureProvider(certificate), options);
        CollectionAssert.AreEqual(sync, output.ToArray().AsSpan(3).ToArray());
    }

    [TestMethod]
    public async Task ProviderChainIsEmbeddedAndValidates()
    {
        TestPki pki = SharedPki.Value;
        using RSA key = pki.Signer.GetRSAPrivateKey()!;
        var provider = new KeyProvider(key, Compat.LoadCertificate(pki.Signer.RawData), PdfSignatureAlgorithm.RsaPkcs1) { Chain = [pki.Intermediate] };

        // The options' chain adds only what the provider does not already have.
        byte[] signed = await PdfSigner.SignAsync(Document(), provider, new PdfSignatureOptions { Chain = [pki.Intermediate, pki.Root] });
        CmsSignedData cms = CmsSignedData.Parse(SignatureContents(signed));
        CollectionAssert.AreEqual(new[] { "Test Signer", "Test Issuing CA", "Test Root CA" }, cms.Certificates.Select(Name).ToArray());

        PdfSignatureInfo info = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [pki.Root], RevocationSource = pki.GoodRevocation() }).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.AreEqual(PdfCheckStatus.Passed, info.Validation!.Chain, string.Join("\n", info.Validation.Problems));
    }

    [TestMethod]
    public async Task SignaturesFromAnotherKeyAreRefused()
    {
        (RSA key, X509Certificate2 certificate) = RsaKey();
        (RSA other, _) = RsaKey("Someone Else");
        using (key)
        using (other)
        {
            var wrongKey = new KeyProvider(other, certificate, PdfSignatureAlgorithm.RsaPkcs1);
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => PdfSigner.SignAsync(Document(), wrongKey));

            // A PSS signature declared as PKCS #1, and garbage, fail the same way.
            var wrongScheme = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1) { Override = PdfSignatureAlgorithm.RsaPss };
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => PdfSigner.SignAsync(Document(), wrongScheme));
            var garbage = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1) { Replace = _ => [1, 2, 3] };
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => PdfSigner.SignAsync(Document(), garbage));
            var empty = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1) { Replace = _ => null! };
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => PdfSigner.SignAsync(Document(), empty));

            // An algorithm the key cannot make is refused before anything is asked.
            var mismatched = new KeyProvider(key, certificate, PdfSignatureAlgorithm.Ecdsa);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => PdfSigner.SignAsync(Document(), mismatched));
            Assert.AreEqual(0, mismatched.Calls);
        }

        Assert.ThrowsExactly<ArgumentException>(() => new PdfCertificateSignatureProvider(certificate));
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        X509Certificate2 ecCertificate = new CertificateRequest("CN=EC", ec, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.ThrowsExactly<ArgumentException>(() => new PdfCertificateSignatureProvider(ecCertificate, PdfSignatureAlgorithm.RsaPss));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfCertificateSignatureProvider(ecCertificate, (PdfSignatureAlgorithm)9));
    }

    [TestMethod]
    public async Task AsynchronousHooksMakeBaselineLta()
    {
        TestPki pki = SharedPki.Value;
        using RSA key = pki.Signer.GetRSAPrivateKey()!;
        var provider = new KeyProvider(key, Compat.LoadCertificate(pki.Signer.RawData), PdfSignatureAlgorithm.RsaPss) { Chain = [pki.Intermediate] };
        var tsa = new PdfTimestampClient(async (request, ct) =>
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return pki.Respond(request);
        });
        var revocation = new AsyncRevocation(pki.GoodRevocation());

        byte[] signed = await PdfSigner.SignAsync(Document(), provider, new PdfSignatureOptions { Timestamp = tsa });
        byte[] lt = await PdfSigner.AddValidationDataAsync(signed, revocation, [pki.Root]);
        Assert.IsGreaterThan(0, revocation.Requests);
        byte[] lta = await PdfSigner.AddDocumentTimestampAsync(lt, tsa);

        using (var input = new NonSeekableStream(lta))
        using (var output = new MemoryStream())
        {
            await PdfSigner.AddValidationDataAsync(input, output, revocation, [pki.Root]);
            lta = output.ToArray();
        }

        IReadOnlyList<PdfSignatureInfo> all = PdfSigner.Verify(lta, new PdfValidationOptions { TrustAnchors = [pki.Root] });
        Assert.HasCount(2, all);
        PdfSignatureInfo signature = all[0];
        Assert.IsTrue(signature.IsIntact, signature.Problem);
        Assert.IsTrue(signature.Timestamp!.IsIntact, signature.Timestamp.Problem);
        Assert.AreEqual(PdfCheckStatus.Passed, signature.Validation!.Status, string.Join("\n", signature.Validation.Problems));
        Assert.AreEqual(PdfValidationTimeSource.Timestamp, signature.Validation.TimeSource);
        Assert.IsTrue(all[1].IsDocumentTimestamp);
        Assert.AreEqual(PdfCheckStatus.Passed, all[1].Validation!.Status, string.Join("\n", all[1].Validation!.Problems));
        OpenSslVerify(lta);

        // The stream form of the document timestamp writes what the array form writes, after the original.
        using var stampInput = new MemoryStream(signed);
        using var stampOutput = new MemoryStream();
        await PdfSigner.AddDocumentTimestampAsync(stampInput, stampOutput, tsa, "Stamp");
        Assert.IsTrue(PdfSigner.Verify(stampOutput.ToArray()).All(s => s.IsIntact));
    }

    [TestMethod]
    public void SynchronousCallsRefuseAsynchronousClients()
    {
        TestPki pki = SharedPki.Value;
        var tsa = new PdfTimestampClient((request, ct) => Task.FromResult(pki.Respond(request)));
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfSigner.Sign(Document(), pki.Signer, new PdfSignatureOptions { Timestamp = tsa }));
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfSigner.AddDocumentTimestamp(Document(), tsa));
        Assert.ThrowsExactly<InvalidOperationException>(() => tsa.GetToken([1]));

        // The same client works asynchronously, and a synchronous client works both ways.
        Assert.IsTrue(TimestampToken.Parse(tsa.GetTokenAsync([1]).GetAwaiter().GetResult()).Verify());
        Assert.IsTrue(TimestampToken.Parse(pki.TimestampClient().GetTokenAsync([1]).GetAwaiter().GetResult()).Verify());
    }

    [TestMethod]
    public async Task DeferredSigningRoundTrip()
    {
        TestPki pki = SharedPki.Value;
        using RSA key = pki.Signer.GetRSAPrivateKey()!;
        byte[] original = Document();
        var options = new PdfSignatureOptions { Reason = "Remote approval", Chain = [pki.Intermediate], Timestamp = pki.TimestampClient(), Digest = PdfDigestAlgorithm.Sha384 };

        // Step one: the application has only the certificate.
        using var input = new MemoryStream(original);
        using var prepared = new MemoryStream();
        PdfDeferredSignature pending = await PdfSigner.PrepareSignatureAsync(input, prepared, Compat.LoadCertificate(pki.Signer.RawData), PdfSignatureAlgorithm.RsaPkcs1, options);
        byte[] preparedBytes = prepared.ToArray();
        CollectionAssert.AreEqual(original, preparedBytes.AsSpan(0, original.Length).ToArray());
        Assert.AreEqual(48, pending.Digest.Length);
        CollectionAssert.AreEqual(Pkix.Hash(HashAlgorithmName.SHA384, pending.DataToSign.ToArray()), pending.Digest.ToArray());

        // The state is small, public and enough on its own.
        byte[] state = pending.Export();
        Assert.IsLessThan(8192, state.Length);
        Assert.AreEqual(-1, state.AsSpan().IndexOf(preparedBytes.AsSpan(0, 64)));
        PdfDeferredSignature restored = PdfDeferredSignature.Import(state);
        Assert.IsFalse(restored.Certificate.HasPrivateKey);
        CollectionAssert.AreEqual(pending.Digest.ToArray(), restored.Digest.ToArray());
        Assert.AreEqual(PdfDigestAlgorithm.Sha384, restored.DigestAlgorithm);
        Assert.AreEqual(PdfSignatureAlgorithm.RsaPkcs1, restored.SignatureAlgorithm);
        CollectionAssert.AreEqual(state, restored.Export());

        // The prepared file is a valid PDF whose signature is still empty.
        Assert.IsFalse(PdfSigner.Verify(preparedBytes).Single().IsIntact);

        // Step two, later: the remote service returns the signature of the digest.
        byte[] signature = key.SignHash(restored.Digest.ToArray(), HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
        using var preparedAgain = new NonSeekableStream(preparedBytes);
        using var output = new MemoryStream();
        await PdfSigner.CompleteSignatureAsync(preparedAgain, output, restored, signature, pki.TimestampClient());
        byte[] signed = output.ToArray();

        Assert.AreEqual(preparedBytes.Length, signed.Length);
        PdfSignatureInfo info = PdfSigner.Verify(signed, new PdfValidationOptions { TrustAnchors = [pki.Root], RevocationSource = pki.GoodRevocation() }).Single();
        Assert.IsTrue(info.IsIntact, info.Problem);
        Assert.IsTrue(info.CoversWholeDocument);
        Assert.AreEqual("Remote approval", info.Reason);
        Assert.IsTrue(info.Timestamp!.IsIntact, info.Timestamp.Problem);
        Assert.AreEqual(PdfCheckStatus.Passed, info.Validation!.Status, string.Join("\n", info.Validation.Problems));
        OpenSslVerify(signed);
    }

    [TestMethod]
    public async Task DeferredEcdsaFromAFileStream()
    {
        (ECDsa key, X509Certificate2 certificate) = EcKey(ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256);
        string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-deferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (key)
            {
                string preparedPath = Path.Combine(directory, "prepared.pdf");
                PdfDeferredSignature pending;
                using (var input = new MemoryStream(Document()))
                using (FileStream file = File.Create(preparedPath))
                {
                    pending = await PdfSigner.PrepareSignatureAsync(input, file, certificate, PdfSignatureAlgorithm.Ecdsa);
                }

                byte[] signature = key.SignHash(pending.Digest.ToArray());
                using var prepared = File.OpenRead(preparedPath);
                using var output = new MemoryStream();
                await PdfSigner.CompleteSignatureAsync(prepared, output, PdfDeferredSignature.Import(pending.Export()), signature);
                byte[] signed = output.ToArray();
                Assert.IsTrue(PdfSigner.Verify(signed).Single().IsIntact);
                OpenSslVerify(signed);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeferredSigningRefusesMismatches()
    {
        (RSA key, X509Certificate2 certificate) = RsaKey("Deferred");
        (RSA other, _) = RsaKey("Other");
        using (key)
        using (other)
        {
            using var input = new MemoryStream(Document());
            using var preparedStream = new MemoryStream();
            PdfDeferredSignature pending = await PdfSigner.PrepareSignatureAsync(input, preparedStream, certificate, PdfSignatureAlgorithm.RsaPss);
            byte[] prepared = preparedStream.ToArray();
            byte[] good = key.SignHash(pending.Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

            // A signature from another key or of another scheme.
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => Complete(prepared, pending, other.SignHash(pending.Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            await Assert.ThrowsExactlyAsync<CryptographicException>(() => Complete(prepared, pending, key.SignHash(pending.Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));

            // A file that changed, outside or inside the Contents string, or lost its last byte.
            byte[] changed = (byte[])prepared.Clone();
            changed[30] ^= 1;
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => Complete(changed, pending, good));
            byte[] filled = (byte[])prepared.Clone();
            filled[System.Text.Encoding.ASCII.GetString(prepared).IndexOf("/Contents<", StringComparison.Ordinal) + "/Contents<".Length + 5] = (byte)'A';
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => Complete(filled, pending, good));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => Complete(prepared.AsSpan(0, prepared.Length - 1).ToArray(), pending, good));

            // The completed file is not a prepared one any more.
            byte[] signed = await Complete(prepared, pending, good);
            Assert.IsTrue(PdfSigner.Verify(signed).Single().IsIntact);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => Complete(signed, pending, good));
        }

        // States that are not states.
        Assert.ThrowsExactly<FormatException>(() => PdfDeferredSignature.Import([0x30, 0x00]));
        Assert.ThrowsExactly<FormatException>(() => PdfDeferredSignature.Import([1, 2, 3]));
        byte[] valid = await PrepareState();
        for (int at = 0; at < valid.Length; at += 97)
        {
            byte[] damaged = (byte[])valid.Clone();
            damaged[at] ^= 0x41;
            try
            {
                PdfDeferredSignature.Import(damaged);
            }
            catch (FormatException)
            {
                // Expected for most bytes; a change inside a certificate's signature still parses.
            }
        }

        static async Task<byte[]> Complete(byte[] prepared, PdfDeferredSignature pending, byte[] signature)
        {
            using var source = new MemoryStream(prepared);
            using var output = new MemoryStream();
            await PdfSigner.CompleteSignatureAsync(source, output, pending, signature);
            return output.ToArray();
        }

        static async Task<byte[]> PrepareState()
        {
            (RSA key, X509Certificate2 certificate) = RsaKey("State");
            key.Dispose();
            using var input = new MemoryStream(Document());
            using var output = new MemoryStream();
            return (await PdfSigner.PrepareSignatureAsync(input, output, certificate, PdfSignatureAlgorithm.RsaPkcs1)).Export();
        }
    }

    [TestMethod]
    public async Task CancellationStopsEveryStep()
    {
        TestPki pki = SharedPki.Value;
        (RSA key, X509Certificate2 certificate) = RsaKey("Cancelled");
        using (key)
        {
            // Cancelled before the call: the provider is never asked.
            var provider = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.SignAsync(Document(), provider, null, cancelled.Token));
            Assert.AreEqual(0, provider.Calls);

            // Cancelled while the provider waits for an approval: the provider's wait ends.
            using var later = new CancellationTokenSource();
            var waiting = new KeyProvider(key, certificate, PdfSignatureAlgorithm.RsaPkcs1) { Wait = true };
            Task<byte[]> pending = PdfSigner.SignAsync(Document(), waiting, null, later.Token);
            await waiting.Started.Task;
            later.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

            // Cancelled while the time stamp authority answers.
            using var stamping = new CancellationTokenSource();
            var slowTsa = new PdfTimestampClient(async (request, ct) =>
            {
                stamping.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return pki.Respond(request);
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.SignAsync(Document(), provider, new PdfSignatureOptions { Timestamp = slowTsa }, stamping.Token));

            // Cancelled while revocation data is fetched.
            byte[] signed = await PdfSigner.SignAsync(Document(), new KeyProvider(pki.Signer.GetRSAPrivateKey()!, Compat.LoadCertificate(pki.Signer.RawData), PdfSignatureAlgorithm.RsaPkcs1));
            using var fetching = new CancellationTokenSource();
            var slowSource = new AsyncRevocation(pki.GoodRevocation()) { OnRequest = fetching.Cancel, Wait = true };
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.AddValidationDataAsync(signed, slowSource, [pki.Intermediate, pki.Root], fetching.Token));

            // Each step of deferred signing.
            using var input = new MemoryStream(Document());
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.PrepareSignatureAsync(input, new MemoryStream(), certificate, PdfSignatureAlgorithm.RsaPkcs1, null, cancelled.Token));
            input.Position = 0;
            using var prepared = new MemoryStream();
            PdfDeferredSignature state = await PdfSigner.PrepareSignatureAsync(input, prepared, certificate, PdfSignatureAlgorithm.RsaPkcs1);
            prepared.Position = 0;
            byte[] signature = key.SignHash(state.Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.CompleteSignatureAsync(prepared, new MemoryStream(), state, signature, null, cancelled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => PdfSigner.AddDocumentTimestampAsync(signed, pki.TimestampClient(), null, cancelled.Token));
        }
    }

    private static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false);

    /// <summary>The CMS of the first signature, without the zero padding of its Contents string.</summary>
    private static byte[] SignatureContents(byte[] pdf)
    {
        PdfDocument doc = PdfDocument.Open(pdf);
        PdfSignatureField field = doc.Form.Fields.OfType<PdfSignatureField>().First();
        var sig = (PdfDictionary)doc.Resolve(field.Dictionary["V"])!;
        byte[] contents = ((PdfString)doc.Resolve(sig["Contents"])!).Value;
        return contents.AsSpan(0, DerNode.Parse(contents).End).ToArray();
    }

    private static string? OpenSsl { get; } = new[] { "/opt/homebrew/bin/openssl", "/usr/local/bin/openssl", "/usr/bin/openssl" }.FirstOrDefault(File.Exists);

    /// <summary>
    /// openssl cms -verify on every CMS signature of the file, where OpenSSL is installed: an
    /// independent check of the structure, the message digest over the byte range, the signing
    /// certificate attribute and the signature value. The chain is not checked (-noverify): the
    /// test certificates are not trusted by OpenSSL.
    /// </summary>
    private static void OpenSslVerify(byte[] pdf)
    {
#if NET8_0_OR_GREATER
        if (OpenSsl is null) return;
        PdfDocument doc = PdfDocument.Open(pdf);
        foreach (PdfSignatureField field in doc.Form.Fields.OfType<PdfSignatureField>())
        {
            var sig = (PdfDictionary)doc.Resolve(field.Dictionary["V"])!;
            if (sig.GetName("SubFilter") != "ETSI.CAdES.detached") continue;
            long[] range = [.. ((PdfArray)doc.Resolve(sig["ByteRange"])!).Select(v => ((PdfNumber)doc.Resolve(v)!).IntegerValue)];
            byte[] contents = ((PdfString)doc.Resolve(sig["Contents"])!).Value;
            string directory = Path.Combine(Path.GetTempPath(), "pdfnetstack-openssl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string cms = Path.Combine(directory, "signature.der"), content = Path.Combine(directory, "content.bin");
                File.WriteAllBytes(cms, contents.AsSpan(0, DerNode.Parse(contents).End).ToArray());
                File.WriteAllBytes(content, [.. pdf.AsSpan(0, (int)range[1]).ToArray(), .. pdf.AsSpan((int)range[2], (int)range[3]).ToArray()]);
                var start = new ProcessStartInfo(OpenSsl) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "cms", "-verify", "-binary", "-inform", "DER", "-in", cms, "-content", content, "-noverify", "-out", Path.Combine(directory, "out.bin") })
                {
                    start.ArgumentList.Add(argument);
                }

                using var process = Process.Start(start)!;
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, field.Name + ": " + output);
                StringAssert.Contains(output, "Verification successful");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
#else
        // OpenSSL is checked on .NET only; .NET Framework runs on Windows, where it is rarely installed.
        _ = pdf;
#endif
    }

    /// <summary>
    /// A key held apart from its certificate, signing hashes as a key vault, KMS or HSM does, and
    /// answering later than the call, as a network round trip does.
    /// </summary>
    private sealed class KeyProvider(AsymmetricAlgorithm key, X509Certificate2 certificate, PdfSignatureAlgorithm algorithm) : IPdfSignatureProvider
    {
        public X509Certificate2 Certificate => certificate;

        public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

        public PdfSignatureAlgorithm SignatureAlgorithm => algorithm;

        /// <summary>Return ECDSA signatures as DER, as AWS KMS does, instead of r || s.</summary>
        public bool DerEcdsa { get; init; }

        /// <summary>Sign with another RSA padding than the one declared.</summary>
        public PdfSignatureAlgorithm? Override { get; init; }

        /// <summary>Return something else than the signature.</summary>
        public Func<byte[], byte[]>? Replace { get; init; }

        /// <summary>Wait for cancellation instead of answering, as a signature waiting for approval does.</summary>
        public bool Wait { get; init; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public byte[]? LastDigest { get; private set; }

        public async Task<byte[]> SignDigestAsync(byte[] digest, PdfDigestAlgorithm digestAlgorithm, CancellationToken cancellationToken)
        {
            Calls++;
            LastDigest = digest;
            Started.TrySetResult(true);
            await Task.Yield();
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            HashAlgorithmName hash = digestAlgorithm switch
            {
                PdfDigestAlgorithm.Sha384 => HashAlgorithmName.SHA384,
                PdfDigestAlgorithm.Sha512 => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256,
            };
            byte[] signature = key switch
            {
                RSA rsa => rsa.SignHash(digest, hash, (Override ?? algorithm) == PdfSignatureAlgorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1),
#if NET8_0_OR_GREATER
                ECDsa ec when DerEcdsa => ec.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence),
#else
                ECDsa ec when DerEcdsa => Pkix.P1363ToDer(ec.SignHash(digest)),
#endif
                ECDsa ec => ec.SignHash(digest),
                _ => throw new NotSupportedException(),
            };
            return Replace is null ? signature : Replace(signature);
        }
    }

    /// <summary>A revocation source that answers asynchronously, counting requests.</summary>
    private sealed class AsyncRevocation(PdfRevocationValues values) : IAsyncPdfRevocationSource
    {
        public Action? OnRequest { get; init; }

        public bool Wait { get; init; }

        public int Requests { get; private set; }

        public async Task<PdfRevocationValues> GetRevocationValuesAsync(PdfRevocationRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            OnRequest?.Invoke();
            await Task.Yield();
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            return values;
        }
    }

    /// <summary>A stream that reads forward only, as a request body does.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
