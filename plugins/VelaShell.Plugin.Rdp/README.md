# RDP remote desktop plugin

`velashell.rdp` embeds a remote desktop in an Avalonia workspace using a **pure-managed C#
RDP client** — no native code, no FreeRDP, no bundled binaries. The protocol stack lives under
[`Managed/`](Managed/) and implements the subset of MS-RDPBCGR / MS-RDPELE / MS-RDPECLIP /
MS-RDPEFS this plugin needs. The plugin owns the Avalonia view and input/clipboard integration;
no external RDP window is launched.

Full design and use: [中文](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/RDP远程桌面插件设计与使用.md) · [English](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/rdp-plugin-design-and-use.md).

## Why managed

The former implementation wrapped FreeRDP (C) behind a C++ ABI bridge with per-platform native
builds and bundled OpenSSL/VC runtimes. This replaces all of it with managed C#: `SslStream`
for TLS, `NegotiateAuthentication` for NLA/CredSSP, and a from-scratch decoder for classic
interleaved-RLE bitmaps. The result is one assembly that runs on every RID with no native
toolchain, no CMake, and nothing to compile per platform — far simpler to maintain.

## Architecture

The managed engine (`Managed/RdpManagedSession`) implements the `IRdpSession` /
`IRdpClipboardTransport` contracts, so the existing desktop view, input queue, clipboard bridge
and codec drive it unchanged. Layers:

- **Wire** — TPKT/X.224 framing, fast-path frames, ASN.1 PER/BER (`Managed/Wire/`).
- **Security** — `RdpTls` (OS trust / name / validity, or an exact SHA-256 leaf pin) and
  `CredSsp` (NLA via NTLM + the TSRequest public-key binding) (`Managed/Security/`).
- **MCS/GCC** — Connect-Initial/Response, erect/attach/channel-join (`Managed/Mcs/`).
- **PDUs** — client info, licensing, capability exchange, connection finalization (`Managed/Pdu/`).
- **Graphics** — interleaved-RLE decode and a BGRA framebuffer; fragmented fast-path updates are
  reassembled (`Managed/Graphics/`).
- **Input** — fast-path keyboard/mouse/Unicode (`Managed/Input/`).
- **Channels** — clipboard (cliprdr) and drive redirection (rdpdr) over chunked static virtual
  channels (`Managed/Channels/`).

## Using the plugin

Install the signed RDP `.vpx` from the same release as the other plugins, then create an RDP
workspace connection. Supply the server (default port 3389), username/password and optional
domain or host-managed SSH jump session. Credentials are passed through APIs, never command-line
arguments.

- Fixed desktop resolution defaults to 1280 × 720. Width: 320–8192; height: 200–8192. Connect
  timeout: 1–120 seconds, default 15. **Fit desktop** and **100%** change local display scaling,
  not the server resolution.
- Keyboard, mouse, Unicode text and the **Ctrl+Alt+Delete** button send remote input. Reconnect
  through the host session tab to renew credentials and certificate approval.
- **Bidirectional clipboard** is off by default. When enabled, remote text/images are received
  automatically; **Receive clipboard** explicitly downloads remote files/directories before
  publishing local file references. **Send clipboard**, focus and Ctrl+V publish local content.
- Received files remain in `<plugin DataDirectory>/rdp-clipboard`. Limits: 16 MiB per clipboard
  payload; images also require dimensions ≤8192 and decoded pixels ≤16 MiB; 4096 file descriptors,
  1 GiB per transfer and 4 GiB retained cache. Unsafe names and symbolic links are rejected.
- **Shared folders** defaults to empty. Add one `name=absolute-path` per line, e.g.
  `work=C:\Work\Share` or `work=/home/alice/share`. At most 32 mappings; names are 1–8 ASCII
  letters, digits or underscores. The remote desktop gets **read/write** access under
  `\\tsclient\work`. Paths are confined to the mapping; traversal and symlinks are rejected.

Security negotiates TLS (and NLA when the server requires it). TLS uses OS trust/name/validity
verification, or an explicitly approved exact SHA-256 leaf-certificate pin that replaces
CA/name/expiry checks. Legacy RDP transport security is disabled. SSH tunnelling preserves the
original server identity for certificate checks.

No audio, multiple monitors, dynamic resolution, drawing-order acceleration (RemoteFX/GFX) or
MPPC stream compression are negotiated: the server renders via classic RLE bitmap updates. The
engine runs in-process and shares the host's crash boundary; it is not a sandbox.

## Build

Managed build only — there is no native step:

```powershell
dotnet build .\plugins\VelaShell.Plugin.Rdp -c Release
dotnet build .\plugins\VelaShell.Plugin.Rdp -c Release -t:PackVpx
```

Uses the repository's .NET 11, SDK and Avalonia settings without changing dependencies. The one
platform-specific touch is `UnixClipboardFile`, which P/Invokes the OS `libc` (`open`/`fstat`)
to open a clipboard file for local→remote transfer without blocking on a FIFO or following a
symlink; Windows uses the .NET file API.

## Verification

Unit tests cover the settings, clipboard codec, headless UI and the CredSSP/NLA ASN.1 encoding.
Live interoperability tests (gated on `VELA_RDP_TEST_TARGET`, skipped in CI) exercise the full
stack against a real server — connection, TLS and leaf-pin trust, desktop rendering, keyboard and
mouse input, text and image clipboard round-trips, and drive-redirection read and write. NLA is
validated offline (no NLA test server available); a live Windows-server NLA check is still
outstanding. Interleaved-RLE decode, MCS/GCC, capability exchange and the channel wire formats
follow MS-RDPBCGR / the published specs.
