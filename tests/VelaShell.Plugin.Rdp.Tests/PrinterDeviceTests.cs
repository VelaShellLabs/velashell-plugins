using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class PrinterDeviceTests
{
    [TestMethod]
    public void AnnouncementUsesUnicodeLengthsAndOnlyLegacyFlags()
    {
        using var device = new PrinterDevice(new PrinterMapping("打印机", "Driver", true, true), 12, new FakeBackend());
        Assert.AreEqual(4u, device.DeviceType);
        byte[] dosName = new byte[8];
        Encoding.ASCII.GetBytes(device.Name).CopyTo(dosName, 0);
        Assert.AreSequenceEqual(Convert.FromHexString("50524E3132000000"), dosName);
        byte[] data = device.DeviceData;
        Assert.AreSequenceEqual(Convert.FromHexString("0600000000000000020000000E00000008000000000000000000").AsSpan(), data.AsSpan(0, 26));
        Assert.AreSequenceEqual(Encoding.Unicode.GetBytes("Driver\0打印机\0").AsSpan(), data.AsSpan(26));
    }

    [TestMethod]
    public async Task CreateWriteCloseUsesPrinterResponseLengthsAndCompletesShortWrites()
    {
        var backend = new FakeBackend { WriteLimit = 2 };
        using var device = Make(backend);
        byte[] create = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        Put(create, 28, 0);
        RdpdrIoResult opened = await Request(device, 0, 0, create);
        Assert.AreEqual(0u, opened.Status);
        Assert.HasCount(4, opened.Body);
        uint id = Get(opened.Body);
        Assert.AreEqual("Queue", backend.Selected!.PrinterName);
        byte[] data = [1, 2, 3, 4, 5];
        byte[] write = Write(data);
        write.AsSpan(4, 8).Fill(0xFF); // Reserved Offset must be ignored.
        RdpdrIoResult written = await Request(device, id, 4, write);
        Assert.AreEqual(0u, written.Status);
        Assert.AreSequenceEqual(new byte[] { 5, 0, 0, 0, 0 }, written.Body);
        FakeJob job = backend.Jobs.Single();
        Assert.AreSequenceEqual(data, job.Data.ToArray());
        Assert.AreEqual(3, job.WriteCalls);
        RdpdrIoResult closed = await Request(device, id, 2, new byte[32]);
        Assert.AreEqual(0u, closed.Status);
        Assert.AreSequenceEqual(new byte[4], closed.Body);
        Assert.AreEqual(1, job.Completions);
        Assert.AreEqual(0, job.Aborts);
        Assert.AreEqual(1, job.Disposals);
        RdpdrIoResult reused = await Request(device, 0, 0, new byte[32]);
        Assert.AreEqual(id, Get(reused.Body));
    }

    [TestMethod]
    public async Task PathBearingCreateAndUnsupportedIoNeverReachBackend()
    {
        var backend = new FakeBackend();
        using var device = Make(backend);
        byte[] create = new byte[32];
        Put(create, 28, 2);
        RdpdrIoResult path = await Request(device, 0, 0, create);
        RdpdrIoResult truncated = await Request(device, 0, 0, new byte[31]);
        RdpdrIoResult ioctl = await Request(device, 0, 14, new byte[32]);
        RdpdrIoResult read = await Request(device, 0, 3, new byte[32]);
        Assert.AreEqual(0xC000000Du, path.Status);
        Assert.AreEqual(0xC000000Du, truncated.Status);
        Assert.AreEqual(0xC00000BBu, ioctl.Status);
        Assert.AreEqual(0xC00000BBu, read.Status);
        Assert.IsEmpty(backend.Jobs);
    }

    [TestMethod]
    public async Task MultipleJobsAreIsolatedAndBounded()
    {
        var backend = new FakeBackend();
        using var device = Make(backend);
        RdpdrIoResult[] opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Request(device, 0, 0, new byte[32])));
        uint[] ids = opened.Select(result => Get(result.Body)).ToArray();
        Assert.AreEqual(4, ids.Distinct().Count());
        RdpdrIoResult rejected = await Request(device, 0, 0, new byte[32]);
        Assert.AreEqual(0xC000009Au, rejected.Status);
        Assert.HasCount(4, backend.Jobs);
        RdpdrIoResult[] writes = await Task.WhenAll(ids.Select(id => Request(device, id, 4, Write([(byte)id]))));
        Assert.IsTrue(writes.All(result => result.Status == 0));
        Assert.IsTrue(backend.Jobs.All(job => job.Data.Count == 1));
        device.Reset();
        await device.DrainAsync();
        Assert.IsTrue(backend.Jobs.All(job => job.Aborts == 1 && job.Disposals == 1 && job.Completions == 0));
        RdpdrIoResult stale = await Request(device, ids[0], 4, Write([1]));
        Assert.AreEqual(0xC0000008u, stale.Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WriteFailureOrZeroProgressAbortsAndReleases(bool throws)
    {
        var backend = new FakeBackend { WriteLimit = throws ? 2 : 0, FailWrite = throws };
        using var device = Make(backend);
        uint id = await Open(device);
        RdpdrIoResult result = await Request(device, id, 4, Write([1, 2, 3]));
        Assert.AreEqual(0xC0000185u, result.Status);
        Assert.AreEqual(0u, Get(result.Body));
        Assert.AreEqual(1, backend.Jobs[0].Aborts);
        Assert.AreEqual(1, backend.Jobs[0].Disposals);
        RdpdrIoResult closed = await Request(device, id, 2, new byte[32]);
        Assert.AreEqual(0xC0000008u, closed.Status);
    }

    [TestMethod]
    public async Task EndFailureAbortsInsteadOfAcknowledgingCompletion()
    {
        var backend = new FakeBackend { FailComplete = true };
        using var device = Make(backend);
        uint id = await Open(device);
        RdpdrIoResult result = await Request(device, id, 2, new byte[32]);
        Assert.AreEqual(0xC0000185u, result.Status);
        Assert.AreEqual(1, backend.Jobs[0].Aborts);
        Assert.AreEqual(1, backend.Jobs[0].Disposals);
    }

    [TestMethod]
    public async Task MalformedWriteAndJobLimitAbortBeforeAdditionalNativeWrites()
    {
        var backend = new FakeBackend { Capture = false };
        using var device = Make(backend);
        uint id = await Open(device);
        byte[] block = Write(new byte[PrinterDevice.MaxWriteLength]);
        for (int index = 0; index < PrinterDevice.MaxJobLength / PrinterDevice.MaxWriteLength; index++)
        {
            RdpdrIoResult result = await Request(device, id, 4, block);
            Assert.AreEqual(0u, result.Status);
        }
        int calls = backend.Jobs[0].WriteCalls;
        RdpdrIoResult exceeded = await Request(device, id, 4, Write([1]));
        Assert.AreEqual(0xC000007Fu, exceeded.Status);
        Assert.AreEqual(calls, backend.Jobs[0].WriteCalls);
        Assert.AreEqual(1, backend.Jobs[0].Aborts);
        uint second = await Open(device);
        byte[] malformed = Write([1]);
        Put(malformed, 0, 2);
        RdpdrIoResult bad = await Request(device, second, 4, malformed);
        Assert.AreEqual(0xC000000Du, bad.Status);
        Assert.AreEqual(0, backend.Jobs[1].WriteCalls);
        Assert.AreEqual(1, backend.Jobs[1].Aborts);
        uint third = await Open(device);
        RdpdrIoResult oversized = await Request(device, third, 4, Write(new byte[PrinterDevice.MaxWriteLength + 1]));
        Assert.AreEqual(0xC000000Du, oversized.Status);
        Assert.AreEqual(0, backend.Jobs[2].WriteCalls);
    }

    [TestMethod]
    public async Task CancellationAfterStartCleansNewJobWithoutAbortingAnotherFileId()
    {
        var backend = new FakeBackend();
        using var device = Make(backend);
        uint original = await Open(device);
        using var cancellation = new CancellationTokenSource();
        backend.OnStart = cancellation.Cancel;
        RdpdrIoResult result = await device.ProcessAsync(new(original, 0, 0, new byte[32]), cancellation.Token);
        Assert.AreEqual(0xC0000120u, result.Status);
        Assert.AreEqual(0, backend.Jobs[0].Aborts);
        Assert.AreEqual(1, backend.Jobs[1].Aborts);
        Assert.AreEqual(1, backend.Jobs[1].Disposals);
    }

    [TestMethod]
    public async Task CancelledWriteAbortsAndCleanupContinuesWhenAbortFails()
    {
        var backend = new FakeBackend { FailAbort = true };
        using var device = Make(backend);
        uint id = await Open(device);
        _ = await Open(device);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        RdpdrIoResult result = await device.ProcessAsync(new(id, 4, 0, Write([1])), cancellation.Token);
        Assert.AreEqual(0xC0000120u, result.Status);
        device.Dispose();
        device.Dispose();
        await device.DrainAsync();
        Assert.IsTrue(backend.Jobs.All(job => job.Aborts == 1 && job.Disposals == 1));
        RdpdrIoResult afterDispose = await Request(device, 0, 0, new byte[32]);
        Assert.AreEqual(0xC0000120u, afterDispose.Status);
    }

    [TestMethod]
    public async Task ResetDoesNotBlockAndPreventsReopeningUntilOldJobsAreReleased()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend { BeforeWrite = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        await using var device = Make(backend);
        uint id = await Open(device);
        Task<RdpdrIoResult> writing = Request(device, id, 4, Write([1]));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Task reset = Task.Run(device.Reset);
            await reset.WaitAsync(TimeSpan.FromSeconds(2));
            Task<RdpdrIoResult> reopened = Request(device, 0, 0, new byte[32]);
            Assert.IsFalse(reopened.IsCompleted);
            release.Set();
            RdpdrIoResult cancelled = await writing;
            RdpdrIoResult fresh = await reopened;
            Assert.AreEqual(0xC0000120u, cancelled.Status);
            Assert.AreEqual(0u, fresh.Status);
            Assert.AreEqual(1, backend.Jobs[0].Aborts);
            Assert.AreEqual(1, backend.Jobs[0].Disposals);
            Assert.AreEqual(0, backend.Jobs[1].Aborts);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task ConcurrentWritesKeepArrivalOrderWithinTheRawStream()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend { BeforeWrite = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        await using var device = Make(backend);
        uint id = await Open(device);
        var pending = new List<Task<RdpdrIoResult>> { Request(device, id, 4, Write([0])) };
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            for (byte i = 1; i < 32; i++) { pending.Add(Request(device, id, 4, Write([i]))); }
        }
        finally { release.Set(); }
        RdpdrIoResult[] results = await Task.WhenAll(pending);
        Assert.IsTrue(results.All(result => result.Status == 0));
        Assert.AreSequenceEqual(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), backend.Jobs[0].Data.ToArray());
    }

    [TestMethod]
    public async Task ChannelAnnouncesPrinterDosNameAndLegacyCapability()
    {
        var sent = new List<byte[]>();
        await using var device = Make(new FakeBackend());
        var channel = new RdpdrChannel(data => { sent.Add(data); return Task.CompletedTask; }, [], additionalDevices: [device]);
        byte[] announce = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(announce, 0x4472);
        BinaryPrimitives.WriteUInt16LittleEndian(announce.AsSpan(2), 0x496E);
        BinaryPrimitives.WriteUInt16LittleEndian(announce.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(announce.AsSpan(6), 12);
        await channel.ProcessAsync(announce);
        await channel.ProcessAsync(Convert.FromHexString("72445053"));
        await channel.ProcessAsync(Convert.FromHexString("72444C55"));
        byte[] frame = sent.Single(data => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) == 0x4441);
        Assert.AreEqual(1u, Get(frame[4..]));
        Assert.AreEqual(4u, Get(frame[8..]));
        Assert.AreSequenceEqual(Convert.FromHexString("50524E3100000000").AsSpan(), frame.AsSpan(16, 8));
        Assert.AreEqual((uint)device.DeviceData.Length, Get(frame[24..]));
        Assert.AreSequenceEqual(device.DeviceData.AsSpan(), frame.AsSpan(28));
        byte[] caps = sent.Single(data => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) == 0x4350);
        Assert.IsTrue(caps.AsSpan().IndexOf(Convert.FromHexString("0200080001000000")) >= 0);
        channel.CloseAll();
        await channel.DrainDevicesAsync();
    }

    [TestMethod]
    public void MappingValidationRejectsAmbiguousOrMalformedAnnouncements()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PrinterMapping("x\0y", "driver"));
        Assert.ThrowsExactly<ArgumentException>(() => new PrinterMapping("queue", ""));
        Assert.ThrowsExactly<ArgumentException>(() => PrinterMapping.Validate([new("queue", "driver"), new("QUEUE", "driver")]));
        Assert.ThrowsExactly<ArgumentException>(() => PrinterMapping.Validate([new("a", "d", true), new("b", "d", true)]));
        Assert.ThrowsExactly<ArgumentException>(() => PrinterMapping.Validate(Enumerable.Range(0, 17).Select(i => new PrinterMapping($"q{i}", "d")).ToArray()));
    }

    [TestMethod]
    public void CatalogQueriesMetadataWithoutStartingPrintJobs()
    {
        if (!OperatingSystem.IsWindows()) { Assert.IsEmpty(WindowsPrinterCatalog.GetPrinters()); return; }
        IReadOnlyList<PrinterMapping> printers = WindowsPrinterCatalog.GetPrinters();
        Assert.IsTrue(printers.All(printer => !string.IsNullOrWhiteSpace(printer.PrinterName) && !string.IsNullOrWhiteSpace(printer.DriverName)));
    }

    [TestMethod]
    public void CatalogExcludesInteractiveAndTerminalSessionQueues()
    {
        Assert.IsFalse(new PrinterNative.QueueInfo("q", "d", "FILE:", 0).IsSupported);
        Assert.IsFalse(new PrinterNative.QueueInfo("q", "d", "USB001, PORTPROMPT:", 0).IsSupported);
        Assert.IsFalse(new PrinterNative.QueueInfo("q", "d", "TS001", 0x8000).IsSupported);
        Assert.IsTrue(new PrinterNative.QueueInfo("q", "d", "USB001", 0).IsSupported);
        Assert.IsTrue(new PrinterNative.QueueInfo("q", "d", "IP_192.0.2.1", 0x10).IsNetwork);
    }

    private static PrinterDevice Make(FakeBackend backend) => new(new("Queue", "Driver"), 1, backend);
    private static Task<RdpdrIoResult> Request(PrinterDevice device, uint id, uint major, byte[] body) => device.ProcessAsync(new(id, major, 0, body), CancellationToken.None);
    private static async Task<uint> Open(PrinterDevice device)
    {
        RdpdrIoResult result = await Request(device, 0, 0, new byte[32]);
        Assert.AreEqual(0u, result.Status);
        return Get(result.Body);
    }
    private static byte[] Write(byte[] data)
    {
        byte[] body = new byte[32 + data.Length];
        Put(body, 0, (uint)data.Length);
        data.CopyTo(body, 32);
        return body;
    }
    private static uint Get(byte[] data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    private sealed class FakeBackend : IPrinterBackend
    {
        internal List<FakeJob> Jobs { get; } = [];
        internal PrinterMapping? Selected;
        internal int WriteLimit = int.MaxValue;
        internal bool FailWrite, FailComplete, FailAbort, Capture = true;
        internal Action? OnStart;
        internal Action? BeforeWrite;
        public IPrinterJob Start(PrinterMapping printer)
        {
            Selected = printer;
            var job = new FakeJob(this);
            Jobs.Add(job);
            OnStart?.Invoke();
            return job;
        }
    }
    private sealed class FakeJob(FakeBackend backend) : IPrinterJob
    {
        internal List<byte> Data { get; } = [];
        internal int WriteCalls, Completions, Aborts, Disposals;
        public int Write(ReadOnlySpan<byte> data)
        {
            WriteCalls++;
            backend.BeforeWrite?.Invoke();
            if (backend.FailWrite && WriteCalls > 1) { throw new IOException("write failed"); }
            int count = Math.Min(backend.WriteLimit, data.Length);
            if (backend.Capture) { Data.AddRange(data[..count].ToArray()); }
            return count;
        }
        public void Complete()
        {
            if (backend.FailComplete) { throw new IOException("end failed"); }
            Completions++;
        }
        public void Abort()
        {
            Aborts++;
            if (backend.FailAbort) { throw new IOException("abort failed"); }
        }
        public void Dispose() => Disposals++;
    }
}
