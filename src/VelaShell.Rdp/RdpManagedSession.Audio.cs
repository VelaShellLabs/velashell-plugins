using System.Threading.Channels;
using VelaShell.Rdp.Audio;
using VelaShell.Rdp.Channels;

namespace VelaShell.Rdp;

/// <summary>Optional audio diagnostics. Failures stop audio, while the desktop remains connected.</summary>
public interface IRdpAudioSource
{
    /// <summary>Raised on a background thread; the last failure is replayed to late subscribers.</summary>
    event Action<Exception>? AudioPlaybackFailed;
}

public sealed partial class RdpManagedSession : IRdpAudioSource
{
    private Channel<(byte[] Data, long ReceivedAt)>? _audioQueue;
    private CancellationTokenSource? _audioStop;
    private Task? _audioWorker;
    private long _audioQueuedBytes;
    private readonly Lock _audioGate = new();
    private Exception? _audioFailure;
    private Action<Exception>? _audioHandlers;

    public event Action<Exception>? AudioPlaybackFailed
    {
        add
        {
            Exception? failure;
            lock (_audioGate)
            {
                _audioHandlers += value;
                failure = _audioFailure;
            }
            if (failure is not null)
            {
                try { value?.Invoke(failure); } catch { /* Observers cannot interrupt audio cleanup. */ }
            }
        }
        remove { lock (_audioGate) { _audioHandlers -= value; } }
    }

    internal void EnableAudio(bool playback, Func<IAudioOutput>? createOutput = null)
    {
        _audioQueue = Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _audioStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _audioWorker = Task.Run(async () =>
        {
            try
            {
                using var channel = new RdpsndChannel(
                    (pdu, ct) => Connection.SendVirtualChannelAsync("rdpsnd", pdu, ct),
                    playback ? (createOutput ?? AudioOutput.Create)() : null);
                await foreach ((byte[] data, long receivedAt) in _audioQueue.Reader.ReadAllAsync(_audioStop.Token).ConfigureAwait(false))
                {
                    _ = Interlocked.Add(ref _audioQueuedBytes, -data.Length);
                    await channel.ProcessAsync(data, receivedAt, _audioStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_audioStop.IsCancellationRequested) { }
            catch (Exception ex) { FailAudio(ex); }
            finally
            {
                while (_audioQueue.Reader.TryRead(out var pending))
                {
                    _ = Interlocked.Add(ref _audioQueuedBytes, -pending.Data.Length);
                }
            }
        });
    }

    internal void ReceiveAudio(byte[] data)
    {
        if (_audioStop?.IsCancellationRequested != false) { return; }
        long queued = Interlocked.Add(ref _audioQueuedBytes, data.Length);
        if (data.Length > ushort.MaxValue + 4 || queued > 1024 * 1024
            || _audioQueue?.Writer.TryWrite((data, Environment.TickCount64)) != true)
        {
            _ = Interlocked.Add(ref _audioQueuedBytes, -data.Length);
            FailAudio(new IOException("RDP audio queue capacity exceeded; reconnect to resume audio."));
        }
    }

    private void FailAudio(Exception error)
    {
        Action<Exception>? handlers;
        lock (_audioGate)
        {
            if (_audioFailure is not null) { return; }
            _audioFailure = error;
            handlers = _audioHandlers;
        }
        _audioQueue?.Writer.TryComplete();
        _audioStop?.Cancel();
        try { _driveLog?.Invoke($"RDPSND playback stopped: {error.Message}"); }
        catch { /* Diagnostics must not interrupt audio cleanup. */ }
        try { handlers?.Invoke(error); } catch { /* Audio errors must not stop the desktop. */ }
    }
}
