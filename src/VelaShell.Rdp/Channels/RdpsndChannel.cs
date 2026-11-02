using System.Buffers.Binary;
using VelaShell.Rdp.Audio;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPEA over the reliable static channel; PCM only, without UDP, pitch or volume redirection.</summary>
internal sealed class RdpsndChannel(Func<byte[], CancellationToken, Task> send, IAudioOutput? output) : IDisposable
{
    private readonly List<AudioFormat> _formats = [];
    private bool _initialized;
    private bool _closed;
    private ushort _version;
    private PendingWave? _pending;
    private sealed record PendingWave(ushort Timestamp, ushort Format, byte Block, int Length, byte[] Prefix);

    internal async Task ProcessAsync(byte[] pdu, long receivedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_pending is { } pending)
        {
            _pending = null;
            // Wave's first four bytes are padding, not an RDPSND header or audio samples.
            Require(pdu.Length == pending.Length && pdu.Length >= 4, "Invalid Wave payload size.");
            byte[] data = (byte[])pdu.Clone();
            pending.Prefix.CopyTo(data, 0);
            await PlayAsync(pending.Timestamp, pending.Format, pending.Block, data, receivedAt, ct).ConfigureAwait(false);
            return;
        }

        Require(pdu.Length >= 4, "Truncated audio header.");
        int bodySize = U16(pdu, 2);
        if (pdu[0] != 2)
        {
            Require(pdu.Length == bodySize + 4, "Invalid audio PDU size.");
        }
        if (pdu[0] == 7)
        {
            await FormatsAsync(pdu, ct).ConfigureAwait(false);
            return;
        }
        Require(_initialized, "Audio data arrived before format negotiation.");
        switch (pdu[0])
        {
            case 6:
                Require(bodySize >= 4, "Truncated audio training PDU.");
                await send(Packet(6, pdu.AsSpan(4, 4).ToArray()), ct).ConfigureAwait(false);
                break;
            case 1:
                Require(bodySize == 0, "Invalid audio close PDU.");
                _closed = true;
                output?.Close();
                break;
            case 2:
                Require(!_closed && pdu.Length == 16 && bodySize >= 12, "Invalid WaveInfo PDU.");
                ValidateFormat(U16(pdu, 6));
                _pending = new(U16(pdu, 4), U16(pdu, 6), pdu[8], bodySize - 8, pdu.AsSpan(12, 4).ToArray());
                break;
            case 13:
                Require(!_closed && _version >= 8 && bodySize >= 12, "Invalid Wave2 PDU.");
                await PlayAsync(U16(pdu, 4), U16(pdu, 6), pdu[8], pdu.AsSpan(16).ToArray(), receivedAt, ct).ConfigureAwait(false);
                break;
            default:
                throw new RdpProtocolException($"Unsupported RDPSND message 0x{pdu[0]:X2}.");
        }
    }

    private async Task FormatsAsync(byte[] pdu, CancellationToken ct)
    {
        Require(pdu.Length >= 24, "Truncated audio formats PDU.");
        int count = U16(pdu, 18);
        int offset = 24;
        var accepted = new List<byte[]>();
        var formats = new List<AudioFormat>();
        for (int i = 0; i < count; i++)
        {
            Require(pdu.Length - offset >= 18, "Truncated audio format.");
            int extra = U16(pdu, offset + 16);
            int length = 18 + extra;
            Require(pdu.Length - offset >= length, "Truncated audio format extension.");
            ushort tag = U16(pdu, offset);
            var format = new AudioFormat(U16(pdu, offset + 2), U32(pdu, offset + 4), U16(pdu, offset + 14));
            if (output is not null && tag == 1 && extra == 0 && format.Channels is 1 or 2
                && format.SampleRate is >= 8000 and <= 48000 && format.BitsPerSample is 8 or 16
                && U16(pdu, offset + 12) == format.BlockAlign
                && U32(pdu, offset + 8) == format.SampleRate * format.BlockAlign)
            {
                accepted.Add(pdu.AsSpan(offset, length).ToArray());
                formats.Add(format);
            }
            offset += length;
        }
        Require(offset == pdu.Length, "Unexpected bytes after audio formats.");
        output?.Close();
        _formats.Clear();
        _formats.AddRange(formats);
        _version = Math.Min((ushort)8, U16(pdu, 21));
        _initialized = true;
        _closed = false;
        byte[] response = new byte[20 + accepted.Sum(f => f.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(response, output is not null ? 1u : 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(14), (ushort)accepted.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(17), 8);
        offset = 20;
        foreach (byte[] format in accepted)
        {
            format.CopyTo(response, offset);
            offset += format.Length;
        }
        await send(Packet(7, response), ct).ConfigureAwait(false);
        if (_version >= 6)
        {
            await send(Packet(12, [2, 0, 0, 0]), ct).ConfigureAwait(false);
        }
        if (output is not null && _formats.Count == 0)
        {
            throw new IOException("The RDP server offered no supported PCM audio format (8/16-bit mono/stereo, 8–48 kHz).");
        }
    }

    private async Task PlayAsync(ushort timestamp, ushort index, byte block, byte[] data, long receivedAt, CancellationToken ct)
    {
        ValidateFormat(index);
        AudioFormat format = _formats[index];
        Require(data.Length > 0 && data.Length % format.BlockAlign == 0, "Audio samples are not frame aligned.");
        await output!.PlayAsync(format, data, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        byte[] confirm = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(confirm, unchecked((ushort)(timestamp + Environment.TickCount64 - receivedAt)));
        confirm[2] = block;
        await send(Packet(5, confirm), ct).ConfigureAwait(false);
    }

    private void ValidateFormat(ushort index) => Require(output is not null && index < _formats.Count, "Invalid negotiated audio format index.");
    private static void Require(bool valid, string message)
    {
        if (!valid) { throw new RdpProtocolException(message); }
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static byte[] Packet(byte type, byte[] body)
    {
        byte[] packet = new byte[4 + body.Length];
        packet[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), checked((ushort)body.Length));
        body.CopyTo(packet, 4);
        return packet;
    }
    public void Dispose() => output?.Dispose();
}
