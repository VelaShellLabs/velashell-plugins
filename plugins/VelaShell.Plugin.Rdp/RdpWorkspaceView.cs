using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpWorkspaceView : Grid, IAsyncDisposable
{
    private readonly RdpClipboardBridge _clipboard;
    private readonly DispatcherTimer _transferTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Grid _transfers = new() { ColumnDefinitions = [with("*,*")], ClipToBounds = true };
    private readonly TextBlock _downloadText = new() { Name = "ClipboardDownloadProgress", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _uploadText = new() { Name = "ClipboardUploadProgress", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _downloadBar = new() { Height = 4, Maximum = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar _uploadBar = new() { Height = 4, Maximum = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cancelDownload = new() { Name = "CancelClipboardDownload", Focusable = false, Height = 24 };
    private readonly Button _cancelUpload = new() { Name = "CancelClipboardUpload", Focusable = false, Height = 24 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Button _attention = new();
    private readonly Button _reconnect = new();
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private Loc _loc;
    private bool _connected = true;
    private bool _failed;
    private bool _reconnecting;
    private string? _disconnectDetail;

    /// <summary>Reconnects the workspace; supplied by the owning document. Null hides the in-view button.</summary>
    internal Func<CancellationToken, Task>? Reconnect { get; set; }

    internal RdpDesktopControl Desktop { get; }
    public RdpWorkspaceView(IRdpSession session, IPluginContext context, RdpSettings settings, Loc loc)
    {
        _loc = loc;
        RowDefinitions = [with("36,*,24")];
        this[!BackgroundProperty] = new DynamicResourceExtension("VelaBgPage");
        Desktop = new(session, settings.Width, settings.Height)
        {
            DesktopSizeChanged = UpdateStatus
        };
        _clipboard = new(session, context, Desktop, settings.Clipboard);
        Desktop.PublishClipboard = () => _clipboard.PublishLocalAsync();
        _transfers.Children.Add(BuildTransferRow(_downloadText, _downloadBar, _cancelDownload));
        var uploadRow = BuildTransferRow(_uploadText, _uploadBar, _cancelUpload);
        SetColumn(uploadRow, 1);
        _transfers.Children.Add(uploadRow);
        _cancelDownload.Click += (_, _) => { _clipboard.CancelTransfer(false); UpdateTransfers(); };
        _cancelUpload.Click += (_, _) => { _clipboard.CancelTransfer(true); UpdateTransfers(); };
        _transferTimer.Tick += (_, _) => UpdateTransfers();
        if (settings.Clipboard) { _transferTimer.Start(); }
        Desktop.OperationFailed = () => { _failed = true; UpdateStatus(); };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        // The desktop always follows the window (Fit stays on); no manual zoom toggle in the toolbar.
        foreach (Button button in new[] { _attention })
        {
            button.Height = 26;
            button.Padding = new Thickness(8, 0);
            button[!Button.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
            button[!Button.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
            button[!Button.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            toolbar.Children.Add(button);
        }
        _attention.Click += (_, _) => { Desktop.SendSecureAttention(); _ = Desktop.Focus(); };
        var header = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
        header[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        header[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");
        var footerContent = new Grid { ColumnDefinitions = [with("Auto,*")], ClipToBounds = true };
        _status.MaxWidth = 260;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        SetColumn(_transfers, 1);
        footerContent.Children.Add(_status);
        footerContent.Children.Add(_transfers);
        var footer = new Border { Name = "RdpStatusBar", Height = 24, Child = footerContent, ClipToBounds = true };
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSidebar");
        _status[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        _status[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        _status[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        var scroller = new ScrollViewer
        {
            Content = Desktop,
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

    private static Grid BuildTransferRow(TextBlock text, ProgressBar bar, Button cancel)
    {
        var row = new Grid { ColumnDefinitions = [with("3*,2*,24")], Height = 24, Margin = new Thickness(4, 0), IsVisible = false, ClipToBounds = true };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        text[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        bar.MaxWidth = 100;
        bar.MinWidth = 0;
        bar.Margin = new Thickness(4, 0);
        cancel.Width = 20;
        cancel.Height = 20;
        cancel.MinWidth = 0;
        cancel.MinHeight = 0;
        cancel.Padding = new Thickness(2);
        cancel.HorizontalAlignment = HorizontalAlignment.Center;
        cancel.VerticalAlignment = VerticalAlignment.Center;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform,
            StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M13 13H8a1 1 0 0 0-1 1v7 M14 8h1 M17 21v-4 M2 2l20 20 M20.41 20.41A2 2 0 0 1 19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 .59-1.41 M9 3h6.2a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V15")
        };
        icon[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        cancel.Content = icon;
        SetColumn(bar, 1);
        SetColumn(cancel, 2);
        row.Children.Add(text);
        row.Children.Add(bar);
        row.Children.Add(cancel);
        return row;
    }

    private void UpdateTransfers() => UpdateTransfers(_clipboard.DownloadProgress, _clipboard.UploadProgress);

    internal void UpdateTransfers(RdpClipboardProgress? download, RdpClipboardProgress? upload)
    {
        UpdateTransfer(download, false, _downloadText, _downloadBar, _cancelDownload);
        UpdateTransfer(upload, true, _uploadText, _uploadBar, _cancelUpload);
    }

    private void UpdateTransfer(RdpClipboardProgress? progress, bool upload, TextBlock text, ProgressBar bar, Button cancel)
    {
        ((Control)text.Parent!).IsVisible = progress is not null;
        if (progress is null) { return; }
        text.Text = _loc[upload ? "TransferUpload" : "TransferDownload"] + " · " + _loc["Transfer" + progress.State]
            + $" · {progress.Bytes:N0} / {progress.Total:N0} B" + (progress.Name.Length > 0 ? " · " + progress.Name : "");
        ToolTip.SetTip(text, progress.Error ?? (progress.State is "Sent" or "Delivered" ? text.Text + "\n" + _loc["TransferDeliveryDetail"] : text.Text));
        bar.IsIndeterminate = progress.State == "Preparing" && progress.Total == 0;
        bar.Value = progress.Total == 0 ? progress.State is "Sent" or "Delivered" or "Completed" ? 100 : 0 : 100.0 * progress.Bytes / progress.Total;
        ToolTip.SetTip(cancel, _loc["TransferCancel"]);
        Avalonia.Automation.AutomationProperties.SetName(cancel, _loc["TransferCancel"]);
        cancel.IsEnabled = progress.Active && _connected;
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
        if (_reconnecting || Reconnect is not { } reconnect)
        {
            return;
        }

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
        _attention.Content = loc["SecureAttention"];
        _reconnect.Content = loc["ReconnectButton"];
        UpdateTransfers();
        UpdateStatus();
    }
    internal void SetConnected(bool connected, string? detail = null)
    {
        _connected = connected;
        if (!connected) { _clipboard.StopTransfers(); UpdateTransfers(); }
        _disconnectDetail = connected ? null : detail;
        Desktop.IsEnabled = connected;
        _attention.IsEnabled = connected;
        _overlay.IsVisible = !connected;
        if (!connected && !_reconnecting)
        {
            // Show the server's reason (signed out, taken over, no licence …) when known, so an
            // immediate disconnect is not a blank "disconnected".
            _overlayText.Text = detail ?? _loc["Disconnected"];
            _reconnect.IsEnabled = Reconnect is not null;
            _reconnect.IsVisible = Reconnect is not null;
        }
        UpdateStatus();
    }
    private void UpdateStatus() => _status.Text = !_connected ? _disconnectDetail ?? _loc["Disconnected"]
        : _failed ? _loc["InputFailed"]
        : _loc["Connected"] + " · " + _loc.Format("Resolution", Desktop.DesktopWidth, Desktop.DesktopHeight);
    public async ValueTask DisposeAsync()
    {
        _transferTimer.Stop();
        try { await _clipboard.DisposeAsync(); }
        finally { await Desktop.DisposeAsync(); Children.Clear(); }
    }
}
