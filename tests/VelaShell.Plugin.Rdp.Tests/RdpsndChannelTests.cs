using System.Buffers.Binary;
using VelaShell.Rdp;
using VelaShell.Rdp.Audio;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpsndChannelTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaybackRegistersBothChannelsWithoutRequiringDeviceMappings(bool drives)
    {
        var options = new RdpClientOptions
        {
            Host = "localhost",
            AudioPlayback = true,
            DynamicResolution = false,
            Drives = drives ? [new DriveMapping("work", Path.GetTempPath())] : []
        };
        string[] names = [.. RdpManagedSession.BuildChannels(options).Select(c => c.Name)];
        Assert.AreEqual(1, names.Count(n => n == "rdpsnd"));
        Assert.AreEqual(1, names.Count(n => n == "rdpdr"),
            "Windows does not send audio formats unless the companion RDPDR channel is advertised (MS-RDPEA 2.1).");
    }

    [TestMethod]
    public async Task NegotiationFiltersFormatsAndUsesClientIndicesAndTrainingEcho()
    {
        var output = new RecordingOutput();
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        byte[] compressed = Format(tag: 6);
        byte[] pcm = Format();
        await Process(channel, Formats(8, compressed, pcm));
        Assert.AreEqual((ushort)1, U16(sent[0], 18));
        Assert.AreEqual((ushort)8, U16(sent[0], 21));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(sent[0].AsSpan(4)));
        Assert.AreEqual((ushort)0, U16(sent[0], 16)); // no UDP
        Assert.AreSequenceEqual(pcm, sent[0].AsSpan(24).ToArray());
        Assert.AreSequenceEqual(new byte[] { 12, 0, 4, 0, 2, 0, 0, 0 }, sent[1]);
        await Process(channel, Packet(6, [0x34, 0x12, 10, 0, 9, 8]));
        Assert.AreSequenceEqual(new byte[] { 6, 0, 4, 0, 0x34, 0x12, 10, 0 }, sent[2]);
        await Process(channel, Wave2(0, [1, 2, 3, 4]));
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, output.Samples.Single().Data);
        Assert.AreEqual(new AudioFormat(2, 44100, 16), output.Samples.Single().Format);
        Assert.AreEqual((byte)5, sent[^1][0]);
        Assert.AreEqual((byte)42, sent[^1][6]);
    }

    [TestMethod]
    public async Task LegacyWaveRestoresFirstFourBytesAndConfirmsOnlyAfterPlayback()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completion = completed.Task };
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(5, Format()));
        Assert.HasCount(1, sent); // versions before 6 have no quality mode
        byte[] info = [2, 0, 16, 0, 0xF0, 0xFF, 0, 0, 255, 0, 0, 0, 10, 11, 12, 13];
        await Process(channel, info);
        Assert.IsEmpty(output.Samples);
        Task playback = channel.ProcessAsync([0, 0, 0, 0, 14, 15, 16, 17], Environment.TickCount64 - 100, default);
        Assert.HasCount(1, sent);
        await playback.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(channel.DrainAsync().IsCompleted);
        completed.SetResult();
        await channel.DrainAsync();
        Assert.AreSequenceEqual(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 }, output.Samples.Single().Data);
        Assert.AreEqual((byte)255, sent[^1][6]);
        ushort elapsed = unchecked((ushort)(U16(sent[^1], 4) - 0xFFF0));
        Assert.IsTrue(elapsed is >= 100 and < 10000);
    }

    [TestMethod]
    public async Task ReceivesMoreWavesAndTrainingWhilePlaybackIsPendingAndConfirmsInOrder()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completions = new Queue<Task>([first.Task, second.Task]) };
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        byte[] one = Wave2(0, [1, 2, 3, 4]);
        byte[] two = Wave2(0, [5, 6, 7, 8]);
        one[8] = 255;
        two[8] = 0;
        await channel.ProcessAsync(one, Environment.TickCount64, default).WaitAsync(TimeSpan.FromSeconds(1));
        await channel.ProcessAsync(two, Environment.TickCount64, default).WaitAsync(TimeSpan.FromSeconds(1));
        await channel.ProcessAsync(Packet(6, [0, 0, 0, 0]), Environment.TickCount64, default);
        Assert.HasCount(2, output.Samples);
        Assert.AreEqual((byte)6, sent[^1][0]);
        second.SetResult();
        Assert.IsFalse(channel.DrainAsync().IsCompleted);
        Assert.DoesNotContain(pdu => pdu[0] == 5, sent);
        first.SetResult();
        await channel.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSequenceEqual(new byte[] { 255, 0 }, sent.Where(pdu => pdu[0] == 5).Select(pdu => pdu[6]).ToArray());
    }

    [TestMethod]
    public async Task AsynchronousDeviceFailureIsObservableWithoutAnotherPacket()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completion = completion.Task };
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        await channel.ProcessAsync(Wave2(0, [1, 2, 3, 4]), Environment.TickCount64, default);
        var error = new IOException("device removed");
        completion.SetException(error);
        Exception actual = await channel.PlaybackFailure.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSame(error, actual);
        _ = await Assert.ThrowsExactlyAsync<IOException>(channel.DrainAsync);
        Assert.DoesNotContain(pdu => pdu[0] == 5, sent);
    }

    [TestMethod]
    public async Task LaterPlaybackFailureDoesNotWaitForAnEarlierPendingBlock()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completions = new Queue<Task>([first.Task, second.Task]) };
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        await channel.ProcessAsync(Wave2(0, [1, 2, 3, 4]), Environment.TickCount64, default);
        await channel.ProcessAsync(Wave2(0, [5, 6, 7, 8]), Environment.TickCount64, default);
        var error = new IOException("later buffer failed");
        second.SetException(error);
        Exception actual = await channel.PlaybackFailure.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSame(error, actual);
        Assert.IsFalse(first.Task.IsCompleted);
        Assert.DoesNotContain(pdu => pdu[0] == 5, sent);
    }

    [TestMethod]
    public async Task DisposeCancelsInFlightPlaybackWithoutConfirmingIt()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completion = completion.Task };
        var sent = new List<byte[]>();
        RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        await channel.ProcessAsync(Wave2(0, [1, 2, 3, 4]), Environment.TickCount64, default);
        channel.Dispose();
        completion.SetResult();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(channel.DrainAsync);
        Assert.IsTrue(output.Disposed);
        Assert.DoesNotContain(pdu => pdu[0] == 5, sent);
    }

    [TestMethod]
    public async Task CloseDrainsThenRequiresRenegotiation()
    {
        var output = new RecordingOutput();
        using RdpsndChannel channel = Make(output, []);
        await Process(channel, Formats(8, Format()));
        await Process(channel, Wave2(0, [1, 2, 3, 4]));
        int previous = output.CloseCount;
        await Process(channel, Packet(1, []));
        Assert.AreEqual(previous + 1, output.CloseCount);
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Wave2(0, [1, 2, 3, 4])));
        await Process(channel, Formats(8, Format(channels: 1, bits: 8)));
        await Process(channel, Wave2(0, [128, 129]));
        Assert.HasCount(2, output.Samples);
        Assert.AreEqual(new AudioFormat(1, 44100, 8), output.Samples[1].Format);
    }

    [TestMethod]
    public async Task DisabledCompanionChannelNegotiatesNoFormatsOrPlayback()
    {
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(null, sent);
        await Process(channel, Formats(8, Format()));
        Assert.AreEqual((ushort)0, U16(sent[0], 18));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(sent[0].AsSpan(4)));
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Wave2(0, [1, 2, 3, 4])));
    }

    [TestMethod]
    public async Task RejectsMalformedFormatsUnknownIndicesAndTruncatedWaves()
    {
        using RdpsndChannel channel = Make(new RecordingOutput(), []);
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Packet(7, new byte[19])));
        byte[] extended = Format();
        extended[16] = 8;
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Formats(8, extended)));
        byte[] badAlignment = Format();
        badAlignment[12] = 1;
        _ = await Assert.ThrowsAsync<IOException>(() => Process(channel, Formats(8, badAlignment)));
        await Process(channel, Formats(8, Format()));
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Wave2(1, [1, 2, 3, 4])));
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, Wave2(0, [1, 2, 3])));
        await Process(channel, new byte[] { 2, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 });
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => Process(channel, new byte[4]));
    }

    [TestMethod]
    public async Task CancelledPlaybackDoesNotAcknowledgeUnplayedAudio()
    {
        var output = new RecordingOutput { Completion = new TaskCompletionSource().Task };
        var sent = new List<byte[]>();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        using var stop = new CancellationTokenSource();
        Task playing = channel.ProcessAsync(Wave2(0, [1, 2, 3, 4]), Environment.TickCount64, stop.Token);
        stop.Cancel();
        await playing;
        _ = await Assert.ThrowsAsync<OperationCanceledException>(channel.DrainAsync);
        Assert.DoesNotContain(pdu => pdu[0] == 5, sent);
    }

    [TestMethod]
    public async Task SessionReportsAndReplaysAudioFailureAndDisposesOutput()
    {
        var options = new RdpConnectOptions("localhost", 3389, "", "", "", 640, 480, 32, null, "localhost", []);
        await using var session = new RdpManagedSession(new RdpConnection(options));
        var output = new RecordingOutput();
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AudioPlaybackFailed += error => failure.TrySetResult(error);
        session.EnableAudio(true, () => output);
        session.ReceiveAudio([7, 0, 0, 0]);
        Exception result = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = Assert.IsInstanceOfType<RdpProtocolException>(result);
        Exception? replay = null;
        session.AudioPlaybackFailed += error => replay = error;
        Assert.AreSame(result, replay);
        await session.DisposeAsync();
        Assert.IsTrue(output.Disposed);
    }

    [TestMethod]
    public async Task SessionReportsAsynchronousPlaybackFailureWhileWaitingForMorePackets()
    {
        var options = new RdpConnectOptions("localhost", 3389, "", "", "", 640, 480, 32, null, "localhost", [new("rdpsnd", 0xC0000000)]);
        await using var session = new RdpManagedSession(new RdpConnection(options));
        using var wire = new MemoryStream();
        var mcs = new VelaShell.Rdp.Mcs.McsLayer(new VelaShell.Rdp.Wire.RdpTransport(wire));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("UserId", flags)!.SetValue(mcs, (ushort)1001);
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("Channels", flags)!.SetValue(mcs, new Dictionary<string, ushort> { ["rdpsnd"] = 1004 });
        typeof(RdpConnection).GetField("_mcs", flags)!.SetValue(session.Connection, mcs);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new RecordingOutput { Completion = completion.Task };
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AudioPlaybackFailed += error => failure.TrySetResult(error);
        session.EnableAudio(true, () => output);
        session.ReceiveAudio(Formats(8, Format()));
        session.ReceiveAudio(Wave2(0, [1, 2, 3, 4]));
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = new IOException("device removed after submission");
        completion.SetException(expected);
        Exception actual = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(expected, actual);
        await session.DisposeAsync();
        Assert.IsTrue(output.Disposed);
    }

    [TestMethod]
    public async Task FragmentedVirtualChannelDeliversPcmAndSendsConfirmationThroughSession()
    {
        var options = new RdpConnectOptions("localhost", 3389, "", "", "", 640, 480, 32, null, "localhost", [new("rdpsnd", 0xC0000000)]);
        await using var session = new RdpManagedSession(new RdpConnection(options));
        using var wire = new MemoryStream();
        var transport = new VelaShell.Rdp.Wire.RdpTransport(wire);
        var mcs = new VelaShell.Rdp.Mcs.McsLayer(transport);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("UserId", flags)!.SetValue(mcs, (ushort)1001);
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("Channels", flags)!.SetValue(mcs, new Dictionary<string, ushort> { ["rdpsnd"] = 1004 });
        typeof(RdpConnection).GetField("_mcs", flags)!.SetValue(session.Connection, mcs);
        var output = new RecordingOutput();
        session.EnableAudio(true, () => output);
        Deliver(Formats(8, Format()));
        Deliver(Wave2(0, [10, 11, 12, 13, 14, 15, 16, 17]));
        Deliver(Packet(1, []));
        await output.ClosedAfterPlayback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSequenceEqual(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 }, output.Samples.Single().Data);
        wire.Position = 0;
        foreach (byte type in new byte[] { 7, 12, 5 })
        {
            RdpFramePdu frame = await transport.ReadPduAsync(default);
            frame.Data[3] = 0x68;
            McsData message = McsLayer.ParseSendDataIndication(frame.Data)!.Value;
            Assert.AreEqual(type, message.Payload[8]);
        }
        Assert.AreEqual(wire.Length, wire.Position);

        void Deliver(byte[] pdu)
        {
            foreach ((int offset, int length, uint chunkFlags) in new[] { (0, 3, 1u), (3, pdu.Length - 3, 2u) })
            {
                byte[] chunk = new byte[8 + length];
                BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)pdu.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), chunkFlags);
                pdu.AsSpan(offset, length).CopyTo(chunk.AsSpan(8));
                session.Connection.ReassembleChannel(1004, "rdpsnd", chunk);
            }
        }
    }

    [TestMethod]
    [TestCategory("NativeAudio")]
    public async Task SystemDefaultOutputPlaysProtocolTone()
    {
        if (Environment.GetEnvironmentVariable("VELA_RDP_AUDIO_TONE_TEST") != "1")
        {
            Assert.Inconclusive("Set VELA_RDP_AUDIO_TONE_TEST=1 to play two quiet 440 Hz tones through RDPSND on the default device.");
            return;
        }
        var sent = new List<byte[]>();
        using IAudioOutput output = AudioOutput.Create();
        using RdpsndChannel channel = Make(output, sent);
        await Process(channel, Formats(8, Format()));
        byte[] samples = new byte[44100 * 4 * 3 / 10];
        for (int frame = 0; frame < samples.Length / 4; frame++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * 440 * frame / 44100) * 2000);
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4), value);
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4 + 2), value);
        }
        await Process(channel, Wave2(0, samples)).WaitAsync(TimeSpan.FromSeconds(7));
        await Process(channel, Wave2(0, samples)).WaitAsync(TimeSpan.FromSeconds(7));
        Assert.AreEqual(2, sent.Count(pdu => pdu[0] == 5));
        output.Dispose();
        await ((NativeAudioOutput)output).Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    [DataRow(8, 128)]
    [DataRow(16, 0)]
    public async Task DiagnosticsDistinguishSilenceFromSamplesWithoutLoggingEveryWave(int bits, int silence)
    {
        var messages = new List<string>();
        using var channel = new RdpsndChannel((_, _) => Task.CompletedTask, new RecordingOutput(), messages.Add);
        await Process(channel, Formats(8, Format(channels: 1, bits: (ushort)bits)));
        await Process(channel, Wave2(0, [(byte)silence, (byte)silence]));
        await Process(channel, Wave2(0, [(byte)silence, (byte)silence]));
        await Process(channel, Wave2(0, [(byte)silence, (byte)(silence + 1)]));
        await Process(channel, Wave2(0, [(byte)silence, (byte)(silence + 1)]));
        Assert.Contains(message => message.Contains("offered=1; accepted=1"), messages);
        Assert.AreEqual(2, messages.Count(message => message.StartsWith("wave received;")));
        Assert.Contains(message => message.Contains("silent=True"), messages);
        Assert.Contains(message => message.Contains("silent=False"), messages);
        Assert.AreEqual(1, messages.Count(message => message == "first playback completed and confirmed"));
    }

    [TestMethod]
    public async Task ThrowingDiagnosticsDoNotInterruptNegotiationPlaybackOrConfirmation()
    {
        var sent = new List<byte[]>();
        var output = new RecordingOutput();
        using var channel = new RdpsndChannel(
            (pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, output,
            _ => throw new InvalidOperationException("Logging unavailable"));
        await Process(channel, Formats(8, Format()));
        await Process(channel, Packet(6, [0, 0, 0, 0]));
        await Process(channel, Wave2(0, [1, 2, 3, 4]));
        await Process(channel, Packet(1, []));
        Assert.HasCount(1, output.Samples);
        Assert.AreSequenceEqual(new byte[] { 7, 12, 6, 5 }, sent.Select(pdu => pdu[0]).ToArray());
    }

    private static RdpsndChannel Make(IAudioOutput? output, List<byte[]> sent) =>
        new((pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, output);
    private static async Task Process(RdpsndChannel channel, byte[] pdu)
    {
        await channel.ProcessAsync(pdu, Environment.TickCount64, default);
        await channel.DrainAsync();
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    internal static byte[] Format(ushort tag = 1, ushort channels = 2, ushort bits = 16)
    {
        byte[] result = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(result, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)(44100 * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), bits);
        return result;
    }
    internal static byte[] Formats(ushort version, params byte[][] formats)
    {
        byte[] body = new byte[20 + formats.Sum(f => f.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)formats.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(17), version);
        int offset = 20;
        foreach (byte[] format in formats) { format.CopyTo(body, offset); offset += format.Length; }
        return Packet(7, body);
    }
    internal static byte[] Wave2(ushort index, byte[] data)
    {
        byte[] body = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 100);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), index);
        body[4] = 42;
        data.CopyTo(body, 12);
        return Packet(13, body);
    }
    internal static byte[] Packet(byte type, byte[] body)
    {
        byte[] pdu = new byte[4 + body.Length];
        pdu[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(pdu.AsSpan(2), (ushort)body.Length);
        body.CopyTo(pdu, 4);
        return pdu;
    }
    private sealed class RecordingOutput : IAudioOutput
    {
        internal List<(AudioFormat Format, byte[] Data)> Samples { get; } = [];
        internal Task Completion { get; init; } = Task.CompletedTask;
        internal Queue<Task>? Completions { get; init; }
        internal int CloseCount { get; private set; }
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PlayAsync(AudioFormat format, byte[] data, CancellationToken ct)
        {
            Samples.Add((format, data));
            _ = Started.TrySetResult();
            await (Completions?.Dequeue() ?? Completion).WaitAsync(ct);
        }
        internal TaskCompletionSource ClosedAfterPlayback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Close()
        {
            CloseCount++;
            if (Samples.Count > 0) { _ = ClosedAfterPlayback.TrySetResult(); }
        }
        public void Dispose() => Disposed = true;
    }
}
