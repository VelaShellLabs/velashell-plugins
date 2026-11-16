using VelaShell.Vnc;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>Continuous Updates (-313, messages 150) and Fence (-312, messages 248), against TigerVNC's server behaviour.</summary>
[TestClass]
public sealed class VncContinuousUpdatesTests
{
    private static readonly byte[] EndOfContinuousUpdates = [150];
    private static readonly byte[] EnableWhole = [150, 1, 0, 0, 0, 0, 0, 64, 0, 48];

    private static FakeVncServer Server(Func<ServerConnection, int[], Task> body) => new(async (c, index) =>
    {
        await c.VersionAsync();
        await c.OfferAsync(1);
        await c.SecurityResultAsync();
        (_, int[] encodings) = await c.InitAsync();
        await body(c, encodings);
        await c.HoldAsync();
    });

    /// <summary>The next client message, whole (type byte included).</summary>
    private static async Task<byte[]> NextAsync(ServerConnection c)
    {
        byte type = await c.U8Async();
        byte[] body = type switch
        {
            3 or 150 => await c.ReadAsync(9),
            4 => await c.ReadAsync(7),
            5 => await c.ReadAsync(5),
            248 => await Fence(c),
            _ => throw new InvalidDataException($"Unexpected client message {type}")
        };
        return [type, .. body];

        static async Task<byte[]> Fence(ServerConnection c)
        {
            byte[] header = await c.ReadAsync(8);
            return [.. header, .. await c.ReadAsync(header[7])];
        }
    }

    private static byte[] ServerFence(uint flags, params byte[] payload) => [248, 0, 0, 0, .. U32(flags), (byte)payload.Length, .. payload];

