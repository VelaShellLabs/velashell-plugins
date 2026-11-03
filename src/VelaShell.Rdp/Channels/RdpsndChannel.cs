using System.Buffers.Binary;
using VelaShell.Rdp.Audio;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPEA over the reliable static channel; PCM only, without UDP, pitch or volume redirection.</summary>
internal sealed class RdpsndChannel(Func<byte[], CancellationToken, Task> send, IAudioOutput? output, Action<string>? log = null) : IDisposable
{
    private readonly List<AudioFormat> _formats = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _confirmations = Task.CompletedTask;
    private int _inFlight;
    private bool _disposed;
    internal Task<Exception> PlaybackFailure => _failure.Task;
    internal async Task DrainAsync()
    {
        await _confirmations.ConfigureAwait(false);
        if (_disposed)
        {
            throw new OperationCanceledException("RDP audio channel was disposed.");
        }

        _stop.Token.ThrowIfCancellationRequested();
        if (_failure.Task.IsCompletedSuccessfully)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_failure.Task.Result);
        }
    }
    private long _playedBlocks;
    private bool _receivedWave;
    private bool _receivedNonSilentWave;
    private bool _initialized;
    private bool _closed;
    private ushort _version;
    private PendingWave? _pending;
    private sealed record PendingWave(ushort Timestamp, ushort Format, byte Block, int Length, byte[] Prefix);

    internal async Task ProcessAsync(byte[] pdu, long receivedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        if (_failure.Task.IsCompletedSuccessfully)
        {
            throw new IOException("RDP audio playback failed.", _failure.Task.Result);
        }

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
                Log("training replied");
                break;
            case 1:
                Require(bodySize == 0, "Invalid audio close PDU.");
                _closed = true;
                await DrainAsync().WaitAsync(ct).ConfigureAwait(false);
                output?.Close();
                Log($"server closed output; playedBlocks={_playedBlocks}");
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
        await DrainAsync().WaitAsync(ct).ConfigureAwait(false);
        output?.Close();
        _formats.Clear();
        _formats.AddRange(formats);
        _version = Math.Min((ushort)8, U16(pdu, 21));
        _initialized = true;
        _closed = false;
        _playedBlocks = 0;
        _receivedWave = _receivedNonSilentWave = false;
        Log($"formats received; serverVersion={U16(pdu, 21)}; offered={count}; accepted={formats.Count}; playback={output is not null}");
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
        Log($"formats replied; version={_version}; quality={(_version >= 6 ? "high" : "legacy")}");
        if (output is not null && _formats.Count == 0)
        {
            throw new IOException("The RDP server offered no supported PCM audio format (8/16-bit mono/stereo, 8–48 kHz).");
        }
    }

    private Task PlayAsync(ushort timestamp, ushort index, byte block, byte[] data, long receivedAt, CancellationToken ct)
    {
        ValidateFormat(index);
        AudioFormat format = _formats[index];
        Require(data.Length > 0 && data.Length % format.BlockAlign == 0, "Audio samples are not frame aligned.");
        if (!_receivedNonSilentWave)
        {
            bool silent = !data.AsSpan().ContainsAnyExcept(format.BitsPerSample == 8 ? (byte)128 : (byte)0);
            if (!_receivedWave || !silent)
            {
                Log($"wave received; format={index}; rate={format.SampleRate}; channels={format.Channels}; bits={format.BitsPerSample}; bytes={data.Length}; silent={silent}");
            }
            _receivedNonSilentWave = !silent;
        }
        _receivedWave = true;
        if (Volatile.Read(ref _inFlight) >= 128)
        {
            throw new IOException("RDP audio confirmation queue capacity exceeded.");
        }

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        Task playback;
        try { playback = output!.PlayAsync(format, data, lifetime.Token); }
        catch { lifetime.Dispose(); throw; }
        _ = Interlocked.Increment(ref _inFlight);
        _confirmations = ConfirmAsync(_confirmations, playback, timestamp, block, receivedAt, lifetime);
        return Task.CompletedTask;
    }

    private async Task ConfirmAsync(Task previous, Task playback, ushort timestamp, byte block,
        long receivedAt, CancellationTokenSource lifetime)
    {
        try
        {
            // Start playback immediately, but retain wire order even if a backend completes out of order.
            await playback.WaitAsync(lifetime.Token).ConfigureAwait(false);
            await previous.WaitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (_failure.Task.IsCompletedSuccessfully)
            {
                return;
            }

            byte[] confirm = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(confirm, unchecked((ushort)(timestamp + Environment.TickCount64 - receivedAt)));
            confirm[2] = block;
            await send(Packet(5, confirm), lifetime.Token).ConfigureAwait(false);
            if (Interlocked.Increment(ref _playedBlocks) == 1) { Log("first playback completed and confirmed"); }
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested)
            {
                _ = _failure.TrySetResult(error);
            }
        }
        finally
        {
            _ = Interlocked.Decrement(ref _inFlight);
            lifetime.Dispose();
        }
    }

    private void Log(string message)
    {
        try { log?.Invoke(message); } catch { /* Diagnostics must not interrupt playback. */ }
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
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        output?.Dispose();
        _stop.Dispose();
    }
}
