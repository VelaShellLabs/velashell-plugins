using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>Only the focused desktop exchanges text; no global clipboard polling.</summary>
internal sealed class RdpClipboardBinding(RdpDesktopSurface surface, IRdpSession session) : IDisposable
{
    private readonly IRdpClipboardSession? _clipboard = session as IRdpClipboardSession;
    private volatile bool _disposed;
    private volatile bool _attached;
    private volatile bool _focused;
    private long _epoch;
    private string? _pendingRemote;
    private int _remotePosted;

    public void Attach()
    {
        if (_disposed || _attached || _clipboard?.ClipboardEnabled != true) return;
        _attached = true;
        _clipboard.RemoteTextAvailable += RemoteText;
        surface.GotFocus += Focused;
        surface.LostFocus += Unfocused;
    }
    public void Dispose()
    {
        _disposed = true;
        Detach();
    }
    public void Detach()
    {
        _attached = false;
        Suspend();
        if (_clipboard is not null) _clipboard.RemoteTextAvailable -= RemoteText;
        surface.GotFocus -= Focused;
        surface.LostFocus -= Unfocused;
    }
    private void Focused(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Resume();
    internal void Resume()
    {
        if (!_attached || _disposed || !surface.IsFocused || _focused) return;
        _focused = true;
        _clipboard?.SetClipboardFocus(true);
        surface.QueueClipboardFocus(true);
    }
    internal void Suspend()
    {
        _focused = false;
        Interlocked.Increment(ref _epoch);
        Interlocked.Exchange(ref _pendingRemote, null);
        _clipboard?.SetClipboardFocus(false);
    }
    private void Unfocused(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Suspend();
        surface.QueueClipboardFocus(false);
    }

    public async Task PrepareAsync(RdpInput input)
    {
        if (_disposed || !_attached || _clipboard?.ClipboardEnabled != true) return;
        // Synchronize before V key-down on the same serial input worker, never async-void in KeyDown.
        if (!(input.Kind == RdpInputKind.ClipboardFocus && input.X != 0) && !input.SynchronizeClipboard) return;
        long epoch = Volatile.Read(ref _epoch);
        string? text = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_disposed || !_focused || !surface.IsFocused) return null;
            var clipboard = TopLevel.GetTopLevel(surface)?.Clipboard;
            if (clipboard is null) return null;
            try { return await clipboard.TryGetTextAsync(); }
            catch { return null; }
        });
        if (_disposed || !_attached || epoch != Volatile.Read(ref _epoch)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool accepted;
        try { accepted = await _clipboard.SynchronizeClipboardAsync(text, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_disposed || !_attached || !_focused || epoch != Volatile.Read(ref _epoch)) { return; }
        if (_disposed || !_attached || !_focused || epoch != Volatile.Read(ref _epoch)) return;
        if (!accepted) throw new IOException("The remote clipboard did not accept the local text.");
    }

    private void RemoteText(string text)
    {
        if (_disposed || !_attached || !_focused) return;
        Interlocked.Exchange(ref _pendingRemote, text);
        if (Interlocked.CompareExchange(ref _remotePosted, 1, 0) != 0) return;
        long epoch = Volatile.Read(ref _epoch);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pendingRemote, null) is { } latest)
                {
                    if (_disposed || !_attached || !_focused || epoch != Volatile.Read(ref _epoch) ||
                        !surface.IsFocused || session.Status.State != RdpSessionState.Connected) return;
                    var clipboard = TopLevel.GetTopLevel(surface)?.Clipboard;
                    if (clipboard is null) return;
                    try
                    {
                        await clipboard.SetTextAsync(latest);
                    }
                    catch { /* Clipboard contention must not disconnect the desktop. */ }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _remotePosted, 0);
                if (Interlocked.Exchange(ref _pendingRemote, null) is { } pending) RemoteText(pending);
            }
        });
    }
}
