using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>Only the focused desktop exchanges text; no global clipboard polling.</summary>
internal sealed class RdpClipboardBinding(RdpDesktopSurface surface, IRdpSession session,
    Func<Task<string?>>? readText = null) : IDisposable
{
    private readonly IRdpClipboardSession? _clipboard = session as IRdpClipboardSession;
    private volatile bool _disposed;
    private volatile bool _attached;
    private volatile bool _focused;
    private long _epoch;
    private string? _pendingRemote;
    private int _remotePosted;
    private readonly Lock _preparationGate = new();
    private CancellationTokenSource? _preparation;
    internal TimeSpan PreparationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public void Attach()
    {
        if (_disposed || _attached || _clipboard?.ClipboardEnabled != true) return;
        _attached = true;
        _clipboard.RemoteTextAvailable += RemoteText;
        _clipboard.ClipboardFailed += ClipboardFaulted;
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
        if (_clipboard is not null) { _clipboard.RemoteTextAvailable -= RemoteText; _clipboard.ClipboardFailed -= ClipboardFaulted; }
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
        lock (_preparationGate) _preparation?.Cancel();
        Interlocked.Exchange(ref _pendingRemote, null);
        _clipboard?.SetClipboardFocus(false);
    }
    private void Unfocused(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Suspend();
        surface.QueueClipboardFocus(false);
    }

    public async Task<RdpInputPreparation> PrepareAsync(RdpInput input, CancellationToken cancellationToken)
    {
        if (!(input.Kind == RdpInputKind.ClipboardFocus && input.X != 0) && !input.SynchronizeClipboard)
            return RdpInputPreparation.Send;
        if (_clipboard?.ClipboardEnabled != true) return RdpInputPreparation.Send;
        if (_disposed || !_attached || !_focused || _clipboard.ClipboardUnavailable)
            return RdpInputPreparation.CancelPaste;
        long epoch = Volatile.Read(ref _epoch);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreparationTimeout);
        CancellationToken operationToken = timeout.Token;
        lock (_preparationGate)
        {
            _preparation = timeout;
            if (_disposed || !_attached || !_focused || epoch != Volatile.Read(ref _epoch)) timeout.Cancel();
        }
        bool synchronizing = false;
        try
        {
            // A successful no-text read is distinct from a failed/busy clipboard read.
            var reading = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (operationToken.IsCancellationRequested || !Current() || !surface.IsFocused)
                    throw new OperationCanceledException(operationToken);
                if (readText is not null) return await readText();
                var clipboard = TopLevel.GetTopLevel(surface)?.Clipboard ?? throw new IOException("Clipboard unavailable.");
                return await clipboard.TryGetTextAsync();
            });
            string? text;
            try { text = await reading.WaitAsync(operationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Observe(reading); throw; }
            catch (Exception) { surface.ReportClipboardFailure(session); return RdpInputPreparation.CancelPaste; }
            if (!Current()) return RdpInputPreparation.CancelPaste;
            synchronizing = true;
            var synchronization = _clipboard.SynchronizeClipboardAsync(text, operationToken);
            bool accepted;
            try { accepted = await synchronization.WaitAsync(operationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Observe(synchronization); throw; }
            if (!Current()) return RdpInputPreparation.CancelPaste;
            if (!accepted) { surface.ReportClipboardFailure(session); return RdpInputPreparation.CancelPaste; }
            return RdpInputPreparation.Send;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested && Current())
            {
                if (synchronizing) _clipboard.PauseClipboard(); // Never re-pair an unacknowledged no-ID slot.
                surface.ReportClipboardFailure(session);
            }
            return RdpInputPreparation.CancelPaste;
        }
        finally { lock (_preparationGate) if (ReferenceEquals(_preparation, timeout)) _preparation = null; }

        bool Current() => !_disposed && _attached && _focused && epoch == Volatile.Read(ref _epoch);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void ClipboardFaulted()
    {
        lock (_preparationGate) _preparation?.Cancel();
        if (_attached && !_disposed) surface.ReportClipboardFailure(session);
    }

    private void RemoteText(string text)
    {
        if (_disposed || !_attached || !_focused || _clipboard?.ClipboardUnavailable == true) return;
        Interlocked.Exchange(ref _pendingRemote, text);
        if (Interlocked.CompareExchange(ref _remotePosted, 1, 0) != 0) return;
        long epoch = Volatile.Read(ref _epoch);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pendingRemote, null) is { } latest)
                {
                    if (_disposed || !_attached || !_focused || _clipboard?.ClipboardUnavailable == true || epoch != Volatile.Read(ref _epoch) ||
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
