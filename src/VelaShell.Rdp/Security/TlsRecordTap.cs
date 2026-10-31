namespace VelaShell.Rdp.Security;

/// <summary>
/// A pass-through stream under the <see cref="System.Net.Security.SslStream" /> that remembers the framing of
/// the TLS records the server sends (content type and length; record headers are plaintext). When a read
/// fails inside SChannel/OpenSSL — e.g. SEC_E_INTERNAL_ERROR right after the first NLA message — this tells
/// whether the server answered with application data, an alert, or a renegotiation (handshake) record.
/// Only headers are kept (plus the two bytes of an unencrypted alert); no payload is ever stored.
/// </summary>
internal sealed class TlsRecordTap(Stream inner) : Stream
{
    private const int Keep = 6;

    private readonly Lock _gate = new();
    private readonly Queue<string> _sinceMark = new();
    private readonly byte[] _header = new byte[5];
    private int _headerFill;
    private int _bodyRemaining;
    private byte[]? _alertBody;
    private int _alertFill;
    private int _droppedSinceMark;
    private bool _marked;
    private bool _stopped;
    private bool _unparseable;

    /// <summary>Starts a fresh record list; called once the handshake has completed.</summary>
    internal void MarkHandshakeComplete()
    {
        lock (_gate)
        {
            _sinceMark.Clear();
            _droppedSinceMark = 0;
            _marked = true;
        }
    }

    /// <summary>Stops observing (the connection is up; no per-read cost for the rest of the session).</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
        }
    }

    /// <summary>
    /// The records received after the handshake, e.g. "Alert 26 B" or "ApplicationData 120 B, Alert 26 B";
    /// <see langword="null" /> before the handshake has completed.
    /// </summary>
    internal string? DescribeSinceHandshake()
    {
        lock (_gate)
        {
            if (!_marked)
            {
                return null;
            }
            if (_unparseable)
            {
                return "unparseable TLS framing";
            }
            if (_sinceMark.Count == 0)
            {
                return _headerFill > 0 ? "partial record header" : "nothing";
            }

            string list = string.Join(", ", _sinceMark);
            return _droppedSinceMark > 0 ? $"… {_droppedSinceMark} more, {list}" : list;
        }
    }

    private void Observe(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_stopped || _unparseable)
            {
                return;
            }

            while (!data.IsEmpty)
            {
                if (_bodyRemaining > 0)
                {
                    int take = Math.Min(_bodyRemaining, data.Length);
                    if (_alertBody is not null)
                    {
                        int copy = Math.Min(take, _alertBody.Length - _alertFill);
                        data[..copy].CopyTo(_alertBody.AsSpan(_alertFill));
                        _alertFill += copy;
                        if (_alertFill == _alertBody.Length)
                        {
                            Add($"Alert {DescribeAlert(_alertBody[0], _alertBody[1])}");
                            _alertBody = null;
                        }
                    }
                    _bodyRemaining -= take;
                    data = data[take..];
                    continue;
                }

                int need = Math.Min(_header.Length - _headerFill, data.Length);
                data[..need].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += need;
                data = data[need..];
                if (_headerFill < _header.Length)
                {
                    break;
                }

                _headerFill = 0;
                byte type = _header[0];
                int length = (_header[3] << 8) | _header[4];
                if (type is < 20 or > 24 || _header[1] != 3)
                {
                    _unparseable = true;
                    return;
                }

                _bodyRemaining = length;
                if (type == 21 && length == 2)
                {
                    // An unencrypted alert (sent before keys are in force, or by a stack that aborts in clear).
                    _alertBody = new byte[2];
                    _alertFill = 0;
                }
                else
                {
                    Add($"{TypeName(type)} {length} B");
                }
            }
        }
    }

    private void Add(string record)
    {
        if (_sinceMark.Count == Keep)
        {
            _ = _sinceMark.Dequeue();
            _droppedSinceMark++;
        }
        _sinceMark.Enqueue(record);
    }

    private static string TypeName(byte type) => type switch
    {
        20 => "ChangeCipherSpec",
        21 => "Alert",
        22 => "Handshake",
        23 => "ApplicationData",
        _ => "Heartbeat"
    };

    private static string DescribeAlert(byte level, byte description)
    {
        string name = description switch
        {
            0 => "close_notify",
            10 => "unexpected_message",
            20 => "bad_record_mac",
            40 => "handshake_failure",
            47 => "illegal_parameter",
            50 => "decode_error",
            51 => "decrypt_error",
            70 => "protocol_version",
            71 => "insufficient_security",
            80 => "internal_error",
            90 => "user_canceled",
            100 => "no_renegotiation",
            _ => "alert"
        };
        return $"{(level == 2 ? "fatal" : "warning")} {name}({description})";
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
