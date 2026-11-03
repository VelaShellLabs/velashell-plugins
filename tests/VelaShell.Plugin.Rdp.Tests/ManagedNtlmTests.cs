using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class ManagedNtlmTests
{
    [TestMethod]
    public void FirstTokenDoesNotRequireAnInstalledNtlmProvider()
    {
        using var auth = new NegotiateSecurityContext("Domain", "User", "password", "server", "NTLM");
        byte[] token = auth.Step([]);
        Assert.AreSequenceEqual("NTLMSSP\0"u8.ToArray(), token[..8]);
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(token.AsSpan(8)));
        Assert.IsFalse(auth.IsComplete);
    }

    [TestMethod]
    public void NtlmV2ProofAndCredSspSealingWorkWithoutAnInstalledNtlmProvider()
    {
        using var auth = new NegotiateSecurityContext("Domain", "User", "password", "server", "NTLM");
        byte[] sessionKey = Complete(auth);
        AssertSealing(auth, sessionKey);
    }

    private static void AssertSealing(INlaSecurityContext auth, byte[] sessionKey,
        uint clientSequence = 0, uint serverSequence = 0, bool consumedMic = false)
    {
        byte[] clientSigningKey = Key(sessionKey, "client-to-server", "signing");
        var clientCipher = new TestRc4(Key(sessionKey, "client-to-server", "sealing"));
        if (consumedMic) _ = clientCipher.Transform(new byte[8]);
        // CredSSP seals the public-key binding, then the credentials on the same context.
        for (uint sequence = clientSequence; sequence < clientSequence + 2; sequence++)
        {
            byte[] plain = Encoding.UTF8.GetBytes($"CredSSP client message {sequence}");
            byte[] wrapped = auth.Seal(plain);
            Assert.AreSequenceEqual(plain, clientCipher.Transform(wrapped[16..]));
            Assert.AreSequenceEqual(Signature(clientSigningKey, clientCipher, sequence, plain), wrapped[..16]);
        }

        byte[] echo = "CredSSP server binding hash"u8.ToArray();
        var serverCipher = new TestRc4(Key(sessionKey, "server-to-client", "sealing"));
        byte[] encrypted = serverCipher.Transform(echo);
        byte[] signature = Signature(Key(sessionKey, "server-to-client", "signing"), serverCipher, serverSequence, echo);
        Assert.AreSequenceEqual(echo, auth.Unseal([.. signature, .. encrypted]));
    }

    [TestMethod]
    public void InvalidServerSignatureIsRejected()
    {
        using var auth = new NegotiateSecurityContext("Domain", "User", "password", "server", "NTLM");
        byte[] sessionKey = Complete(auth);
        byte[] echo = "server binding"u8.ToArray();
        var cipher = new TestRc4(Key(sessionKey, "server-to-client", "sealing"));
        byte[] encrypted = cipher.Transform(echo);
        byte[] signature = Signature(Key(sessionKey, "server-to-client", "signing"), cipher, 0, echo);
        signature[4] ^= 1;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => auth.Unseal([.. signature, .. encrypted]));
    }

    [TestMethod]
    [DataRow("Domain", "User", "password")]
    [DataRow("域", "用户", "密码")]
    [DataRow("", "User", "password")]
    public void WindowsSspiCompletesChallengeQueriesSizesAndSeals(string domain, string user, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows SSPI requires Windows.");
            return;
        }
        using var auth = new SspiNegotiate(domain, user, password, "127.0.0.1");
        _ = auth.Step([]);
        byte[] response = auth.Step(BuildChallenge());
        Assert.IsTrue(auth.IsComplete);
        Assert.AreSequenceEqual("NTLMSSP\0"u8.ToArray(), response[..8]);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)));
        Assert.AreEqual(domain, Encoding.Unicode.GetString(Field(response, 28)));
        Assert.AreEqual(user, Encoding.Unicode.GetString(Field(response, 36)));
        byte[] sealedMessage = auth.Seal([1, 2, 3, 4]);
        Assert.IsTrue(sealedMessage.Length > 4, "The queried context sizes must provide space for the sealing signature.");
    }

    private static byte[] Complete(NegotiateSecurityContext auth)
    {
        _ = auth.Step([]);
        byte[] challenge = BuildChallenge();
        byte[] response = auth.Step(challenge);
        Assert.IsTrue(auth.IsComplete);
        Assert.AreSequenceEqual("NTLMSSP\0"u8.ToArray(), response[..8]);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)));
        Assert.AreEqual("Domain", Encoding.Unicode.GetString(Field(response, 28)));
        Assert.AreEqual("User", Encoding.Unicode.GetString(Field(response, 36)));

        return VerifyProofAndGetSessionKey(response, challenge);
    }

    private static byte[] VerifyProofAndGetSessionKey(byte[] response, byte[] challenge)
    {
        // MS-NLMP: NTOWFv1("password"), then NTOWFv2 and the challenge/blob proof.
        byte[] passwordHash = Convert.FromHexString("8846F7EAEE8FB117AD06BDD830B7586C");
        byte[] responseKey = HMACMD5.HashData(passwordHash, Encoding.Unicode.GetBytes("USERDomain"));
        byte[] ntResponse = Field(response, 20);
        byte[] proofInput = [.. challenge[24..32], .. ntResponse[16..]];
        byte[] proof = HMACMD5.HashData(responseKey, proofInput);
        Assert.AreSequenceEqual(proof, ntResponse[..16]);
        byte[] keyExchangeKey = HMACMD5.HashData(responseKey, proof);
        return new TestRc4(keyExchangeKey).Transform(Field(response, 52));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnixDefaultNegotiatesSpnegoNtlmAndSeals(bool requestMic)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Exercises the Unix managed SPNEGO provider.");
            return;
        }
        using INlaSecurityContext auth = NlaSecurityContext.Create("Domain", "User", "password", "127.0.0.1");
        Assert.IsInstanceOfType<NegotiateSecurityContext>(auth);
        byte[] first = auth.Step([]);
        Assert.AreEqual((byte)0x60, first[0]);
        Assert.IsTrue(CredSsp.IsNtlmNegotiate(first), "Without a usable realm the offer must still support NTLM.");
        AsnReader gss = new AsnReader(first, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 0));
        _ = gss.ReadObjectIdentifier();
        AsnReader init = gss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        byte[] mechList = init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadEncodedValue().ToArray();
        byte[] challenge = BuildChallenge();
        byte[] response = auth.Step(SpnegoReply(requestMic ? NegState.RequestMic : NegState.Incomplete, challenge));
        AsnReader resp = new AsnReader(response, AsnEncodingRules.DER)
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
        byte[] ntlmResponse = resp.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadOctetString();
        byte[] mic = resp.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3)).ReadOctetString();
        byte[] sessionKey = VerifyProofAndGetSessionKey(ntlmResponse, challenge);
        byte[] expectedMic = Signature(Key(sessionKey, "client-to-server", "signing"),
            new TestRc4(Key(sessionKey, "client-to-server", "sealing")), 0, mechList);
        Assert.AreSequenceEqual(expectedMic, mic);
        Assert.AreEqual(!requestMic, auth.IsComplete);
        if (requestMic)
        {
            byte[] serverMic = Signature(Key(sessionKey, "server-to-client", "signing"),
                new TestRc4(Key(sessionKey, "server-to-client", "sealing")), 0, mechList);
            byte[] final = auth.Step(SpnegoReply(NegState.Completed, mic: serverMic));
            Assert.IsEmpty(final);
        }
        Assert.IsTrue(auth.IsComplete);
        AssertSealing(auth, sessionKey, 1, requestMic ? 1u : 0u, consumedMic: !requestMic);
    }

    private enum NegState { Completed, Incomplete, Reject, RequestMic }

    private static byte[] SpnegoReply(NegState state, byte[]? token = null, byte[]? mic = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                writer.WriteEnumeratedValue(state);
            if (token is not null)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                    writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.2.10");
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    writer.WriteOctetString(token);
            }
            if (mic is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                    writer.WriteOctetString(mic);
        }
        return writer.Encode();
    }

    private static byte[] BuildChallenge()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("NTLMSSP\0"u8);
        writer.Write(2u);
        writer.Write((ushort)12);
        writer.Write((ushort)12);
        writer.Write(56u);
        writer.Write(0xE28A8237u); // Unicode, target name, NTLM, extended session security, sign/seal, key exchange, 128-bit.
        writer.Write(Convert.FromHexString("0123456789ABCDEF"));
        writer.Write(0UL);
        writer.Write((ushort)48);
        writer.Write((ushort)48);
        writer.Write(68u);
        writer.Write(new byte[] { 10, 0, 0, 0, 0, 0, 0, 15 });
        writer.Write(Encoding.Unicode.GetBytes("Domain"));
        WriteAvPair(writer, 2, Encoding.Unicode.GetBytes("Domain"));
        WriteAvPair(writer, 1, Encoding.Unicode.GetBytes("Server"));
        WriteAvPair(writer, 7, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        writer.Write(0u);
        return stream.ToArray();
    }

    private static void WriteAvPair(BinaryWriter writer, ushort id, byte[] value)
    {
        writer.Write(id);
        writer.Write((ushort)value.Length);
        writer.Write(value);
    }

    private static byte[] Field(byte[] message, int offset)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(offset));
        int start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(offset + 4)));
        return message.AsSpan(start, length).ToArray();
    }

    private static byte[] Key(byte[] sessionKey, string direction, string purpose) =>
        MD5.HashData([.. sessionKey, .. Encoding.ASCII.GetBytes($"session key to {direction} {purpose} key magic constant\0")]);

    private static byte[] Signature(byte[] signingKey, TestRc4 cipher, uint sequence, byte[] plain)
    {
        byte[] number = BitConverter.GetBytes(sequence);
        byte[] signed = [.. number, .. plain];
        byte[] checksum = HMACMD5.HashData(signingKey, signed)[..8];
        return [1, 0, 0, 0, .. cipher.Transform(checksum), .. number];
    }

    // Independent server-side RC4 fixture for MS-NLMP sealing; never used by production code.
    private sealed class TestRc4
    {
        private readonly byte[] _state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        private int _x, _y;

        internal TestRc4(byte[] key)
        {
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + _state[i] + key[i % key.Length]) & 255;
                (_state[i], _state[j]) = (_state[j], _state[i]);
            }
        }

        internal byte[] Transform(byte[] input)
        {
            byte[] output = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                _x = (_x + 1) & 255;
                _y = (_y + _state[_x]) & 255;
                (_state[_x], _state[_y]) = (_state[_y], _state[_x]);
                output[i] = (byte)(input[i] ^ _state[(_state[_x] + _state[_y]) & 255]);
            }
            return output;
        }
    }
}
