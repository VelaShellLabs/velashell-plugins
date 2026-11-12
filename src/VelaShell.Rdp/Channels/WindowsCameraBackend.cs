using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Rdp.Channels;

/// <summary>Enumerates camera identities without activating a source or reading any samples.</summary>
public static class RdpCameraDevices
{
    public static IReadOnlyList<RdpCameraDevice> Enumerate() => OperatingSystem.IsWindows()
        ? WindowsCameraBackend.Enumerate() : [];
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCameraBackend(string deviceId) : ICameraBackend
{
    private readonly Lock _gate = new();
    private readonly ReaderCallback _callback = new();
    private readonly List<(CameraMediaType Type, nint Native)> _types = [];
    private nint _activate, _source, _reader;
    private bool _started, _mfStarted;

    public IReadOnlyList<CameraMediaType> MediaTypes => [.. _types.Select(static x => x.Type)];

    [AllowNull]
    public CameraMediaType CurrentMediaType { get => field ?? throw new InvalidOperationException("Camera is inactive."); private set; }

    internal static IReadOnlyList<RdpCameraDevice> Enumerate()
    {
        using var apartment = new MfNative.Apartment();
        MfNative.Check(MfNative.MFStartup(0x20070, 0));
        try
        {
            List<RdpCameraDevice> result = [];
            MfNative.ForEachDevice(activation => result.Add(new(MfNative.GetString(activation, MfNative.SymbolicLink), MfNative.GetString(activation, MfNative.FriendlyName))));
            return result;
        }
        finally { _ = MfNative.MFShutdown(); }
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (_reader != 0)
            {
                throw new InvalidOperationException("Camera is already active.");
            }

            using var apartment = new MfNative.Apartment();
            try
            {
                MfNative.Check(MfNative.MFStartup(0x20070, 0)); _mfStarted = true;
                MfNative.ForEachDevice(activation =>
                {
                    if (_activate == 0 && string.Equals(MfNative.GetString(activation, MfNative.SymbolicLink), deviceId, StringComparison.Ordinal))
                    { _ = Marshal.AddRef(activation); _activate = activation; }
                });
                if (_activate == 0)
                {
                    throw new CameraDeviceRemovedException();
                }

                _source = MfNative.Activate(_activate);
                MfNative.Check(MfNative.MFCreateAttributes(out nint attributes, 1));
                try
                {
                    nint callback = Marshal.GetIUnknownForObject(_callback);
                    try { MfNative.SetUnknown(attributes, MfNative.AsyncCallback, callback); }
                    finally { _ = Marshal.Release(callback); }
                    MfNative.Check(MfNative.MFCreateSourceReaderFromMediaSource(_source, attributes, out _reader));
                }
                finally { _ = Marshal.Release(attributes); }
                MfNative.SelectStream(_reader, 0xfffffffe, false);
                MfNative.SelectStream(_reader, MfNative.VideoStream, true);
                for (uint index = 0; index < 1024; index++)
                {
                    int hr = MfNative.NativeType(_reader, index, out nint media);
                    if (hr == unchecked((int)0xc00d36b9))
                    {
                        break; // MF_E_NO_MORE_TYPES
                    }

                    MfNative.Check(hr);
                    bool keep = false;
                    try
                    {
                        if (MfNative.GetGuid(media, MfNative.Subtype) != MfNative.Mjpg)
                        {
                            continue;
                        }

                        ulong size = MfNative.GetUInt64(media, MfNative.FrameSize);
                        ulong rate = MfNative.GetUInt64(media, MfNative.FrameRate);
                        ulong aspect = MfNative.TryGetUInt64(media, MfNative.PixelAspect, (1ul << 32) | 1);
                        var type = new CameraMediaType(2, (uint)(size >> 32), (uint)size, (uint)(rate >> 32), (uint)rate, (uint)(aspect >> 32), (uint)aspect, 1);
                        if (type.Width is 0 or > 8192 || type.Height is 0 or > 8192 || type.FrameRateNumerator == 0 || type.FrameRateDenominator == 0 || type.PixelAspectNumerator == 0 || type.PixelAspectDenominator == 0 || _types.Any(x => x.Type == type))
                        {
                            continue;
                        }

                        _types.Add((type, media)); keep = true;
                    }
                    finally
                    {
                        if (!keep)
                        {
                            _ = Marshal.Release(media);
                        }
                    }
                }
                if (_types.Count == 0)
                {
                    throw new NotSupportedException("Selected camera has no native MJPEG capture format.");
                }

                MfNative.SetType(_reader, _types[0].Native); CurrentMediaType = _types[0].Type;
            }
            catch { Release(); throw; }
        }
    }

