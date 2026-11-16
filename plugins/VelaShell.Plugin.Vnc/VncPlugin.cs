using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Registers the managed VNC workspace. Unlike RDP there are no local devices to enumerate, so there is no watcher: the
/// descriptor is registered once and again only when the host language changes.
/// </summary>
[VelaPlugin]
public sealed class VncPlugin : IVelaPlugin
{
    internal const string WorkspaceId = "velashell.vnc";
    private readonly Lock _gate = new();
    private IPluginContext? _context;
    private VncWorkspaceProvider? _provider;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _context = context;
            _provider = new(context, new Loc(context.Host.Locale));
            _registration = context.Workspaces.Register(Describe(_provider.Loc), _provider);
        }
        context.Events.LocaleChanged += OnLocaleChanged;
        return Task.CompletedTask;
    }

    internal static WorkspaceDescriptor Describe(Loc loc) => new()
    {
        Id = WorkspaceId,
        DisplayName = "VNC",
        DefaultPort = 5900,
        // Lucide "monitor-play": a screen being watched and driven, told apart from RDP's plain monitor at a glance.
        Icon = PluginIcon.Stroked("M10 7.75a.75.75 0 0 1 1.142-.638l3.664 2.249a.75.75 0 0 1 0 1.278l-3.664 2.25a.75.75 0 0 1-1.142-.64z M12 17v4 M8 21h8 M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"),
        HostLabel = loc["Host"],
        HostPlaceholder = "vnc.example.com",
        UsernameLabel = loc["User"],
        PasswordLabel = loc["Password"],
        Pages = [new ProtocolSettingPage(VncSettings.DisplayPage, loc["DisplayPage"])],
        Fields = VncSettings.Declare(loc),
        Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
        TrustedThumbprintSettingKey = VncSettings.TrustedThumbprintKey
    };

    private void OnLocaleChanged(string locale)
    {
        VncWorkspaceProvider? provider;
        lock (_gate) { provider = _context is null ? null : _provider; }
        if (provider is null) { return; }
        var loc = new Loc(locale);
        provider.Loc = loc;
        lock (_gate)
        {
            // The same provider instance: the host treats it as a refresh and open tabs stay open.
            if (_context is { } context && ReferenceEquals(_provider, provider))
            {
                _registration = context.Workspaces.Register(Describe(loc), provider);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_context is { } context) { context.Events.LocaleChanged -= OnLocaleChanged; }
        IDisposable? registration;
        VncWorkspaceProvider? provider;
        lock (_gate)
        {
            registration = _registration;
            provider = _provider;
            _registration = null;
            _provider = null;
            _context = null;
        }
        registration?.Dispose();
        if (provider is not null) { await provider.DisposeAsync().ConfigureAwait(false); }
    }
}
