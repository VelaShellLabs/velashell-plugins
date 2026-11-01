using System.Threading.Channels;

namespace VelaShell.Rdp.Channels;

internal sealed record WpdLocalDevice(string Path, string Name, byte[] HardwareIds, byte[] CompatibleIds, string? StableIdentity);
internal sealed record WpdDeviceChange(string Path, bool Removed);
internal interface IWpdNotifications
{
    IDisposable Register(Action<WpdDeviceChange> changed);
}
internal interface IWpdDeviceMonitor : IAsyncDisposable
{
    void Start(Func<uint, Task> removed, Func<WpdDeviceDescription, Task> added);
}

internal sealed class WpdDeviceMonitor : IWpdDeviceBackend, IWpdDeviceMonitor
{
    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<WpdLocalDevice>> _enumerate;
    private readonly Func<WpdLocalDevice, WpdOpenOptions, IWpdDeviceHandle> _open;
    private readonly Action<string> _log;
    private readonly Channel<WpdDeviceChange> _events = Channel.CreateBounded<WpdDeviceChange>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<uint, WpdLocalDevice> _present = [];
    private readonly WpdLocalDevice[] _authorized;
    private readonly IDisposable? _registration;
    private Task? _worker, _disposing;
    private uint _nextId;
    private int _closed, _overflow;

    internal WpdDeviceMonitor(IEnumerable<string> selectedIds, Func<IReadOnlyList<WpdLocalDevice>> enumerate,
        Func<WpdLocalDevice, WpdOpenOptions, IWpdDeviceHandle> open, IWpdNotifications notifications, Action<string> log)
    {
        _enumerate = enumerate; _open = open; _log = log;
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        if (selected.Count > 32) throw new ArgumentException("At most 32 WPD interfaces can be selected.", nameof(selectedIds));
        try { _registration = notifications.Register(Enqueue); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
        { _log($"WPD device notifications unavailable ({ex.Message}); idle removal and automatic reinsertion are disabled; I/O errors still revoke devices."); }
        try
        {
            _authorized = enumerate().Where(d => selected.Contains(d.Path)).ToArray();
            foreach (WpdLocalDevice device in _authorized)
            {
                _present.Add(++_nextId, device);
                if (device.StableIdentity is null) _log("A selected WPD interface has no verified system-unique instance identity; removal is monitored, but automatic reinsertion is disabled for it.");
            }
        }
        catch { _registration?.Dispose(); throw; }
    }

    private void Enqueue(WpdDeviceChange change)
    {
        if (Volatile.Read(ref _closed) == 0 && !_events.Writer.TryWrite(change)) Interlocked.Exchange(ref _overflow, 1);
    }
    public IReadOnlyList<WpdDeviceDescription> Devices
    { get { lock (_gate) return _present.Select(d => Describe(d.Key, d.Value)).ToArray(); } }
    public IWpdDeviceHandle Open(uint id, WpdOpenOptions options)
    {
        lock (_gate)
        {
            if (_closed != 0 || !_present.TryGetValue(id, out WpdLocalDevice? device)) throw new System.ComponentModel.Win32Exception(1167);
            return _open(device, options);
        }
    }
    public void Start(Func<uint, Task> removed, Func<WpdDeviceDescription, Task> added)
    {
        lock (_gate)
        {
            if (_closed != 0 || _worker is not null) return;
            _worker = Task.Run(() => RunAsync(removed, added));
        }
    }
    private async Task RunAsync(Func<uint, Task> removed, Func<WpdDeviceDescription, Task> added)
    {
        await foreach (WpdDeviceChange change in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Volatile.Read(ref _closed) != 0) break;
            try
            {
                if (Interlocked.Exchange(ref _overflow, 0) != 0)
                {
                    _log("WPD notification queue overflow; revoking all devices rather than retaining potentially stale handles.");
                    await RevokeAsync(_ => true, removed).ConfigureAwait(false);
                }
                if (change.Removed) await RevokeAsync(d => StringComparer.OrdinalIgnoreCase.Equals(d.Path, change.Path), removed).ConfigureAwait(false);
                IReadOnlyList<WpdLocalDevice> current = _enumerate();
                await RevokeAsync(d => !current.Any(n => SamePresentDevice(d, n)), removed).ConfigureAwait(false);
                if (change.Removed) continue;
                foreach (WpdLocalDevice authorized in _authorized)
                {
                    if (authorized.StableIdentity is null) continue;
                    WpdLocalDevice[] matches = current.Where(d => SameIdentity(authorized, d)).ToArray();
                    if (matches.Length != 1) continue;
                    uint id;
                    lock (_gate)
                    {
                        if (_closed != 0) return;
                        if (_present.Values.Any(d => SameIdentity(authorized, d))) continue;
                        id = checked(++_nextId);
                        _present.Add(id, matches[0]);
                    }
                    await added(Describe(id, matches[0])).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log($"WPD notification processing failed ({ex.GetType().Name}); revoking active devices.");
                await RevokeAsync(_ => true, removed).ConfigureAwait(false);
            }
        }
    }
    private async Task RevokeAsync(Func<WpdLocalDevice, bool> predicate, Func<uint, Task> removed)
    {
        uint[] ids;
        lock (_gate)
        {
            ids = _present.Where(p => predicate(p.Value)).Select(p => p.Key).ToArray();
            foreach (uint id in ids) _present.Remove(id);
        }
        foreach (uint id in ids) await removed(id).ConfigureAwait(false);
    }
    private static bool SameIdentity(WpdLocalDevice a, WpdLocalDevice b) => a.StableIdentity is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(a.StableIdentity, b.StableIdentity) && a.HardwareIds.AsSpan().SequenceEqual(b.HardwareIds);
    private static bool SamePresentDevice(WpdLocalDevice a, WpdLocalDevice b) =>
        StringComparer.OrdinalIgnoreCase.Equals(a.Path, b.Path) && (a.StableIdentity is null ? b.StableIdentity is null : SameIdentity(a, b));
    private static WpdDeviceDescription Describe(uint id, WpdLocalDevice device) => new(id, device.Name, device.HardwareIds, device.CompatibleIds);
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposing ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _events.Writer.TryComplete();
        // Unregister waits for callbacks and must never run on the native callback thread.
        try { await Task.Run(() => _registration?.Dispose()).ConfigureAwait(false); }
        finally
        {
            try { if (_worker is not null) await _worker.ConfigureAwait(false); }
            finally { lock (_gate) _present.Clear(); }
        }
    }
}
