using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using VelaShell.Rdp.Audio;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class AudioOutputTests
{
    private static readonly AudioFormat Stereo = new(2, 48000, 16);

    [TestMethod]
    public async Task ReusesTheDeviceAndReopensOnFormatChange()
    {
        var device = new FakeDevice { _complete = true };
        using var output = new NativeAudioOutput(device);
        await output.PlayAsync(Stereo, new byte[192], default);
        await output.PlayAsync(Stereo, new byte[192], default);
        Assert.HasCount(1, device._opens);
        var mono8 = new AudioFormat(1, 8000, 8);
        await output.PlayAsync(mono8, new byte[80], default);
        Assert.AreSequenceEqual([Stereo, mono8], device._opens);
        Assert.AreEqual(3, device._starts);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(device._opened);
    }

    [TestMethod]
    public async Task PlaybackWaitsForDeviceCompletionAndCloseCancelsThenReopens()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task play = output.PlayAsync(Stereo, new byte[192], default);
        await device._started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(play.IsCompleted);
        output.Close();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => play);
        device._complete = true;
        await output.PlayAsync(Stereo, new byte[192], default);
        Assert.HasCount(2, device._opens);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task SubmitsMultipleBuffersBeforeCompletionAndPreservesCompletionOrder()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task first = output.PlayAsync(Stereo, new byte[3840], default);
        Task second = output.PlayAsync(Stereo, new byte[3840], default);
        Task third = output.PlayAsync(Stereo, new byte[3840], default);
        await device._multipleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.IsFalse(third.IsCompleted);
        Volatile.Write(ref device._permittedCompletions, 1);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(second.IsCompleted);
        Assert.IsFalse(third.IsCompleted);
        Volatile.Write(ref device._permittedCompletions, 2);
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, device._opens);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CloseCancelsAllInFlightBuffersAndReopensWithDifferentFormat()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task[] playing = [.. Enumerable.Range(0, 3).Select(_ => output.PlayAsync(Stereo, new byte[3840], default))];
        await device._multipleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task changed = output.PlayAsync(new(1, 8000, 8), new byte[160], default);
        Assert.HasCount(1, device._opens);
        output.Close();
        foreach (Task task in playing.Append(changed))
        {
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        device._complete = true;
        await output.PlayAsync(new(1, 8000, 8), new byte[160], default);
        Assert.AreEqual(4, device._starts);
        Assert.HasCount(2, device._opens);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task FormatChangeWaitsUntilPreviouslySubmittedBuffersFinish()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task[] playing = [.. Enumerable.Range(0, 3).Select(_ => output.PlayAsync(Stereo, new byte[3840], default))];
        await device._multipleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task changed = output.PlayAsync(new(1, 8000, 8), new byte[160], default);
        Volatile.Write(ref device._permittedCompletions, 1);
        await playing[0].WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(changed.IsCompleted);
        Assert.HasCount(1, device._opens);
        device._complete = true;
        await Task.WhenAll(playing.Append(changed)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSequenceEqual([Stereo, new AudioFormat(1, 8000, 8)], device._opens);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task QueueBudgetIncludesBuffersAlreadyTakenByWorker()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task[] playing = [.. Enumerable.Range(0, 3).Select(_ => output.PlayAsync(Stereo, new byte[3840], default))];
        await device._multipleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => output.PlayAsync(Stereo, new byte[384000], default));
        output.Dispose();
        foreach (Task task in playing)
        {
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        }

        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CancellationAndDisposalDoNotWaitForANativeCall()
    {
        using var nativeCall = new ManualResetEventSlim();
        var device = new FakeDevice { _blockOpen = nativeCall };
        using var output = new NativeAudioOutput(device);
        using var cancel = new CancellationTokenSource();
        try
        {
            Task play = output.PlayAsync(Stereo, new byte[192], cancel.Token);
            await device._openEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => play.WaitAsync(TimeSpan.FromSeconds(1)));
            var elapsed = Stopwatch.StartNew();
            output.Dispose();
            Assert.IsLessThan(TimeSpan.FromSeconds(1), elapsed.Elapsed);
        }
        finally
        {
            nativeCall.Set();
            output.Dispose();
            await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.AreEqual(0, device._starts);
        Assert.IsFalse(device._opened);
    }

    [TestMethod]
    public async Task StalledPlaybackTimesOutAndDeviceErrorsAreReported()
    {
        var stalled = new FakeDevice();
        using var output = new NativeAudioOutput(stalled, TimeSpan.FromMilliseconds(50));
        _ = await Assert.ThrowsAsync<TimeoutException>(() => output.PlayAsync(Stereo, new byte[192], default));
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(stalled._opened);

        var broken = new FakeDevice { _startError = new IOException("native device disconnected") };
        using var failed = new NativeAudioOutput(broken);
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => failed.PlayAsync(Stereo, new byte[192], default));
        Assert.AreEqual("native device disconnected", error.Message);
        failed.Dispose();
        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(broken._opened);
    }

    [TestMethod]
    public async Task TimeoutDuringNativeOpenDoesNotPlayAfterTheCallReturns()
    {
        using var nativeCall = new ManualResetEventSlim();
        var device = new FakeDevice { _blockOpen = nativeCall };
        using var output = new NativeAudioOutput(device, TimeSpan.FromMilliseconds(100));
        try
        {
            Task play = output.PlayAsync(Stereo, new byte[192], default);
            await device._openEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _ = await Assert.ThrowsAsync<TimeoutException>(() => play);
            nativeCall.Set();
            await device._closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(0, device._starts);
        }
        finally
        {
            nativeCall.Set();
            output.Dispose();
            await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public async Task InvalidPcmAndCancelledRequestsNeverOpenTheDevice()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        _ = Assert.ThrowsExactly<ArgumentException>(() => output.PlayAsync(Stereo, new byte[3], default));
        _ = Assert.ThrowsExactly<ArgumentException>(() => output.PlayAsync(new(2, 96000, 16), new byte[4], default));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => output.PlayAsync(Stereo, new byte[4], new CancellationToken(true)));
        await output.PlayAsync(Stereo, [], default);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(device._opens);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => output.PlayAsync(Stereo, new byte[4], default));
    }

    [TestMethod]
    public void PlatformAudioImportsResolveActualExports()
    {
        Type backend;
        if (OperatingSystem.IsWindows())
        {
            backend = typeof(WaveOutDevice);
        }
        else if (OperatingSystem.IsMacOS())
        {
            backend = typeof(AudioQueueDevice);
        }
        else if (OperatingSystem.IsLinux())
        {
            backend = typeof(AlsaDevice);
        }
        else { Assert.Inconclusive("No native audio backend for this platform."); return; }

        Type native = backend.GetNestedType("Native", BindingFlags.NonPublic)!;
        foreach (MethodInfo method in native.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            LibraryImportAttribute? import = method.GetCustomAttribute<LibraryImportAttribute>();
            if (import is null)
            {
                continue;
            }

            nint library = NativeLibrary.Load(import.LibraryName);
            try
            {
                string entryPoint = import.EntryPoint ?? method.Name;
                Assert.IsTrue(NativeLibrary.TryGetExport(library, entryPoint, out _),
                    $"{import.LibraryName} does not export {entryPoint}.");
            }
            finally { NativeLibrary.Free(library); }
        }
    }

    [TestMethod]
    [TestCategory("NativeAudio")]
    public async Task SystemDefaultOutputPlaysAndReopens()
    {
        if (Environment.GetEnvironmentVariable("VELA_RDP_AUDIO_TEST") != "1")
        {
            Assert.Inconclusive("Set VELA_RDP_AUDIO_TEST=1 to exercise the system default playback device.");
            return;
        }
        using IAudioOutput output = AudioOutput.Create();
        foreach (AudioFormat format in new[] { Stereo, new AudioFormat(1, 8000, 8) })
        {
            byte[] silence = new byte[format.SampleRate * format.BlockAlign / 10];
            if (format.BitsPerSample == 8)
            {
                Array.Fill(silence, (byte)128);
            }

            var elapsed = Stopwatch.StartNew();
            await output.PlayAsync(format, silence, default).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50), elapsed.Elapsed,
                "Playback completion must not just mean that a native buffer was accepted.");
            Task[] continuous = [.. Enumerable.Range(0, 3).Select(_ => output.PlayAsync(format, silence, default))];
            await Task.WhenAll(continuous).WaitAsync(TimeSpan.FromSeconds(7));
            output.Close();
        }
        output.Dispose();
        await ((NativeAudioOutput)output).Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    [TestCategory("NativeAudio")]
    public async Task AlsaNullOutputPlaysRepeatsSwitchesFormatAndReopens()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("VELA_RDP_ALSA_NULL_TEST") != "1")
        {
            Assert.Inconclusive("Requires Linux and VELA_RDP_ALSA_NULL_TEST=1 with a process-local ALSA_CONFIG_PATH defining the default null PCM.");
            return;
        }

        using IAudioOutput output = AudioOutput.Create();
        try
        {
            for (int round = 0; round < 2; round++)
            {
                foreach (AudioFormat format in new[] { Stereo, new AudioFormat(1, 8000, 8) })
                {
                    byte[] silence = new byte[format.SampleRate * format.BlockAlign / 10];
                    if (format.BitsPerSample == 8)
                    {
                        Array.Fill(silence, (byte)128);
                    }

                    await output.PlayAsync(format, silence, default).WaitAsync(TimeSpan.FromSeconds(7));
                    await output.PlayAsync(format, silence, default).WaitAsync(TimeSpan.FromSeconds(7));
                }
                output.Close();
            }
        }
        finally
        {
            output.Dispose();
            await ((NativeAudioOutput)output).Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    [DataRow(-11)]
    [DataRow(-4)]
    public void AlsaUnderrunRetriesUnwrittenFramesWithoutReplayingAcceptedAudio(int retry)
    {
        var api = new FakeAlsaApi([2, -32, retry, 1]);
        var device = new AlsaDevice(api);
        byte[] samples = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        try
        {
            device.Open(Stereo);
            device.Start(samples);
            Assert.IsFalse(device.IsComplete()); // partial write
            Assert.IsFalse(device.IsComplete()); // underrun and prepare
            Assert.IsFalse(device.IsComplete()); // retryable write
            Assert.IsFalse(device.IsComplete()); // remaining frame is still buffered
            Assert.IsTrue(device.IsComplete());
            Assert.AreEqual(1, api._prepares);
            Assert.AreEqual(2, api._starts);
            Assert.AreSequenceEqual(samples, api._writes[0]);
            Assert.HasCount(4, api._writes);
            foreach (byte[] write in api._writes.Skip(1))
            {
                Assert.AreSequenceEqual(samples.AsSpan(8).ToArray(), write);
            }
        }
        finally { device.Close(); }
        Assert.IsTrue(api._closed);
    }

    [TestMethod]
    public void AlsaSuspendIsRecoveredLikeAnUnderrunInsteadOfEndingPlayback()
    {
        var api = new FakeAlsaApi([2, -86, 1]);
        var device = new AlsaDevice(api);
        byte[] samples = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        try
        {
            device.Open(Stereo);
            device.Start(samples);
            int polls = 0;
            while (!device.IsComplete() && polls++ < 10) { }
            Assert.IsLessThan(10, polls, "Playback must finish after the suspend.");
            Assert.AreEqual(1, api._prepares);
            Assert.HasCount(3, api._writes);
            Assert.AreSequenceEqual(samples.AsSpan(8).ToArray(), api._writes[2], "Only the unwritten frame is retried.");
        }
        finally { device.Close(); }
        Assert.IsTrue(api._closed);
    }

    [TestMethod]
    public void AlsaFeedsLaterBuffersBeforeCompletionAndRecoversTheirUnwrittenSuffix()
    {
        var api = new FakeAlsaApi([3, 1, -32, -11, 1]) { _delayResults = new Queue<int>([3, 1, 0]) };
        var device = new AlsaDevice(api);
        byte[] first = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        byte[] second = [12, 13, 14, 15, 16, 17, 18, 19];
        try
        {
            device.Open(Stereo);
            device.Start(first);
            device.Start(second);
            Assert.IsFalse(device.IsComplete());
            Assert.HasCount(2, api._writes, "The second buffer must be written while the first is still audible.");
            Assert.IsTrue(device.IsComplete()); // XRUN consumed the first buffer and one frame of the second.
            Assert.IsFalse(device.IsComplete());
            Assert.IsFalse(device.IsComplete());
            Assert.IsTrue(device.IsComplete());
            Assert.AreSequenceEqual(first, api._writes[0]);
            Assert.AreSequenceEqual(second, api._writes[1]);
            foreach (byte[] retry in api._writes.Skip(2))
            {
                Assert.AreSequenceEqual(second.AsSpan(4).ToArray(), retry);
            }

            Assert.AreEqual(1, api._prepares);
        }
        finally { device.Close(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlsaFatalWriteOrRecoveryFailureStopsPlaybackAndClosesDevice(bool duringRecovery)
    {
        var api = new FakeAlsaApi([duringRecovery ? -32 : -19]) { _prepareResult = -19 };
        using var output = new NativeAudioOutput(new AlsaDevice(api));
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(
            () => output.PlayAsync(Stereo, new byte[192], default));
        Assert.Contains("error -19", error.Message);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(api._closed);
        Assert.HasCount(1, api._writes);
    }

    private sealed class FakeAlsaApi(IEnumerable<int> results) : IAlsaApi
    {
        private readonly Queue<int> _results = new(results);
        private int _state = 2;
        private int _delays;
        internal readonly List<byte[]> _writes = [];
        internal int _prepares, _starts, _prepareResult;
        internal Queue<int>? _delayResults;
        internal bool _closed;
        public int Open(out nint pcm) { pcm = 1; return 0; }
        public int Configure(nint pcm, AudioFormat format) => 0;
        public int Prepare(nint pcm) { _prepares++; _state = 2; return _prepareResult; }
        public nint Write(nint pcm, nint data, nuint frames)
        {
            byte[] samples = new byte[checked((int)frames * Stereo.BlockAlign)];
            Marshal.Copy(data, samples, 0, samples.Length);
            _writes.Add(samples);
            int result = _results.Dequeue();
            if (result == -32)
            {
                _state = 4;
            }
            else if (result == -86)
            {
                _state = 7;   // SND_PCM_STATE_SUSPENDED
            }

            return result;
        }
        public int Start(nint pcm) { _starts++; _state = 3; return 0; }
        public int State(nint pcm) => _state;
        public int Delay(nint pcm, out nint frames) { frames = _delayResults?.Dequeue() ?? (_delays++ == 0 ? 1 : 0); return 0; }
        public int Drop(nint pcm) => 0;
        public int Close(nint pcm) { _closed = true; return 0; }
        public string Error(int code) => "injected ALSA failure";
    }

    private sealed class FakeDevice : IPcmDevice
    {
        internal readonly List<AudioFormat> _opens = [];
        internal readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource _multipleStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int _permittedCompletions;
        internal readonly TaskCompletionSource _openEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool _complete;
        internal ManualResetEventSlim? _blockOpen;
        internal Exception? _startError;
        internal int _starts;
        internal bool _opened;

        public void Open(AudioFormat format)
        {
            _ = _openEntered.TrySetResult();
            _ = (_blockOpen?.Wait(TimeSpan.FromSeconds(5)));
            _opens.Add(format);
            _opened = true;
        }
        public void Start(byte[] data)
        {
            if (_startError is not null)
            {
                throw _startError;
            }

            _starts++;
            _ = _started.TrySetResult();
            if (_starts >= 3)
            {
                _ = _multipleStarted.TrySetResult();
            }
        }
        public long PendingFrames => 0;
        public bool IsComplete()
        {
            if (_complete)
            {
                return true;
            }

            if (Volatile.Read(ref _permittedCompletions) <= 0)
            {
                return false;
            }

            _ = Interlocked.Decrement(ref _permittedCompletions);
            return true;
        }
        public void Reset() { }
        public void Close()
        {
            if (_opened)
            {
                _ = _closed.TrySetResult();
            }

            _opened = false;
        }
    }
}
