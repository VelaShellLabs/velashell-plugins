using System.Security.Cryptography;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>
/// One network writer, at most one managed copying consumer. Filling and UI copying happen outside the
/// short metadata lock. A busy back buffer defers publication, not decoding; the next publication copies
/// the complete native image, so skipped notifications never lose desktop pixels.
/// </summary>
internal sealed class RdpFrameBuffer : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _consumer = new(1, 1);
    private readonly System.Action _requestRefresh;
    private byte[] _front;
    private byte[] _back;
    private int _frontReaders;
    private int _backReaders;
    private bool _pending;
    private bool _closed;
    private RdpFrameInfo _info;
    public RdpFrameInfo Info { get { lock (_gate) return _info; } }
    public bool Pending { get { lock (_gate) return _pending; } }
    public RdpFrameBuffer(int width, int height, System.Action requestRefresh)
    {
        int bytes = RdpLimits.DesktopBytes(width, height);
        _front = new byte[bytes]; _back = new byte[bytes];
        _info = new(width, height, checked(width * 4), 0);
        _requestRefresh = requestRefresh;
    }
    // Called only by the single native/network writer. The fill callback must be synchronous.
    public bool Publish(System.Action<byte[]> fill)
    {
        byte[] target;
        lock (_gate)
        {
            if (_closed) return false;
            if (_backReaders != 0) { _pending = true; return false; }
            target = _back;
        }
        fill(target);
        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            (_frontReaders, _backReaders) = (0, _frontReaders);
            _info = _info with { Sequence = _info.Sequence + 1 };
            _pending = false;
        }
        return true;
    }
    // Called only by the writer; never waits for the UI. With one consumer at most one old array is retired.
    public void Resize(int width, int height)
    {
        int bytes = RdpLimits.DesktopBytes(width, height);
        var front = new byte[bytes]; var back = new byte[bytes];
        lock (_gate)
        {
            if (_closed) { CryptographicOperations.ZeroMemory(front); CryptographicOperations.ZeroMemory(back); return; }
            if (_frontReaders == 0) CryptographicOperations.ZeroMemory(_front);
            if (_backReaders == 0) CryptographicOperations.ZeroMemory(_back);
            _front = front; _back = back; _frontReaders = _backReaders = 0;
            _info = new(width, height, checked(width * 4), _info.Sequence + 1);
            _pending = false;
        }
    }
    public bool TryCopy(Memory<byte> destination, out RdpFrameInfo info)
    {
        if (!TryAcquire(out var lease)) { info = Info; return false; }
        using (lease)
        {
            info = lease.Info;
            if (destination.Length < info.ByteLength) return false;
            lease.Pixels.CopyTo(destination);
            return true;
        }
    }
    // An internal implementation primitive also lets tests hold the exact same consumer lease deterministically.
    internal bool TryAcquire(out Lease lease)
    {
        lease = null!;
        if (!_consumer.Wait(0)) return false;
        lock (_gate)
        {
            if (_closed) { _consumer.Release(); return false; }
            _frontReaders++;
            lease = new(this, _front, _info);
            return true;
        }
    }
    private void Release(byte[] pixels)
    {
        bool refresh;
        lock (_gate)
        {
            if (ReferenceEquals(pixels, _front)) _frontReaders--;
            else if (ReferenceEquals(pixels, _back)) _backReaders--;
            else CryptographicOperations.ZeroMemory(pixels);
            refresh = _pending && !_closed;
        }
        _consumer.Release();
        if (refresh) _requestRefresh();
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _closed = true;
        await _consumer.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) { CryptographicOperations.ZeroMemory(_front); CryptographicOperations.ZeroMemory(_back); _front = []; _back = []; }
        }
        finally { _consumer.Release(); }
    }
    internal sealed class Lease(RdpFrameBuffer owner, byte[] pixels, RdpFrameInfo info) : IDisposable
    {
        private RdpFrameBuffer? _owner = owner;
        public ReadOnlyMemory<byte> Pixels { get; } = pixels;
        public RdpFrameInfo Info { get; } = info;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(pixels);
    }
}
