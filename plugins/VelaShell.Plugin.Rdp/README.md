# RDP remote desktop plugin

`velashell.rdp` embeds a remote desktop in an Avalonia workspace using a **pure-managed C#
RDP client** — no FreeRDP or bundled native binaries. Windows virtual-file clipboard uses OS OLE interop. The protocol stack is the
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
- **Bidirectional clipboard** is off by default. Text/images synchronize as before. On Windows,
  remote Ctrl+C publishes virtual file/directory descriptors only. Paste in the chosen local
  Explorer directory requests IStream content; only then do RDP FILECONTENTS requests transfer
  bytes. Explorer owns the destination and writes files. No download cache, global key hooks,
  inferred destination, or manual receive button is involved. Local→remote is also lazy: publication
  enumerates names/sizes and opens safe source handles without reading content; the remote shell's
  FILECONTENTS requests read/send bytes after paste in its chosen directory.
- Descriptors in both directions set FD_SHOWPROGRESSUI (0x4000), as FreeRDP's `wf_cliprdr.c` and
  `synthetic_file.c` do, so the receiving Windows Shell runs its own copy-progress dialog instead of
  a large paste looking like a hung Explorer. Background vs. blocking extraction remains the receiving
  Shell's decision (rdpclip's data object remotely; ours supports IDataObjectAsyncCapability).
