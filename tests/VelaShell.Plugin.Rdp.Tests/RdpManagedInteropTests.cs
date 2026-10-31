using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Plugin.Rdp.Managed;
using VelaShell.Plugin.Rdp.Managed.Mcs;
using VelaShell.Plugin.Rdp.Managed.Security;
using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Live interoperability checks against a real RDP server. Skipped unless
/// VELA_RDP_TEST_TARGET is set (host:port). Credentials come from
/// VELA_RDP_TEST_USER / VELA_RDP_TEST_PASS. Used to drive the managed stack
/// against xrdp during development; never runs in CI.
/// </summary>
[TestClass]
public sealed class RdpManagedInteropTests
{
    internal static (string Host, int Port)? Target()
    {
        string? raw = Environment.GetEnvironmentVariable("VELA_RDP_TEST_TARGET");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] parts = raw.Split(':');
        return (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 3389);
    }

    internal static string User => Environment.GetEnvironmentVariable("VELA_RDP_TEST_USER") ?? "veladev";
    internal static string Pass => Environment.GetEnvironmentVariable("VELA_RDP_TEST_PASS") ?? "";

    [TestMethod]
    public async Task NegotiatesTlsAndSurfacesLeafCertificate()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(target.Host, target.Port);
        var transport = new RdpTransport(tcp.GetStream());
        await transport.SendTpktAsync(RdpTransport.BuildConnectionRequest(User, SecurityProtocol.Ssl | SecurityProtocol.Hybrid), default);
        (SecurityProtocol protocol, _) = await transport.ReadConnectionConfirmAsync(default);
        Console.WriteLine($"Negotiated protocol: {protocol}");
        Assert.IsTrue(protocol.HasFlag(SecurityProtocol.Ssl) || protocol.HasFlag(SecurityProtocol.Hybrid),
            $"Server selected {protocol}");

        var rejected = await Assert.ThrowsExactlyAsync<RdpCertificateRejectedException>(() =>
            RdpTls.AuthenticateAsync(tcp.GetStream(), target.Host, pin: null, default));
        using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            System.Text.Encoding.UTF8.GetString(rejected.Pem));
        string sha = Convert.ToHexString(SHA256.HashData(leaf.RawData));
        Console.WriteLine($"LEAF-SHA256={sha}");
        Assert.AreEqual(64, sha.Length);
    }

    [TestMethod]
    public async Task AuthenticatesWithPinnedCertificate()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        // First pass: learn the leaf SHA-256.
        using var probe = new TcpClient();
        await probe.ConnectAsync(target.Host, target.Port);
        var t1 = new RdpTransport(probe.GetStream());
        await t1.SendTpktAsync(RdpTransport.BuildConnectionRequest(User, SecurityProtocol.Ssl), default);
        await t1.ReadConnectionConfirmAsync(default);
        byte[] pin = [];
        try { await RdpTls.AuthenticateAsync(probe.GetStream(), target.Host, null, default); }
        catch (RdpCertificateRejectedException ex)
        {
            using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(ex.Pem));
            pin = SHA256.HashData(leaf.RawData);
        }
        Assert.AreEqual(32, pin.Length, "Expected the probe to reject and yield a pin.");

        // Second pass: pin must authenticate.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(target.Host, target.Port);
        var t2 = new RdpTransport(tcp.GetStream());
        await t2.SendTpktAsync(RdpTransport.BuildConnectionRequest(User, SecurityProtocol.Ssl), default);
        await t2.ReadConnectionConfirmAsync(default);
        SslStream ssl = await RdpTls.AuthenticateAsync(tcp.GetStream(), target.Host, pin, default);
        Console.WriteLine($"TLS OK: {ssl.SslProtocol}, cipher {ssl.NegotiatedCipherSuite}");
        Assert.IsTrue(ssl.IsAuthenticated);
        await ssl.DisposeAsync();
    }

    internal static async Task<byte[]> LearnPinAsync((string Host, int Port) target)
    {
        using var probe = new TcpClient();
        await probe.ConnectAsync(target.Host, target.Port);
        var t = new RdpTransport(probe.GetStream());
        await t.SendTpktAsync(RdpTransport.BuildConnectionRequest(User, SecurityProtocol.Ssl), default);
        await t.ReadConnectionConfirmAsync(default);
        try { await RdpTls.AuthenticateAsync(probe.GetStream(), target.Host, null, default); }
        catch (RdpCertificateRejectedException ex)
        {
            using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(ex.Pem));
            return SHA256.HashData(leaf.RawData);
        }
        return [];
    }

    [TestMethod]
    public async Task ConnectsAndReceivesDesktopFrame()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        byte[] pin = await LearnPinAsync(target);
        Assert.AreEqual(32, pin.Length);

        var options = new RdpConnectOptions(target.Host, target.Port, User, Pass, Domain: "",
            Width: 1024, Height: 768, Bpp: 16, CertificatePin: pin, CertificateHost: target.Host,
            Channels: Array.Empty<ChannelDef>());

        await using var conn = new RdpConnection(options);
        int frames = 0;
        conn.FrameUpdated += () => Interlocked.Increment(ref frames);
        uint errorInfo = 0;
        conn.ErrorInfoReceived += code => errorInfo = code;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await conn.ConnectAsync(cts.Token);
        Console.WriteLine("Connection active; pumping...");

        var pump = Task.Run(async () =>
        {
            try { while (!cts.IsCancellationRequested && await conn.PumpAsync(cts.Token)) { } }
            catch (OperationCanceledException) { }
        });

        // Pump for a fixed window so the desktop paints substantially, not just the first partial update.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
            await Task.Delay(250, CancellationToken.None);

        Console.WriteLine($"Frames: {frames}, errorInfo: 0x{errorInfo:X8}, pumpFaulted={pump.IsFaulted}");
        if (pump.IsFaulted) Console.WriteLine("Pump exception: " + pump.Exception?.GetBaseException());
        Assert.IsTrue(frames > 0, "No desktop frame was received.");

        byte[] bgra = conn.Framebuffer.Bgra;
        int distinct = CountDistinct(bgra);
        Console.WriteLine($"Framebuffer {conn.Framebuffer.Width}x{conn.Framebuffer.Height}, distinct colours ~{distinct}");
        WriteBmp(bgra, conn.Framebuffer.Width, conn.Framebuffer.Height, "managed-rdp-frame.bmp");
        Assert.IsTrue(distinct > 1, "Framebuffer appears blank.");

        // --- Input check: click the IceWM start button (bottom-left) and expect the menu to paint. ---
        // Press Escape first so the desktop starts from a known state (no menu open), robust to a
        // persistent server session left open by earlier tests.
        await conn.SendScancodeAsync(0x01, true, CancellationToken.None);
        await conn.SendScancodeAsync(0x01, false, CancellationToken.None);
        await Task.Delay(1200, CancellationToken.None);
        long before = RegionHash(conn.Framebuffer, 0, conn.Framebuffer.Height - 320, 260, 320);
        await conn.SendMouseAsync(0x0800, 12, (uint)(conn.Framebuffer.Height - 8), CancellationToken.None); // move
        await conn.SendMouseAsync(0x9000, 12, (uint)(conn.Framebuffer.Height - 8), CancellationToken.None); // button1 down
        await conn.SendMouseAsync(0x1000, 12, (uint)(conn.Framebuffer.Height - 8), CancellationToken.None); // button1 up
        await Task.Delay(2500, CancellationToken.None);
        long after = RegionHash(conn.Framebuffer, 0, conn.Framebuffer.Height - 320, 260, 320);
        WriteBmp(conn.Framebuffer.Bgra, conn.Framebuffer.Width, conn.Framebuffer.Height, "managed-rdp-menu.bmp");
        Console.WriteLine($"Region hash before={before:X} after={after:X} changed={before != after}");
        Assert.AreNotEqual(before, after, "Clicking the start button produced no visible change (input not working).");

        cts.Cancel();
        await pump;
    }

    [TestMethod]
    public async Task ManagedSessionRendersThroughInterface()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        byte[] pin = await LearnPinAsync(target);
        Assert.AreEqual(32, pin.Length);

        var request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)),
            ("width", "1024"), ("height", "768")) with
        { Host = target.Host, Port = target.Port, Username = User, Password = Pass };
        var loc = new Loc("en");
        RdpSettings settings = RdpSettings.From(request, loc);

        IRdpSession session = await RdpManagedSession.ConnectAsync(request, settings, loc, default);
        try
        {
            int frames = 0;
            session.FrameAvailable += () => Interlocked.Increment(ref frames);
            RdpFrame? latest = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                RdpFrame? f = session.TakeFrame();
                if (f is not null) latest = f;
                await Task.Delay(150, CancellationToken.None);
            }
            Console.WriteLine($"Interface frames={frames}, connected={session.IsConnected}, last={latest?.Width}x{latest?.Height}");
            Assert.IsTrue(session.IsConnected, "Session dropped.");
            Assert.IsNotNull(latest, "No frame delivered through IRdpSession.TakeFrame.");
            Assert.IsTrue(CountDistinct(latest.Pixels) > 1, "Delivered frame is blank.");

            // Exercise input plumbing through the interface.
            await session.MouseAsync(0x0800, 12, 760, default);
            await session.KeyAsync(0x1C, true, default);   // Enter down
            await session.KeyAsync(0x1C, false, default);  // Enter up
        }
        finally { await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ClipboardChannelHandshakeCompletes()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        byte[] pin = await LearnPinAsync(target);
        Assert.AreEqual(32, pin.Length);

        var request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)),
            ("clipboard", "true"), ("width", "1024"), ("height", "768")) with
        { Host = target.Host, Port = target.Port, Username = User, Password = Pass };
        var loc = new Loc("en");
        RdpSettings settings = RdpSettings.From(request, loc);
        Assert.IsTrue(settings.Clipboard);

        IRdpSession session = await RdpManagedSession.ConnectAsync(request, settings, loc, default);
        try
        {
            var kinds = new System.Collections.Concurrent.ConcurrentBag<uint>();
            session.ClipboardEvent += ev => kinds.Add(ev.Kind);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline && !kinds.Any(k => k == 2u))
            {
                session.TakeFrame();
                await Task.Delay(150, CancellationToken.None);
            }
            Console.WriteLine($"Clipboard event kinds seen: {string.Join(",", kinds.Distinct().Order())}");
            Assert.IsTrue(kinds.Any(k => k == 2u), "cliprdr handshake (CB_MONITOR_READY → ready) did not arrive.");
            Assert.IsTrue(session.IsConnected, "Session dropped during clipboard handshake.");
        }
        finally { await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ClipboardReceivesRemoteText()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        string? display = Environment.GetEnvironmentVariable("VELA_RDP_TEST_XDISPLAY");
        if (string.IsNullOrEmpty(display)) { Assert.Inconclusive("VELA_RDP_TEST_XDISPLAY (e.g. :10) not set."); return; }

        string tag = "VelaClip-" + Guid.NewGuid().ToString("N")[..8];
        SetRemoteClipboard(display, tag);

        byte[] pin = await LearnPinAsync(target);
        var request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)),
            ("clipboard", "true")) with { Host = target.Host, Port = target.Port, Username = User, Password = Pass };
        var loc = new Loc("en");
        IRdpSession session = await RdpManagedSession.ConnectAsync(request, RdpSettings.From(request, loc), loc, default);
        try
        {
            var gate = new SemaphoreSlim(0);
            string? formatList = null;
            byte[]? textData = null;
            session.ClipboardEvent += ev =>
            {
                if (ev.Kind == 3) { formatList = System.Text.Encoding.UTF8.GetString(ev.Data); gate.Release(); }
                else if (ev.Kind == 5 && ev.A != 0) { textData = ev.Data; gate.Release(); }
            };

            var pump = Task.Run(async () => { while (session.IsConnected) { session.TakeFrame(); await Task.Delay(100); } });
            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "No clipboard format list from the server.");
            Console.WriteLine($"Format list:\n{formatList}");
            Assert.IsNotNull(formatList);
            Assert.IsTrue(formatList.Split('\n').Any(l => l.StartsWith("13\t")), "CF_UNICODETEXT (13) not offered.");

            await session.RequestFormatAsync(13, default);
            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "No format data response.");
            Assert.IsNotNull(textData);
            string received = System.Text.Encoding.Unicode.GetString(textData).TrimEnd('\0');
            Console.WriteLine($"Received clipboard text: '{received}' (expected '{tag}')");
            Assert.AreEqual(tag, received);
        }
        finally { await session.DisposeAsync(); }
    }

    private static void SetRemoteClipboard(string display, string text)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add("Debian"); psi.ArgumentList.Add("-u"); psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("bash"); psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add($"su - {User} -c 'export DISPLAY={display}; export XAUTHORITY=/home/{User}/.Xauthority; printf %s \"{text}\" | xclip -selection clipboard; sleep 0.4'");
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(8000);
    }

    [TestMethod]
    public async Task ClipboardReceivesRemoteImage()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        string? display = Environment.GetEnvironmentVariable("VELA_RDP_TEST_XDISPLAY");
        if (string.IsNullOrEmpty(display)) { Assert.Inconclusive("VELA_RDP_TEST_XDISPLAY not set."); return; }

        byte[] pin = await LearnPinAsync(target);
        var request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)), ("clipboard", "true"))
            with { Host = target.Host, Port = target.Port, Username = User, Password = Pass };
        var loc = new Loc("en");
        IRdpSession session = await RdpManagedSession.ConnectAsync(request, RdpSettings.From(request, loc), loc, default);
        var pump = Task.Run(async () => { while (session.IsConnected) { session.TakeFrame(); await Task.Delay(100); } });
        try
        {
            var ready = new SemaphoreSlim(0);
            var gate = new SemaphoreSlim(0);
            string? formats = null;
            byte[]? imageData = null;
            session.ClipboardEvent += ev =>
            {
                if (ev.Kind == 2) ready.Release();
                else if (ev.Kind == 3 && System.Text.Encoding.UTF8.GetString(ev.Data).Split('\n').Any(l => l.StartsWith("8\t") || l.StartsWith("17\t")))
                { formats = System.Text.Encoding.UTF8.GetString(ev.Data); gate.Release(); }
                else if (ev.Kind == 5 && ev.A != 0) { imageData = ev.Data; gate.Release(); }
            };
            await ready.WaitAsync(TimeSpan.FromSeconds(8));
            // Copy an image on the remote AFTER the handshake so the server notifies us of the change.
            // xrdp's chansrv maps the X11 image/bmp target to CF_DIB (it does not convert image/png).
            string bmpB64 = Convert.ToBase64String(MakeBmp(8, 8, 0x20, 0x80, 0xC0));
            RunInSession(display, $"echo '{bmpB64}' | base64 -d > /tmp/vimg.bmp; (xclip -selection clipboard -t image/bmp -i /tmp/vimg.bmp &) ; sleep 0.2");
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(12)))
            { Assert.Inconclusive("xrdp did not offer a DIB image format for an image/png clipboard (server-side limitation)."); return; }
            Console.WriteLine($"Image format list:\n{formats}");
            Assert.IsNotNull(formats);
            uint imageFormat = formats.Split('\n').Any(l => l.StartsWith("8\t")) ? 8u
                : formats.Split('\n').Any(l => l.StartsWith("17\t")) ? 17u : 0u;
            Assert.AreNotEqual(0u, imageFormat, "Server offered no DIB image format.");

            await session.RequestFormatAsync(imageFormat, default);
            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "No image format data response.");
            Assert.IsNotNull(imageData);
            RdpClipboardImage decoded = RdpClipboardCodec.DecodeDib(imageData);
            Console.WriteLine($"Decoded remote image: {decoded.Width}x{decoded.Height}");
            Assert.AreEqual(8, decoded.Width);
            Assert.AreEqual(8, decoded.Height);
        }
        finally { await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task DriveRedirectionServesLocalFiles()
    {
        if (Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        string? display = Environment.GetEnvironmentVariable("VELA_RDP_TEST_XDISPLAY");
        if (string.IsNullOrEmpty(display)) { Assert.Inconclusive("VELA_RDP_TEST_XDISPLAY not set."); return; }

        string shareDir = Path.Combine(Path.GetTempPath(), "vrdp-share-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shareDir);
        string tag = "DriveRoundTrip-" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(Path.Combine(shareDir, "hello.txt"), tag);

        byte[] pin = await LearnPinAsync(target);
        var request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)),
            ("drives", $"vshare={shareDir}")) with { Host = target.Host, Port = target.Port, Username = User, Password = Pass };
        var loc = new Loc("en");
        RdpSettings settings = RdpSettings.From(request, loc);
        Assert.AreEqual(1, settings.Drives.Count);

        IRdpSession session = await RdpManagedSession.ConnectAsync(request, settings, loc, default);
        var pump = Task.Run(async () => { while (session.IsConnected) { session.TakeFrame(); await Task.Delay(100); } });
        try
        {
            string listing = "", content = "";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, CancellationToken.None);
                listing = RunInSession(display, "ls ~/thinclient_drives 2>/dev/null");
                if (!string.IsNullOrWhiteSpace(listing))
                {
                    content = RunInSession(display, "cat ~/thinclient_drives/vshare/hello.txt 2>&1");
                    if (content.Contains(tag)) break;
                }
            }
            Console.WriteLine($"thinclient_drives listing: '{listing.Trim()}'");
            Console.WriteLine($"Remote read of hello.txt: '{content.Trim()}' (expected '{tag}')");
            Assert.IsTrue(content.Contains(tag), "The remote session could not read the redirected file.");

            // Write-back: remote creates a file, the managed client must materialize it locally.
            string writeTag = "RemoteWrote-" + Guid.NewGuid().ToString("N")[..6];
            RunInSession(display, $"cd ~/thinclient_drives/vshare && printf %s '{writeTag}' > from-remote.txt 2>&1");
            await Task.Delay(1500, CancellationToken.None);
            string localWritten = File.Exists(Path.Combine(shareDir, "from-remote.txt"))
                ? await File.ReadAllTextAsync(Path.Combine(shareDir, "from-remote.txt")) : "";
            Console.WriteLine($"Local file written by remote: '{localWritten}' (expected '{writeTag}')");
            Assert.AreEqual(writeTag, localWritten, "The remote write did not reach the local shared folder.");
        }
        finally
        {
            await session.DisposeAsync();
            try { Directory.Delete(shareDir, true); } catch { }
        }
    }

    private static byte[] MakeBmp(int width, int height, byte r, byte g, byte b)
    {
        int rowSize = (width * 3 + 3) & ~3;
        int pixels = rowSize * height;
        byte[] bmp = new byte[54 + pixels];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), 54 + pixels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixels);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = 54 + y * rowSize + x * 3;
                bmp[p] = b; bmp[p + 1] = g; bmp[p + 2] = r;
            }
        return bmp;
    }

    private static string RunInSession(string display, string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add("Debian"); psi.ArgumentList.Add("-u"); psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("bash"); psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add($"su - {User} -c 'export DISPLAY={display}; export XAUTHORITY=/home/{User}/.Xauthority; {command}'");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(8000);
        return output;
    }

    private static int CountDistinct(byte[] bgra)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i + 4 <= bgra.Length && seen.Count <= 64; i += 4)
            seen.Add(bgra[i] | (bgra[i + 1] << 8) | (bgra[i + 2] << 16));
        return seen.Count;
    }

    private static long RegionHash(VelaShell.Plugin.Rdp.Managed.Graphics.Framebuffer fb, int x0, int y0, int w, int h)
    {
        byte[] bgra = fb.Bgra;
        long hash = 1469598103934665603L;
        for (int y = y0; y < y0 + h && y < fb.Height; y++)
            for (int x = x0; x < x0 + w && x < fb.Width; x++)
            {
                int p = (y * fb.Width + x) * 4;
                hash = (hash ^ bgra[p] ^ ((long)bgra[p + 1] << 8) ^ ((long)bgra[p + 2] << 16)) * 1099511628211L;
            }
        return hash;
    }

    private static void WriteBmp(byte[] bgra, int width, int height, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("VELA_RDP_TEST_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        int rowSize = (width * 3 + 3) & ~3;
        int imageSize = rowSize * height;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0x4D42);
        w.Write(54 + imageSize);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(width);
        w.Write(height);          // positive → bottom-up BMP
        w.Write((ushort)1);
        w.Write((ushort)24);
        w.Write(0);
        w.Write(imageSize);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        byte[] row = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--)
        {
            int sp = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = bgra[sp + x * 4];
                row[x * 3 + 1] = bgra[sp + x * 4 + 1];
                row[x * 3 + 2] = bgra[sp + x * 4 + 2];
            }
            w.Write(row);
        }
        Console.WriteLine($"Wrote frame BMP: {path}");
    }
}
