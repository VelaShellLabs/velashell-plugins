namespace VelaShell.Rdp.Wire;

/// <summary>Single-owner v1 state machine. Time is supplied by the socket driver or a deterministic test.</summary>
internal sealed class RdpUdpState(bool lossy, uint initial)
{
    internal const int Capacity = 256;
    internal const int MaxPayload = 768; // Leaves room for a worst-case selective ACK vector and FEC prefix.
    private sealed class Sent(byte[] payload, long now)
    {
        internal readonly byte[] Payload = payload;
        internal long Time = now;
        internal int Retries;
    }
    private readonly Dictionary<uint, Sent> _sent = [];
    private readonly Dictionary<uint, byte[]> _pending = [];
    private readonly HashSet<uint> _received = [];
    private readonly Queue<uint> _history = new();
    private readonly RdpUdpFec _fec = new();
    private readonly Queue<byte[]> _delivered = new();
    private readonly List<byte[]> _fecSend = [];
    private uint _fecStart;
    private readonly uint _initial = initial;
    private uint _source = initial, _coded = initial, _acked = initial;
    private uint _next, _highest, _ackFloor, _peerInitial;
    private int _window;
    private double _cwnd = 4, _threshold = 32;
    private long _lastReceive, _lastSend, _lastReduction = -10000, _gapSince = -1;
    private bool _cwr, _congestion;
    private uint _cwrSequence;
    internal bool Established { get; private set; }
    internal bool RequireCompleteDelivery { get; set; }
    internal double RttMilliseconds { get; private set; } = 250;
    internal long PacketsSent { get; private set; }
    internal long PacketsLost { get; private set; }
    internal int Outstanding => _sent.Count;

    internal RdpUdpPacket Syn() => new(uint.MaxValue, Capacity, RdpUdpFlags.Syn | (lossy ? RdpUdpFlags.Lossy : 0))
    {
        InitialSequence = _initial,
        UpMtu = 1232,
        DownMtu = 1232
    };

    internal List<byte[]> Receive(RdpUdpPacket packet, long now)
    {
        var output = new List<byte[]>();
        if (packet.Flags.HasFlag(RdpUdpFlags.Syn))
        {
            if (!packet.Flags.HasFlag(RdpUdpFlags.Ack) || packet.Ack != _initial || packet.Flags.HasFlag(RdpUdpFlags.Lossy) != lossy)
            { throw new RdpProtocolException("RDP-UDP SYN-ACK mismatch."); }
            if (!Established)
            {
                _next = unchecked(packet.InitialSequence + 1);
                _highest = _ackFloor = _peerInitial = packet.InitialSequence;
                _ = _received.Add(_highest);
                _history.Enqueue(_highest);
                _window = packet.Window;
                Established = true;
            }
            else if (packet.InitialSequence != _peerInitial)
            { throw new RdpProtocolException("RDP-UDP peer sequence changed."); }
            _lastReceive = now;
            output.Add(Ack(now).Encode());
            return output;
        }
        if (!Established) { return output; }
        _lastReceive = now;
        if (packet.Flags.HasFlag(RdpUdpFlags.Ack))
        {
            if (RdpUdpPacket.Distance(packet.Ack, _source) > 0) { throw new RdpProtocolException("RDP-UDP acknowledges unsent data."); }
            _window = packet.Window;
            uint sequence = packet.Ack;
            int newer = 0;
            foreach (byte element in packet.AckVector)
            {
                bool received = (element >> 6) == 0;
                for (int i = 0; i < (element & 63); i++, sequence = unchecked(sequence - 1))
                {
                    if (received)
                    {
                        newer++;
                        if (_sent.Remove(sequence, out Sent? sent))
                        {
                            if (sent.Retries == 0) { RttMilliseconds = RttMilliseconds * .875 + Math.Max(1, now - sent.Time) * .125; }
                            _cwnd = Math.Min(Capacity, _cwnd + (_cwnd < _threshold ? 1 : 1 / _cwnd));
                        }
                    }
                    else if (newer >= 3 && _sent.TryGetValue(sequence, out Sent? missing) && now - missing.Time >= RttMilliseconds)
                    {
                        Reduce(now);
                        if (lossy) { _ = _sent.Remove(sequence); }
                        else { output.Add(Retransmit(sequence, missing, now)); }
                    }
                }
            }
            while (_acked != _source && !_sent.ContainsKey(unchecked(_acked + 1))) { _acked = unchecked(_acked + 1); }
            if (packet.Flags.HasFlag(RdpUdpFlags.Congestion) && RdpUdpPacket.Distance(packet.Ack, _cwrSequence) >= 0) { Reduce(now); }
        }
        if (packet.Flags.HasFlag(RdpUdpFlags.AckOfAcks) && RdpUdpPacket.Distance(packet.AckOfAcks, _ackFloor) > 0
            && RdpUdpPacket.Distance(packet.AckOfAcks, _highest) <= 0) { _ackFloor = packet.AckOfAcks; }
        if (packet.Flags.HasFlag(RdpUdpFlags.CongestionReset)) { _congestion = false; }
        if (packet.Flags.HasFlag(RdpUdpFlags.Data))
        {
            if (packet.Flags.HasFlag(RdpUdpFlags.Fec))
            {
                if (RdpUdpPacket.Distance(unchecked(packet.SourceSequence + packet.FecRange), _next) is >= 0 and < Capacity
                    && RdpUdpPacket.Distance(packet.SourceSequence, _next) >= -Capacity)
                { _fec.AddEquation(packet); }
            }
            else { Accept(packet.SourceSequence, packet.Payload, now); }
            foreach ((uint sequence, byte[] payload) in _fec.Recover(_next, Capacity).ToArray()) { Accept(sequence, payload, now); }
            Drain(now);
            output.Add(Ack(now).Encode());
        }
        return output;
    }

