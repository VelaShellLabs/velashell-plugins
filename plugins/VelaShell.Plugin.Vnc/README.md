# VNC remote desktop plugin

`velashell.vnc` embeds a VNC (RFB) remote desktop in an Avalonia workspace using a **pure-managed C#
client**: no native VNC library, no bundled binaries, no external viewer window. The protocol stack is the
standalone [`VelaShell.Vnc`](../../src/VelaShell.Vnc) library (MIT, UI-agnostic, no SDK dependency); the plugin
owns the Avalonia view, input and clipboard integration. The split and the workspace shell mirror the
[RDP plugin](../VelaShell.Plugin.Rdp/README.md): Plugin → Provider → Document → View → DesktopControl.

Behaviour follows RFC 6143 (RFB 3.8) and the community protocol registry (rfbproto) for the extensions
listed below.

Full design and use: [中文](https://github.com/VelaShell/velashell-docs/blob/main/zh/host/VNC远程桌面插件设计与使用.md) · [English](https://github.com/VelaShell/velashell-docs/blob/main/en/host/vnc-plugin-design-and-use.md).

## What it does

| Area | Supported |
| --- | --- |
| Protocol versions | RFB 3.3, 3.7, 3.8 (Apple's 3.889 is treated as 3.8; 3.4–3.6 as 3.3) |
| Security | None (1), VNC authentication (2), VeNCrypt 0.2 with X.509 TLS (subtypes 260 None / 261 VNC / 262 Plain), Apple Remote Desktop (30) |
| Encodings | Raw, CopyRect, RRE, Hextile, ZRLE, Tight (fill, copy / palette / gradient filters, JPEG) |
| Pseudo-encodings | ExtendedDesktopSize + SetDesktopSize, DesktopSize, Cursor, LastRect, DesktopName, QEMU Extended Key Event, QEMU Audio, Extended Clipboard, SupportedMessages (when file transfer is enabled), Continuous Updates + Fence, JPEG quality, compression level |
| Clipboard | Text both ways only: UTF-8 through the extended clipboard (Notify / Request / Provide), Latin-1 ServerCutText / ClientCutText otherwise; no file clipboard |
| Audio | QEMU Audio (QEMU / libvirt consoles): S16 stereo 44.1 kHz through WaveOut / AudioQueue / ALSA |
| File transfer | UltraVNC file transfer (UltraVNC Server, `x11vnc -ultrafilexfer`): browse, upload, download, new folder, delete, rename |
| Transport | Host network proxy (`INetworkApi`), host-managed SSH jump session, TCP keep-alive 30 s |

Not supported, deliberately: anonymous-TLS VeNCrypt subtypes 257–259 (anonymous Diffie-Hellman cipher suites,
which neither SChannel nor .NET's OpenSSL/Apple TLS bindings offer), VeNCrypt Plain without TLS (256, the
password would travel in the clear), RA2 / RealVNC encryption (5, 6), UltraVNC MS-Logon, TightVNC's file-transfer protocol,
transferring whole folders, audio input, colour depths below 24 bits, TightPNG, Open H.264 (encoding 50: the common
servers do not send it; only neatvnc built with it and an FFmpeg / V4L2 encoder does).

## Security choice

`security` = `auto` (default) picks, in order: VeNCrypt (when the server offers an X.509 subtype), Apple Remote
Desktop (when a username is given), VNC authentication, None, then ARD without a username (which then asks for
one). If Auto picked VeNCrypt and the server turns out to offer only anonymous-TLS subtypes, the client connects
once more and takes the next type — the security type cannot be changed on the same connection. An explicit
choice never falls back to another type.

- **VNC authentication** uses DES with the password's first eight characters. The cipher is implemented in
  managed code (`Security/Des.cs`, FIPS 46-3): OpenSSL 3 moved single DES to its legacy provider, which most
  Linux distributions do not load, so the platform cipher would make the three OSes behave differently.
- **VeNCrypt X.509**: the certificate is validated by the OS for the target host name; a self-signed
  certificate goes through the host's trust prompt (`WorkspaceFeatures.CertificateTrust`), and the approved
  SHA-256 thumbprint is stored in the hidden `trustedThumbprint` field and pinned from then on. A changed
  certificate is offered again, worded as a change. A revoked certificate is refused outright. Behind an SSH
  tunnel the certificate must name the tunnel's real target, not the local forwarding endpoint.
- **Apple Remote Desktop** (macOS Screen Sharing): Diffie-Hellman with the server's prime, MD5 of the shared
  secret as the AES-128 key, username and password (UTF-8, 63 bytes max each) in the encrypted block.
- Credentials are requested from the host's login dialog: a missing password raises
  `ProtocolAuthenticationException` before anything is sent, so the host asks again. Exceptions and logs never
  contain credentials.

## Display

- **Display size** (`scaling`): *Match window* (default) asks the server to resize its desktop to the tab in
  device pixels through SetDesktopSize when the server answered ExtendedDesktopSize (for example TigerVNC),
  keeping the server's screen id; requests are coalesced 350 ms after the window stops changing
  and ignore jitter under 8 px. Servers that cannot resize are scaled to fit. *Scale to fit* never resizes the
  remote. *Actual size* draws 1:1 with scroll bars (centred when smaller than the view).
- A desktop within a few device pixels of the view is drawn exactly 1:1 on the pixel grid with no filtering;
  only a real mismatch is resampled (high quality).
- Frames are decoded on the receive thread into a BGRA framebuffer that keeps the union of changed rectangles;
  `CopyFrame` copies only that into the locked `WriteableBitmap`. Frame posts use Input priority.
- **Continuous updates** (`continuousUpdates`, advanced, on): the client lists Continuous Updates (-313) and Fence
  (-312). Once the server confirms with EndOfContinuousUpdates (150) — TigerVNC only does for a fence-capable
  client — the next update turns them on for the whole desktop (EnableContinuousUpdates 150), and the server
  pushes changes itself instead of waiting for one request per frame (as the TigerVNC viewer does). Server fence
  requests (248) are answered at once with the payload, keeping BlockBefore / BlockAfter (messages are handled in
  order) and clearing SyncNext / Request; TigerVNC paces its pushes by these round trips. A resize re-sends the
  area; a server ending them on its own gets requests again. Without the extension (or with the setting off) the
  next incremental update is requested as soon as one has been decoded.
- A hidden tab or minimized window receives no updates (continuous updates off, or no request); showing it again
  re-enables them (or requests one), and the server sends what changed meanwhile — no full refresh.
- **Picture quality** (`quality`): High (JPEG 8, default) / Medium (6) / Low (2) / Lossless (no JPEG). JPEG
  rectangles are decoded by the host's Skia through Avalonia's `Bitmap` (`VncJpeg`), so neither the plugin nor
  the library ships a codec. **Preferred encoding** (`encoding`) and **Compression level** (`compression`,
  0–9, default 2) are on the Display page.
- The server's cursor (Cursor pseudo-encoding) replaces the local pointer over the desktop; an empty cursor
  hides it.

## Input

- Typed text goes as the keysym of each character (Latin-1 directly, everything else `0x01000000 + code point`),
  so it arrives as typed whatever the two keyboard layouts are. Keys without text (arrows, F-keys, modifiers)
  and shortcuts go as the US-layout keysym of the physical key; when the server confirmed the QEMU Extended Key
  Event, the XT scan code goes with it (layout-independent, as on a physical keyboard).
- AltGr (Windows, reported as Ctrl+Alt) and Option (macOS) typing a character is treated as text: the held
  Ctrl/Alt is lifted around the character and pressed again.
- **Command key on macOS** (`macCommand`, advanced): *Send as Ctrl* (default) so Cmd+C/V copy and paste on a
  Windows or Linux desktop; *Send as Super / ⌘* for a Mac server.
- Mouse: RFB button mask (left/middle/right), each wheel notch as a press and release of buttons 4–7, fractional
  wheel deltas carried over; pointer moves are coalesced so a slow link never queues a backlog.
- One ordered input queue (a key-up can never overtake its key-down); an overflowing queue ends the session as a
  lost connection rather than dropping a key-up.
- **Remote keys** floating button (same component as RDP): Ctrl+Alt+Del, Alt+Tab, Alt+F4, Type clipboard; outer
  ring Super, Win+R, Win+L, PrintScreen. *Type clipboard* types local clipboard text as keystrokes (confirmation
  above 100 characters or with line breaks, refused above 2000).
- **View only** (`viewOnly`) sends no keyboard, pointer or clipboard data and hides the remote-keys button.

## Text clipboard reliability

Local publication and remote writes share a FIFO queue. Deduplication is committed only after success; a paste-time
duplicate waits for an in-flight focus publication, and failures remain retryable. Disposal cancels active work and
prevents queued work from starting (an already-started host call that ignores cancellation cannot be forcibly undone).
Extended-clipboard Caps sizes limit unsolicited Provide only: a Request receives the full text, never a truncated
UTF-8 sequence. The protocol entry point rejects text over 20 MiB, including CRLF normalization and the terminating
NUL, before announcing it. Late Caps re-announce the original Unicode text when Notify is supported. Regression
cases cover these paths; they have not been executed during this repair.

## Audio

- **Play remote audio** (`audio`, off by default like RDP's). The client lists the QEMU Audio pseudo-encoding (-259);
  when the server confirms it (a rectangle with encoding -259 — QEMU fills in the desktop size, which means nothing),
  it sends set-format (S16, stereo, 44.1 kHz) and enable. Only QEMU / libvirt consoles implement it; others ignore it.
- Samples are little-endian (QEMU's default). The PCM output is `VelaShell.Rdp`'s, copied into `src/VelaShell.Vnc/Audio/`
  (plugins cannot share assemblies).
- The receive loop never blocks on audio: `VncAudioPlayer` gathers 40 ms buffers and keeps at most 8 queued at the
  device; when the output falls behind, the newest data is dropped (QEMU drops under back-pressure as well).
- A failing device stops audio only: a status-bar notice (kept as the status tooltip), a disable message to the
  server, and the desktop goes on. A buffer that is not whole frames stops audio too (skipped, stream in step); one over 1 MiB is a protocol error.

## File transfer

UltraVNC file transfer (RFB message type 7), as UltraVNC Server and LibVNC (`x11vnc -ultrafilexfer`) implement it.
TigerVNC, QEMU and macOS Screen Sharing do not support this extension. For TigerVNC, use a separate SSH/SFTP
connection to transfer files; this fix adds no automatic SFTP backend. The UltraVNC file panel is separate from
the clipboard: only bidirectional text clipboard is supported, not copying and pasting files.

- **File transfer** (`fileTransfer`, off by default). When enabled, `VncSession` includes SupportedMessages
  (`-131071`, `0xFFFE0001`) in SetEncodings and reads the server's two 32-byte bitmaps for client-to-server and
  server-to-client messages. By default, both bitmaps must advertise message type 7 before an UltraVNC access
  request is sent. Opening the file panel waits at most 2 seconds for this capability information; absent or
  explicitly unsupported capabilities raise `VncFileTransferException` with `Unsupported`, shown in the panel
  while the desktop connection stays open. View only disables file transfer.
- **Legacy compatibility** (`allowUnadvertisedFileTransfer`, advanced boolean, default `false`, visible only when
  `fileTransfer=true`). Enable only for an older UltraVNC-compatible server whose file-transfer support has been
  confirmed but which does not advertise capabilities. It permits an access request without an advertisement,
  but cannot override an explicit negative advertisement. Misuse can still disconnect the desktop: a server
  that does not understand message type 7 may close the connection.
- Access: AbortFileTransfer with version 2 → FileTransferAccess (1 granted / -1 refused). UltraVNC then sends its
  protocol version: ≥ 3 → SessionStart, ≥ 4 → Unicode names (0x80 flag in the pad byte, UTF-16 entries) and its block
  size. LibVNC sends none: 8 KiB blocks, single drive `C:` mapped to `/`.
- Paths are Windows-style, sent as UTF-8. One operation at a time (no request ids); replies are routed to it and
  leftovers dropped; after a failed operation the next one waits for 500 ms of quiet first. 30 s per reply; 15 s for
  access, where no answer counts as refused. Cancellation acts between messages, never mid-write.
- Download asks for compressed packets; fewer bytes than announced is a failure (LibVNC's header has only a 32-bit size,
  so more is not). Upload: Offer (`path,MM/DD/YYYY HH:MM` + high size word) → FileAcceptHeader → blocks (compressed
  when smaller) → EndOfFile. Cancel or a local error sends AbortFileTransfer.
- New folder / delete / rename through Command / CommandReturn. UltraVNC deletes a folder recursively, LibVNC only an
  empty one; the confirmation says so.
- Limits on server data: body 2 MiB, inflated packet 1 MiB, 100,000 entries per folder.

**File dialog** (`VncFilePanel`, positioned by `VncFileDialog`): the folder button at the right of the status bar
opens a centered overlay, initially 800 x 600 px. Drag the title bar to move it within the desktop area, or drag any
edge or corner to resize it. It keeps 16 px margins and normally has a minimum size of 400 x 280 px; smaller views
take priority so the close button remains reachable. Reopening retains the chosen size and position for this session.
It does not take space from the desktop: opening, moving, resizing, closing and reopening leave the viewport unchanged
and do not trigger remote resolution changes.
The dialog uses the host's 28px dialog title bar, followed by a 36px toolbar, 26px path bar, 28px rows and 24px status line.
The scrim blocks remote mouse input and Tab stays in the dialog. Escape cancels an inline prompt first, otherwise
it closes the dialog and returns focus to the desktop. Closing hides the dialog without cancelling transfers or
discarding its state; disconnecting also hides it. The first opening requests access
and enters the only drive directly. Up / refresh / new folder / upload in the toolbar; download / rename / delete for
the selection; Enter or double-click opens or downloads, Backspace goes up, Delete deletes; files dropped onto the
panel upload into the open folder. Confirmations and names are inline. Existing names are confirmed once per batch and
deleted before uploading (UltraVNC would resume onto the old file). A failed or cancelled download deletes the partial
local file. Local files come from the host window's `IStorageProvider` pickers (in-process, like the Docker panel);
the manifest declares `fs.local.read` / `fs.local.write`.

## Connection lifetime

- A dropped network connection (reset, timeout) reconnects automatically with back-off (1, 2, 4, 8, 15, 30 s; 20
  attempts), stopping at once when the password or certificate changed.
- A server that closes the connection itself (session ended, or a non-shared viewer elsewhere took over) is
  **not** reconnected automatically — with `shared` off on both sides that would start a tug of war — and the
  disconnect card offers Reconnect.
- `shared` (default on) is the ClientInit shared flag; turning it off asks the server to disconnect other viewers.

## Architecture

`../../src/VelaShell.Vnc/`:

- **Wire** — buffered big-endian reader/writer (`RfbReader`, `RfbWriter`); every client message is one write.
- **Security** — managed DES and VNC authentication, Apple Remote Desktop, VeNCrypt TLS with pinning (`VncTls`).
- **Graphics** — `VncFramebuffer` (bounds-checked, dirty region), `RectangleDecoder` (all encodings, persistent
  zlib streams in `ZlibChannel`: one for ZRLE, four for Tight, each rectangle sync-flushed).
- **Audio** — `VncAudioPlayer` and the PCM output (WaveOut / AudioQueue / ALSA), the same source as `VelaShell.Rdp`'s `Audio/`.
- **FileTransfer** — `UltraFileTransfer` (UltraVNC file transfer, message type 7).
- **VncSession** — handshake, receive loop, input, clipboard (`ExtendedClipboard`), resize, audio and file-transfer dispatch.

Server data is untrusted: rectangles outside the desktop, desktop sides over 16384, compressed payloads over
64 MiB, inflated data larger than the rectangle allows, palette indices out of range, runs past a tile and cursors
over 256×256 end the session with a protocol error instead of allocating or writing out of bounds.

## Using the plugin

Install the `.vpx` from the same release as the other plugins and create a VNC workspace connection: server
(default port 5900 — display `:1` is 5901), password, and a username only for VeNCrypt Plain or macOS. For a
server bound to localhost (the usual `x11vnc -localhost` / `vncserver -localhost` setup) pick a saved SSH session
as the jump host; the host builds the tunnel and the plugin connects to its local endpoint.

## Build

```bash
dotnet build plugins/VelaShell.Plugin.Vnc
dotnet build plugins/VelaShell.Plugin.Vnc -t:PackVpx      # bin/vpx/velashell.vnc-<version>.vpx
dotnet test  tests/VelaShell.Plugin.Vnc.Tests
```

## Verification

`tests/VelaShell.Plugin.Vnc.Tests` exercises the real `VncSession` against a scripted in-process
RFB server (`FakeVncServer`) that checks the client's bytes. The suite has not been run for this fix; the
following describes test scope, not a passing result:

- Handshake: every version mapping, None / VNC / ARD (the fake server decrypts the credentials) / VeNCrypt
  X.509 with a self-signed certificate (trust prompt, then pinned reconnect; Plain credentials inside TLS;
  changed-certificate mismatch), Auto's fallback from anonymous-only VeNCrypt, server refusal reasons,
  unsupported and not-offered types, missing credentials.
- DES against the FIPS 46-3 known answer and, on Windows, against the platform cipher.
- Every encoding through the real reader and framebuffer, including ZRLE's five subencodings over one zlib
  stream, Tight's filters, reset bits and JPEG, and malformed-data rejection.
- Session: incremental requests, keysym vs. QEMU scan-code messages, pointer clamping, Latin-1 and extended
  clipboards in both directions, SetDesktopSize round trip, cursor / desktop name / LastRect, paused updates,
  clean close vs. reset vs. protocol error.
- Continuous updates and fences: both pseudo-encodings asked for unless turned off, enable for the whole desktop
  after the server's confirmation and no further requests, requests without it, fence answers (payload echoed,
  BlockBefore / BlockAfter kept, SyncNext / Request cleared, non-requests ignored), re-enable on resize, off when
  hidden and on again when shown, and falling back to requests when the server ends them.
- Audio: the -259 confirmation, set-format and enable bytes, PCM reaching the output in order, a device failure
  stopping audio only (and sending disable), malformed buffers, dropping instead of blocking.
- File transfer, against both LibVNC and UltraVNC behaviour: access, drives and listings, Unicode names and block size,
  compressed and raw downloads, compressed block uploads, refusals, cancel → abort, commands, timeouts, and a server
  that closes the connection when asked.
- Plugin: manifest vs. registration, all five languages complete with matching placeholders, settings parsing,
  exception translation, auto-reconnect rules, headless UI (status bar, input, view-only, remote keys, window
  matching, clipboard, disconnect card and reconnect, file panel, audio status), JPEG decoding through Skia, and unloading in a
  collectible load context.

**Observed with the original plugin:** on Ubuntu 24 with TigerVNC 1.13.1, sending message type 7 caused an
`unknown message type 7` error and disconnected the desktop. This is evidence of the original compatibility
problem, not validation of the fix. **The fix has not been built, tested or verified with a real client/server**:
local command execution was unavailable during this update.

Pending regression checks include SupportedMessages advertisement and both 32-byte bitmaps; no type-7 request
when capabilities are absent after the 2-second wait or explicitly negative; the panel error with the desktop
still connected; and the legacy option allowing only absent advertisements, never explicit negatives.
Repeat the TigerVNC scenario with the default settings and with file transfer enabled but the legacy option off.
Before relying on it, also connect to TigerVNC (`Xvnc` with `-SecurityTypes VncAuth,X509Vnc`), x11vnc, a
QEMU/libvirt console and macOS Screen Sharing, and check login, rendering with each preferred encoding,
Match window resizing, typing on a non-US layout and text clipboard both ways. Audio needs a QEMU guest with
an audio backend; file transfer needs UltraVNC Server (Windows) and `x11vnc -ultrafilexfer`: browse, upload,
download, replace, delete, rename, non-ASCII names and cancel. Verify the compatibility option separately on a
confirmed compatible older UltraVNC server that does not advertise capabilities.
