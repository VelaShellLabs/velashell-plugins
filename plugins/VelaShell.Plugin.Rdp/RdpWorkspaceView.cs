using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly TransferSlot _download = new("ClipboardDownload", "CancelClipboardDownload");
    private readonly TransferSlot _upload = new("ClipboardUpload", "CancelClipboardUpload");
    private readonly Border _footer;
    private readonly TextBlock _status = new() { Name = "RdpStatus", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly IPluginContext _context;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private string? _notice;
    private string? _armedTyping;
    private readonly Button _reconnect = new();
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private Loc _loc;
    private bool _connected = true;
    private bool _failed;
    private bool _reconnecting;
    private string? _disconnectDetail;
    private string? _audioError;
    private readonly IRdpNetworkSource? _network;
    private int _disposed;

    internal void SetAudioError(string? error)
    {
        _audioError = error;
        UpdateStatus();
        ToolTip.SetTip(_status, error is null ? null : _loc.Format("AudioFailed", error));
    }

    /// <summary>Reconnects the workspace; supplied by the owning document. Null hides the in-view button.</summary>
    internal Func<CancellationToken, Task>? Reconnect { get; set; }

    internal RdpDesktopControl Desktop { get; }
    public RdpWorkspaceView(IRdpSession session, IPluginContext context, RdpSettings settings, Loc loc)
    {
        _loc = loc;
        _context = context;
        _network = session as IRdpNetworkSource;
        _network?.NetworkChanged += OnNetworkChanged;
        // No document header: the only toolbar action (Ctrl+Alt+Delete) moved into the floating remote-keys
        // button, so the remote desktop gets the full height above the 24px status bar.
        RowDefinitions = [with("*,24")];
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
        QuickBall = new RdpQuickBall(QuickActions(), loc) { Folded = () => { _armedTyping = null; _ = Desktop.Focus(); } };
        // Esc folds an open fan; it must not also reach the remote desktop, which has keyboard focus.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (QuickBall.IsExpanded && e.Key == Key.Escape) { QuickBall.Collapse(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; UpdateStatus(); };
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
        SetRow(_footer, 1);
        Children.Add(scroller);
        Children.Add(QuickBall);
        Children.Add(_overlay);
        Children.Add(_footer);
        ApplyLocale(loc);
    }

    internal RdpQuickBall QuickBall { get; }

    /// <summary>
    /// What the floating button offers: combinations the local OS keeps for itself (there is no global keyboard
    /// hook, so pressing them on the local keyboard never reaches the remote) plus typing the clipboard as text.
    /// Inner ring: the most used; outer ring: the Windows-key shortcuts.
    /// </summary>
    private IEnumerable<RdpQuickAction> QuickActions()
    {
        const uint ctrl = 0x1d, shift = 0x2a, alt = 0x38, win = 0x15b;
        Func<Task<bool>> Chord(params uint[] codes) => () => { Desktop.SendChord(codes); return Task.FromResult(true); };
        yield return new("SecureAttention", "QuickSecureAttention", "Ctrl+Alt+Delete", ShieldIcon, 0, Chord(ctrl, alt, 0x153));
        yield return new("TaskManager", "QuickTaskManager", "Ctrl+Shift+Esc", ActivityIcon, 0, Chord(ctrl, shift, 0x01));
        yield return new("SwitchWindow", "QuickSwitchWindow", "Alt+Tab", ArrowRightLeftIcon, 0, Chord(alt, 0x0f));
        yield return new("TypeClipboard", "QuickTypeClipboard", null, ClipboardIcon, 0, TypeClipboardAsync);
        yield return new("Start", "QuickStart", "Win", LayoutGridIcon, 1, Chord(win));
        yield return new("Run", "QuickRun", "Win+R", SquareTerminalIcon, 1, Chord(win, 0x13));
        yield return new("ShowDesktop", "QuickShowDesktop", "Win+D", MonitorIcon, 1, Chord(win, 0x20));
        yield return new("Lock", "QuickLock", "Win+L", LockIcon, 1, Chord(win, 0x26));
        yield return new("PrintScreen", "QuickPrintScreen", "PrtSc", CameraIcon, 1, Chord(0x137));
    }

    // Lucide geometries, copied from the host's Icons.axaml where it has them (keyboard / monitor / camera are not there).
    private const string ShieldIcon = "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1 1 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1Z";
    private const string ActivityIcon = "M22 12h-3.86l-2.64 7.92a.5.5 0 0 1-.94 0L9.4 4.08a.5.5 0 0 0-.94 0L5.82 12H2";
    private const string ArrowRightLeftIcon = "M8 3 4 7l4 4 M4 7h16 M16 21l4-4-4-4 M20 17H4";
    private const string ClipboardIcon = "M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1Z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2";
    private const string LayoutGridIcon = "M4 3h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z M15 3h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z M4 14h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1Z M15 14h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1Z";
    private const string SquareTerminalIcon = "M7 11l2-2-2-2 M11 13h4 M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z";
    private const string MonitorIcon = "M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M8 21h8 M12 17v4";
    private const string LockIcon = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2Z M7 11V7a5 5 0 0 1 10 0v4";
    private const string CameraIcon = "M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3Z M9 13a3 3 0 1 0 6 0a3 3 0 1 0-6 0";

    internal const int TypeLimit = 2000, TypeConfirmLength = 100;

    /// <summary>
    /// Reads the local clipboard through the SDK (never logged) and types it. Text over
    /// <see cref="TypeConfirmLength"/> characters, or with line breaks (sent as Enter, so a console would run them),
    /// needs a second click on the same text; over <see cref="TypeLimit"/> it is refused.
    /// </summary>
    private async Task<bool> TypeClipboardAsync()
    {
        string? text;
        try { text = await _context.Clipboard.GetTextAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowNotice(_loc["TypeClipboardUnavailable"]); return true; }
        string typed = RdpDesktopControl.NormalizeTypedText(text ?? "");
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

    private void UpdateTransfers() => UpdateTransfers(_clipboard.DownloadProgress, _clipboard.UploadProgress);

    internal void UpdateTransfers(RdpClipboardProgress? download, RdpClipboardProgress? upload)
    {
        _download.Update(download, upload: false, _connected, _loc);
        _upload.Update(upload, upload: true, _connected, _loc);
        // Poll at 10 Hz only while bytes are moving; idle, once a second is enough to notice a new transfer start.
        var interval = TimeSpan.FromMilliseconds(download?.Active == true || upload?.Active == true ? 100 : 1000);
        if (_transferTimer.Interval != interval)
        {
            _transferTimer.Interval = interval;
        }
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
            foreach (Control? slot in slots)
            {
                slot.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                natural[slot] = slot.DesiredSize.Width;
            }
            double remaining = width;
            int left = slots.Count;
            foreach (Control? slot in slots.OrderBy(slot => natural[slot]))
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
            foreach (Control? slot in Children.Skip(1).Where(child => child.IsVisible).Reverse())
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
        private const double TooltipPreferredMinWidth = 520;
        private const double TooltipPreferredMaxWidth = 900;
        private const double TooltipScreenMargin = 64;
        // The parent status panel provides the available width. Do not impose a second cap here:
        // on narrow views the transfer slot must be able to use every pixel not needed by the status text.
        internal readonly SlotPanel Root = new() { IsVisible = false, ClipToBounds = true, Margin = new Thickness(10, 0, 0, 0) };
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
            Bar[!Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("VelaBorderPrimary");
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
                foreach (Control child in Children)
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
                Name = name,
                Focusable = false,
                Width = 16,
                Height = 16,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new RdpGlyph { Data = CancelIcon, Width = 10, Height = 10 }
            };
            RdpChrome.AddIconButtonStyles(button, "VelaTextTertiary", "VelaTextPrimary");
            return button;
        }

        internal void Update(RdpClipboardProgress? progress, bool upload, bool connected, Loc loc)
        {
            Root.IsVisible = progress is not null;
            if (progress is null) { return; }
            string direction = loc[upload ? "TransferUpload" : "TransferDownload"], state = loc["Transfer" + progress.State];
            string size = progress.Total > 0 ? FormatSize(progress.Bytes) + " / " + FormatSize(progress.Total)
                : progress.Bytes > 0 ? FormatSize(progress.Bytes) : "";
            // Keep the status bar focused on measurable progress. The complete file name remains in the tooltip,
            // where it is not truncated by a narrow workspace.
            Text.Text = string.Join(" · ", new[] { direction, size }.Where(part => part.Length > 0));
            string metrics = FormatMetrics(progress, loc);
            ToolTip.SetTip(Root, BuildTooltip(Root, progress, direction, state, loc));
            // Show compact live metrics beside the percentage; the tooltip retains exact values.
            string percentage = progress.State == "Transferring" && progress.Total > 0
                ? Math.Clamp(100 * progress.Bytes / progress.Total, 0, 100).ToString(CultureInfo.InvariantCulture) + "%"
                : state;
            string compactMetrics = FormatMetrics(progress, loc, compact: true);
            State.Text = compactMetrics.Length > 0 ? percentage + " · " + compactMetrics : percentage;
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

        private static TextBlock BuildTooltip(Control anchor, RdpClipboardProgress progress, string direction, string state, Loc loc)
        {
            List<string> lines = [$"{direction}", $"{state}: {FormatExactBytes(progress.Bytes)}/{FormatExactBytes(progress.Total)}"];
            if (progress.Name.Length > 0) { lines.Add(loc["TransferFileName"] + ": " + progress.Name); }
            string metrics = FormatMetrics(progress, loc);
            if (metrics.Length > 0) { lines.Add(metrics); }
            if (progress.Error is { } error) { lines.Add(error); }
            else if (progress.State is "Sent" or "Delivered") { lines.Add(loc["TransferDeliveryDetail"]); }

            TopLevel? topLevel = TopLevel.GetTopLevel(anchor);
            double windowWidth = topLevel is null
                ? 0
                : Math.Min(topLevel.ClientSize.Width, topLevel.Bounds.Width);
            double maxWidth = windowWidth > TooltipScreenMargin
                ? Math.Max(240, Math.Min(TooltipPreferredMaxWidth, windowWidth - TooltipScreenMargin))
                : TooltipPreferredMaxWidth;
            return new TextBlock
            {
                Text = string.Join("\n", lines),
                TextWrapping = TextWrapping.Wrap,
                MinWidth = Math.Min(TooltipPreferredMinWidth, maxWidth),
                MaxWidth = maxWidth
            };
        }

        private static string FormatMetrics(RdpClipboardProgress progress, Loc loc, bool compact = false)
        {
            List<string> parts = [];
            if (progress.SpeedBytesPerSecond is { } speed && speed > 0)
            {
                parts.Add(compact ? FormatRate(speed) : loc["TransferSpeed"] + ": " + FormatRate(speed));
            }
            if (progress.Remaining is { } remaining && remaining > TimeSpan.Zero)
            {
                parts.Add(compact ? FormatDuration(remaining) : loc["TransferRemainingTime"] + ": " + FormatDuration(remaining));
            }
            return string.Join(" · ", parts);
        }

        private static string FormatExactBytes(long bytes) => Math.Max(0, bytes).ToString("N0", CultureInfo.InvariantCulture) + "B";

        private static string FormatRate(double bytesPerSecond) => FormatSize((long)Math.Min(bytesPerSecond, long.MaxValue)).Replace(" ", "", StringComparison.Ordinal) + "/s";

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 60) { return Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s"; }
            if (duration.TotalMinutes < 60) { return $"{(int)duration.TotalMinutes}m {duration.Seconds}s"; }
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        private void SetTone(string token)
        {
            if (_tone == token) { return; }
            _tone = token;
            State[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(token);
            Bar[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension(token);
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
        _reconnect[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        _reconnect[!Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        _reconnect[!Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
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
        QuickBall.ApplyLocale(loc);
        _reconnect.Content = loc["ReconnectButton"];
        UpdateTransfers();
        SetAudioError(_audioError);
    }
    internal void SetConnected(bool connected, string? detail = null)
    {
        _connected = connected;
        if (!connected) { _clipboard.StopTransfers(); UpdateTransfers(); }
        _disconnectDetail = connected ? null : detail;
        Desktop.IsEnabled = connected;
        if (!connected) { QuickBall.Collapse(); ClearNotice(); }
        QuickBall.IsVisible = connected;
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
        : _notice is { } notice ? notice
        : _failed ? _loc["InputFailed"]
        : _audioError is { } audioError ? _loc.Format("AudioFailed", audioError)
        : _loc["Connected"] + " · " + _loc.Format("Resolution", Desktop.DesktopWidth, Desktop.DesktopHeight) + NetworkSummary();

    private void OnNetworkChanged(RdpNetworkStatus _) => Dispatcher.UIThread.Post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0) { UpdateStatus(); }
    });

    private string NetworkSummary()
    {
        if (_network is null) { return ""; }
        RdpNetworkStatus network = _network.NetworkStatus;
        string transport = "TCP" + (network.ReliableUdp ? " + UDP-R" : "") + (network.LossyUdp ? " + UDP-L" : "");
        if (network.UdpFallback) { transport += " (" + _loc["UdpFallback"] + ")"; }
        string result = " · " + transport;
        if (network.RoundTripMilliseconds is { } rtt) { result += $" · {rtt} ms"; }
        if (network.BandwidthKbps is { } bandwidth) { result += $" · {bandwidth} kbps"; }
        if (network.Quality != RdpNetworkQuality.Unknown) { result += " · " + _loc["Network" + network.Quality]; }
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        _network?.NetworkChanged -= OnNetworkChanged;
        _transferTimer.Stop();
        _noticeTimer.Stop();
        try { await _clipboard.DisposeAsync(); }
        finally { await Desktop.DisposeAsync(); Children.Clear(); }
    }
}
