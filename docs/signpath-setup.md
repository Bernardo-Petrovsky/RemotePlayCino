# SignPath setup after acceptance

This is preparation, not an active signing subscription. Foundation admission, reputation review and the maintainer's acceptance of the terms are external steps.

1. Submit through https://signpath.org/apply. Provide the public sources, MIT license, build workflow, privacy policy, releases and honest adoption evidence. Download counters include development tests and are not user counts. Disclose the build-only SonarAnalyzer source-available license as documented in THIRD-PARTY-NOTICES.md; it is not shipped in the EXE and the Foundation must assess whether the build-tool dependency is acceptable.
2. The maintainer completes contact fields, personal-data consent, legal acceptance and any CAPTCHA. Optional marketing consent is separate. Do not put personal contact data in this repository.
3. After acceptance, follow the Foundation's instructions to configure its GitHub integration for this repository only. Review access permissions. Require MFA and a manual release-signing approval policy.
4. Review/import `signpath-artifact.xml` with SignPath. It signs the project's outer EXE with metadata restrictions. Do not sign upstream DLLs or FFmpeg as original project code.
5. Configure variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` from the real account. Store `SIGNPATH_API_TOKEN` as an Actions secret with signing-request submission rights only for this project. No credentials are included in source or the application.
6. Set `SIGNPATH_READY=true` only after setup approval. Dispatch **Windows portable build** on `main` with `request_signing=true`. Approve in SignPath, then confirm signature verification passes and the signed artifact contains the EXE and regenerated SHA-256.
7. Test a future signed version, including updating without losing `Datos`, before publishing its Release. Keep a stable signing identity and timestamping policy. SmartScreen still evaluates reputation; do not promise an immediate warning-free download.

GitHub artifact transport may wrap files internally in a ZIP. Application releases remain a portable EXE and SHA-256; users do not need the internal CI/signing transport archive.

References: https://docs.signpath.io/trusted-build-systems/github and https://signpath.org/terms
