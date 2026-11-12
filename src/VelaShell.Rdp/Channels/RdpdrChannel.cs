using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// File-system device redirection (MS-RDPEFS) over the static "rdpdr" channel: the announce /
/// capability / client-id handshake, the device-list announce for each shared folder, and the
/// Device I/O Request → I/O Completion loop that serves file operations via <see cref="DriveFile"/>.
/// Additional selected devices implement their own I/O; smart cards are announced before logon.
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
    // Serializes the drive requests (served on the channel worker) with CloseAll (called on session teardown), which
    // used to enumerate and clear the same dictionary concurrently.
    private readonly Lock _filesGate = new();

    /// <summary>Open drive file handles (tests).</summary>
    internal int OpenFileCount { get { lock (_filesGate) { return _files.Count; } } }
    private uint _clientId;
    private uint _fileSequence = 1;
    private ushort _serverMinor;
    private bool _announced;
    private bool _smartCardsAnnounced;

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
                _smartCardsAnnounced = false;
                _devices.Clear();
                _additionalById.Clear();
                // A restarted handshake invalidates every file id the server held; close them instead of leaking handles.
                CloseFiles();
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
                else if (!_announced && !_smartCardsAnnounced && _additional.Any(device => device.DeviceType == 0x20))
                {
                    await _send(BuildDeviceListAnnounce(smartCardsOnly: true)).ConfigureAwait(false);
                    _smartCardsAnnounced = true;
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
        IRdpdrDevice[] capabilities = [.. _additional.DistinctBy(static d => d.CapabilityType)];
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
        w.U32LE((uint)_additional.Count(static device => device.DeviceType == 0x20)); // pre-logon smart-card devices

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

    private byte[] BuildDeviceListAnnounce(bool smartCardsOnly = false)
    {
        bool Include(IRdpdrDevice device) => smartCardsOnly ? device.DeviceType == 0x20 : !_smartCardsAnnounced || device.DeviceType != 0x20;
        ByteWriter w = Header(PakidDeviceListAnnounce);
        w.U32LE((uint)((smartCardsOnly ? 0 : _mappings.Count) + _additional.Count(Include)));
        uint deviceId = 1;
        // One 8-byte PreferredDosName buffer reused for every device: a stackalloc inside the loop
        // would grow the frame per mapping (CA2014). It is cleared at the top of each iteration.
        Span<byte> dos = stackalloc byte[8];
        foreach (DriveMapping mapping in _mappings)
        {
            if (smartCardsOnly) { deviceId++; continue; }
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
            if (!Include(device)) { deviceId++; continue; }
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
            try
            {
                lock (_filesGate) { status = Dispatch(major, minor, mapping, fileId, ref r, body); }
            }
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
        bool matched = true;
        if (initialQuery != 0)
        {
            matched = file.BeginEnumeration(path);
        }

        if (file.QueryDirectory(infoClass, body))
        {
            return NtStatus.Success;
        }

        body.U32LE(0);
        body.U8(0);
        // A first query that matches nothing is STATUS_NO_SUCH_FILE (what FindFirstFile turns into
        // ERROR_FILE_NOT_FOUND); STATUS_NO_MORE_FILES only ends an enumeration that returned entries.
        return matched ? NtStatus.NoMoreFiles : NtStatus.NoSuchFile;
    }

    private static void SetInformation(DriveFile file, uint infoClass, ReadOnlySpan<byte> buffer)
    {
        var br = new ByteReader(buffer);
        switch (infoClass)
        {
            case DriveFile.FileDispositionInformation:
                // FILE_DISPOSITION_INFORMATION is one DeletePending byte; servers may also send no buffer, meaning delete.
                file.SetDeletePending(buffer.IsEmpty || buffer[0] != 0);
                break;
            case DriveFile.FileRenameInformation:
                {
                    bool replaceIfExists = br.U8() != 0;
                    _ = br.U8();                        // RootDirectory
                    int nameLength = (int)br.U32LE();
                    string target = DecodeUnicode(br.Take(nameLength));
                    file.Rename(target, replaceIfExists);
                    break;
                }
            case DriveFile.FileEndOfFileInformation:
                file.SetEndOfFile(br.I64LE());
                break;
            case DriveFile.FileAllocationInformation:
                file.SetAllocation(br.I64LE());
                break;
            case DriveFile.FileBasicInformation:
                {
                    long creation = br.I64LE(), lastAccess = br.I64LE(), lastWrite = br.I64LE();
                    _ = br.I64LE();                     // ChangeTime: not settable through .NET
                    uint attributes = br.Remaining >= 4 ? br.U32LE() : 0;
                    file.SetBasic(creation, lastAccess, lastWrite, attributes);
                    break;
                }
        }
    }

    /// <summary>
    /// The volume that holds the mapped directory. Windows takes the drive root (DriveInfo accepts only roots there);
    /// Unix passes the directory itself, which DriveInfo measures with statvfs — so /Volumes/USB, /media/… or a separate
    /// /home report their own size instead of the root file system's.
    /// </summary>
    internal static (long Total, long Free) VolumeSpace(string localPath)
    {
        try
        {
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(localPath) ?? localPath : localPath);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// A volume serial number that stays the same for a mapping across queries and sessions (FNV-1a of its name and
    /// path). It used to be derived from the free space, so it changed with every write and could look like new media.
    /// </summary>
    internal static uint VolumeSerial(DriveMapping mapping)
    {
        uint hash = 2166136261;
        foreach (char c in mapping.Name + "\0" + mapping.LocalPath)
        {
            hash = (hash ^ c) * 16777619;
        }
        return hash;
    }

    private static uint QueryVolume(DriveMapping mapping, uint infoClass, ByteWriter body)
    {
        (long total, long free) = VolumeSpace(mapping.LocalPath);
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
                    body.U32LE(VolumeSerial(mapping));      // VolumeSerialNumber
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
                    // FILE_CASE_PRESERVED_NAMES | FILE_UNICODE_ON_DISK (MS-FSCC 2.5.1). Not FILE_CASE_SENSITIVE_SEARCH:
                    // names are matched without regard to case on every platform, as on NTFS by default.
                    body.U32LE(0x00000006);
                    body.U32LE(255);                        // MaximumComponentNameLength (NTFS, and Unix NAME_MAX)
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
        CloseFiles();
    }

    private void CloseFiles()
    {
        lock (_filesGate)
        {
            foreach (DriveFile file in _files.Values)
            {
                try { file.Close(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
            _files.Clear();
        }
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
