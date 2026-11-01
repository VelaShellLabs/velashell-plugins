using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// File-system device redirection (MS-RDPEFS) over the static "rdpdr" channel: the announce /
/// capability / client-id handshake, the device-list announce for each shared folder, and the
/// Device I/O Request → I/O Completion loop that serves file operations via <see cref="DriveFile"/>.
/// Matches FreeRDP's rdpdr/drive wire formats. Printers, ports, smart cards and audio are not offered.
/// </summary>
internal sealed class RdpdrChannel(Func<byte[], Task> send, IReadOnlyList<DriveMapping> mappings, Action<string>? log = null,
    IReadOnlyList<IRdpdrDevice>? additionalDevices = null, CancellationToken cancellationToken = default)
{
    private const ushort CtypCore = 0x4472;
    private const ushort PakidServerAnnounce = 0x496E, PakidClientIdConfirm = 0x4343, PakidClientName = 0x434E;
    private const ushort PakidDeviceListAnnounce = 0x4441, PakidDeviceReply = 0x6472;
    private const ushort PakidDeviceIoRequest = 0x4952, PakidDeviceIoCompletion = 0x4943;
    private const ushort PakidServerCapability = 0x5350, PakidClientCapability = 0x4350, PakidUserLoggedOn = 0x554C;
    private const ushort CapGeneralType = 0x0001, CapDriveType = 0x0004;
    private const uint GeneralCapabilityVersion02 = 0x00000002, DriveCapabilityVersion02 = 0x00000002;
    private const uint DeviceTypeFilesystem = 0x00000008;
    private const ushort VersionMajor = 0x0001, VersionMinor = 0x000C; // RDP 6.x
    // IRP major functions.
    private const uint IrpCreate = 0x00, IrpClose = 0x02, IrpRead = 0x03, IrpWrite = 0x04;
    private const uint IrpQueryInformation = 0x05, IrpSetInformation = 0x06, IrpQueryVolumeInformation = 0x0A;
    private const uint IrpDirectoryControl = 0x0C, IrpLockControl = 0x11, IrpDeviceControl = 0x0E;
    private const uint IrpMnQueryDirectory = 0x01;

    // Servers at this version (RDP 6.x+) send PAKID_CORE_USER_LOGGEDON, and a real Windows host ignores a
    // file-system device announced before it — the drive then never appears. Older servers (e.g. xrdp at a
    // lower minor) never send it, so for them the devices go out as soon as the client id is confirmed.
    private const ushort UserLoggedOnMinVersion = 0x000C;

    private readonly Func<byte[], Task> _send = send;
    private readonly IReadOnlyList<DriveMapping> _mappings = mappings;
    private readonly Dictionary<uint, DriveMapping> _devices = [];
    private readonly IReadOnlyList<IRdpdrDevice> _additional = additionalDevices ?? [];
    private readonly Dictionary<uint, IRdpdrDevice> _additionalById = [];
    private readonly Lock _pendingGate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly CancellationTokenSource _deviceStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private int _generation;
    private bool _closed;
    private const int MaxOpenFiles = 4096;
    private readonly Dictionary<uint, DriveFile> _files = [];
    private uint _clientId;
    private uint _fileSequence = 1;
    private ushort _serverMinor;
    private bool _announced;

    /// <summary>The server's result code for the last device whose announce it rejected (0 = all accepted).</summary>
    internal uint LastDeviceRejection { get; private set; }

    internal async Task ProcessAsync(byte[] pdu)
    {
        var r = new ByteReader(pdu);
        if (r.Remaining < 4)
        {
            return;
        }

        ushort component = r.U16LE();
        ushort packetId = r.U16LE();
        if (component != CtypCore)
        {
            log?.Invoke($"ignored component=0x{component:X4}; packet=0x{packetId:X4}; bytes={pdu.Length}");
            return;
        }

        if (packetId != PakidDeviceIoRequest)
        {
            log?.Invoke($"receive packet=0x{packetId:X4}; bytes={pdu.Length}");
        }

        switch (packetId)
        {
            case PakidServerAnnounce:
                _ = r.U16LE();                             // server version major
                _serverMinor = r.Remaining >= 2 ? r.U16LE() : (ushort)0;
                _clientId = r.Remaining >= 4 ? r.U32LE() : 0;
                // Windows can restart the channel handshake after logon; announce drives again in that round.
                _announced = false;
                _ = Interlocked.Increment(ref _generation);
                foreach (IRdpdrDevice device in _additional) { device.Reset(); }
                await _send(BuildClientAnnounceReply()).ConfigureAwait(false);
                await _send(BuildClientName()).ConfigureAwait(false);
                break;
            case PakidServerCapability:
                await _send(BuildCapabilityResponse()).ConfigureAwait(false);
                break;
            case PakidClientIdConfirm:
                // Modern servers (>= RDP 6.x) want the drives only after the user has logged on; announce
                // now only for older servers that never send PAKID_CORE_USER_LOGGEDON.
                if (_serverMinor < UserLoggedOnMinVersion)
                {
                    await AnnounceDevicesAsync().ConfigureAwait(false);
                }

                break;
            case PakidUserLoggedOn:
                await AnnounceDevicesAsync().ConfigureAwait(false);
                break;
            case PakidDeviceReply:
                // Server Device Announce Response: deviceId then a result NTSTATUS. A non-zero result means
                // the server refused that drive (commonly a "drive redirection" group policy on the host).
                uint deviceId = r.U32LE();
                LastDeviceRejection = r.Remaining >= 4 ? r.U32LE() : 0;
                log?.Invoke($"device reply id={deviceId}; status=0x{LastDeviceRejection:X8}");
                break;
            case PakidDeviceIoRequest:
                byte[]? completion = HandleIoRequest(ref r);
                if (completion is not null)
                {
                    await _send(completion).ConfigureAwait(false);
                }

                break;
        }
    }

    // ---- Handshake PDUs ----

    /// <summary>Sends the device-list announce once per server handshake; repeated triggers in that round are a no-op.</summary>
    private async Task AnnounceDevicesAsync()
    {
        if (_announced)
        {
            return;
        }

        _announced = true;
        await _send(BuildDeviceListAnnounce()).ConfigureAwait(false);
        log?.Invoke($"announced devices={_mappings.Count + _additional.Count}; serverMinor=0x{_serverMinor:X4}");
    }

    private byte[] BuildClientAnnounceReply()
    {
        ByteWriter w = Header(PakidClientIdConfirm);
        w.U16LE(VersionMajor);
        w.U16LE(VersionMinor);
        w.U32LE(_clientId);
        return w.ToArray();
    }

    private static byte[] BuildClientName()
    {
        ByteWriter w = Header(PakidClientName);
        byte[] name = Encoding.Unicode.GetBytes("VELASHELL\0");
        w.U32LE(1);                 // unicodeFlag
        w.U32LE(0);                 // codePage
        w.U32LE((uint)name.Length); // computerNameLen incl. null
        w.Bytes(name);
        return w.ToArray();
    }

    private byte[] BuildCapabilityResponse()
    {
        IRdpdrDevice[] capabilities = [.. _additional.DistinctBy(d => d.CapabilityType)];
        ByteWriter w = Header(PakidClientCapability);
        w.U16LE((ushort)(2 + capabilities.Length)); // general + drive + selected non-drive capabilities
        w.U16LE(0);                 // pad

        // General capability set (version 2, body 36).
        w.U16LE(CapGeneralType);
        w.U16LE(8 + 36);
        w.U32LE(GeneralCapabilityVersion02);
        w.U32LE(0);                 // osType
        w.U32LE(0);                 // osVersion
        w.U16LE(VersionMajor);
        w.U16LE(VersionMinor);
        w.U32LE(0x0000FFFF);        // ioCode1: all IRP_MJ supported
        w.U32LE(0);                 // ioCode2
        w.U32LE(0x00000007);        // extendedPDU: REMOVE | DISPLAY_NAME | USER_LOGGEDON
        w.U32LE(0x00000001);        // extraFlags1: ENABLE_ASYNCIO
        w.U32LE(0);                 // extraFlags2
        w.U32LE(0);                 // SpecialTypeDeviceCap

        // Drive capability set (version 2, header only).
        w.U16LE(CapDriveType);
        w.U16LE(8);
        w.U32LE(DriveCapabilityVersion02);
        foreach (IRdpdrDevice device in capabilities)
        {
            w.U16LE(device.CapabilityType);
            w.U16LE(8);
            w.U32LE(device.CapabilityVersion);
        }
        return w.ToArray();
    }

    private byte[] BuildDeviceListAnnounce()
    {
        _devices.Clear();
        _additionalById.Clear();
        ByteWriter w = Header(PakidDeviceListAnnounce);
        w.U32LE((uint)(_mappings.Count + _additional.Count));
        uint deviceId = 1;
        // One 8-byte PreferredDosName buffer reused for every device: a stackalloc inside the loop
        // would grow the frame per mapping (CA2014). It is cleared at the top of each iteration.
        Span<byte> dos = stackalloc byte[8];
        foreach (DriveMapping mapping in _mappings)
        {
            _devices[deviceId] = mapping;
            w.U32LE(DeviceTypeFilesystem);
            w.U32LE(deviceId);
            dos.Clear();
            int n = Encoding.ASCII.GetBytes(mapping.Name.AsSpan(0, Math.Min(8, mapping.Name.Length)), dos);
            for (int i = 0; i < n; i++)
            {
                if (dos[i] > 0x7F)
                {
                    dos[i] = (byte)'_';
                }
            }

            w.Bytes(dos);
            byte[] data = Encoding.Unicode.GetBytes(mapping.Name + "\0");
            w.U32LE((uint)data.Length);
            w.Bytes(data);
            deviceId++;
        }
        foreach (IRdpdrDevice device in _additional)
        {
            _additionalById.Add(deviceId, device);
            w.U32LE(device.DeviceType);
            w.U32LE(deviceId++);
            dos.Clear();
            _ = Encoding.ASCII.GetBytes(device.Name.AsSpan(0, Math.Min(7, device.Name.Length)), dos);
            w.Bytes(dos);
            byte[] data = device.DeviceData;
            w.U32LE((uint)data.Length);
            w.Bytes(data);
        }
        return w.ToArray();
    }

    // ---- Device I/O ----

    private byte[]? HandleIoRequest(ref ByteReader r)
    {
        uint deviceId = r.U32LE();
        uint fileId = r.U32LE();
        uint completionId = r.U32LE();
        uint major = r.U32LE();
        uint minor = r.U32LE();

        if (_additionalById.TryGetValue(deviceId, out IRdpdrDevice? device))
        {
            lock (_pendingGate)
            {
                if (_closed || _pending.Count >= 64 || r.Remaining > 1024 * 1024 + 32)
                {
                    return DeviceCompletion(deviceId, completionId, PortDevice.Failure(major, NtStatus.InsufficientResources));
                }
                var request = new RdpdrIoRequest(fileId, major, minor, r.Rest().ToArray());
                Task work = CompleteDeviceAsync(device, request, deviceId, completionId, _generation);
                _ = _pending.Add(work);
                _ = work.ContinueWith(t => { lock (_pendingGate) { _ = _pending.Remove(t); } }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return null;
        }

        var body = new ByteWriter(64);
        uint status;
        if (!_devices.TryGetValue(deviceId, out DriveMapping mapping))
        {
            status = NtStatus.Unsuccessful;
        }
        else
        {
            try { status = Dispatch(major, minor, mapping, fileId, ref r, body); }
            catch (DriveStatusException ex) { status = ex.Status; }
            catch (UnauthorizedAccessException) { status = NtStatus.AccessDenied; }
            catch (FileNotFoundException) { status = NtStatus.NoSuchFile; }
            catch (DirectoryNotFoundException) { status = NtStatus.ObjectPathNotFound; }
            catch (IOException) { status = NtStatus.Unsuccessful; }
            // A truncated request, an unexpected argument, a stream that cannot do what was asked: still answer,
            // so the server never waits on a completion that does not come.
            catch (RdpProtocolException) { status = NtStatus.InvalidParameter; }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or OverflowException or InvalidOperationException)
            {
                status = NtStatus.Unsuccessful;
            }
        }

        if (status != NtStatus.Success)
        {
            log?.Invoke($"I/O completion device={deviceId}; major=0x{major:X2}; minor=0x{minor:X2}; status=0x{status:X8}");
        }

        ByteWriter w = Header(PakidDeviceIoCompletion);
        w.U32LE(deviceId);
        w.U32LE(completionId);
        w.U32LE(status);
        w.Bytes(body.Span);
        return w.ToArray();
    }

    private uint Dispatch(uint major, uint minor, DriveMapping mapping, uint fileId, ref ByteReader r, ByteWriter body)
    {
        switch (major)
        {
            case IrpCreate:
                {
                    uint desiredAccess = r.U32LE();
                    _ = r.U32LE(); _ = r.U32LE();           // AllocationSize (u64)
                    uint fileAttributes = r.U32LE();
                    _ = r.U32LE();                      // SharedAccess
                    uint disposition = r.U32LE();
                    uint options = r.U32LE();
                    int pathLength = (int)r.U32LE();
                    string path = DecodeUnicode(r.Take(pathLength));
                    if (_files.Count >= MaxOpenFiles)
                    {
                        // Each open file holds a local handle until the server closes it; bound them.
                        body.U32LE(0); body.U8(0);
                        return NtStatus.InsufficientResources;
                    }

                    try
                    {
                        var file = DriveFile.Create(mapping.LocalPath, path, desiredAccess, disposition, options, fileAttributes);
                        uint newId = _fileSequence++;
                        _files[newId] = file;
                        body.U32LE(newId);
                        body.U8(file.Information);
                        return NtStatus.Success;
                    }
                    catch (DriveStatusException ex) { body.U32LE(0); body.U8(0); return ex.Status; }
                }
            case IrpClose:
                if (_files.Remove(fileId, out DriveFile? closing))
                {
                    closing.Close();
                }

                body.Zeros(4);
                return NtStatus.Success;
            case IrpRead:
                {
                    uint length = r.U32LE();
                    ulong offset = r.U32LE() | ((ulong)r.U32LE() << 32);
                    r.Skip(20);
                    if (!_files.TryGetValue(fileId, out DriveFile? file)) { body.U32LE(0); return NtStatus.Unsuccessful; }
                    byte[] data = length == 0 ? [] : file.Read(offset, length);
                    body.U32LE((uint)data.Length);
                    body.Bytes(data);
                    return NtStatus.Success;
                }
            case IrpWrite:
                {
                    uint length = r.U32LE();
                    ulong offset = r.U32LE() | ((ulong)r.U32LE() << 32);
                    r.Skip(20);
                    ReadOnlySpan<byte> data = r.Take((int)length);
                    if (!_files.TryGetValue(fileId, out DriveFile? file)) { body.U32LE(0); body.U8(0); return NtStatus.Unsuccessful; }
                    uint written = file.Write(offset, data);
                    body.U32LE(written);
                    body.U8(0);
                    return NtStatus.Success;
                }
            case IrpQueryInformation:
                {
                    uint infoClass = r.U32LE();
                    if (!_files.TryGetValue(fileId, out DriveFile? file)) { body.U32LE(0); return NtStatus.Unsuccessful; }
                    file.QueryInformation(infoClass, body);
                    return NtStatus.Success;
                }
            case IrpSetInformation:
                {
                    uint infoClass = r.U32LE();
                    uint length = r.U32LE();
                    r.Skip(24);
                    ReadOnlySpan<byte> buffer = r.Take((int)length);
                    if (!_files.TryGetValue(fileId, out DriveFile? file)) { body.U32LE(length); return NtStatus.Unsuccessful; }
                    SetInformation(file, infoClass, buffer);
                    body.U32LE(length);
                    return NtStatus.Success;
                }
            case IrpQueryVolumeInformation:
                return QueryVolume(mapping, r.U32LE(), body);
            case IrpDirectoryControl:
                if (minor != IrpMnQueryDirectory) { body.U32LE(0); return NtStatus.NotSupported; }
                return QueryDirectory(fileId, ref r, body);
            case IrpDeviceControl:
                body.U32LE(0);                  // OutputBufferLength
                return NtStatus.NotSupported;
            case IrpLockControl:
                return NtStatus.Success;
            default:
                body.U32LE(0);
                return NtStatus.NotSupported;
        }
    }

    private uint QueryDirectory(uint fileId, ref ByteReader r, ByteWriter body)
    {
        uint infoClass = r.U32LE();
        byte initialQuery = r.U8();
        int pathLength = (int)r.U32LE();
        r.Skip(23);
        string path = DecodeUnicode(r.Take(pathLength));
        if (!_files.TryGetValue(fileId, out DriveFile? file)) { body.U32LE(0); return NtStatus.Unsuccessful; }
        if (initialQuery != 0)
        {
            file.BeginEnumeration(path);
        }

        if (file.QueryDirectory(infoClass, body))
        {
            return NtStatus.Success;
        }

        body.U32LE(0);
        body.U8(0);
        return NtStatus.NoMoreFiles;
    }

    private static void SetInformation(DriveFile file, uint infoClass, ReadOnlySpan<byte> buffer)
    {
        switch (infoClass)
        {
            case DriveFile.FileDispositionInformation:
                file.MarkForDeletion();
                break;
            case DriveFile.FileRenameInformation:
                {
                    var br = new ByteReader(buffer);
                    _ = br.U8();                        // ReplaceIfExists
                    _ = br.U8();                        // RootDirectory
                    int nameLength = (int)br.U32LE();
                    string target = DecodeUnicode(br.Take(nameLength));
                    file.Rename(target);
                    break;
                }
                // FileBasicInformation / FileEndOfFileInformation / FileAllocationInformation: accepted, no-op.
        }
    }

    private static uint QueryVolume(DriveMapping mapping, uint infoClass, ByteWriter body)
    {
        long total = 0, free = 0;
        try { var di = new DriveInfo(Path.GetPathRoot(mapping.LocalPath) ?? mapping.LocalPath); total = di.TotalSize; free = di.AvailableFreeSpace; }
        catch { /* report zero */ }
        const uint bytesPerSector = 512, sectorsPerCluster = 8;
        ulong clusterBytes = bytesPerSector * sectorsPerCluster;
        ulong totalClusters = clusterBytes == 0 ? 0 : (ulong)total / clusterBytes;
        ulong freeClusters = clusterBytes == 0 ? 0 : (ulong)free / clusterBytes;

        switch (infoClass)
        {
            case 1: // FileFsVolumeInformation
                {
                    byte[] label = Encoding.Unicode.GetBytes(mapping.Name + "\0");
                    body.U32LE((uint)(17 + label.Length));
                    body.U32LE(0); body.U32LE(0);           // VolumeCreationTime
                    body.U32LE((uint)(freeClusters & 0xFFFF)); // VolumeSerialNumber
                    body.U32LE((uint)label.Length);
                    body.U8(0);                             // SupportsObjects
                    body.Bytes(label);
                    return NtStatus.Success;
                }
            case 3: // FileFsSizeInformation
                body.U32LE(24);
                body.U32LE((uint)totalClusters); body.U32LE((uint)(totalClusters >> 32));
                body.U32LE((uint)freeClusters); body.U32LE((uint)(freeClusters >> 32));
                body.U32LE(sectorsPerCluster);
                body.U32LE(bytesPerSector);
                return NtStatus.Success;
            case 5: // FileFsAttributeInformation
                {
                    byte[] fsName = Encoding.Unicode.GetBytes("NTFS\0");
                    body.U32LE((uint)(12 + fsName.Length));
                    body.U32LE(0x00000007);                 // CASE_SENSITIVE | CASE_PRESERVED | UNICODE_ON_DISK
                    body.U32LE(260);                        // MaximumComponentNameLength
                    body.U32LE((uint)fsName.Length);
                    body.Bytes(fsName);
                    return NtStatus.Success;
                }
            case 7: // FileFsFullSizeInformation
                body.U32LE(32);
                body.U32LE((uint)totalClusters); body.U32LE((uint)(totalClusters >> 32));
                body.U32LE((uint)freeClusters); body.U32LE((uint)(freeClusters >> 32));
                body.U32LE((uint)freeClusters); body.U32LE((uint)(freeClusters >> 32));
                body.U32LE(sectorsPerCluster);
                body.U32LE(bytesPerSector);
                return NtStatus.Success;
            case 4: // FileFsDeviceInformation
                body.U32LE(8);
                body.U32LE(0x00000007);                 // FILE_DEVICE_DISK
                body.U32LE(0);
                return NtStatus.Success;
            default:
                body.U32LE(0);
                return NtStatus.Unsuccessful;
        }
    }

    // ---- Helpers ----

    private static ByteWriter Header(ushort packetId)
    {
        var w = new ByteWriter(64);
        w.U16LE(CtypCore);
        w.U16LE(packetId);
        return w;
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> data)
    {
        string s = Encoding.Unicode.GetString(data);
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    private static byte[] DeviceCompletion(uint deviceId, uint completionId, RdpdrIoResult result)
    {
        ByteWriter body = Header(PakidDeviceIoCompletion);
        body.U32LE(deviceId);
        body.U32LE(completionId);
        body.U32LE(result.Status);
        body.Bytes(result.Body);
        return body.ToArray();
    }

    private async Task CompleteDeviceAsync(IRdpdrDevice device, RdpdrIoRequest request, uint deviceId, uint completionId, int generation)
    {
        try
        {
            RdpdrIoResult result;
            try { result = await device.ProcessAsync(request, _deviceStop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = PortDevice.Failure(request.MajorFunction, 0xC0000120); }
            catch (Exception ex)
            {
                log?.Invoke($"device operation failed: {ex.GetType().Name}");
                result = PortDevice.Failure(request.MajorFunction, NtStatus.Unsuccessful);
            }
            if (!_deviceStop.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                await _send(DeviceCompletion(deviceId, completionId, result)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { log?.Invoke($"device completion failed: {ex.GetType().Name}"); }
    }

    internal void CloseAll()
    {
        lock (_pendingGate)
        {
            if (!_closed)
            {
                _closed = true;
                _deviceStop.Cancel();
                foreach (IRdpdrDevice device in _additional) { device.Dispose(); }
            }
        }
        foreach (DriveFile file in _files.Values) { try { file.Close(); } catch { /* ignore */ } }
        _files.Clear();
    }

    internal async Task DrainDevicesAsync()
    {
        Task[] pending;
        lock (_pendingGate) { pending = [.. _pending]; }
        await Task.WhenAll(pending).ConfigureAwait(false);
        if (_closed)
        {
            foreach (IAsyncDisposable device in _additional.OfType<IAsyncDisposable>())
            {
                await device.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
