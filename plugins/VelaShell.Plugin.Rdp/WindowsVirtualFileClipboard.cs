using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace VelaShell.Plugin.Rdp;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsVirtualFileClipboard : IRdpVirtualFileClipboard
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _current;
    private WindowsVirtualFileDataObject? _data;
    internal bool OwnsClipboard => _data is not null && Native.GetClipboardOwner() == _ownerWindow;
    public bool IsCurrent => _current && _data?.Files.Cancelled == false
        && Native.GetClipboardOwner() == _ownerWindow;
    private nint _ownerWindow;

    internal WindowsVirtualFileClipboard()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RDP virtual file clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    public async Task PublishAsync(RdpVirtualFiles files, uint? expectedSequence, CancellationToken ct)
    {
        await RetryClipboardAsync(() => PublishOnceAsync(files, expectedSequence, ct), ct).ConfigureAwait(false);
    }
    internal static async Task RetryClipboardAsync(Func<Task> action, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { await action().ConfigureAwait(false); return; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 7)
            { await Task.Delay(25 * (attempt + 1), ct).ConfigureAwait(false); }
        }
    }
    private async Task PublishOnceAsync(RdpVirtualFiles files, uint? expectedSequence, CancellationToken ct)
    {
        await _initialized.Task.WaitAsync(ct).ConfigureAwait(false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_work)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(WindowsVirtualFileClipboard));
            _work.Enqueue(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (_stopping) throw new ObjectDisposedException(nameof(WindowsVirtualFileClipboard));
                    if (expectedSequence is { } sequence && Native.GetClipboardSequenceNumber() != sequence)
                        throw new IOException("The local clipboard changed before virtual files could be published.");
                    var next = new WindowsVirtualFileDataObject(files);
                    int result = Native.OleSetClipboard(next);
                    if (result < 0) { next.Dispose(); Marshal.ThrowExceptionForHR(result); }
                    _data?.Files.Retire();
                    _data = next;
                    // Delayed rendering itself can change the sequence number; ownership cannot use it.
                    _ownerWindow = Native.GetClipboardOwner();
                    _current = true;
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
            });
            _wake.Set();
        }
        await done.Task.ConfigureAwait(false);
    }
    private void Run()
    {
        int initialized = Native.OleInitialize(0);
        Exception? failure = null;
        try
        {
            Marshal.ThrowExceptionForHR(initialized);
            _initialized.TrySetResult();
            while (!_stopping)
            {
                while (_work.TryDequeue(out var action)) action();
                while (Native.PeekMessage(out var message, 0, 0, 0, 1))
                {
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
                if (_data is { } data && Native.OleIsCurrentClipboard(data) != 0)
                {
                    _current = false;
                    data.Files.Retire();
                    _data = null;
                }
                // Keep cancelled descriptors inert until replaced. Clearing them here races the
                // next remote copy's local-clipboard sequence check.
                if (_data?.Files.Cancelled == true) _current = false;
                _wake.WaitOne(20);
            }
        }
        catch (Exception ex) { _stopping = true; _initialized.TrySetException(ex); failure = ex; }
        finally
        {
            while (_work.TryDequeue(out var action)) action();
            if (_data is { } data)
            {
                data.Files.Dispose();
                if (Native.OleIsCurrentClipboard(data) == 0) Native.OleSetClipboard(null);
            }
            _data = null;
            _current = false;
            if (initialized >= 0) Native.OleUninitialize();
            if (failure is null) _ended.TrySetResult();
            else _ended.TrySetException(failure);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _data?.Files.Dispose();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _ended.Task.ConfigureAwait(false); return; }
        lock (_work) { _stopping = true; _wake.Set(); }
        try { await _ended.Task.ConfigureAwait(false); }
        finally { _wake.Dispose(); }
    }
    internal static partial class Native
    {
        [LibraryImport("ole32.dll")] internal static partial int OleInitialize(nint reserved);
        [LibraryImport("ole32.dll")] internal static partial void OleUninitialize();
        // LibraryImport cannot marshal these classic COM interfaces through the runtime CCW.
        [DllImport("ole32.dll")] internal static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IVirtualFileDataObject? data);
        [DllImport("ole32.dll")] internal static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] IVirtualFileDataObject data);
        [LibraryImport("user32.dll")] internal static partial uint GetClipboardSequenceNumber();
        [LibraryImport("user32.dll")] internal static partial nint GetClipboardOwner();
        [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint RegisterClipboardFormat(string name);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalAlloc(uint flags, nuint size);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalLock(nint memory);
        [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GlobalUnlock(nint memory);
        [LibraryImport("kernel32.dll")] internal static partial nuint GlobalSize(nint memory);
        // STGMEDIUM.pUnkForRelease uses runtime COM object marshalling, unsupported by LibraryImport.
        [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [LibraryImport("kernel32.dll")] internal static partial nint GlobalFree(nint memory);
        [StructLayout(LayoutKind.Sequential)] internal struct Message
        { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal int X, Y; internal uint Private; }
        [LibraryImport("user32.dll", EntryPoint = "PeekMessageA")][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
        [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TranslateMessage(ref Message message);
        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageA")] internal static partial nint DispatchMessage(ref Message message);
    }
}

[SupportedOSPlatform("windows"), ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class WindowsVirtualFileDataObject : IVirtualFileDataObject, IDataObjectAsyncCapability, IDisposable
{
    internal RdpVirtualFiles Files { get; }
    internal static short DescriptorFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("FileGroupDescriptorW"));
    internal static short ContentsFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("FileContents"));
    private static short EffectFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("Preferred DropEffect"));
    private readonly Lock _gate = new();
    private IDisposable? _lease;
    private readonly Dictionary<short, byte[]> _feedback = [];
    private bool _async = true, _inOperation;
    internal static short PerformedFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("Performed DropEffect"));
    internal static short LogicalPerformedFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("Logical Performed DropEffect"));
    internal static short PasteSucceededFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("Paste Succeeded"));
    internal static short TargetClsidFormat => unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat("TargetCLSID"));
    internal WindowsVirtualFileDataObject(RdpVirtualFiles files) { Files = files; _lease = files.Acquire(); }
    ~WindowsVirtualFileDataObject() => Dispose();
    public void Dispose() { lock (_gate) { _lease?.Dispose(); _lease = null; } GC.SuppressFinalize(this); }
    public void SetAsyncMode(bool value) => _async = value;
    public void GetAsyncMode(out bool value) => value = _async;
    public void StartOperation(IBindCtx? context)
    {
        lock (_gate)
        {
            _lease ??= Files.Acquire();
            if (!_inOperation) { _feedback.Clear(); Files.BeginOperation(); }
            _inOperation = true;
        }
    }
    public void InOperation(out bool value) => value = _inOperation;
    public void EndOperation(int result, IBindCtx? context, uint effects)
    {
        Files.EndOperation(result, effects);
        CompleteOperation();
    }
    private void CompleteOperation()
    {
        lock (_gate)
        {
            _inOperation = false;
            // COM consumers own their streams. Releasing the operation lease must not close them.
            _lease?.Dispose(); _lease = null;
        }
    }
    internal static FORMATETC Format(short id, TYMED medium, int index = -1) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    public int QueryGetData(ref FORMATETC format)
    {
        if (Files.Cancelled) return unchecked((int)0x800401D0);
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if (format.cfFormat == ContentsFormat)
        {
            if (format.lindex < -1 || format.lindex >= Files.Files.Count || (format.lindex >= 0 && Files.Files[format.lindex].IsDirectory)) return unchecked((int)0x80040068);
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? 0 : unchecked((int)0x80040069);
        }
        bool feedback;
        lock (_gate) feedback = _feedback.ContainsKey(format.cfFormat);
        if (format.cfFormat == DescriptorFormat || format.cfFormat == EffectFormat || feedback)
            return format.lindex == -1 && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : unchecked((int)0x80040069);
        return unchecked((int)0x80040064);
    }
    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        int result = QueryGetData(ref format);
        if (result < 0) return result;
        try { return GetDataCore(ref format, out medium); }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }
    private int GetDataCore(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        lock (_gate) _lease ??= Files.Acquire();
        if (format.cfFormat == ContentsFormat)
        {
            if (format.lindex < 0) return unchecked((int)0x80040068);
            lock (_gate)
            {
                _lease ??= Files.Acquire();
                var stream = new VirtualFileStream(Files, (uint)format.lindex);
                medium = new() { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IVirtualFileStream)) };
            }
            return 0;
        }
        byte[] bytes;
        lock (_gate) bytes = _feedback.TryGetValue(format.cfFormat, out var value) ? value : BitConverter.GetBytes(1u);
        if (format.cfFormat == DescriptorFormat) bytes = RdpClipboardCodec.EncodeFiles(Files.Files);
        nint memory = WindowsVirtualFileClipboard.Native.GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) throw new OutOfMemoryException();
        nint address = WindowsVirtualFileClipboard.Native.GlobalLock(memory);
        if (address == 0) { WindowsVirtualFileClipboard.Native.GlobalFree(memory); throw new OutOfMemoryException(); }
        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
        finally { WindowsVirtualFileClipboard.Native.GlobalUnlock(memory); }
        medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
        if (format.cfFormat == DescriptorFormat) Files.DescriptorsDelivered();
        return 0;
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction == DATADIR.DATADIR_SET) return new Formats([Format(PerformedFormat, TYMED.TYMED_HGLOBAL), Format(LogicalPerformedFormat, TYMED.TYMED_HGLOBAL), Format(PasteSucceededFormat, TYMED.TYMED_HGLOBAL), Format(TargetClsidFormat, TYMED.TYMED_HGLOBAL)]);
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Invalid direction", unchecked((int)0x80070057));
        return new Formats([Format(DescriptorFormat, TYMED.TYMED_HGLOBAL), Format(ContentsFormat, TYMED.TYMED_ISTREAM), Format(EffectFormat, TYMED.TYMED_HGLOBAL)]);
    }
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = 0; return 0x40130; }
    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => unchecked((int)0x80040069);
    public int SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
    {
        int size = format.cfFormat == TargetClsidFormat ? 16 :
            format.cfFormat == PerformedFormat || format.cfFormat == LogicalPerformedFormat || format.cfFormat == PasteSucceededFormat ? 4 : 0;
        if (size == 0) return unchecked((int)0x80040064);
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if (format.lindex != -1) return unchecked((int)0x80040068);
        if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0 || medium.tymed != TYMED.TYMED_HGLOBAL
            || medium.unionmember == 0 || WindowsVirtualFileClipboard.Native.GlobalSize(medium.unionmember) < (nuint)size)
            return unchecked((int)0x80040069);
        nint memory = WindowsVirtualFileClipboard.Native.GlobalLock(medium.unionmember);
        if (memory == 0) return unchecked((int)0x80040069);
        byte[] value = new byte[size];
        try { Marshal.Copy(memory, value, 0, size); }
        finally { WindowsVirtualFileClipboard.Native.GlobalUnlock(medium.unionmember); }
        lock (_gate) _feedback[format.cfFormat] = value;
        if (format.cfFormat == PasteSucceededFormat && (BitConverter.ToUInt32(value) & 3) != 0)
            Files.ConfirmShellCompletion();
        if (release) WindowsVirtualFileClipboard.Native.ReleaseStgMedium(ref medium);
        // Feedback describes a Shell effect/target; only EndOperation ends an async operation.
        // Neither feedback nor successful EndOperation owns the consumer's IStream references.
        return 0;
    }
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA? advise) { advise = null; return unchecked((int)0x80040003); }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Formats(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _position;
        public int Next(int count, FORMATETC[] output, int[]? fetched)
        {
            int read = 0;
            while (read < count && _position < formats.Length) output[read++] = formats[_position++];
            if (fetched is { Length: > 0 }) fetched[0] = read;
            return read == count ? 0 : 1;
        }
        public int Skip(int count) { int next = Math.Min(formats.Length, _position + count); bool full = next - _position == count; _position = next; return full ? 0 : 1; }
        public int Reset() { _position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) => clone = new Formats(formats) { _position = _position };
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class VirtualFileStream : IVirtualFileStream, IDisposable
{
    private readonly RdpVirtualFiles files;
    private readonly uint index;
    private IDisposable? _lease;
    internal VirtualFileStream(RdpVirtualFiles files, uint index) { this.files = files; this.index = index; _lease = files.Acquire(); }
    ~VirtualFileStream() => Dispose();
    public void Dispose() { Interlocked.Exchange(ref _lease, null)?.Dispose(); GC.SuppressFinalize(this); }
    private long _position;
    private readonly Lock _gate = new();
    public int Read(nint buffer, int count, nint read)
    {
        lock (_gate)
        {
            int total = 0;
            try
            {
                ObjectDisposedException.ThrowIf(_lease is null, this);
                if (count < 0 || (buffer == 0 && count != 0)) throw new ArgumentOutOfRangeException(nameof(count));
                while (total < count)
                {
                    byte[] data = files.ReadAsync(index, _position, Math.Min(count - total, RdpClipboardFiles.ChunkSize)).GetAwaiter().GetResult();
                    if (data.Length == 0) break;
                    Marshal.Copy(data, 0, buffer + total, data.Length);
                    total += data.Length;
                    _position += data.Length;
                }
                return total == count ? 0 : 1;
            }
            catch (Exception ex) { return Marshal.GetHRForException(ex); }
            finally { if (read != 0) Marshal.WriteInt32(read, total); }
        }
    }
    internal void Read(byte[] buffer, int count, nint read)
    {
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { Marshal.ThrowExceptionForHR(Read(pin.AddrOfPinnedObject(), count, read)); }
        finally { pin.Free(); }
    }
    public void Seek(long move, int origin, nint position)
    {
        lock (_gate) { SeekCore(move, origin, position); }
    }
    private void SeekCore(long move, int origin, nint position)
    {
        long next = checked((origin switch { 0 => 0, 1 => _position, 2 => files.Files[(int)index].Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + move);
        if (next < 0 || next > files.Files[(int)index].Length) throw new IOException("Invalid seek.");
        _position = next;
        if (position != 0) Marshal.WriteInt64(position, next);
    }
    public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = files.Files[(int)index].Length, pwcsName = (flags & 1) == 0 ? files.Files[(int)index].Name : "", grfMode = 0 };
    public void Clone(out IVirtualFileStream stream)
    {
        lock (_gate) { stream = new VirtualFileStream(files, index) { _position = _position }; }
    }
    public void CopyTo(IStream target, long count, nint read, nint written)
    {
        long total = 0;
        nint actual = Marshal.AllocCoTaskMem(4);
        try
        {
            byte[] buffer = new byte[RdpClipboardFiles.ChunkSize];
            while (total < count)
            {
                Read(buffer, (int)Math.Min(buffer.Length, count - total), actual);
                int size = Marshal.ReadInt32(actual);
                if (size == 0) break;
                target.Write(buffer, size, actual);
                if (Marshal.ReadInt32(actual) != size) throw new IOException("Incomplete shell stream write.");
                total += size;
            }
        }
        finally { Marshal.FreeCoTaskMem(actual); if (read != 0) Marshal.WriteInt64(read, total); if (written != 0) Marshal.WriteInt64(written, total); }
    }
    public void Commit(int flags) { }
    public void Write(nint buffer, int count, nint written) => throw new COMException("Read-only", unchecked((int)0x80030005));
    public void SetSize(long size) => throw new COMException("Read-only", unchecked((int)0x80030005));
    public void Revert() => throw new COMException("Not supported", unchecked((int)0x80030001));
    public void LockRegion(long offset, long count, int type) => throw new COMException("Not supported", unchecked((int)0x80030001));
    public void UnlockRegion(long offset, long count, int type) => throw new COMException("Not supported", unchecked((int)0x80030001));
}

// CLR COM exposure requires public ABI interfaces; these are not plugin extension APIs.
#pragma warning disable CS1591
// IDataObject ABI with direct HRESULTs for normal Shell format probes.
[ComVisible(true), Guid("0000010E-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVirtualFileDataObject
{
    [PreserveSig] int GetData(ref FORMATETC format, out STGMEDIUM medium);
    [PreserveSig] int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);
    [PreserveSig] int QueryGetData(ref FORMATETC format);
    [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output);
    [PreserveSig] int SetData(ref FORMATETC format, ref STGMEDIUM medium, [MarshalAs(UnmanagedType.Bool)] bool release);
    IEnumFORMATETC EnumFormatEtc(DATADIR direction);
    [PreserveSig] int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection);
    void DUnadvise(int connection);
    [PreserveSig] int EnumDAdvise(out IEnumSTATDATA? advise);
}

[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    void StartOperation(IBindCtx? context);
    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    void EndOperation(int result, IBindCtx? context, uint effects);
}

// Raw pointers keep COM marshaling from allocating an array as large as the caller's Read request.
[ComVisible(true), Guid("0000000C-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVirtualFileStream
{
    [PreserveSig] int Read(nint buffer, int count, nint read);
    void Write(nint buffer, int count, nint written);
    void Seek(long move, int origin, nint position);
    void SetSize(long size);
    void CopyTo(IStream target, long count, nint read, nint written);
    void Commit(int flags);
    void Revert();
    void LockRegion(long offset, long count, int type);
    void UnlockRegion(long offset, long count, int type);
    void Stat(out STATSTG stat, int flags);
    void Clone(out IVirtualFileStream stream);
}

#pragma warning restore CS1591
