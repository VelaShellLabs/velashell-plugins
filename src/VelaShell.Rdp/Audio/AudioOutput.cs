using System.Diagnostics;
using System.Threading.Channels;

namespace VelaShell.Rdp.Audio;

internal readonly record struct AudioFormat(ushort Channels, uint SampleRate, ushort BitsPerSample)
{
    internal int BlockAlign => Channels * BitsPerSample / 8;
}

internal interface IAudioOutput : IDisposable
{
    Task PlayAsync(AudioFormat format, byte[] data, CancellationToken ct);
    void Close();
}

internal static class AudioOutput
{
    internal static IAudioOutput Create() => Create(null);

    internal static IAudioOutput Create(Action<string>? log) => new NativeAudioOutput(
        OperatingSystem.IsWindows() ? new WaveOutDevice() :
        OperatingSystem.IsMacOS() ? new AudioQueueDevice() :
        OperatingSystem.IsLinux() ? new AlsaDevice() :
        throw new PlatformNotSupportedException("RDP audio playback requires Windows, macOS or Linux."), log: log);
}

// All device methods run on the output worker; Close/Dispose only invalidate and enqueue work.
internal interface IPcmDevice
{
    void Open(AudioFormat format);
    // Start appends a buffer; IsComplete consumes only the oldest completed buffer.
    void Start(byte[] data);
    bool IsComplete();
    long PendingFrames { get; }
    void Reset();
    void Close();
}

internal sealed class NativeAudioOutput : IAudioOutput
{
    private readonly IPcmDevice _device;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly TimeSpan _stallAllowance;
    private int _generation;
    private int _disposed;
    private Exception? _closeError;
    private readonly Lock _budgetGate = new();
    private int _queuedCount;
    private long _queuedBytes;
    private double _queuedMilliseconds;

    private readonly Action<string>? _log;

    internal NativeAudioOutput(IPcmDevice device, TimeSpan? stallAllowance = null, Action<string>? log = null)
    {
        _device = device;
        _log = log;
        _stallAllowance = stallAllowance ?? TimeSpan.FromSeconds(5);
        Completion = Task.Run(RunAsync);
    }

    internal Task Completion { get; }

    public Task PlayAsync(AudioFormat format, byte[] data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(data);
        if (format.Channels is not (1 or 2) || format.SampleRate is < 8000 or > 48000 ||
            format.BitsPerSample is not (8 or 16) || data.Length % format.BlockAlign != 0)
        {
            throw new ArgumentException("RDP audio requires frame-aligned PCM8/PCM16, 1–2 channels, 8000–48000 Hz.", nameof(format));
        }

        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var command = new Command(Volatile.Read(ref _generation), format, data, ct,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Stopwatch.GetTimestamp());
        lock (_budgetGate)
        {
            double duration = DurationMilliseconds(command);
            if (_queuedCount >= 128 || _queuedBytes + data.Length > 1024 * 1024 ||
                (_queuedCount > 0 && _queuedMilliseconds + duration > 2000))
                return Task.FromException(new IOException("RDP audio output queue capacity exceeded."));
            _queuedCount++;
            _queuedBytes += data.Length;
            _queuedMilliseconds += duration;
            command.QueueAllowance = TimeSpan.FromMilliseconds(_queuedMilliseconds + 100);
            if (!_commands.Writer.TryWrite(command))
            {
                ReleaseBudget(command);
                command.Result!.SetException(new IOException("RDP audio output queue is closed."));
            }
        }
        // Keep the caller cancellable even if a system driver stalls inside a native call. Native
        // buffers remain owned by the worker until that call returns and cleanup is safe.
        return WaitForPlaybackAsync(command);
    }

