namespace VelaShell.Rdp;

/// <summary>Requested local microphone format for future RDP audio-input redirection.</summary>
/// <remarks>
/// Audio input is intentionally opt-in. The managed client currently validates this contract and fails
/// before connecting because MS-RDPEAI capture and platform backends are not implemented yet.
/// </remarks>
public sealed record RdpAudioInputOptions
{
    /// <summary>Number of PCM channels. Mono and stereo are supported by the contract.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>PCM sample rate in hertz.</summary>
    public int SampleRate { get; init; } = 16_000;

    /// <summary>PCM bits per sample.</summary>
    public int BitsPerSample { get; init; } = 16;

    internal void Validate()
    {
        if (Channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "RDP audio input supports one or two channels.");
        }

        if (SampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "RDP audio input supports 8000 to 48000 Hz.");
        }

        if (BitsPerSample is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(BitsPerSample), BitsPerSample, "RDP audio input supports 8-bit or 16-bit PCM.");
        }
    }
}
