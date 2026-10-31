# RDP remote desktop plugin

`velashell.rdp` embeds a remote desktop in an Avalonia workspace using a **pure-managed C#
RDP client** — no native code, no FreeRDP, no bundled binaries. The protocol stack is the
standalone [`VelaShell.Rdp`](../../src/VelaShell.Rdp) library (MIT), which implements the subset of
MS-RDPBCGR / MS-RDPELE / MS-RDPECLIP / MS-RDPEDISP / MS-RDPEFS this plugin needs. The plugin
references that library and owns only the Avalonia view and input/clipboard integration; no
external RDP window is launched.

Full design and use: [中文](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/RDP远程桌面插件设计与使用.md) · [English](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/rdp-plugin-design-and-use.md).

## Why managed

The former implementation wrapped FreeRDP (C) behind a C++ ABI bridge with per-platform native
builds and bundled OpenSSL/VC runtimes. This replaces all of it with managed C#: `SslStream`
for TLS, `NegotiateAuthentication` for NLA/CredSSP, and a from-scratch decoder for classic
interleaved-RLE bitmaps. The result is one assembly that runs on every RID with no native
toolchain, no CMake, and nothing to compile per platform — far simpler to maintain.

## Architecture

The engine — the standalone `VelaShell.Rdp` library — exposes `RdpManagedSession` implementing the
`IRdpSession` / `IRdpClipboardTransport` contracts, so the plugin's desktop view, input queue,
clipboard bridge and codec drive it across that boundary. Layers (under `../../src/VelaShell.Rdp/`):

- **Wire** — TPKT/X.224 framing, fast-path frames, ASN.1 PER/BER (`Wire/`).
- **Security** — `RdpTls` (OS trust / name / validity, or an exact SHA-256 leaf pin) and
  `CredSsp` (NLA via NTLM + the TSRequest public-key binding) (`Security/`).
- **MCS/GCC** — Connect-Initial/Response, erect/attach/channel-join (`Mcs/`).
- **PDUs** — client info, licensing, capability exchange, connection finalization (`Pdu/`).
- **Graphics** — interleaved-RLE decode and a BGRA framebuffer; fragmented fast-path updates are
  reassembled (`Graphics/`).
- **Input** — fast-path keyboard/mouse/Unicode (`Input/`).
- **Channels** — clipboard (cliprdr) and drive redirection (rdpdr) over chunked static virtual
  channels, plus the display-control dynamic channel (drdynvc + MS-RDPEDISP) for dynamic
  resolution (`Channels/`).

## Using the plugin

Install the signed RDP `.vpx` from the same release as the other plugins, then create an RDP
workspace connection. Supply the server (default port 3389), username/password and optional
domain or host-managed SSH jump session. Credentials are passed through APIs, never command-line
arguments.

- Desktop resolution follows the workspace tab: on connect and whenever the window resizes, the
  plugin asks the server over the display-control channel (MS-RDPEDISP) to match the viewport, so
  the remote renders 1:1. The width/height settings (default 1920 × 1080) are the initial size and
  the fallback when a server declines dynamic resize. Width 320–8192; height 200–8192. Connect
  timeout 1–120 seconds, default 15. **Fit desktop** scales a mismatched desktop into the tab
  (smoothed); **100%** shows it 1:1 with scrollbars.
- Keyboard, mouse, Unicode text and the **Ctrl+Alt+Delete** button send remote input. When a
  session drops, a **Reconnect** button appears over the desktop (the host session tab reconnects
  too); reconnecting renews credentials and certificate approval.
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

No audio, multiple monitors or drawing-order acceleration (RemoteFX/GFX) are negotiated: the
server renders via classic RLE bitmap updates with MPPC bulk compression. Dynamic resolution uses
the display-control channel only; the graphics pipeline (GFX) is declined. The engine runs
in-process and shares the host's crash boundary; it is not a sandbox.

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
