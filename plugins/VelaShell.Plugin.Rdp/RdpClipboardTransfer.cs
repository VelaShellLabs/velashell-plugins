namespace VelaShell.Plugin.Rdp;

internal sealed record RdpClipboardProgress(string State, long Bytes, long Total, string Name = "", string? Error = null)
{
    internal bool Active => State is "Preparing" or "Transferring" or "Publishing" or "Retrying";
}

internal sealed class RdpClipboardTransfer
{
    private readonly Lock _gate = new();
    private RdpClipboardProgress? _progress;
    private readonly Dictionary<uint, List<(long Start, long End)>> _ranges = [];
    private long _lastActivity;

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
            _progress = new(state, 0, total);
            _lastActivity = Environment.TickCount64;
        }
    }
    internal void Update(long bytes, string name, string state = "Transferring")
    {
        lock (_gate)
        {
            if (_progress?.Active != true) { return; }
            _progress = _progress with { Bytes = bytes, Name = name, State = state };
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
            _progress = _progress with { Bytes = bytes, Name = name, State = state };
            _lastActivity = Environment.TickCount64;
        }
    }
    internal void ConfirmShellCompletion()
    {
        lock (_gate)
        {
            if (_progress is { } progress && (progress.Active || progress.State is "Ready" or "Sent" or "Delivered"))
            {
                _progress = progress with { State = "Completed" };
            }
        }
    }
    internal void Finish(string state, string? error = null, bool force = false)
    {
        lock (_gate)
        {
            if (force || _progress?.Active == true || _progress?.State == "Ready") { _progress = (_progress ?? new(state, 0, 0)) with { State = state, Error = error }; }
        }
    }
}
