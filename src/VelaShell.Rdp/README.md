# VelaShell.Rdp

A **managed .NET RDP (Remote Desktop Protocol) client** — no FreeRDP or bundled native binaries.
Optional audio playback uses the system's WinMM, AudioToolbox or ALSA libraries. It is the engine behind the VelaShell RDP plugin, extracted
into a standalone, UI-agnostic library so it can be reused on its own.

Released under the **MIT License** (see [LICENSE](LICENSE)) alongside the
[velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) repository.

## What it does

- **Connection sequence** — X.224 negotiation, MCS/GCC, client info, licensing, capability
  exchange and finalization (MS-RDPBCGR).
- **Security** — TLS 1.2/1.3 via `SslStream` with OS trust-chain validation *or* an explicit SHA-256
  leaf-certificate pin; CredSSP/NLA (v5+ only) via the native SSPI Negotiate package on Windows and
  `NegotiateAuthentication` elsewhere. `RdpNlaUnavailableException` reports a server that stopped offering NLA, so a
  caller can refuse that downgrade for hosts it knows use NLA.
- **Graphics** — interleaved RLE (8–24 bpp) and RDP 6.0 planar (32 bpp) bitmap decoding, MPPC bulk
  decompression, GDI drawing orders (DstBlt, PatBlt, ScrBlt, OpaqueRect, MultiOpaqueRect, MemBlt) with a revision 2
  bitmap cache, and pointer shapes (`IRdpPointerSource`), into a BGRA framebuffer that tracks its changed region;
  fast-path fragment reassembly.
- **Input** — fast-path keyboard (scancode), Unicode and mouse; Unicode text is batched 7 characters per PDU.
- **Channels** — clipboard (cliprdr), folder redirection (rdpdr), and dynamic resolution over the
  display-control channel (drdynvc + MS-RDPEDISP); other dynamic channels are declined so the
  server keeps to the classic bitmap path.

- **Audio output** — opt-in `AudioPlayback` over `rdpsnd` (MS-RDPEA), including formats, training,
  WaveInfo/Wave, Wave2 and playback confirmations. PCM 8/16-bit mono/stereo at 8–48 kHz plays on
  the system default output: WinMM on Windows, AudioQueue on macOS, ALSA `default` on Linux
  (`libasound.so.2` plus a working ALSA/PipeWire/PulseAudio configuration). Audio failures are replayed
  through `IRdpAudioSource.AudioPlaybackFailed` and disable audio without disconnecting the desktop.

It does **not** negotiate microphone capture, multiple monitors, or the graphics pipeline (RemoteFX/GFX).

## Usage

```csharp
using VelaShell.Rdp;

await using var session = await RdpManagedSession.ConnectAsync(new RdpClientOptions
{
    Host = "desktop.example",
    Username = "alice",
    Password = "…",
    Width = 1920,
    Height = 1080,
    ColorDepth = 32,            // 15, 16, 24 or 32
    DesktopScaleFactor = 150,   // the local display scale, so remote text is not tiny on a high-DPI screen
    Clipboard = true,
    AudioPlayback = true,       // default output device; optional, disabled by default
    Drives = [new DriveMapping("work", @"C:\Work\Share")],
}, cancellationToken);

session.FrameAvailable += () =>
{
    // Copies only what changed into your own BGRA32 surface (e.g. a locked bitmap); TakeFrame() returns a full copy.
    RdpFrameCopy copy = session.CopyFrame(surface, stride, width, height);
    if (copy.SizeChanged) { /* recreate the surface at copy.Width × copy.Height, then CopyFrame(..., full: true) */ }
};

await session.MouseAsync(0x0800, x, y);          // move
await session.KeyAsync(scancode, down: true);    // key
await session.RequestResizeAsync(1280, 1024, 150);   // match the remote desktop (and DPI) to your viewport
await session.SetDisplayUpdatesAsync(false);       // view hidden: the server pauses updates (Suppress Output)

session.Disconnected += cause =>
{
    // RdpConnectionLostException: the network dropped — connecting again normally resumes the remote session.
    // RdpDisconnectException: the server ended it and said why (ErrorInfo). Anything else: protocol/channel failure.
};
```

`KeepAliveInterval` (default 30 s, `TimeSpan.Zero` to disable) keeps a quiet session alive: TCP keep-alive
probes after that much idle time (3 unanswered probes 10 s apart end the session with
`RdpConnectionLostException`), and while display updates are paused the paused state is re-sent at that
interval, because a paused session is otherwise silent and NAT gateways and port forwarders expire idle
connections. Reconnecting is left to the caller.

Without a `CertificatePin`, an untrusted server certificate raises
`RdpCertificateRejectedException`, whose `Pem` lets you show the leaf's SHA-256 and offer to pin it.

## UDP and network adaptation

`RdpClientOptions.Transport` defaults to `RdpTransportMode.Auto`; `Tcp` disables UDP negotiation.
The main connection, classic bitmap/GDI output, input and static channels remain on TCP.
When the server supports extended negotiation and Soft-Sync, dynamic channels can move to
MS-RDPEUDP v1 reliable UDP (TLS) or lossy UDP (DTLS 1.2), using MS-RDPEMT.
Both side tunnels must present the main connection's exact leaf certificate. Creation cookies
are sent only inside encryption. UDP v2 and UDP Preferred are not advertised.

Setup failures leave TCP running. After a tunnel is accepted, its failure ends the connection
so callers can reconnect without replaying channel operations on another transport.
UDP-L uses FEC but currently requires reconnection on an unrecovered application-data gap,
preventing DVC fragments from different messages being spliced together. Outbound UDP-L DVC
messages are limited to 600 bytes and are never fragmented. This is not continuous playback
through arbitrary residual loss. `TcpTunnel = true` disables direct UDP for TCP-only proxies;
the plugin sets this automatically for SSH jump hosts.

`AdaptiveNetwork` defaults to `true`. Server-provided RTT/bandwidth measurements are exposed
through `IRdpNetworkSource.NetworkStatus` and `NetworkChanged`. The plugin displays transport
and quality and coalesces pointer motion at 25/50 ms on constrained/poor links; keys and clicks
flush pending motion in order. Improvement requires three healthier samples. Automatic network
characteristics allow the server to override manual experience flags; disable adaptation to retain
the manual profile. Unknown measurements are not fabricated.

Offline tests cover loss/reordering/FEC, Soft-Sync routing, early message-channel handling,
and real loopback UDP sockets with TLS/DTLS, EMT messages split across records or combined
in one record, certificate rejection and cancellation. Real Windows
server interoperability and WAN performance remain unverified.

## Design notes

- **No UI, no plugin SDK.** The library produces frames and accepts input through `IRdpSession`;
  the host supplies the display surface. The VelaShell plugin wraps it with an Avalonia view.
- **Bounded against a hostile server.** Bitmap rectangles, drawing-order work, the bitmap cache (256 MiB),
  virtual-channel reassembly, CredSSP messages and the desktop size are all capped; drive redirection confines
  paths component by component and refuses links below the mapped folder. Malformed input raises
  `RdpProtocolException` and ends the session instead of allocating without bound. Audio has a separate
  128-message / 1 MiB queue limit; audio errors stop only playback, leaving desktop input and display active.
- **Targets `net11.0`.** UDP-L uses the managed `BouncyCastle.Cryptography` 2.7.0 package for DTLS 1.2; UDP-R uses `SslStream` TLS. Transport code does not require a native UDP/DTLS library.
- The library DLL ships inside the RDP plugin's signed `.vpx`; it is not published as a separate
  NuGet package.