    private void Accept(uint sequence, byte[] payload, long now)
    {
        int distance = RdpUdpPacket.Distance(sequence, _next);
        if (distance < 0 || distance >= Capacity || _received.Contains(sequence) || _pending.Count + _delivered.Count >= Capacity) { return; }
        _pending.Add(sequence, payload);
        _ = _received.Add(sequence);
        _history.Enqueue(sequence);
        while (_history.Count > Capacity) { _ = _received.Remove(_history.Dequeue()); }
        _fec.AddSource(sequence, payload);
        if (RdpUdpPacket.Distance(sequence, _highest) > 0) { _highest = sequence; }
        if (distance > 0 && _gapSince < 0) { _gapSince = now; }
        if (distance >= 3) { _congestion = true; }
    }

    private void Drain(long now)
    {
        while (_pending.Remove(_next, out byte[]? payload))
        {
            _delivered.Enqueue(payload);
            _next = unchecked(_next + 1);
        }
        if (_pending.Count == 0) { _gapSince = -1; }
        else if (_gapSince < 0) { _gapSince = now; }
    }

    internal byte[]? Read() => _delivered.TryDequeue(out byte[]? payload) ? payload : null;

    internal List<byte[]>? Send(byte[] payload, long now)
    {
        if (payload.Length > MaxPayload) { throw new ArgumentOutOfRangeException(nameof(payload)); }
        if (!Established || _sent.Count >= Math.Min(_window, Math.Floor(_cwnd)) || RdpUdpPacket.Distance(_source, _acked) >= _window) { return null; }
        uint sequence = _source = unchecked(_source + 1);
        _sent.Add(sequence, new(payload, now));
        PacketsSent++;
        var output = new List<byte[]> { Data(sequence, payload, now).Encode() };
        if (lossy)
        {
            if (_fecSend.Count == 0) { _fecStart = sequence; }
            _fecSend.Add(payload);
            if (_fecSend.Count == 4)
            {
                byte index = unchecked((byte)(sequence + 1));
                RdpUdpPacket fec = Ack(now) with
                {
                    Flags = RdpUdpFlags.Ack | RdpUdpFlags.Data | RdpUdpFlags.Fec | RdpUdpFlags.AckOfAcks,
                    CodedSequence = unchecked(++_coded),
                    SourceSequence = _fecStart,
                    FecRange = 3,
                    FecIndex = index,
                    Payload = RdpUdpFec.Encode(_fecStart, _fecSend, index)
                };
                output.Add(fec.Encode());
                _fecSend.Clear();
            }
        }
        return output;
    }

