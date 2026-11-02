using VelaShell.Rdp.Graphics;

namespace VelaShell.Rdp.Channels;

/// <summary>Owns one graphics pipeline; the multiplexer supplies complete DVC messages.</summary>
internal sealed class RdpGfxChannel(IRdpGfxBackend backend, Action<RdpGfxFrame> paint,
    Action<Exception> fail, CancellationToken stop) : IDynamicChannelHandler, IAsyncDisposable
{
    internal const string ChannelName = "Microsoft::Windows::RDS::Graphics";
    internal const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DynamicChannel? _channel;
    private int _claimed;
    private bool _opened, _disposed;

    internal void Register(DrdynvcChannel multiplexer) => multiplexer.RegisterListener(ChannelName, channel =>
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
        {
            throw new InvalidOperationException("The graphics pipeline supports one channel per connection.");
        }
        _channel = channel;
        return this;
    }, MaxMessageBytes);

    public Task OnOpenAsync() => RunAsync(null);
    public Task OnMessageAsync(byte[] message) => RunAsync(message);

    private async Task RunAsync(byte[]? message)
    {
        await _gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (message is null)
            {
                if (_opened) { throw new RdpProtocolException("RDP graphics channel opened twice."); }
                _opened = true;
                backend.Open();
            }
            else
            {
                if (!_opened) { throw new RdpProtocolException("RDP graphics data arrived before channel open."); }
                if (message.Length > MaxMessageBytes) { throw new RdpProtocolException("RDP graphics message exceeds 32 MiB."); }
                backend.Feed(message);
            }
            // Consume each frame before advancing the decoder or sending its acknowledgement.
            while (backend.TryGetFrame(out RdpGfxFrame frame)) { frame.Validate(); paint(frame); }
            while (backend.ReadResponse() is { } response)
            {
                stop.ThrowIfCancellationRequested();
                await _channel!.SendAsync(response, stop).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            fail(ex);
            throw;
        }
        finally { _ = _gate.Release(); }
    }

    public async Task OnCloseAsync()
    {
        if (!stop.IsCancellationRequested)
        {
            fail(new RdpProtocolException("The server closed the RDP graphics channel; reconnect the session."));
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            backend.Dispose();
        }
        finally { _ = _gate.Release(); }
    }
}
