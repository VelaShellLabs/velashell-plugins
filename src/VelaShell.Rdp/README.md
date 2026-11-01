# VelaShell.Rdp

A **pure-managed .NET RDP (Remote Desktop Protocol) client** — no native dependencies, no
FreeRDP, no P/Invoke beyond the BCL. It is the engine behind the VelaShell RDP plugin, extracted
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

It does **not** negotiate audio, multiple monitors, or the graphics pipeline (RemoteFX/GFX).

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

## Design notes

- **No UI, no plugin SDK.** The library produces frames and accepts input through `IRdpSession`;
  the host supplies the display surface. The VelaShell plugin wraps it with an Avalonia view.
- **Bounded against a hostile server.** Bitmap rectangles, drawing-order work, the bitmap cache (256 MiB),
  virtual-channel reassembly, CredSSP messages and the desktop size are all capped; drive redirection confines
  paths component by component and refuses links below the mapped folder. Malformed input raises
  `RdpProtocolException` and ends the session instead of allocating without bound.
- **Targets `net11.0`.** BCL-only; nothing is pulled from NuGet.
- The library DLL ships inside the RDP plugin's signed `.vpx`; it is not published as a separate
  NuGet package.
