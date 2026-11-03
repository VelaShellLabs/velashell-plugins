using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Pdu;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class LicensingExchangeTests
{
    [TestMethod]
    [DataRow(false, 512)]
    [DataRow(false, 2048)]
    [DataRow(true, 2048)]
    public void NewLicenseExchangeAuthenticatesChallengeAndLicense(bool x509, int keySize)
    {
        using var server = new LicenseServer(x509, keySize: keySize);
        using var client = new Licensing("licensing-user", "licensing-machine");
        server.AcceptRequest(client.Process(server.Request()), "licensing-user", "licensing-machine");
        Assert.IsFalse(client.CanActivate);
        _ = server.AcceptChallenge(client.Process(server.Challenge()));
        Assert.IsFalse(client.CanActivate);
        Assert.IsNull(client.Process(server.IssueLicense()));
        Assert.IsTrue(client.CanActivate);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TamperedCertificateSignatureReturnsAbortAndPreventsActivation(bool x509)
    {
        using var server = new LicenseServer(x509, tamperSignature: true);
        using var client = new Licensing("user", "machine");
        using BinaryReader alert = ReadPacket(client.Process(server.Request()), 0xFF);
        Assert.AreEqual(1u, alert.ReadUInt32(), "ERR_INVALID_SERVER_CERTIFICATE");
        Assert.AreEqual(1u, alert.ReadUInt32(), "ST_TOTAL_ABORT");
        Assert.AreEqual(0, ReadBlob(alert, 4).Length);
        Assert.AreEqual(alert.BaseStream.Length, alert.BaseStream.Position);
        Assert.IsNotNull(client.Failure);
        Assert.IsFalse(client.CanActivate);
    }

    [TestMethod]
    public async Task ConnectionRejectsDemandActiveAfterOnlyTheInitialLicenseRequest()
    {
        using var server = new LicenseServer(false);
        using var wire = new LicensingTests.ScriptedStream(
            [.. LicensingTests.Frame(server.Request()), .. LicensingTests.Frame(LicensingStream.DemandActive)]);
        await using RdpConnection connection = LicensingTests.Connection(wire);
        _ = await Assert.ThrowsAsync<RdpProtocolException>(() => LicensingTests.Receive(connection));
        byte[] frame = wire.Output.ToArray();
        Assert.IsTrue(frame.Length > 15, "The initial licensing response must have been sent before activation was rejected.");
        int payloadOffset = (frame[13] & 0x80) == 0 ? 14 : 15;
        server.AcceptRequest(frame[payloadOffset..], "user", Environment.MachineName);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TamperedServerMacCannotCompleteLicensing(bool issuedLicense)
    {
        using var server = new LicenseServer(false);
        using var client = new Licensing("user", "machine");
        server.AcceptRequest(client.Process(server.Request()), "user", "machine");
        if (issuedLicense)
        {
            _ = server.AcceptChallenge(client.Process(server.Challenge()));
        }
        byte[] tampered = issuedLicense ? server.IssueLicense() : server.Challenge();
        tampered[^1] ^= 0x80;
        RdpProtocolException error = Assert.Throws<RdpProtocolException>(() => client.Process(tampered));
        StringAssert.Contains(error.Message, "MAC");
        Assert.IsFalse(client.CanActivate);
    }

    [TestMethod]
    public void ResendPreservesResponsesAndRestartNegotiatesFreshKeysWithStableHardwareId()
    {
        using var server = new LicenseServer(false);
        using var client = new Licensing("user", "machine");
        byte[] request = client.Process(server.Request())!;
        server.AcceptRequest(request, "user", "machine");
        Assert.AreSequenceEqual(request, client.Process(LicensingTests.Alert(12, 4))!);
        byte[] challenge = client.Process(server.Challenge())!;
        byte[] hardware = server.AcceptChallenge(challenge);
        Assert.AreSequenceEqual(challenge, client.Process(LicensingTests.Alert(12, 4))!);
        byte[] firstRandom = server.ClientRandom;

        Assert.IsNull(client.Process(LicensingTests.Alert(12, 3)));
        Assert.IsFalse(client.CanActivate, "A restart must not allow Demand Active before a new exchange.");
        server.AcceptRequest(client.Process(server.Request()), "user", "machine");
        Assert.IsFalse(firstRandom.AsSpan().SequenceEqual(server.ClientRandom), "Restart must generate fresh key material.");
        byte[] restartedHardware = server.AcceptChallenge(client.Process(server.Challenge()));
        Assert.AreSequenceEqual(hardware, restartedHardware);
        Assert.IsNull(client.Process(server.IssueLicense()));
        Assert.IsTrue(client.CanActivate);
    }

    [TestMethod]
    public async Task ConnectionWritesLicensingRepliesOnIoChannelBeforeReadingDemandActive()
    {
        using var server = new LicenseServer(true);
        using var wire = new LicensingStream(server);
        await using RdpConnection connection = LicensingTests.Connection(wire);
        byte[] actual = await LicensingTests.Receive(connection);
        Assert.AreSequenceEqual(LicensingStream.DemandActive, actual);
        Assert.AreEqual(2, wire.Replies);
    }

    private sealed class LicenseServer : IDisposable
    {
        // Public protocol constants, not deployment credentials: MS-RDPBCGR 5.3.3.1.1.
        // https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpbcgr/c2389e29-5706-4ac4-b555-e26f93144db7
        private static readonly RSAParameters SigningKey = new()
        {
            Modulus = Convert.FromHexString(
                "3D3A5EBD72433EC94DBBC11E4ABA5FCB3E882087EFF5C1E2D7B76B9AF2524595" +
                "CE63656B583AFEEF7CE7BFFE3DF65C7D6C5E06091AF561BB2093095F056DEA87").Reverse().ToArray(),
            Exponent = Convert.FromHexString("C0887B5B"),
            D = Convert.FromHexString(
                "87A71932DA11875558001616256568F8243EE6FAE9674994CF92CC3399E80860" +
                "179A129F24DDB12499C73AB80A7B0DDD350779170B519BB3C7100113E73FF35F").Reverse().ToArray()
        };

        private readonly RSA? _rsa;
        private readonly RSAParameters _key;
        private readonly byte[] _certificate;
        private readonly byte[] _random = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _challenge = Encoding.Unicode.GetBytes("TEST\0");
        private byte[] _macSalt = [];
        private byte[] _encryptionKey = [];
        internal byte[] ClientRandom { get; private set; } = [];

        internal LicenseServer(bool x509, bool tamperSignature = false, int keySize = 2048)
        {
            if (!x509 && keySize == 512)
            {
                // macOS rejects 512-bit OS RSA keys; raw protocol arithmetic needs no OS key object.
                _key = SigningKey;
            }
            else
            {
                _rsa = RSA.Create(keySize);
                _key = _rsa.ExportParameters(true);
            }
            _certificate = x509 ? X509Certificate() : ProprietaryCertificate();
            if (tamperSignature)
            {
                // Change only the last signature byte, before the chain/RSA padding.
                _certificate[^(x509 ? 17 : 9)] ^= 1;
            }
        }

        internal byte[] Request() => LicensingTests.Packet(1, Encode(w =>
        {
            w.Write(_random);
            w.Write(0x00060000u);
            WriteString(w, "Fixture vendor", Encoding.Unicode);
            WriteString(w, "Fixture product", Encoding.Unicode);
            WriteBlob(w, 13, [1, 0, 0, 0]);
            WriteBlob(w, 3, _certificate);
            w.Write(1u);
            WriteBlob(w, 14, Encoding.ASCII.GetBytes("fixture-scope\0"));
        }));

        internal void AcceptRequest(byte[]? packet, string username, string machine)
        {
            using BinaryReader r = ReadPacket(packet, 0x13);
            Assert.AreEqual(1u, r.ReadUInt32());
            Assert.AreEqual(0u, r.ReadUInt32());
            ClientRandom = r.ReadBytes(32);
            Assert.AreEqual(32, ClientRandom.Length);
            byte[] encrypted = ReadBlob(r, 2);
            RSAParameters key = _key;
            int width = key.Modulus!.Length;
            Assert.AreEqual(width + 8, encrypted.Length);
            Assert.AreSequenceEqual(new byte[8], encrypted.AsSpan(width).ToArray());
            BigInteger ciphertext = new(encrypted.AsSpan(0, width), isUnsigned: true, isBigEndian: false);
            BigInteger modulus = new(key.Modulus, isUnsigned: true, isBigEndian: true);
            Assert.IsTrue(ciphertext < modulus);
            var decrypted = BigInteger.ModPow(ciphertext,
                new BigInteger(key.D!, isUnsigned: true, isBigEndian: true), modulus);
            byte[] premaster = new byte[48];
            Assert.IsTrue(decrypted.TryWriteBytes(premaster, out _, isUnsigned: true, isBigEndian: false),
                "Raw RSA plaintext must fit the 48-byte premaster, without PKCS padding.");
            Assert.AreSequenceEqual(Encoding.Latin1.GetBytes(username + '\0'), ReadBlob(r, 15));
            Assert.AreSequenceEqual(Encoding.Latin1.GetBytes(machine + '\0'), ReadBlob(r, 16));
            Assert.AreEqual(r.BaseStream.Length, r.BaseStream.Position);
            DeriveKeys(premaster);
            CryptographicOperations.ZeroMemory(premaster);
        }

        internal byte[] Challenge() => LicensingTests.Packet(2, Encode(w =>
        {
            w.Write(0u);
            WriteAuthenticated(w, _challenge);
        }));

        internal byte[] AcceptChallenge(byte[]? packet)
        {
            using BinaryReader r = ReadPacket(packet, 0x15);
            byte[] response = Rc4(ReadBlob(r, 9));
            byte[] hardware = Rc4(ReadBlob(r, 9));
            byte[] mac = r.ReadBytes(16);
            Assert.AreEqual(r.BaseStream.Length, r.BaseStream.Position);
            byte[] expected = Encode(w =>
            {
                w.Write((ushort)0x0100);
                w.Write((ushort)0xFF00);
                w.Write((ushort)3);
                w.Write((ushort)_challenge.Length);
                w.Write(_challenge);
            });
            Assert.AreSequenceEqual(expected, response, "Challenge response includes the complete eight-byte header.");
            Assert.AreEqual(20, hardware.Length);
            Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(hardware));
            Assert.IsTrue(hardware.AsSpan(4).ContainsAnyExcept((byte)0));
            Assert.AreSequenceEqual(Mac([.. response, .. hardware]), mac, "MAC must cover plaintext response and HWID together.");
            return hardware;
        }

        internal byte[] IssueLicense()
        {
            byte[] license = Encode(w =>
            {
                w.Write(0x00060000u);
                WriteString(w, "fixture-scope", Encoding.ASCII);
                WriteString(w, "Fixture vendor", Encoding.Unicode);
                WriteString(w, "Fixture product", Encoding.Unicode);
                w.Write(5u);
                w.Write(new byte[] { 0x31, 0x42, 0x53, 0x64, 0x75 });
            });
            return LicensingTests.Packet(3, Encode(w => WriteAuthenticated(w, license)));
        }

        private void WriteAuthenticated(BinaryWriter w, byte[] plaintext)
        {
            // Windows ignores the server encrypted-blob type (MS-RDPELE product behavior).
            WriteBlob(w, 0x4321, Rc4(plaintext));
            w.Write(Mac(plaintext));
        }

        private void DeriveKeys(byte[] premaster)
        {
            // MS-RDPELE 5.1: the session expansion reverses the two randoms.
            byte[] Expand(byte[] secret, byte[] first, byte[] second) => new[] { "A", "BB", "CCC" }
                .SelectMany(label => MD5.HashData(secret.Concat(SHA1.HashData(
                    Encoding.ASCII.GetBytes(label).Concat(secret).Concat(first).Concat(second).ToArray())).ToArray())).ToArray();
            byte[] master = Expand(premaster, ClientRandom, _random);
            byte[] session = Expand(master, _random, ClientRandom);
            _macSalt = session.Take(16).ToArray();
            _encryptionKey = MD5.HashData(session.Skip(16).Take(16).Concat(ClientRandom).Concat(_random).ToArray());
        }

        private byte[] Mac(byte[] data)
        {
            byte[] length = Encode(w => w.Write((uint)data.Length));
            byte[] inner = SHA1.HashData(_macSalt.Concat(Enumerable.Repeat((byte)0x36, 40))
                .Concat(length).Concat(data).ToArray());
            return MD5.HashData(_macSalt.Concat(Enumerable.Repeat((byte)0x5C, 48)).Concat(inner).ToArray());
        }

        private byte[] Rc4(byte[] data)
        {
            int[] permutation = Enumerable.Range(0, 256).ToArray();
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + permutation[i] + _encryptionKey[i % _encryptionKey.Length]) % 256;
                (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
            }
            int x = 0;
            j = 0;
            return data.Select(value =>
            {
                x = (x + 1) % 256;
                j = (j + permutation[x]) % 256;
                (permutation[x], permutation[j]) = (permutation[j], permutation[x]);
                return (byte)(value ^ permutation[(permutation[x] + permutation[j]) % 256]);
            }).ToArray();
        }

        private byte[] ProprietaryCertificate()
        {
            RSAParameters key = _key;
            byte[] publicKey = Encode(w =>
            {
                w.Write(0x31415352u);
                w.Write((uint)(key.Modulus!.Length + 8));
                w.Write((uint)(key.Modulus.Length * 8));
                w.Write((uint)(key.Modulus.Length - 1));
                w.Write((uint)new BigInteger(key.Exponent!, isUnsigned: true, isBigEndian: true));
                w.Write(key.Modulus.Reverse().ToArray());
                w.Write(new byte[8]);
            });
            byte[] signed = Encode(w =>
            {
                w.Write(1u);
                w.Write(1u);
                w.Write(1u);
                WriteBlob(w, 6, publicKey);
            });
            BigInteger modulus = new(SigningKey.Modulus!, isUnsigned: true, isBigEndian: true);
            BigInteger exponent = new(SigningKey.D!, isUnsigned: true, isBigEndian: true);
            byte[] paddedHash = [.. MD5.HashData(signed), 0, .. Enumerable.Repeat((byte)0xFF, 45), 1];
            byte[] signature = new byte[72];
            Assert.IsTrue(BigInteger.ModPow(new BigInteger(paddedHash, isUnsigned: true), exponent, modulus)
                .TryWriteBytes(signature.AsSpan(0, 64), out _, isUnsigned: true));
            return [.. signed, .. Encode(w => WriteBlob(w, 8, signature))];
        }

        private byte[] X509Certificate()
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Licensing fixture root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
            var leafRequest = new CertificateRequest("CN=Licensing fixture leaf", _rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
            using X509Certificate2 leaf = leafRequest.Create(root, now.AddDays(-1), now.AddDays(1), new byte[] { 1, 2, 3, 4 });
            return Encode(w =>
            {
                w.Write(2u);
                w.Write(2u);
                foreach (X509Certificate2 cert in new[] { root, leaf })
                {
                    byte[] der = cert.Export(X509ContentType.Cert);
                    w.Write((uint)der.Length);
                    w.Write(der);
                }
                w.Write(new byte[16]);
            });
        }

        public void Dispose() => _rsa?.Dispose();
    }

    private sealed class LicensingStream : Stream
    {
        internal static readonly byte[] DemandActive = [6, 0, 0x11, 0, 0xEA, 3];
        private readonly LicenseServer _server;
        private MemoryStream _input;
        private readonly MemoryStream _output = new();
        internal int Replies { get; private set; }

        internal LicensingStream(LicenseServer server)
        {
            _server = server;
            _input = new MemoryStream(LicensingTests.Frame(server.Request()));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.IsTrue(_input.Position < _input.Length, "Client read ahead without sending the required licensing reply.");
            return _input.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Assert.IsTrue(_input.Position < _input.Length, "Client read ahead without sending the required licensing reply.");
            return _input.Read(buffer.Span);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _output.Write(buffer, offset, count);
            byte[] frame = _output.ToArray();
            if (frame.Length < 4) { return; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame.Length < length) { return; }
            Assert.AreEqual(length, frame.Length);
            Assert.AreSequenceEqual(new byte[] { 3, 0 }, frame[..2]);
            Assert.AreSequenceEqual(new byte[] { 2, 0xF0, 0x80, 0x64, 0, 0, 3, 0xEB, 0x70 }, frame[4..13]);
            int payloadOffset = (frame[13] & 0x80) == 0 ? 14 : 15;
            int payloadLength = payloadOffset == 14 ? frame[13] : ((frame[13] & 0x7F) << 8) | frame[14];
            Assert.AreEqual(frame.Length - payloadOffset, payloadLength);
            Assert.AreEqual(_input.Length, _input.Position);
            byte[] next;
            if (Replies == 0)
            {
                _server.AcceptRequest(frame[payloadOffset..], "user", Environment.MachineName);
                next = LicensingTests.Frame(_server.Challenge());
            }
            else
            {
                Assert.AreEqual(1, Replies);
                _ = _server.AcceptChallenge(frame[payloadOffset..]);
                next = [.. LicensingTests.Frame(_server.IssueLicense()), .. LicensingTests.Frame(DemandActive)];
            }
            Replies++;
            _input.Dispose();
            _input = new MemoryStream(next);
            _output.SetLength(0);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] bytes = buffer.ToArray();
            Write(bytes, 0, bytes.Length);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); _output.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static BinaryReader ReadPacket(byte[]? packet, byte expectedType)
    {
        Assert.IsNotNull(packet);
        var r = new BinaryReader(new MemoryStream(packet));
        Assert.AreEqual((ushort)0x80, r.ReadUInt16());
        Assert.AreEqual((ushort)0, r.ReadUInt16());
        Assert.AreEqual(expectedType, r.ReadByte());
        Assert.AreEqual((byte)3, r.ReadByte());
        Assert.AreEqual(packet.Length - 4, r.ReadUInt16());
        return r;
    }

    private static byte[] ReadBlob(BinaryReader r, ushort expectedType)
    {
        Assert.AreEqual(expectedType, r.ReadUInt16());
        int length = r.ReadUInt16();
        byte[] data = r.ReadBytes(length);
        Assert.AreEqual(length, data.Length);
        return data;
    }

    private static void WriteBlob(BinaryWriter w, ushort type, byte[] data)
    {
        w.Write(type);
        w.Write(checked((ushort)data.Length));
        w.Write(data);
    }

    private static void WriteString(BinaryWriter w, string value, Encoding encoding)
    {
        byte[] data = encoding.GetBytes(value + '\0');
        w.Write((uint)data.Length);
        w.Write(data);
    }

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
