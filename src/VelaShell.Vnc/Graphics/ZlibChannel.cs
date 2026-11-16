using System.IO.Compression;

namespace VelaShell.Vnc.Graphics;

/// <summary>
/// One persistent zlib stream (ZRLE uses one per connection, Tight four). The server compresses every rectangle with a
/// sync flush, so the compressed bytes of one rectangle always inflate completely on their own while the dictionary
/// carries over to the next. A <see cref="ZLibStream"/> over a feed that hands it one rectangle at a time does exactly
/// that: when the feed runs dry it returns what it has, and its inflater state survives for the next rectangle.
/// </summary>
internal sealed class ZlibChannel : IDisposable
{
    private readonly Feed _feed = new();
    private ZLibStream? _inflater;

    /// <summary>Starts a new stream (Tight's per-stream reset bits).</summary>
    internal void Reset()
    {
        _inflater?.Dispose();
        _inflater = null;
        _feed.Set([]);
    }

    /// <summary>Inflates one rectangle's compressed bytes into exactly <paramref name="expected"/> bytes.</summary>
    internal byte[] Inflate(byte[] compressed, int expected)
    {
        _feed.Set(compressed);
        _inflater ??= new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[expected];
        int filled = 0;
        try
        {
            while (filled < expected)
            {
                int read = _inflater.Read(output, filled, expected - filled);
                if (read == 0)
                {
                    throw new VncProtocolException($"Compressed rectangle data inflated to {filled} bytes instead of {expected}.");
                }
                filled += read;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new VncProtocolException("The server sent corrupt compressed data.", ex);
        }

        return output;
    }

    /// <summary>Inflates everything one rectangle's compressed bytes hold, refusing more than <paramref name="limit"/> bytes.</summary>
    internal byte[] InflateAll(byte[] compressed, int limit)
    {
        _feed.Set(compressed);
        _inflater ??= new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
        var output = new MemoryStream(Math.Min(limit, Math.Max(4096, compressed.Length * 4)));
        byte[] chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = _inflater.Read(chunk)) > 0)
            {
                if (output.Length + read > limit)
                {
                    throw new VncProtocolException("A compressed rectangle inflated to more data than its size allows.");
                }
                output.Write(chunk, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new VncProtocolException("The server sent corrupt compressed data.", ex);
        }

        return output.ToArray();
    }

    public void Dispose() => _inflater?.Dispose();

    private sealed class Feed : Stream
    {
        private byte[] _data = [];
        private int _position;

        internal void Set(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Bounds-checked sequential reads over inflated rectangle data.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    internal readonly int Remaining => _data.Length - _position;

    internal byte U8()
    {
        if (_position >= _data.Length) { throw Short(); }
        return _data[_position++];
    }

    internal ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _data.Length - _position) { throw Short(); }
        ReadOnlySpan<byte> span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private static VncProtocolException Short() => new("A rectangle's encoded data ended early.");
}
