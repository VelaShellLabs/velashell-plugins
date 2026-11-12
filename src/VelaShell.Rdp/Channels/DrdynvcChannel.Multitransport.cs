using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

internal sealed partial class DrdynvcChannel
{
    private readonly Func<byte[], CancellationToken, Task> _tcpSend;
    private readonly Func<int, Task>? _waitForTunnel;
    private readonly Action<int>? _enableTunnel;
    private readonly Func<int, byte[], CancellationToken, Task>? _sendTunnel;
    private readonly Dictionary<uint, int> _routes = [];

    private static uint? PduChannel(byte[] pdu)
    {
        if (pdu.Length == 0 || pdu[0] >> 4 is not (1 or 2 or 3 or 4 or 6 or 7) || (pdu[0] & 3) == 3) { return null; }
        var r = new ByteReader(pdu.AsSpan(1));
        if (r.Remaining < CbIdBytes(pdu[0] & 3)) { return null; }
        return ReadVariable(ref r, pdu[0] & 3);
    }

    private Task SendRoutedAsync(byte[] pdu, CancellationToken ct)
    {
        int type = PduChannel(pdu) is { } id ? _routes.GetValueOrDefault(id) : 0;
        return type == 0 ? _tcpSend(pdu, ct) : _sendTunnel!(type, pdu, ct);
    }

    private void ValidateTransport(byte[] pdu, int type)
    {
        uint? channel = PduChannel(pdu);
        if (channel is not { } id)
        {
            if (type != 0) { throw new RdpProtocolException("DVC control PDU on unexpected tunnel."); }
            return;
        }
        int command = pdu[0] >> 4;
        if (command == 1)
        {
            lock (_lock)
            {
                if (!_instances.ContainsKey(id))
                {
                    _routes.Remove(id);
                    if (type != 0) { _routes[id] = type; }
                }
            }
        }
        if (_routes.GetValueOrDefault(id) != type) { throw new RdpProtocolException("DVC data arrived on the wrong transport."); }
    }

    private async Task SoftSyncAsync(byte[] pdu)
    {
        if (_waitForTunnel is null || _sendTunnel is null || _enableTunnel is null)
        { throw new RdpProtocolException("Unnegotiated DVC Soft-Sync."); }
        var r = new ByteReader(pdu);
        if (r.U8() != 0x80 || r.U8() != 0 || r.U32LE() != pdu.Length - 2)
        { throw new RdpProtocolException("Invalid Soft-Sync length."); }
        ushort flags = r.U16LE(), count = r.U16LE();
        if ((flags & 1) == 0 || (flags & ~3) != 0 || count > 2 || (count > 0 && (flags & 2) == 0))
        { throw new RdpProtocolException("Invalid Soft-Sync flags or tunnel count."); }
        var routes = new Dictionary<uint, int>();
        var tunnels = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int type = checked((int)r.U32LE());
            ushort channels = r.U16LE();
            if (type is not (1 or 3) || !tunnels.Add(type) || channels > 64)
            { throw new RdpProtocolException("Invalid Soft-Sync channel list."); }
            for (int j = 0; j < channels; j++)
            {
                uint id = r.U32LE();
                if (!routes.TryAdd(id, type)) { throw new RdpProtocolException("Duplicate Soft-Sync channel."); }
                lock (_lock)
                {
                    if (!_instances.TryGetValue(id, out Instance? instance) || instance._buffer is not null
                        || (_routes.TryGetValue(id, out int previous) && previous != type))
                    { throw new RdpProtocolException("Invalid Soft-Sync channel transition."); }
                }
            }
        }
        if (r.Remaining != 0) { throw new RdpProtocolException("Trailing Soft-Sync data."); }
        foreach (int type in tunnels) { await _waitForTunnel(type).ConfigureAwait(false); }
        var response = new ByteWriter(14);
        response.U8(0x90); response.U8(0); response.U32LE((uint)tunnels.Count);
        foreach (int type in tunnels.Order()) { response.U32LE((uint)type); }
        // _sendGate drains complete outbound messages before this barrier, and blocks new ones.
        await _tcpSend(response.ToArray(), _ct).ConfigureAwait(false);
        foreach ((uint id, int type) in routes) { _routes[id] = type; }
        foreach (int type in tunnels) { _enableTunnel(type); }
    }
}
