using System.Threading.Channels;
using VelaShell.Rdp.Channels;

namespace VelaShell.Rdp;

public sealed partial class RdpManagedSession
{
    private RailChannel? _rail;
    private Channel<byte[]>? _railQueue;
    private Task? _railWorker;
    private long _railQueuedBytes;
    public bool IsRemoteApp => Connection.RemoteWindows is not null;
    public uint ActiveRemoteWindow => Connection.RemoteWindows?.ActiveWindow ?? 0;
    public IReadOnlyList<RemoteAppWindow> RemoteWindows => Connection.RemoteWindows?.Snapshot() ?? [];
    public event Action? RemoteWindowsChanged;

    private void EnableRemoteApp(RemoteAppOptions options, int width, int height)
    {
        _rail = new RailChannel(options, width, height, (pdu, token) => Connection.SendVirtualChannelAsync("rail", pdu, token),
            () => (Connection.Framebuffer.Width, Connection.Framebuffer.Height));
        _railQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        Connection.RemoteWindows!.Changed += OnRemoteWindowsChanged;
        _railWorker = DrainRailAsync();
    }
    private void OnRemoteWindowsChanged() => RemoteWindowsChanged?.Invoke();
    private void ReceiveRail(byte[] pdu)
    {
        long queued = Interlocked.Add(ref _railQueuedBytes, pdu.Length);
        if (pdu.Length > ushort.MaxValue || queued > 1024 * 1024 || _railQueue?.Writer.TryWrite(pdu) != true)
        {
            Interlocked.Add(ref _railQueuedBytes, -pdu.Length);
            throw new RdpProtocolException("RemoteApp channel queue capacity exceeded.");
        }
    }
    private async Task DrainRailAsync()
    {
        try
        {
            await foreach (byte[] pdu in _railQueue!.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _railQueuedBytes, -pdu.Length);
                await _rail!.ProcessAsync(pdu, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _dynamicChannelFailure = ex;
            _rail!.Stop(ex);
            _stop.Cancel();
        }
    }
    private RailChannel RequireRemoteWindow(uint id)
    {
        if (_rail is null || Connection.RemoteWindows?.Contains(id) != true)
        {
            throw new InvalidOperationException("The remote application window no longer exists.");
        }
        return _rail;
    }
    public Task ActivateWindowAsync(uint windowId, bool active, CancellationToken ct = default) =>
        Guard(() => RequireRemoteWindow(windowId).ActivateAsync(windowId, active, ct));
    public Task CloseWindowAsync(uint windowId, CancellationToken ct = default) =>
        Guard(() => RequireRemoteWindow(windowId).SystemCommandAsync(windowId, 0xf060, ct));
    public Task SetWindowStateAsync(uint windowId, bool maximized, CancellationToken ct = default) =>
        Guard(() => RequireRemoteWindow(windowId).SystemCommandAsync(windowId, maximized ? (ushort)0xf030 : (ushort)0xf120, ct));
    public Task MoveWindowAsync(uint windowId, int x, int y, int width, int height, CancellationToken ct = default) =>
        Guard(() => RequireRemoteWindow(windowId).MoveAsync(windowId, x, y, width, height, ct));
}
