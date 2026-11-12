using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;
using static VelaShell.Plugin.Rdp.Tests.RdpHeadlessTests;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RemoteAppTests
{
    private static RemoteAppOptions App => new() { Program = "||editor", Arguments = "document.txt", WorkingDirectory = "C:\\Work" };

    [TestMethod]
    public async Task HandshakeLaunchAndWindowControlUseRealRailPackets()
    {
        var sent = new List<byte[]>();
        var rail = new RailChannel(App, 1024, 768, (pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, () => (1280, 800));
        await rail.ProcessAsync(RailChannel.Packet(0x13, w => { w.U32LE(22621); w.U32LE(0); }), default);
        Assert.AreEqual((ushort)5, BitConverter.ToUInt16(sent[0]));
        Assert.AreEqual((ushort)0xb, BitConverter.ToUInt16(sent[1]));
        Assert.AreEqual(0x600u, BitConverter.ToUInt32(sent[1], 4), "only cloaking and icon suppression are advertised");
        byte[] highContrast = sent.Single(p => BitConverter.ToUInt16(p) == 3 && BitConverter.ToUInt32(p, 4) == 0x43);
        Assert.AreEqual((ushort)18, BitConverter.ToUInt16(highContrast, 2), "high-contrast parameter uses the standard empty colour scheme layout");
        Assert.AreEqual(0x7eu, BitConverter.ToUInt32(highContrast, 8));
        Assert.AreEqual(2u, BitConverter.ToUInt32(highContrast, 12));
        Assert.AreEqual((ushort)0, BitConverter.ToUInt16(highContrast, 16));
        byte[] workArea = sent.Single(p => BitConverter.ToUInt16(p) == 3 && BitConverter.ToUInt32(p, 4) == 0x2f);
        Assert.AreEqual((ushort)1280, BitConverter.ToUInt16(workArea, 12), "server-negotiated dimensions replace requested dimensions");
        Assert.AreEqual((ushort)(800 - RemoteAppOptions.WorkspaceStripHeight), BitConverter.ToUInt16(workArea, 14),
            "the work area uses the negotiated desktop and ends above the local application tabs");
        Assert.DoesNotContain(p => BitConverter.ToUInt16(p) == 3 && BitConverter.ToUInt32(p, 4) is 0xf000 or 0xf001, sent, "startup does not send optional taskbar/display-change orders");
        byte[] execute = sent.Single(p => BitConverter.ToUInt16(p) == 1);
        var r = new ByteReader(execute);
        Assert.AreEqual((ushort)1, r.U16LE());
        Assert.AreEqual(execute.Length, r.U16LE());
        Assert.AreEqual((ushort)0, r.U16LE());
        int appLength = r.U16LE(), directoryLength = r.U16LE(), argsLength = r.U16LE();
        Assert.AreEqual(App.Program, Encoding.Unicode.GetString(r.Take(appLength)));
        Assert.AreEqual(App.WorkingDirectory, Encoding.Unicode.GetString(r.Take(directoryLength)));
        Assert.AreEqual(App.Arguments, Encoding.Unicode.GetString(r.Take(argsLength)));
        Assert.AreEqual(0, r.Remaining);
        Assert.IsFalse(rail.Launched.IsCompleted);
        await rail.ProcessAsync(ExecuteResult(0), default);
        await rail.Launched;
        sent.Clear();
        await rail.ActivateAsync(42, true, default);
        await rail.SystemCommandAsync(42, 0xf060, default);
        await rail.MoveAsync(42, 10, 20, 400, 300, default);
        Assert.AreSequenceEqual(new byte[] { 2, 0, 9, 0, 42, 0, 0, 0, 1 }, sent[0]);
        Assert.AreSequenceEqual(new byte[] { 4, 0, 10, 0, 42, 0, 0, 0, 0x60, 0xf0 }, sent[1]);
        Assert.AreSequenceEqual(new byte[] { 8, 0, 16, 0, 42, 0, 0, 0, 10, 0, 20, 0, 154, 1, 64, 1 }, sent[2]);
    }

    [TestMethod]
    public async Task LaunchRejectionMalformedMessagesAndEnhancedModeFailExplicitly()
    {
        var rail = new RailChannel(App, 1024, 768, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<RdpProtocolException>(() => rail.ProcessAsync(ExecuteResult(0), default));
        await rail.ProcessAsync(RailChannel.Packet(5, w => w.U32LE(7601)), default);
        // TS_RAIL_ORDER_CLOAK reaches the window decoder; a wrong length is malformed.
        var cloaks = new List<(uint, bool)>();
        var cloaking = new RailChannel(App, 1024, 768, (_, _) => Task.CompletedTask, cloak: (id, cloaked) => cloaks.Add((id, cloaked)));
        await cloaking.ProcessAsync(RailChannel.Packet(5, w => w.U32LE(7601)), default);
        await cloaking.ProcessAsync(RailChannel.Packet(0x15, w => { w.U32LE(42); w.U8(1); }), default);
        Assert.AreSequenceEqual([(42u, true)], cloaks);
        await Assert.ThrowsAsync<RdpProtocolException>(() => cloaking.ProcessAsync(RailChannel.Packet(0x15, w => w.U32LE(42)), default));
        // Orders and system parameters this client does not know are ignored, not fatal (a newer server may send them).
        await rail.ProcessAsync(RailChannel.Packet(0x16, w => w.U32LE(1)), default);
        await rail.ProcessAsync(RailChannel.Packet(3, w => { w.U32LE(0x1234); w.U8(1); }), default);
        RdpProtocolException rejection = await Assert.ThrowsAsync<RdpProtocolException>(() =>
            rail.ProcessAsync(ExecuteResult(3, 0x15), default));
        Assert.Contains("not in the server's RemoteApp allowlist", rejection.Message);
        Assert.Contains("device is not ready", rejection.Message);
        await Assert.ThrowsAsync<RdpProtocolException>(() => rail.ProcessAsync([1, 0, 3, 0], default));
        await Assert.ThrowsAsync<RdpProtocolException>(() => rail.ProcessAsync([1, 0, 8, 0], default));
        var handshakeEx = new RailChannel(App, 1024, 768, (_, _) => Task.CompletedTask);
        await handshakeEx.ProcessAsync(RailChannel.Packet(0x13, w => { w.U32LE(22621); w.U32LE(1); }), default);
        Assert.IsFalse(handshakeEx.Launched.IsCompleted, "HIDEF is a valid classic RAIL handshake extension");
        Assert.Throws<ArgumentOutOfRangeException>(() => rail.MoveAsync(1, -1, 0, 10, 10, default));
        Assert.Throws<ArgumentException>(() => RailChannel.BuildExecute(App with { Program = "cmd.exe" }));
        Assert.Throws<ArgumentException>(() => RailChannel.BuildExecute(App with { Arguments = "bad\0argument" }));
    }

    [TestMethod]
    public void RemoteAppNegotiationIsOptInAndCannotEnableDesktopGfx()
    {
        byte[] plain = ClientInfo.Build("", "user", "", false, 0, false);
        byte[] rail = ClientInfo.Build("", "user", "", false, 0, false, remoteApp: true);
        Assert.AreEqual(0u, BitConverter.ToUInt32(plain, 8) & 0x8000);
        Assert.AreEqual(0x8000u, BitConverter.ToUInt32(rail, 8) & 0x8000);
        Assert.AreEqual(0u, BitConverter.ToUInt32(rail, 8) & 0x02000000);
        var options = new RdpClientOptions { Host = "h", Username = "u", Password = "", RemoteApp = App, DynamicResolution = true, Clipboard = false };
        ChannelDef railChannel = RdpManagedSession.BuildChannels(options).Single();
        Assert.AreEqual("rail", railChannel.Name);
        Assert.AreEqual(0xC0A00000u, railChannel.Options);
        Assert.AreSequenceEqual(["rail"], [.. RdpManagedSession.BuildChannels(options).Select(c => c.Name)]);
        Assert.Throws<ArgumentException>(() => new RdpConnection(ConnectionOptions() with { GraphicsPipeline = true }));
        bool factoryCalled = false;
        Assert.IsNull(RdpManagedSession.PrepareGraphics(options with { EnableGraphicsPipeline = true }, (_, _) => { factoryCalled = true; throw new InvalidOperationException(); }));
        Assert.IsFalse(factoryCalled);
        Dictionary<int, byte[]> railCaps = CapabilitySets(Capabilities.BuildConfirmActive(1, 1001, 1024, 768, 32, true, remoteApp: true));
        Assert.AreEqual(0x81u, BitConverter.ToUInt32(railCaps[23]));
        Assert.AreEqual(2u, BitConverter.ToUInt32(railCaps[24]));
        Assert.AreEqual(0, railCaps[24][4]);
        Assert.IsFalse(CapabilitySets(Capabilities.BuildConfirmActive(1, 1001, 1024, 768, 32, true)).ContainsKey(23));
    }

    [TestMethod]
    public void ActualAlternateSecondaryOrdersCreateUpdateDeleteAndResynchronizeWindows()
    {
        var connection = new RdpConnection(ConnectionOptions());
        connection.StartSurface(32);
        int changes = 0;
        connection.RemoteWindows!.Changed += () => changes++;
        SendOrder(connection, Window(42, 0x10000000 | 4 | 8 | 0x10 | 0x800 | 0x400 | 0x1000 | 0x200, w =>
        {
            w.U32LE(0x00c40000); w.U32LE(0); w.U8(5); String(w, "Editor <plain text>");
            w.U32LE(10); w.U32LE(20); w.U32LE(400); w.U32LE(300);
            w.U32LE(10); w.U32LE(20); Rects(w, 0, 0, 400, 300);
        }));
        RemoteAppWindow app = connection.RemoteWindows.Snapshot().Single();
        Assert.AreEqual("Editor <plain text>", app.Title);
        Assert.AreEqual(400, app.Width);
        Assert.AreEqual(new Rect(10, 20, 400, 300), RdpDesktopControl.RemoteAppRegions(app, 1024, 768).Single());
        SendOrder(connection, Window(42, 4 | 0x10, w => { w.U8(2); String(w, "Minimized"); }));
        Assert.IsFalse(connection.RemoteWindows.Snapshot().Single().Visible);
        Assert.IsEmpty(RdpDesktopControl.RemoteAppRegions(connection.RemoteWindows.Snapshot().Single(), 1024, 768).ToArray());
        // 0x00080000 is EnforceServerZOrder (MS-RDPERP 2.2.1.3.1.2.1), not visibility.
        SendOrder(connection, Window(42, 0x80000, w => w.U8(1)));
        Assert.IsTrue(connection.RemoteWindows.Snapshot().Single().EnforceServerZOrder);
        Assert.IsFalse(connection.RemoteWindows.Snapshot().Single().IsCloaked);
        // Cloaking comes from TS_RAIL_ORDER_CLOAK on the RAIL channel.
        connection.RemoteWindows.SetCloaked(42, true);
        Assert.IsTrue(connection.RemoteWindows.Snapshot().Single().IsCloaked);
        Assert.IsFalse(connection.RemoteWindows.Snapshot().Single().Visible);
        connection.RemoteWindows.SetCloaked(42, false);
        connection.RemoteWindows.SetCloaked(999, true);   // unknown window: ignored
        Assert.IsFalse(connection.RemoteWindows.Snapshot().Single().IsCloaked);
        SendOrder(connection, Desktop(0x20 | 0x10, w => { w.U32LE(42); w.U8(1); w.U32LE(42); }));
        Assert.AreEqual(42u, connection.RemoteWindows.ActiveWindow);
        SendOrder(connection, Desktop(0x0a, _ => { }));
        SendOrder(connection, Window(43, 0x10000000, _ => { }));
        SendOrder(connection, Desktop(4, _ => { }));
        Assert.AreEqual(43u, connection.RemoteWindows.Snapshot().Single().Id);
        SendOrder(connection, Window(43, 0x20000000, _ => { }));
        Assert.IsEmpty(connection.RemoteWindows.Snapshot());
        Assert.IsGreaterThan(5, changes);
    }

    [TestMethod]
    public void WindowOrdersAreBoundedAndHiddenDesktopNeverBecomesAnApplication()
    {
        var decoder = new RemoteAppWindowDecoder();
        Assert.Throws<RdpProtocolException>(() => decoder.Process(Window(1, 0x10000000 | 0x400, w => { w.U32LE(8192); w.U32LE(8192); })));
        Assert.Throws<RdpProtocolException>(() => decoder.Process(Window(1, 0x10000000 | 4, w => { w.U16LE(3); w.Bytes([65, 0, 66]); })));
        Assert.Throws<RdpProtocolException>(() => decoder.Process(Window(1, 0x10000000 | 0x200, w => w.U16LE(2049))));
        Assert.Throws<RdpProtocolException>(() => decoder.Process(Window(1, 0x10000000 | 0x100 | 0x200, w =>
        {
            foreach (ushort count in new ushort[] { 64, 65 })
            {
                w.U16LE(count);
                for (int i = 0; i < count; i++) { w.U16LE(0); w.U16LE(0); w.U16LE(1); w.U16LE(1); }
            }
        })));
        for (uint id = 1; id <= RemoteAppWindowDecoder.MaxWindows; id++) { decoder.Process(Window(id, 0x10000000, _ => { })); }
        Assert.Throws<RdpProtocolException>(() => decoder.Process(Window(999, 0x10000000, _ => { })));
        decoder.Process(Desktop(1, _ => { }));
        Assert.IsEmpty(decoder.Snapshot());
        var visible = new RemoteAppWindow(1) { X = -10, Y = 10, Width = 30, Height = 40, VisibilityRects = [new(0, 0, 30, 40)] };
        Assert.AreEqual(new Rect(0, 10, 20, 40), RdpDesktopControl.RemoteAppRegions(visible, 100, 100).Single());
        Assert.IsEmpty(RdpDesktopControl.RemoteAppRegions(visible with { VisibilityRects = [] }, 100, 100).ToArray());
    }

    [TestMethod]
    public void WorkspaceRoutesOnlyApplicationPixelsAndLocalChromeToTheRemoteApp() => Run(static async () =>
    {
        var session = new FakeRdpSession { IsRemoteApp = true };
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        var window = new Window { Width = 800, Height = 600, Content = desktop };
        window.Show(); window.UpdateLayout();
        session.EmitWindows(new RemoteAppWindow(42)
        {
            Title = "Editor",
            X = 100,
            Y = 100,
            VisibleX = 100,
            VisibleY = 100,
            Width = 400,
            Height = 300,
            Style = 0x00c40000,
            VisibilityRects = [new(0, 0, 400, 300)]
        });
        byte[] pixels = new byte[800 * 600 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        session.Emit(new RdpFrame(800, 600, pixels));
        Dispatcher.UIThread.RunJobs();
        using (var rendered = new RenderTargetBitmap(new PixelSize(800, 600)))
        using (var copy = new WriteableBitmap(new PixelSize(800, 600), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
        {
            rendered.Render(desktop);
            using ILockedFramebuffer buffer = copy.Lock();
            rendered.CopyPixels(buffer);
            byte[] actual = new byte[buffer.RowBytes * 600];
            Marshal.Copy(buffer.Address, actual, 0, actual.Length);
            Assert.AreEqual(255, actual[200 * buffer.RowBytes + 200 * 4 + 2], "application pixels are rendered");
            Assert.AreEqual(0, actual[20 * buffer.RowBytes + 20 * 4 + 3], "remote desktop pixels outside window regions are never rendered");
        }
        Assert.IsNull(desktop.HitRemoteWindow(20, 20));
        Assert.AreEqual(42u, desktop.HitRemoteWindow(200, 200)!.Id);
        window.MouseDown(new Point(20, 20), MouseButton.Left); window.MouseUp(new Point(20, 20), MouseButton.Left);
        window.MouseDown(new Point(200, 200), MouseButton.Left); window.MouseUp(new Point(200, 200), MouseButton.Left);
        window.MouseDown(new Point(488, 110), MouseButton.Left); window.MouseUp(new Point(488, 110), MouseButton.Left);
        window.MouseDown(new Point(150, 110), MouseButton.Left); window.MouseMove(new Point(175, 135)); window.MouseUp(new Point(175, 135), MouseButton.Left);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.HasCount(2, session.Mouse.Where(static m => (m.Flags & 0x1000) != 0).ToArray(), "only the application client click reaches RDP input");
        Assert.Contains("activate:42:True", session.RemoteCommands);
        Assert.Contains("close:42", session.RemoteCommands);
        Assert.Contains("move:42:125:125:400:300", session.RemoteCommands);
        Assert.IsEmpty(session.Resizes, "RemoteApp keeps its negotiated virtual work area");
        session.EmitWindows(); Dispatcher.UIThread.RunJobs();
        Assert.IsNull(desktop.HitRemoteWindow(200, 200));
        window.Close();
    });

    private static RdpConnectOptions ConnectionOptions() => new("h", 3389, "", "", "", 1024, 768, 32, null, "h", [], DrawingOrders: false, RemoteApp: App);
    private static byte[] ExecuteResult(ushort result, uint rawResult = 0) => RailChannel.Packet(0x80, w =>
    {
        w.U16LE(0); w.U16LE(result); w.U32LE(rawResult); w.U16LE(0); String(w, App.Program);
    });
    private static void String(ByteWriter w, string text) { byte[] bytes = Encoding.Unicode.GetBytes(text); w.U16LE((ushort)bytes.Length); w.Bytes(bytes); }
    private static void Rects(ByteWriter w, ushort x, ushort y, ushort right, ushort bottom) { w.U16LE(1); w.U16LE(x); w.U16LE(y); w.U16LE(right); w.U16LE(bottom); }
    private static byte[] Window(uint id, uint flags, Action<ByteWriter> fields) { var w = new ByteWriter(); w.U32LE(0x01000000 | flags); w.U32LE(id); fields(w); return w.ToArray(); }
    private static byte[] Desktop(uint flags, Action<ByteWriter> fields) { var w = new ByteWriter(); w.U32LE(0x04000000 | flags); fields(w); return w.ToArray(); }
    private static void SendOrder(RdpConnection connection, byte[] payload)
    {
        var w = new ByteWriter(); w.U16LE(1); w.U8((0x0b << 2) | 2); w.U16LE((ushort)(payload.Length + 3)); w.Bytes(payload);
        connection.ApplyFastPathUpdate(0, w.Span);
    }
    private static Dictionary<int, byte[]> CapabilitySets(byte[] pdu)
    {
        var r = new ByteReader(pdu); r.Skip(12); int sourceLength = r.U16LE(); r.Skip(2 + sourceLength); int count = r.U16LE(); r.Skip(2);
        var result = new Dictionary<int, byte[]>();
        for (int i = 0; i < count; i++) { int type = r.U16LE(), size = r.U16LE(); result[type] = r.Take(size - 4).ToArray(); }
        return result;
    }
}
