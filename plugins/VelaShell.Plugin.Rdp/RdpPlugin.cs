using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// Registers the managed RDP workspace. The connection form's drive-mapping page lists the local
/// disks; a light watcher (≥5 s, stopped on shutdown/deactivation, per the plugin performance rules)
/// re-registers the descriptor when the set of disks changes, so a USB disk plugged in later shows up
/// the next time the connection dialog opens.
/// </summary>
[VelaPlugin]
public sealed class RdpPlugin : IVelaPlugin
{
    private static readonly TimeSpan DiskPollInterval = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlyList<LocalDisk>> _enumerateDisks;
    private readonly Func<IReadOnlyList<PrinterMapping>> _enumeratePrinters;
    private IReadOnlyList<PrinterMapping> _printers = [];
    private readonly Func<IReadOnlyList<RdpCameraDevice>> _enumerateCameras;
    private IReadOnlyList<RdpCameraDevice> _cameras = [];
    private Task? _cameraWatch;
    private readonly Func<IReadOnlyList<RdpWpdDevice>> _enumerateWpdDevices;
    private IReadOnlyList<RdpWpdDevice> _wpdDevices = [];
    private Task? _wpdWatch;
    private readonly TimeSpan _pollInterval;
    private readonly Lock _gate = new();
    private IPluginContext? _context;
    private RdpWorkspaceProvider? _provider;
    private IDisposable? _registration;
    private IReadOnlyList<LocalDisk> _disks = [];
    private CancellationTokenSource? _watchStop;
    private Task? _watch;
    private Task? _printerWatch;

    /// <summary>Creates the plugin (host entry point).</summary>
    public RdpPlugin() : this(LocalDisks.Enumerate, DiskPollInterval, WindowsPrinterCatalog.GetPrinters, RdpCameraDevices.Enumerate, RdpWpdDevices.Enumerate)
    {
    }

