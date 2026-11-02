using System.Buffers.Binary;

namespace VelaShell.Rdp.Wire;

/// <summary>Bounded GF(256) recovery, with the MS-RDPEUDP reduction polynomial 0x11d.</summary>
internal sealed class RdpUdpFec
{
    private readonly Dictionary<uint, byte[]> _sources = [];
    private readonly Queue<uint> _order = new();
    private readonly List<RdpUdpPacket> _equations = [];
    private static readonly byte[] Exp = BuildExponents();
    private static readonly byte[] Log = BuildLogarithms();

    private static byte[] BuildExponents()
    {
        var result = new byte[510];
        int value = 1;
        for (int i = 0; i < 255; i++)
        {
            result[i] = result[i + 255] = (byte)value;
            value <<= 1;
            if ((value & 256) != 0) { value ^= 0x11d; }
        }
        return result;
    }

    private static byte[] BuildLogarithms()
    {
        var result = new byte[256];
        for (int i = 0; i < 255; i++) { result[Exp[i]] = (byte)i; }
        return result;
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte Inverse(byte a) => a == 0 ? (byte)0 : Exp[255 - Log[a]];
    private static byte Coefficient(byte index, uint sequence) => Inverse((byte)(index ^ (byte)sequence));

    internal void AddSource(uint sequence, byte[] payload)
    {
        if (_sources.ContainsKey(sequence)) { return; }
        byte[] source = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(source, checked((ushort)payload.Length));
        payload.CopyTo(source, 2);
        _sources.Add(sequence, source);
        _order.Enqueue(sequence);
        while (_order.Count > 512) { _sources.Remove(_order.Dequeue()); }
    }

    internal static byte[] Encode(uint start, IReadOnlyList<byte[]> sources, byte index)
    {
        byte[] result = new byte[sources.Max(s => s.Length) + 2];
        for (int s = 0; s < sources.Count; s++)
        {
            byte coefficient = Coefficient(index, unchecked(start + (uint)s));
            if (coefficient == 0) { throw new ArgumentException("FEC index overlaps source sequences."); }
            byte[] payload = sources[s];
            result[0] ^= Mul(coefficient, (byte)(payload.Length >> 8));
            result[1] ^= Mul(coefficient, (byte)payload.Length);
            for (int i = 0; i < payload.Length; i++) { result[i + 2] ^= Mul(coefficient, payload[i]); }
        }
        return result;
    }

    internal void AddEquation(RdpUdpPacket packet)
    {
        if (packet.Payload.Length < 2 || packet.FecRange == 255) { return; }
        for (uint i = 0; i <= packet.FecRange; i++)
        {
            if (Coefficient(packet.FecIndex, unchecked(packet.SourceSequence + i)) == 0) { return; }
        }
        if (_equations.Any(p => p.SourceSequence == packet.SourceSequence && p.FecRange == packet.FecRange && p.FecIndex == packet.FecIndex)) { return; }
        if (_equations.Count == 32) { _equations.RemoveAt(0); }
        _equations.Add(packet);
    }

    internal IEnumerable<(uint Sequence, byte[] Payload)> Recover(uint next, int window)
    {
        _equations.RemoveAll(p => RdpUdpPacket.Distance(unchecked(p.SourceSequence + p.FecRange), next) < 0);
        if (_equations.Count == 0) { yield break; }
        uint[] missing = _equations.SelectMany(p => Enumerable.Range(0, p.FecRange + 1)
            .Select(i => unchecked(p.SourceSequence + (uint)i))).Where(s => !_sources.ContainsKey(s)).Distinct().ToArray();
        // An oversized/incomplete system is left for retransmission (R) or the reorder timer (L).
        if (missing.Length == 0 || missing.Length > 32 || missing.Any(s => RdpUdpPacket.Distance(s, next) < 0 || RdpUdpPacket.Distance(s, next) >= window)) { yield break; }
        int length = _equations.Max(p => p.Payload.Length);
        var rows = new List<byte[]>();
        foreach (RdpUdpPacket packet in _equations)
        {
            byte[] row = new byte[missing.Length + length];
            packet.Payload.CopyTo(row, missing.Length);
            bool valid = true;
            for (uint i = 0; i <= packet.FecRange; i++)
            {
                uint sequence = unchecked(packet.SourceSequence + i);
                byte c = Coefficient(packet.FecIndex, sequence);
                if (_sources.TryGetValue(sequence, out byte[]? known))
                {
                    if (known.Length > length) { valid = false; break; }
                    for (int j = 0; j < known.Length; j++) { row[missing.Length + j] ^= Mul(c, known[j]); }
                }
                else { row[Array.IndexOf(missing, sequence)] = c; }
            }
            if (valid) { rows.Add(row); }
        }
        int pivot = 0;
        for (int column = 0; column < missing.Length && pivot < rows.Count; column++)
        {
            int selected = rows.FindIndex(pivot, r => r[column] != 0);
            if (selected < 0) { continue; }
            (rows[selected], rows[pivot]) = (rows[pivot], rows[selected]);
            byte inverse = Inverse(rows[pivot][column]);
            for (int j = column; j < rows[pivot].Length; j++) { rows[pivot][j] = Mul(rows[pivot][j], inverse); }
            for (int r = 0; r < rows.Count; r++)
            {
                if (r == pivot) { continue; }
                byte c = rows[r][column];
                for (int j = column; j < rows[r].Length; j++) { rows[r][j] ^= Mul(c, rows[pivot][j]); }
            }
            pivot++;
        }
        foreach (byte[] row in rows)
        {
            int column = -1;
            for (int i = 0; i < missing.Length; i++)
            {
                if (row[i] == 0) { continue; }
                if (column >= 0 || row[i] != 1) { column = -1; break; }
                column = i;
            }
            if (column < 0) { continue; }
            int size = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(missing.Length));
            if (size > length - 2) { continue; }
            yield return (missing[column], row.AsSpan(missing.Length + 2, size).ToArray());
        }
    }
}
