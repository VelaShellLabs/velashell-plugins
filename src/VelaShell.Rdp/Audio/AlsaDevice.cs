using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Audio;

internal sealed partial class AlsaDevice(IAlsaApi? api = null) : IPcmDevice
{
    private const int Again = -11, Interrupted = -4, Underrun = -32;
    private readonly IAlsaApi _api = api ?? new SystemAlsaApi();
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
            Check(_api.Open(out _pcm), "open default output");
            Check(_api.Configure(_pcm, format), "configure PCM output");
            _blockAlign = format.BlockAlign;
        }
        catch (DllNotFoundException error)
        {
            throw new IOException("Linux audio requires libasound.so.2 and an ALSA default playback device (including a configured PulseAudio/PipeWire ALSA plugin).", error);
        }
    }

    public void Start(byte[] data)
    {
        int state = _api.State(_pcm);
        // Hardware PCMs can underrun in the gap between packets; retain the handle and prepare again.
        if (state is 1 or 4) Check(_api.Prepare(_pcm), "prepare output after underrun");
        _samples = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, _samples, data.Length);
        _length = data.Length;
        _offset = 0;
    }

    public bool IsComplete()
    {
        if (_offset < _length)
        {
            nint written = _api.Write(_pcm, _samples + _offset, (nuint)((_length - _offset) / _blockAlign));
            if (written is Again or Interrupted) return false;
            if (written == Underrun)
            {
                // XRUN can occur after Start's state check or between partial writes. Frames already
                // accepted were consumed before the underrun; retry only the unwritten suffix.
                Check(_api.Prepare(_pcm), "recover output after underrun");
                return false;
            }
            Check((int)written, "write PCM samples");
            _offset += checked((int)written * _blockAlign);
            if (written > 0 && _api.State(_pcm) == 2)
                Check(_api.Start(_pcm), "start output");
            if (_offset < _length) return false;
        }
        // Delay includes samples still buffered for playback. Keep the PCM running between packets,
        // rather than draining to SETUP each time. XRUN after the last write means all frames played.
        int result = _api.Delay(_pcm, out nint remainingFrames);
        if (result == Underrun && _api.State(_pcm) == 4)
        {
            ReleaseBuffer();
            return true;
        }
        if (result is Again or Interrupted) return false;
        Check(result, "query playback delay");
        if (remainingFrames > 0) return false;
        ReleaseBuffer();
        return true;
    }

    public void Reset()
    {
        if (_pcm != 0) Check(_api.Drop(_pcm), "stop output");
        ReleaseBuffer();
    }

    public void Close()
    {
        if (_pcm != 0)
        {
            // snd_pcm_close releases the handle even when a plugin reports a cleanup error.
            nint pcm = _pcm;
            _pcm = 0;
            int dropped = _api.Drop(pcm);
            int closed = _api.Close(pcm);
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

    private void Check(int result, string operation)
    {
        if (result < 0)
            throw new IOException($"Linux ALSA could not {operation}: {_api.Error(result)} (error {result}). Check the system default playback device and its PulseAudio/PipeWire ALSA configuration.");
    }

    private sealed class SystemAlsaApi : IAlsaApi
    {
        public int Open(out nint pcm) => Native.snd_pcm_open(out pcm, "default", 0, 1);
        public int Configure(nint pcm, AudioFormat format) => Native.snd_pcm_set_params(pcm,
            format.BitsPerSample == 8 ? 1 : 2, 3, format.Channels, format.SampleRate, 1, 100_000);
        public int Prepare(nint pcm) => Native.snd_pcm_prepare(pcm);
        public nint Write(nint pcm, nint data, nuint frames) => Native.snd_pcm_writei(pcm, data, frames);
        public int Start(nint pcm) => Native.snd_pcm_start(pcm);
        public int State(nint pcm) => Native.snd_pcm_state(pcm);
        public int Delay(nint pcm, out nint frames) => Native.snd_pcm_delay(pcm, out frames);
        public int Drop(nint pcm) => Native.snd_pcm_drop(pcm);
        public int Close(nint pcm) => Native.snd_pcm_close(pcm);
        public string? Error(int code) => Marshal.PtrToStringUTF8(Native.snd_strerror(code));
    }

    private static partial class Native
    {
        private const string Alsa = "libasound.so.2";
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_open(out nint pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_prepare(nint pcm);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint snd_pcm_writei(nint pcm, nint data, nuint frames);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_start(nint pcm);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_state(nint pcm);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_delay(nint pcm, out nint delay);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_drop(nint pcm);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial int snd_pcm_close(nint pcm);
        [LibraryImport(Alsa)]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        internal static partial nint snd_strerror(int error);
    }
}

internal interface IAlsaApi
{
    int Open(out nint pcm);
    int Configure(nint pcm, AudioFormat format);
    int Prepare(nint pcm);
    nint Write(nint pcm, nint data, nuint frames);
    int Start(nint pcm);
    int State(nint pcm);
    int Delay(nint pcm, out nint frames);
    int Drop(nint pcm);
    int Close(nint pcm);
    string? Error(int code);
}
