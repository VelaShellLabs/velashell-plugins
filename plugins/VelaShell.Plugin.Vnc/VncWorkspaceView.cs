using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// The VNC workspace: the desktop above a 24px status bar, the remote-keys ball, and the host-style disconnected card.
/// Same chrome as the RDP workspace (DESIGN.md: no document header, 24px status bar, the SSH tab's disconnect card).
/// </summary>
internal sealed class VncWorkspaceView : Grid, IAsyncDisposable
{
    private static readonly Geometry WifiOffIcon = Geometry.Parse(
        "M12 20h.01 M8.5 16.429a5 5 0 0 1 7 0 M5 12.859a10 10 0 0 1 5.17-2.69 M19 12.859a10 10 0 0 0-2.007-1.523 " +
        "M2 8.82a15 15 0 0 1 4.177-2.643 M22 8.82a15 15 0 0 0-11.288-3.764 M2 2l20 20");   // Lucide "wifi-off"
    private static readonly Geometry RefreshIcon = Geometry.Parse(
        "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5");   // Lucide "refresh-cw"
    private static readonly Geometry CopyIcon = Geometry.Parse(
        "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2Z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2");   // Lucide "copy"

    // Lucide geometries (the same set the RDP remote-keys fan uses).
    private const string ShieldIcon = "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1 1 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1Z";
    private const string ArrowRightLeftIcon = "M8 3 4 7l4 4 M4 7h16 M16 21l4-4-4-4 M20 17H4";
    private const string ClipboardIcon = "M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1Z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2";
    private const string SquareXIcon = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M9 9l6 6 M15 9l-6 6";
    private const string LayoutGridIcon = "M4 3h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z M15 3h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z M4 14h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1Z M15 14h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1Z";
    private const string SquareTerminalIcon = "M7 11l2-2-2-2 M11 13h4 M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z";
    private const string LockIcon = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2Z M7 11V7a5 5 0 0 1 10 0v4";
    private const string CameraIcon = "M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3Z M9 13a3 3 0 1 0 6 0a3 3 0 1 0-6 0";

    internal const int TypeLimit = 2000, TypeConfirmLength = 100;

