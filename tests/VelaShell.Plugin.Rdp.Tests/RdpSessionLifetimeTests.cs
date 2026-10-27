using System.Net.Security;
using System.Reflection;
using System.Reflection.Emit;
using Devolutions.IronRdp;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpSessionLifetimeTests
{
    [TestMethod]
    public async Task RealPumpReadFailure_ReleasesNativeAndFramesWithoutExplicitDispose()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackingSslStream();
        var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        Assert.AreNotEqual(IntPtr.Zero, NativePointer(image));
        var session = new RdpSession(stream, image, 2, 2, start.Task);
        var frames = Frames(session);
        byte[] pixels = Pixels(frames, "_front");
        Array.Fill(pixels, (byte)88);
        start.SetResult();
        await session.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(RdpSessionState.Faulted, session.Status.State);
        Assert.AreEqual(1, stream.DisposeCalls);
        Assert.IsTrue(pixels.All(value => value == 0));
        Assert.IsEmpty(Pixels(frames, "_front"));
        Assert.IsFalse(frames.TryAcquire(out _));
        AssertNativeReleased(session, image);
        await session.SendInputAsync(new(RdpInputKind.PointerMove, X: 1, Y: 1));
        await session.DisconnectAsync();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => session.DisposeAsync().AsTask()));
        Assert.AreEqual(1, stream.DisposeCalls);
        Assert.AreEqual(RdpSessionState.Closed, session.Status.State);
    }

    [TestMethod]
    public async Task BlockedExternalNotifier_DoesNotDelayNativeOrFrameCleanup()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stream = new TrackingSslStream();
        var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        Assert.AreNotEqual(IntPtr.Zero, NativePointer(image));
        var session = new RdpSession(stream, image, 2, 2, start.Task);
        session.FrameAvailable += () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            start.SetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, stream.DisposeCalls);
            Assert.IsEmpty(Pixels(Frames(session), "_front"));
            AssertNativeReleased(session, image);
            Task disposal = session.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await session.DisposeAsync(); }
    }

    [TestMethod]
    public async Task StreamDisposeFailure_DoesNotSkipNativeFramesOrIdempotentCleanup()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackingSslStream(throwOnDispose: true);
        var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        Assert.AreNotEqual(IntPtr.Zero, NativePointer(image));
        var session = new RdpSession(stream, image, 2, 2, start.Task);
        start.SetResult();
        await Assert.ThrowsAsync<IOException>(() => session.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(RdpSessionState.Faulted, session.Status.State);
        Assert.IsEmpty(Pixels(Frames(session), "_front"));
        AssertNativeReleased(session, image);
        Task first = session.DisposeAsync().AsTask();
        Task second = session.DisposeAsync().AsTask();
        Assert.AreSame(first, second);
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.AreEqual(1, stream.DisposeCalls);
        Assert.AreEqual(RdpSessionState.Closed, session.Status.State);
    }

    [TestMethod]
    public async Task OutstandingFrameLease_DelaysOnlyFrameZeroingAndReleasesFinalLeaseSafely()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackingSslStream();
        var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        Assert.AreNotEqual(IntPtr.Zero, NativePointer(image));
        var session = new RdpSession(stream, image, 2, 2, start.Task);
        var frames = Frames(session);
        Assert.IsTrue(frames.TryAcquire(out var lease));
        byte[] held = Pixels(frames, "_front");
        Array.Fill(held, (byte)99);
        try
        {
            start.SetResult();
            await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(session.CleanupCompletion.IsCompleted);
            Assert.AreEqual((byte)99, held[0]);
        }
        finally { lease.Dispose(); }
        await session.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(held.All(value => value == 0));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task SendDisconnectDisposeRace_DoesNotAccessDisposedCancellationSource()
    {
        var stream = new TrackingSslStream();
        var session = new RdpSession(stream, DecodedImage.New(PixelFormat.RgbA32, 2, 2), 2, 2, Task.CompletedTask);
        Task[] senders = Enumerable.Range(0, 300).Select(index => Task.Run(async () =>
            await session.SendInputAsync(new(RdpInputKind.PointerMove, X: index, Y: index)))).ToArray();
        Task disconnect = session.DisconnectAsync();
        Task dispose = session.DisposeAsync().AsTask();
        await Task.WhenAll(senders.Append(disconnect).Append(dispose)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(RdpSessionState.Closed, session.Status.State);
        Assert.AreEqual(1, stream.DisposeCalls);
    }

    [TestMethod]
    public async Task RealPump_IdleAttachAndLeaseReturnPublishWithoutNetworkOrInputSentinels()
    {
        var stream = new IdleSslStream();
        var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        var session = new RdpSession(stream, image, 2, 2, Task.CompletedTask);
        using var published = new SemaphoreSlim(0);
        session.FrameAvailable += () => published.Release();
        try
        {
            await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0L, session.FrameInfo.Sequence); // No initial native Fill with no view.
            session.SetFrameConsumer(true);
            await published.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1L, session.FrameInfo.Sequence);
            var frames = Frames(session);
            Assert.IsTrue(frames.TryAcquire(out var lease));
            try
            {
                session.SetFrameConsumer(false);
                session.SetFrameConsumer(true);
                await published.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(2L, session.FrameInfo.Sequence);
                session.SetFrameConsumer(false);
                session.SetFrameConsumer(true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!frames.Pending) await Task.Delay(1, timeout.Token);
                Assert.AreEqual(2L, session.FrameInfo.Sequence);
            }
            finally { lease.Dispose(); }
            await published.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3L, session.FrameInfo.Sequence);
            Assert.IsFalse(frames.Pending);
            Assert.AreEqual(1, stream.ReadCalls); // The pending read was never replaced or duplicated.
        }
        finally { await session.DisposeAsync(); }
    }

    private sealed class IdleSslStream() : SslStream(new MemoryStream())
    {
        public int ReadCalls;
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReadCalls);
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static RdpFrameBuffer Frames(RdpSession session)
        => (RdpFrameBuffer)typeof(RdpSession).GetField("_frames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static byte[] Pixels(RdpFrameBuffer frames, string name)
        => (byte[])typeof(RdpFrameBuffer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frames)!;
    private static void AssertNativeReleased(RdpSession session, DecodedImage image)
    {
        // This bridge owns raw `_inner` pointers, not SafeHandles. Its real Dispose destroys the
        // native value then nulls that pointer. Read the pointer value, never invoke disposed FFI.
        Assert.AreEqual(IntPtr.Zero, NativePointer(image));
        object input = typeof(RdpSession).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Assert.AreEqual(IntPtr.Zero, NativePointer(input));
    }
    private static IntPtr NativePointer(object native)
    {
        FieldInfo field = native.GetType().GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsNotNull(field);
        Assert.IsTrue(field.FieldType.IsPointer);
        var read = new DynamicMethod("ReadNativePointer", typeof(IntPtr), [typeof(object)], native.GetType(), true);
        ILGenerator il = read.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, native.GetType());
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ret);
        return ((Func<object, IntPtr>)read.CreateDelegate(typeof(Func<object, IntPtr>)))(native);
    }
    private sealed class TrackingSslStream(bool throwOnDispose = false) : SslStream(new MemoryStream())
    {
        public int DisposeCalls;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            base.Dispose();
            Disposed.TrySetResult();
            return throwOnDispose ? ValueTask.FromException(new IOException("Synthetic stream cleanup failure.")) : ValueTask.CompletedTask;
        }
    }
}
