using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Audio;

internal sealed class AlsaDevice : IPcmDevice
{
    private const int Again = -11;
    private nint _pcm;
    private nint _samples;
    private int _length;
    private int _offset;
    private int _blockAlign;

    public void Open(AudioFormat format)
    {
        try
        {
            // SND_PCM_NONBLOCK applies to writes as well as opening the default PCM.
            // The system's default ALSA configuration selects PulseAudio/PipeWire when installed.
            Check(Native.snd_pcm_open(out _pcm, "default", 0, 1), "open default output");
            Check(Native.snd_pcm_set_params(_pcm, format.BitsPerSample == 8 ? 1 : 2,
                3, format.Channels, format.SampleRate, 1, 100_000), "configure PCM output");
            _blockAlign = format.BlockAlign;
        }
        catch (DllNotFoundException error)
        {
            throw new IOException("Linux audio requires libasound.so.2 and an ALSA default playback device (including a configured PulseAudio/PipeWire ALSA plugin).", error);
        }
    }

    public void Start(byte[] data)
    {
        int state = Native.snd_pcm_state(_pcm);
        // Hardware PCMs can underrun in the gap between packets; retain the handle and prepare again.
        if (state is 1 or 4) Check(Native.snd_pcm_prepare(_pcm), "prepare output after underrun");
        _samples = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, _samples, data.Length);
        _length = data.Length;
        _offset = 0;
    }

    public bool IsComplete()
    {
        if (_offset < _length)
        {
            nint written = Native.snd_pcm_writei(_pcm, _samples + _offset, (nuint)((_length - _offset) / _blockAlign));
            if (written == Again) return false;
            Check((int)written, "write PCM samples");
            _offset += checked((int)written * _blockAlign);
            if (written > 0 && Native.snd_pcm_state(_pcm) == 2)
                Check(Native.snd_pcm_start(_pcm), "start output");
            if (_offset < _length) return false;
        }
        // Delay includes samples still buffered for playback. Keep the PCM running between packets,
        // rather than draining to SETUP each time. XRUN after the last write means all frames played.
        int result = Native.snd_pcm_delay(_pcm, out nint remainingFrames);
        if (result == -32 && Native.snd_pcm_state(_pcm) == 4)
        {
            ReleaseBuffer();
            return true;
        }
        if (result == Again) return false;
        Check(result, "query playback delay");
        if (remainingFrames > 0) return false;
        ReleaseBuffer();
        return true;
    }

    public void Reset()
    {
        if (_pcm != 0) Check(Native.snd_pcm_drop(_pcm), "stop output");
        ReleaseBuffer();
    }

    public void Close()
    {
        if (_pcm != 0)
        {
            // snd_pcm_close releases the handle even when a plugin reports a cleanup error.
            nint pcm = _pcm;
            _pcm = 0;
            int dropped = Native.snd_pcm_drop(pcm);
            int closed = Native.snd_pcm_close(pcm);
            ReleaseBuffer();
            Check(dropped, "stop output");
            Check(closed, "close output");
        }
        else ReleaseBuffer();
    }

    private void ReleaseBuffer()
    {
        if (_samples != 0) Marshal.FreeHGlobal(_samples);
        _samples = 0;
        _length = _offset = 0;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw new IOException($"Linux ALSA could not {operation}: {Marshal.PtrToStringUTF8(Native.snd_strerror(result))} (error {result}). Check the system default playback device and its PulseAudio/PipeWire ALSA configuration.");
    }

    private static class Native
    {
        private const string Alsa = "libasound.so.2";
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_open(out nint pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_prepare(nint pcm);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint snd_pcm_writei(nint pcm, nint data, nuint frames);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_start(nint pcm);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_state(nint pcm);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_delay(nint pcm, out nint delay);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_drop(nint pcm);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int snd_pcm_close(nint pcm);
        [DllImport(Alsa, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint snd_strerror(int error);
    }
}
