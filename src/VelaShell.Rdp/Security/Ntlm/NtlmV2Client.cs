using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace VelaShell.Rdp.Security.Ntlm;

/// <summary>NEGOTIATE flags (MS-NLMP 2.2.2.5).</summary>
[Flags]
internal enum NtlmFlags : uint
{
    Unicode = 0x00000001,
    Oem = 0x00000002,
    RequestTarget = 0x00000004,
    Sign = 0x00000010,
    Seal = 0x00000020,
    LmKey = 0x00000080,
    Ntlm = 0x00000200,
    Anonymous = 0x00000800,
    OemDomainSupplied = 0x00001000,
    OemWorkstationSupplied = 0x00002000,
    AlwaysSign = 0x00008000,
    TargetTypeDomain = 0x00010000,
    TargetTypeServer = 0x00020000,
    ExtendedSessionSecurity = 0x00080000,
    Identify = 0x00100000,
    NonNtSessionKey = 0x00400000,
    TargetInfo = 0x00800000,
    Version = 0x02000000,
    Key128 = 0x20000000,
    KeyExchange = 0x40000000,
    Key56 = 0x80000000
}

/// <summary>AV_PAIR identifiers (MS-NLMP 2.2.2.1).</summary>
internal enum AvId : ushort
{
    Eol = 0,
    NbComputerName = 1,
    NbDomainName = 2,
    DnsComputerName = 3,
    DnsDomainName = 4,
    DnsTreeName = 5,
    Flags = 6,
    Timestamp = 7,
    SingleHost = 8,
    TargetName = 9,
    ChannelBindings = 10
}

/// <summary>
/// NTLMv2 client written to MS-NLMP: NEGOTIATE → CHALLENGE → AUTHENTICATE with extended session security, key
/// exchange, 128-bit keys and the message integrity code (MIC), then sealing/unsealing for CredSSP (3.4.3–3.4.4).
/// Used bare as an <see cref="INlaSecurityContext" />, or as the mechanism inside <see cref="SpnegoNtlmContext" />.
/// <para>
/// Policy is fail-closed: the server must agree to signing, sealing, extended session security, key exchange and
/// 128-bit keys, and must supply target information (NTLMv2 cannot be computed without it). NTLMv1 and LM are never
/// produced. Credentials stay in this object only until the AUTHENTICATE message is built.
/// </para>
/// </summary>
internal sealed class NtlmV2Client : INlaSecurityContext
{
    private static readonly byte[] Signature = "NTLMSSP\0"u8.ToArray();

    /// <summary>What the client offers in NEGOTIATE.</summary>
    internal const NtlmFlags Requested = NtlmFlags.Unicode | NtlmFlags.Oem | NtlmFlags.RequestTarget | NtlmFlags.Sign
        | NtlmFlags.Seal | NtlmFlags.Ntlm | NtlmFlags.AlwaysSign | NtlmFlags.ExtendedSessionSecurity | NtlmFlags.Version
        | NtlmFlags.Key128 | NtlmFlags.KeyExchange | NtlmFlags.Key56;

    /// <summary>What the CHALLENGE must grant; anything less is refused.</summary>
    internal const NtlmFlags Required = NtlmFlags.Unicode | NtlmFlags.Sign | NtlmFlags.Seal | NtlmFlags.Ntlm
        | NtlmFlags.ExtendedSessionSecurity | NtlmFlags.Key128 | NtlmFlags.KeyExchange;

    /// <summary>MsvAvFlags bit 0x2: the AUTHENTICATE message carries a MIC (MS-NLMP 2.2.2.1).</summary>
    private const uint AvFlagMicPresent = 0x00000002;

    // VERSION (MS-NLMP 2.2.2.10): 10.0, build 0, NTLMSSP_REVISION_W2K3.
    private static readonly byte[] VersionBytes = [10, 0, 0, 0, 0, 0, 0, 15];

    private readonly string _domain;
    private readonly string _user;
    private string? _password;
    private readonly string _spn;
    private readonly Func<int, byte[]> _random;
    private readonly Func<DateTime> _now;

    private byte[]? _negotiate;
    private NtlmSealing? _clientSealing;
    private NtlmSealing? _serverSealing;
    private bool _disposed;

