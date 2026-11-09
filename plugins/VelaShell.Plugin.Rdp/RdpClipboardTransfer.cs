using System.Diagnostics;

namespace VelaShell.Plugin.Rdp;

internal sealed record RdpClipboardProgress(
    string State,
    long Bytes,
    long Total,
    string Name = "",
    string? Error = null,
    double? SpeedBytesPerSecond = null,
    TimeSpan? Remaining = null)
{
    internal bool Active => State is "Preparing" or "Transferring" or "Publishing" or "Retrying";
}

internal sealed class RdpClipboardTransfer
{
    private readonly Lock _gate = new();
    private RdpClipboardProgress? _progress;
    private readonly Dictionary<uint, List<(long Start, long End)>> _ranges = [];
    private long _lastActivity;
    private long _startedAt;

    internal RdpClipboardProgress? Snapshot { get { lock (_gate) { return _progress; } } }
    internal bool IsStalled(TimeSpan timeout)
    {
        lock (_gate) { return (_progress?.Active == true || _progress?.State is "Sent" or "Ready") && Environment.TickCount64 - _lastActivity > timeout.TotalMilliseconds; }
    }
    internal void Touch() { lock (_gate) { _lastActivity = Environment.TickCount64; } }
    internal void Retry()
    {
        lock (_gate)
        {
            if (_progress?.Active == true)
            {
                _progress = _progress with { State = "Retrying" };
            }

            _lastActivity = Environment.TickCount64;
        }
    }
    internal void Start(long total, string state = "Transferring")
    {
        lock (_gate)
        {
            _ranges.Clear();
            _startedAt = Stopwatch.GetTimestamp();
            _progress = new(state, 0, total);
            _lastActivity = Environment.TickCount64;
        }
    }
    internal void Update(long bytes, string name, string state = "Transferring")
    {
        lock (_gate)
        {
            if (_progress?.Active != true) { return; }
            _progress = WithMetrics(_progress with { Bytes = bytes, Name = name, State = state });
            _lastActivity = Environment.TickCount64;
        }
    }
    internal void Sent(uint index, long offset, int length, string name, string completedState = "Sent")
    {
        lock (_gate)
        {
            _lastActivity = Environment.TickCount64;
            if (_progress is null || (!_progress.Active && _progress.State is not ("Ready" or "Sent" or "Delivered" or "Completed"))) { return; }
            if (!_ranges.TryGetValue(index, out List<(long Start, long End)>? ranges)) { ranges = []; _ranges.Add(index, ranges); }
            long end = checked(offset + length), removed = 0;
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                (long Start, long End) range = ranges[i];
                if (range.End < offset || range.Start > end) { continue; }
                offset = Math.Min(offset, range.Start);
                end = Math.Max(end, range.End);
                removed += range.End - range.Start;
                ranges.RemoveAt(i);
            }
            if (ranges.Count >= 4096) { throw new InvalidDataException("Too many disjoint clipboard file ranges."); }
            ranges.Add((offset, end));
            long bytes = _progress.Bytes + end - offset - removed;
            string state = _progress.State is "Sent" or "Delivered" or "Completed" ? _progress.State
                : bytes == _progress.Total ? completedState : "Transferring";
            _progress = WithMetrics(_progress with { Bytes = bytes, Name = name, State = state });
            _lastActivity = Environment.TickCount64;
        }
    }
    internal void ConfirmShellCompletion()
    {
        lock (_gate)
        {
            if (_progress is { } progress && (progress.Active || progress.State is "Ready" or "Sent" or "Delivered"))
            {
                _progress = progress with { State = "Completed", Remaining = null };
            }
        }
    }
    internal void Finish(string state, string? error = null, bool force = false)
    {
        lock (_gate)
        {
            if (force || _progress?.Active == true || _progress?.State == "Ready")
            {
                _progress = (_progress ?? new(state, 0, 0)) with { State = state, Error = error, Remaining = null };
            }
        }
    }

    private RdpClipboardProgress WithMetrics(RdpClipboardProgress progress)
    {
        if (progress.Total <= 0 || progress.Bytes <= 0 || _startedAt == 0) { return progress with { SpeedBytesPerSecond = null, Remaining = null }; }
        double elapsed = (Stopwatch.GetTimestamp() - _startedAt) / (double)Stopwatch.Frequency;
        if (elapsed < 0.25) { return progress with { SpeedBytesPerSecond = null, Remaining = null }; }
        double speed = progress.Bytes / elapsed;
        TimeSpan? remaining = progress.Active && progress.Bytes < progress.Total && speed > 0
            ? TimeSpan.FromSeconds((progress.Total - progress.Bytes) / speed)
            : null;
        return progress with { SpeedBytesPerSecond = speed, Remaining = remaining };
    }
}
