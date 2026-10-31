using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

/// <summary>Registers the embedded FreeRDP workspace.</summary>
[VelaPlugin]
public sealed class RdpPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private RdpWorkspaceProvider? _provider;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _provider = new(context, new Loc(context.Host.Locale));
        _registration = context.Workspaces.Register(Describe(_provider.Loc), _provider);
        context.Events.LocaleChanged += OnLocaleChanged;
        return Task.CompletedTask;
    }
    private static WorkspaceDescriptor Describe(Loc loc) => new()
    {
        Id = "velashell.rdp",
        DisplayName = "RDP",
        DefaultPort = 3389,
        Icon = PluginIcon.Stroked("M3 4h18v12H3z M8 20h8 M12 16v4"),
        HostLabel = loc["Host"],
        HostPlaceholder = "rdp.example.com",
        UsernameLabel = loc["User"],
        PasswordLabel = loc["Password"],
        Fields = RdpSettings.Declare(loc),
        Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
        TrustedThumbprintSettingKey = RdpSettings.TrustedThumbprintKey
    };
    private void OnLocaleChanged(string locale)
    {
        if (_context is not { } context || _provider is not { } provider)
        {
            return;
        }

        provider.Loc = new(locale);
        _registration = context.Workspaces.Register(Describe(provider.Loc), provider);
    }
    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_context is { } context)
        {
            context.Events.LocaleChanged -= OnLocaleChanged;
        }

        _registration?.Dispose();
        _registration = null;
        if (_provider is { } provider)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        _provider = null;
        _context = null;
    }
}
