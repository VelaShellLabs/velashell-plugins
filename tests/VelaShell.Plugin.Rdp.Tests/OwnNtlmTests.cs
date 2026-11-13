using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;
using VelaShell.Rdp.Security.Ntlm;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The VelaShell NTLMv2 / SPNEGO implementation: MS-NLMP 4.2 test vectors, interoperability with Windows' own NTLM
/// (SSPI) in both sealing directions, fail-closed parsing, MS-SPNG 3.2.5.1 RC4 state, and complete CredSSP exchanges
/// over TLS. Everything except the SSPI interoperability test runs on every OS.
/// </summary>
[TestClass]
public sealed class OwnNtlmTests
{
    private static readonly byte[] ServerChallenge = Convert.FromHexString("0123456789ABCDEF");

    // ---- MS-NLMP 4.2 test vectors ----

    [TestMethod]
    public void NtowfMatchesTheSpecificationVectors()
    {
        // MS-NLMP 4.2.2.1.2 and 4.2.4.1.1: Password "Password", User "User", Domain "Domain".
        Assert.AreEqual("A4F49C406510BDCAB6824EE7C30FD852", Convert.ToHexString(NtlmV2Client.NtowfV1("Password")));
        Assert.AreEqual("0C868A403BFD7A93A3001EF22EF02E3F", Convert.ToHexString(NtlmV2Client.NtowfV2("Password", "User", "Domain")));
    }

    [TestMethod]
    public void NtlmV2ResponseKeysAndSealingMatchTheSpecificationVectors()
    {
        // MS-NLMP 4.2.4: ClientChallenge aa…, Time 0, RandomSessionKey 55…, AV pairs NbDomainName "Domain",
        // NbComputerName "Server".
        byte[] ntowf = NtlmV2Client.NtowfV2("Password", "User", "Domain");
        byte[] clientChallenge = Enumerable.Repeat((byte)0xAA, 8).ToArray();
        byte[] avPairs = [.. AvPair(2, Encoding.Unicode.GetBytes("Domain")), .. AvPair(1, Encoding.Unicode.GetBytes("Server")), 0, 0, 0, 0];
        (byte[] proof, byte[] response) = NtlmV2Client.NtResponse(ntowf, ServerChallenge, clientChallenge, 0, avPairs);
        Assert.AreEqual("68CD0AB851E51C96AABC927BEBEF6A1C", Convert.ToHexString(proof));
        Assert.AreSequenceEqual(proof, response[..16]);
        Assert.AreEqual("86C35097AC9CEC102554764A57CCCC19AAAAAAAAAAAAAAAA",
            Convert.ToHexString(NtlmV2Client.LmV2Response(ntowf, ServerChallenge, clientChallenge)));

        byte[] sessionBaseKey = HMACMD5.HashData(ntowf, proof);
        Assert.AreEqual("8DE40CCADBC14A82F15CB0AD0DE95CA3", Convert.ToHexString(sessionBaseKey));
        byte[] randomSessionKey = Enumerable.Repeat((byte)0x55, 16).ToArray();
        Assert.AreEqual("C5DAD2544FC9799094CE1CE90BC9D03E", Convert.ToHexString(Rc4.Apply(sessionBaseKey, randomSessionKey)));

        // 4.2.4.4: GSS_WrapEx of "Plaintext" (UTF-16) on the client-to-server keys, sequence 0.
        using var sealing = new NtlmSealing(NtlmV2Client.SignKey(randomSessionKey, true), NtlmV2Client.SealKey(randomSessionKey, true));
        byte[] sealedMessage = sealing.Seal(Encoding.Unicode.GetBytes("Plaintext"));
        Assert.AreEqual("010000007FB38EC5C55D497600000000", Convert.ToHexString(sealedMessage[..16]));
        Assert.AreEqual("54E50165BF1936DC996020C1811B0F06FB5F", Convert.ToHexString(sealedMessage[16..]));
    }

    // ---- Interoperability with Windows' own NTLM ----

