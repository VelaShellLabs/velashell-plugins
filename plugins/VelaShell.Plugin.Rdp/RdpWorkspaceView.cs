using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpWorkspaceView : Grid, IAsyncDisposable
{
    private readonly RdpDesktopControl _desktop;
    private readonly RdpClipboardBridge _clipboard;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Button _send = new();
    private readonly Button _receive = new();
    private readonly Button _attention = new();
    private readonly Button _fit = new();
    private readonly Button _actual = new();
    private readonly RdpSettings _settings;
    private Loc _loc;
    private bool _connected = true;
    private bool _failed;

    internal RdpDesktopControl Desktop => _desktop;
    public RdpWorkspaceView(IRdpSession session, IPluginContext context, RdpSettings settings, Loc loc)
    {
        _settings = settings;
        _loc = loc;
        RowDefinitions = new RowDefinitions("36,*,24");
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgPage");
        _desktop = new(session, settings.Width, settings.Height);
        _clipboard = new(session, context, _desktop, settings.Clipboard);
        _desktop.PublishClipboard = () => _clipboard.PublishLocalAsync();
        _desktop.OperationFailed = () => { _failed = true; UpdateStatus(); };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (Button button in new[] { _send, _receive, _attention, _fit, _actual })
        {
            button.Height = 26;
            button.Padding = new Thickness(8, 0);
            button[!Button.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
            button[!Button.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
            button[!Button.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            toolbar.Children.Add(button);
        }
        _send.IsEnabled = _receive.IsEnabled = settings.Clipboard;
        _send.Click += (_, _) => _desktop.Queue(() => _clipboard.PublishLocalAsync());
        _receive.Click += (_, _) => _desktop.Queue(() => _clipboard.ReceiveRemoteAsync());
        _attention.Click += (_, _) => { _desktop.SendSecureAttention(); _desktop.Focus(); };
        _fit.Click += (_, _) => _desktop.Fit = true;
        _actual.Click += (_, _) => _desktop.Fit = false;
        var header = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
        header[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        header[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        var footer = new Border { Child = _status };
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSidebar");
        _status[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        _status[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        _status[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        var scroller = new ScrollViewer
        {
            Content = _desktop,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        SetRow(scroller, 1);
        SetRow(footer, 2);
        Children.Add(header);
        Children.Add(scroller);
        Children.Add(footer);
        ApplyLocale(loc);
    }
    internal void ApplyLocale(Loc loc)
    {
        _loc = loc;
        _send.Content = loc["Send"];
        _receive.Content = loc["Receive"];
        _attention.Content = loc["SecureAttention"];
        _fit.Content = loc["Fit"];
        _actual.Content = loc["Actual"];
        UpdateStatus();
    }
    internal void SetConnected(bool connected)
    {
        _connected = connected;
        _desktop.IsEnabled = connected;
        _send.IsEnabled = _receive.IsEnabled = connected && _settings.Clipboard;
        _attention.IsEnabled = connected;
        UpdateStatus();
    }
    private void UpdateStatus() => _status.Text = !_connected ? _loc["Disconnected"]
        : _failed ? _loc["InputFailed"] : _loc["Connected"] + " · " + _loc.Format("Resolution", _settings.Width, _settings.Height);
    public async ValueTask DisposeAsync()
    {
        try { await _clipboard.DisposeAsync(); }
        finally { await _desktop.DisposeAsync(); Children.Clear(); }
    }
}
