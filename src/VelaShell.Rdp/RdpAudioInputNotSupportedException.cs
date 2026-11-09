namespace VelaShell.Rdp;

/// <summary>Raised when microphone redirection is requested before the managed implementation is available.</summary>
public sealed class RdpAudioInputNotSupportedException : PlatformNotSupportedException
{
    internal RdpAudioInputNotSupportedException()
        : base("RDP microphone capture is not supported by the managed client yet; no audio-input channel or capture device was opened.")
    {
    }
}
