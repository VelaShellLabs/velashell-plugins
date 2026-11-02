using System.Runtime.InteropServices;
using System.Text;

namespace VelaShell.Rdp.Audio;

internal sealed class WaveOutDevice : IPcmDevice
{
    private nint _handle;
    private nint _header;
    private nint _samples;
    private bool _prepared;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    public void Open(AudioFormat format)
    {
        var waveFormat = new WaveFormat
        {
            FormatTag = 1,
            Channels = format.Channels,
            SamplesPerSecond = format.SampleRate,
            AverageBytesPerSecond = format.SampleRate * (uint)format.BlockAlign,
            BlockAlign = (ushort)format.BlockAlign,
            BitsPerSample = format.BitsPerSample
        };
        Check(Native.waveOutOpen(out _handle, uint.MaxValue, ref waveFormat, 0, 0, 0), "open default output");
    }

    public void Start(byte[] data)
    {
        _samples = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, _samples, data.Length);
        _header = Marshal.AllocHGlobal((int)HeaderSize);
        Marshal.StructureToPtr(new WaveHeader { Data = _samples, BufferLength = (uint)data.Length }, _header, false);
        Check(Native.waveOutPrepareHeader(_handle, _header, HeaderSize), "prepare PCM buffer");
        _prepared = true;
        Check(Native.waveOutWrite(_handle, _header, HeaderSize), "submit PCM buffer");
    }

    public bool IsComplete()
    {
        if ((Marshal.ReadInt32(_header, FlagsOffset) & 1) == 0) return false; // WHDR_DONE
        ReleaseBuffer();
        return true;
    }

    public void Reset()
    {
        if (_handle != 0) Check(Native.waveOutReset(_handle), "stop output");
        ReleaseBuffer();
    }

    public void Close()
    {
        Reset();
        if (_handle == 0) return;
        Check(Native.waveOutClose(_handle), "close output");
        _handle = 0;
    }

    private void ReleaseBuffer()
    {
        if (_prepared)
        {
            // Do not release memory if a broken driver still owns the buffer.
            Check(Native.waveOutUnprepareHeader(_handle, _header, HeaderSize), "release PCM buffer");
            _prepared = false;
        }
        if (_header != 0) Marshal.FreeHGlobal(_header);
        if (_samples != 0) Marshal.FreeHGlobal(_samples);
        _header = _samples = 0;
    }

    private static void Check(uint result, string operation)
    {
        if (result == 0) return;
        var message = new StringBuilder(256);
        _ = Native.waveOutGetErrorTextW(result, message, (uint)message.Capacity);
        throw new IOException($"Windows audio could not {operation}: {message} (MMRESULT {result}).");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal nint Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal nuint User;
        internal uint Flags;
        internal uint Loops;
        internal nint Next;
        internal nuint Reserved;
    }

    private static class Native
    {
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutOpen(out nint handle, uint deviceId, ref WaveFormat format, nuint callback, nuint instance, uint flags);
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutPrepareHeader(nint handle, nint header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutWrite(nint handle, nint header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutUnprepareHeader(nint handle, nint header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutReset(nint handle);
        [DllImport("winmm.dll", ExactSpelling = true)]
        internal static extern uint waveOutClose(nint handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint waveOutGetErrorTextW(uint error, StringBuilder text, uint length);
    }
}
