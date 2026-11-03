using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp;

internal sealed partial class RdpConnection
{
    private readonly ConcurrentDictionary<int, Task> _tunnelAttempts = new();
    private readonly ConcurrentDictionary<int, RdpMultitransport> _tunnels = new();
    private readonly RdpAutoDetect _autoDetect;
    private byte[] _certificateDigest = [];
    private bool _udpAdvertised;
    internal RdpNetworkMonitor Network { get; }

    private async Task ProcessMessageChannelAsync(byte[] payload, CancellationToken ct)
    {
        var r = new ByteReader(payload);
        ushort flags = r.U16LE();
        _ = r.U16LE();
        if (flags == 0x1000)
        {
            byte[]? response = _autoDetect.Process(r.Rest(), Environment.TickCount64);
            if (response is not null) { await SendMessageAsync(0x2000, response, ct).ConfigureAwait(false); }
            return;
        }
        if (flags != 2) { throw new RdpProtocolException("Unexpected MCS message channel security flags."); }
        uint requestId = r.U32LE();
        ushort protocol = r.U16LE();
        _ = r.U16LE();
        byte[] cookie = r.Take(16).ToArray();
        if (r.Remaining != 0) { throw new RdpProtocolException("Trailing multitransport initiation data."); }
        int type = protocol == 1 ? 1 : 3;
        uint serverFlags = _mcs.Server.MultitransportFlags;
        bool supported = _udpAdvertised && protocol is 1 or 2 && (serverFlags & 0x200) != 0
            && (serverFlags & (protocol == 1 ? 1u : 4u)) != 0;
        if (!supported || _tunnelAttempts.ContainsKey(type))
        {
            CryptographicOperations.ZeroMemory(cookie);
            await SendInitiateResponseAsync(requestId, false, ct).ConfigureAwait(false);
            return;
        }
        // Only the TCP receive pump starts attempts, and there can be at most one per transport type.
        _tunnelAttempts[type] = EstablishTunnelAsync(type, requestId, cookie);
    }

    private async Task EstablishTunnelAsync(int type, uint requestId, byte[] cookie)
    {
        RdpMultitransport? tunnel = null;
        bool announced = false;
        try
        {
            tunnel = await RdpMultitransport.ConnectAsync((IPEndPoint)_tcp!.Client.RemoteEndPoint!,
                _options.CertificateHost, _certificateDigest, type == 3, requestId, cookie, Network, _sendStop.Token).ConfigureAwait(false);
            _tunnels[type] = tunnel;
            await SendInitiateResponseAsync(requestId, true, _sendStop.Token).ConfigureAwait(false);
            announced = true;
            tunnel.Start(pdu =>
            {
                ChannelData?.Invoke(new("drdynvc", pdu, type));
                return Task.CompletedTask;
            }, _ =>
            {
                // Once S_OK has been sent the peer may have moved channels. Reconnect the session;
                // silently replaying their bytes on TCP would corrupt ordering and device operations.
                try { _tcp?.Close(); } catch (ObjectDisposedException) { }
            });
            Network.Transport(_tunnels.ContainsKey(1), _tunnels.ContainsKey(3), Network.Status.UdpFallback);
        }
        catch (Exception) when (!_sendStop.IsCancellationRequested)
        {
            _ = _tunnels.TryRemove(type, out _);
            if (tunnel is not null) { await tunnel.DisposeAsync().ConfigureAwait(false); }
            Network.Transport(_tunnels.ContainsKey(1), _tunnels.ContainsKey(3), true);
            if (announced) { _tcp?.Close(); }
            else
            {
                try { await SendInitiateResponseAsync(requestId, false, _sendStop.Token).ConfigureAwait(false); }
                catch (Exception) { _tcp?.Close(); }
            }
        }
        catch (Exception) when (_sendStop.IsCancellationRequested)
        {
            if (tunnel is not null) { await tunnel.DisposeAsync().ConfigureAwait(false); }
        }
        finally { CryptographicOperations.ZeroMemory(cookie); }
    }

    private Task SendInitiateResponseAsync(uint requestId, bool success, CancellationToken ct)
    {
        var w = new ByteWriter(8);
        w.U32LE(requestId); w.U32LE(success ? 0u : 0x80004004u);
        return SendMessageAsync(4, w.ToArray(), ct);
    }

    private Task SendMessageAsync(ushort flags, byte[] payload, CancellationToken ct)
    {
        var w = new ByteWriter(4 + payload.Length);
        w.U16LE(flags); w.U16LE(0); w.Bytes(payload);
        return Guarded(() => _mcs.SendOnChannelAsync(_mcs.Server.MessageChannelId, w.ToArray(), ct), ct);
    }

    internal async Task WaitForTunnelAsync(int type)
    {
        if (!_tunnelAttempts.TryGetValue(type, out Task? attempt)) { throw new RdpProtocolException("Soft-Sync names an unrequested tunnel."); }
        await attempt.ConfigureAwait(false);
        if (!_tunnels.ContainsKey(type)) { throw new RdpProtocolException("Soft-Sync names an unavailable tunnel."); }
    }

    internal void EnableTunnelReading(int type) => _tunnels[type].EnableReading();
    internal Task SendTunnelAsync(int type, byte[] pdu, CancellationToken ct) => _tunnels.TryGetValue(type, out RdpMultitransport? tunnel)
        ? tunnel.SendAsync(pdu, ct) : throw new IOException("RDP multitransport unavailable.");
}
