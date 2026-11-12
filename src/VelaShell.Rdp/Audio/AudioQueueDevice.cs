using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Audio;

internal sealed partial class AudioQueueDevice : IPcmDevice
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private nint _queue;
    private readonly Queue<Buffer> _buffers = new();
    private readonly Stack<Buffer> _free = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Buffer> _owned = new();
    private int _blockAlign;
    private sealed class Buffer(nint pointer, uint capacity)
    {
        internal readonly nint Pointer = pointer;
        internal readonly uint Capacity = capacity;
        internal double EndSampleTime;
        internal int Returned;
    }
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
        // A native callback must keep the collectible plugin alive until AudioQueueDispose has joined it. After a
        // failed dispose the old queue may still call back through the existing root, so it is reused rather than
        // replaced (replacing it leaked one handle per reopen) and never freed early.
        bool allocated = !_callbackRoot.IsAllocated;
        if (allocated)
        {
            _callbackRoot = GCHandle.Alloc(this);
        }

        try
        {
            Check(Native.AudioQueueNewOutput(ref description, &BufferReturned, GCHandle.ToIntPtr(_callbackRoot),
                0, 0, 0, out _queue), "open default output");
        }
        catch
        {
            if (_queue == 0 && allocated)
            {
                _callbackRoot.Free();
            }

            throw;
        }
    }

    public void Start(byte[] data)
    {
        _ = _free.TryPop(out Buffer? buffer);
        if (buffer is not null && buffer.Capacity < data.Length)
        {
            Check(Native.AudioQueueFreeBuffer(_queue, buffer.Pointer), "release PCM buffer");
            _ = _owned.TryRemove(buffer.Pointer, out _);
            buffer = null;
        }
        if (buffer is null)
        {
            Check(Native.AudioQueueAllocateBuffer(_queue, (uint)data.Length, out nint pointer), "allocate PCM buffer");
            buffer = new Buffer(pointer, (uint)data.Length);
            _owned[pointer] = buffer;
        }
        _buffers.Enqueue(buffer);
        Marshal.Copy(data, 0, Marshal.ReadIntPtr(buffer.Pointer, AudioDataOffset), data.Length);
        Marshal.WriteInt32(buffer.Pointer, ByteSizeOffset, data.Length);
        Volatile.Write(ref buffer.Returned, 0);
        Check(Native.AudioQueueEnqueueBufferWithParameters(_queue, buffer.Pointer, 0, 0, 0, 0, 0, 0, 0,
            out AudioTimeStamp start), "submit PCM buffer");
        if ((start.Flags & 1) == 0)
        {
            throw new IOException("macOS AudioQueue did not provide a PCM playback start position.");
        }

        buffer.EndSampleTime = _endSampleTime = start.SampleTime + data.Length / _blockAlign;
        if (!_started)
        {
            Check(Native.AudioQueueStart(_queue, 0), "start output");
            _started = true;
        }
    }

    private double PlaybackPosition()
    {
        Check(Native.AudioQueueGetCurrentTime(_queue, 0, out AudioTimeStamp time, 0), "query playback position");
        return (time.Flags & 1) != 0 ? time.SampleTime : 0;
    }

    public long PendingFrames => _started ? (long)Math.Max(0, _endSampleTime - PlaybackPosition()) : 0;

    public bool IsComplete()
    {
        // Returned memory alone is not audible completion; keep each scheduled end position.
        if (!_buffers.TryPeek(out Buffer? buffer) || Volatile.Read(ref buffer.Returned) == 0 ||
            PlaybackPosition() < buffer.EndSampleTime)
        {
            return false;
        }

        _ = _buffers.Dequeue();
        _free.Push(buffer);
        return true;
    }

    public void Reset()
    {
        if (_queue != 0)
        {
            Check(Native.AudioQueueStop(_queue, 1), "stop output");
        }

        _started = false;
    }

    public void Close()
    {
        if (_queue != 0)
        {
            Check(Native.AudioQueueDispose(_queue, 1), "close output");
            _queue = 0;
            _buffers.Clear();
            _free.Clear();
            _owned.Clear();
            _endSampleTime = 0;
            _started = false;
        }
        if (_callbackRoot.IsAllocated)
        {
            _callbackRoot.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BufferReturned(nint userData, nint queue, nint buffer)
    {
        if (GCHandle.FromIntPtr(userData).Target is AudioQueueDevice device && device._owned.TryGetValue(buffer, out Buffer? owner))
        {
            Volatile.Write(ref owner.Returned, 1);
        }
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new IOException($"macOS AudioQueue could not {operation}: OSStatus {result} (0x{result:X8}).");
        }
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
