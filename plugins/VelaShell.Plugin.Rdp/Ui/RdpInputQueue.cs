using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>A bounded UI-side producer. Only this worker awaits the engine's bounded input channel.</summary>
internal sealed class RdpInputQueue(IRdpSession session, Action failed)
{
    private const int Capacity = 128;
    private readonly Lock _gate = new();
    private readonly LinkedList<RdpInput> _queue = new();
    private TaskCompletionSource _drained = Completed();
    private bool _running;
    private bool _failed;
    public Task Drained { get { lock (_gate) return _drained.Task; } }

    public bool Enqueue(RdpInput input)
    {
        bool accepted = true;
        lock (_gate)
        {
            if (_failed) return false;
            if (input.Kind == RdpInputKind.ReleaseAll)
            {
                // Drop unsent downs/moves as one atomic reset, never drop the reset itself.
                _queue.Clear();
            }
            else if (input.Kind == RdpInputKind.PointerMove && _queue.Last?.Value.Kind == RdpInputKind.PointerMove)
            {
                _queue.Last.Value = input;
                return true;
            }
            else if (_queue.Count >= Capacity)
            {
                _queue.Clear();
                input = new(RdpInputKind.ReleaseAll);
                _failed = true; // Require an explicit reconnect after overflow, never resume a partial chord.
                accepted = false;
            }
            _queue.AddLast(input);
            if (!_running)
            {
                _running = true;
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(RunAsync);
            }
        }
        if (!accepted) failed();
        return accepted;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            RdpInput input;
            lock (_gate)
            {
                if (_queue.First is null)
                {
                    _running = false;
                    _drained.TrySetResult();
                    return;
                }
                input = _queue.First.Value;
                _queue.RemoveFirst();
            }
            try
            {
                // In particular, focus-loss releases must not use a cancelled UI lifetime token.
                await session.SendInputAsync(input, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _queue.Clear();
                    _failed = true;
                    _running = false;
                }
                failed(); // Report a safe localized error; engine/document own transport diagnostics.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await session.SendInputAsync(new(RdpInputKind.ReleaseAll), cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) { failed(); } // A broken input path must still proceed to disconnect.
                try { await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { failed(); }
                lock (_gate) _drained.TrySetResult();
                return;
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }
}
