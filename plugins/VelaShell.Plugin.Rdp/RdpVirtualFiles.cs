namespace VelaShell.Plugin.Rdp;

internal interface IRdpVirtualFileClipboard : IAsyncDisposable
{
    bool IsCurrent { get; }
    Task PublishAsync(RdpVirtualFiles files, uint? expectedSequence, CancellationToken ct);
}

// No destination path belongs here: the shell which requests the streams owns destination I/O.
internal sealed class RdpVirtualFiles : IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly CancellationToken _token;
    private int _disposed;
    private readonly Func<uint, ulong, uint, CancellationToken, Task<byte[]>> _read;
    private readonly RdpClipboardTransfer _progress;
    private readonly Lock _gate = new();
    private bool _started;
    private bool _retired;
    private int _leases;
    private readonly Action? _released;
    internal IReadOnlyList<RdpClipboardFile> Files { get; }
    internal bool Cancelled => _token.IsCancellationRequested;
    internal RdpVirtualFiles(IReadOnlyList<RdpClipboardFile> files,
        Func<uint, ulong, uint, CancellationToken, Task<byte[]>> read, RdpClipboardTransfer progress, CancellationToken ct, Action? released = null)
    {
        Files = RdpClipboardCodec.DecodeFiles(RdpClipboardCodec.EncodeFiles(files));
        if (Files.Sum(f => f.Length) > RdpClipboardFiles.MaxTransferBytes)
        {
            throw new InvalidDataException("Clipboard files exceed the 1 GiB transfer limit.");
        }

        _read = read;
        _released = released;
        _progress = progress;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _token = _cancel.Token;
    }
    internal async Task<byte[]> ReadAsync(uint index, long offset, int count)
    {
        using IDisposable lease = Acquire();
        _token.ThrowIfCancellationRequested();
        if (index >= Files.Count || Files[(int)index].IsDirectory || offset < 0 || offset > Files[(int)index].Length || count < 0)
        {
            throw new InvalidDataException("Invalid virtual clipboard file range.");
        }

        RdpClipboardFile file = Files[(int)index];
        count = (int)Math.Min(Math.Min(count, RdpClipboardFiles.ChunkSize), file.Length - offset);
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                if (_progress.Snapshot?.State != "Completed")
                {
                    _progress.Start(Files.Sum(f => f.Length));
                }
            }
        }
        try
        {
            byte[] data = count == 0 ? [] : await _read(index, (ulong)offset, (uint)count, _token).ConfigureAwait(false);
            _token.ThrowIfCancellationRequested();
            if (data.Length > count || (count > 0 && data.Length == 0))
            {
                throw new InvalidDataException("Invalid clipboard file block.");
            }

            _progress.Sent(index, offset, data.Length, file.Name, "Delivered");
            return data;
        }
        catch (Exception ex)
        {
            _progress.Finish(ex is OperationCanceledException ? "Cancelled" : ex is TimeoutException ? "TimedOut" : "Failed", ex.Message);
            Dispose();
            throw;
        }
    }
    internal void BeginOperation()
    {
        lock (_gate)
        {
            _token.ThrowIfCancellationRequested();
            _started = true;
            long total = Files.Sum(f => f.Length);
            _progress.Start(total, total == 0 ? "Delivered" : "Transferring");
        }
    }
    internal void DescriptorsDelivered()
    {
        if (Files.All(f => f.Length == 0))
        {
            lock (_gate)
            {
                if (_progress.Snapshot is null)
                {
                    _progress.Start(0, "Ready");
                }

                _progress.Sent(0, 0, 0, "", "Delivered");
            }
        }
    }
    internal void ConfirmShellCompletion() => _progress.ConfirmShellCompletion();
    internal void EndOperation(int result, uint effects)
    {
        if (result < 0)
        {
            _progress.Finish(result is unchecked((int)0x80004004) or unchecked((int)0x800704C7) ? "Cancelled" : "Failed",
                $"Shell copy failed (0x{result:X8}).", force: true);
            Dispose();
        }
        else if ((effects & 3) != 0)
        {
            ConfirmShellCompletion();
        }
        else
        {
            _progress.Finish("Cancelled", force: true);
        }
    }
    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            _token.ThrowIfCancellationRequested();
            _leases++;
            return new Lease(this);
        }
    }
    internal void Retire()
    {
        lock (_gate)
        {
            _retired = true; if (_leases == 0)
            {
                Dispose();
            }
        }
    }
    private void ReleaseLease()
    {
        lock (_gate)
        {
            if (--_leases == 0 && _retired)
            {
                Dispose();
            }
        }
    }
    private sealed class Lease(RdpVirtualFiles owner) : IDisposable
    {
        private RdpVirtualFiles? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
    public void Dispose()
    {
        // Existing COM streams retain the cancelled token after registration resources are released.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        _cancel.Dispose();
        _progress.Finish("Cancelled");
        _released?.Invoke();
    }
}
