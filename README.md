# RemotePlayCino

Portable screen, audio and gamepad sharing for Windows, with access controlled by the host.

RemotePlayCino captures a Windows display, encodes video locally and sends it directly to invited guests over QUIC. Guests can send gamepad, keyboard and mouse input when the host permits it. Connections use an access code, and the host can require manual admission and manage each guest's input permissions.

## Download and use

Download **RemotePlayCino.exe** from [Releases](https://github.com/Bernardo-Petrovsky/RemotePlayCino/releases). Save it in a writable folder and run it. No installer or manual ZIP extraction is required. The portable prepares FFmpeg and its native dependencies next to the EXE. Preferences and appearance are kept in `Datos`; keep that folder when moving your existing profile. Share only the EXE to give someone a clean copy.

The accompanying `.exe.sha256` file is a download checksum, not a signing certificate. Current public releases are unsigned.

The host selects **Compartir pantalla** and gives the guest the host address, UDP port (default **48123**) and access code. A guest on another network needs the host's public address and a working route through the host's router/firewall. Admit only guests you trust. Internet routing and available upload bandwidth affect connection quality.

Windows 11 x64 with QUIC support is the tested target. Guests do not need a separate .NET runtime or FFmpeg installation. Exposing a virtual gamepad on the host may require a separately installed ViGEmBus driver. RemotePlayCino does not install that driver automatically.

## Build from source

Install the .NET SDK selected by `global.json` and PowerShell 7 on Windows. From the repository root:

```powershell
pwsh -NoProfile -File tools/Build-CI.ps1
```

The script downloads pinned SDL and FFmpeg archives, verifies their SHA-256, restores locked NuGet packages, compiles a self-contained portable and checks extraction in an isolated profile. The publication directory is `build/ci/publication` and contains only the EXE and SHA-256. No existing installation, user settings or local executable is used as a build input.

The [Windows build workflow](.github/workflows/windows-build.yml) runs on GitHub-hosted Windows runners for pushes and pull requests. CI artifacts are identified as unsigned. They are not automatically uploaded over existing Releases.

## Privacy and security

See [Privacy](PRIVACY.md), [Security](SECURITY.md), [Third-party notices](THIRD-PARTY-NOTICES.md) and [Code signing policy](CODE_SIGNING_POLICY.md).

The application checks GitHub for updates and queries ipify for the public IP. These are separate from video transport. Video, audio and enabled input are exchanged directly between the selected host and its admitted guests. Do not share your `Datos` folder, access code or diagnostic logs publicly.

## Code signing policy

The SignPath Foundation integration is being prepared. **The project is not yet accepted or signed by SignPath Foundation.** See [the policy and setup checklist](CODE_SIGNING_POLICY.md) for the current status and approval requirements.

## License

RemotePlayCino's own source is released under the [MIT license](LICENSE). Third-party components retain their own licenses. In particular, the separately executed FFmpeg distribution is GPLv3; its notices and source references must be retained. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
