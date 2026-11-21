# Privacy

This policy describes the source version in this repository, not unrelated software on the user's computer.

## Sharing with guests

When the host starts sharing, RemotePlayCino captures the selected display and, if enabled, audio. It transmits these directly to admitted guests. Visible windows, notifications and other information on the shared display may be seen by those guests. Enabled gamepad, keyboard and mouse input is exchanged between the selected client and host. The application does not use a central streaming relay or upload screen recordings to the project maintainer.

## Web requests

- **GitHub:** On opening the application, it checks the latest release. Update installation downloads release assets after the user requests it. GitHub receives the requester's IP and ordinary connection/request metadata; the updater sends a product User-Agent. No stored preferences or connection logs are attached. See [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
- **ipify:** On opening the application, approximately every five minutes while it remains open, and on a manual refresh, it calls `https://api.ipify.org` to display the public IPv4 address. ipify receives the source IP and ordinary connection metadata. See [ipify](https://www.ipify.org/).

These automatic requests currently have no separate in-app opt-out. They do not carry the screen/video stream. Restricting them may cause address discovery or update checks to fail.

The application has no embedded browser, advertising SDK or implementation that imports browser cookies. Its HTTP handlers currently retain .NET's default cookie handling: a responding web service may set cookies in the handler's memory. The application does not serialize this cookie container to disk. Browser cookies are not part of screen capture or guest transport.

## Local data

The portable stores preferences in `Datos/settings.json`, appearance in `Datos/estilo.txt` and diagnostic connection errors in `Datos/conexion.log`, with a possible previous log. A `.perfil-portatil` marker records that profile preparation is complete. On first portable use, legacy settings may be imported from the previous Windows local profile directory. Optional tracing writes additional diagnostic files next to the program when `trace.on` is enabled.

Preferences and diagnostics can contain host addresses, device/application names, paths and connection details. Review them before sending a bug report. They are not automatically sent to the maintainer. The updater creates temporary download/restart files; .NET can create extraction caches for a self-contained executable.

## Removal and system settings

Close sharing and the application before removing its portable folder. Deleting `Datos` removes the portable profile. Older Windows profile data and runtime caches are separate. If you enabled network priority or created firewall/router rules, those settings can persist after closing or deleting the program; remove the specific settings separately when no longer needed. Closing the application releases its listening socket, but does not delete a firewall or router rule.

## Questions

Use the issue tracker for general questions. Do not post access codes, personal addresses, credentials or unredacted logs in a public issue.
