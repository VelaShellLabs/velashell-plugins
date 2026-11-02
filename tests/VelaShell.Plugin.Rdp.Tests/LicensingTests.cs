using System.Reflection;
using VelaShell.Rdp;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Security;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class LicensingTests
{
    [TestMethod]
    public void LicensingKeyDerivationMatchesIndependentFixture()
    {
        // Inputs from MS-RDPELE 4.2/4.3; expected keys independently calculated with Python hashlib
        // using 5.1.2 (the second salted-hash round reverses the randoms).
        byte[] premaster = Convert.FromHexString("CF7ADBCBFB0E1523871C8481BA9D4E15BBD256BDD8F7F316CC353BE1934278DD929AE47AE299D473B1AA6F55943BC9BC");
        byte[] clientRandom = Convert.FromHexString("DC73A0C869256B18AF0B947AA9A520AF8BBC0DCCA395B7B9EB815DBE0A109CD8");
        byte[] serverRandom = Convert.FromHexString("84EFAE20B1D59E36491AE82E0A9989AC49A6474F339B5AB99503A6C6C23C3F61");
        (byte[] mac, byte[] encryption) = LicenseEncryption.DeriveKeys(premaster, clientRandom, serverRandom);
        Assert.AreEqual("74CCA2098E38F01D3EDDF0B495C56968", Convert.ToHexString(mac));
        Assert.AreEqual("6E1891432DB6E14CE25985FBA5C6F36D", Convert.ToHexString(encryption));
    }

    [TestMethod]
    public void ValidClientCompletesWithoutAReply()
    {
        using var client = new Licensing("user", "machine");
        Assert.IsNull(client.Process(Alert(7, 2)));
        Assert.IsTrue(client.CanActivate);
    }

    [TestMethod]
    [DataRow(6u, 1u)]
    [DataRow(8u, 1u)]
    [DataRow(3u, 2u)]
    [DataRow(7u, 1u)]
    [DataRow(12u, 4u)]
    public void LicensingFailuresAreNotReportedAsSuccess(uint code, uint transition)
    {
        using var client = new Licensing("user", "machine");
        RdpProtocolException error = Assert.Throws<RdpProtocolException>(() => client.Process(Alert(code, transition)));
        StringAssert.Contains(error.Message, $"0x{code:X8}");
    }

    [TestMethod]
    public void InvalidPreamblesAndTruncatedBlobsAreRejected()
    {
        byte[] valid = Alert(7, 2);
        for (int length = 0; length < valid.Length; length++)
        {
            using var client = new Licensing("user", "machine");
            byte[] truncated = valid[..length];
            Assert.Throws<RdpProtocolException>(() => client.Process(truncated));
        }
        foreach (int offset in new[] { 0, 5, 6, 16, 18 })
        {
            using var client = new Licensing("user", "machine");
            byte[] corrupt = (byte[])valid.Clone();
            corrupt[offset] = 0x7F;
            Assert.Throws<RdpProtocolException>(() => client.Process(corrupt));
        }
    }

    [TestMethod]
    [DataRow((byte)2)]
    [DataRow((byte)3)]
    [DataRow((byte)4)]
    public void ChallengeAndLicenseBeforeRequestAreRejected(byte type)
    {
        using var client = new Licensing("user", "machine");
        Assert.Throws<RdpProtocolException>(() => client.Process(Packet(type, [])));
    }

    [TestMethod]
    public void RepeatedResetRequestsAreBounded()
    {
        using var client = new Licensing("user", "machine");
        for (int i = 0; i < 3; i++)
        {
            Assert.IsNull(client.Process(Alert(12, 3)));
        }
        Assert.Throws<RdpProtocolException>(() => client.Process(Alert(12, 3)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConnectionAcceptsSkippedLicensingAndValidClient(bool validClient)
    {
        byte[] demandActive = [6, 0, 0x11, 0, 0xEA, 3];
        byte[] input = validClient ? [.. Frame(Alert(7, 2)), .. Frame(demandActive)] : Frame(demandActive);
        using var wire = new ScriptedStream(input);
        await using RdpConnection connection = Connection(wire);
        byte[] actual = await Receive(connection);
        Assert.AreSequenceEqual(demandActive, actual);
        Assert.AreEqual(0L, wire.Output.Length);
    }

    [TestMethod]
    public async Task ResetDoesNotAllowPrematureActivation()
    {
        using var wire = new ScriptedStream([.. Frame(Alert(12, 3)), .. Frame([6, 0, 0x11, 0, 0xEA, 3])]);
        await using RdpConnection connection = Connection(wire);
        await Assert.ThrowsAsync<RdpProtocolException>(() => Receive(connection));
    }

    [TestMethod]
    public async Task InvalidCertificateSendsAbortBeforeFailing()
    {
        foreach (byte[] certificate in new byte[][] { [], [1, 0, 0, 0], [99, 0, 0, 0] })
        {
            byte[] request = RequestWithCertificate(certificate);
            using var client = new Licensing("user", "machine");
            byte[]? reply = client.Process(request);
            Assert.AreSequenceEqual(Alert(1, 1), reply);
            Assert.IsNotNull(client.Failure);
            Assert.IsFalse(client.CanActivate);
            Assert.Throws<RdpProtocolException>(() => client.Process(Alert(7, 2)));

            using var wire = new ScriptedStream(Frame(request));
            await using RdpConnection connection = Connection(wire);
            await Assert.ThrowsAsync<RdpProtocolException>(() => Receive(connection));
            byte[] output = wire.Output.ToArray();
            Assert.AreSequenceEqual(Alert(1, 1), output[14..]);
        }
    }

    private static byte[] RequestWithCertificate(byte[] certificate)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(new byte[32]);
        w.Write(0u);
        w.Write(2u);
        w.Write((ushort)0);
        w.Write(2u);
        w.Write((ushort)0);
        w.Write((ushort)13);
        w.Write((ushort)4);
        w.Write(1u);
        w.Write((ushort)3);
        w.Write((ushort)certificate.Length);
        w.Write(certificate);
        w.Write(0u);
        return Packet(1, stream.ToArray());
    }

    internal static byte[] Packet(byte type, byte[] body)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0x80);
        w.Write((ushort)0);
        w.Write(type);
        w.Write((byte)3);
        w.Write((ushort)(body.Length + 4));
        w.Write(body);
        return stream.ToArray();
    }

    internal static byte[] Alert(uint code, uint transition)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(code);
        w.Write(transition);
        w.Write((ushort)4);
        w.Write((ushort)0);
        return Packet(0xFF, stream.ToArray());
    }

    internal static byte[] Frame(byte[] payload, ushort channel = 1003)
    {
        var frame = new ByteWriter();
        frame.Bytes([3, 0]);
        frame.U16BE((ushort)(14 + payload.Length));
        frame.Bytes([2, 0xF0, 0x80, 0x68, 0, 0]);
        frame.U16BE(channel);
        frame.U8(0x70);
        if (payload.Length < 128)
        {
            frame.U8((byte)payload.Length);
        }
        else
        {
            frame.U16BE((ushort)(0x8000 | payload.Length));
            frame.PatchU16BE(2, (ushort)(15 + payload.Length));
        }
        frame.Bytes(payload);
        return frame.ToArray();
    }

    internal static RdpConnection Connection(Stream wire)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var connection = new RdpConnection(new RdpConnectOptions(
            "localhost", 3389, "user", "", "", 800, 600, 16, null, "localhost", []));
        var transport = new RdpTransport(wire);
        var mcs = new McsLayer(transport);
        typeof(McsLayer).GetProperty("UserId", flags)!.SetValue(mcs, (ushort)1001);
        typeof(RdpConnection).GetField("_transport", flags)!.SetValue(connection, transport);
        typeof(RdpConnection).GetField("_mcs", flags)!.SetValue(connection, mcs);
        typeof(RdpConnection).GetField("_ioChannel", flags)!.SetValue(connection, (ushort)1003);
        return connection;
    }

    internal static Task<byte[]> Receive(RdpConnection connection) =>
        (Task<byte[]>)typeof(RdpConnection).GetMethod("ReceiveLicensingThenDemandActiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [CancellationToken.None])!;

    internal sealed class ScriptedStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        internal MemoryStream Output { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Output.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
