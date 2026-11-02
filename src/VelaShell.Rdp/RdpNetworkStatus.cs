using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp;

/// <summary>UDP is optional; the main RDP connection always uses TCP.</summary>
public enum RdpTransportMode { Tcp, Auto }

public enum RdpNetworkQuality { Unknown, Good, Constrained, Poor }

/// <summary>Detected network characteristics. Null measurements mean that the server has not supplied them.</summary>
public sealed record RdpNetworkStatus(bool ReliableUdp = false, bool LossyUdp = false,
    uint? RoundTripMilliseconds = null, uint? BandwidthKbps = null, RdpNetworkQuality Quality = RdpNetworkQuality.Unknown,
    bool Adaptive = true, bool UdpFallback = false)
{
    /// <summary>Coalescing interval for pointer motion; clicks and keys must remain immediate.</summary>
    public int PointerIntervalMilliseconds => !Adaptive ? 0 : Quality switch
    { RdpNetworkQuality.Poor => 50, RdpNetworkQuality.Constrained => 25, _ => 0 };
}

public interface IRdpNetworkSource
{
    RdpNetworkStatus NetworkStatus { get; }
    event Action<RdpNetworkStatus>? NetworkChanged;
}

internal sealed class RdpNetworkMonitor(bool adaptive)
{
    private readonly Lock _gate = new();
    private RdpNetworkStatus _status = new(Adaptive: adaptive);
    private int _healthySamples;
    internal event Action<RdpNetworkStatus>? Changed;
    internal RdpNetworkStatus Status { get { lock (_gate) { return _status; } } }

    internal void Transport(bool reliable, bool lossy, bool fallback)
    {
        RdpNetworkStatus next;
        lock (_gate) { next = _status = _status with { ReliableUdp = reliable, LossyUdp = lossy, UdpFallback = fallback }; }
        Publish(next);
    }

    internal void Measure(uint? rtt, uint? bandwidth)
    {
        RdpNetworkStatus next;
        lock (_gate)
        {
            rtt ??= _status.RoundTripMilliseconds;
            bandwidth = bandwidth is > 0 ? bandwidth : _status.BandwidthKbps;
            RdpNetworkQuality candidate = rtt >= 250 || bandwidth < 1000 ? RdpNetworkQuality.Poor
                : rtt >= 100 || bandwidth < 5000 ? RdpNetworkQuality.Constrained
                : rtt is not null || bandwidth is not null ? RdpNetworkQuality.Good : RdpNetworkQuality.Unknown;
            RdpNetworkQuality quality = _status.Quality;
            if (candidate >= quality) { quality = candidate; _healthySamples = 0; }
            else if (++_healthySamples >= 3) { quality = candidate; _healthySamples = 0; }
            next = _status = _status with { RoundTripMilliseconds = rtt, BandwidthKbps = bandwidth, Quality = quality };
        }
        Publish(next);
    }

    private void Publish(RdpNetworkStatus status)
    {
        try { Changed?.Invoke(status); }
        catch { /* Observers cannot interrupt transport processing. */ }
    }
}

/// <summary>One detector per transport; all byte counters and elapsed time are bounded on the wire.</summary>
internal sealed class RdpAutoDetect(RdpNetworkMonitor monitor)
{
    private long? _started;
    private long _bytes;
    private bool _connectTime;

    internal void Observe(int bytes)
    {
        if (_started is not null && !_connectTime) { _bytes = Math.Min(uint.MaxValue, _bytes + bytes); }
    }

    internal byte[]? Process(ReadOnlySpan<byte> message, long now)
    {
        var r = new ByteReader(message);
        byte length = r.U8(), type = r.U8();
        ushort sequence = r.U16LE(), request = r.U16LE();
        int expected = request switch { 0x0002 or 0x002B => 8, 0x0840 or 0x0880 => 14, 0x08C0 => 18, _ => 6 };
        if (type != 0 || length != expected || message.Length < length) { throw new RdpProtocolException("Invalid network detection header."); }
        var reply = new ByteWriter(14);
        switch (request)
        {
            case 0x0001:
            case 0x1001:
                reply.U8(6); reply.U8(1); reply.U16LE(sequence); reply.U16LE(0);
                break;
            case 0x0014:
            case 0x0114:
            case 0x1014:
                _started = now; _bytes = 0; _connectTime = request == 0x1014;
                break;
            case 0x0002:
                ushort count = r.U16LE();
                r.Skip(count);
                if (_started is not null && _connectTime) { _bytes = Math.Min(uint.MaxValue, _bytes + count); }
                break;
            case 0x002B:
            case 0x0429:
            case 0x0629:
                if (request == 0x002B) { ushort final = r.U16LE(); r.Skip(final); _bytes = Math.Min(uint.MaxValue, _bytes + final); }
                if (_started is not { } start) { break; }
                uint elapsed = (uint)Math.Clamp(now - start, 0, uint.MaxValue);
                reply.U8(14); reply.U8(1); reply.U16LE(sequence); reply.U16LE((ushort)(request == 0x002B ? 3 : 11));
                reply.U32LE(elapsed); reply.U32LE((uint)_bytes);
                _started = null;
                break;
            case 0x0840:
            case 0x0880:
            case 0x08C0:
                if (request != 0x0880) { _ = r.U32LE(); }
                uint? bandwidth = request != 0x0840 ? r.U32LE() : null;
                monitor.Measure(r.U32LE(), bandwidth);
                break;
            default:
                throw new RdpProtocolException("Unsupported network detection request.");
        }
        if (r.Remaining != 0) { throw new RdpProtocolException("Trailing network detection data."); }
        return reply.Length == 0 ? null : reply.ToArray();
    }
}