    internal NtlmV2Client(string domain, string username, string password, string servicePrincipalName = "",
        Func<int, byte[]>? random = null, Func<DateTime>? now = null)
    {
        _domain = domain;
        _user = username;
        _password = password;
        _spn = servicePrincipalName;
        _random = random ?? RandomNumberGenerator.GetBytes;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsComplete { get; private set; }

    /// <summary>The flags agreed in AUTHENTICATE (after <see cref="IsComplete" />).</summary>
    internal NtlmFlags NegotiatedFlags { get; private set; }

    internal NtlmSealing ClientSealing => _clientSealing ?? throw new InvalidOperationException("NTLM is not complete.");
    internal NtlmSealing ServerSealing => _serverSealing ?? throw new InvalidOperationException("NTLM is not complete.");

    public byte[] Step(byte[] incoming)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_negotiate is null)
        {
            if (incoming.Length != 0)
            {
                throw new RdpProtocolException("NTLM: the first leg takes no input.");
            }
            _negotiate = BuildNegotiate();
            return (byte[])_negotiate.Clone();
        }
        if (IsComplete)
        {
            throw new RdpProtocolException("NTLM: unexpected token after AUTHENTICATE.");
        }
        byte[] authenticate = ProcessChallenge(incoming);
        IsComplete = true;
        return authenticate;
    }

    public byte[] Seal(byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ClientSealing.Seal(plaintext);
    }

    public byte[] Unseal(byte[] wrapped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ServerSealing.Unseal(wrapped);
    }

    public void Dispose()
    {
        _disposed = true;
        _password = null;
        _clientSealing?.Dispose();
        _serverSealing?.Dispose();
    }

    // ---- NEGOTIATE (MS-NLMP 2.2.1.1) ----

    private static byte[] BuildNegotiate()
    {
        byte[] message = new byte[40];
        Signature.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), (uint)Requested);
        // DomainNameFields and WorkstationFields stay zero: neither OEM_*_SUPPLIED flag is set.
        VersionBytes.CopyTo(message, 32);
        return message;
    }

    // ---- CHALLENGE (2.2.1.2) → AUTHENTICATE (2.2.1.3) ----

    private byte[] ProcessChallenge(byte[] challenge)
    {
        string password = _password ?? throw new ObjectDisposedException(nameof(NtlmV2Client));
        if (challenge.Length < 48 || !challenge.AsSpan(0, 8).SequenceEqual(Signature)
            || BinaryPrimitives.ReadUInt32LittleEndian(challenge.AsSpan(8)) != 2)
        {
            throw new RdpProtocolException("NTLM: the server's CHALLENGE message is malformed.");
        }

        var serverFlags = (NtlmFlags)BinaryPrimitives.ReadUInt32LittleEndian(challenge.AsSpan(20));
        if ((serverFlags & Required) != Required)
        {
            throw new RdpProtocolException(
                $"NTLM: the server did not grant {Required & ~serverFlags} (it offered 0x{(uint)serverFlags:X8}); NLA needs NTLMv2 with signing and sealing.");
        }
        byte[] serverChallenge = challenge[24..32];
        byte[] targetInfo = Field(challenge, 40, "TargetInfo");
        List<(AvId Id, byte[] Value)> pairs = ParseAvPairs(targetInfo);
        if (!pairs.Exists(static p => p.Id == AvId.NbComputerName) || !pairs.Exists(static p => p.Id == AvId.NbDomainName))
        {
            throw new RdpProtocolException("NTLM: the server's target information lacks its NetBIOS computer or domain name.");
        }

        // A server timestamp means the server validates NTLMv2 freshness itself; then LmChallengeResponse is
        // Z(24) (3.1.5.1.2) and the client uses the server's time in the blob.
        (AvId Id, byte[] Value) timestamp = pairs.Find(static p => p.Id == AvId.Timestamp && p.Value.Length == 8);
        long time = timestamp.Value is { } ts ? BinaryPrimitives.ReadInt64LittleEndian(ts) : _now().ToFileTimeUtc();

        byte[] clientChallenge = _random(8);
        byte[] avPairs = BuildClientAvPairs(pairs);
        byte[] ntowf = NtowfV2(password, _user, _domain);
        try
        {
            (byte[] ntProof, byte[] ntResponse) = NtResponse(ntowf, serverChallenge, clientChallenge, time, avPairs);
            byte[] lmResponse = timestamp.Value is null ? LmV2Response(ntowf, serverChallenge, clientChallenge) : new byte[24];
            byte[] sessionBaseKey = HMACMD5.HashData(ntowf, ntProof);
            // NTLMv2: KeyExchangeKey = SessionBaseKey (3.4.5.1). With KEY_EXCH the exported key is random,
            // sent RC4-encrypted under KeyExchangeKey.
            byte[] exportedSessionKey = _random(16);
            byte[] encryptedSessionKey = Rc4.Apply(sessionBaseKey, exportedSessionKey);
            CryptographicOperations.ZeroMemory(sessionBaseKey);

            NegotiatedFlags = (serverFlags & Requested) & ~NtlmFlags.Oem;
            byte[] authenticate = BuildAuthenticate(lmResponse, ntResponse, encryptedSessionKey, NegotiatedFlags);

            // MIC over the three messages with the MIC field zero (3.1.5.1.2).
            using (var mic = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, exportedSessionKey))
            {
                mic.AppendData(_negotiate!);
                mic.AppendData(challenge);
                mic.AppendData(authenticate);
                mic.GetHashAndReset().CopyTo(authenticate, 72);
            }

            _clientSealing = new NtlmSealing(SignKey(exportedSessionKey, clientToServer: true), SealKey(exportedSessionKey, clientToServer: true));
            _serverSealing = new NtlmSealing(SignKey(exportedSessionKey, clientToServer: false), SealKey(exportedSessionKey, clientToServer: false));
            CryptographicOperations.ZeroMemory(exportedSessionKey);
            _password = null;
            return authenticate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ntowf);
        }
    }

    private byte[] BuildClientAvPairs(List<(AvId Id, byte[] Value)> serverPairs)
    {
        using var stream = new MemoryStream();
        foreach ((AvId id, byte[] value) in serverPairs)
        {
            // The client supplies its own flags, channel bindings and target name (3.1.5.1.2).
            if (id is AvId.Flags or AvId.ChannelBindings or AvId.TargetName)
            {
                continue;
            }
            WriteAvPair(stream, id, value);
        }
        byte[] flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, AvFlagMicPresent);
        WriteAvPair(stream, AvId.Flags, flags);
        // CredSSP binds the TLS channel itself through pubKeyAuth; no GSS channel bindings are supplied, which
        // MS-NLMP encodes as an all-zero MsvAvChannelBindings value.
        WriteAvPair(stream, AvId.ChannelBindings, new byte[16]);
        if (_spn.Length > 0)
        {
            WriteAvPair(stream, AvId.TargetName, Encoding.Unicode.GetBytes(_spn));
        }
        WriteAvPair(stream, AvId.Eol, []);
        return stream.ToArray();
    }

    private byte[] BuildAuthenticate(byte[] lmResponse, byte[] ntResponse, byte[] encryptedSessionKey, NtlmFlags flags)
    {
        byte[] domain = Encoding.Unicode.GetBytes(_domain);
        byte[] user = Encoding.Unicode.GetBytes(_user);
        byte[] workstation = [];   // Not supplied: the field is optional and NLA does not need it.
        const int Header = 88;     // through the MIC (2.2.1.3, VERSION present)
        byte[] message = new byte[Header + domain.Length + user.Length + workstation.Length + lmResponse.Length
            + ntResponse.Length + encryptedSessionKey.Length];
        Signature.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 3);
        int offset = Header;
        offset = PutField(message, 12, lmResponse, offset);
        offset = PutField(message, 20, ntResponse, offset);
        offset = PutField(message, 28, domain, offset);
        offset = PutField(message, 36, user, offset);
        offset = PutField(message, 44, workstation, offset);
        _ = PutField(message, 52, encryptedSessionKey, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(60), (uint)flags);
        VersionBytes.CopyTo(message, 64);
        return message;
    }

    private static int PutField(byte[] message, int fieldOffset, byte[] value, int payloadOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset), checked((ushort)value.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset + 2), checked((ushort)value.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(fieldOffset + 4), (uint)payloadOffset);
        value.CopyTo(message, payloadOffset);
        return payloadOffset + value.Length;
    }

    private static byte[] Field(byte[] message, int fieldOffset, string name)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(fieldOffset));
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(fieldOffset + 4));
        if (start > (uint)message.Length || length > message.Length - (int)start)
        {
            throw new RdpProtocolException($"NTLM: the {name} field points outside the message.");
        }
        return message.AsSpan((int)start, length).ToArray();
    }

    internal static List<(AvId Id, byte[] Value)> ParseAvPairs(byte[] targetInfo)
    {
        var pairs = new List<(AvId, byte[])>();
        ReadOnlySpan<byte> rest = targetInfo;
        while (true)
        {
            if (rest.Length < 4)
            {
                throw new RdpProtocolException("NTLM: the target information is not terminated.");
            }
            var id = (AvId)BinaryPrimitives.ReadUInt16LittleEndian(rest);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..]);
            if (length > rest.Length - 4)
            {
                throw new RdpProtocolException("NTLM: a target information entry overruns the message.");
            }
            if (id == AvId.Eol)
            {
                return pairs;
            }
            pairs.Add((id, rest.Slice(4, length).ToArray()));
            rest = rest[(4 + length)..];
        }
    }

    private static void WriteAvPair(MemoryStream stream, AvId id, byte[] value)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)id);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)value.Length));
        stream.Write(header);
        stream.Write(value);
    }

    // ---- Cryptographic primitives (MS-NLMP 3.3.2, 3.4.5) ----

    /// <summary>NTOWFv1: MD4 of the UTF-16LE password (3.3.1).</summary>
    internal static byte[] NtowfV1(string password)
    {
        byte[] secret = Encoding.Unicode.GetBytes(password);
        try
        {
            var md4 = new MD4Digest();
            md4.BlockUpdate(secret, 0, secret.Length);
            byte[] hash = new byte[16];
            _ = md4.DoFinal(hash, 0);
            return hash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>NTOWFv2: HMAC_MD5(NTOWFv1, UNICODE(Uppercase(user) + domain)) (3.3.2).</summary>
    internal static byte[] NtowfV2(string password, string user, string domain)
    {
        byte[] v1 = NtowfV1(password);
        try
        {
            return HMACMD5.HashData(v1, Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(v1);
        }
    }

    /// <summary>
    /// NTProofStr and NtChallengeResponse (3.3.2): temp = 0x01 0x01 Z(6) Time ClientChallenge Z(4) AvPairs Z(4).
    /// </summary>
    internal static (byte[] NtProof, byte[] Response) NtResponse(byte[] ntowfV2, byte[] serverChallenge, byte[] clientChallenge,
        long fileTime, byte[] avPairs)
    {
        byte[] temp = new byte[28 + avPairs.Length + 4];
        temp[0] = 1;
        temp[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(temp.AsSpan(8), fileTime);
        clientChallenge.CopyTo(temp, 16);
        avPairs.CopyTo(temp, 28);
        byte[] ntProof = HMACMD5.HashData(ntowfV2, (byte[])[.. serverChallenge, .. temp]);
        return (ntProof, [.. ntProof, .. temp]);
    }

    /// <summary>LMv2 response: HMAC_MD5(NTOWFv2, ServerChallenge ‖ ClientChallenge) ‖ ClientChallenge (3.3.2).</summary>
    internal static byte[] LmV2Response(byte[] ntowfV2, byte[] serverChallenge, byte[] clientChallenge) =>
        [.. HMACMD5.HashData(ntowfV2, (byte[])[.. serverChallenge, .. clientChallenge]), .. clientChallenge];

    /// <summary>SIGNKEY with extended session security (3.4.5.2).</summary>
    internal static byte[] SignKey(byte[] exportedSessionKey, bool clientToServer) => MD5.HashData([.. exportedSessionKey,
        .. Encoding.ASCII.GetBytes(clientToServer
            ? "session key to client-to-server signing key magic constant\0"
            : "session key to server-to-client signing key magic constant\0")]);

    /// <summary>SEALKEY with extended session security and 128-bit keys (3.4.5.3).</summary>
    internal static byte[] SealKey(byte[] exportedSessionKey, bool clientToServer) => MD5.HashData([.. exportedSessionKey,
        .. Encoding.ASCII.GetBytes(clientToServer
            ? "session key to client-to-server sealing key magic constant\0"
            : "session key to server-to-client sealing key magic constant\0")]);
}

/// <summary>
/// One direction of NTLM message security with extended session security and key exchange (MS-NLMP 3.4.3, 3.4.4):
/// a persistent RC4 handle, a signing key and a sequence number. Tokens are laid out as CredSSP carries them:
/// the 16-byte NTLMSSP_MESSAGE_SIGNATURE followed by the sealed message.
/// </summary>
internal sealed class NtlmSealing(byte[] signingKey, byte[] sealingKey) : IDisposable
{
    private Rc4 _handle = new(sealingKey);

    internal uint SequenceNumber { get; private set; }

    /// <summary>SEAL: Sealed = RC4(Handle, Message); Signature = MAC(Handle, SigningKey, SeqNum, Message).</summary>
    internal byte[] Seal(ReadOnlySpan<byte> message)
    {
        byte[] output = new byte[16 + message.Length];
        _handle.Transform(message, output.AsSpan(16));
        Mac(message, output.AsSpan(0, 16));
        return output;
    }

    /// <summary>UNSEAL: Message = RC4(Handle, Sealed), then the signature must equal MAC over the message.</summary>
    internal byte[] Unseal(ReadOnlySpan<byte> token)
    {
        if (token.Length < 16)
        {
            throw new RdpProtocolException("NLA unwrap failed (InvalidToken).");
        }
        byte[] message = new byte[token.Length - 16];
        _handle.Transform(token[16..], message);
        Span<byte> expected = stackalloc byte[16];
        Mac(message, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, token[..16]))
        {
            CryptographicOperations.ZeroMemory(message);
            throw new RdpProtocolException("NLA unwrap failed (MessageAltered).");
        }
        return message;
    }

    /// <summary>A signature without sealing (GSS_GetMIC), e.g. the SPNEGO mechListMIC.</summary>
    internal byte[] Sign(ReadOnlySpan<byte> message)
    {
        byte[] signature = new byte[16];
        Mac(message, signature);
        return signature;
    }

    /// <summary>Checks a GSS_GetMIC signature from the peer (GSS_VerifyMIC).</summary>
    internal bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 16)
        {
            return false;
        }
        Span<byte> expected = stackalloc byte[16];
        Mac(message, expected);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    /// <summary>
    /// Runs <paramref name="action" /> and then puts the RC4 handle back where it was; the sequence number keeps the
    /// increment. This is MS-SPNG 3.2.5.1 ("NTLM RC4 Key State for MechListMIC and First Signed Message"): the
    /// mechListMIC and the first application message use the same RC4 key state.
    /// </summary>
    internal T WithRc4StatePreserved<T>(Func<NtlmSealing, T> action)
    {
        Rc4 saved = _handle.Clone();
        try
        {
            return action(this);
        }
        finally
        {
            _handle.Dispose();
            _handle = saved;
        }
    }

    // MAC with extended session security and key exchange (3.4.4.2):
    // Version = 1, Checksum = RC4(Handle, HMAC_MD5(SigningKey, SeqNum ‖ Message)[0..7]), SeqNum; then SeqNum++.
    private void Mac(ReadOnlySpan<byte> message, Span<byte> signature)
    {
        Span<byte> sequence = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sequence, SequenceNumber);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, signingKey);
        hmac.AppendData(sequence);
        hmac.AppendData(message);
        Span<byte> digest = stackalloc byte[16];
        _ = hmac.GetHashAndReset(digest);
        BinaryPrimitives.WriteUInt32LittleEndian(signature, 1);
        _handle.Transform(digest[..8], signature.Slice(4, 8));
        sequence.CopyTo(signature[12..]);
        SequenceNumber++;
    }

    public void Dispose()
    {
        _handle.Dispose();
        CryptographicOperations.ZeroMemory(signingKey);
        CryptographicOperations.ZeroMemory(sealingKey);
    }
}

