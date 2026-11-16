using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VelaShell.Vnc;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

[TestClass]
public sealed class VncSessionTests
{
    /// <summary>Accepts one client with security None, runs <paramref name="body"/>, then holds the connection open.</summary>
    private static FakeVncServer Server(Func<ServerConnection, Task> body, int width = 64, int height = 48) => new(async (c, index) =>
    {
        await c.VersionAsync();
        await c.OfferAsync(1);
        await c.SecurityResultAsync();
        await c.InitAsync(width, height);
        await body(c);
        await c.HoldAsync();
    });

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) { await Task.Delay(25); }
        Assert.IsTrue(condition(), what);
    }

    [TestMethod]
    public async Task AnUpdateIsPresentedAndTheNextIncrementalRequestFollows()
    {
        byte[]? request = null;
        await using FakeVncServer server = Server(async c =>
        {
            await c.WriteAsync(Update(Rect(2, 3, 1, 1, 0, Pixel(255, 0, 0))));
            request = await c.ExpectAsync(3);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        int frames = 0;
        session.FrameAvailable += () => Interlocked.Increment(ref frames);
        await Until(() => request is not null, "an incremental request after the update");
        Assert.AreEqual(1, request![0], "incremental");
        Assert.AreEqual(64, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(5)));
        byte[] surface = new byte[64 * 48 * 4];
        VncFrameCopy copy = session.CopyFrame(surface, 64 * 4, 64, 48, full: true);
        Assert.IsFalse(copy.SizeChanged);
        int at = (3 * 64 + 2) * 4;
        Assert.AreSequenceEqual(new byte[] { 0, 0, 255, 255 }, surface.AsSpan(at, 4).ToArray());
        Assert.AreEqual("Raw", session.LastEncoding);
    }

    [TestMethod]
    public async Task KeysGoAsKeysymsUntilTheServerTakesScanCodes()
    {
        var received = new List<byte[]>();
        var go = new TaskCompletionSource();
        await using FakeVncServer server = Server(async c =>
        {
            received.Add(await c.ExpectAsync(4));
            await c.WriteAsync(Update(Rect(0, 0, 0, 0, -258)));
            await go.Task;
            received.Add(await c.ExpectAsync(255));
            received.Add(await c.ExpectAsync(5));
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await session.KeyAsync('a', 0x1e, down: true);
        await Until(() => session.SupportsScanCodes, "the QEMU extended key event confirmation");
        go.SetResult();
        await session.KeyAsync(0xff52, 0x148, down: true);   // Up arrow: E0 48
        await session.PointerAsync(VncButtons.Left | VncButtons.Right, 500, -3);
        await Until(() => received.Count == 3, "three client messages");
        Assert.AreSequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0x61 }, received[0]);
        Assert.AreSequenceEqual(new byte[] { 0, 0, 1, 0, 0, 0xff, 0x52, 0, 0, 0, 0xC8 }, received[1]);
        Assert.AreSequenceEqual(new byte[] { 5, 0, 63, 0, 0 }, received[2], "clamped to the 64×48 desktop");
    }

    [TestMethod]
    public async Task LatinOneClipboardBothWays()
    {
        byte[]? cut = null;
        await using FakeVncServer server = Server(async c =>
        {
            byte[] text = Encoding.Latin1.GetBytes("héllo\nwörld");
            await Task.Delay(200);
            await c.WriteAsync([3, 0, 0, 0], U32((uint)text.Length), text);
            cut = await c.ExpectAsync(6);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        string? remote = null;
        session.ClipboardReceived += text => remote = text;
        await Until(() => remote is not null, "ServerCutText");
        Assert.AreEqual("héllo" + Environment.NewLine + "wörld", remote);
        await session.SendClipboardAsync("a中b\r\nc");
        await Until(() => cut is not null, "ClientCutText");
        Assert.AreEqual("a?b\nc", Encoding.Latin1.GetString(cut!, 7, cut!.Length - 7));
    }

    private static byte[] Extended(uint flags, byte[] body)
    {
        byte[] payload = [.. U32(flags), .. body];
        return [3, 0, 0, 0, .. S32(-payload.Length), .. payload];
    }

    private static byte[] Zlib(params byte[][] parts)
    {
        var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (byte[] part in parts) { z.Write(part); }
        }
        return output.ToArray();
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(3u)]
    [DataRow(1048576u)]
    public async Task ExtendedClipboardExchangesUtf8ThroughNotifyRequestProvide(uint unsolicitedLimit)
    {
        const uint Caps = 1u << 24, Request = 1u << 25, Notify = 1u << 27, Provide = 1u << 28, Text = 1;
        var go = new TaskCompletionSource();
        var replies = new List<uint>();
        string? provided = null;
        await using FakeVncServer server = Server(async c =>
        {
            await go.Task;
            await c.WriteAsync(Extended(Caps | Request | Notify | Provide | Text, U32(unsolicitedLimit)));
            byte[] caps = await c.ExpectAsync(6);
            replies.Add(BinaryPrimitives.ReadUInt32BigEndian(caps.AsSpan(7)));
            await c.WriteAsync(Extended(Notify | Text, []));
            byte[] request = await c.ExpectAsync(6);
            replies.Add(BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(7)));
            byte[] utf8 = [.. Encoding.UTF8.GetBytes("你好\r\n世界"), 0];
            await c.WriteAsync(Extended(Provide | Text, Zlib(U32((uint)utf8.Length), utf8)));

            byte[] notify = await c.ExpectAsync(6);           // the client offers its own text …
            replies.Add(BinaryPrimitives.ReadUInt32BigEndian(notify.AsSpan(7)));
            await c.WriteAsync(Extended(Request | Text, []));   // … and provides it when asked
            byte[] provide = await c.ExpectAsync(6);
            replies.Add(BinaryPrimitives.ReadUInt32BigEndian(provide.AsSpan(7)));
            using var z = new ZLibStream(new MemoryStream(provide[11..]), CompressionMode.Decompress);
            byte[] size = new byte[4];
            z.ReadExactly(size);
            byte[] text = new byte[BinaryPrimitives.ReadUInt32BigEndian(size)];
            z.ReadExactly(text);
            provided = Encoding.UTF8.GetString(text);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        string? remote = null;
        session.ClipboardReceived += text => remote = text;
        go.SetResult();
        await Until(() => remote is not null, "the provided text");
        Assert.AreEqual("你好" + Environment.NewLine + "世界", remote);
        await session.SendClipboardAsync("ünï\ncode");
        await Until(() => provided is not null, "the client's Provide");
        Assert.AreEqual("ünï\r\ncode\0", provided);
        Assert.AreEqual(Caps, replies[0] & Caps);
        Assert.AreEqual(Request | Text, replies[1]);
        Assert.AreEqual(Notify | Text, replies[2]);
        Assert.AreEqual(Provide | Text, replies[3]);
    }

    [TestMethod]
    public async Task LateClipboardCapabilitiesReofferOriginalUnicodeText()
    {
        var offered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            byte[] legacy = await c.ExpectAsync(6);
            Assert.AreEqual("??", Encoding.Latin1.GetString(legacy, 7, legacy.Length - 7));
            await c.WriteAsync(Extended(ExtendedClipboard.Caps | ExtendedClipboard.Request |
                ExtendedClipboard.Notify | ExtendedClipboard.Provide | ExtendedClipboard.Text, U32(0)));
            _ = await c.ExpectAsync(6); // client capabilities
            byte[] notify = await c.ExpectAsync(6);
            Assert.AreEqual(ExtendedClipboard.Notify | ExtendedClipboard.Text,
                BinaryPrimitives.ReadUInt32BigEndian(notify.AsSpan(7)));
            await c.WriteAsync(Extended(ExtendedClipboard.Request | ExtendedClipboard.Text, []));
            byte[] provide = await c.ExpectAsync(6);
            using var z = new ZLibStream(new MemoryStream(provide[11..]), CompressionMode.Decompress);
            byte[] size = new byte[4];
            z.ReadExactly(size);
            byte[] text = new byte[BinaryPrimitives.ReadUInt32BigEndian(size)];
            z.ReadExactly(text);
            offered.TrySetResult(Encoding.UTF8.GetString(text));
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await session.SendClipboardAsync("中文");
        string received = await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("中文\0", received);
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public async Task OversizedPublicationLeavesPreviousTextAndDesktopIntact()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provided = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            await c.WriteAsync(Extended(ExtendedClipboard.Caps | ExtendedClipboard.Request |
                ExtendedClipboard.Notify | ExtendedClipboard.Provide | ExtendedClipboard.Text, U32(0)));
            _ = await c.ExpectAsync(6); // client Caps: the extended mode is ready
            ready.TrySetResult();
            _ = await c.ExpectAsync(6); // Notify for the valid value
            await request.Task;
            await c.WriteAsync(Extended(ExtendedClipboard.Request | ExtendedClipboard.Text, []));
            byte[] response = await c.ExpectAsync(6);
            Assert.AreEqual(ExtendedClipboard.Provide | ExtendedClipboard.Text,
                BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(7)), "no Notify for the oversized value");
            using var z = new ZLibStream(new MemoryStream(response[11..]), CompressionMode.Decompress);
            byte[] size = new byte[4];
            z.ReadExactly(size);
            byte[] text = new byte[BinaryPrimitives.ReadUInt32BigEndian(size)];
            z.ReadExactly(text);
            provided.TrySetResult(Encoding.UTF8.GetString(text));
            _ = await c.ExpectAsync(4);
            keyReceived.TrySetResult();
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.SendClipboardAsync("valid");
        string oversized = new('中', ExtendedClipboard.MaxText / 3 + 1);
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.SendClipboardAsync(oversized));
        request.TrySetResult();
        string received = await provided.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("valid\0", received);
        await session.KeyAsync('a', 0, true);
        await keyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(session.IsConnected);
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public void OversizedClipboardTextIsRejectedInsteadOfTruncated()
    {
        string text = new('中', ExtendedClipboard.MaxText / 3 + 1);
        var clipboard = new ExtendedClipboard();
        _ = Assert.ThrowsExactly<ArgumentException>(() => clipboard.ProvideMessage(text));
        Assert.AreEqual(9, ExtendedClipboard.TextSize("中\n文"), "size includes CRLF normalization and the NUL");
    }

    private static byte[] Screens(uint id, int width, int height) =>
        [1, 0, 0, 0, .. U32(id), .. U16(0), .. U16(0), .. U16(width), .. U16(height), .. U32(0)];

    [TestMethod]
    public async Task SetDesktopSizeRoundTrip()
    {
        byte[]? setSize = null;
        await using FakeVncServer server = Server(async c =>
        {
            await c.WriteAsync(Update(Rect(0, 0, 64, 48, -308, Screens(7, 64, 48))));
            setSize = await c.ExpectAsync(251);
            await c.WriteAsync(Update(Rect(1, 0, 800, 600, -308, Screens(7, 800, 600))));
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        Assert.IsFalse(session.CanResize);
        await Until(() => session.CanResize, "the ExtendedDesktopSize answer");
        await session.RequestResizeAsync(800, 600);
        await Until(() => session.DesktopWidth == 800, "the resized desktop");
        Assert.AreEqual(800, BinaryPrimitives.ReadUInt16BigEndian(setSize.AsSpan(1)));
        Assert.AreEqual(600, BinaryPrimitives.ReadUInt16BigEndian(setSize.AsSpan(3)));
        Assert.AreEqual(1, setSize![5], "one screen");
        Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32BigEndian(setSize.AsSpan(7)), "keeps the server's screen id");
        Assert.IsTrue(session.CopyFrame(new byte[64 * 48 * 4], 256, 64, 48).SizeChanged);
    }

    [TestMethod]
    public async Task CursorDesktopNameAndLastRect()
    {
        await using FakeVncServer server = Server(async c =>
        {
            await Task.Delay(300);   // let the test subscribe first
            byte[] cursor = [.. Pixel(255, 255, 255), .. Pixel(0, 0, 0), 0b1000_0000];
            await c.WriteAsync([0, 0, 0xFF, 0xFF],
                Rect(1, 0, 2, 1, -239, cursor),
                Rect(0, 0, 0, 0, -307, Str("renamed")),
                Rect(0, 0, 1, 1, 0, Pixel(0, 255, 0)),
                Rect(0, 0, 0, 0, -224));
            await c.WriteAsync(Update(Rect(1, 0, 1, 1, 0, Pixel(0, 0, 255))));
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        VncPointer? pointer = null;
        int infos = 0;
        session.PointerChanged += p => pointer = p;
        session.InfoChanged += () => Interlocked.Increment(ref infos);
        byte[] surface = new byte[64 * 48 * 4];
        await Until(() => { _ = session.CopyFrame(surface, 256, 64, 48); return surface[4] == 255; }, "the update after LastRect");
        Assert.AreEqual(255, surface[1], "green from the LastRect-terminated update");
        Assert.AreEqual("renamed", session.Info.DesktopName);
        Assert.IsNotNull(pointer);
        Assert.AreEqual(1, pointer.HotspotX);
        Assert.AreEqual(255, pointer.Bgra[3]);
        Assert.AreEqual(0, pointer.Bgra[7]);
    }

    [TestMethod]
    public async Task APausedDisplayRequestsNothingUntilShownAgain()
    {
        var types = new List<int>();
        var resumed = new TaskCompletionSource();
        await using FakeVncServer server = Server(async c =>
        {
            await Task.Delay(300);
            await c.WriteAsync(Update(Rect(0, 0, 1, 1, 0, Pixel(1, 2, 3))));
            types.Add(await c.U8Async());                  // must be the key below, not an update request
            _ = await c.ReadAsync(7);
            await resumed.Task;
            types.Add(await c.U8Async());
            byte[] request = await c.ReadAsync(9);
            types.Add(request[0]);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        int frames = 0;
        session.FrameAvailable += () => Interlocked.Increment(ref frames);
        await session.SetDisplayUpdatesAsync(false);
        await Until(() => frames > 0, "the update");
        await Task.Delay(200);
        await session.KeyAsync(0xff0d, 0x1c, true);
        await session.SetDisplayUpdatesAsync(true);
        resumed.SetResult();
        await Until(() => types.Count == 3, "the request after showing");
        Assert.AreSequenceEqual([4, 3, 1], types);
    }

    [TestMethod]
    public async Task ACleanCloseIsNotALostConnection()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.OfferAsync(1);
            await c.SecurityResultAsync();
            await c.InitAsync();
            await Task.Delay(300);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += failure => ended.TrySetResult(failure);
        Exception? cause = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNull(cause);
        Assert.IsFalse(session.IsConnected);
    }

    [TestMethod]
    public async Task AResetIsALostConnection()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.OfferAsync(1);
            await c.SecurityResultAsync();
            await c.InitAsync();
            await Task.Delay(300);
            c.Abort();
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += failure => ended.TrySetResult(failure);
        Exception? cause = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A reset surfaces as an I/O error on every platform; Linux may report a reset with nothing pending as end of stream.
        if (cause is null && !OperatingSystem.IsWindows()) { Assert.Inconclusive("The platform reported the reset as a clean close."); }
        Assert.IsInstanceOfType<VncConnectionLostException>(cause, $"got {cause?.GetType().FullName ?? "null"}: {cause?.InnerException?.GetType().Name} {cause?.Message}");
    }

    [TestMethod]
    public async Task AnAbortFromTheInputQueueIsALostConnection()
    {
        await using FakeVncServer server = Server(static _ => Task.CompletedTask);
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += failure => ended.TrySetResult(failure);
        session.Abort("Remote input could not keep up");
        Exception? cause = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<VncConnectionLostException>(cause, "so the tab reconnects by itself");
        Assert.AreEqual("Remote input could not keep up", cause!.Message);
    }

    [TestMethod]
    public async Task AnUnknownServerMessageIsAProtocolError()
    {
        await using FakeVncServer server = Server(async c => { await Task.Delay(300); await c.WriteAsync([77]); });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += failure => ended.TrySetResult(failure);
        Exception? cause = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<VncProtocolException>(cause);
        StringAssert.Contains(cause!.Message, "77");
    }
}
