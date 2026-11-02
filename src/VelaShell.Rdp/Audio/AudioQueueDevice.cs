using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Audio;

internal sealed partial class AudioQueueDevice : IPcmDevice
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private nint _queue;
    private nint _buffer;
    private uint _capacity;
    private int _blockAlign;
    private int _returned;
    private bool _started;
    private double _endSampleTime;
    private GCHandle _callbackRoot;
    private static readonly int AudioDataOffset = (int)Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.AudioData));
    private static readonly int ByteSizeOffset = (int)Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.ByteSize));

    public unsafe void Open(AudioFormat format)
    {
        var description = new StreamDescription
        {
            SampleRate = format.SampleRate,
            FormatId = 0x6C70636D, // 'lpcm'
            FormatFlags = 8u | (format.BitsPerSample == 16 ? 4u : 0u), // packed, signed for PCM16
            BytesPerPacket = (uint)format.BlockAlign,
            FramesPerPacket = 1,
            BytesPerFrame = (uint)format.BlockAlign,
            ChannelsPerFrame = format.Channels,
            BitsPerChannel = format.BitsPerSample
        };
        _blockAlign = format.BlockAlign;
        // A native callback must keep the collectible plugin alive until AudioQueueDispose has joined it.
        _callbackRoot = GCHandle.Alloc(this);
        try
        {
            Check(Native.AudioQueueNewOutput(ref description, &BufferReturned, GCHandle.ToIntPtr(_callbackRoot),
                0, 0, 0, out _queue), "open default output");
        }
        catch
        {
            if (_queue == 0) _callbackRoot.Free();
            throw;
        }
    }

    public void Start(byte[] data)
    {
        if (_capacity < data.Length)
        {
            if (_buffer != 0)
            {
                Check(Native.AudioQueueFreeBuffer(_queue, _buffer), "release PCM buffer");
                _buffer = 0;
                _capacity = 0;
            }
            Check(Native.AudioQueueAllocateBuffer(_queue, (uint)data.Length, out _buffer), "allocate PCM buffer");
            _capacity = (uint)data.Length;
        }
        Marshal.Copy(data, 0, Marshal.ReadIntPtr(_buffer, AudioDataOffset), data.Length);
        Marshal.WriteInt32(_buffer, ByteSizeOffset, data.Length);
        Volatile.Write(ref _returned, 0);
        Check(Native.AudioQueueEnqueueBufferWithParameters(_queue, _buffer, 0, 0, 0, 0, 0, 0, 0,
            out AudioTimeStamp start), "submit PCM buffer");
        if ((start.Flags & 1) == 0)
            throw new IOException("macOS AudioQueue did not provide a PCM playback start position.");
        _endSampleTime = start.SampleTime + data.Length / _blockAlign;
        if (!_started)
        {
            Check(Native.AudioQueueStart(_queue, 0), "start output");
            _started = true;
        }
    }

    public bool IsComplete()
    {
        // Buffer-return callbacks mean reusable memory, not audible completion. Compare the actual
        // scheduled end position with the queue's playback clock, leaving the queue running.
        if (Volatile.Read(ref _returned) == 0) return false;
        Check(Native.AudioQueueGetCurrentTime(_queue, 0, out AudioTimeStamp time, 0), "query playback position");
        return (time.Flags & 1) != 0 && time.SampleTime >= _endSampleTime;
    }

    public void Reset()
    {
        if (_queue != 0) Check(Native.AudioQueueStop(_queue, 1), "stop output");
        _started = false;
    }

    public void Close()
    {
        if (_queue != 0)
        {
            Check(Native.AudioQueueDispose(_queue, 1), "close output");
            _queue = _buffer = 0;
            _capacity = 0;
            _started = false;
        }
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BufferReturned(nint userData, nint queue, nint buffer)
    {
        if (GCHandle.FromIntPtr(userData).Target is AudioQueueDevice device)
            Volatile.Write(ref device._returned, 1);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
            throw new IOException($"macOS AudioQueue could not {operation}: OSStatus {result} (0x{result:X8}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        internal double SampleRate;
        internal uint FormatId;
        internal uint FormatFlags;
        internal uint BytesPerPacket;
        internal uint FramesPerPacket;
        internal uint BytesPerFrame;
        internal uint ChannelsPerFrame;
        internal uint BitsPerChannel;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer
    {
        internal uint Capacity;
        internal nint AudioData;
        internal uint ByteSize;
        internal nint UserData;
        internal uint PacketDescriptionCapacity;
        internal nint PacketDescriptions;
        internal uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioTimeStamp
    {
        internal double SampleTime;
        internal ulong HostTime;
        internal double RateScalar;
        internal ulong WordClockTime;
        internal SmpteTime Smpte;
        internal uint Flags;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmpteTime
    {
        internal short Subframes;
        internal short SubframeDivisor;
        internal uint Counter;
        internal uint Type;
        internal uint Flags;
        internal short Hours;
        internal short Minutes;
        internal short Seconds;
        internal short Frames;
    }

    private static partial class Native
    {
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int AudioQueueNewOutput(ref StreamDescription format,
            delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint userData,
            nint runLoop, nint runLoopMode, uint flags, out nint queue);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueAllocateBuffer(nint queue, uint size, out nint buffer);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueFreeBuffer(nint queue, nint buffer);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueEnqueueBufferWithParameters(nint queue, nint buffer,
            uint packetCount, nint packetDescriptions, uint trimStart, uint trimEnd, uint parameterCount,
            nint parameters, nint startTime, out AudioTimeStamp actualStartTime);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueStart(nint queue, nint startTime);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueStop(nint queue, byte immediate);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueGetCurrentTime(nint queue, nint timeline, out AudioTimeStamp time, nint discontinuity);
        [LibraryImport(AudioToolbox)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AudioQueueDispose(nint queue, byte immediate);
    }
}