    internal List<byte[]> Tick(long now)
    {
        var output = new List<byte[]>();
        if (!Established) { return output; }
        if (now - _lastReceive >= 65000) { throw new IOException("RDP-UDP peer timed out."); }
        long timeout = (long)Math.Max(500, 2 * RttMilliseconds);
        foreach ((uint sequence, Sent sent) in _sent.ToArray())
        {
            if (now - sent.Time < timeout) { continue; }
            Reduce(now);
            if (lossy) { _ = _sent.Remove(sequence); }
            else { output.Add(Retransmit(sequence, sent, now)); }
        }
        while (_acked != _source && !_sent.ContainsKey(unchecked(_acked + 1))) { _acked = unchecked(_acked + 1); }
        if (lossy && _gapSince >= 0 && now - _gapSince >= Math.Clamp(RttMilliseconds, 50, 200))
        {
            // DVC fragments carry no message identifier. A missing DATA_FIRST or continuation
            // cannot safely be joined to later data for stateful channel handlers.
            if (RequireCompleteDelivery) { throw new IOException("Unrecoverable RDP-UDP application data loss; reconnect required."); }
            _next = _pending.Keys.MinBy(s => RdpUdpPacket.Distance(s, _next));
            _gapSince = -1;
            Drain(now);
            output.Add(Ack(now).Encode());
        }
        if (now - _lastSend >= 1000) { output.Add(Ack(now).Encode()); }
        return output;
    }

    private void Reduce(long now)
    {
        if (now - _lastReduction < RttMilliseconds) { return; }
        PacketsLost++;
        _lastReduction = now;
        _threshold = Math.Max(2, _cwnd / 2);
        _cwnd = _threshold;
        _cwr = true;
    }

    private byte[] Retransmit(uint sequence, Sent sent, long now)
    {
        if (sent.Retries >= 4) { throw new IOException("RDP-UDP retransmit limit exceeded."); }
        sent.Time = now;
        sent.Retries++;
        return Data(sequence, sent.Payload, now).Encode();
    }

    private RdpUdpPacket Data(uint sequence, byte[] payload, long now)
    {
        RdpUdpPacket packet = Ack(now);
        RdpUdpFlags flags = packet.Flags | RdpUdpFlags.Data;
        if (_cwr) { flags |= RdpUdpFlags.CongestionReset; _cwr = false; _cwrSequence = sequence; }
        return packet with { Flags = flags, CodedSequence = unchecked(++_coded), SourceSequence = sequence, Payload = payload };
    }

    private RdpUdpPacket Ack(long now)
    {
        var vector = new List<byte>();
        int count = Math.Min(Capacity, Math.Max(1, RdpUdpPacket.Distance(_highest, _ackFloor)));
        int run = 0, previous = -1;
        for (int i = 0; i < count; i++)
        {
            uint sequence = unchecked(_highest - (uint)i);
            int state = _received.Contains(sequence) ? 0 : 3;
            if (state != previous || run == 63)
            {
                if (run > 0) { vector.Add((byte)((previous << 6) | run)); }
                previous = state; run = 0;
            }
            run++;
        }
        if (run > 0) { vector.Add((byte)((previous << 6) | run)); }
        _lastSend = now;
        return new(_highest, (ushort)(Capacity - _pending.Count - _delivered.Count),
            RdpUdpFlags.Ack | RdpUdpFlags.AckOfAcks | (_congestion ? RdpUdpFlags.Congestion : 0))
        { AckVector = vector.ToArray(), AckOfAcks = _acked };
    }
}