    /// <summary>
    /// Windows SSPI as the client, this implementation as the server side: our NTOWFv2/proof verify SSPI's response,
    /// our MIC matches the MIC SSPI put in the AUTHENTICATE message, our unseal opens SSPI's sealed messages, and
    /// SSPI opens messages our implementation sealed — key derivation, RC4, MAC and token layout all agree.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsSspiAndThisImplementationAgreeOnEveryKeyAndSeal()
    {
        using var sspi = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "NTLM",
            Credential = new NetworkCredential("User", "password", "Domain"),
            TargetName = "",
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign
        });
        byte[] negotiate = sspi.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out NegotiateAuthenticationStatusCode status)!;
        Assert.AreEqual(NegotiateAuthenticationStatusCode.ContinueNeeded, status);
        byte[] challenge = BuildChallenge(withTimestamp: true);
        byte[] authenticate = sspi.GetOutgoingBlob(challenge, out status)!;
        Assert.AreEqual(NegotiateAuthenticationStatusCode.Completed, status);

        byte[] exportedSessionKey = RecoverExportedSessionKey(authenticate, "password", "User", "Domain");
        Assert.AreSequenceEqual(ExpectedMic(exportedSessionKey, negotiate, challenge, authenticate), authenticate[72..88],
            "The MIC is HMAC_MD5(ExportedSessionKey, NEGOTIATE ‖ CHALLENGE ‖ AUTHENTICATE with a zero MIC) at offset 72.");

        using var clientToServer = new NtlmSealing(NtlmV2Client.SignKey(exportedSessionKey, true), NtlmV2Client.SealKey(exportedSessionKey, true));
        using var serverToClient = new NtlmSealing(NtlmV2Client.SignKey(exportedSessionKey, false), NtlmV2Client.SealKey(exportedSessionKey, false));
        for (int i = 0; i < 2; i++)
        {
            byte[] message = Encoding.UTF8.GetBytes($"client message {i}");
            var wrapped = new ArrayBufferWriter<byte>();
            Assert.AreEqual(NegotiateAuthenticationStatusCode.Completed, sspi.Wrap(message, wrapped, true, out bool encrypted));
            Assert.IsTrue(encrypted);
            Assert.AreSequenceEqual(message, clientToServer.Unseal(wrapped.WrittenSpan));

            byte[] reply = Encoding.UTF8.GetBytes($"server message {i}");
            var unwrapped = new ArrayBufferWriter<byte>();
            Assert.AreEqual(NegotiateAuthenticationStatusCode.Completed, sspi.Unwrap(serverToClient.Seal(reply), unwrapped, out encrypted));
            Assert.AreSequenceEqual(reply, unwrapped.WrittenSpan.ToArray());
        }
    }

    // ---- The client ----

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheClientProducesAVerifiableAuthenticateMessageAndSealsInOrder(bool withTimestamp)
    {
        byte[] exported = Enumerable.Repeat((byte)0x42, 16).ToArray();
        using var client = new NtlmV2Client("Domain", "User", "password", "", Fixed(exported));
        byte[] negotiate = client.Step([]);
        Assert.HasCount(40, negotiate);
        Assert.AreEqual((uint)NtlmV2Client.Requested, BinaryPrimitives.ReadUInt32LittleEndian(negotiate.AsSpan(12)));
        Assert.IsTrue(CredSsp.IsNtlmNegotiate(negotiate), "Auto mode must still recognise the first token as credential-free.");

        byte[] challenge = BuildChallenge(withTimestamp);
        byte[] authenticate = client.Step(challenge);
        Assert.IsTrue(client.IsComplete);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(authenticate.AsSpan(8)));
        Assert.AreEqual("Domain", Encoding.Unicode.GetString(Field(authenticate, 28)));
        Assert.AreEqual("User", Encoding.Unicode.GetString(Field(authenticate, 36)));
        Assert.AreSequenceEqual(exported, RecoverExportedSessionKey(authenticate, "password", "User", "Domain"));
        Assert.AreSequenceEqual(ExpectedMic(exported, negotiate, challenge, authenticate), authenticate[72..88]);

        byte[] lm = Field(authenticate, 12);
        byte[] blob = Field(authenticate, 20)[16..];
        if (withTimestamp)
        {
            Assert.IsTrue(lm.All(static b => b == 0), "With a server timestamp the LM response is Z(24).");
            Assert.AreSequenceEqual(BitConverter.GetBytes(Timestamp), blob[8..16], "The blob reuses the server's timestamp.");
        }
        else
        {
            Assert.AreSequenceEqual(NtlmV2Client.LmV2Response(NtlmV2Client.NtowfV2("password", "User", "Domain"), ServerChallenge, blob[16..24]), lm);
        }
        List<(AvId Id, byte[] Value)> pairs = NtlmV2Client.ParseAvPairs(blob[28..]);
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(pairs.Single(static p => p.Id == AvId.Flags).Value), "MsvAvFlags announces the MIC.");
        Assert.IsTrue(pairs.Single(static p => p.Id == AvId.ChannelBindings).Value.All(static b => b == 0));
        Assert.IsFalse(pairs.Exists(static p => p.Id == AvId.TargetName), "No SPN, no MsvAvTargetName.");

        AssertSealsLikeTheServerExpects(client, exported);
    }

    [TestMethod]
    public void AnSpnIsSentAsTheTargetName()
    {
        using var client = new NtlmV2Client("Domain", "User", "password", "TERMSRV/rdp.example");
        _ = client.Step([]);
        byte[] authenticate = client.Step(BuildChallenge(withTimestamp: true));
        List<(AvId Id, byte[] Value)> pairs = NtlmV2Client.ParseAvPairs(Field(authenticate, 20)[(16 + 28)..]);
        Assert.AreEqual("TERMSRV/rdp.example", Encoding.Unicode.GetString(pairs.Single(static p => p.Id == AvId.TargetName).Value));
    }

    public static IEnumerable<object[]> BadChallenges()
    {
        byte[] good = BuildChallenge(withTimestamp: true);
        yield return ["truncated", good[..40]];
        yield return ["wrong type", Patch(good, 8, 3)];
        yield return ["no sealing", WithFlags(good, ~0x20u)];
        yield return ["no extended session security", WithFlags(good, ~0x80000u)];
        yield return ["no key exchange", WithFlags(good, ~0x40000000u)];
        yield return ["target info offset out of range", Patch(good, 44, 0xFFFF)];
        yield return ["target info overrun", Patch(good, 40, 0x7FFF)];
        yield return ["no NetBIOS computer name", BuildChallenge(withTimestamp: true, withComputer: false)];
        yield return ["unterminated target info", BuildChallenge(withTimestamp: true, terminate: false)];
    }

    [TestMethod]
    [DynamicData(nameof(BadChallenges))]
    public void MalformedOrWeakChallengesAreRefused(string why, byte[] challenge)
    {
        using var client = new NtlmV2Client("Domain", "User", "password");
        _ = client.Step([]);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => client.Step(challenge), why);
        Assert.IsFalse(client.IsComplete);
    }

    [TestMethod]
    public void ATamperedServerMessageIsRejected()
    {
        byte[] exported = Enumerable.Repeat((byte)0x42, 16).ToArray();
        using var client = new NtlmV2Client("Domain", "User", "password", "", Fixed(exported));
        _ = client.Step([]);
        _ = client.Step(BuildChallenge(withTimestamp: true));
        using var server = new NtlmSealing(NtlmV2Client.SignKey(exported, false), NtlmV2Client.SealKey(exported, false));
        byte[] sealedMessage = server.Seal("echo"u8);
        sealedMessage[^1] ^= 1;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => client.Unseal(sealedMessage));
    }

    // ---- SPNEGO ----

    [TestMethod]
    public void SpnegoSendsTheMechListMicAndSealsPubKeyAuthWithThePreMicRc4State()
    {
        byte[] exported = Enumerable.Repeat((byte)0x42, 16).ToArray();
        using var spnego = new SpnegoNtlmContext(new NtlmV2Client("Domain", "User", "password", "", Fixed(exported)));
        byte[] init = spnego.Step([]);
        Assert.AreEqual((byte)0x60, init[0]);
        Assert.IsTrue(CredSsp.IsNtlmNegotiate(init), "An NTLM-only SPNEGO offer is still recognised as credential-free.");
        byte[] mechTypes = MechTypes(init);

        byte[] reply = spnego.Step(NegTokenResp(1, SpnegoNtlmContext.NtlmOid, BuildChallenge(withTimestamp: true), null));
        Assert.IsTrue(spnego.IsComplete);
        (byte[]? authenticate, byte[]? mic) = ReadNegTokenResp(reply);
        Assert.IsNotNull(authenticate);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(authenticate.AsSpan(8)));

        // Computed independently of NtlmSealing: the mechListMIC at sequence 0 on a fresh RC4 state…
        byte[] signKey = NtlmV2Client.SignKey(exported, true), sealKey = NtlmV2Client.SealKey(exported, true);
        Assert.AreSequenceEqual(ReferenceMac(signKey, new Rc4(sealKey), 0, mechTypes), mic!);
        // …and pubKeyAuth at sequence 1 on the same fresh RC4 state (MS-SPNG 3.2.5.1).
        byte[] binding = RandomNumberGenerator.GetBytes(32);
        byte[] sealedBinding = spnego.Seal(binding);
        using var rc4 = new Rc4(sealKey);
        byte[] ciphertext = new byte[binding.Length];
        rc4.Transform(binding, ciphertext);
        Assert.AreSequenceEqual(ciphertext, sealedBinding[16..]);
        Assert.AreSequenceEqual(ReferenceMac(signKey, rc4, 1, binding), sealedBinding[..16]);

        // The server's final token: accept-completed and its mechListMIC (sequence 0, fresh server RC4); its echo is
        // then sequence 1 on that same fresh state.
        byte[] serverSign = NtlmV2Client.SignKey(exported, false), serverSeal = NtlmV2Client.SealKey(exported, false);
        spnego.Finish(NegTokenResp(0, null, null, ReferenceMac(serverSign, new Rc4(serverSeal), 0, mechTypes)));
        using var serverRc4 = new Rc4(serverSeal);
        byte[] echo = "echo"u8.ToArray();
        byte[] echoCipher = new byte[echo.Length];
        serverRc4.Transform(echo, echoCipher);
        Assert.AreSequenceEqual(echo, spnego.Unseal([.. ReferenceMac(serverSign, serverRc4, 1, echo), .. echoCipher]));
    }

    [TestMethod]
    public void SpnegoRefusesABadServerMicAnotherMechanismAndRejection()
    {
        byte[] exported = Enumerable.Repeat((byte)0x42, 16).ToArray();
        using (var spnego = new SpnegoNtlmContext(new NtlmV2Client("Domain", "User", "password", "", Fixed(exported))))
        {
            _ = spnego.Step([]);
            _ = spnego.Step(NegTokenResp(1, SpnegoNtlmContext.NtlmOid, BuildChallenge(withTimestamp: true), null));
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => spnego.Finish(NegTokenResp(0, null, null, new byte[16])));
        }
        using (var spnego = new SpnegoNtlmContext(new NtlmV2Client("Domain", "User", "password")))
        {
            _ = spnego.Step([]);
            _ = Assert.ThrowsExactly<RdpProtocolException>(() =>
                spnego.Step(NegTokenResp(1, "1.2.840.113554.1.2.2", BuildChallenge(withTimestamp: true), null)));
        }
        using (var spnego = new SpnegoNtlmContext(new NtlmV2Client("Domain", "User", "password")))
        {
            _ = spnego.Step([]);
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => spnego.Step(NegTokenResp(2, null, null, null)));
        }
    }

    // ---- Complete CredSSP exchanges over TLS ----

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CredSspCompletesAgainstAServerFollowingMsNlmpAndMsSpng(bool spnego)
    {
        const string Domain = "Domain", User = "User", Password = "password";
        var ntlm = new NtlmV2Client(Domain, User, Password);
        using INlaSecurityContext client = spnego ? new SpnegoNtlmContext(ntlm) : ntlm;
        bool credentialsChecked = false;
        await CredSspNegotiationTests.RunAsync(async tls =>
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(tls.LocalCertificate!.GetRawCertData());
            byte[] publicKey = CredSsp.ServerPublicKey(certificate);

            Dictionary<int, byte[]> first = ReadRequest(await FakeRdpConnection.ReadDerAsync(tls));
            byte[] negotiate = spnego ? NegTokenInitMechToken(first[1], out byte[] mechTypes) : first[1];
            byte[] challenge = BuildChallenge(withTimestamp: true);
            await tls.WriteAsync(Reply(spnego ? NegTokenResp(1, SpnegoNtlmContext.NtlmOid, challenge, null) : challenge, null));

            Dictionary<int, byte[]> second = ReadRequest(await FakeRdpConnection.ReadDerAsync(tls));
            (byte[]? authenticate, byte[]? clientMic) = spnego ? ReadNegTokenResp(second[1]) : (second[1], null);
            byte[] exported = RecoverExportedSessionKey(authenticate!, Password, User, Domain);
            Assert.AreSequenceEqual(ExpectedMic(exported, negotiate, challenge, authenticate!), authenticate![72..88]);
            using var fromClient = new NtlmSealing(NtlmV2Client.SignKey(exported, true), NtlmV2Client.SealKey(exported, true));
            using var toClient = new NtlmSealing(NtlmV2Client.SignKey(exported, false), NtlmV2Client.SealKey(exported, false));
            byte[]? serverMic = null;
            if (spnego)
            {
                mechTypes = MechTypes(first[1]);
                Assert.IsTrue(fromClient.WithRc4StatePreserved(s => s.Verify(mechTypes, clientMic)), "Client mechListMIC.");
                serverMic = toClient.WithRc4StatePreserved(s => s.Sign(mechTypes));
            }
            Assert.AreSequenceEqual(CredSsp.BindingHashForTest(true, second[5], publicKey), fromClient.Unseal(second[3]));
            byte[] echo = toClient.Seal(CredSsp.BindingHashForTest(false, second[5], publicKey));
            await tls.WriteAsync(Reply(spnego ? NegTokenResp(0, null, null, serverMic) : null, echo));

            Dictionary<int, byte[]> last = ReadRequest(await FakeRdpConnection.ReadDerAsync(tls));
            byte[] credentials = fromClient.Unseal(last[2]);
            Assert.AreSequenceEqual(CredSsp.BuildCredentialsForTest(Domain, User, Password), credentials);
            credentialsChecked = true;
        }, client, Domain, User, Password);
        Assert.IsTrue(credentialsChecked);
    }

    // ---- Provider selection ----

    [TestMethod]
    [DataRow("own", typeof(NtlmV2Client))]
    [DataRow("own-spnego", typeof(SpnegoNtlmContext))]
    public void TheConfiguredProviderIsUsedForAnIpTarget(string provider, Type expected)
    {
        NlaSecurityContext.ProviderOverrideForTest = provider == "own" ? NtlmProvider.Own : NtlmProvider.OwnSpnego;
        try
        {
            using INlaSecurityContext auth = NlaSecurityContext.Create("Domain", "User", "password", "192.0.2.10");
            Assert.IsInstanceOfType(auth, expected);
        }
        finally
        {
            NlaSecurityContext.ProviderOverrideForTest = null;
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void OutsideWindowsAnIpTargetUsesThisImplementationByDefault()
    {
        if (NlaSecurityContext.ConfiguredProvider() is not null)
        {
            Assert.Inconclusive($"{NlaSecurityContext.ProviderVariable} is set in this environment.");
            return;
        }
        using INlaSecurityContext auth = NlaSecurityContext.Create("Domain", "User", "password", "192.0.2.10");
        Assert.IsInstanceOfType<NtlmV2Client>(auth);
    }

    // ---- Fixtures (independent of the production code where they serve as a reference) ----

    private const long Timestamp = 0x01DA_0000_0000_0000;

    private static Func<int, byte[]> Fixed(byte[] exportedSessionKey) =>
        length => length == 16 ? (byte[])exportedSessionKey.Clone() : Enumerable.Repeat((byte)0xAA, length).ToArray();

    private static byte[] BuildChallenge(bool withTimestamp, bool withComputer = true, bool terminate = true)
    {
        byte[] targetName = Encoding.Unicode.GetBytes("Domain");
        var info = new List<byte>();
        info.AddRange(AvPair(2, Encoding.Unicode.GetBytes("Domain")));
        if (withComputer) info.AddRange(AvPair(1, Encoding.Unicode.GetBytes("Server")));
        if (withTimestamp) info.AddRange(AvPair(7, BitConverter.GetBytes(Timestamp)));
        if (terminate) info.AddRange([0, 0, 0, 0]);
        byte[] message = new byte[56 + targetName.Length + info.Count];
        "NTLMSSP\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(12), (ushort)targetName.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), (ushort)targetName.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), 0xE28A8235); // Unicode, target, sign, seal, NTLM, always sign, ESS, target info, version, 128, key exch, 56
        ServerChallenge.CopyTo(message, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(40), (ushort)info.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(42), (ushort)info.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(44), (uint)(56 + targetName.Length));
        message[48] = 10;
        message[55] = 15;
        targetName.CopyTo(message, 56);
        info.CopyTo(message, 56 + targetName.Length);
        return message;
    }

    private static byte[] AvPair(ushort id, byte[] value) =>
        [(byte)id, (byte)(id >> 8), (byte)value.Length, (byte)(value.Length >> 8), .. value];

    private static byte[] Patch(byte[] message, int offset, ushort value)
    {
        byte[] copy = (byte[])message.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static byte[] WithFlags(byte[] challenge, uint mask)
    {
        byte[] copy = (byte[])challenge.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(20), BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(20)) & mask);
        return copy;
    }

    private static byte[] Field(byte[] message, int offset)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(offset));
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(offset + 4));
        return message.AsSpan(start, length).ToArray();
    }

    /// <summary>Server side of MS-NLMP 3.2.5.1.2: verify NTProofStr, derive the key exchange key, decrypt the session key.</summary>
    private static byte[] RecoverExportedSessionKey(byte[] authenticate, string password, string user, string domain)
    {
        byte[] ntowf = HMACMD5.HashData(NtlmV2Client.NtowfV1(password), Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain));
        byte[] nt = Field(authenticate, 20);
        byte[] proof = HMACMD5.HashData(ntowf, (byte[])[.. ServerChallenge, .. nt[16..]]);
        Assert.AreSequenceEqual(proof, nt[..16], "NTProofStr");
        byte[] keyExchangeKey = HMACMD5.HashData(ntowf, proof);
        return Rc4.Apply(keyExchangeKey, Field(authenticate, 52));
    }

    private static byte[] ExpectedMic(byte[] exportedSessionKey, byte[] negotiate, byte[] challenge, byte[] authenticate)
    {
        byte[] zeroed = (byte[])authenticate.Clone();
        Array.Clear(zeroed, 72, 16);
        return HMACMD5.HashData(exportedSessionKey, (byte[])[.. negotiate, .. challenge, .. zeroed]);
    }

    /// <summary>MS-NLMP 3.4.4.2 MAC, written out independently: 1 ‖ RC4(HMAC_MD5(key, seq ‖ msg)[0..7]) ‖ seq.</summary>
    private static byte[] ReferenceMac(byte[] signKey, Rc4 handle, uint sequence, byte[] message)
    {
        byte[] seq = BitConverter.GetBytes(sequence);
        byte[] checksum = HMACMD5.HashData(signKey, (byte[])[.. seq, .. message])[..8];
        byte[] encrypted = new byte[8];
        handle.Transform(checksum, encrypted);
        return [1, 0, 0, 0, .. encrypted, .. seq];
    }

    /// <summary>Bare NTLM: CredSSP's messages are sequence 0, 1, … on fresh RC4 states in both directions.</summary>
    private static void AssertSealsLikeTheServerExpects(NtlmV2Client client, byte[] exported)
    {
        using var fromClient = new NtlmSealing(NtlmV2Client.SignKey(exported, true), NtlmV2Client.SealKey(exported, true));
        using var toClient = new NtlmSealing(NtlmV2Client.SignKey(exported, false), NtlmV2Client.SealKey(exported, false));
        for (int i = 0; i < 2; i++)
        {
            byte[] message = Encoding.UTF8.GetBytes($"CredSSP message {i}");
            byte[] sealedMessage = client.Seal(message);
            Assert.AreEqual((uint)i, BinaryPrimitives.ReadUInt32LittleEndian(sealedMessage.AsSpan(12)));
            Assert.AreSequenceEqual(message, fromClient.Unseal(sealedMessage));
        }
        byte[] echo = "server echo"u8.ToArray();
        Assert.AreSequenceEqual(echo, client.Unseal(toClient.Seal(echo)));
    }

    private static byte[] MechTypes(byte[] negTokenInit)
    {
        AsnReader gss = new AsnReader(negTokenInit, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 0));
        Assert.AreEqual(SpnegoNtlmContext.SpnegoOid, gss.ReadObjectIdentifier());
        AsnReader init = gss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        return init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadEncodedValue().ToArray();
    }

    private static byte[] NegTokenInitMechToken(byte[] negTokenInit, out byte[] mechTypes)
    {
        mechTypes = MechTypes(negTokenInit);
        AsnReader gss = new AsnReader(negTokenInit, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 0));
        _ = gss.ReadObjectIdentifier();
        AsnReader init = gss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        _ = init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        return init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadOctetString();
    }

    private static byte[] NegTokenResp(int state, string? mech, byte[]? token, byte[]? mic)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteEnumeratedValue((NegState)state);
            if (mech is not null) using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1))) writer.WriteObjectIdentifier(mech);
            if (token is not null) using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2))) writer.WriteOctetString(token);
            if (mic is not null) using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3))) writer.WriteOctetString(mic);
        }
        return writer.Encode();
    }

    private enum NegState { AcceptCompleted, AcceptIncomplete, Reject, RequestMic }

    private static (byte[]? Token, byte[]? Mic) ReadNegTokenResp(byte[] resp)
    {
        AsnReader body = new AsnReader(resp, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
        byte[]? token = null, mic = null;
        while (body.HasData)
        {
            Asn1Tag tag = body.PeekTag();
            AsnReader field = body.ReadSequence(tag);
            if (tag.TagValue == 2) token = field.ReadOctetString();
            else if (tag.TagValue == 3) mic = field.ReadOctetString();
            else _ = field.ReadEncodedValue();
        }
        return (token, mic);
    }

    private static Dictionary<int, byte[]> ReadRequest(byte[] packet)
    {
        var fields = new Dictionary<int, byte[]>();
        AsnReader request = new AsnReader(packet, AsnEncodingRules.DER).ReadSequence();
        while (request.HasData)
        {
            Asn1Tag tag = request.PeekTag();
            AsnReader field = request.ReadSequence(tag);
            if (tag.TagValue == 0) { _ = field.ReadInteger(); continue; }
            if (tag.TagValue == 1)
                field = field.ReadSequence().ReadSequence().ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            fields.Add(tag.TagValue, field.ReadOctetString());
        }
        return fields;
    }

    private static byte[] Reply(byte[]? token, byte[]? pubKeyAuth)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteInteger(6);
            if (token is { Length: > 0 })
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (writer.PushSequence())
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteOctetString(token);
            }
            if (pubKeyAuth is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3))) writer.WriteOctetString(pubKeyAuth);
        }
        return writer.Encode();
    }
}
