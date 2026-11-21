# Third-party components

The root MIT license covers RemotePlayCino's own source and resources. It does not replace the licenses or copyright notices of the following components. Upstream binaries are not signed as original project code.

| Component | Version used by this source | License / source |
| --- | --- | --- |
| .NET Windows Desktop runtime | 10.0.2, self-contained win-x64 | [dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.2), [dotnet/winforms](https://github.com/dotnet/winforms/tree/v10.0.2); MIT and upstream third-party notices |
| MsQuic | supplied by the pinned .NET runtime | [microsoft/msquic](https://github.com/microsoft/msquic), MIT and upstream notices |
| SDL3 | 3.4.20 Windows x64 | [libsdl-org/SDL](https://github.com/libsdl-org/SDL/tree/release-3.4.20), zlib; bundled `SDL3-LICENSE.txt` |
| NAudio and its component packages | 2.2.1 | [naudio/NAudio](https://github.com/naudio/NAudio), [MIT notice](LICENSES/NAudio-MIT.txt) |
| Nefarius.ViGEm.Client | 1.21.256 | [ViGEm/ViGEm.NET](https://github.com/ViGEm/ViGEm.NET), MIT; copyright Nefarius Software Solutions e.U. 2017-2023 |
| SonarAnalyzer.CSharp | 10.36.0.15337, build-time analyzer | [SonarSource/sonar-dotnet](https://github.com/SonarSource/sonar-dotnet), SONAR Source-Available License v1.0 in the pinned NuGet package; not application runtime code |
| Adapted Switch 2 USB controller protocol code | original notice retained | [Wired-Pro2 MIT notice](remote-play/Wired-Pro2-LICENSE.txt), copyright amccomb 2026 |
| FFmpeg essentials Windows build | 9.0.2, supplied by Gyan Doshi | [GPLv3](LICENSES/FFmpeg-GPL-3.0.txt), [build information and dependency versions](LICENSES/FFmpeg-9.0.2-build-information.txt) |

FFmpeg runs as a separate process. This build fetches the exact upstream archive, verifies its pinned SHA-256 and retains its LICENSE and README inside the portable payload. FFmpeg's recorded source revision is [946fcce07b](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b); the build provider's packages and provenance are at https://www.gyan.dev/ffmpeg/builds/.

FFmpeg also incorporates other open-source libraries. Their licenses remain applicable. Before distributing a new binary build, retain and verify the complete corresponding-source and build-information arrangements for those GPL components, including their dependencies; publishing only this application's MIT sources does not replace those obligations.

SonarAnalyzer's current source-available license is not an OSI open-source license. It is a build tool excluded from application runtime dependencies by `PrivateAssets=all`. Disclose it to SignPath during review rather than claiming that every build tool has an OSI license.

Archive hashes are fixed in `tools/Get-CIDependencies.ps1`. NuGet versions and package content hashes are fixed in `remote-play/packages.lock.json`. The build also retains the runtime's upstream notices when present in its bootstrap output. Signing is an integrity/publisher mechanism, not a change of third-party licensing.
