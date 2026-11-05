using System.Buffers.Binary;
using System.ComponentModel;
using System.Threading.Channels;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class WpdHotplugTests
{
    private sealed class Notifications : IWpdNotifications, IDisposable
    {
        internal Action<WpdDeviceChange>? Changed;
        internal bool Disposed;
        internal bool Fail;
        public IDisposable Register(Action<WpdDeviceChange> changed)
        { if (Fail) { throw new Win32Exception(5); } Changed = changed; return this; }
        public void Dispose() => Disposed = true;
        internal void Fire(string path, bool removed = false) => Changed!(new(path, removed));
    }
    private sealed class Handle : IWpdDeviceHandle
    {
        internal bool Disposed;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WpdIoResult> ExecuteAsync(WpdIoRequest request, CancellationToken token)
        { _ = Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(0, 0, []); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static WpdLocalDevice Device(string path, string? identity = "serial-A") => new(path, "same friendly name", [1, 0, 0, 0], [], identity);
    private static byte[] Packet(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }
    private static async Task<byte[]> Next(Channel<byte[]> output) => await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    [TestMethod]
    public async Task IdleRemovalReinsertionAndPendingIoUseFreshIdsWithoutExpandingAuthorization()
    {
        WpdLocalDevice[] devices = [Device("old-path")];
        var notifications = new Notifications();
        var handles = new List<Handle>();
        string? openedPath = null;
        var output = Channel.CreateUnbounded<byte[]>();
        using var enumerated = new SemaphoreSlim(0);
        async Task Barrier()
        {
            notifications.Fire("barrier");
            Assert.IsTrue(await enumerated.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await using var backend = new WpdDeviceMonitor(["old-path"], () => { WpdLocalDevice[] snapshot = Volatile.Read(ref devices); _ = enumerated.Release(); return snapshot; },
            (device, _) => { openedPath = device.Path; var handle = new Handle(); handles.Add(handle); return handle; }, notifications, _ => { });
        await using var owner = new WpdRedirection(backend);
        IDynamicChannelHandler control = owner.CreateControl(bytes => { _ = output.Writer.TryWrite(bytes); return Task.CompletedTask; }, () => Task.CompletedTask);
        await control.OnMessageAsync(Packet(20, 0x65, 1, 5, 1)); _ = await Next(output);
        await control.OnMessageAsync(Packet(8, 0x67)); _ = await Next(output);
        owner.StartMonitoring();
        await enumerated.WaitAsync();
        Volatile.Write(ref devices, []);
        notifications.Fire("old-path", true);
        byte[] removal = await Next(output);
        Assert.AreSequenceEqual(Packet(12, 0x68, 1), removal);
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        notifications.Fire("old-path", true);
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        await Barrier();
        Assert.IsFalse(output.Reader.TryRead(out _));
        Volatile.Write(ref devices, [Device("unselected", "serial-B"), Device("ambiguous", null)]);
        notifications.Fire("unselected");
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        await Barrier();
        Assert.IsFalse(output.Reader.TryRead(out _));
        Volatile.Write(ref devices, [Device("duplicate-a"), Device("duplicate-b")]);
        notifications.Fire("duplicate-a");
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        await Barrier();
        Assert.IsEmpty(backend.Devices);
        Assert.IsFalse(output.Reader.TryRead(out _));
        Volatile.Write(ref devices, [Device("new-path"), Device("unselected", "serial-B")]);
        notifications.Fire("new-path");
        byte[] addition = await Next(output);
        uint newId = BinaryPrimitives.ReadUInt32LittleEndian(addition.AsSpan(12));
        Assert.AreEqual(2u, newId);
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        notifications.Fire("new-path");
        _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
        await Barrier();
        Assert.IsFalse(output.Reader.TryRead(out _));
        Assert.AreEqual(1, backend.Devices.Count);
        _ = Assert.Throws<Win32Exception>(() => backend.Open(1, new(0x80000000, 3, 3, 0x80)));
        WpdFileChannel file = owner.CreateFile(_ => Task.CompletedTask, () => Task.CompletedTask);
        await file.OnMessageAsync(Packet(1, 4, newId, 0x80000000, 3, 3, 0x80));
        Assert.AreEqual("new-path", openedPath);
        await file.OnMessageAsync(Packet(2, 0, 4, 0, 0));
        await handles.Single().Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref devices, []);
        notifications.Fire("new-path", true);
        removal = await Next(output);
        Assert.AreSequenceEqual(Packet(12, 0x68, newId), removal);
        Assert.IsTrue(handles.Single().Disposed);
        await owner.DisposeAsync();
        Assert.IsTrue(notifications.Disposed);
        notifications.Fire("new-path");
        Assert.IsEmpty(backend.Devices);
    }

    [TestMethod]
    public async Task RemovalRacingCreateClosesTheNewlyOpenedHandle()
    {
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Handle();
        Task? removal = null;
        WpdRedirection? owner = null;
        await using var backend = new WpdDeviceMonitor(["selected"], () => [Device("selected")], (_, _) =>
        {
            removal = Task.Run(() => owner!.RemoveDeviceAsync(1));
            closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return handle;
        }, new Notifications(), _ => { });
        await using (owner = new WpdRedirection(backend))
        {
            IDynamicChannelHandler control = owner.CreateControl(_ => Task.CompletedTask, () => Task.CompletedTask);
            await control.OnMessageAsync(Packet(20, 0x65, 1, 5, 1));
            await control.OnMessageAsync(Packet(8, 0x67));
            WpdFileChannel file = owner.CreateFile(_ => Task.CompletedTask, () => { _ = closeStarted.TrySetResult(); return Task.CompletedTask; });
            await file.OnMessageAsync(Packet(1, 4, 1, 0x80000000, 3, 3, 0x80));
            Assert.IsNotNull(removal);
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(handle.Disposed);
        }
    }

    [TestMethod]
    public async Task RegistrationPrecedesSnapshotAndSessionCloseCancelsActiveIo()
    {
        var notifications = new Notifications();
        var handle = new Handle();
        int enumerations = 0;
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var backend = new WpdDeviceMonitor(["selected"], () =>
        {
            Assert.IsNotNull(notifications.Changed);
            if (Interlocked.Increment(ref enumerations) == 1)
            {
                notifications.Fire("selected", true);
                return new[] { Device("selected") };
            }
            return [];
        }, (_, _) => handle, notifications, _ => { }))
        {
            backend.Start(deviceId => { _ = removed.TrySetResult(); return Task.CompletedTask; }, device => Task.CompletedTask);
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsEmpty(backend.Devices);
        }
        Assert.IsTrue(notifications.Disposed);
        notifications = new Notifications();
        await using var activeBackend = new WpdDeviceMonitor(["selected"], () => { _ = Interlocked.Increment(ref enumerations); return [Device("selected")]; }, (_, _) => handle, notifications, _ => { });
        await using var owner = new WpdRedirection(activeBackend);
        IDynamicChannelHandler control = owner.CreateControl(_ => Task.CompletedTask, () => Task.CompletedTask);
        await control.OnMessageAsync(Packet(20, 0x65, 1, 5, 1));
        await control.OnMessageAsync(Packet(8, 0x67));
        owner.StartMonitoring();
        WpdFileChannel file = owner.CreateFile(_ => Task.CompletedTask, () => Task.CompletedTask);
        await file.OnMessageAsync(Packet(1, 4, 1, 0x80000000, 3, 3, 0x80));
        await file.OnMessageAsync(Packet(2, 0, 4, 0, 0));
        await handle.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.DisposeAsync();
        Assert.IsTrue(handle.Disposed);
        Assert.IsTrue(notifications.Disposed);
        int before = enumerations;
        notifications.Fire("selected");
        Assert.AreEqual(before, enumerations);
        _ = Assert.Throws<InvalidOperationException>(() => owner.CreateFile(_ => Task.CompletedTask, () => Task.CompletedTask));
    }

    [TestMethod]
    public async Task AmbiguousIdentityCannotRestoreAndRegistrationFailureReportsDegradedMode()
    {
        WpdLocalDevice[] devices = [Device("selected", null)];
        var notifications = new Notifications();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int additions = 0;
        using var enumerated = new SemaphoreSlim(0);
        await using (var backend = new WpdDeviceMonitor(["selected"], () => { WpdLocalDevice[] snapshot = Volatile.Read(ref devices); _ = enumerated.Release(); return snapshot; },
            (_, _) => new Handle(), notifications, _ => { }))
        {
            backend.Start(deviceId => { _ = removed.TrySetResult(); return Task.CompletedTask; }, device => { _ = Interlocked.Increment(ref additions); return Task.CompletedTask; });
            await enumerated.WaitAsync();
            Volatile.Write(ref devices, []); notifications.Fire("selected", true);
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await enumerated.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref devices, [Device("selected", null)]); notifications.Fire("selected");
            Assert.IsTrue(await enumerated.WaitAsync(TimeSpan.FromSeconds(5)));
            notifications.Fire("barrier");
            Assert.IsTrue(await enumerated.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsEmpty(backend.Devices);
        }
        Assert.AreEqual(0, additions);
        var logs = new List<string>();
        await using var degraded = new WpdDeviceMonitor(["selected"], () => devices, (_, _) => new Handle(), new Notifications { Fail = true }, logs.Add);
        Assert.AreEqual(1, degraded.Devices.Count);
        Assert.IsTrue(logs.Any(log => log.Contains("idle removal and automatic reinsertion are disabled", StringComparison.Ordinal)));
    }
}
