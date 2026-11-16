using VelaShell.Vnc;
using VelaShell.Vnc.Audio;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>QEMU Audio (pseudo-encoding -259, QEMU client / server message 255 submessage 1) against the scripted server.</summary>
[TestClass]
public sealed class VncAudioTests
{
    private static readonly byte[] SetFormat = [255, 1, 0, 2, 3, 2, 0, 0, 0xAC, 0x44];   // S16, stereo, 44100 Hz
    private static readonly byte[] Enable = [255, 1, 0, 0];
    private static readonly byte[] Disable = [255, 1, 0, 1];

    internal sealed class RecordingOutput : IAudioOutput
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _played = new();
        internal Exception? Fail { get; set; }
        internal List<AudioFormat> Formats { get; } = [];
        internal int Disposed { get; private set; }

        internal byte[] Played { get { lock (_gate) { return _played.ToArray(); } } }

        public Task PlayAsync(AudioFormat format, byte[] data, CancellationToken ct)
        {
            if (Fail is { } fail) { return Task.FromException(fail); }
            lock (_gate)
            {
                Formats.Add(format);
                _played.Write(data);
            }
            return Task.CompletedTask;
        }

        public void Close() { }
        public void Dispose() => Disposed++;
    }

    private static FakeVncServer Server(Func<ServerConnection, int[], Task> body) => new(async (c, index) =>
    {
        await c.VersionAsync();
        await c.OfferAsync(1);
        await c.SecurityResultAsync();
        (_, int[] encodings) = await c.InitAsync();
        await body(c, encodings);
        await c.HoldAsync();
    });

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) { await Task.Delay(25); }
        Assert.IsTrue(condition(), what);
    }

    private static byte[] AudioData(byte[] pcm) => [255, 1, 0, 2, .. U32((uint)pcm.Length), .. pcm];

    [TestMethod]
    public async Task AudioIsOnlyAskedForWhenEnabled()
    {
        int[]? off = null, on = null;
        await using (FakeVncServer server = Server((c, e) => { off = e; return Task.CompletedTask; }))
        {
            await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port));
            await Until(() => off is not null, "the encodings");
            Assert.IsFalse(session.AudioActive);
        }
        await using (FakeVncServer server = Server((c, e) => { on = e; return Task.CompletedTask; }))
        {
            await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => new RecordingOutput()));
            await Until(() => on is not null, "the encodings");
        }
        Assert.DoesNotContain(-259, off!);
        Assert.Contains(-259, on!);
    }

    [TestMethod]
    public async Task ConfirmationSetsTheFormatEnablesAndThePcmReachesTheOutputInOrder()
    {
        var output = new RecordingOutput();
        byte[] pcm = [.. Enumerable.Range(0, VncAudioPlayer.ChunkBytes * 2 + 400).Select(static i => (byte)(i * 7))];
        byte[]? setFormat = null, enable = null;
        await using FakeVncServer server = Server(async (c, encodings) =>
        {
            // QEMU confirms with a rectangle of the desktop's size carrying encoding -259.
            await c.WriteAsync(Update(Rect(0, 0, 64, 48, -259)));
            setFormat = await c.ReadAsync(10);
            enable = await c.ReadAsync(4);
            await c.WriteAsync([255, 1, 0, 1]);                       // begin
            await c.WriteAsync(AudioData(pcm[..5000]));
            await c.WriteAsync(AudioData(pcm[5000..]));
            await c.WriteAsync([255, 1, 0, 0]);                       // end: the partial buffer is played too
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => output));
        await Until(() => output.Played.Length == pcm.Length, "all PCM played");
        Assert.AreSequenceEqual(SetFormat, setFormat!);
        Assert.AreSequenceEqual(Enable, enable!);
        byte[] played = output.Played;
        Assert.AreSequenceEqual(pcm, played);
        Assert.IsTrue(output.Formats.All(static f => f == VncAudioPlayer.Format));
        Assert.IsTrue(session.AudioActive);
    }

    [TestMethod]
    public async Task AFailingDeviceStopsAudioOnlyAndTheServerIsToldToStopCapturing()
    {
        var output = new RecordingOutput { Fail = new IOException("no output device") };
        byte[]? disable = null, key = null;
        await using FakeVncServer server = Server(async (c, encodings) =>
        {
            await c.WriteAsync(Update(Rect(0, 0, 64, 48, -259)));
            _ = await c.ReadAsync(14);
            await c.WriteAsync(AudioData(new byte[VncAudioPlayer.ChunkBytes]));
            while (disable is null)
            {
                byte type = await c.U8Async();
                if (type == 3) { _ = await c.ReadAsync(9); continue; }
                disable = [type, .. await c.ReadAsync(3)];
            }
            key = await c.ExpectAsync(4);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => output));
        Exception? failure = null;
        session.AudioFailed += e => failure = e;
        await Until(() => failure is not null, "the audio failure");
        await Until(() => disable is not null, "the disable message");
        Assert.AreSequenceEqual(Disable, disable!);
        Assert.IsFalse(session.AudioActive);
        Assert.IsTrue(session.IsConnected, "the desktop stays connected");
        await session.KeyAsync('a', 0, down: true);
        await Until(() => key is not null, "input still flows");

        Exception? late = null;
        session.AudioFailed += e => late = e;
        Assert.AreSame(failure, late, "a late subscriber learns about the failure");
    }

    [TestMethod]
    public async Task AnAudioBufferThatIsNotWholeFramesStopsAudioButNotTheDesktop()
    {
        byte[]? key = null;
        var sent = new TaskCompletionSource();
        await using FakeVncServer server = Server(async (c, encodings) =>
        {
            await c.WriteAsync(Update(Rect(0, 0, 64, 48, -259)));
            _ = await c.ReadAsync(14);
            await c.WriteAsync(AudioData([1, 2, 3]));
            sent.SetResult();
            while (key is null)   // the disable message (255, 1, op) comes first, then the key
            {
                byte type = await c.U8Async();
                byte[] body = await c.ReadAsync(type switch { 3 => 9, 4 => 7, 255 => 3, _ => throw new InvalidDataException($"client message {type}") });
                if (type == 4) { key = body; }
            }
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => new RecordingOutput()));
        Exception? failure = null;
        session.AudioFailed += e => failure = e;
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => failure is not null, "the audio failure");
        Assert.IsInstanceOfType<VncProtocolException>(failure);
        await session.KeyAsync('c', 0, down: true);
        await Until(() => key is not null, "the stream stayed in step and the desktop is alive");
    }

    [TestMethod]
    public async Task AnOversizedAudioBufferIsAProtocolError()
    {
        await using FakeVncServer server = Server(async (c, encodings) =>
        {
            await c.WriteAsync(Update(Rect(0, 0, 64, 48, -259)));
            _ = await c.ReadAsync(14);
            await c.WriteAsync([255, 1, 0, 2, .. U32(2 * 1024 * 1024)]);
        });
        var ended = new TaskCompletionSource<Exception?>();
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => new RecordingOutput()));
        session.Disconnected += e => ended.TrySetResult(e);
        Exception? failure = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<VncProtocolException>(failure);
    }

    [TestMethod]
    public async Task AudioWithoutAConfirmationIsSkipped()
    {
        byte[]? key = null;
        await using FakeVncServer server = Server(async (c, encodings) =>
        {
            await c.WriteAsync(AudioData(new byte[400]));   // never enabled: read and dropped
            key = await c.ExpectAsync(4);
        });
        await using VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, audio: true, audioOutput: () => new RecordingOutput()));
        await Task.Delay(100);
        await session.KeyAsync('b', 0, down: true);
        await Until(() => key is not null, "the session survived");
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task ThePlayerDropsRatherThanBlocksWhenTheOutputFallsBehind()
    {
        var gate = new TaskCompletionSource();
        var blocked = new BlockingOutput(gate.Task);
        Exception? failed = null;
        await using var player = new VncAudioPlayer(() => blocked, e => failed = e, null);
        for (int i = 0; i < 2000; i++) { player.Push(new byte[VncAudioPlayer.ChunkBytes]); }   // returns at once
        await Task.Delay(100);
        Assert.IsGreaterThan(0L, player.Dropped);
        Assert.IsLessThanOrEqualTo(VncAudioPlayer.MaxOutstanding, blocked.Started);
        gate.SetResult();
        Assert.IsNull(failed);
    }

    private sealed class BlockingOutput(Task release) : IAudioOutput
    {
        private int _started;
        internal int Started => Volatile.Read(ref _started);

        public Task PlayAsync(AudioFormat format, byte[] data, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _started);
            return release.WaitAsync(ct);
        }

        public void Close() { }
        public void Dispose() { }
    }
}
