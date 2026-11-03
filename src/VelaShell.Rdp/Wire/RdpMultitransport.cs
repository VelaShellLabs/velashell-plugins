using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using VelaShell.Rdp.Security;

namespace VelaShell.Rdp.Wire;

internal sealed class RdpMultitransport : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _readEnabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RdpUdpTransport? _udp;
    private SslStream? _tls;
    private DtlsTransport? _dtls;
    private byte[] _dtlsInput = [];
    private int _dtlsOffset, _dtlsCount;
    private readonly RdpAutoDetect _detect;
    private Task? _pump;
    private int _disposed;
    internal bool Lossy { get; }
    internal bool Reading => _readEnabled.Task.IsCompletedSuccessfully;
    internal int FragmentSize => Lossy ? 600 : 1500;

    private RdpMultitransport(bool lossy, RdpNetworkMonitor monitor, CancellationToken ct)
    {
        Lossy = lossy;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _detect = new(monitor);
    }

    internal static async Task<RdpMultitransport> ConnectAsync(IPEndPoint peer, string host, byte[] certificateDigest,
        bool lossy, uint requestId, byte[] cookie, RdpNetworkMonitor monitor, CancellationToken ct)
    {
        var tunnel = new RdpMultitransport(lossy, monitor, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(tunnel._stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using CancellationTokenRegistration registration = timeout.Token.Register(tunnel._stop.Cancel);
        try
        {
            tunnel._udp = await RdpUdpTransport.ConnectAsync(peer, lossy, timeout.Token).ConfigureAwait(false);
            if (lossy)
            {
                tunnel._dtls = await Task.Run(() => new DtlsClientProtocol().Connect(new RdpDtls.Client(certificateDigest),
                    new RdpDtls.Adapter(tunnel._udp, tunnel._stop.Token)), timeout.Token).ConfigureAwait(false);
            }
            else
            {
                tunnel._tls = await RdpTls.AuthenticateAsync(new RdpUdpStream(tunnel._udp), host, certificateDigest, timeout.Token).ConfigureAwait(false);
            }
            if (lossy) { tunnel._udp.RequireCompleteDelivery(); }
            byte[] frame = new byte[28];
            frame[3] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), requestId);
            cookie.CopyTo(frame, 12);
            try { await tunnel.SendFrameAsync(frame, timeout.Token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(frame); }
            byte[] response = await tunnel.ReadFrameAsync(timeout.Token).ConfigureAwait(false);
            if (response.Length != 8 || response[0] != 1 || response[3] != 4 || BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(4)) < 0)
            { throw new IOException("RDP multitransport creation rejected."); }
            return tunnel;
        }
        catch { await tunnel.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal void Start(Func<byte[], Task> deliver, Action<Exception> failed) => _pump = PumpAsync(deliver, failed);
    internal void EnableReading() => _readEnabled.TrySetResult();

    private async Task PumpAsync(Func<byte[], Task> deliver, Action<Exception> failed)
    {
        try
        {
            // The TCP DVC worker opens this gate only after processing Soft-Sync in FIFO order.
            await _readEnabled.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                byte[] frame = await ReadFrameAsync(_stop.Token).ConfigureAwait(false);
                if (frame[0] != 2) { throw new RdpProtocolException("Unexpected multitransport action."); }
                _detect.Observe(frame.Length);
                int headerLength = frame[3];
                int offset = 4;
                while (offset < headerLength)
                {
                    int length = frame[offset];
                    if (length < 2 || offset + length > headerLength) { throw new RdpProtocolException("Invalid tunnel subheader."); }
                    if (frame[offset + 1] == 0)
                    {
                        byte[]? reply = _detect.Process(frame.AsSpan(offset + 2, length - 2), Environment.TickCount64);
                        if (reply is not null) { await SendFrameAsync(Frame(2, [], reply), _stop.Token).ConfigureAwait(false); }
                    }
                    offset += length;
                }
                if (frame.Length > headerLength) { await deliver(frame[headerLength..]).ConfigureAwait(false); }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { failed(ex); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    internal Task SendAsync(byte[] pdu, CancellationToken ct) => SendFrameAsync(Frame(2, pdu), ct);

    internal static byte[] Frame(byte action, byte[] payload, byte[]? autoDetectResponse = null)
    {
        int header = 4 + (autoDetectResponse is null ? 0 : autoDetectResponse.Length + 2);
        byte[] result = new byte[header + payload.Length];
        result[0] = action;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1), checked((ushort)payload.Length));
        result[3] = checked((byte)header);
        if (autoDetectResponse is not null)
        {
            result[4] = checked((byte)(autoDetectResponse.Length + 2)); result[5] = 1;
            autoDetectResponse.CopyTo(result, 6);
        }
        payload.CopyTo(result, header);
        return result;
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _write.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_tls is { } tls) { await tls.WriteAsync(frame, linked.Token).ConfigureAwait(false); }
            else if (_dtls is { } dtls)
            {
                if (frame.Length > dtls.GetSendLimit()) { throw new RdpProtocolException("Lossy tunnel PDU exceeds DTLS MTU."); }
                await Task.Run(() => dtls.Send(frame, 0, frame.Length), linked.Token).ConfigureAwait(false);
            }
        }
        finally { _ = _write.Release(); }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        if (_tls is { } tls)
        {
            byte[] head = new byte[4];
            await tls.ReadExactlyAsync(head, ct).ConfigureAwait(false);
            int length = FrameLength(head);
            byte[] frame = new byte[length];
            head.CopyTo(frame, 0);
            await tls.ReadExactlyAsync(frame.AsMemory(4), ct).ConfigureAwait(false);
            return frame;
        }
        return await Task.Run(() =>
        {
            byte[] head = new byte[4];
            ReadDtlsExactly(head, ct);
            byte[] frame = new byte[FrameLength(head)];
            head.CopyTo(frame, 0);
            ReadDtlsExactly(frame.AsSpan(4), ct);
            return frame;
        }, ct).ConfigureAwait(false);
    }

    private void ReadDtlsExactly(Span<byte> destination, CancellationToken ct)
    {
        // EMT length, not the encrypted record boundary, delimits a message. UDP gap protection
        // is enabled before application traffic so a missing record cannot splice two messages.
        if (_dtlsInput.Length == 0) { _dtlsInput = new byte[_dtls!.GetReceiveLimit()]; }
        while (!destination.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            if (_dtlsOffset == _dtlsCount)
            {
                int received = _dtls!.Receive(_dtlsInput, 0, _dtlsInput.Length, 200);
                if (received <= 0) { continue; }
                _dtlsOffset = 0; _dtlsCount = received;
            }
            int count = Math.Min(destination.Length, _dtlsCount - _dtlsOffset);
            _dtlsInput.AsSpan(_dtlsOffset, count).CopyTo(destination);
            _dtlsOffset += count;
            destination = destination[count..];
        }
    }

    internal static int FrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] > 2 || header[3] < 4) { throw new RdpProtocolException("Invalid multitransport header."); }
        return header[3] + BinaryPrimitives.ReadUInt16LittleEndian(header[1..]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _stop.Cancel();
        if (_udp is not null) { await _udp.DisposeAsync().ConfigureAwait(false); }
        if (_pump is not null) { await _pump.ConfigureAwait(false); }
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            try { if (_tls is not null) { await _tls.DisposeAsync().ConfigureAwait(false); } }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            try { _dtls?.Close(); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally { _ = _write.Release(); _stop.Dispose(); }
    }
}
