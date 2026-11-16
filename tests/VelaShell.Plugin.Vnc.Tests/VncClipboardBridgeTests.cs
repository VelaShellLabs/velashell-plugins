using System.Collections.Concurrent;
using VelaShell.PluginSdk.Clipboard;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Vnc.Tests;

[TestClass]
public sealed class VncClipboardBridgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task ConcurrentDuplicatePublishWaitsForSendBeforePaste()
    {
        var entered = Signal();
        var release = Signal();
        var clipboard = new ControlledClipboard { Text = "local" };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession
        {
            ClipboardHandler = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        using var bridge = new VncClipboardBridge(session, context, true);
        Task focus = bridge.PublishLocalAsync();
        await entered.Task.WaitAsync(Timeout);
        Task paste = PasteAsync();
        try
        {
            Assert.IsFalse(focus.IsCompleted);
            Assert.IsFalse(paste.IsCompleted, "A duplicate must not let the paste key overtake the first send.");
            Assert.IsEmpty(session.Keys);
            Assert.HasCount(1, session.SentClipboard);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(focus, paste).WaitAsync(Timeout);
        Assert.AreSequenceEqual(["local"], session.SentClipboard);
        Assert.HasCount(1, session.Keys);

        async Task PasteAsync()
        {
            await bridge.PublishLocalAsync();
            await session.KeyAsync('v', 0x2f, true);
        }
    }

    [TestMethod]
    public async Task QueuedDuplicateRetriesAfterSendFailure()
    {
        var entered = Signal();
        var release = Signal();
        int attempts = 0;
        var clipboard = new ControlledClipboard { Text = "retry" };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession
        {
            ClipboardHandler = async (_, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    throw new IOException("Simulated send failure.");
                }
            }
        };
        using var bridge = new VncClipboardBridge(session, context, true);
        Task first = bridge.PublishLocalAsync();
        await entered.Task.WaitAsync(Timeout);
        Task retry = bridge.PublishLocalAsync();
        try { Assert.IsFalse(retry.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<IOException>(() => first.WaitAsync(Timeout));
        await retry.WaitAsync(Timeout);
        await bridge.PublishLocalAsync().WaitAsync(Timeout);
        Assert.AreEqual(2, attempts, "Only the successful retry commits the deduplication state.");
        Assert.AreSequenceEqual(["retry", "retry"], session.SentClipboard);
    }

    [TestMethod]
    public async Task RemoteWritesStayInOrderAndLocalPublishDoesNotEchoThem()
    {
        var entered = Signal();
        var release = Signal();
        var clipboard = new ControlledClipboard
        {
            SetHandler = async (text, ct) =>
            {
                if (text == "first")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
            }
        };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession();
        using var bridge = new VncClipboardBridge(session, context, true);
        session.RaiseClipboard("first");
        await entered.Task.WaitAsync(Timeout);
        session.RaiseClipboard("second");
        // This queued read is also a barrier for both preceding remote writes.
        Task publish = bridge.PublishLocalAsync();
        try
        {
            Assert.AreSequenceEqual(["first"], clipboard.Started.ToArray());
            Assert.IsFalse(publish.IsCompleted);
            Assert.AreEqual(0, clipboard.ReadCount);
        }
        finally { release.TrySetResult(); }
        await publish.WaitAsync(Timeout);
        Assert.AreSequenceEqual(["first", "second"], clipboard.Written.ToArray());
        Assert.AreEqual("second", clipboard.Text);
        Assert.IsEmpty(session.SentClipboard);
    }

    [TestMethod]
    public async Task FailedRemoteWriteCanRetryTheSameText()
    {
        int attempts = 0;
        var clipboard = new ControlledClipboard
        {
            SetHandler = (_, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new IOException("Simulated clipboard failure."))
                : Task.CompletedTask
        };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession();
        using var bridge = new VncClipboardBridge(session, context, true);
        session.RaiseClipboard("retry");
        session.RaiseClipboard("retry");
        await bridge.PublishLocalAsync().WaitAsync(Timeout);
        Assert.AreEqual(2, attempts);
        Assert.AreSequenceEqual(["retry"], clipboard.Written.ToArray());
        Assert.AreEqual("retry", clipboard.Text);
        Assert.IsEmpty(session.SentClipboard);
    }

    [TestMethod]
    public async Task DisposeCancelsActiveWriteAndSkipsQueuedOperations()
    {
        var entered = Signal();
        var release = Signal();
        CancellationToken activeToken = default;
        var clipboard = new ControlledClipboard
        {
            SetHandler = async (_, ct) =>
            {
                activeToken = ct;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession();
        using var bridge = new VncClipboardBridge(session, context, true);
        session.RaiseClipboard("active");
        await entered.Task.WaitAsync(Timeout);
        session.RaiseClipboard("queued");
        Task queuedPublish = bridge.PublishLocalAsync();
        bridge.Dispose();
        bridge.Dispose();
        session.RaiseClipboard("after disposal");
        await bridge.PublishLocalAsync().WaitAsync(Timeout);
        try
        {
            Assert.IsTrue(activeToken.IsCancellationRequested);
            await Assert.ThrowsAsync<OperationCanceledException>(() => queuedPublish.WaitAsync(Timeout));
            Assert.AreSequenceEqual(["active"], clipboard.Started.ToArray());
            Assert.IsEmpty(clipboard.Written);
            Assert.AreEqual(0, clipboard.ReadCount);
            Assert.IsEmpty(session.SentClipboard);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task DisposePreventsSendWhenActiveReadIgnoresCancellation()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        var clipboard = new ControlledClipboard
        {
            GetHandler = ct =>
            {
                activeToken = ct;
                entered.TrySetResult();
                return release.Task; // Simulate a host that cannot cancel an already-started clipboard read.
            }
        };
        using var context = new TestPluginContext { Clipboard = clipboard };
        var session = new FakeVncSession();
        using var bridge = new VncClipboardBridge(session, context, true);
        Task active = bridge.PublishLocalAsync();
        await entered.Task.WaitAsync(Timeout);
        session.RaiseClipboard("queued");
        Task queued = bridge.PublishLocalAsync();
        bridge.Dispose();
        release.TrySetResult("late local text");
        await Assert.ThrowsAsync<OperationCanceledException>(() => active.WaitAsync(Timeout));
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        Assert.IsTrue(activeToken.IsCancellationRequested);
        Assert.AreEqual(1, clipboard.ReadCount);
        Assert.IsEmpty(session.SentClipboard);
        Assert.IsEmpty(clipboard.Started);
    }

    private sealed class ControlledClipboard : IClipboardApi
    {
        private int _readCount;
        internal string? Text { get; set; }
        internal int ReadCount => Volatile.Read(ref _readCount);
        internal ConcurrentQueue<string> Started { get; } = new();
        internal ConcurrentQueue<string> Written { get; } = new();
        internal Func<CancellationToken, Task<string?>>? GetHandler { get; init; }
        internal Func<string, CancellationToken, Task>? SetHandler { get; init; }

        public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return GetHandler?.Invoke(cancellationToken) ?? Task.FromResult(Text);
        }

        public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Started.Enqueue(text);
            if (SetHandler is not null) { await SetHandler(text, cancellationToken); }
            Text = text;
            Written.Enqueue(text);
        }
    }
}
