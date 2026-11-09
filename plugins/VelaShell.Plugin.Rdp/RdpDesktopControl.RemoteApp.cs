using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed partial class RdpDesktopControl
{
    private const double AppCaptionHeight = 26;
    private IRdpRemoteAppSource? RemoteApp => _session is IRdpRemoteAppSource { IsRemoteApp: true } source ? source : null;
    private IReadOnlyList<RemoteAppWindow> _remoteWindows = [];
    private uint _activeRemoteWindow;
    private int _windowsPending;
    private sealed record RemoteDrag(RemoteAppWindow Window, uint StartX, uint StartY, bool Resize);
    private RemoteDrag? _remoteDrag;
    private Rect? _remoteDragPreview;

    private void OnRemoteWindows()
    {
        if (_disposed != 0 || Interlocked.Exchange(ref _windowsPending, 1) != 0) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _windowsPending, 0);
            if (_disposed != 0) { return; }
            _remoteWindows = RemoteApp?.RemoteWindows ?? [];
            if (RemoteApp?.ActiveRemoteWindow is > 0 and var active) { _activeRemoteWindow = active; }
            if (!_remoteWindows.Any(w => w.Id == _activeRemoteWindow && !w.IsCloaked))
            {
                _activeRemoteWindow = _remoteWindows.FirstOrDefault(w => w.Visible)?.Id ?? 0;
            }
            if (_remoteDrag is { } drag && !_remoteWindows.Any(w => w.Id == drag.Window.Id))
            {
                _remoteDrag = null;
                _remoteDragPreview = null;
                ReleaseInput();
            }
            InvalidateVisual();
        }, DispatcherPriority.Input);
    }

    // Both rendering and hit testing use the same clipped server visibility regions. The backing
    // bitmap is never displayed as a desktop; even the space between applications stays local.
    internal static IEnumerable<Rect> RemoteAppRegions(RemoteAppWindow window, int width, int height)
    {
        if (!window.Visible) { yield break; }
        var bounds = new Rect(window.X, window.Y, window.Width, window.Height).Intersect(new Rect(0, 0, width, height));
        foreach (RdpFrameRegion region in window.VisibilityRects)
        {
            Rect visible = new Rect(window.X + region.X, window.Y + region.Y, region.Width, region.Height).Intersect(bounds);
            if (visible.Width <= 0 || visible.Height <= 0) { continue; }
            if (window.WindowRects.Count == 0) { yield return visible; continue; }
            foreach (RdpFrameRegion shape in window.WindowRects)
            {
                Rect clipped = visible.Intersect(new Rect(window.X + shape.X, window.Y + shape.Y, shape.Width, shape.Height));
                if (clipped.Width > 0 && clipped.Height > 0) { yield return clipped; }
            }
        }
    }
    internal RemoteAppWindow? HitRemoteWindow(double x, double y) => _remoteWindows.FirstOrDefault(w =>
        RemoteAppRegions(w, DesktopWidth, DesktopHeight).Any(rect => rect.Contains(new Point(x, y))));

    private Rect ToLocal(Rect remote)
    {
        Rect viewport = DesktopViewport;
        return new Rect(viewport.X + remote.X * viewport.Width / DesktopWidth,
            viewport.Y + remote.Y * viewport.Height / DesktopHeight,
            remote.Width * viewport.Width / DesktopWidth, remote.Height * viewport.Height / DesktopHeight);
    }
    private static bool HasCaption(RemoteAppWindow window) => (window.Style & 0x00c00000) == 0x00c00000;
    private Rect Caption(RemoteAppWindow window) => ToLocal(new Rect(window.X, window.Y, window.Width, Math.Min(AppCaptionHeight, window.Height)));

    private void RenderRemoteApps(DrawingContext context)
    {
        IBrush background = this.TryFindResource("VelaSurface", out object? bg) && bg is IBrush bgBrush ? bgBrush : Brushes.DimGray;
        IBrush foreground = this.TryFindResource("VelaTextPrimary", out object? fg) && fg is IBrush fgBrush ? fgBrush : Brushes.White;
        if (_bitmap is { } bitmap)
        {
            foreach (RemoteAppWindow window in _remoteWindows.Reverse())
            {
                foreach (Rect region in RemoteAppRegions(window, DesktopWidth, DesktopHeight))
                {
                    Rect source = new Rect(region.X + window.VisibleX - window.X, region.Y + window.VisibleY - window.Y, region.Width, region.Height)
                        .Intersect(new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
                    if (source.Width <= 0 || source.Height <= 0) { continue; }
                    Rect destination = ToLocal(new Rect(source.X - window.VisibleX + window.X, source.Y - window.VisibleY + window.Y, source.Width, source.Height));
                    context.DrawImage(bitmap, source, destination);
                    if (!HasCaption(window)) { continue; }
                    using (context.PushClip(ToLocal(region)))
                    {
                        Rect caption = Caption(window);
                        context.FillRectangle(background, caption);
                        using (context.PushClip(new Rect(caption.X + 4, caption.Y, Math.Max(0, caption.Width - 58), caption.Height)))
                        {
                            DrawAppText(context, window.Title, new Point(caption.X + 5, caption.Y + 3), foreground);
                        }
                        DrawAppText(context, window.ShowState == 3 ? "↙" : "□", new Point(caption.Right - 44, caption.Y + 3), foreground);
                        DrawAppText(context, "×", new Point(caption.Right - 20, caption.Y + 3), foreground);
                    }
                }
            }
        }
        if (_remoteDragPreview is { } preview) { context.DrawRectangle(null, new Pen(foreground, 2), ToLocal(preview)); }
        // A local application strip also restores windows minimized by a remote keyboard command.
        int index = 0;
        foreach (RemoteAppWindow window in AppTabs())
        {
            Rect tab = AppTab(index++);
            context.FillRectangle(background, tab);
            using (context.PushClip(tab.Deflate(3))) { DrawAppText(context, window.Title, new Point(tab.X + 5, tab.Y + 5), foreground); }
        }
    }
    private static void DrawAppText(DrawingContext context, string value, Point position, IBrush brush) =>
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), position);
    private IEnumerable<RemoteAppWindow> AppTabs() => _remoteWindows.Where(w => HasCaption(w) && !w.IsCloaked && w.ShowState != 0 && (w.ExtendedStyle & 0x80) == 0);
    private Rect AppTab(int index)
    {
        int count = Math.Max(1, AppTabs().Count());
        double width = Math.Min(180, (double)DesktopWidth / count);
        return ToLocal(new Rect(index * width, Math.Max(0, DesktopHeight - RemoteAppOptions.WorkspaceStripHeight), width,
            Math.Min(DesktopHeight, RemoteAppOptions.WorkspaceStripHeight)));
    }
    private void SetRemoteAppFocus(bool active)
    {
        if (RemoteApp is { } source && _activeRemoteWindow != 0)
        {
            uint id = _activeRemoteWindow;
            Queue(() => source.ActivateWindowAsync(id, active));
        }
    }
    private void SelectRemoteWindow(RemoteAppWindow window)
    {
        _activeRemoteWindow = window.Id;
        _ = Focus();
        SetRemoteAppFocus(true);
    }
    private bool RemoteAppPressed(PointerPressedEventArgs e)
    {
        if (RemoteApp is not { } source) { return false; }
        Point local = e.GetPosition(this);
        int index = 0;
        foreach (RemoteAppWindow tab in AppTabs())
        {
            if (AppTab(index++).Contains(local))
            {
                SelectRemoteWindow(tab);
                if (tab.ShowState == 2) { Queue(() => source.SetWindowStateAsync(tab.Id, false)); }
                e.Handled = true;
                return true;
            }
        }
        if (!Coordinates(e, false) || HitRemoteWindow(_mouseX, _mouseY) is not { } window) { return true; }
        SelectRemoteWindow(window);
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) { return false; }
        if (HasCaption(window) && Caption(window).Contains(local))
        {
            Rect caption = Caption(window);
            if (local.X >= caption.Right - 26) { Queue(() => source.CloseWindowAsync(window.Id)); }
            else if (local.X >= caption.Right - 52 || e.ClickCount == 2) { Queue(() => source.SetWindowStateAsync(window.Id, window.ShowState != 3)); }
            else if (window.ShowState != 3) { BeginRemoteDrag(window, e, false); }
            e.Handled = true;
            return true;
        }
        if ((window.Style & 0x00040000) != 0 && window.ShowState != 3 &&
            _mouseX >= window.X + window.Width - 8 && _mouseY >= window.Y + window.Height - 8)
        {
            BeginRemoteDrag(window, e, true);
            e.Handled = true;
            return true;
        }
        return false;
    }
    private void BeginRemoteDrag(RemoteAppWindow window, PointerPressedEventArgs e, bool resize)
    {
        _remoteDrag = new(window, _mouseX, _mouseY, resize);
        _remoteDragPreview = new Rect(window.X, window.Y, window.Width, window.Height);
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
    }
    private bool RemoteAppMoved(PointerEventArgs e)
    {
        if (_remoteDrag is not { } drag) { return false; }
        _ = Coordinates(e, true);
        int dx = (int)_mouseX - (int)drag.StartX, dy = (int)_mouseY - (int)drag.StartY;
        RemoteAppWindow w = drag.Window;
        int x = Math.Clamp(w.X, 0, Math.Max(0, DesktopWidth - 1));
        int y = Math.Clamp(w.Y, 0, Math.Max(0, DesktopHeight - 1));
        int width = Math.Min(w.Width, DesktopWidth), height = Math.Min(w.Height, DesktopHeight);
        if (drag.Resize)
        {
            width = Math.Clamp(w.Width + dx, Math.Min(100, DesktopWidth - x), DesktopWidth - x);
            height = Math.Clamp(w.Height + dy, Math.Min(60, DesktopHeight - y), DesktopHeight - y);
        }
        else
        {
            x = Math.Clamp(w.X + dx, 0, DesktopWidth - width);
            y = Math.Clamp(w.Y + dy, 0, DesktopHeight - height);
        }
        _remoteDragPreview = new Rect(x, y, width, height);
        InvalidateVisual();
        e.Handled = true;
        return true;
    }
    private bool RemoteAppReleased(PointerReleasedEventArgs e)
    {
        if (_remoteDrag is not { } drag || RemoteApp is not { } source) { return false; }
        _ = RemoteAppMoved(e);
        Rect bounds = _remoteDragPreview!.Value;
        _remoteDrag = null;
        _remoteDragPreview = null;
        _pointer = null;
        e.Pointer.Capture(null);
        Queue(() => source.MoveWindowAsync(drag.Window.Id, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
        InvalidateVisual();
        e.Handled = true;
        return true;
    }
}
