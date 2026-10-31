# VelaShell.Rdp

A **pure-managed .NET RDP (Remote Desktop Protocol) client** — no native dependencies, no
FreeRDP, no P/Invoke beyond the BCL. It is the engine behind the VelaShell RDP plugin, extracted
into a standalone, UI-agnostic library so it can be reused on its own.

Released under the **MIT License** (see [LICENSE](LICENSE)) alongside the
[velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) repository.

## What it does

- **Connection sequence** — X.224 negotiation, MCS/GCC, client info, licensing, capability
  exchange and finalization (MS-RDPBCGR).
- **Security** — TLS via `SslStream` with OS trust-chain validation *or* an explicit SHA-256
  leaf-certificate pin; CredSSP/NLA via `NegotiateAuthentication` when a server requires it.
- **Graphics** — interleaved RLE bitmap decoding and MPPC bulk decompression into a BGRA
  framebuffer, with fast-path fragment reassembly.
- **Input** — fast-path keyboard (scancode), Unicode and mouse.
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
    Clipboard = true,
    Drives = [new DriveMapping("work", @"C:\Work\Share")],
}, cancellationToken);

session.FrameAvailable += () =>
{
    if (session.TakeFrame() is { } frame) { /* frame.Pixels is BGRA32, frame.Width × frame.Height */ }
};

await session.MouseAsync(0x0800, x, y);          // move
await session.KeyAsync(scancode, down: true);    // key
await session.RequestResizeAsync(1280, 1024);    // match the remote desktop to your viewport
```

Without a `CertificatePin`, an untrusted server certificate raises
`RdpCertificateRejectedException`, whose `Pem` lets you show the leaf's SHA-256 and offer to pin it.

## Design notes

- **No UI, no plugin SDK.** The library produces frames and accepts input through `IRdpSession`;
  the host supplies the display surface. The VelaShell plugin wraps it with an Avalonia view.
- **Targets `net11.0`.** BCL-only; nothing is pulled from NuGet.
- The library DLL ships inside the RDP plugin's signed `.vpx`; it is not published as a separate
  NuGet package.
