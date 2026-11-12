using System.Buffers.Binary;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class PortDeviceTests
{
    [TestMethod]
    [DataRow("\\\\.\\COM1")]
    [DataRow("COM0")]
    [DataRow("COM01")]
    [DataRow("COM1:")]
    [DataRow("COM１００")]
    [DataRow("LPT10000")]
    public void RejectsDevicePathsAndAliases(string name) => Assert.Throws<ArgumentException>(() => new PortMapping(name));

    [TestMethod]
    public void SettingsAreExplicitAndRegisterCompanionChannel()
    {
        Assert.IsEmpty(RdpSettings.From(RdpSettingsTests.Request(), new("en"), []).Ports);
        var settings = RdpSettings.From(RdpSettingsTests.Request(("ports", "com3;LPT1")), new("en"), [], windowsDevices: true);
        Assert.AreEqual("COM3", settings.Ports[0].Name);
        var options = new RdpClientOptions { Host = "unused", Ports = settings.Ports, DynamicResolution = false };
        Assert.HasCount(2, RdpManagedSession.BuildChannels(options));
        _ = Assert.Throws<ArgumentException>(() => new RdpClientOptions { Host = "unused", Ports = [new("COM1"), new("com1")] });
    }

    [TestMethod]
    public async Task PortAnnounceHasNoDeviceDataAndSerialCapability()
    {
        var sent = new List<byte[]>();
        var channel = new RdpdrChannel(p => { sent.Add(p); return Task.CompletedTask; }, [], additionalDevices: [new PortDevice(new("COM3")), new PortDevice(new("LPT1"))]);
        await channel.ProcessAsync([0x72, 0x44, 0x50, 0x53]); // server capabilities
        Assert.AreEqual((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(sent[0].AsSpan(4)));
        await channel.ProcessAsync([0x72, 0x44, 0x4C, 0x55]); // user logged on
        byte[] announce = sent[1];
        Assert.AreEqual(2u, U32(announce, 4));
        Assert.AreEqual(1u, U32(announce, 8));
        Assert.AreEqual(0u, U32(announce, 24));
        Assert.AreEqual(2u, U32(announce, 28));
        Assert.AreEqual(0u, U32(announce, 44));
        channel.CloseAll();
    }

    [TestMethod]
    public async Task CreateReadShortWriteControlAndClose()
    {
        var fake = new FakePort();
        using var device = new PortDevice(new("COM3"), _ => fake);
        RdpdrIoResult created = await device.ProcessAsync(new(0, 0, 0, CreateBody()), default);
        uint id = U32(created.Body);
        Assert.AreEqual(0u, created.Status);
        Assert.HasCount(5, created.Body);
        RdpdrIoResult read = await device.ProcessAsync(new(id, 3, 0, Transfer(5)), default);
        CollectionAssert.AreEqual(new byte[] { 2, 0, 0, 0, 7, 8 }, read.Body);
        RdpdrIoResult written = await device.ProcessAsync(new(id, 4, 0, Transfer(3, [1, 2, 3])), default);
        Assert.AreEqual(2u, U32(written.Body));
        RdpdrIoResult baud = await device.ProcessAsync(new(id, 14, 0, Control(PortControl.GetBaud, [], 4)), default);
        Assert.AreEqual(4u, U32(baud.Body));
        RdpdrIoResult closed = await device.ProcessAsync(new(id, 2, 0, new byte[32]), default);
        Assert.AreEqual(0u, closed.Status);
        Assert.IsTrue(fake.Disposed);
        RdpdrIoResult stale = await device.ProcessAsync(new(id, 3, 0, Transfer(1)), default);
        Assert.AreEqual(0xC0000008u, stale.Status);
    }

    [TestMethod]
    public async Task InvalidRequestsNeverReachBackend()
    {
        var fake = new FakePort();
        using var device = new PortDevice(new("COM1"), _ => fake);
        RdpdrIoResult opened = await device.ProcessAsync(new(0, 0, 0, CreateBody()), default);
        uint id = U32(opened.Body);
        foreach (byte[] body in new[] { Transfer(PortDevice.MaxTransfer + 1), new byte[4] })
        {
            RdpdrIoResult result = await device.ProcessAsync(new(id, 3, 0, body), default);
            Assert.AreEqual(NtStatus.InvalidParameter, result.Status);
        }
        RdpdrIoResult unknown = await device.ProcessAsync(new(id, 14, 0, Control((PortControl)0xDEADBEEF, [], 0)), default);
        Assert.AreEqual(NtStatus.NotSupported, unknown.Status);
        RdpdrIoResult malformed = await device.ProcessAsync(new(id, 14, 0, Control(PortControl.SetBaud, [0, 0, 0, 0], 0)), default);
        Assert.AreEqual(NtStatus.InvalidParameter, malformed.Status);
        Assert.AreEqual(0, fake.Calls);
    }

    [TestMethod]
    public async Task LineWaitsOutliveTheDeadlineWhileControlsTimeOutAndCloseCancels()
    {
        var fake = new FakePort { Block = true };
        using var device = new PortDevice(new("COM1"), _ => fake, TimeSpan.FromMilliseconds(100));
        RdpdrIoResult opened = await device.ProcessAsync(new(0, 0, 0, CreateBody()), default);
        uint id = U32(opened.Body);
        // A read and WaitCommEvent on a quiet line pend like on a local port: no client-imposed timeout.
        Task<RdpdrIoResult> read = device.ProcessAsync(new(id, 3, 0, Transfer(1)), default);
        Task<RdpdrIoResult> wait = device.ProcessAsync(new(id, 14, 0, Control(PortControl.WaitOnMask, [], 4)), default);
        RdpdrIoResult control = await device.ProcessAsync(new(id, 14, 0, Control(PortControl.GetBaud, [], 4)), default);
        Assert.AreEqual(0u, control.Status);
        await Task.Delay(400);
        Assert.IsFalse(read.IsCompleted);
        Assert.IsFalse(wait.IsCompleted);
        // A configuration control that hangs in the driver still reports a timeout.
        fake.BlockControl = true;
        RdpdrIoResult timeout = await device.ProcessAsync(new(id, 14, 0, Control(PortControl.GetLine, [], 3)), default)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0xC00000B5u, timeout.Status);
        _ = await device.ProcessAsync(new(id, 2, 0, new byte[32]), default);
        RdpdrIoResult cancelled = await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0xC0000120u, cancelled.Status);
        RdpdrIoResult waitCancelled = await wait.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0xC0000120u, waitCancelled.Status);
    }

    [TestMethod]
    public void ControlCodesMatchTheWindowsHeaders()
    {
        // ntddser.h: IOCTL_SERIAL_GET_CHARS = function 22, IOCTL_SERIAL_SET_CHARS = function 23 (SERIAL_CHARS is 6 bytes).
        Assert.AreEqual((PortControl.GetChars, 6), PortControls.Validate(true, 0x1B0058, [], 6));
        Assert.AreEqual((PortControl.SetChars, 0), PortControls.Validate(true, 0x1B005C, new byte[6], 0));
        Assert.AreEqual(NtStatus.InvalidParameter, Assert.Throws<DriveStatusException>(() => PortControls.Validate(true, 0x1B0058, new byte[6], 0)).Status);
        // ntddpar.h: PARALLEL_INIT = 0x01; 0x10 is the PARALLEL_POWER_OFF status bit, not a command.
        Assert.AreEqual((PortControl.ParallelSetInformation, 0), PortControls.Validate(false, 0x160008, [0x01], 0));
        Assert.AreEqual(NtStatus.InvalidParameter, Assert.Throws<DriveStatusException>(() => PortControls.Validate(false, 0x160008, [0x10], 0)).Status);
    }

    private static uint U32(byte[] bytes, int offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static byte[] CreateBody() { byte[] body = new byte[32]; body[20] = 1; return body; }
    private static byte[] Transfer(int size, byte[]? data = null)
    {
        byte[] body = new byte[32 + (data?.Length ?? 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)size);
        data?.CopyTo(body, 32);
        return body;
    }
    private static byte[] Control(PortControl control, byte[] input, uint capacity)
    {
        byte[] body = new byte[32 + input.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)input.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)control);
        input.CopyTo(body, 32);
        return body;
    }
    private sealed class FakePort : IRedirectedPort
    {
        private readonly CancellationTokenSource _closed = new();
        public bool Block, BlockControl, Disposed;
        public int Calls;
        public async Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken)
        {
            Calls++;
            if (Block) { await BlockAsync(cancellationToken); }
            return [7, 8];
        }
        public Task<int> WriteAsync(byte[] data, CancellationToken cancellationToken) { Calls++; return Task.FromResult(2); }
        public async Task<byte[]> ControlAsync(PortControl control, byte[] input, int outputLength, CancellationToken cancellationToken)
        {
            Calls++;
            if ((Block && control == PortControl.WaitOnMask) || BlockControl) { await BlockAsync(cancellationToken); }
            return new byte[outputLength];
        }
        private async Task BlockAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        public void Dispose() { Disposed = true; _closed.Cancel(); }
    }
}
