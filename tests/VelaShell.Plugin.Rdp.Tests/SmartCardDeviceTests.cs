using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class SmartCardDeviceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ContextAndCardHandlesAreOpaqueScopedAndInvalidated()
    {
        var backend = new Backend();
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device);
        Assert.AreNotEqual((ulong)backend.Context, context);
        Assert.AreEqual(0u, Code(await Call(device, 0x1C, Context(context))));
        ulong card = await Connect(device, context, true);
        Assert.AreNotEqual((ulong)backend.Card, card);
        Assert.AreEqual("Reader Ω", backend.Reader);
        ulong other = await Establish(device);
        Assert.AreEqual(SmartCardException.InvalidHandle, Code(await Call(device, 0xBC, Card(other, card, static w => w.Write(0u)))));
        Assert.AreEqual(0u, Code(await Call(device, 0xB4, Card(context, card, static w => { w.Write(2u); w.Write(2u); w.Write(1u); }))));
        Assert.AreEqual(0u, Code(await Call(device, 0xBC, Card(context, card, static w => w.Write(0u)))));
        Assert.AreEqual(0u, Code(await Call(device, 0xC0, Card(context, card, static w => w.Write(0u)))));
        Assert.AreEqual(0u, Code(await Call(device, 0xB8, Card(context, card, static w => w.Write(0u)))));
        Assert.AreEqual(SmartCardException.InvalidHandle, Code(await Call(device, 0xC8, Card(context, card, static w => { w.Write(0u); w.Write(100u); w.Write(32u); }))));
        Assert.AreEqual(0u, Code(await Call(device, 0x18, Context(context))));
        Assert.AreEqual(SmartCardException.InvalidHandle, Code(await Call(device, 0x1C, Context(context))));
        device.Reset();
        Assert.AreEqual(SmartCardException.InvalidHandle, Code(await Call(device, 0x1C, Context(other))));
        Assert.AreEqual(2, backend.Releases);
        Assert.AreEqual(1, backend.Disconnects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReaderEnumerationStatusAndAttributesRespectCharacterAndByteLengths(bool wide)
    {
        using var device = new SmartCardDevice(new Backend());
        ulong context = await Establish(device);
        byte[] query = Context(context, static w => { w.Write(0u); w.Write(0u); w.Write(1u); w.Write(0u); });
        RdpdrIoResult length = await Call(device, wide ? 0x2Cu : 0x28u, query);
        Assert.AreEqual(0u, Code(length));
        Assert.AreEqual((uint)(wide ? 20 : 10), U32(length.Body, 24));
        Assert.AreEqual(0u, U32(length.Body, 28));
        byte[] request = Context(context, static w => { w.Write(0u); w.Write(0u); w.Write(0u); w.Write(11u); });
        RdpdrIoResult readers = await Call(device, wide ? 0x2Cu : 0x28u, request);
        Assert.AreEqual(0u, Code(readers));
        byte[] expected = (wide ? Encoding.Unicode : Encoding.ASCII).GetBytes("Reader Ω\0\0");
        // The backend returns the reader plus two terminators (10 characters).
        Assert.AreEqual((uint)expected.Length, U32(readers.Body, 24));
        Assert.AreSequenceEqual(expected, readers.Body.AsSpan(36, expected.Length).ToArray());
        RdpdrIoResult tooSmall = await Call(device, wide ? 0x2Cu : 0x28u,
            Context(context, static w => { w.Write(0u); w.Write(0u); w.Write(0u); w.Write(1u); }));
        Assert.AreEqual(SmartCardException.InsufficientBuffer, Code(tooSmall));
        RdpdrIoResult groups = await Call(device, wide ? 0x24u : 0x20u, Context(context, static w => { w.Write(0u); w.Write(uint.MaxValue); }));
        Assert.AreEqual(0u, Code(groups));
        ulong card = await Connect(device, context, wide);
        RdpdrIoResult status = await Call(device, wide ? 0xCCu : 0xC8u,
            Card(context, card, static w => { w.Write(0u); w.Write(100u); w.Write(0u); }));
        Assert.AreEqual(0u, Code(status));
        Assert.AreEqual(6u, U32(status.Body, 32));
        Assert.AreEqual(2u, U32(status.Body, 36));
        Assert.AreEqual(2u, U32(status.Body, 72));
        RdpdrIoResult attr = await Call(device, 0xD8, Card(context, card, static w => { w.Write(0x90303u); w.Write(0u); w.Write(0u); }));
        Assert.AreEqual(0u, Code(attr));
        Assert.AreEqual(3u, U32(attr.Body, 24));
        Assert.AreEqual(0u, U32(attr.Body, 28));
    }

    [TestMethod]
    public async Task SetAttributeForwardsOnlyAllowlistedReaderAttribute()
    {
        var backend = new Backend();
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device), card = await Connect(device, context, false);
        byte[] request = Card(context, card, static w => { w.Write(0x7FFF0007u); w.Write(2u); w.Write(0x20000u); }, static w => Blob(w, [1, 0]));
        Assert.AreEqual(0u, Code(await Call(device, 0xDC, request)));
        Assert.AreEqual(0x7FFF0007u, backend.Attribute);
        Assert.AreSequenceEqual(new byte[] { 1, 0 }, backend.AttributeValue!);

        byte[] denied = Card(context, card, static w => { w.Write(0x7FFF0006u); w.Write(1u); w.Write(0x20000u); }, static w => Blob(w, [1]));
        Assert.AreEqual(SmartCardException.Unsupported, Code(await Call(device, 0xDC, denied)));
    }

    [TestMethod]
    public async Task TransmitAndControlRoundTripDeferredBuffersAndPci()
    {
        var backend = new Backend();
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device), card = await Connect(device, context, false);
        byte[] apdu = [0, 0xA4, 4, 0, 1, 0x42];
        byte[] transmit = Card(context, card, w =>
        {
            w.Write(2u); Header(w, 0, false); Header(w, apdu.Length); w.Write(0x30000u); w.Write(0u); w.Write(256u);
        }, w => { Blob(w, apdu); w.Write(2u); Header(w, 0, false); });
        RdpdrIoResult result = await Call(device, 0xD0, transmit);
        Assert.AreEqual(0u, Code(result));
        Assert.AreSequenceEqual(apdu, backend.Apdu!);
        Assert.AreEqual(2u, U32(result.Body, 28));
        Assert.AreEqual(2u, U32(result.Body, 36));
        Assert.AreSequenceEqual(new byte[] { 0x90, 0 }, result.Body.AsSpan(52, 2).ToArray());
        RdpdrIoResult control = await Call(device, 0xD4, Card(context, card, w =>
        {
            w.Write(0x00313520u); Header(w, 3); w.Write(0u); w.Write(16u);
        }, w => Blob(w, [1, 2, 3])));
        Assert.AreEqual(0u, Code(control));
        Assert.AreSequenceEqual(new byte[] { 3, 2, 1 }, control.Body.AsSpan(36, 3).ToArray());
        backend.OversizedResponse = true;
        Assert.AreEqual(SmartCardException.InsufficientBuffer, Code(await Call(device, 0xD0, transmit)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task PendingStatusCanBeCancelledReleasedResetOrDisposed(int action)
    {
        var backend = new Backend { BlockStatus = true };
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device);
        Task<RdpdrIoResult> waiting = Call(device, 0xA4, StatusChange(context, true));
        Assert.IsTrue(backend.Entered.Wait(TimeSpan.FromSeconds(5)));
        switch (action)
        {
            case 0: Assert.AreEqual(0u, Code(await Call(device, 0xA8, Context(context)))); break;
            case 1: Assert.AreEqual(0u, Code(await Call(device, 0x18, Context(context)))); break;
            case 2: device.Reset(); break;
            case 3: device.Dispose(); break;
        }
        RdpdrIoResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SmartCardException.Cancelled, Code(result));
        Assert.AreEqual(action == 0 ? 0 : 1, backend.Releases);
        Assert.IsFalse(backend.ReleasedDuringWait);
    }

    [TestMethod]
    public async Task TransportCancellationUnblocksNativeStatusWait()
    {
        var backend = new Backend { BlockStatus = true };
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device);
        using var cancellation = new CancellationTokenSource();
        Task<RdpdrIoResult> wait = Call(device, 0xA0, StatusChange(context, false), cancellation.Token);
        Assert.IsTrue(backend.Entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        RdpdrIoResult result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SmartCardException.Cancelled, Code(result));
    }

    [TestMethod]
    public async Task EmptyStatusArrayAndNativeReleaseFailureAreReported()
    {
        var backend = new Backend { ReleaseError = SmartCardException.NoService };
        using var device = new SmartCardDevice(backend);
        ulong context = await Establish(device);
        RdpdrIoResult result = await Call(device, 0xA0, Context(context, static w => { w.Write(0u); w.Write(0u); w.Write(0u); }));
        Assert.AreEqual(0u, Code(result));
        Assert.AreEqual(SmartCardException.NoService, Code(await Call(device, 0x18, Context(context))));
    }

    [TestMethod]
    public async Task MalformedLengthsAndHandlesDoNotReachNativeOperations()
    {
        var backend = new Backend();
        using var device = new SmartCardDevice(backend);
        // Golden establish request from the MS-RPCE common/private headers and scope DWORD.
        byte[] golden = Convert.FromHexString("01100800CCCCCCCC08000000000000000200000000000000");
        RdpdrIoResult establish = await Call(device, 0x14, golden);
        Assert.AreEqual(0u, Code(establish));
        Assert.AreEqual(40u, U32(establish.Body, 0));
        Assert.AreSequenceEqual(Convert.FromHexString("01100800CCCCCCCC1800000000000000"), establish.Body.AsSpan(4, 16).ToArray());
        byte[] invalid = (byte[])golden.Clone(); invalid[8] = 16;
        Assert.AreEqual(0xC000000Du, (await Call(device, 0x14, invalid)).Status);
        Assert.AreEqual(0xC000000Du, (await Call(device, 0x14, golden[..^1])).Status);
        byte[] oversized = Ndr(static w => { Header(w, 17); w.Write(17u); w.Write(new byte[17]); });
        Assert.AreEqual(0xC000000Du, (await Call(device, 0x1C, oversized)).Status);
        byte[] inconsistent = Ndr(static w => { Header(w, 8); w.Write(7u); w.Write(123ul); });
        Assert.AreEqual(0xC000000Du, (await Call(device, 0x1C, inconsistent)).Status);
        Assert.AreEqual(SmartCardException.InvalidHandle, Code(await Call(device, 0x1C, Context(123))));
        Assert.AreEqual(0xC000000Du, (await Call(device, 0xDC, Ndr(static w => w.Write(0u)))).Status);
        Assert.AreEqual(0xC0000023u, (await Call(device, 0x14, golden, outputLength: 24)).Status);
        Assert.AreEqual(1, backend.Establishes);
    }

    [TestMethod]
    public void SystemPcscEntryPointsProbeContextAndReadersWithoutOpeningCards()
    {
        var backend = new PcscSmartCardBackend();
        nint context;
        try { context = backend.EstablishContext(2); }
        catch (SmartCardException ex) when (ex.Code is SmartCardException.NoService or 0x8010001E)
        {
            TestContext.WriteLine($"System PC/SC entrypoint reached; service unavailable: 0x{ex.Code:X8}");
            // Without a PC/SC service the device is skipped with a reason; the connection goes ahead without it.
            Assert.IsNull(SmartCardDevice.TryCreate(out string? reason));
            Assert.IsNotNull(reason);
            return;
        }
        catch (DllNotFoundException) { TestContext.WriteLine("System PC/SC library is not installed."); return; }
        try
        {
            backend.IsValidContext(context);
            try { TestContext.WriteLine($"System PC/SC available: {backend.ListReaders(context, null).Length} readers."); }
            catch (SmartCardException ex) when (ex.Code == 0x8010002E) { TestContext.WriteLine("System PC/SC available; no readers."); }
            backend.Cancel(context);
        }
        finally { backend.ReleaseContext(context); }
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static uint Code(RdpdrIoResult result) { Assert.AreEqual(0u, result.Status); return U32(result.Body, 20); }
    private static async Task<ulong> Establish(SmartCardDevice device)
    {
        RdpdrIoResult result = await Call(device, 0x14, Ndr(static w => w.Write(2u)));
        Assert.AreEqual(0u, Code(result));
        return BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(36));
    }
    private static async Task<ulong> Connect(SmartCardDevice device, ulong context, bool wide)
    {
        byte[] input = Ndr(w => { w.Write(0x20000u); Header(w, 8); w.Write(2u); w.Write(2u); Text(w, wide ? "Reader Ω" : "Reader A", wide); Handle(w, context); });
        RdpdrIoResult result = await Call(device, wide ? 0xB0u : 0xACu, input);
        Assert.AreEqual(0u, Code(result));
        Assert.AreEqual(context, BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(48)));
        return BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(60));
    }
    private static byte[] StatusChange(ulong context, bool wide) => Context(context,
        w => { w.Write(uint.MaxValue); w.Write(1u); w.Write(0x30000u); },
        w => { w.Write(1u); w.Write(0x40000u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(new byte[36]); Text(w, "Reader A", wide); });
    private static byte[] Context(ulong context, Action<BinaryWriter>? fields = null, Action<BinaryWriter>? deferred = null) =>
        Ndr(w => { Header(w, 8); fields?.Invoke(w); Handle(w, context); deferred?.Invoke(w); });
    private static byte[] Card(ulong context, ulong card, Action<BinaryWriter> fields, Action<BinaryWriter>? deferred = null) =>
        Ndr(w => { Header(w, 8); Header(w, 8); fields(w); Handle(w, context); Handle(w, card); deferred?.Invoke(w); });
    private static void Align(BinaryWriter w) { while ((w.BaseStream.Position & 3) != 0) { w.Write((byte)0); } }
    private static void Header(BinaryWriter w, int count, bool present = true) { Align(w); w.Write((uint)count); w.Write(present ? 0x20000u : 0); }
    private static void Handle(BinaryWriter w, ulong handle) { Align(w); w.Write(8u); w.Write(handle); }
    private static void Blob(BinaryWriter w, byte[] bytes) { Align(w); w.Write((uint)bytes.Length); w.Write(bytes); Align(w); }
    private static void Text(BinaryWriter w, string text, bool wide)
    {
        Align(w); w.Write((uint)text.Length + 1); w.Write(0u); w.Write((uint)text.Length + 1);
        w.Write((wide ? Encoding.Unicode : Encoding.ASCII).GetBytes(text + '\0')); Align(w);
    }
    private static byte[] Ndr(Action<BinaryWriter> fields)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Convert.FromHexString("01100800CCCCCCCC0000000000000000")); fields(writer);
        while ((stream.Length & 7) != 0) { writer.Write((byte)0); }
        byte[] bytes = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length - 16); return bytes;
    }
    private static Task<RdpdrIoResult> Call(SmartCardDevice device, uint code, byte[] input, CancellationToken token = default, uint outputLength = 4096)
    {
        byte[] body = new byte[32 + input.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, outputLength);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)input.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0x90000 + code); input.CopyTo(body, 32);
        return device.ProcessAsync(new RdpdrIoRequest(0, 0x0E, 0, body), token);
    }

    private sealed class Backend : ISmartCardBackend
    {
        internal nint Context = 0x12345678, Card = 0x23456789;
        internal int Establishes, Releases, Disconnects, Waiting;
        internal bool BlockStatus, OversizedResponse, ReleasedDuringWait;
        internal uint ReleaseError;
        internal string? Reader;
        internal byte[]? Apdu;
        internal uint Attribute;
        internal byte[]? AttributeValue;
        internal readonly ManualResetEventSlim Entered = new(), Cancelled = new();
        public nint EstablishContext(uint scope) { Interlocked.Increment(ref Establishes); return Context; }
        public void ReleaseContext(nint context)
        {
            ReleasedDuringWait |= Volatile.Read(ref Waiting) != 0; Interlocked.Increment(ref Releases);
            if (ReleaseError != 0) { throw new SmartCardException(ReleaseError); }
        }
        public void IsValidContext(nint context) => Assert.AreEqual(Context, context);
        public string[] ListReaders(nint context, string[]? groups) => ["Reader Ω"];
        public string[] ListReaderGroups(nint context) => ["SCard$DefaultReaders"];
        public SmartCardReaderState[] GetStatusChange(nint context, uint timeout, SmartCardReaderState[] readers)
        {
            if (!BlockStatus) { return readers; }
            Interlocked.Increment(ref Waiting); Entered.Set();
            try
            {
                if (Cancelled.Wait((int)timeout)) { throw new SmartCardException(SmartCardException.Cancelled); }
                throw new SmartCardException(SmartCardException.Timeout);
            }
            finally { Interlocked.Decrement(ref Waiting); }
        }
        public void Cancel(nint context) => Cancelled.Set();
        public (nint Card, uint Protocol) Connect(nint context, string reader, uint shareMode, uint protocols) { Reader = reader; return (Card, 2); }
        public uint Reconnect(nint card, uint shareMode, uint protocols, uint initialization) => 2;
        public void Disconnect(nint card, uint disposition) => Interlocked.Increment(ref Disconnects);
        public void BeginTransaction(nint card) { }
        public void EndTransaction(nint card, uint disposition) { }
        public SmartCardStatus Status(nint card) => new(["Reader Ω"], 6, 2, [0x3B, 0]);
        public SmartCardTransmitResult Transmit(nint card, SmartCardPci sendPci, byte[] send, SmartCardPci? receivePci, int capacity, bool lengthOnly)
        {
            Apdu = send;
            return new(receivePci, new([0x90, 0], OversizedResponse ? capacity + 1 : 2));
        }
        public SmartCardBuffer Control(nint card, uint code, byte[] input, int capacity, bool lengthOnly) => new([.. input.Reverse()], input.Length);
        public byte[] GetAttribute(nint card, uint attribute) => [1, 2, 3];
        public void SetAttribute(nint card, uint attribute, byte[] value) { Attribute = attribute; AttributeValue = value; }
    }
}
