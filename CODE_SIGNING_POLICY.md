# Code signing policy

## Current status

Current releases, including 0.12.3, are unsigned. SignPath Foundation has not accepted this project, and no Foundation signing credential is configured. Signing is disabled by default and refuses to run until setup is complete after acceptance. No claim of Foundation endorsement applies to existing downloads.

## Responsible maintainer

- Source maintainer and reviewer: [Bernardo-Petrovsky](https://github.com/Bernardo-Petrovsky).
- Release/signing approver: the same repository owner, unless additional trusted people are explicitly appointed.
- GitHub and SignPath accounts must use multifactor authentication. This is a policy requirement; account MFA status has not been independently verified by this repository.

## Build and approval

The workflow builds from a checked-out revision on a GitHub-hosted Windows runner. External archives are pinned by hash, NuGet dependencies are locked, and the unsigned artifact is uploaded to GitHub Actions before a signing request can be sent. Signing requires an explicit manual workflow dispatch on `main` and a manually approved SignPath signing policy. Pull requests cannot invoke signing.

After signing, the workflow verifies Authenticode and the expected publisher, checks the application version and regenerates SHA-256 from the signed EXE. It uploads a separate artifact with only the EXE and checksum. A signed build is released as a new version, never silently substituted for an existing public release.

## After Foundation acceptance

Configure the scoped GitHub integration, artifact configuration and manual release-signing policy with the Foundation. Store the token in Actions secrets, not source or application settings. Set the documented variables and enable `SIGNPATH_READY` only after setup and an approved signing test. See [docs/signpath-setup.md](docs/signpath-setup.md).

Only after acceptance and actual signing should the download page display: Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Scope

The intended signature covers the outer `RemotePlayCino.exe`. Upstream executables and libraries inside its payload retain their own provenance and notices; they are not individually re-signed as this project's original work. See [Privacy](PRIVACY.md) and [Third-party notices](THIRD-PARTY-NOTICES.md).
