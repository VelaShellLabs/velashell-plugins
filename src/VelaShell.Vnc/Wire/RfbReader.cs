using System.Buffers.Binary;

namespace VelaShell.Vnc.Wire;

/// <summary>
/// Buffered big-endian reader over the RFB byte stream (RFC 6143 §7: all multi-byte integers are big-endian).
/// Small fields are served from one 64 KiB buffer; large payloads are copied straight into the caller's memory.
/// </summary>
internal sealed class RfbReader(Stream stream)
{
    private Stream _stream = stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;

    /// <summary>Bytes received but not yet consumed.</summary>
    internal int Buffered => _end - _start;

    /// <summary>
    /// Continues on another stream (the TLS stream wrapping this one after a VeNCrypt upgrade). Nothing may be buffered:
    /// bytes read ahead from the plain stream would belong to the TLS handshake.
    /// </summary>
    internal void Rebase(Stream stream)
    {
        if (Buffered != 0)
        {
            throw new VncProtocolException("The server sent data before the TLS handshake started.");
        }

        _stream = stream;
    }

    private async ValueTask EnsureAsync(int count, CancellationToken ct)
    {
        if (_end - _start >= count)
        {
            return;
        }

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        while (_end < count)
        {
            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The VNC server closed the connection.");
            }

            _end += read;
        }
    }

    internal async ValueTask<byte> U8Async(CancellationToken ct)
    {
        await EnsureAsync(1, ct).ConfigureAwait(false);
        return _buffer[_start++];
    }

    internal async ValueTask<ushort> U16Async(CancellationToken ct)
    {
        await EnsureAsync(2, ct).ConfigureAwait(false);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_start));
        _start += 2;
        return value;
    }

    internal async ValueTask<uint> U32Async(CancellationToken ct)
    {
        await EnsureAsync(4, ct).ConfigureAwait(false);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_start));
        _start += 4;
        return value;
    }

    internal async ValueTask<int> S32Async(CancellationToken ct) => unchecked((int)await U32Async(ct).ConfigureAwait(false));

    /// <summary>Fills <paramref name="destination"/> completely.</summary>
    internal async ValueTask ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        int buffered = Math.Min(Buffered, destination.Length);
        _buffer.AsSpan(_start, buffered).CopyTo(destination.Span);
        _start += buffered;
        if (buffered < destination.Length)
        {
            await _stream.ReadExactlyAsync(destination[buffered..], ct).ConfigureAwait(false);
        }
    }

    internal async ValueTask<byte[]> BytesAsync(int count, CancellationToken ct)
    {
        byte[] bytes = new byte[count];
        await ReadAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    internal async ValueTask SkipAsync(long count, CancellationToken ct)
    {
        while (count > 0)
        {
            int step = (int)Math.Min(count, _buffer.Length);
            await EnsureAsync(step, ct).ConfigureAwait(false);
            _start += step;
            count -= step;
        }
    }
}
