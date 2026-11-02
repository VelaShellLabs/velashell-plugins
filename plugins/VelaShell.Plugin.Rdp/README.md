# RDP remote desktop plugin

`velashell.rdp` embeds a remote desktop in an Avalonia workspace using a **pure-managed C#
RDP client** — no FreeRDP or bundled native binaries. Windows virtual-file clipboard uses OS OLE interop; explicitly selected COM/LPT ports use overlapped Windows I/O (MS-RDPESP). In the Drive mapping page, enter `COM3;LPT1` in COM / LPT ports; empty shares none. Port names are validated, transfers are capped at 256 KiB and controls are allowlisted. Reads, writes and `WAIT_ON_MASK` wait as on a local port (bounded by the remote application's own COMMTIMEOUTS) until they complete or a purge, close or disconnect cancels them; other controls get a 30-second deadline. Faulty drivers may delay native completion. Fake-backend tests do not access port hardware; real-server/hardware interoperability remains unverified. The protocol stack is the
standalone [`VelaShell.Rdp`](../../src/VelaShell.Rdp) library (MIT), which implements the subset of
MS-RDPBCGR / MS-RDPELE / MS-RDPECLIP / MS-RDPEDISP / MS-RDPEFS / MS-RDPEA this plugin needs. The plugin
references that library and owns only the Avalonia view and input/clipboard integration; no
external RDP window is launched.

Full design and use: [中文](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/RDP远程桌面插件设计与使用.md) · [English](https://github.com/VelaShellLabs/velashell-docs/blob/main/en/host/rdp-plugin-design-and-use.md).

## Managed GFX and H.264

The **Graphics pipeline** setting on **Experience** (`graphicsMode`) defaults to `auto`.
It enables the managed MS-RDPEGFX dynamic channel with AVC420, AVC444/AVC444v2, RemoteFX,
Progressive, ClearCodec, Planar and Alpha decoding. Servers that do not open GFX continue
using classic bitmap/GDI updates. Select `classic` and reconnect to disable GFX explicitly.
Malformed data after GFX negotiation ends the session rather than replaying updates on another path.

The same C# implementation runs on Windows, Linux and macOS. The `.vpx` includes the pure-managed
`ManagedCodecs.H264` 0.6.0 and `ManagedCodecs.Core` assemblies and third-party notices;
no FreeRDP/FFmpeg binary or graphics-specific native build is needed. H.264 uses CPU decoding.
Desktop/scaled-output mappings are implemented; RemoteApp window mappings and Progressive V2
(codec 13) are not. Offline tests cover real IDR/P decoding, AVC444 reconstruction, cache/context
lifetimes and frame acknowledgements. Real Windows-server interoperability and 4K/video performance
still require live-server validation; the CI matrix runs codec/protocol tests on all three OSes.

## Remote audio playback

Enable **Remote audio** on **Experience** (`audio`, off by default), then reconnect. `rdpsnd`
negotiates PCM 8/16-bit mono/stereo at 8–48 kHz, handles training and WaveInfo/Wave or Wave2,
and confirms samples after playback completes. Output uses the system default device through
WinMM (Windows), AudioToolbox AudioQueue (macOS), or ALSA `default` (Linux). Linux needs
`libasound.so.2` and a configured default PCM; PipeWire/PulseAudio desktops need their ALSA plugin.
No native binaries are bundled. Microphone capture, compressed codecs and UDP audio are not supported.
The audio worker has a 128-message / 1 MiB queue bound. Device/protocol failures disable audio and
show a status-bar warning without ending the desktop; fix the default device and reconnect.
Audio continues in background tabs. Check the remote Windows playback-redirection policy and
Windows Audio service if the remote session has no audio device. Tests cover protocol and error
handling; actual speaker output and real-server interoperability require platform hardware validation.

## Legacy printer redirection (Windows)

On **Drive mapping**, explicitly check individual installed printer queues; all are off by default. Read-only background discovery refreshes every minute. The server needs a matching printer driver and must allow printer redirection. This is MS-RDPEPC legacy RAW printing, **not Easy Print/XPS**. File/prompt output queues and nested TS queues are excluded. Library callers use `WindowsPrinterCatalog.GetPrinters()` off the UI thread and `RdpClientOptions.Printers` (up to 16 explicit selections).

CREATE starts a fixed-name RAW document on the selected queue, ordered WRITE requests complete short writes, and CLOSE finishes the document. Each queue allows four open jobs, writes up to 1 MiB and jobs up to 64 MiB. Failures/disconnect abort unfinished jobs; remote paths and arbitrary IOCTLs are rejected. Native spooler calls may block in drivers, so cleanup runs off-thread and finishes only after a blocked call returns. Fake tests exercise protocol/lifecycle; actual local validation only enumerates metadata. No real jobs were printed or live-server printing tested.

## Why managed

The former implementation wrapped FreeRDP (C) behind a C++ ABI bridge with per-platform native
builds and bundled OpenSSL/VC runtimes. This replaces all of it with managed C#: `SslStream`
for TLS, `NegotiateAuthentication` for NLA/CredSSP, and a from-scratch decoder for classic
interleaved-RLE bitmaps. The managed engine and its NuGet dependencies run on every RID with no graphics-native
toolchain or CMake build. C# owns protocol handling, graphics state and presentation.

## Architecture

The engine — the standalone `VelaShell.Rdp` library — exposes `RdpManagedSession` implementing the
`IRdpSession` / `IRdpClipboardTransport` contracts, so the plugin's desktop view, input queue,
clipboard bridge and codec drive it across that boundary. Layers (under `../../src/VelaShell.Rdp/`):

- **Wire** — TPKT/X.224 framing, fast-path frames, ASN.1 PER/BER (`Wire/`).
- **Security** — `RdpTls` (OS trust / name / validity, or an exact SHA-256 leaf pin) and
  `CredSsp` (NLA via NTLM + the TSRequest public-key binding) (`Security/`).
- **MCS/GCC** — Connect-Initial/Response, erect/attach/channel-join (`Mcs/`).
- **PDUs** — client info, licensing, capability exchange, connection finalization (`Pdu/`).
- **Graphics** — interleaved-RLE and RDP 6.0 planar decode, GDI drawing orders with a bitmap cache
  (`Orders.cs`, ported from FreeRDP), pointer (cursor) decode, and a BGRA framebuffer that tracks its changed
  region; fragmented fast-path updates are reassembled. MS-RDPEGFX adds ZGFX decompression,
  surface/cache operations and managed AVC420/AVC444 decoding (`Graphics/`).
- **Input** — fast-path keyboard/mouse/Unicode (`Input/`).
- **Channels** — clipboard (cliprdr), audio playback (rdpsnd), and selected drives, ports and legacy printers (rdpdr)
  over chunked static virtual channels; drdynvc supports graphics (MS-RDPEGFX), display control (MS-RDPEDISP),
  the selected camera (MS-RDPECAM) and selected Windows portable devices (MS-RDPEPNP)
  (`Channels/`).

## Using the plugin

Install the signed RDP `.vpx` from the same release as the other plugins, then create an RDP
workspace connection. Supply the server (default port 3389), username/password and optional
domain or host-managed SSH jump session. Credentials are passed through APIs, never command-line
arguments.

- Desktop resolution follows the workspace tab: on connect and whenever the window resizes (or
  moves to a monitor with another DPI), the plugin asks the server over the display-control channel
  (MS-RDPEDISP) to match the viewport in device pixels, with the local display scale (150% → remote
  UI at 150%, deviceScaleFactor bucketed to 100/140/180) also sent in CS_CORE. A desktop within a
  few device pixels of the view is drawn exactly 1:1 on the pixel grid with no filtering; only a real
  mismatch (a server that cannot follow the window) is scaled to fit with a high-quality filter. Only
  the width is forced even (MS-RDPEDISP). A new session connects at the last view's size and scale
  (`RdpViewMetrics`), so there is no full repaint at the configured size followed by a resize; the
  width/height settings (default 1920 × 1080; width 320–8192, height 200–8192) are the fallback before
  any view exists and for servers without dynamic resolution. Connect timeout 1–120 seconds, default 15.
- **Colour depth** (experience page, `colorDepth`): 16-bit High Color, 24-bit True Color, or 32-bit
  Highest Quality (default, like mstsc). 32-bit compressed bitmaps are RDP 6.0 planar
  (`Graphics/Planar.cs`, ported from FreeRDP) with DRAW_ALLOW_SKIP_ALPHA; verified pixel-identical to
  24-bit against xrdp.
- Frames: decoding and pixel conversion run on the receive thread outside the surface lock; the surface
  keeps the union of changed rectangles and `IRdpSession.CopyFrame` copies only that into the locked
  `WriteableBitmap` (no per-frame full-desktop clone). Bitmaps are clipped to their destination
  rectangle (row padding is never painted). A view that is detached (inactive tab) or whose window is
  minimized stops presenting and sends Suppress Output (when the server supports it); coming back
  resumes updates with a Refresh Rect. Frame posts use Input priority so a frame flood never delays input.
- **Remote pointer**: pointer updates (colour/new/cached/system, 1–32 bpp, AND/XOR rules from FreeRDP)
  are decoded and shown as the desktop's cursor — I-beam over text, resize arrows, hidden while typing.
- Typed text (IME commits, Type clipboard) is batched up to 7 characters (15 fast-path events) per PDU.
- Keyboard, mouse and Unicode text go to the remote desktop. Key combinations the local OS keeps
  for itself (the plugin installs no global keyboard hook) live in the **remote-keys floating
  button** (`RdpQuickBall`) on the desktop edge: drag it and it snaps to the nearer left/right edge;
  click it and two semicircles fan out inwards over a dimmed desktop — inner ring Ctrl+Alt+Del,
  Task Manager (Ctrl+Shift+Esc), Switch window (Alt+Tab), Type clipboard; outer ring Start (Win),
  Run (Win+R), Desktop (Win+D), Lock (Win+L), Screenshot (PrintScreen). Esc or a click on the
  dimmed desktop folds it; neither reaches the remote. In small views the whole fan (items too)
  scales down to 0.6 so labels never collide. Rings are separate hit-test-transparent layers above
  each button (a bordered circle used to let the hover fill overflow it by a pixel and hide half the
  ring) and use `VelaAccent`, i.e. the user's accent colour: hover fades in the accent ring and a
  halo growing outwards, the open ball cross-fades the keyboard into × with a quarter turn (that ×
  turns once more on hover, over a `VelaAccentDim` fill), fan items grow 8% and light their ring on
  hover. **Type clipboard** reads the local clipboard through
  the SDK (`IClipboardApi`, never logged) and types it — runs as Unicode, line breaks as Enter, tabs
  as Tab — for the sign-in screen, UAC and consoles where clipboard sharing does not work. Text with
  line breaks or over 100 characters needs a second click on the same text (status-bar notice
  explains; Enter runs commands in a console); over 2000 characters is refused.
- Deviation from DESIGN.md: there is no 36px document header. Its only action (Ctrl+Alt+Delete)
  moved into the floating button, so the desktop gets the full height above the 24px status bar.
- **Background sessions stay connected.** A paused (hidden) session is completely silent, and NAT
  gateways, firewalls, cloud load balancers and port forwarders expire idle connections after
  minutes without telling either end — the tab then showed a frozen desktop on return and only
  later the disconnect card. The library now enables TCP keep-alive (after 30 s idle, 3 probes 10 s
  apart) and, while display updates are paused, re-sends the paused state every 30 s so even a
  user-space forwarder sees traffic. A path that dies anyway is noticed within about a minute,
  in the background, instead of when the user comes back.
- **Automatic reconnect.** When the network drops (`RdpConnectionLostException`: reset, keep-alive
  timeout, the server closing TCP without a reason) the tab reconnects by itself, like mstsc: up to
  20 attempts after 1, 2, 4, 8, 15 and then every 30 s, the card showing "reconnecting (attempt n
  of 20)". The remote Windows session waits in the disconnected state, so the desktop and its
  programs come back as they were. A reconnect while the tab is in the background starts with
  display updates paused. A session that drops again within a minute continues the same series, so
  a failing path gives up instead of looping. Not reconnected automatically: the server ending the
  session with a reason (signed out, taken over, idle limit, licence — Set Error Info wins over the
  TCP close that follows it), protocol or channel failures, and an attempt the server rejects for
  credentials or certificate. A copy that was in flight when the connection dropped is not resumed.
  Every disconnect, including plain network loss, is logged with its cause.
- When automatic reconnect does not apply or gives up, a **Reconnect** button appears over the
  desktop (the host session tab reconnects too); reconnecting renews credentials and certificate
  approval. The button also works while automatic attempts are waiting.
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

- **Portable devices / WPD** (Windows, same page) are individually unchecked by default.
  Selecting a device grants remote read/change access through MS-RDPEPNP; this is not general
  USB redirection. Background SetupAPI discovery reads metadata only. The native backend uses
  overlapped I/O and accepts only WPD message IOCTLs `0x00404108` and `0x0040C108`, with
  request-specific cancellation and one handle per file channel. Library callers set
  `RdpClientOptions.WpdDevices` to IDs from `RdpWpdDevices.Enumerate()`.
  On Windows 8+, windowless CM notifications revoke even idle removed devices and close their
  handles. Reinsertion restores only an originally selected device with a verified unique PnP
  instance ID, identical hardware IDs and exactly one current match, using a fresh protocol ID
  and refreshed path. Friendly names and unselected devices never authorize sharing. Devices
  without verifiable identity require a new explicit selection/connection. Notification
  registration failure logs degraded I/O-only removal detection; settings edits do not alter
  existing sessions. Tests use fake devices/notifications; physical hardware and server
  interoperability have not been exercised.

The advanced **security layer** setting (`security`) picks what is offered: `auto` (default) offers
TLS and NLA and lets the server choose; `nla` insists on NLA; `tls` offers TLS only, so the server
checks the password from the client info itself. In `auto`, a server that refuses NLA before any
credentials are sent — it answers the first CredSSP message (the NTLM NEGOTIATE) with errorCode
0x80090302 or a TLS abort, i.e. it does not accept incoming NTLM — is retried once with TLS only;
if that is refused too (`HYBRID_REQUIRED_BY_SERVER`) the error explains the server-side fix. A wrong
password is never retried. **Downgrade protection:** the X.224 negotiation is unauthenticated, so in
`auto` a host that once logged on with NLA is remembered (plugin storage key `nla-host:<host>:<port>`)
and later connections to it require NLA; if it then offers TLS only (`RdpNlaUnavailableException`)
or refuses NLA, the connection stops before any password is sent and explains that this can be a
man-in-the-middle forcing a weaker logon (set `tls` explicitly if the server really changed).
Servers speaking CredSSP below v5 (before the CVE-2018-0886 fix) are refused and never retried as
TLS. On Windows, NLA authenticates through the OS's native SSPI Negotiate package, like mstsc:
a DNS host name is targeted as `TERMSRV/<host>` so Kerberos works for domain servers; an IP address
keeps the null target. Other platforms fall back to managed NTLM. TLS 1.2/1.3 with renegotiation
refused. Plaintext password copies (Client Info, TSCredentials) are wiped after sending, and the
options records never print the password. TLS uses OS
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

Microphone capture and multiple monitors are not negotiated. When GFX is disabled or unopened, the server
renders via classic bitmap updates (interleaved RLE, RDP 6.0 planar at 32 bpp) and GDI drawing orders
(DstBlt, PatBlt, OpaqueRect, MultiOpaqueRect, ScrBlt, MemBlt from a 600/600/2048-entry revision 2 bitmap
cache; `RdpClientOptions.DrawingOrders`, on by default) with MPPC bulk compression. Orders let a server
scroll with a screen copy and repaint repeated tiles from the cache instead of resending pixels; xrdp draws
the whole desktop as cached MemBlts, newer Windows servers send bitmaps either way. The order stream
itself is budgeted (16 desktops of drawing per update, a 256 MiB cache). Dynamic resolution uses the display-control channel; GFX reset/surface state follows the server's graphics commands. The engine
runs in-process and shares the host's crash boundary; it is not a sandbox — so everything a server
sends is bounded (2026-11-01 review): bitmap rectangles are clipped and capped (8192 per side, a
per-update budget of four desktop areas) before decoding; virtual-channel reassembly is bounded by
the length declared in its first chunk (32 MB) and the rdpdr queue by bytes; Display Control
messages ≤ 64 KB and only on that channel; CredSSP messages ≤ 1 MB; a server-imposed desktop ≤ 8K
UHD. Drive redirection serves reads of at most 1 MiB, holds at most 4096 open files, refuses writes
starting more than 256 MB past the end, answers every request, and confines paths component by
component: `.`/`..`, control characters and — on Windows — alternate data streams, device names
(CON, NUL, COM¹ …) and trailing dots/spaces are refused, symbolic links/junctions below the mapping
root are refused for opens, creates and rename targets (cloud placeholders are not links), and the
prefix check is case-sensitive off Windows.

## Build

Managed build only — there is no native step:

```powershell
dotnet build .\plugins\VelaShell.Plugin.Rdp -c Release
dotnet build .\plugins\VelaShell.Plugin.Rdp -c Release -t:PackVpx
```

Uses the repository's .NET 11, SDK and Avalonia settings, adding the pure-managed `ManagedCodecs.H264` 0.6.0 dependency for GFX. Platform interop includes `WindowsVirtualFileClipboard` (system ole32/user32/kernel32) and
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
channel wire formats follow MS-RDPBCGR / the published specs. Hardening, rendering, planar, pointer and
input-batching behaviour is covered by `RdpHardeningTests` (incl. a real directory junction and a fake
server sending oversized or pre-CVE-2018-0886 TSRequests), `RdpRenderingTests`, `PlanarTests` (FreeRDP
vectors + fuzzing) and `RdpPointerAndInputTests`; against xrdp, 24-bit and 32-bit (planar) decodes of the
same desktop are pixel-identical (`FullColourDepthsDecodeTheSameDesktop`) and the server's pointer arrives
as a 32×32 I-beam (`TheServersPointerShapesArrive`). Drawing orders: `OrderDecoderTests` (every order
and cache form, bounds, deltas, truncation and 2000 mutations), `RdpDrawingOrderTests` (overlap-safe
screen copies, ROP3 truth table, brush origin, clipping, both update paths); against xrdp the desktop and a
terminal scrolled through 2000 lines are pixel-identical with orders on and off
(`DrawingOrdersPaintTheSameDesktopAsBitmaps`, `AScrollingTerminalEndsTheSameWithDrawingOrders`; xrdp sent
~70 orders updates, 300–500 cached MemBlts). ScrBlt, fills and patterns are not sent by xrdp's Xorg
backend, so they are covered by the unit tests only.
