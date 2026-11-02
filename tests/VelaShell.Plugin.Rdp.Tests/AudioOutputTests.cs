using System.Diagnostics;
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