    public void Start(CameraMediaType mediaType)
    {
        lock (_gate)
        {
            using var apartment = new MfNative.Apartment();
            if (_reader == 0 || _started)
            {
                throw new InvalidOperationException("Invalid camera state.");
            }

            (CameraMediaType Type, nint Native) selected = _types.FirstOrDefault(x => x.Type == mediaType);
            if (selected.Native == 0)
            {
                throw new ArgumentException("Unsupported camera format.", nameof(mediaType));
            }

            MfNative.SetType(_reader, selected.Native); CurrentMediaType = mediaType; _started = true;
        }
    }

    public Task<byte[]> ReadSampleAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_started)
            {
                throw new InvalidOperationException("Camera is not streaming.");
            }

            using var apartment = new MfNative.Apartment();
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _callback.Pending, completion, null) is not null)
            {
                throw new InvalidOperationException("One sample is already pending.");
            }

            int hr = MfNative.ReadSample(_reader);
            if (hr < 0) { _ = Interlocked.Exchange(ref _callback.Pending, null); MfNative.Check(hr); }
            return completion.Task.WaitAsync(ct);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _ = (Interlocked.Exchange(ref _callback.Pending, null)?.TrySetCanceled());
            using var apartment = new MfNative.Apartment();
            _callback.Flushed.Reset();
            MfNative.Check(MfNative.Flush(_reader));
            if (!_callback.Flushed.Wait(TimeSpan.FromSeconds(5)))
            { Release(); throw new IOException("Camera flush did not complete."); }
        }
    }
    public void Deactivate()
    {
        lock (_gate)
        {
            using var apartment = new MfNative.Apartment();
            Release();
        }
    }
    public void Dispose() => Deactivate();
    private void Release()
    {
        _started = false;
        _ = (Interlocked.Exchange(ref _callback.Pending, null)?.TrySetCanceled());
        if (_source != 0)
        {
            MfNative.ShutdownSource(_source);
        }

        if (_reader != 0) { _ = Marshal.Release(_reader); _reader = 0; }
        foreach ((CameraMediaType Type, nint Native) type in _types)
        {
            _ = Marshal.Release(type.Native);
        }

        _types.Clear(); CurrentMediaType = null;
        if (_source != 0) { _ = Marshal.Release(_source); _source = 0; }
        if (_activate != 0) { MfNative.ShutdownActivation(_activate); _ = Marshal.Release(_activate); _activate = 0; }
        if (_mfStarted) { _ = MfNative.MFShutdown(); _mfStarted = false; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ReaderCallback : IMfCameraReaderCallback
    {
        internal TaskCompletionSource<byte[]>? Pending;
        internal readonly ManualResetEventSlim Flushed = new(false);
        public int OnReadSample(int status, uint stream, uint flags, long timestamp, nint sample)
        {
            TaskCompletionSource<byte[]>? pending = Interlocked.Exchange(ref Pending, null);
            if (pending is null)
            {
                return 0;
            }

            try
            {
                if (status == unchecked((int)0xc00d3ea2))
                {
                    throw new CameraDeviceRemovedException();
                }

                MfNative.Check(status);
                if (sample == 0 || (flags & 0x137) != 0)
                {
                    throw new IOException("Camera returned no complete sample or changed its media type.");
                }

                _ = pending.TrySetResult(MfNative.CopySample(sample));
            }
            catch (Exception ex) { _ = pending.TrySetException(ex); }
            return 0;
        }
        public int OnFlush(uint stream) { Flushed.Set(); return 0; }
        public int OnEvent(uint stream, nint mediaEvent) => 0;
    }
}

[ComVisible(true), Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMfCameraReaderCallback
{
    [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, nint sample);
    [PreserveSig] int OnFlush(uint stream);
    [PreserveSig] int OnEvent(uint stream, nint mediaEvent);
}

/// <summary>Vtable slots follow the Windows SDK mfobjects.h, mfidl.h and mfreadwrite.h.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MfNative
{
    internal const uint VideoStream = 0xfffffffc;
    internal static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    internal static readonly Guid VideoSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    internal static readonly Guid SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    internal static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    internal static readonly Guid AsyncCallback = new("1e3dbeac-bb43-4c35-b507-cd644464c965");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid Mjpg = new("47504a4d-0000-0010-8000-00aa00389b71");
    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    internal static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();
    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateAttributes(out nint attributes, uint size);
    [LibraryImport("mf.dll")]
    private static partial int MFEnumDeviceSources(nint attributes, out nint devices, out uint count);
    [LibraryImport("mfreadwrite.dll")]
    internal static partial int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    internal sealed class Apartment : IDisposable
    {
        private readonly bool _uninitialize;
        internal Apartment() { int hr = CoInitializeEx(0, 0); if (hr != unchecked((int)0x80010106)) { Check(hr); } _uninitialize = hr >= 0; }
        public void Dispose()
        {
            if (_uninitialize)
            {
                CoUninitialize();
            }
        }
    }
    internal static void ForEachDevice(Action<nint> visit)
    {
        Check(MFCreateAttributes(out nint attributes, 1));
        nint devices = 0; uint count = 0;
        try
        {
            Guid key = SourceType, value = VideoSource;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value));
            Check(MFEnumDeviceSources(attributes, out devices, out count));
            for (uint i = 0; i < count; i++)
            {
                visit(((nint*)devices)[i]);
            }
        }
        finally
        {
            for (uint i = 0; i < count; i++)
            {
                if (((nint*)devices)[i] != 0)
                {
                    _ = Marshal.Release(((nint*)devices)[i]);
                }
            }

            if (devices != 0)
            {
                Marshal.FreeCoTaskMem(devices);
            }

            _ = Marshal.Release(attributes);
        }
    }
    internal static string GetString(nint obj, Guid key)
    {
        nint text = 0; uint length = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint*, int>)Slot(obj, 13))(obj, &key, &text, &length));
        try { return Marshal.PtrToStringUni(text, checked((int)length)) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    internal static Guid GetGuid(nint obj, Guid key)
    { Guid value; Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(obj, 10))(obj, &key, &value)); return value; }
    internal static ulong GetUInt64(nint obj, Guid key)
    { ulong value; Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(obj, 8))(obj, &key, &value)); return value; }
    internal static ulong TryGetUInt64(nint obj, Guid key, ulong fallback)
    { ulong value; return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(obj, 8))(obj, &key, &value) >= 0 ? value : fallback; }
    internal static void SetUnknown(nint obj, Guid key, nint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(obj, 27))(obj, &key, value));
    internal static nint Activate(nint activation)
    { Guid iid = new("279a808d-aec7-40c8-9c6b-a6b492c78a66"); nint source; Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(activation, 33))(activation, &iid, &source)); return source; }
    internal static void ShutdownActivation(nint activation) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(activation, 34))(activation);
    internal static void ShutdownSource(nint source) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(source, 12))(source);
    internal static void SelectStream(nint reader, uint stream, bool selected) => Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, stream, selected ? 1 : 0));
    internal static int NativeType(nint reader, uint index, out nint media)
    { nint result; int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(reader, 5))(reader, VideoStream, index, &result); media = result; return hr; }
    internal static void SetType(nint reader, nint media) => Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(reader, 7))(reader, VideoStream, 0, media));
    internal static int ReadSample(nint reader) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, nint, nint, int>)Slot(reader, 9))(reader, VideoStream, 0, 0, 0, 0, 0);
    internal static int Flush(nint reader) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(reader, 10))(reader, VideoStream);
    internal static byte[] CopySample(nint sample)
    {
        nint buffer; Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
        try
        {
            nint data; uint maximum, length;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &maximum, &length));
            try
            {
                if (length is 0 or > 16 * 1024 * 1024 - 3 || length > maximum)
                {
                    throw new IOException("Invalid camera sample length.");
                }

                byte[] result = new byte[length]; Marshal.Copy(data, result, 0, result.Length); return result;
            }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
        }
        finally { _ = Marshal.Release(buffer); }
    }
}