    private readonly IPluginContext _context;
    private readonly IVncSession _session;
    private readonly VncSettings _settings;
    private readonly VncClipboardBridge _clipboard;
    private readonly TextBlock _status = new() { Name = "VncStatus", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Button _reconnect = new() { Name = "VncReconnect" };
    private readonly TextBlock _reconnectLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly Button _dismiss = new() { Name = "VncDismiss" };
    private readonly TextBlock _dismissLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium };
    private readonly Button _copy = new();
    private readonly TextBlock _copyLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _overlay;
    private readonly Border _card = new();
    private readonly Border _badge = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly VncGlyph _badgeIcon = new() { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _overlayTitle = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _detailBox = new();
    private readonly SelectableTextBlock _overlayDetail = new() { Name = "VncOverlayDetail", TextWrapping = TextWrapping.Wrap };
    private Loc _loc;
    private string? _notice, _armedTyping, _disconnectDetail;
    private bool _connected = true, _failed, _reconnecting, _overlayError, _errorCopied;
    private int _disposed;

    internal Func<CancellationToken, Task>? Reconnect { get; set; }
    internal VncDesktopControl Desktop { get; }
    internal VncQuickBall QuickBall { get; }
    internal string StatusText => _status.Text ?? "";
    internal bool OverlayVisible => _overlay.IsVisible;

    public VncWorkspaceView(IVncSession session, IPluginContext context, VncSettings settings, Loc loc)
    {
        _loc = loc;
        _context = context;
        _session = session;
        _settings = settings;
        RowDefinitions = [with("*,24")];
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgPage");
        Desktop = new(session, settings) { DesktopSizeChanged = UpdateStatus };
        _clipboard = new(session, context, settings.Clipboard && !settings.ViewOnly);
        Desktop.PublishClipboard = () => _clipboard.PublishLocalAsync();
        Desktop.OperationFailed = () => { _failed = true; UpdateStatus(); };
        session.InfoChanged += OnInfoChanged;
        QuickBall = new VncQuickBall(QuickActions(), loc) { Folded = () => { _armedTyping = null; _ = Desktop.Focus(); } };
        QuickBall.IsVisible = !settings.ViewOnly;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (QuickBall.IsExpanded && e.Key == Key.Escape) { QuickBall.Collapse(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; UpdateStatus(); };
        // The encoding in the status bar follows what the server actually sends; refresh it now and then, not per frame.
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        var footer = new Border { Name = "VncStatusBar", Height = 24, BorderThickness = new Thickness(0, 1, 0, 0), Child = _status, ClipToBounds = true };
        Token(footer, Border.BackgroundProperty, "VelaBgSidebar");
        Token(footer, Border.BorderBrushProperty, "VelaBorderPrimary");
        Token(_status, TextBlock.ForegroundProperty, "VelaTextSecondary");
        Token(_status, TextBlock.FontFamilyProperty, "VelaUiFont");
        Token(_status, TextBlock.FontSizeProperty, "VelaFontSize11");
        var scroller = new ScrollViewer
        {
            Content = Desktop,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        _overlay = BuildOverlay();
        SetRow(footer, 1);
        Children.Add(scroller);
        Children.Add(QuickBall);
        Children.Add(_overlay);
        Children.Add(footer);
        ApplyLocale(loc);
    }

    /// <summary>
    /// What the floating button offers: combinations the local OS keeps for itself (no global keyboard hook, so pressing
    /// them locally never reaches the remote), plus typing the clipboard. Works for Windows, Linux and macOS desktops.
    /// </summary>
    private IEnumerable<VncQuickAction> QuickActions()
    {
        (uint, uint) ctrl = (VncInput.ControlL, 0x1d), alt = (VncInput.AltL, 0x38), super = (VncInput.SuperL, 0x15b);
        Func<Task<bool>> Chord(params (uint, uint)[] keys) => () => { Desktop.SendChord(keys); return Task.FromResult(true); };
        yield return new("SecureAttention", "QuickSecureAttention", "Ctrl+Alt+Del", ShieldIcon, 0, Chord(ctrl, alt, (VncInput.Delete, 0x153)));
        yield return new("SwitchWindow", "QuickSwitchWindow", "Alt+Tab", ArrowRightLeftIcon, 0, Chord(alt, (VncInput.Tab, 0x0f)));
        yield return new("CloseWindow", "QuickCloseWindow", "Alt+F4", SquareXIcon, 0, Chord(alt, (VncInput.F4, 0x3e)));
        yield return new("TypeClipboard", "QuickTypeClipboard", null, ClipboardIcon, 0, TypeClipboardAsync);
        yield return new("Start", "QuickStart", "Super", LayoutGridIcon, 1, Chord(super));
        yield return new("Run", "QuickRun", "Win+R", SquareTerminalIcon, 1, Chord(super, ('r', 0x13)));
        yield return new("Lock", "QuickLock", "Win+L", LockIcon, 1, Chord(super, ('l', 0x26)));
        yield return new("PrintScreen", "QuickPrintScreen", "PrtSc", CameraIcon, 1, Chord((VncInput.Print, 0x137)));
    }

    /// <summary>
    /// Reads the local clipboard and types it. Text over <see cref="TypeConfirmLength"/> characters, or with line breaks
    /// (sent as Enter, so a console would run them), needs a second click; over <see cref="TypeLimit"/> it is refused.
    /// </summary>
    private async Task<bool> TypeClipboardAsync()
    {
        string? text;
        try { text = await _context.Clipboard.GetTextAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowNotice(_loc["TypeClipboardUnavailable"]); return true; }
        string typed = VncInput.NormalizeTypedText(text ?? "");
        if (typed.Length == 0) { ShowNotice(_loc["TypeClipboardEmpty"]); return true; }
        if (typed.Length > TypeLimit) { ShowNotice(_loc.Format("TypeClipboardTooLong", typed.Length, TypeLimit)); return true; }
        bool lines = typed.Contains('\n');
        if ((lines || typed.Length > TypeConfirmLength) && !(QuickBall.IsArmed("TypeClipboard") && _armedTyping == typed))
        {
            _armedTyping = typed;
            QuickBall.Arm("TypeClipboard");
            ShowNotice(_loc.Format(lines ? "TypeClipboardConfirmLines" : "TypeClipboardConfirm", typed.Length));
            return false;
        }
        _armedTyping = null;
        ClearNotice();
        Desktop.TypeText(typed);
        return true;
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        UpdateStatus();
    }

    private void ClearNotice()
    {
        _noticeTimer.Stop();
        _notice = null;
        UpdateStatus();
    }

    private void OnInfoChanged() => Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref _disposed) == 0) { UpdateStatus(); } });

    private static void Token(AvaloniaObject target, AvaloniaProperty property, string token) =>
        target[!property] = new DynamicResourceExtension(token);

    private Border BuildOverlay()
    {
        _badgeIcon.RestToken = "VelaError";
        _badgeIcon.Data = WifiOffIcon;
        _badge.Child = _badgeIcon;
        Token(_badge, Border.BackgroundProperty, "VelaErrorSurface");
        Token(_overlayTitle, TextBlock.ForegroundProperty, "VelaTextPrimary");
        Token(_overlayTitle, TextBlock.FontFamilyProperty, "VelaUiFont");
        Token(_overlayTitle, TextBlock.FontSizeProperty, "VelaFontSize16");
        Token(_overlayDetail, TextBlock.ForegroundProperty, "VelaTextMuted");
        Token(_overlayDetail, TextBlock.FontFamilyProperty, "VelaUiMonoFont");
        Token(_overlayDetail, TextBlock.FontSizeProperty, "VelaFontSize11");
        _detailBox.Child = new ScrollViewer
        {
            MaxHeight = 200,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _overlayDetail
        };

        _copy.HorizontalAlignment = HorizontalAlignment.Center;
        _copy.Background = Brushes.Transparent;
        _copy.BorderThickness = new Thickness(1);
        _copy.Height = 24;
        _copy.Padding = new Thickness(8, 0);
        _copy.CornerRadius = new CornerRadius(4);
        _copy.Cursor = new Cursor(StandardCursorType.Hand);
        Token(_copy, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, "VelaBorderPrimary");
        Token(_copyLabel, TextBlock.ForegroundProperty, "VelaTextSecondary");
        Token(_copyLabel, TextBlock.FontSizeProperty, "VelaFontSize10");
        _copy.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children = { new VncGlyph { Data = CopyIcon, Width = 11, Height = 11, RestToken = "VelaTextSecondary", VerticalAlignment = VerticalAlignment.Center }, _copyLabel }
        };
        _copy.Click += OnCopyClick;

        _reconnect.CornerRadius = new CornerRadius(4);
        _reconnect.Padding = new Thickness(16, 8);
        _reconnect.BorderThickness = new Thickness(0);
        Token(_reconnect, Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, "VelaAccent");
        Token(_reconnectLabel, TextBlock.ForegroundProperty, "VelaAccentForeground");
        Token(_reconnectLabel, TextBlock.FontSizeProperty, "VelaFontSize12");
        _reconnect.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new VncGlyph { Data = RefreshIcon, Width = 13, Height = 13, RestToken = "VelaAccentForeground", VerticalAlignment = VerticalAlignment.Center }, _reconnectLabel }
        };
        _reconnect.Click += OnReconnectClick;

        _dismiss.Background = Brushes.Transparent;
        _dismiss.BorderThickness = new Thickness(1);
        _dismiss.CornerRadius = new CornerRadius(4);
        _dismiss.Padding = new Thickness(16, 8);
        Token(_dismiss, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, "VelaBorderSecondary");
        Token(_dismissLabel, TextBlock.ForegroundProperty, "VelaTextSecondary");
        Token(_dismissLabel, TextBlock.FontSizeProperty, "VelaFontSize12");
        _dismiss.Content = _dismissLabel;
        _dismiss.Click += (_, _) => _overlay.IsVisible = false;

        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.CornerRadius = new CornerRadius(8);
        _card.Padding = new Thickness(32, 24);
        _card.BorderThickness = new Thickness(1);
        Token(_card, Border.BackgroundProperty, "VelaBgSurface");
        Token(_card, Border.BorderBrushProperty, "VelaBorderSecondary");
        Token(_card, Border.BoxShadowProperty, "VelaShadowWindow");
        _card.Child = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                _badge, _overlayTitle, _detailBox, _copy,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = { _reconnect, _dismiss } }
            }
        };
        ApplyOverlayStyle(error: false);
        var scrim = new Border { Name = "VncOverlay", IsVisible = false, Child = _card };
        Token(scrim, Border.BackgroundProperty, "VelaScrim");
        return scrim;
    }

    /// <summary>A closed connection is one centred sentence in a 340px card; a failure is raw text in a 560px card with Copy.</summary>
    private void ApplyOverlayStyle(bool error)
    {
        _overlayError = error;
        _card.Width = error ? 560 : 340;
        _overlayDetail.TextAlignment = error ? TextAlignment.Left : TextAlignment.Center;
        _overlayDetail.MaxWidth = error ? double.PositiveInfinity : 280;
        _overlayDetail.LineHeight = error ? 18 : double.NaN;
        _detailBox.Padding = error ? new Thickness(14, 12) : new Thickness(0);
        _detailBox.BorderThickness = new Thickness(error ? 1 : 0);
        _detailBox.CornerRadius = new CornerRadius(error ? 4 : 0);
        if (error)
        {
            Token(_detailBox, Border.BackgroundProperty, "VelaBgInput");
            Token(_detailBox, Border.BorderBrushProperty, "VelaBorderPrimary");
        }
        else
        {
            _detailBox.Background = Brushes.Transparent;
        }
        _copy.IsVisible = error;
        _errorCopied = false;
        UpdateOverlayLabels();
    }

    private void ShowOverlayMessage(string detail, bool error)
    {
        ApplyOverlayStyle(error);
        _overlayDetail.Text = detail;
        _detailBox.IsVisible = detail.Length > 0;
    }

    private void UpdateOverlayLabels()
    {
        _overlayTitle.Text = _reconnecting ? _loc["Reconnecting"] : _loc[_overlayError ? "OverlayFailedTitle" : "OverlayClosedTitle"];
        Token(_badge, Border.BackgroundProperty, _reconnecting ? "VelaAccentDim" : "VelaErrorSurface");
        _badgeIcon.RestToken = _reconnecting ? "VelaAccent" : "VelaError";
        _badgeIcon.Data = _reconnecting ? RefreshIcon : WifiOffIcon;
        _reconnectLabel.Text = _loc["ReconnectButton"];
        _dismissLabel.Text = _loc["DismissOverlay"];
        _copyLabel.Text = _loc[_errorCopied ? "ErrorCopied" : "CopyError"];
        ToolTip.SetTip(_copy, _loc["CopyErrorTip"]);
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_overlayDetail.Text is not { Length: > 0 } text) { return; }
        try
        {
            await _context.Clipboard.SetTextAsync(text, CancellationToken.None);
            _errorCopied = true;
            UpdateOverlayLabels();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Warn("Could not copy the VNC error message.", ex);
        }
    }

    /// <summary>What the card says after a failed reconnect: the connector's localized reason.</summary>
    internal static string ReconnectFailure(Exception ex, Loc loc) => ex switch
    {
        PluginSdk.Protocols.ProtocolAuthenticationException or PluginSdk.Protocols.ProtocolCertificateTrustException => $"{ex.Message} {loc["Reconnect"]}",
        PluginSdk.Protocols.ProtocolConnectionException or PluginSdk.Protocols.ProtocolUnsupportedException => ex.Message,
        _ => loc["ReconnectFailed"]
    };

    private async void OnReconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_reconnecting || Reconnect is not { } reconnect) { return; }
        _reconnecting = true;
        _reconnect.IsEnabled = false;
        _dismiss.IsEnabled = false;
        ShowOverlayMessage("", error: false);
        try { await reconnect(default); }
        catch (Exception ex)
        {
            _reconnecting = false;
            ShowOverlayMessage(ReconnectFailure(ex, _loc), error: true);
            _reconnect.IsEnabled = true;
        }
        finally
        {
            _reconnecting = false;
            _dismiss.IsEnabled = true;
            UpdateOverlayLabels();
        }
    }

    internal void ApplyLocale(Loc loc)
    {
        _loc = loc;
        QuickBall.ApplyLocale(loc);
        UpdateOverlayLabels();
        UpdateStatus();
    }

    internal void SetConnected(bool connected, string? detail = null)
    {
        _connected = connected;
        _disconnectDetail = connected ? null : detail;
        Desktop.IsEnabled = connected;
        if (!connected) { QuickBall.Collapse(); ClearNotice(); }
        QuickBall.IsVisible = connected && !_settings.ViewOnly;
        _overlay.IsVisible = !connected;
        if (!connected && !_reconnecting)
        {
            ShowOverlayMessage(detail ?? _loc["Disconnected"], error: false);
            _reconnect.IsEnabled = Reconnect is not null;
            _reconnect.IsVisible = Reconnect is not null;
            _dismiss.IsEnabled = true;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_connected) { _status.Text = _disconnectDetail ?? _loc["Disconnected"]; return; }
        if (_notice is { } notice) { _status.Text = notice; return; }
        if (_failed) { _status.Text = _loc["InputFailed"]; return; }
        VncSessionInfo info = _session.Info;
        var parts = new List<string> { _loc["Connected"], _loc.Format("Resolution", Desktop.DesktopWidth, Desktop.DesktopHeight) };
        if (info.DesktopName.Length > 0) { parts.Add(info.DesktopName); }
        parts.Add($"RFB {info.ProtocolVersion}");
        parts.Add(info.Tls ? info.Security + " · TLS" : info.Security);
        if (_session.LastEncoding.Length > 0) { parts.Add(_session.LastEncoding); }
        if (_settings.ViewOnly) { parts.Add(_loc["ViewOnlyStatus"]); }
        _status.Text = string.Join(" · ", parts.Where(static p => p.Length > 0));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _session.InfoChanged -= OnInfoChanged;
        _noticeTimer.Stop();
        _statusTimer.Stop();
        _clipboard.Dispose();
        await Desktop.DisposeAsync();
        Children.Clear();
    }
}
