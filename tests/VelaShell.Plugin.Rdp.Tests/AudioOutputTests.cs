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
        var device = new FakeDevice { Complete = true };
        using var output = new NativeAudioOutput(device);
        await output.PlayAsync(Stereo, new byte[192], default);
        await output.PlayAsync(Stereo, new byte[192], default);
        Assert.AreEqual(1, device.Opens.Count);
        var mono8 = new AudioFormat(1, 8000, 8);
        await output.PlayAsync(mono8, new byte[80], default);
        Assert.AreSequenceEqual(new[] { Stereo, mono8 }, device.Opens);
        Assert.AreEqual(3, device.Starts);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(device.Opened);
    }

    [TestMethod]
    public async Task PlaybackWaitsForDeviceCompletionAndCloseCancelsThenReopens()
    {
        var device = new FakeDevice();
        using var output = new NativeAudioOutput(device);
        Task play = output.PlayAsync(Stereo, new byte[192], default);
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(play.IsCompleted);
        output.Close();
        await Assert.ThrowsAsync<OperationCanceledException>(() => play);
        device.Complete = true;
        await output.PlayAsync(Stereo, new byte[192], default);
        Assert.AreEqual(2, device.Opens.Count);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CancellationAndDisposalDoNotWaitForANativeCall()
    {
        using var nativeCall = new ManualResetEventSlim();
        var device = new FakeDevice { BlockOpen = nativeCall };
        using var output = new NativeAudioOutput(device);
        using var cancel = new CancellationTokenSource();
        try
        {
            Task play = output.PlayAsync(Stereo, new byte[192], cancel.Token);
            await device.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => play.WaitAsync(TimeSpan.FromSeconds(1)));
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
        Assert.AreEqual(0, device.Starts);
        Assert.IsFalse(device.Opened);
    }

    [TestMethod]
    public async Task StalledPlaybackTimesOutAndDeviceErrorsAreReported()
    {
        var stalled = new FakeDevice();
        using var output = new NativeAudioOutput(stalled, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => output.PlayAsync(Stereo, new byte[192], default));
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(stalled.Opened);

        var broken = new FakeDevice { StartError = new IOException("native device disconnected") };
        using var failed = new NativeAudioOutput(broken);
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => failed.PlayAsync(Stereo, new byte[192], default));
        Assert.AreEqual("native device disconnected", error.Message);
        failed.Dispose();
        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(broken.Opened);
    }

    [TestMethod]
    public async Task TimeoutDuringNativeOpenDoesNotPlayAfterTheCallReturns()
    {
        using var nativeCall = new ManualResetEventSlim();
        var device = new FakeDevice { BlockOpen = nativeCall };
        using var output = new NativeAudioOutput(device, TimeSpan.FromMilliseconds(100));
        try
        {
            Task play = output.PlayAsync(Stereo, new byte[192], default);
            await device.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => play);
            nativeCall.Set();
            await device.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(0, device.Starts);
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
        Assert.ThrowsExactly<ArgumentException>(() => output.PlayAsync(Stereo, new byte[3], default));
        Assert.ThrowsExactly<ArgumentException>(() => output.PlayAsync(new(2, 96000, 16), new byte[4], default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => output.PlayAsync(Stereo, new byte[4], new CancellationToken(true)));
        await output.PlayAsync(Stereo, [], default);
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(device.Opens);
        Assert.ThrowsExactly<ObjectDisposedException>(() => output.PlayAsync(Stereo, new byte[4], default));
    }

    [TestMethod]
    public void PlatformAudioImportsResolveActualExports()
    {
        Type backend;
        if (OperatingSystem.IsWindows()) backend = typeof(WaveOutDevice);
        else if (OperatingSystem.IsMacOS()) backend = typeof(AudioQueueDevice);
        else if (OperatingSystem.IsLinux()) backend = typeof(AlsaDevice);
        else { Assert.Inconclusive("No native audio backend for this platform."); return; }

        Type native = backend.GetNestedType("Native", BindingFlags.NonPublic)!;
        foreach (MethodInfo method in native.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            LibraryImportAttribute? import = method.GetCustomAttribute<LibraryImportAttribute>();
            if (import is null) continue;
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
            if (format.BitsPerSample == 8) Array.Fill(silence, (byte)128);
            var elapsed = Stopwatch.StartNew();
            await output.PlayAsync(format, silence, default).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50), elapsed.Elapsed,
                "Playback completion must not just mean that a native buffer was accepted.");
            await output.PlayAsync(format, silence, default).WaitAsync(TimeSpan.FromSeconds(7));
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
                    if (format.BitsPerSample == 8) Array.Fill(silence, (byte)128);
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
            Assert.AreEqual(1, api.Prepares);
            Assert.AreEqual(2, api.Starts);
            Assert.AreSequenceEqual(samples, api.Writes[0]);
            Assert.HasCount(4, api.Writes);
            foreach (byte[] write in api.Writes.Skip(1))
                Assert.AreSequenceEqual(samples.AsSpan(8).ToArray(), write);
        }
        finally { device.Close(); }
        Assert.IsTrue(api.Closed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlsaFatalWriteOrRecoveryFailureStopsPlaybackAndClosesDevice(bool duringRecovery)
    {
        var api = new FakeAlsaApi([duringRecovery ? -32 : -19]) { PrepareResult = -19 };
        using var output = new NativeAudioOutput(new AlsaDevice(api));
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(
            () => output.PlayAsync(Stereo, new byte[192], default));
        StringAssert.Contains(error.Message, "error -19");
        output.Dispose();
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(api.Closed);
        Assert.HasCount(1, api.Writes);
    }

    private sealed class FakeAlsaApi(IEnumerable<int> results) : IAlsaApi
    {
        private readonly Queue<int> _results = new(results);
        private int _state = 2;
        private int _delays;
        internal readonly List<byte[]> Writes = [];
        internal int Prepares, Starts, PrepareResult;
        internal bool Closed;
        public int Open(out nint pcm) { pcm = 1; return 0; }
        public int Configure(nint pcm, AudioFormat format) => 0;
        public int Prepare(nint pcm) { Prepares++; _state = 2; return PrepareResult; }
        public nint Write(nint pcm, nint data, nuint frames)
        {
            byte[] samples = new byte[checked((int)frames * Stereo.BlockAlign)];
            Marshal.Copy(data, samples, 0, samples.Length);
            Writes.Add(samples);
            int result = _results.Dequeue();
            if (result == -32) _state = 4;
            return result;
        }
        public int Start(nint pcm) { Starts++; _state = 3; return 0; }
        public int State(nint pcm) => _state;
        public int Delay(nint pcm, out nint frames) { frames = _delays++ == 0 ? 1 : 0; return 0; }
        public int Drop(nint pcm) => 0;
        public int Close(nint pcm) { Closed = true; return 0; }
        public string Error(int code) => "injected ALSA failure";
    }

    private sealed class FakeDevice : IPcmDevice
    {
        internal readonly List<AudioFormat> Opens = [];
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Complete;
        internal ManualResetEventSlim? BlockOpen;
        internal Exception? StartError;
        internal int Starts;
        internal bool Opened;

        public void Open(AudioFormat format)
        {
            OpenEntered.TrySetResult();
            BlockOpen?.Wait(TimeSpan.FromSeconds(5));
            Opens.Add(format);
            Opened = true;
        }
        public void Start(byte[] data)
        {
            if (StartError is not null) throw StartError;
            Starts++;
            Started.TrySetResult();
        }
        public bool IsComplete() => Complete;
        public void Reset() { }
        public void Close()
        {
            if (Opened) Closed.TrySetResult();
            Opened = false;
        }
    }
}
