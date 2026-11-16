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

    /// <summary>Sends the local clipboard text if it changed since the last exchange.</summary>
    internal async Task PublishLocalAsync()
    {
        if (!_enabled || _disposed != 0 || !_session.IsConnected) { return; }
        string? text = await _context.Clipboard.GetTextAsync(CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) { return; }
        lock (_gate)
        {
            if (text == _last) { return; }
            _last = text;
        }
        await _session.SendClipboardAsync(text).ConfigureAwait(false);
    }

    private void OnRemote(string text)
    {
        if (_disposed != 0) { return; }
        lock (_gate)
        {
            if (text == _last) { return; }
            _last = text;
        }
        _ = SetLocalAsync(text);
    }

    private async Task SetLocalAsync(string text)
    {
        try { await _context.Clipboard.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Warn("Could not place the VNC server's clipboard text on the local clipboard.", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        if (_enabled) { _session.ClipboardReceived -= OnRemote; }
    }
}
