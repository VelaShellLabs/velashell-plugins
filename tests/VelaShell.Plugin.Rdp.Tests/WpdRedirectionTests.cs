using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class WpdRedirectionTests
{
    [TestMethod]
    public async Task DynamicListenersRouteFragmentedControlAndIndependentFileInstances()
    {
        var backend = new FakeBackend();
        var output = new Output();
        var dvc = new DrdynvcChannel((bytes, _) => output.Send(bytes), CancellationToken.None);
        new WpdRedirection(backend).Register(dvc);
        await dvc.ProcessAsync([0x10, 1, .. Encoding.ASCII.GetBytes("PNPDR"), 0]);
        _ = await output.Next();
        byte[] version = Control(0x65, 1, 5, 1);
        await dvc.ProcessAsync([0x20, 1, 20, .. version[..8]]);
        Assert.AreEqual(0, output.Count);
        await dvc.ProcessAsync([0x30, 1, .. version[8..]]);
        byte[] reply = await output.Next();
        byte[] expectedVersion = [0x30, 1, .. version];
        Assert.AreSequenceEqual(expectedVersion, reply);
        await dvc.ProcessAsync([0x30, 1, .. Control(0x67)]);
        _ = await output.Next();
        foreach (byte id in new byte[] { 2, 3 })
        {
            await dvc.ProcessAsync([0x10, id, .. Encoding.ASCII.GetBytes("FileRedirectorChannel"), 0]);
            _ = await output.Next();
            await dvc.ProcessAsync([0x30, id, .. Create(id, 7)]);
            _ = await output.Next();
        }
        Assert.HasCount(2, backend.Handles);
        await dvc.ProcessAsync([0x30, 2, .. Request(4, 99)]);
        reply = await output.Next();
        Assert.AreSequenceEqual(new byte[] { 0x40, 2 }, reply);
        Assert.IsTrue(backend.Handles[0].Disposed);
        Assert.IsFalse(backend.Handles[1].Disposed);
        await dvc.CloseAsync();
        Assert.IsTrue(backend.Handles[1].Disposed);
    }

    [TestMethod]
    public async Task VersionAuthenticationAnnouncementAndRemovalUseRealWireLayout()
    {
        var backend = new FakeBackend();
        var owner = new WpdRedirection(backend);
        var output = new Output();
        IDynamicChannelHandler? control = null;
        control = owner.CreateControl(output.Send, () => control!.OnCloseAsync());
        await control.OnOpenAsync();
        Assert.AreEqual(0, output.Count);
        await control.OnMessageAsync(Control(0x65, 1, 5, 1));
        byte[] version = await output.Next();
        Assert.AreSequenceEqual(Control(0x65, 1, 5, 1), version);
        Assert.AreEqual(0, output.Count);
        await control.OnMessageAsync(Control(0x67));
        byte[] add = await output.Next();
        Assert.AreEqual((uint)add.Length, Get(add));
        Assert.AreEqual(0x66u, Get(add, 4));
        Assert.AreEqual(1u, Get(add, 8));
        Assert.AreEqual(7u, Get(add, 12));
        Assert.AreEqual((uint)add.Length - 12, Get(add, 16));
        Assert.AreEqual(16u, Get(add, 20));
        Assert.AreSequenceEqual(WpdPolicy.InterfaceClass.ToByteArray(), add.AsSpan(24, 16).ToArray());
        Assert.AreEqual(0u, Get(add, 40));
        Assert.AreEqual(0u, Get(add, 44));
        Assert.AreEqual(6u, Get(add, 48));
        Assert.AreSequenceEqual(Encoding.Unicode.GetBytes("WPD"), add.AsSpan(52, 6).ToArray());
        Assert.AreEqual(4u, Get(add, 58));
        Assert.AreEqual(0u, Get(add, 62));
        await owner.RemoveDeviceAsync(7);
        byte[] remove = await output.Next();
        Assert.AreSequenceEqual(Control(0x68, 7), remove);
        await control.OnCloseAsync();
    }

    [TestMethod]
    public async Task AuthenticationAndSelectionGateEveryOpen()
    {
        var backend = new FakeBackend();
        var owner = new WpdRedirection(backend);
        var first = new Output();
        WpdFileChannel file = owner.CreateFile(first.Send, static () => Task.CompletedTask);
        await file.OnMessageAsync(Create(1, 7));
        byte[] denied = await first.Next();
        Assert.AreEqual(unchecked((uint)WpdPolicy.HResult(5)), Get(denied, 4));
        Assert.IsEmpty(backend.Handles);
        await Authenticate(owner);
        var second = new Output();
        WpdFileChannel unknown = owner.CreateFile(second.Send, static () => Task.CompletedTask);
        await unknown.OnMessageAsync(Create(2, 999));
        denied = await second.Next();
        Assert.AreEqual(unchecked((uint)WpdPolicy.HResult(5)), Get(denied, 4));
        Assert.IsEmpty(backend.Handles);
        await file.OnCloseAsync();
        await unknown.OnCloseAsync();
    }

    [TestMethod]
    public async Task MultipleFileChannelsIsolateHandlesAndReadWriteOffsets()
    {
        var backend = new FakeBackend();
        var owner = new WpdRedirection(backend);
        await Authenticate(owner);
        var a = new Output(); var b = new Output();
        WpdFileChannel one = owner.CreateFile(a.Send, static () => Task.CompletedTask);
        WpdFileChannel two = owner.CreateFile(b.Send, static () => Task.CompletedTask);
        await one.OnMessageAsync(Create(1, 7)); await two.OnMessageAsync(Create(2, 7));
        _ = await a.Next(); _ = await b.Next();
        byte[] write = Request(0x123456, 1, 3, 0x12345678, 0x9abcdef0);
        write = [.. write, 10, 20, 30, 0xff];
        await one.OnMessageAsync(write);
        byte[] reply = await a.Next();
        Assert.HasCount(12, reply);
        Assert.AreEqual(0x123456u, Get(reply));
        Assert.AreEqual(3u, Get(reply, 8));
        Assert.AreEqual(0x123456789abcdef0ul, backend.Handles[0].Requests.Single().Offset);
        Assert.IsEmpty(backend.Handles[1].Requests);
        await two.OnMessageAsync(Request(4, 0, 2, 0, 9));
        reply = await b.Next();
        Assert.AreSequenceEqual(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 40, 41, 0 }, reply);
        await one.OnCloseAsync();
        Assert.IsTrue(backend.Handles[0].Disposed);
        Assert.IsFalse(backend.Handles[1].Disposed);
        await two.OnCloseAsync();
    }

    [TestMethod]
    public async Task WpdIoctlAllowlistOptionalOutputAndFailureHResultAreEnforced()
    {
        var backend = new FakeBackend();
        var output = new Output();
        var file = new WpdFileChannel(backend, static () => true, static id => id == 7, output.Send, static () => Task.CompletedTask);
        await file.OnMessageAsync(Create(1, 7)); _ = await output.Next();
        byte[] ioctl = [.. Request(2, 2, WpdPolicy.ReadIoctl, 2, 3), 7, 8, 1, 2, 3, 99];
        await file.OnMessageAsync(ioctl);
        byte[] reply = await output.Next();
        Assert.AreEqual(3u, Get(reply, 8));
        Assert.HasCount(16, reply);
        Assert.AreSequenceEqual(new byte[] { 7, 8 }, backend.Handles[0].Requests[0].Input);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, backend.Handles[0].Requests[0].Output);
        await file.OnMessageAsync([.. Request(3, 2, 0x222003, 0, 4), 0]);
        reply = await output.Next();
        Assert.AreEqual(unchecked((uint)WpdPolicy.HResult(5)), Get(reply, 4));
        Assert.HasCount(1, backend.Handles[0].Requests);
        backend.Handles[0].Failure = new Win32Exception(31);
        await file.OnMessageAsync([.. Request(4, 2, WpdPolicy.ReadWriteIoctl, 0, 4), 0]);
        reply = await output.Next();
        Assert.AreEqual(0x8007001fu, Get(reply, 4));
        Assert.AreEqual(0u, Get(reply, 8));
        Assert.HasCount(13, reply);
        await file.OnCloseAsync();
    }

    [TestMethod]
    public async Task SpecificCancelCompletesOnlyOriginalRequestAndCloseDrainsOthers()
    {
        var backend = new FakeBackend();
        var output = new Output();
        var file = new WpdFileChannel(backend, static () => true, static _ => true, output.Send, static () => Task.CompletedTask);
        await file.OnMessageAsync(Create(1, 7)); _ = await output.Next();
        FakeHandle handle = backend.Handles.Single();
        handle.Wait = true;
        await file.OnMessageAsync(Request(0x123456, 0, 1, 0, 0));
        await file.OnMessageAsync(Request(0xabcdef, 0, 1, 0, 0));
        await file.OnMessageAsync(Request(9, 6, 0x12345600));
        byte[] cancelled = await output.Next();
        Assert.AreEqual(0x123456u, Get(cancelled));
        Assert.AreEqual(0x800703e3u, Get(cancelled, 4));
        Assert.AreEqual(1, handle.Cancellations);
        Assert.AreEqual(0, output.Count);
        await file.OnMessageAsync(Request(10, 6, 0x12345600));
        await file.OnCloseAsync();
        Assert.AreEqual(2, handle.Cancellations);
        Assert.IsTrue(handle.Disposed);
        Assert.AreEqual(0, output.Count);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task MalformedUnknownAndDuplicateCreateCloseAndRelease(int kind)
    {
        var backend = new FakeBackend(); var output = new Output();
        int closed = 0;
        WpdFileChannel? file = null;
        file = new(backend, () => true, _ => true, output.Send, async () => { closed++; await file!.OnCloseAsync(); });
        await file.OnMessageAsync(Create(1, 7)); _ = await output.Next();
        byte[] bad = kind switch
        {
            0 => Request(2, 99),
            1 => Request(2, 0, (uint)WpdPolicy.MaxTransfer + 1, 0, 0),
            2 => [.. Request(2, 1, 4, 0, 0), 1, 2, 3, 4],
            3 => [.. Request(2, 2, WpdPolicy.ReadIoctl, 0, 4), 1, 2, 0],
            _ => Create(2, 7)
        };
        await file.OnMessageAsync(bad);
        Assert.AreEqual(1, closed);
        Assert.IsTrue(backend.Handles[0].Disposed);
        Assert.IsEmpty(backend.Handles[0].Requests);
    }

    [TestMethod]
    public async Task CapabilitiesUseSixByteReplyAndDuplicatePendingIdCloses()
    {
        var backend = new FakeBackend(); var output = new Output();
        WpdFileChannel? file = null;
        file = new(backend, () => true, _ => true, output.Send, () => file!.OnCloseAsync());
        await file.OnMessageAsync([.. Request(0x654321, 5), 6, 0]);
        byte[] capabilities = await output.Next();
        Assert.AreSequenceEqual(new byte[] { 0x21, 0x43, 0x65, 0, 4, 0 }, capabilities);
        await file.OnMessageAsync(Create(1, 7)); _ = await output.Next();
        backend.Handles[0].Wait = true;
        await file.OnMessageAsync(Request(2, 0, 1, 0, 0));
        await file.OnMessageAsync(Request(2, 0, 1, 0, 0));
        Assert.IsTrue(backend.Handles[0].Disposed);
        Assert.AreEqual(1, backend.Handles[0].Cancellations);
    }

    [TestMethod]
    public async Task DeviceDisconnectWithdrawsAnnouncementAndClosesItsHandle()
    {
        var backend = new FakeBackend(); var output = new Output(); var controlOutput = new Output();
        var owner = new WpdRedirection(backend);
        IDynamicChannelHandler control = owner.CreateControl(controlOutput.Send, static () => Task.CompletedTask);
        await control.OnMessageAsync(Control(0x65, 1, 5, 1)); _ = await controlOutput.Next();
        await control.OnMessageAsync(Control(0x67)); _ = await controlOutput.Next();
        WpdFileChannel file = owner.CreateFile(output.Send, static () => Task.CompletedTask);
        await file.OnMessageAsync(Create(1, 7)); _ = await output.Next();
        backend.Handles[0].Failure = new Win32Exception(1167);
        await file.OnMessageAsync(Request(2, 0, 1, 0, 0));
        byte[] failure = await output.Next();
        byte[] removal = await controlOutput.Next();
        Assert.AreEqual(unchecked((uint)WpdPolicy.HResult(1167)), Get(failure, 4));
        Assert.AreSequenceEqual(Control(0x68, 7), removal);
        Assert.IsTrue(backend.Handles[0].Disposed);
        WpdFileChannel reopened = owner.CreateFile(output.Send, static () => Task.CompletedTask);
        await reopened.OnMessageAsync(Create(3, 7));
        byte[] denied = await output.Next();
        Assert.AreEqual(0x80070005u, Get(denied, 4));
        Assert.HasCount(1, backend.Handles);
        await control.OnCloseAsync();
    }

    [TestMethod]
    public async Task PendingRequestsAreBoundedAndDangerousOpenFlagsNeverReachBackend()
    {
        var backend = new FakeBackend(); var output = new Output();
        var denied = new WpdFileChannel(backend, static () => true, static _ => true, output.Send, static () => Task.CompletedTask);
        byte[] badOpen = Create(1, 7);
        Put(badOpen, 24, 0x04000000); // DELETE_ON_CLOSE is never allowed for a selected WPD interface.
        await denied.OnMessageAsync(badOpen);
        byte[] response = await output.Next();
        Assert.AreEqual(0x80070005u, Get(response, 4));
        Assert.IsEmpty(backend.Handles);
        var file = new WpdFileChannel(backend, static () => true, static _ => true, output.Send, static () => Task.CompletedTask);
        await file.OnMessageAsync(Create(2, 7)); _ = await output.Next();
        backend.Handles[0].Wait = true;
        for (uint i = 0; i <= WpdPolicy.MaxPending; i++)
        {
            await file.OnMessageAsync(Request(i + 10, 0, 1, 0, 0));
        }

        response = await output.Next();
        Assert.AreEqual(unchecked((uint)WpdPolicy.HResult(170)), Get(response, 4));
        Assert.HasCount(WpdPolicy.MaxPending, backend.Handles[0].Requests);
        await file.OnCloseAsync();
        Assert.IsTrue(backend.Handles[0].Disposed);
        await denied.OnCloseAsync();
    }

    private static async Task Authenticate(WpdRedirection owner)
    {
        IDynamicChannelHandler control = owner.CreateControl(static _ => Task.CompletedTask, static () => Task.CompletedTask);
        await control.OnMessageAsync(Control(0x65, 1, 5, 1));
        await control.OnMessageAsync(Control(0x67));
    }
    private static byte[] Create(uint id, uint device) => Request(id, 4, device, 0xc0000000, 3, 3, 0x40000080);
    private static byte[] Request(uint id, uint function, params uint[] values)
    {
        byte[] bytes = new byte[8 + values.Length * 4];
        Put(bytes, 0, id); Put(bytes, 4, function);
        for (int i = 0; i < values.Length; i++)
        {
            Put(bytes, 8 + i * 4, values[i]);
        }

        return bytes;
    }
    private static byte[] Control(uint id, params uint[] values)
    {
        byte[] bytes = Request(0, id, values); Put(bytes, 0, (uint)bytes.Length); return bytes;
    }
    private static uint Get(byte[] bytes, int offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private sealed class Output
    {
        private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>();
        internal int Count => _messages.Reader.Count;
        internal Task Send(byte[] bytes) { _ = _messages.Writer.TryWrite(bytes); return Task.CompletedTask; }
        internal Task<byte[]> Next() => _messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private sealed class FakeBackend : IWpdDeviceBackend
    {
        public IReadOnlyList<WpdDeviceDescription> Devices { get; } = [new(7, "WPD", [], [])];
        internal List<FakeHandle> Handles { get; } = [];
        public IWpdDeviceHandle Open(uint deviceId, WpdOpenOptions options)
        {
            if (deviceId != 7)
            {
                throw new Win32Exception(5);
            }

            var handle = new FakeHandle(); Handles.Add(handle); return handle;
        }
    }
    private sealed class FakeHandle : IWpdDeviceHandle
    {
        internal List<WpdIoRequest> Requests { get; } = [];
        internal bool Disposed, Wait;
        internal int Cancellations;
        internal Exception? Failure;
        public async Task<WpdIoResult> ExecuteAsync(WpdIoRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (Wait)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { _ = Interlocked.Increment(ref Cancellations); throw; }
            }
            return new(0, request.Count, request.Function == 1 ? [] : [.. Enumerable.Range(40, request.Count).Select(static i => (byte)i)]);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