/// <summary>
/// RC4 as MS-NLMP uses it (RC4K / RC4Init / RC4). Kept here rather than taken from a library because NTLM under
/// SPNEGO needs to snapshot and restore the cipher state (<see cref="Clone" />).
/// </summary>
internal sealed class Rc4 : IDisposable
{
    private readonly byte[] _state = new byte[256];
    private int _i, _j;

    internal Rc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("RC4 needs a key.", nameof(key));
        }
        for (int n = 0; n < 256; n++)
        {
            _state[n] = (byte)n;
        }
        for (int n = 0, j = 0; n < 256; n++)
        {
            j = (j + _state[n] + key[n % key.Length]) & 0xFF;
            (_state[n], _state[j]) = (_state[j], _state[n]);
        }
    }

    private Rc4(Rc4 other)
    {
        other._state.CopyTo(_state, 0);
        _i = other._i;
        _j = other._j;
    }

    internal Rc4 Clone() => new(this);

    internal void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (int n = 0; n < input.Length; n++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _state[_i]) & 0xFF;
            (_state[_i], _state[_j]) = (_state[_j], _state[_i]);
            output[n] = (byte)(input[n] ^ _state[(_state[_i] + _state[_j]) & 0xFF]);
        }
    }

    /// <summary>RC4K(key, message): a one-shot cipher with a fresh state.</summary>
    internal static byte[] Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        using var rc4 = new Rc4(key);
        byte[] output = new byte[message.Length];
        rc4.Transform(message, output);
        return output;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_state);
        _i = _j = 0;
    }
}
