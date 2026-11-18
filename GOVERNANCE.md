# Governance

PdfNetStack is an open source project of [JADEV](https://www.jadev-corp.com/en), published under the
MIT license. This page says who maintains it, how decisions and releases are made, and how to
become a maintainer.

## Maintainers

| Name | GitHub | Role |
|---|---|---|
| Jamal Abdelkhalek | [jams4code](https://github.com/jams4code) | Created the project; lead maintainer |
| Ilyass Krichi | [ilyass-krichi](https://github.com/ilyass-krichi) | Maintainer |

Maintainers review and merge pull requests, triage issues, handle security reports as
[SECURITY.md](SECURITY.md) describes, and publish releases. Reach them through issues and pull
requests on GitHub, or privately through GitHub's vulnerability reporting for security matters.

## How decisions are made

- **Changes.** Every change reaches `main` through a pull request. The required checks must pass
  on Linux, Windows and macOS, and a maintainer reviews the change and squash merges it, as
  [CONTRIBUTING.md](CONTRIBUTING.md) sets out.
- **Larger changes start with an issue.** A new public API, a new package or dependency, a changed
  default, anything that is not lossless, or a roadmap item is agreed in an issue before the code
  is written, so the approach is settled once.
- **Compatibility.** The public API follows semantic versioning. Package validation compares the
  core package with a published release and fails on a change that would break code built against
  it; breaking changes wait for a major version.
- **Scope.** What the project is for, and what it leaves out, is in
  [CONTRIBUTING.md](CONTRIBUTING.md#what-the-project-is-for) and [docs/ROADMAP.md](docs/ROADMAP.md).
- **Disagreement.** Maintainers look for agreement in the issue or pull request. When there is
  none, the lead maintainer decides and writes down why there.

## Releases

- Versions follow semantic versioning: a patch release for fixes, a minor release for features,
  a major release for breaking changes. Security fixes follow the supported versions in
  [SECURITY.md](SECURITY.md).
- A maintainer cuts a release by pushing a version tag on `main`. The release workflow builds and
  tests every package, publishes them to NuGet with trusted publishing from the `production`
  environment, and attaches the command line binaries, their checksums, a CycloneDX SBOM per
  package and build provenance attestations to the GitHub release. The corpus benchmark runs for
  the tag and attaches its report.
- Each release has its entry in [CHANGELOG.md](CHANGELOG.md), and the documentation site is built
  from the same commit.

## Becoming a maintainer

Maintainers are contributors who have shown, over time, that they care for the project as it is
written: changes that landed with tests, careful reviews of other people's pull requests, and help
in issues. The maintainers invite new ones together, and anyone may ask. A new maintainer gets the
Maintain role on the GitHub repository and, once they cut releases, publishing rights on NuGet.
A maintainer who steps away is thanked here and their access is removed.

## Code of conduct

Everyone in the project's spaces follows the [code of conduct](CODE_OF_CONDUCT.md).
