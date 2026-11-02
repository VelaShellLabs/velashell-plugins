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
    internal static IAudioOutput Create() => new NativeAudioOutput(
        OperatingSystem.IsWindows() ? new WaveOutDevice() :
        OperatingSystem.IsMacOS() ? new AudioQueueDevice() :
        OperatingSystem.IsLinux() ? new AlsaDevice() :
        throw new PlatformNotSupportedException("RDP audio playback requires Windows, macOS or Linux."));
}

// All device methods run on the output worker; Close/Dispose only invalidate and enqueue work.
internal interface IPcmDevice
{
    void Open(AudioFormat format);
    void Start(byte[] data);
    bool IsComplete();
    void Reset();
    void Close();
}

internal sealed class NativeAudioOutput : IAudioOutput
{
    private readonly IPcmDevice _device;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly TimeSpan _stallAllowance;
    private int _generation;
    private int _disposed;
    private Exception? _closeError;

    internal NativeAudioOutput(IPcmDevice device, TimeSpan? stallAllowance = null)
    {
        _device = device;
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
        if (!_commands.Writer.TryWrite(command))
        {
            command.Result!.SetException(new IOException("RDP audio output queue is full or closed."));
        }
        // Keep the caller cancellable even if a system driver stalls inside a native call. Native
        // buffers remain owned by the worker until that call returns and cleanup is safe.
        return command.Result!.Task.WaitAsync(PlaybackLimit(format, data.Length), ct);
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
        try
        {
            await foreach (Command command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (opened is not null && deviceGeneration != Volatile.Read(ref _generation))
                {
                    CloseDevice();
                    opened = null;
                }
                if (command.Result is null) continue;
                try
                {
                    CheckCancellation(command);
                    if (_closeError is { } closeError)
                    {
                        _closeError = null;
                        throw new IOException("The previous RDP audio device could not be closed.", closeError);
                    }
                    if (command.Data!.Length == 0)
                    {
                        command.Result.SetResult();
                        continue;
                    }
                    if (opened != command.Format)
                    {
                        _device.Close();
                        opened = null;
                        _device.Open(command.Format);
                        opened = command.Format;
                    }
                    deviceGeneration = command.Generation;
                    CheckCancellation(command);
                    _device.Start(command.Data);
                    while (true)
                    {
                        CheckCancellation(command);
                        if (_device.IsComplete()) break;
                        await Task.Delay(2, command.Cancellation).ConfigureAwait(false);
                    }
                    CheckCancellation(command);
                    command.Result.SetResult();
                }
                catch (Exception error)
                {
                    // The caller may already have canceled; only the worker releases in-flight native buffers.
                    try { _device.Reset(); }
                    catch (Exception resetError) { error = new AggregateException(error, resetError); }
                    CloseDevice();
                    opened = null;
                    if (error is OperationCanceledException)
                        command.Result.TrySetCanceled(command.Cancellation.IsCancellationRequested ? command.Cancellation : new CancellationToken(true));
                    else
                        command.Result.TrySetException(error);
                }
            }
        }
        finally
        {
            CloseDevice();
        }
    }

    private TimeSpan PlaybackLimit(AudioFormat format, int length) => _stallAllowance +
        TimeSpan.FromSeconds((double)length / format.BlockAlign / format.SampleRate);

    private void CheckCancellation(Command command)
    {
        command.Cancellation.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || command.Generation != Volatile.Read(ref _generation))
            throw new OperationCanceledException("RDP audio playback was closed.");
        TimeSpan deadline = PlaybackLimit(command.Format, command.Data!.Length);
        if (Stopwatch.GetElapsedTime(command.EnqueuedAt) > deadline)
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
        CancellationToken Cancellation, TaskCompletionSource? Result, long EnqueuedAt);
}
