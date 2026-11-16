using VelaShell.PluginSdk;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Text clipboard in both directions. The local clipboard is offered when the desktop gains focus (and before a paste),
/// not polled: the SDK has no change notification, and focus is exactly when the remote can next need it. The last text
/// exchanged either way is remembered, so focusing the desktop does not echo the server's own text back to it.
/// </summary>
internal sealed class VncClipboardBridge : IDisposable
{
    internal const int MaxLength = 10 * 1024 * 1024;
    private readonly IVncSession _session;
    private readonly IPluginContext _context;
    private readonly bool _enabled;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    // A non-faulting FIFO tail: a failed exchange must not poison later attempts.
    private Task _tail = Task.CompletedTask;
    private string? _last;
    private int _disposed;

    internal VncClipboardBridge(IVncSession session, IPluginContext context, bool enabled)
    {
        _session = session;
        _context = context;
        _enabled = enabled;
        if (enabled)
        {
            session.ClipboardReceived += OnRemote;
        }
    }

    /// <summary>Sends changed local text, waiting for all earlier exchanges even when it is a duplicate.</summary>
    internal Task PublishLocalAsync() => Enqueue(PublishLocalCoreAsync);

    private async Task PublishLocalCoreAsync(CancellationToken ct)
    {
        if (!_session.IsConnected) { return; }
        string? text = await _context.Clipboard.GetTextAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text == _last) { return; }
        Task send;
        lock (_gate)
        {
            // A clipboard read may finish after Dispose, even if the host ignored cancellation.
            if (_disposed != 0) { throw new OperationCanceledException(ct); }
            if (!_session.IsConnected) { return; }
            send = _session.SendClipboardAsync(text, ct);
        }
        await send.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _last = text;
    }

    private void OnRemote(string text) => _ = ObserveRemoteAsync(Enqueue(ct => SetLocalAsync(text, ct)));

    private async Task SetLocalAsync(string text, CancellationToken ct)
    {
        if (text == _last) { return; }
        await _context.Clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _last = text;
    }

    private async Task ObserveRemoteAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Host exceptions can contain clipboard text; do not include them in the log.
            _context.Log.Warn("Could not place the VNC server's clipboard text on the local clipboard.");
        }
    }

    private Task Enqueue(Func<CancellationToken, Task> operation)
    {
        Task previous;
        CancellationToken ct;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_enabled || _disposed != 0) { return Task.CompletedTask; }
            previous = _tail;
            _tail = finished.Task;
            ct = _stop.Token;
        }
        return RunAsync();

        async Task RunAsync()
        {
            try
            {
                await previous.ConfigureAwait(false);
                Task pending;
                lock (_gate)
                {
                    // Serialize the start of host calls with Dispose, without waiting under the lock.
                    if (_disposed != 0) { throw new OperationCanceledException(ct); }
                    pending = operation(ct);
                }
                await pending.ConfigureAwait(false);
            }
            finally { finished.TrySetResult(); }
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed != 0) { return; }
            _disposed = 1;
            pending = _tail;
        }
        if (_enabled) { _session.ClipboardReceived -= OnRemote; }
        try { _stop.Cancel(); }
        finally { _ = DisposeStopAsync(pending); }
    }

    private async Task DisposeStopAsync(Task pending)
    {
        // Dispose is synchronous: let the queue drain before releasing its cancellation source.
        // No SemaphoreSlim is disposed while a waiter or releaser could still be using it.
        await pending.ConfigureAwait(false);
        _stop.Dispose();
    }
}
