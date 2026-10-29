using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Ui;

internal sealed partial class RdpWorkspaceView : UserControl
{
    private readonly RdpWorkspaceDocument _document;
    private volatile bool _attached;
    private IRdpSession? _boundSession;
    private bool _busy;
    private string? _localErrorKey;
    private int _refreshPending;
    private int _refreshDirty;
    private Loc? _displayedLocale;
    private (int Width, int Height) _displayedResolution;

    public RdpWorkspaceView(RdpWorkspaceDocument document)
    {
        _document = document;
        InitializeComponent();
        Desktop.ScaleMode = document.ScaleMode;
        Desktop.Updated += Refresh;
        Desktop.InputFailed += SurfaceInputFailed;
        Desktop.ClipboardFailed += () =>
        {
            if (_localErrorKey != "Rdp_SendInputError") _localErrorKey = "Rdp_ClipboardError";
            Refresh();
        };
        EndpointText.Text = document.Endpoint;
        SetScale(document.ScaleMode);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _document.SessionChanged += RequestRefresh;
        _document.LocaleChanged += RequestRefresh;
        _document.StatusChanged += DocumentStatusChanged;
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _document.SessionChanged -= RequestRefresh;
        _document.LocaleChanged -= RequestRefresh;
        _document.StatusChanged -= DocumentStatusChanged;
        // Surface detaches its own subscriptions and resources. Keep its input queue so a quick
        // reattach to the same session cannot overtake the pending focus-loss ReleaseAll.
        base.OnDetachedFromVisualTree(e);
    }
    private void DocumentStatusChanged(object? sender, WorkspaceStatus status) => RequestRefresh();
    private void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshDirty, 1);
        if (!_attached || Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshDirty, 0);
            try { if (_attached) Refresh(); }
            finally
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                if (_attached && Volatile.Read(ref _refreshDirty) != 0) RequestRefresh();
            }
        });
    }
    private void SurfaceInputFailed()
    {
        _localErrorKey = "Rdp_SendInputError";
        Refresh();
    }
    private void Refresh()
    {
        Loc loc = _document.Loc;
        if (_attached)
        {
            IRdpSession? session = _document.Session;
            if (session is not null && !ReferenceEquals(session, _boundSession)) _localErrorKey = null;
            _boundSession = session;
            Desktop.SetSession(session);
        }
        bool localeChanged = !ReferenceEquals(_displayedLocale, loc);
        if (localeChanged)
        {
            _displayedLocale = loc;
            FitButton.Content = loc["Rdp_ScaleFit"];
            ActualButton.Content = loc["Rdp_ScaleActual"];
            CtrlAltDelButton.Content = loc["Rdp_SendCtrlAltDel"];
            DisconnectButton.Content = loc["Rdp_Disconnect"];
            ReconnectButton.Content = loc["Rdp_Reconnect"];
            KeyboardHintText.Text = loc["Rdp_KeyboardHint"];
            ToolTip.SetTip(KeyboardHintText, KeyboardHintText.Text);
            WaitingText.Text = loc["Rdp_NoFrame"];
            AutomationProperties.SetName(Desktop, loc["Rdp_Desktop"]);
            AutomationProperties.SetName(FitButton, FitButton.Content?.ToString());
            AutomationProperties.SetName(ActualButton, ActualButton.Content?.ToString());
            AutomationProperties.SetName(CtrlAltDelButton, CtrlAltDelButton.Content?.ToString());
            AutomationProperties.SetName(DisconnectButton, DisconnectButton.Content?.ToString());
            AutomationProperties.SetName(ReconnectButton, ReconnectButton.Content?.ToString());
        }
        WaitingText.IsVisible = Desktop.Frame.Width == 0;
        bool connected = _document.Session?.Status.State == RdpSessionState.Connected &&
                         _document.Status.State == ProtocolSessionState.Connected;
        bool closed = _document.Status.State == ProtocolSessionState.Closed;
        CtrlAltDelButton.IsEnabled = connected && !_busy;
        DisconnectButton.IsEnabled = connected && !_busy;
        ReconnectButton.IsEnabled = !closed && !_busy;
        StatusText.Text = _document.Status.Message;
        var resolution = (Desktop.Frame.Width, Desktop.Frame.Height);
        if (localeChanged || _displayedResolution != resolution)
        {
            _displayedResolution = resolution;
            ResolutionText.Text = resolution.Width > 0 ? loc.Format("Rdp_Resolution", resolution.Width, resolution.Height) : "";
        }
        string? error = _localErrorKey is not null ? loc[_localErrorKey] :
            !connected && !closed ? _document.Status.Message : null;
        ErrorText.Text = error;
        ErrorBanner.IsVisible = !string.IsNullOrEmpty(error);
        if (!connected) Desktop.ReleaseInput();
    }
    private void SetScale(RdpScaleMode mode)
    {
        Desktop.ReleaseInput();
        Desktop.ScaleMode = mode;
        DesktopScroll.HorizontalScrollBarVisibility = mode == RdpScaleMode.Actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        DesktopScroll.VerticalScrollBarVisibility = mode == RdpScaleMode.Actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        DesktopScroll.Offset = default;
        FitButton.Classes.Set("active", mode == RdpScaleMode.Fit);
        ActualButton.Classes.Set("active", mode == RdpScaleMode.Actual);
    }
    private void FitClicked(object? sender, RoutedEventArgs e) => SetScale(RdpScaleMode.Fit);
    private void ActualClicked(object? sender, RoutedEventArgs e) => SetScale(RdpScaleMode.Actual);
    private void CtrlAltDelClicked(object? sender, RoutedEventArgs e) => Desktop.SendCtrlAltDelete();
    private async void DisconnectClicked(object? sender, RoutedEventArgs e) => await RunActionAsync(false);
    private async void ReconnectClicked(object? sender, RoutedEventArgs e) => await RunActionAsync(true);
    private async Task RunActionAsync(bool reconnect)
    {
        if (_busy) return;
        _busy = true;
        _localErrorKey = null;
        Desktop.ReleaseInput();
        Refresh();
        try
        {
            if (reconnect) await _document.ReconnectAsync();
            else await _document.DisconnectAsync();
        }
        catch (OperationCanceledException) { /* Document status owns cancellation/close presentation. */ }
        catch (Exception)
        {
            // Reconnect maps certificate/auth/timeout failures on the document. Preserve its actionable message.
            if (_document.Status.State == ProtocolSessionState.Connected) _localErrorKey = "Rdp_Error";
        }
        finally { _busy = false; if (_attached) Refresh(); }
    }
}