    private async Task WaitForPlaybackAsync(Command command)
    {
        try
        {
            await command.Result!.Task.WaitAsync(_stallAllowance + command.QueueAllowance, command.Cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The timer can expire just before the stopwatch deadline due to millisecond rounding.
            // Invalidate the command before the caller can unblock a stalled native operation.
            Volatile.Write(ref command.TimedOut, 1);
            throw;
        }
    }

    public void Close()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        int generation = Interlocked.Increment(ref _generation);
        // A full queue already wakes the worker, which also observes the generation change.
        _commands.Writer.TryWrite(new Command(generation, default, null, default, null, 0));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _generation);
        _commands.Writer.TryComplete();
    }

    private async Task RunAsync()
    {
        AudioFormat? opened = null;
        int deviceGeneration = -1;
        var waiting = new Queue<Command>();
        var active = new Queue<Command>();
        var timing = new AudioPlayoutTiming();
        double waitingMilliseconds = 0;
        long bufferingSince = 0;
        long lastReport = 0;
        try
        {
            while (true)
            {
                while (_commands.Reader.TryRead(out Command? incoming))
                {
                    if (incoming.Result is null) continue;
                    waiting.Enqueue(incoming);
                    waitingMilliseconds += DurationMilliseconds(incoming);
                }
                Command? submitting = null;
                try
                {
                    foreach (Command command in active) CheckCancellation(command);
                    if (opened is not null && deviceGeneration != Volatile.Read(ref _generation))
                    {
                        CloseDevice();
                        opened = null;
                        timing = new AudioPlayoutTiming();
                    }
                    // Reap completed buffers without draining the device. Later buffers keep playing.
                    while (active.Count > 0 && _device.IsComplete())
                    {
                        Command completed = active.Peek();
                        CheckCancellation(completed);
                        active.Dequeue();
                        Finish(completed);
                    }
                    if (active.Count == 0 && bufferingSince == 0)
                    {
                        bufferingSince = Stopwatch.GetTimestamp();
                        timing.ResetDrift();
                    }
                    while (waiting.TryPeek(out Command? next) && active.Count < 32)
                    {
                        submitting = next;
                        CheckCancellation(next);
                        if (_closeError is { } closeError)
                        {
                            _closeError = null;
                            throw new IOException("The previous RDP audio device could not be closed.", closeError);
                        }
                        if (next.Data!.Length == 0)
                        {
                            waiting.Dequeue();
                            Finish(next);
                            submitting = null;
                            continue;
                        }
                        if (opened != next.Format)
                        {
                            if (active.Count > 0) break;
                            _device.Close();
                            opened = null;
                            _device.Open(next.Format);
                            opened = next.Format;
                            deviceGeneration = next.Generation;
                            timing = new AudioPlayoutTiming();
                            bufferingSince = Stopwatch.GetTimestamp();
                            CheckCancellation(next);
                        }
                        if (!next.Observed)
                        {
                            timing.ObserveArrival(next.EnqueuedAt * 1000.0 / Stopwatch.Frequency, DurationMilliseconds(next));
                            next.Observed = true;
                        }
                        if (active.Count == 0 && Stopwatch.GetElapsedTime(bufferingSince).TotalMilliseconds < timing.TargetMilliseconds)
                            break;
                        double deviceMilliseconds = _device.PendingFrames * 1000.0 / next.Format.SampleRate;
                        if (active.Count > 0 && deviceMilliseconds >= 120) break;
                        double duration = DurationMilliseconds(next);
                        timing.UpdateRate(deviceMilliseconds + waitingMilliseconds - duration, duration);
                        _device.Start(timing.Resample(next.Format, next.Data));
                        waiting.Dequeue();
                        waitingMilliseconds -= duration;
                        active.Enqueue(next);
                        if (_log is not null && (lastReport == 0 || Stopwatch.GetElapsedTime(lastReport).TotalSeconds >= 5))
                        {
                            lastReport = Stopwatch.GetTimestamp();
                            try
                            {
                                _log($"playout; targetMs={timing.TargetMilliseconds:F1}; bufferedMs={deviceMilliseconds + waitingMilliseconds + duration:F1}; correctionPpm={timing.RateCorrection * 1000000:F0}; inFlight={active.Count}");
                            }
                            catch { /* Diagnostics cannot interrupt native buffer ownership. */ }
                        }
                        bufferingSince = 0;
                        submitting = null;
                    }
                }
                catch (Exception error)
                {
                    // Native ownership stays on this worker, even if callers already cancelled.
                    try { _device.Reset(); }
                    catch (Exception resetError) { error = new AggregateException(error, resetError); }
                    CloseDevice();
                    opened = null;
                    timing = new AudioPlayoutTiming();
                    bufferingSince = 0;
                    while (active.TryDequeue(out Command? command)) Finish(command, error);
                    if (submitting is not null && waiting.TryPeek(out Command? first) && ReferenceEquals(first, submitting))
                    {
                        waiting.Dequeue();
                        waitingMilliseconds -= DurationMilliseconds(submitting);
                        Finish(submitting, error);
                    }
                }
                if (active.Count == 0 && waiting.Count == 0)
                {
                    bufferingSince = 0;
                    if (!await _commands.Reader.WaitToReadAsync().ConfigureAwait(false)) break;
                }
                else
                {
                    // Only active playback needs clock checks; idle outputs sleep on the channel.
                    await Task.Delay(5).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CloseDevice();
        }
    }

    private static double DurationMilliseconds(Command command) =>
        command.Data!.Length * 1000.0 / command.Format.BlockAlign / command.Format.SampleRate;

    private void ReleaseBudget(Command command)
    {
        lock (_budgetGate)
        {
            _queuedCount--;
            _queuedBytes -= command.Data!.Length;
            _queuedMilliseconds -= DurationMilliseconds(command);
        }
    }

    private void Finish(Command command, Exception? error = null)
    {
        ReleaseBudget(command);
        if (error is OperationCanceledException)
            command.Result!.TrySetCanceled(command.Cancellation.IsCancellationRequested ? command.Cancellation : new CancellationToken(true));
        else if (error is not null)
            command.Result!.TrySetException(error);
        else command.Result!.TrySetResult();
    }

    private void CheckCancellation(Command command)
    {
        command.Cancellation.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || command.Generation != Volatile.Read(ref _generation))
            throw new OperationCanceledException("RDP audio playback was closed.");
        TimeSpan deadline = _stallAllowance + command.QueueAllowance;
        if (Volatile.Read(ref command.TimedOut) != 0 || Stopwatch.GetElapsedTime(command.EnqueuedAt) >= deadline)
            throw new TimeoutException($"RDP audio device stalled for {deadline.TotalSeconds:F1} seconds.");
    }

    private void CloseDevice()
    {
        try { _device.Close(); }
        catch (Exception error)
        {
            _closeError = error;
            Trace.TraceError("RDP audio device cleanup failed: {0}", error);
        }
    }

    private sealed record Command(int Generation, AudioFormat Format, byte[]? Data,
        CancellationToken Cancellation, TaskCompletionSource? Result, long EnqueuedAt)
    {
        internal int TimedOut;
        internal TimeSpan QueueAllowance;
        internal bool Observed;
    }
}