    /// <summary>Confirms the extension, sends the first update and returns the client's EnableContinuousUpdates.</summary>
    private static async Task<byte[]> TurnOnAsync(ServerConnection c)
    {
        await c.WriteAsync(EndOfContinuousUpdates);
        await c.WriteAsync(Update(Rect(0, 0, 1, 1, 0, Pixel(1, 2, 3))));
        return await NextAsync(c);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) { await Task.Delay(25); }
        Assert.IsTrue(condition(), what);
    }

    [TestMethod]
    public async Task BothPseudoEncodingsAreAskedForUnlessTurnedOff()
    {
        int[]? on = null, off = null;
        await using (FakeVncServer server = Server((c, e) => { on = e; return Task.CompletedTask; }))
        {
            await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
            await Until(() => on is not null, "the encodings");
        }
        await using (FakeVncServer server = Server((c, e) => { off = e; return Task.CompletedTask; }))
        {
            await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, continuousUpdates: false));
            await Until(() => off is not null, "the encodings");
        }
        Assert.Contains(-312, on!);
        Assert.Contains(-313, on!);
        Assert.DoesNotContain(-312, off!);
        Assert.DoesNotContain(-313, off!);
    }

    [TestMethod]
    public async Task AfterTheConfirmationTheClientTurnsThemOnAndStopsRequesting()
    {
        byte[]? enable = null, afterSecondUpdate = null;
        await using FakeVncServer server = Server(async (c, e) =>
        {
            enable = await TurnOnAsync(c);
            await c.WriteAsync(Update(Rect(0, 0, 1, 1, 0, Pixel(4, 5, 6))));
            // Had the client asked for another update, that request would arrive before the fence answer.
            await c.WriteAsync(ServerFence(0x80000000, 9));
            afterSecondUpdate = await NextAsync(c);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await Until(() => afterSecondUpdate is not null, "the fence answer");
        Assert.AreSequenceEqual(EnableWhole, enable!, "the whole 64×48 desktop");
        Assert.AreEqual((byte)248, afterSecondUpdate![0], "no FramebufferUpdateRequest while the server pushes");
        Assert.IsTrue(session.ContinuousUpdatesActive);
    }

    [TestMethod]
    public async Task WithoutTheConfirmationEveryUpdateIsRequested()
    {
        byte[]? next = null;
        await using FakeVncServer server = Server(async (c, e) =>
        {
            await c.WriteAsync(Update(Rect(0, 0, 1, 1, 0, Pixel(1, 2, 3))));
            next = await NextAsync(c);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await Until(() => next is not null, "the next request");
        Assert.AreEqual((byte)3, next![0]);
        Assert.AreEqual((byte)1, next[1], "incremental");
        Assert.IsFalse(session.ContinuousUpdatesActive);
    }

    [TestMethod]
    public async Task AFenceRequestIsAnsweredWithItsPayloadAndBlockFlagsOnly()
    {
        byte[]? answer = null;
        await using FakeVncServer server = Server(async (c, e) =>
        {
            await c.WriteAsync(ServerFence(0x00000001, 7));                       // not a request: no answer
            await c.WriteAsync(ServerFence(0x80000000 | 0x4 | 0x2 | 0x1, 1, 2, 3)); // Request, SyncNext, BlockAfter, BlockBefore
            while (answer is null)
            {
                byte[] message = await NextAsync(c);
                if (message[0] == 248) { answer = message; }
            }
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await Until(() => answer is not null, "the fence answer");
        Assert.AreSequenceEqual(new byte[] { 248, 0, 0, 0, 0, 0, 0, 3, 3, 1, 2, 3 }, answer!, "BlockBefore and BlockAfter kept; Request and SyncNext cleared; payload echoed");
    }

    [TestMethod]
    public async Task AResizedDesktopIsCoveredAgain()
    {
        byte[]? again = null;
        await using FakeVncServer server = Server(async (c, e) =>
        {
            _ = await TurnOnAsync(c);
            // ExtendedDesktopSize: reason 0 (server), status 0, 80×60, one screen.
            byte[] screens = [1, 0, 0, 0, .. U32(7), .. U16(0), .. U16(0), .. U16(80), .. U16(60), .. U32(0)];
            await c.WriteAsync(Update(Rect(0, 0, 80, 60, -308, screens)));
            again = await NextAsync(c);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await Until(() => again is not null, "the new area");
        Assert.AreSequenceEqual(new byte[] { 150, 1, 0, 0, 0, 0, 0, 80, 0, 60 }, again!);
    }

    [TestMethod]
    public async Task AHiddenTabTurnsThemOffAndShowingItTurnsThemOnAgain()
    {
        var on = new TaskCompletionSource();
        var messages = new List<byte[]>();
        await using FakeVncServer server = Server(async (c, e) =>
        {
            _ = await TurnOnAsync(c);
            on.SetResult();
            byte[] off = await NextAsync(c);
            lock (messages) { messages.Add(off); }
            await c.WriteAsync(EndOfContinuousUpdates);   // the server confirms the disable
            byte[] back = await NextAsync(c);
            lock (messages) { messages.Add(back); }
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await on.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => session.ContinuousUpdatesActive, "continuous updates on");
        await session.SetDisplayUpdatesAsync(false);
        Assert.IsFalse(session.ContinuousUpdatesActive);
        await Until(() => { lock (messages) { return messages.Count == 1; } }, "the disable");
        await Task.Delay(100);
        await session.SetDisplayUpdatesAsync(true);
        await Until(() => { lock (messages) { return messages.Count == 2; } }, "the enable");
        Assert.AreSequenceEqual(new byte[] { 150, 0, 0, 0, 0, 0, 0, 64, 0, 48 }, messages[0]);
        Assert.AreSequenceEqual(EnableWhole, messages[1], "re-enabled, not a full refresh: the server sends what changed meanwhile");
    }

    [TestMethod]
    public async Task AServerThatEndsThemOnItsOwnGetsRequestsAgain()
    {
        byte[]? next = null;
        await using FakeVncServer server = Server(async (c, e) =>
        {
            _ = await TurnOnAsync(c);
            await c.WriteAsync(EndOfContinuousUpdates);
            next = await NextAsync(c);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
        await Until(() => next is not null, "a request after the server stopped pushing");
        Assert.AreEqual((byte)3, next![0]);
        Assert.IsFalse(session.ContinuousUpdatesActive);
    }
}
