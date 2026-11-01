using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpWorkspaceView : Grid, IAsyncDisposable
{
    private readonly RdpClipboardBridge _clipboard;
    private readonly DispatcherTimer _transferTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TransferSlot _download = new("ClipboardDownload", "CancelClipboardDownload");
    private readonly TransferSlot _upload = new("ClipboardUpload", "CancelClipboardUpload");
    private readonly Border _footer;
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
        _download.Cancel.Click += (_, _) => { _clipboard.CancelTransfer(false); UpdateTransfers(); };
        _upload.Cancel.Click += (_, _) => { _clipboard.CancelTransfer(true); UpdateTransfers(); };
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
        // Each transfer is one compact group — direction · size · name, a 3px bar, the state, × — like the
        // host's transfer rows. They sit right-aligned (upload outermost); the connection status takes what
        // is left and is the first thing to trim.
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        var footerContent = new StatusBarPanel { ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0), Children = { _status, _download.Root, _upload.Root } };
        _footer = new Border { Name = "RdpStatusBar", Height = 24, BorderThickness = new Thickness(0, 1, 0, 0), Child = footerContent, ClipToBounds = true };
        _footer[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSidebar");
        _footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");
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
        SetRow(_footer, 2);
        Children.Add(header);
        Children.Add(scroller);
        Children.Add(_overlay);
        Children.Add(_footer);
        ApplyLocale(loc);
    }

    private void UpdateTransfers() => UpdateTransfers(_clipboard.DownloadProgress, _clipboard.UploadProgress);

    internal void UpdateTransfers(RdpClipboardProgress? download, RdpClipboardProgress? upload)
    {
        _download.Update(download, upload: false, _connected, _loc);
        _upload.Update(upload, upload: true, _connected, _loc);
    }

    /// <summary>
    /// Status bar layout: child 0 (the connection status) on the left, every other visible child
    /// right-aligned in order. The transfers share the width fairly — the shorter one keeps its natural
    /// size and the other gets the rest — so with both running in a narrow view each trims its own name
    /// instead of one squeezing the other's bar and × off the edge. The status gets what remains.
    /// </summary>
    private sealed class StatusBarPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var slots = Children.Skip(1).Where(child => child.IsVisible).ToList();
            double width = availableSize.Width, height = 0;
            var natural = new Dictionary<Control, double>();
            foreach (var slot in slots)
            {
                slot.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                natural[slot] = slot.DesiredSize.Width;
            }
            double remaining = width;
            int left = slots.Count;
            foreach (var slot in slots.OrderBy(slot => natural[slot]))
            {
                slot.Measure(new Size(Math.Max(0, Math.Min(natural[slot], remaining / left)), availableSize.Height));
                remaining -= slot.DesiredSize.Width;
                left--;
                height = Math.Max(height, slot.DesiredSize.Height);
            }
            Children[0].Measure(new Size(double.IsInfinity(width) ? width : Math.Max(0, remaining), availableSize.Height));
            height = Math.Max(height, Children[0].DesiredSize.Height);
            return new Size(double.IsInfinity(width) ? Children.Sum(child => child.DesiredSize.Width) : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double right = finalSize.Width;
            foreach (var slot in Children.Skip(1).Where(child => child.IsVisible).Reverse())
            {
                right -= slot.DesiredSize.Width;
                slot.Arrange(new Rect(right, 0, slot.DesiredSize.Width, finalSize.Height));
            }
            Children[0].Arrange(new Rect(0, 0, Math.Max(0, right), finalSize.Height));
            return finalSize;
        }
    }

    /// <summary>One direction's status-bar entry: │ direction · size · name ▬▬▬ state ×</summary>
    private sealed class TransferSlot
    {
        private static readonly Geometry CancelIcon = Geometry.Parse("M18 6 6 18 M6 6l12 12"); // Lucide "x"
        // Capped so one long file name cannot take the whole bar; the name trims, never the bar or ×.
        internal readonly SlotPanel Root = new() { IsVisible = false, ClipToBounds = true, MaxWidth = 480, Margin = new Thickness(10, 0, 0, 0) };
        internal readonly TextBlock Text;
        internal readonly ProgressBar Bar;
        // Long state wording ("Copy completed (Shell confirmed)") trims here; the tooltip has it in full.
        internal readonly TextBlock State = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeight.Medium, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly Button Cancel;
        private string? _tone;

        internal TransferSlot(string name, string cancelName)
        {
            var divider = new Avalonia.Controls.Shapes.Rectangle { Width = 1, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            divider[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("VelaBorderPrimary");
            Text = new() { Name = name + "Progress", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
            Text[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
            Text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
            // Same bar as the host's transfer panel: 3px, rounded, border-primary track. Fixed width so the
            // bars of two transfers line up and do not jump as the name or the state text changes.
            Bar = new() { Name = name + "Bar", Width = 72, MinWidth = 0, Height = 3, MinHeight = 3, Maximum = 100, CornerRadius = new CornerRadius(2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Bar[!ProgressBar.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
            State[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
            State[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
            Cancel = BuildCancelButton(cancelName);
            Root.Children.AddRange([divider, Text, Bar, State, Cancel]);
        }

        /// <summary>
        /// Lays out divider · text · bar · state · × left to right. Under pressure the text trims first,
        /// then the state; the divider, the fixed-width bar and the × always keep their size, so nothing
        /// overlaps and the × is never pushed out.
        /// </summary>
        internal sealed class SlotPanel : Panel
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                Control divider = Children[0], text = Children[1], bar = Children[2], state = Children[3], cancel = Children[4];
                var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
                divider.Measure(unbounded);
                bar.Measure(unbounded);
                cancel.Measure(unbounded);
                double rest = Math.Max(0, availableSize.Width - divider.DesiredSize.Width - bar.DesiredSize.Width - cancel.DesiredSize.Width);
                state.Measure(new Size(rest, availableSize.Height));
                text.Measure(new Size(Math.Max(0, rest - state.DesiredSize.Width), availableSize.Height));
                return new Size(Children.Sum(child => child.DesiredSize.Width), Children.Max(child => child.DesiredSize.Height));
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                // Any slack beyond the desired size goes to the text, so the right-hand parts stay put.
                double x = 0, slack = Math.Max(0, finalSize.Width - Children.Sum(child => child.DesiredSize.Width));
                foreach (var child in Children)
                {
                    double width = child.DesiredSize.Width + (child == Children[1] ? slack : 0);
                    child.Arrange(new Rect(x, 0, width, finalSize.Height));
                    x += width;
                }
                return finalSize;
            }
        }

        /// <summary>
        /// The host's per-item ×: a 10px Lucide x in <c>VelaTextTertiary</c> on a transparent 16px button,
        /// <c>VelaBgHover</c> behind it and <c>VelaTextPrimary</c> on hover (FileTransferView's CancelItemButton).
        /// </summary>
        private static Button BuildCancelButton(string name)
        {
            var button = new Button
            {
                Name = name, Focusable = false, Width = 16, Height = 16, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new RdpGlyph { Data = CancelIcon, Width = 10, Height = 10 }
            };
            // Styles, not local values, so the hover state can override the resting colours.
            button.Styles.Add(TokenStyle(x => x.OfType<RdpGlyph>(), RdpGlyph.ForegroundProperty, "VelaTextTertiary"));
            button.Styles.Add(TokenStyle(x => x.OfType<Button>().Class(":pointerover").Descendant().OfType<RdpGlyph>(), RdpGlyph.ForegroundProperty, "VelaTextPrimary"));
            foreach (string state in new[] { ":pointerover", ":pressed" })
            {
                button.Styles.Add(TokenStyle(x => x.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                    ContentPresenter.BackgroundProperty, "VelaBgHover"));
            }
            return button;
        }

        private static Style TokenStyle(Func<Selector?, Selector> selector, AvaloniaProperty property, string token) =>
            new(selector) { Setters = { new Setter(property, new DynamicResourceExtension(token)) } };

        internal void Update(RdpClipboardProgress? progress, bool upload, bool connected, Loc loc)
        {
            Root.IsVisible = progress is not null;
            if (progress is null) { return; }
            string direction = loc[upload ? "TransferUpload" : "TransferDownload"], state = loc["Transfer" + progress.State];
            string size = progress.Total > 0 ? FormatSize(progress.Bytes) + " / " + FormatSize(progress.Total)
                : progress.Bytes > 0 ? FormatSize(progress.Bytes) : "";
            Text.Text = string.Join(" · ", new[] { direction, size, progress.Name }.Where(part => part.Length > 0));
            // The tooltip keeps the exact byte counts and the full state wording the compact line drops.
            string summary = $"{direction} · {state} · {progress.Bytes:N0} / {progress.Total:N0} B" + (progress.Name.Length > 0 ? " · " + progress.Name : "");
            ToolTip.SetTip(Root, progress.Error is { } error ? summary + "\n" + error
                : progress.State is "Sent" or "Delivered" ? summary + "\n" + loc["TransferDeliveryDetail"] : summary);
            // Like the host's transfer rows: a percentage while bytes are moving, otherwise the state in words.
            State.Text = progress.State == "Transferring" && progress.Total > 0
                ? Math.Clamp(100 * progress.Bytes / progress.Total, 0, 100).ToString(CultureInfo.InvariantCulture) + "%"
                : state;
            Bar.IsIndeterminate = progress.State == "Preparing" && progress.Total == 0;
            Bar.Value = progress.Total == 0 ? progress.State is "Sent" or "Delivered" or "Completed" ? 100 : 0 : 100.0 * progress.Bytes / progress.Total;
            SetTone(progress.State switch
            {
                "Sent" or "Delivered" or "Completed" => "VelaStatusConnected",
                "Failed" or "TimedOut" => "VelaError",
                "Unsupported" => "VelaWarning",
                "Cancelled" => "VelaTextMuted",
                "Ready" => "VelaTextSecondary",
                _ => "VelaAccent"
            });
            ToolTip.SetTip(Cancel, loc["TransferCancel"]);
            Avalonia.Automation.AutomationProperties.SetName(Cancel, loc["TransferCancel"]);
            // Only a transfer that can still be stopped offers ×; a finished one has nothing to cancel.
            Cancel.IsVisible = progress.Active && connected;
        }

        private void SetTone(string token)
        {
            if (_tone == token) { return; }
            _tone = token;
            State[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(token);
            Bar[!ProgressBar.ForegroundProperty] = new DynamicResourceExtension(token);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) { return bytes.ToString(CultureInfo.InvariantCulture) + " B"; }
            double value = bytes;
            int unit = -1;
            do { value /= 1024; unit++; } while (value >= 1024 && unit < 3);
            return value.ToString(value < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + "KMGT"[unit] + "B";
        }
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
