# Security policy

PdfNetStack parses untrusted input, so parser robustness is a security property.

## Supported versions

Security fixes ship as a patch release of the latest minor version. The minor version before it
gets fixes for high and critical issues for three months after its successor is released.

| Version | Supported |
|---|---|
| 1.3.x | Yes |
| 1.2.x | High and critical fixes until three months after 1.3.0 is released |
| 1.1.x and earlier | No |

The add-on packages and the command line tools follow the version of the core package they ship
with. `PdfNetStack.Ocr.Models` holds data only and is versioned on its own.

## Reporting a vulnerability

Report privately through GitHub: **Security > Report a vulnerability** on this repository.
Please do not open a public issue for crashes, hangs, memory exhaustion or anything that lets a
crafted PDF escape the size and nesting limits.

Include a proof of concept file if you can, the options used, and the .NET version.

## Disclosure timeline

| When | What happens |
|---|---|
| Within 7 days of the report | Acknowledgement |
| Within 14 days | Assessment: confirmed or not, severity (CVSS), affected versions |
| Within 90 days | Fixed release on NuGet, sooner for critical issues |
| With the fixed release | GitHub security advisory and CVE, crediting the reporter unless they prefer not |

If a fix needs longer than 90 days, the advisory is published at 90 days with the known
mitigations, on a date agreed with the reporter. Please keep the details private until then.

## Scope

In scope: crashes or unhandled exceptions from crafted input, unbounded CPU or memory use beyond
the configured limits, output that differs from the input while verification reports success.

Out of scope: high memory use that stays within the documented limits and scales with
`MaxDegreeOfParallelism`, and files the optimizer correctly refuses.

## Running on untrusted input

- Keep `MaxInputBytes` and `MaxDecodedStreamBytes` at values your workers can afford.
- Run batch workers with an OS level memory limit (container limits or job objects).
- Use a `CancellationToken` with a timeout per document.

## Verifying a release

- The release workflow publishes to NuGet with trusted publishing, without a stored API key.
- Every `.nupkg` and command line binary has a build provenance attestation:
  `gh attestation verify <file> --repo jams4code/pdf.netstack` checks that this repository's
  release workflow built it from the tagged commit.
- Each GitHub release carries a CycloneDX SBOM per package (`<package>.<version>.cdx.json`) and
  `SHA256SUMS.txt` for the command line binaries.
- The parsers are fuzzed on every pull request that touches them and every night
  (`.github/workflows/fuzz.yml`); CodeQL and OpenSSF Scorecard run on every change to `main`.
