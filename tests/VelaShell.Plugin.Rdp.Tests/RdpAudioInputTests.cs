using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpAudioInputTests
{
    [TestMethod]
    public void AudioInputIsDisabledByDefaultAndDoesNotChangePlaybackChannels()
    {
        var options = new RdpClientOptions { Host = "example", DynamicResolution = false };

        Assert.IsNull(options.AudioInput);
        Assert.IsFalse(options.AudioPlayback);
        Assert.IsEmpty(RdpManagedSession.BuildChannels(options));
    }

    [TestMethod]
    public async Task RequestedAudioInputFailsBeforeNetworkSetup()
    {
        var options = new RdpClientOptions
        {
            Host = "invalid.example",
            AudioInput = new RdpAudioInputOptions()
        };

        RdpAudioInputNotSupportedException error = await Assert.ThrowsAsync<RdpAudioInputNotSupportedException>(
            () => RdpManagedSession.ConnectAsync(options));
        Assert.Contains("no audio-input channel or capture device was opened", error.Message);
    }

    [TestMethod]
    public async Task InvalidAudioInputFormatIsRejectedBeforeUnsupportedResult()
    {
        var options = new RdpClientOptions
        {
            Host = "invalid.example",
            AudioInput = new RdpAudioInputOptions { Channels = 3 }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RdpManagedSession.ConnectAsync(options));
    }

    [TestMethod]
    public void AudioInputFormatAcceptsBoundedPcmValues()
    {
        var options = new RdpAudioInputOptions { Channels = 2, SampleRate = 48_000, BitsPerSample = 8 };

        options.Validate();
    }
}
