using System.Buffers;
using System.Threading.Channels;

namespace VelaShell.Vnc.Audio;

/// <summary>
/// Plays the QEMU Audio stream. The receive loop only hands data over (<see cref="Push"/> never blocks): a worker gathers
/// it into 40 ms buffers and keeps at most <see cref="MaxOutstanding"/> of them queued at the output, so latency stays
/// bounded. When the network delivers faster than the device plays (clock drift, a burst after a stall), the newest data
/// is dropped — QEMU itself drops audio under back-pressure. A device failure stops audio only.
/// </summary>
internal sealed class VncAudioPlayer : IAsyncDisposable
{
    /// <summary>What the client asks for with set-format: signed 16-bit (little-endian, QEMU's default), stereo, 44.1 kHz.</summary>
    internal static readonly AudioFormat Format = new(2, 44100, 16);

    internal const int ChunkBytes = 44100 * 4 / 25;   // 40 ms of S16 stereo: 7056 bytes, frame-aligned
    internal const int MaxOutstanding = 8;              // 320 ms queued at the device at most
    private const int QueueCapacity = 256;
    private const long MaxQueuedBytes = 4 * 1024 * 1024;   // about 24 s of audio: a stalled device, not a backlog to keep

    private readonly Func<IAudioOutput> _createOutput;
    private readonly Action<Exception> _failed;
    private readonly Action<string>? _log;
    private readonly Channel<byte[]?> _queue = Channel.CreateBounded<byte[]?>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _dropped;
    private long _queuedBytes;
    private int _stopped;

    internal VncAudioPlayer(Func<IAudioOutput> createOutput, Action<Exception> failed, Action<string>? log)
    {
        _createOutput = createOutput;
        _failed = failed;
        _log = log;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Bytes discarded because the output fell behind.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues PCM data (frame-aligned S16 stereo). Never blocks.</summary>
    internal void Push(byte[] data)
    {
        if (Volatile.Read(ref _stopped) != 0 || data.Length == 0) { return; }
        bool queued = Interlocked.Add(ref _queuedBytes, data.Length) <= MaxQueuedBytes && _queue.Writer.TryWrite(data);
        if (!queued)
        {
            _ = Interlocked.Add(ref _queuedBytes, -data.Length);
            if (Interlocked.Add(ref _dropped, data.Length) == data.Length)
            {
                _log?.Invoke("VNC audio output fell behind; dropping audio.");
            }
        }
    }

    /// <summary>The server ended the stream: play what is gathered without waiting for a full buffer.</summary>
    internal void Flush()
    {
        if (Volatile.Read(ref _stopped) == 0) { _ = _queue.Writer.TryWrite(null); }
    }

    private async Task RunAsync()
    {
        CancellationToken ct = _stop.Token;
        IAudioOutput? output = null;
        var pending = new Queue<Task>();
        var gathered = new ArrayBufferWriter<byte>(ChunkBytes * 2);
        try
        {
            await foreach (byte[]? item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item is not null)
                {
                    _ = Interlocked.Add(ref _queuedBytes, -item.Length);
                    gathered.Write(item);
                }
                while (gathered.WrittenCount >= ChunkBytes || (item is null && gathered.WrittenCount >= Format.BlockAlign))
                {
                    int take = gathered.WrittenCount >= ChunkBytes ? ChunkBytes : gathered.WrittenCount - gathered.WrittenCount % Format.BlockAlign;
                    byte[] chunk = gathered.WrittenSpan[..take].ToArray();
                    byte[] rest = gathered.WrittenSpan[take..].ToArray();
                    gathered.ResetWrittenCount();
                    gathered.Write(rest);

                    output ??= _createOutput();
                    while (pending.Count >= MaxOutstanding) { await pending.Dequeue().ConfigureAwait(false); }
                    pending.Enqueue(output.PlayAsync(Format, chunk, ct));
                    while (pending.TryPeek(out Task? done) && done.IsCompleted) { await pending.Dequeue().ConfigureAwait(false); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Stop();
            try { _failed(ex); } catch { /* observers cannot break audio cleanup */ }
        }
        finally
        {
            // Buffers still queued end with the output; observe them so their cancellation is not reported as unobserved.
            foreach (Task play in pending) { _ = play.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            output?.Dispose();
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
        _ = _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _stop.Cancel();
        try { await _worker.ConfigureAwait(false); } catch { /* reported through the failure callback */ }
        _stop.Dispose();
    }
}