    /// <summary>Test seam: inject the disk source and poll interval.</summary>
    internal RdpPlugin(Func<IReadOnlyList<LocalDisk>> enumerateDisks, TimeSpan pollInterval,
        Func<IReadOnlyList<PrinterMapping>>? enumeratePrinters = null,
        Func<IReadOnlyList<RdpCameraDevice>>? enumerateCameras = null,
        Func<IReadOnlyList<RdpWpdDevice>>? enumerateWpdDevices = null)
    {
        _enumerateDisks = enumerateDisks;
        _enumeratePrinters = enumeratePrinters ?? (() => []);
        _enumerateCameras = enumerateCameras ?? (() => []);
        _enumerateWpdDevices = enumerateWpdDevices ?? (() => []);
        _pollInterval = pollInterval;
    }

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _context = context;
            _provider = new(context, new Loc(context.Host.Locale));
            // Enumeration reads drive letters/types or mount points only — no media access — so
            // activation stays instant.
            _disks = _enumerateDisks();
            _registration = context.Workspaces.Register(Describe(_provider.Loc, _disks, _printers, _cameras), _provider);
        }
        context.Events.LocaleChanged += OnLocaleChanged;
        _watchStop = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown);
        // Printer discovery runs off-thread; activation never waits for the spooler.
        _watch = WatchDisksAsync(_watchStop.Token);
        _printerWatch = WatchPrintersAsync(_watchStop.Token);
        _cameraWatch = WatchCamerasAsync(_watchStop.Token);
        _wpdWatch = WatchWpdDevicesAsync(_watchStop.Token);
        return Task.CompletedTask;
    }

    private WorkspaceDescriptor Describe(Loc loc, IReadOnlyList<LocalDisk> disks, IReadOnlyList<PrinterMapping> printers, IReadOnlyList<RdpCameraDevice> cameras) => new()
    {
        Id = "velashell.rdp",
        DisplayName = "RDP",
        DefaultPort = 3389,
        Icon = PluginIcon.Stroked("M3 4h18v12H3z M8 20h8 M12 16v4"),
        HostLabel = loc["Host"],
        HostPlaceholder = "rdp.example.com",
        UsernameLabel = loc["User"],
        PasswordLabel = loc["Password"],
        Pages =
        [
            new ProtocolSettingPage(RdpSettings.DrivesPage, loc["DrivesPage"]),
            new ProtocolSettingPage(RdpSettings.ExperiencePage, loc["ExperiencePage"])
        ],
        Fields = RdpSettings.Declare(loc, disks, printers, cameras, _wpdDevices),
        Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
        TrustedThumbprintSettingKey = RdpSettings.TrustedThumbprintKey
    };

    private void OnLocaleChanged(string locale)
    {
        lock (_gate)
        {
            if (_context is not { } context || _provider is not { } provider)
            {
                return;
            }

            provider.Loc = new(locale);
            _registration = context.Workspaces.Register(Describe(provider.Loc, _disks, _printers, _cameras), provider);
        }
    }

    private async Task WatchDisksAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                RefreshDisks();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WatchPrintersAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do { await RefreshPrintersAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchWpdDevicesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DiskPollInterval);
        try
        {
            do
            {
                try
                {
                    IReadOnlyList<RdpWpdDevice> devices = await Task.Run(_enumerateWpdDevices, ct).WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!ct.IsCancellationRequested && _context is { } context && _provider is { } provider
                            && !devices.SequenceEqual(_wpdDevices))
                        {
                            _wpdDevices = devices;
                            _registration = context.Workspaces.Register(Describe(provider.Loc, _disks, _printers, _cameras), provider);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context?.Log.Warn("Refreshing the RDP WPD device list failed.", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchCamerasAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    IReadOnlyList<RdpCameraDevice> cameras = await Task.Run(_enumerateCameras, ct).WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!ct.IsCancellationRequested && _context is { } context && _provider is { } provider
                            && !cameras.SequenceEqual(_cameras))
                        {
                            _cameras = cameras;
                            _registration = context.Workspaces.Register(Describe(provider.Loc, _disks, _printers, cameras), provider);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context?.Log.Warn("Refreshing the RDP camera list failed.", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshPrintersAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<PrinterMapping> printers = await Task.Run(_enumeratePrinters, ct).WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (ct.IsCancellationRequested || _context is not { } context || _provider is not { } provider
                    || printers.SequenceEqual(_printers)) { return; }
                _printers = printers;
                _registration = context.Workspaces.Register(Describe(provider.Loc, _disks, printers, _cameras), provider);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context?.Log.Warn("Refreshing the RDP installed printer list failed.", ex);
        }
    }

    /// <summary>
    /// Re-enumerates local disks and, only if the set changed, re-registers the descriptor. The same
    /// provider instance is passed, which the host treats as a refresh: open RDP tabs stay open, and an
    /// open connection dialog keeps what the user typed (the new disk appears on its next open).
    /// </summary>
    internal void RefreshDisks()
    {
        IPluginContext? log = null;
        try
        {
            IReadOnlyList<LocalDisk> disks = _enumerateDisks();
            lock (_gate)
            {
                log = _context;
                if (_context is not { } context || _provider is not { } provider || disks.SequenceEqual(_disks))
                {
                    return;
                }

                _disks = disks;
                _registration = context.Workspaces.Register(Describe(provider.Loc, disks, _printers, _cameras), provider);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A disk vanishing mid-enumeration must not kill the watcher; the next tick retries.
            log?.Log.Warn("Refreshing the RDP drive-mapping disk list failed.", ex);
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_context is { } context)
        {
            context.Events.LocaleChanged -= OnLocaleChanged;
        }

        // Stop the watcher before tearing down, so it cannot re-register after the registration is gone.
        _watchStop?.Cancel();
        if (_watch is { } watch)
        {
            await watch.ConfigureAwait(false);
        }

        if (_printerWatch is { } printerWatch) { await printerWatch.ConfigureAwait(false); }
        if (_cameraWatch is { } cameraWatch) { await cameraWatch.ConfigureAwait(false); }
        _cameraWatch = null;
        if (_wpdWatch is { } wpdWatch) { await wpdWatch.ConfigureAwait(false); }
        _wpdWatch = null;
        _watchStop?.Dispose();
        _watchStop = null;
        _watch = null;
        _printerWatch = null;

        IDisposable? registration;
        RdpWorkspaceProvider? provider;
        lock (_gate)
        {
            registration = _registration;
            provider = _provider;
            _registration = null;
            _provider = null;
            _context = null;
            _disks = [];
            _printers = [];
            _cameras = [];
            _wpdDevices = [];
        }
        registration?.Dispose();
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
