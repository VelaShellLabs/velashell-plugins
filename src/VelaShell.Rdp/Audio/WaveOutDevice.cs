using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Audio;

internal sealed partial class WaveOutDevice : IPcmDevice
{
    private nint _handle;
    private readonly Queue<Buffer> _buffers = new();
    private readonly Stack<Buffer> _free = new();
    private long _submittedFrames;
    private long _playedFrames;
    private uint _lastPosition;
    private int _blockAlign;
    private uint _sampleRate;
    private long _positionRemainder;

    private sealed class Buffer
    {
        internal nint Header, Samples;
        internal int Capacity;
        internal bool Prepared;
    }
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
        _blockAlign = format.BlockAlign;
        _sampleRate = format.SampleRate;
        _submittedFrames = _playedFrames = _positionRemainder = 0;
        _lastPosition = 0;
    }

    public void Start(byte[] data)
    {
        Buffer buffer = _free.TryPop(out Buffer? reusable) ? reusable : new Buffer();
        _buffers.Enqueue(buffer);
        if (buffer.Capacity < data.Length)
        {
            if (buffer.Samples != 0)
            {
                Marshal.FreeHGlobal(buffer.Samples);
            }

            buffer.Samples = 0;
            buffer.Samples = Marshal.AllocHGlobal(data.Length);
            buffer.Capacity = data.Length;
        }
        if (buffer.Header == 0)
        {
            buffer.Header = Marshal.AllocHGlobal((int)HeaderSize);
        }

        Marshal.Copy(data, 0, buffer.Samples, data.Length);
        Marshal.StructureToPtr(new WaveHeader { Data = buffer.Samples, BufferLength = (uint)data.Length }, buffer.Header, false);
        Check(Native.waveOutPrepareHeader(_handle, buffer.Header, HeaderSize), "prepare PCM buffer");
        buffer.Prepared = true;
        Check(Native.waveOutWrite(_handle, buffer.Header, HeaderSize), "submit PCM buffer");
        _submittedFrames += data.Length / _blockAlign;
    }

    public long PendingFrames
    {
        get
        {
            var time = new MultimediaTime { Type = 2 }; // TIME_SAMPLES
            Check(Native.waveOutGetPosition(_handle, ref time, 12), "query playback position");
            long delta = unchecked(time.Value - _lastPosition);
            long unitsPerFrame = time.Type switch
            {
                1 => 1000, // TIME_MS: drivers may substitute a supported clock representation.
                2 => 1,
                4 => _blockAlign,
                _ => throw new IOException("Windows audio did not provide a supported playback position.")
            };
            long numerator = (time.Type == 1 ? delta * _sampleRate : delta) + _positionRemainder;
            _playedFrames += numerator / unitsPerFrame;
            _positionRemainder = numerator % unitsPerFrame;
            _lastPosition = time.Value;
            return Math.Max(0, _submittedFrames - _playedFrames);
        }
    }

    public bool IsComplete()
    {
        if (!_buffers.TryPeek(out Buffer? buffer) || (Marshal.ReadInt32(buffer.Header, FlagsOffset) & 1) == 0)
        {
            return false;
        }

        Unprepare(buffer);
        _ = _buffers.Dequeue();
        _free.Push(buffer);
        return true;
    }

    public void Reset()
    {
        if (_handle != 0)
        {
            Check(Native.waveOutReset(_handle), "stop output");
        }

        while (_buffers.TryPeek(out Buffer? buffer))
        {
            Unprepare(buffer);
            _ = _buffers.Dequeue();
            _free.Push(buffer);
        }
        _submittedFrames = _playedFrames = _positionRemainder = 0;
        _lastPosition = 0;
    }

    public void Close()
    {
        Reset();
        while (_free.TryPop(out Buffer? buffer))
        {
            if (buffer.Header != 0)
            {
                Marshal.FreeHGlobal(buffer.Header);
            }

            if (buffer.Samples != 0)
            {
                Marshal.FreeHGlobal(buffer.Samples);
            }
        }
        if (_handle == 0)
        {
            return;
        }

        Check(Native.waveOutClose(_handle), "close output");
        _handle = 0;
    }

    private void Unprepare(Buffer buffer)
    {
        if (!buffer.Prepared)
        {
            return;
        }
        // A failed unprepare must retain memory that the driver could still own.
        Check(Native.waveOutUnprepareHeader(_handle, buffer.Header, HeaderSize), "release PCM buffer");
        buffer.Prepared = false;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint Value;
    }

    private static unsafe void Check(uint result, string operation)
    {
        if (result == 0)
        {
            return;
        }

        Span<char> message = stackalloc char[256];
        message.Clear();
        fixed (char* pointer = message)
        {
            _ = Native.waveOutGetErrorTextW(result, pointer, (uint)message.Length);
        }
        int terminator = message.IndexOf('\0');
        string text = new(terminator < 0 ? message : message[..terminator]);
        throw new IOException($"Windows audio could not {operation}: {text} (MMRESULT {result}).");
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

    private static partial class Native
    {
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutOpen(out nint handle, uint deviceId, ref WaveFormat format, nuint callback, nuint instance, uint flags);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutPrepareHeader(nint handle, nint header, uint size);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutWrite(nint handle, nint header, uint size);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutUnprepareHeader(nint handle, nint header, uint size);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutGetPosition(nint handle, ref MultimediaTime time, uint size);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutReset(nint handle);
        [LibraryImport("winmm.dll")]
        internal static partial uint waveOutClose(nint handle);
        [LibraryImport("winmm.dll")]
        internal static unsafe partial uint waveOutGetErrorTextW(uint error, char* text, uint length);
    }
}
