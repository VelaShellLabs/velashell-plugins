using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
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
    private readonly Button _reconnect = new();
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly RdpSettings _settings;
    private Loc _loc;
    private bool _connected = true;
    private bool _failed;
    private bool _reconnecting;

    /// <summary>Reconnects the workspace; supplied by the owning document. Null hides the in-view button.</summary>
    internal Func<CancellationToken, Task>? Reconnect { get; set; }

    internal RdpDesktopControl Desktop => _desktop;
    public RdpWorkspaceView(IRdpSession session, IPluginContext context, RdpSettings settings, Loc loc)
    {
        _settings = settings;
        _loc = loc;
        RowDefinitions = new RowDefinitions("36,*,24");
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgPage");
        _desktop = new(session, settings.Width, settings.Height);
        _desktop.DesktopSizeChanged = UpdateStatus;
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
        _overlay = BuildOverlay();
        SetRow(scroller, 1);
        SetRow(_overlay, 1);
        SetRow(footer, 2);
        Children.Add(header);
        Children.Add(scroller);
        Children.Add(_overlay);
        Children.Add(footer);
        ApplyLocale(loc);
    }

    private Border BuildOverlay()
    {
        _overlayText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        _overlayText[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        _overlayText[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize14");
        _reconnect.Height = 28;
        _reconnect.Padding = new Thickness(16, 0);
        _reconnect.HorizontalAlignment = HorizontalAlignment.Center;
        _reconnect[!Button.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        _reconnect[!Button.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        _reconnect[!Button.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        _reconnect.Click += OnReconnectClick;
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(28, 20),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 14, Children = { _overlayText, _reconnect } }
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        return new Border { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Child = card };
    }

    private async void OnReconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_reconnecting || Reconnect is not { } reconnect) return;
        _reconnecting = true;
        _reconnect.IsEnabled = false;
        _overlayText.Text = _loc["Reconnecting"];
        try { await reconnect(default); }
        catch
        {
            // The owning document keeps this (disconnected) view alive on failure, so re-arm the button.
            _overlayText.Text = _loc["ReconnectFailed"];
            _reconnect.IsEnabled = true;
        }
        finally { _reconnecting = false; }
    }

    internal void ApplyLocale(Loc loc)
    {
        _loc = loc;
        _send.Content = loc["Send"];
        _receive.Content = loc["Receive"];
        _attention.Content = loc["SecureAttention"];
        _fit.Content = loc["Fit"];
        _actual.Content = loc["Actual"];
        _reconnect.Content = loc["ReconnectButton"];
        UpdateStatus();
    }
    internal void SetConnected(bool connected)
    {
        _connected = connected;
        _desktop.IsEnabled = connected;
        _send.IsEnabled = _receive.IsEnabled = connected && _settings.Clipboard;
        _attention.IsEnabled = connected;
        _fit.IsEnabled = _actual.IsEnabled = connected;
        _overlay.IsVisible = !connected;
        if (!connected && !_reconnecting)
        {
            _overlayText.Text = _loc["Disconnected"];
            _reconnect.IsEnabled = Reconnect is not null;
            _reconnect.IsVisible = Reconnect is not null;
        }
        UpdateStatus();
    }
    private void UpdateStatus() => _status.Text = !_connected ? _loc["Disconnected"]
        : _failed ? _loc["InputFailed"]
        : _loc["Connected"] + " · " + _loc.Format("Resolution", _desktop.DesktopWidth, _desktop.DesktopHeight);
    public async ValueTask DisposeAsync()
    {
        try { await _clipboard.DisposeAsync(); }
        finally { await _desktop.DisposeAsync(); Children.Clear(); }
    }
}