- Both transfer indicators and cancel buttons share the existing fixed **24px resolution bar**,
  right-aligned, one group per direction: direction · size · name, a fixed 72px/3px bar, the state
  (percentage while transferring), and the host transfer panel's 16px button with a 10px Lucide `x`
  (`RdpGlyph` scales the pen like the host's `LucideIcon`). Under pressure the name trims, then the
  state; the bar and × keep their size.
  Ready means no content has transferred; delivered/sent bytes do not certify destination save.
  Replacing the clipboard retires the publication while acquired streams retain independent leases.
  Negotiated CAN_LOCK_CLIPDATA, LOCK/UNLOCK and clipDataId preserve remote versions; peer locks likewise
  retain local source handles across new copies. Retired versions unlock after consumers finish or release
  their streams/data object. Explicit cancellation, disconnect and disposal invalidate active leases.
  Without peer locking, remote source replacement cannot be preserved; provider failure or process exit
  can still fail a transfer even with locks. Only unlocked idle local handles expire after 30 seconds
  and are prepared again on the next remote paste.
- A sent file-block request waits up to 30 seconds for its response. Only that response timeout gets
  up to two additional attempts, after cancellable 500/1000 ms delays. Each attempt keeps the same
  file version, index, offset and length but uses a fresh streamId; late responses are discarded.
  The existing bar shows Retrying, and only successfully delivered unique ranges count toward progress.
  There is no whole-file retransmission, duplicate destination write, or reconnect resume. Explicit
  remote CB_RESPONSE_FAIL has no transient classification and is not retried; neither are send errors,
  cancellation, disconnect or unlocked source replacement. TCP is reliable: a remote rejection is not
  evidence of packet loss. LOCK precedes descriptors; clipDataId zero is valid and differs from an absent
  field. Request/response/timeout diagnostics include operation, streamId, index, offset, length, attempt,
  responseFlags, lock capability and clipDataId where applicable; LOCK/UNLOCK and unmatched replies are
  logged without file content or credentials. These identify the failing exchange, not the server provider's
  internal reason; real-server 200+MB paste still needs field verification.
- Linux/macOS remote→local virtual file paste is explicitly unsupported by this integration;
  the status bar explains this, and no hidden automatic download occurs. Text/images and lazy
  local→remote file sharing remain available. Limits remain 16 MiB per clipboard payload, image
  dimensions ≤8192 and decoded pixels ≤16 MiB, 4096 descriptors and 1 GiB per transfer. Unsafe names,
  traversal, collisions and symbolic links are rejected. Existing caches from older versions are
  untouched; this implementation creates no clipboard cache, staging or temporary file in either direction.
  Native IStream fills the caller's buffer in bounded 256 KiB chunks without accumulating a whole file.
  Progress measures bytes obtained/provided by the plugin, not confirmed writes to the destination.
  A missing or not-yet-visible partial file does not establish where Explorer is writing: creation,
  flushing, cleanup and any temporary files used by the destination application are outside plugin control.
- Windows OLE accepts DWORD Performed DropEffect, Logical Performed DropEffect and Paste Succeeded,
  plus the 16-byte TargetCLSID. Successful SetData copies feedback and honors STGMEDIUM ownership;
  feedback does not end extraction or dispose consumer streams. Successful IDataObjectAsyncCapability
  EndOperation releases the operation lease while consumer streams retain their own leases; failure
  cancels extraction. Ordinary unsupported format/aspect probes return PreserveSig HRESULTs directly,
  avoiding managed exception noise. Failed required formats remain errors, distinct from explicit remote
  FILECONTENTS rejection. CLIPBRD_E_CANT_OPEN is retried up to eight attempts with asynchronous bounded backoff.
  Publication avoids reading back its own virtual clipboard and rechecks the local sequence on each retry.
- **Drive mapping** is its own page of the connection dialog, mstsc-style. It lists one checkbox per
  local disk — on Windows every drive letter, labelled by type ("Local Disk (C:)", "Removable Disk
  (E:)", "Network Drive (Z:)", "CD/DVD Drive (F:)"); on Linux/macOS the root (`/`), the home folder
  (`~`) and mounts under `/media`, `/run/media`, `/mnt` or `/Volumes`. All are **unchecked by
  default**. Disks are enumerated without touching media (no volume query), and a ≥5 s watcher
  re-registers the form when the set of disks changes, so a USB disk plugged in later appears the
  next time the dialog opens. A ticked disk that is absent at connect time is skipped, not an error.
- **Custom folders** (same page) take `name=absolute-path` entries separated by semicolons, e.g.
  `work=C:\Work\Share;docs=D:\Docs` — the host renders the field single-line, so `;` is the
  separator (newlines from older saved values still work). At most 32 mappings in total; names are
  1–8 ASCII letters, digits or underscores and must not clash with a ticked disk's name (`C`, `E`,
  `root`, `home`, …). The remote desktop gets **read/write** access to ticked disks and custom
  folders under `\\tsclient\<name>`. Paths are confined to the mapping; traversal and symlinks are
  rejected. The field's hint (always visible, unlike the placeholder) shows the format with
  examples for the current OS, and a wrong entry fails the connection with a message that quotes
  that entry and the expected format — or names the clashing name, or the 32-mapping limit.

The advanced **security layer** setting (`security`) picks what is offered: `auto` (default) offers
TLS and NLA and lets the server choose; `nla` insists on NLA; `tls` offers TLS only, so the server
checks the password from the client info itself. In `auto`, a server that refuses NLA before any
credentials are sent — it answers the first CredSSP message (the NTLM NEGOTIATE) with errorCode
0x80090302 or a TLS abort, i.e. it does not accept incoming NTLM — is retried once with TLS only;
if that is refused too (`HYBRID_REQUIRED_BY_SERVER`) the error explains the server-side fix. A wrong
password is never retried. On Windows, NLA authenticates through the OS's native SSPI Negotiate
package (SPNEGO), exactly like mstsc — a null target yields SPNEGO rather than the bare NTLM that
hardened, NLA-required servers reject; other platforms fall back to managed NTLM. TLS uses OS
trust/name/validity verification, or an explicitly approved
exact SHA-256 leaf-certificate pin that replaces CA/name/expiry checks, identically in every mode.
Legacy RDP transport security is disabled. SSH tunnelling preserves the original server identity
for certificate checks. Failures name the stage and cause chain (SChannel/socket codes, negotiated
TLS version); the plugin log adds the cipher suite and the TLS record headers the server sent. A
session dropped by the server mid-way surfaces the Set Error Info reason (signed out, taken over,
no Remote Desktop licence, …) in the disconnect overlay instead of a blank "disconnected".

Clipboard (when enabled) uses ordinary Ctrl+C / Ctrl+V: local→remote publication waits for the
server's format-list acknowledgement before sending Ctrl+V. The cliprdr handshake completes even
before the workspace attaches, and format names follow the server's negotiated capabilities.
Remote text/images synchronize automatically; Windows remote file descriptors are published without
content reads. A newer local copy supersedes pending publication. Unchanged content is not echoed back.

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

Uses the repository's .NET 11, SDK and Avalonia settings without changing dependencies. Platform interop includes `WindowsVirtualFileClipboard` (system ole32/user32/kernel32) and
`UnixClipboardFile`, which P/Invokes the OS `libc` (`open`/`fstat`)
to open a clipboard file for local→remote transfer without blocking on a FIFO or following a
symlink; Windows source files use the .NET file API.

Avalonia 12.1.4 supports delayed values via `DataTransferItem.Set(format, Func<T>)`, but its
public file format is `IStorageItem`; application format identifiers are transformed and cannot
express native FileContents `FORMATETC.lindex` plus `TYMED_ISTREAM`. The Windows adapter therefore
implements OLE `IDataObject` with `FileGroupDescriptorW`, indexed `FileContents` streams and copy
drop effect on a dedicated STA message thread. Reads wait only there (or on a shell COM caller),
not on Avalonia UI or the RDP receive pump. It never flushes OLE content on teardown.

## Verification

Focused tests cover descriptor-only copy, demand-driven reads, cancellation/disconnect and fixed
status-bar geometry. A Windows native COM-vtable test exercises IDataObject HGLOBAL descriptors and
IStream acquisition/read/seek/release without replacing the user clipboard or pasting to disk.
Actual Explorer paste against a live RDP server still requires field validation. Unit tests also
cover settings, clipboard codec, headless UI and CredSSP/NLA ASN.1 encoding.
Live interoperability tests (gated on `VELA_RDP_TEST_TARGET`, skipped in CI) exercise the full
stack against a real server — connection, TLS and leaf-pin trust, desktop rendering, keyboard and
mouse input, text and image clipboard round-trips, and drive-redirection read and write. NLA is
verified live against the FreeRDP shadow server (`VELA_RDP_TEST_NLA_TARGET`): logon, wrong
password without fallback, TLS-only refused by an NLA-only server. The security-layer fallback is
covered by in-process fake-server tests (`FakeRdpServer`). A successful NLA logon against a real
Windows server is still outstanding. Interleaved-RLE decode, MCS/GCC, capability exchange and the
channel wire formats follow MS-RDPBCGR / the published specs.
