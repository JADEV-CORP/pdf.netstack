# Signing samples

PdfNetStack signs with keys that never enter the process. The library builds the CAdES signed
attributes, hashes them, and asks an `IPdfSignatureProvider` for the raw signature of that hash.
Every signature is verified with the certificate's public key before it is written, so a
provider that signs with the wrong key or algorithm fails loudly instead of producing a document
that does not verify.

| File | What it shows |
|---|---|
| [KeyVaultSignatureProvider.cs](KeyVaultSignatureProvider.cs) | Azure Key Vault or Managed HSM key (RS, PS and ES algorithms) |
| [AwsKmsSignatureProvider.cs](AwsKmsSignatureProvider.cs) | AWS KMS asymmetric key, MessageType DIGEST |
| [RemoteSigningFlow.cs](RemoteSigningFlow.cs) | Two step signing for remote qualified signatures (ETSI TS 119 432, CSC API) |

Copy the provider you need into your application. The library itself depends on no cloud SDK.

## One step: a key in a vault or HSM

```csharp
var key = new CryptographyClient(keyId, new DefaultAzureCredential());
var signer = new KeyVaultSignatureProvider(key, certificate, PdfSignatureAlgorithm.RsaPss) { Chain = [issuingCa] };
await PdfSigner.SignAsync(request.Body, response.Body, signer,
    new PdfSignatureOptions { Reason = "Approved", Timestamp = tsa }, cancellationToken);
```

An HSM through PKCS #11 follows the same shape: sign the hash with `CKM_RSA_PKCS` (after
prepending the PKCS #1 DigestInfo), `CKM_RSA_PKCS_PSS` or `CKM_ECDSA`, and return the bytes.

## Two steps: the signature arrives later

```csharp
PdfDeferredSignature pending = await PdfSigner.PrepareSignatureAsync(upload, prepared, certificate,
    PdfSignatureAlgorithm.RsaPkcs1, new PdfSignatureOptions { Timestamp = tsa }, cancellationToken);
byte[] state = pending.Export();          // keep it: certificates, digest, positions; no key, no document
byte[] hash = pending.Digest.ToArray();   // send it to the signing service

// Later, maybe in another process:
PdfDeferredSignature restored = PdfDeferredSignature.Import(state);
await PdfSigner.CompleteSignatureAsync(prepared, signed, restored, signatureFromService, tsa, cancellationToken);
```

`CompleteSignatureAsync` checks the prepared file's length, its empty signature and its digest
against the state, and the signature against the certificate, before writing anything.

## Build

```sh
dotnet build samples/signing/SigningSamples.csproj
```

The project references the library by project reference and builds with the repository's
analyzers. Nothing here ships.

## Packages and licenses

| Package | License |
|---|---|
| Azure.Security.KeyVault.Keys 4.10.2 | MIT |
| Azure.Core, System.ClientModel, Microsoft.Identity.Client, Microsoft.IdentityModel.Abstractions, Microsoft.Extensions.*, Microsoft.Bcl.AsyncInterfaces, System.* (transitive) | MIT |
| AWSSDK.KeyManagementService 4.1.1.1 | Apache-2.0 |
| AWSSDK.Core, AWSSDK.Extensions.Numerics (transitive) | Apache-2.0 |

`dotnet list samples/signing/SigningSamples.csproj package --include-transitive` prints the full list.
