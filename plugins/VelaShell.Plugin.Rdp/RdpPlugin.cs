using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

/// <summary>Registers the embedded desktop lazily; discovery itself loads no native code.</summary>
[VelaPlugin]
public sealed class RdpPlugin : IVelaPlugin
{
    private readonly Lock _sync = new();
    private IPluginContext? _context;
    private RdpWorkspaceProvider? _provider;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var loc = new Loc(context.Host.Locale);
            _context = context;
            _provider = new(context, loc);
            _registration = context.Workspaces.Register(Describe(context.PluginId, loc), _provider);
            context.Events.LocaleChanged += OnLocaleChanged;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        RdpWorkspaceProvider? provider;
        IDisposable? registration;
        IPluginContext? context;
        lock (_sync)
        {
            context = _context;
            registration = _registration;
            _registration = null;
            provider = _provider;
            _provider = null;
            _context = null;
        }
        context?.Events.LocaleChanged -= OnLocaleChanged;
        registration?.Dispose();
        if (provider is not null) await provider.DisposeAsync().ConfigureAwait(false);
    }

    private void OnLocaleChanged(string locale)
    {
        IPluginContext? context;
        RdpWorkspaceProvider? provider;
        lock (_sync) { context = _context; provider = _provider; }
        if (context is null || provider is null) return;
        try
        {
            var loc = new Loc(locale);
            // Document callbacks run without either the plugin or provider monitor held.
            provider.UpdateLocale(loc);
            lock (_sync)
            {
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(_provider, provider)) return;
                // Same provider preserves documents. Do not dispose the superseded handle.
                _registration = context.Workspaces.Register(Describe(context.PluginId, loc), provider);
            }
        }
        catch
        {
            context.Log.Warn("RDP locale registration failed.");
        }
    }

    private static WorkspaceDescriptor Describe(string pluginId, Loc loc) => new()
    {
        Id = pluginId, DisplayName = "RDP", DefaultPort = 3389,
        Icon = PluginIcon.Stroked("M4 3h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M8 23h8 M12 19v4 M8 9l3 3-3 3 M13 15h3"),
        HostLabel = loc["Rdp_Host"], HostPlaceholder = "windows.example",
        UsernameLabel = loc["Rdp_User"], PasswordLabel = loc["Rdp_Password"],
        Fields = RdpSettings.Declare(loc),
        Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
        TrustedThumbprintSettingKey = RdpSettings.TrustedThumbprintKey
    };
}
