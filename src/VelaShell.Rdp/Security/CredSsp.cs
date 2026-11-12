using System.Formats.Asn1;
using System.Net.Security;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VelaShell.Rdp.Security;

/// <summary>
/// CredSSP / NLA (MS-CSSP) over the established TLS channel, for servers that select
/// PROTOCOL_HYBRID (Windows with Network Level Authentication). The authentication tokens and message
/// sealing come from an <see cref="INlaSecurityContext"/> (SSPI on Windows, .NET SPNEGO on Unix).
/// The TSRequest envelope and nonce-bound SHA-256 public-key binding follow MS-CSSP.
/// Credentials are sent only after the server's public-key echo verifies.
/// </summary>
internal static class CredSsp
{
    private const uint ClientVersion = 6;
    /// <summary>Oldest server CredSSP version accepted: v5 added the nonce-bound public-key hash (CVE-2018-0886).</summary>
    internal const uint MinPeerVersion = 5;
    /// <summary>Largest TSRequest accepted; real ones are a few KB (tokens + sealed credentials).</summary>
    private const int MaxTsRequest = 1024 * 1024;
    private static readonly byte[] ClientServerHashMagic =
        "CredSSP Client-To-Server Binding Hash\0"u8.ToArray();
    private static readonly byte[] ServerClientHashMagic =
        "CredSSP Server-To-Client Binding Hash\0"u8.ToArray();

    internal static async Task AuthenticateAsync(SslStream tls, string targetHost, string domain, string username,
        string password, CancellationToken ct)
    {
        using INlaSecurityContext auth = NlaSecurityContext.Create(domain, username, password, targetHost);
        await AuthenticateAsync(tls, domain, username, password, auth, ct).ConfigureAwait(false);
    }

    internal static async Task AuthenticateAsync(SslStream tls, string domain, string username,
        string password, INlaSecurityContext auth, CancellationToken ct)
    {
        byte[] publicKey = ServerPublicKey(tls);
        byte[] nonce = RandomNumberGenerator.GetBytes(32);

        // Negotiation loop: exchange security-package tokens (one round for NTLM, possibly more for SPNEGO)
        // until the context is established; only then is pubKeyAuth attached. The 32-byte clientNonce rides on
        // every client TSRequest (v5+, MS-CSSP TSRequest.clientNonce), as mstsc sends it.
        byte[] outToken = auth.Step([]);
        string authInfo = DescribeAuth(auth, outToken);
        bool canRetryWithoutNla = !auth.IsComplete && IsNtlmNegotiate(outToken);
        if (!auth.IsComplete)
            await WriteAsync(tls, BuildTsRequest(ClientVersion, Nz(outToken), null, null, nonce), ct).ConfigureAwait(false);

        uint peerVersion = ClientVersion;
        bool sawReply = false;
        while (!auth.IsComplete)
        {
            // Only an NTLM Type 1 is known to contain no credential proof. A Kerberos AP-REQ
            // can authenticate in the very first token, so its refusal must never trigger TLS fallback.
            byte[] replyBytes;
            try
            {
                replyBytes = await ReadDerAsync(tls, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (canRetryWithoutNla && !sawReply && ex is IOException or RdpProtocolException)
            {
                // A server that restricts incoming NTLM aborts here — closing the connection or sending a fatal TLS
                // alert (SChannel's internal_error). Under TLS 1.3 the same alert is also what a broken TLS 1.3 path
                // produces, so RdpManagedSession retries with TLS 1.2 before treating it as a policy refusal.
                throw new RdpNlaRefusedException(null, ex, auth: authInfo);
            }

            TsRequest reply = ParseTsRequest(replyBytes);
            if (reply.ErrorCode is { } refused && refused != 0)
            {
                throw sawReply || !canRetryWithoutNla
                    ? new RdpAuthenticationException($"The server rejected NLA (0x{refused:X8}).", refused)
                    : new RdpNlaRefusedException(refused, null, auth: authInfo);
            }

            peerVersion = reply.Version;
            sawReply = true;
            if (peerVersion < MinPeerVersion)
            {
                // Versions before 5 bind the raw TLS key with no nonce — the CVE-2018-0886 relay oracle. mstsc's
                // default ("Mitigated") refuses them too. Not an NlaRefused: falling back to TLS-only here would
                // turn a downgrade attempt into a successful downgrade.
                throw new RdpProtocolException(
                    $"The server speaks CredSSP v{peerVersion}, which predates the CVE-2018-0886 fix (v{MinPeerVersion}+ required). Update the server.");
            }
            outToken = auth.Step(reply.NegoToken ?? []);
            if (!auth.IsComplete)
            {
                await WriteAsync(tls, BuildTsRequest(ClientVersion, Nz(outToken), null, null, nonce), ct).ConfigureAwait(false);
            }
        }

        // A context that completes immediately sends its token only once, with the v5+ binding.
        byte[] binding = BindingHash(ClientServerHashMagic, nonce, publicKey);
        byte[] pubKeyAuth = auth.Seal(binding);
        await WriteAsync(tls, BuildTsRequest(ClientVersion, Nz(outToken), null, pubKeyAuth, nonce), ct).ConfigureAwait(false);

        // Server public-key echo — verify before sending credentials. A rejection here (e.g. a wrong password,
        // which the server validates during NLA) is an authentication failure.
        TsRequest echo = ParseTsRequest(await ReadDerAsync(tls, ct).ConfigureAwait(false));
        if (echo.ErrorCode is { } error && error != 0)
        {
            throw new RdpAuthenticationException($"The server rejected NLA (0x{error:X8}).", error);
        }

        if (echo.Version < MinPeerVersion)
            throw new RdpProtocolException($"The server speaks CredSSP v{echo.Version}; v{MinPeerVersion}+ is required.");

        if (echo.PubKeyAuth is null)
        {
            throw new RdpProtocolException("NLA public-key echo missing.");
        }

        byte[] serverEcho = auth.Unseal(echo.PubKeyAuth);
        VerifyServerEcho(peerVersion, serverEcho, publicKey, nonce);

        // Finally send the credentials, sealed.
        // Plaintext copies of the password are wiped as soon as they are sealed (best effort: managed strings stay).
        byte[] plain = BuildTsCredentials(domain, username, password);
        byte[] credentials;
        try { credentials = auth.Seal(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await WriteAsync(tls, BuildTsRequest(ClientVersion, null, credentials, null, nonce), ct).ConfigureAwait(false);
    }

    private static byte[]? Nz(byte[] token) => token.Length == 0 ? null : token;

    /// <summary>
    /// The TLS stack itself failed: a fatal alert from the peer or a record that would not decrypt or verify.
    /// <see cref="SslStream" /> reports these as an <see cref="IOException" /> wrapping the platform's error
    /// (SChannel <see cref="System.ComponentModel.Win32Exception" />, OpenSSL or Apple security exceptions), whereas
    /// a transport failure wraps a <see cref="System.Net.Sockets.SocketException" /> and an unexpected end of
    /// stream wraps nothing. The test is on that shape, not on platform-specific exception types, so it classifies
    /// the same way on every OS.
    /// </summary>
    internal static bool IsTlsLayerFailure(Exception? ex)
    {
        bool tls = false;
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Net.Sockets.SocketException:
                    return false;
                case System.Security.Authentication.AuthenticationException:
                    tls = true;
                    break;
                case IOException { InnerException: { } inner } when inner is not IOException:
                    tls = true;
                    break;
            }
        }
        return tls;
    }

    // Fail closed: only recognize a bare Type 1, or a SPNEGO offer containing NTLM alone.
    internal static bool IsNtlmNegotiate(byte[] token)
    {
        static bool IsType1(byte[] value) => value.Length >= 32 &&
            value.AsSpan(0, 12).SequenceEqual("NTLMSSP\0\u0001\0\0\0"u8);

        if (IsType1(token)) return true;
        try
        {
            var reader = new AsnReader(token, AsnEncodingRules.DER);
            AsnReader gss = reader.ReadSequence(new Asn1Tag(TagClass.Application, 0));
            if (gss.ReadObjectIdentifier() != "1.3.6.1.5.5.2") return false;
            AsnReader initWrapper = gss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            AsnReader init = initWrapper.ReadSequence();
            AsnReader mechsWrapper = init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            AsnReader mechs = mechsWrapper.ReadSequence();
            if (mechs.ReadObjectIdentifier() != "1.3.6.1.4.1.311.2.2.10") return false;
            mechs.ThrowIfNotEmpty();
            mechsWrapper.ThrowIfNotEmpty();
            AsnReader inner = init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2));
            bool isType1 = IsType1(inner.ReadOctetString());
            inner.ThrowIfNotEmpty();
            init.ThrowIfNotEmpty();
            initWrapper.ThrowIfNotEmpty();
            gss.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            return isType1;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Names the provider and first token's envelope without logging credential material.
    /// </summary>
    private static string DescribeAuth(INlaSecurityContext auth, byte[] token)
    {
        string provider = auth is SspiNegotiate ? "native SSPI" : ".NET Negotiate";
        string shape = token.Length == 0 ? "empty"
            : token.Length >= 8 && token.AsSpan(0, 7).SequenceEqual("NTLMSSP"u8) ? "bare NTLM"
            : token[0] == 0x60 ? "SPNEGO"
            : token[0] == 0xA1 ? "SPNEGO-resp"
            : $"0x{token[0]:X2}";
        return $"NLA via {provider}, first token {shape} {token.Length}B";
    }

    private static void VerifyServerEcho(uint peerVersion, byte[] serverEcho, byte[] publicKey, byte[] nonce)
    {
        if (peerVersion < 5)
        {
            // The server returns the public key with the first byte incremented (little-endian +1).
            byte[] expected = (byte[])publicKey.Clone();
            for (int i = 0; i < expected.Length; i++)
            {
                if (++expected[i] != 0)
                {
                    break;
                }
            }
            if (!CryptographicOperations.FixedTimeEquals(serverEcho, expected))
            {
                throw new RdpProtocolException("The server's public-key echo did not match (possible MITM).");
            }
        }
        else
        {
            byte[] expected = BindingHash(ServerClientHashMagic, nonce, publicKey);
            if (!CryptographicOperations.FixedTimeEquals(serverEcho, expected))
            {
                throw new RdpProtocolException("The server's public-key hash did not match (possible MITM).");
            }
        }
    }

    private static byte[] BindingHash(byte[] magic, byte[] nonce, byte[] publicKey)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(magic);        // magic already includes its trailing NUL
        sha.AppendData(nonce);
        sha.AppendData(publicKey);
        return sha.GetHashAndReset();
    }

    private static byte[] ServerPublicKey(SslStream tls)
    {
        X509Certificate remote = tls.RemoteCertificate ?? throw new RdpProtocolException("No server certificate for NLA.");
        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
        return ServerPublicKey(cert);
    }

    /// <summary>
    /// The key bound by pubKeyAuth (MS-CSSP 3.1.5): the contents of the certificate's SubjectPublicKeyInfo
    /// subjectPublicKey BIT STRING, taken verbatim. For RSA that is the PKCS#1 RSAPublicKey; for EC the encoded
    /// point. Using the certificate's own bytes rather than re-encoding the key supports every key type and
    /// cannot differ from what the server hashes.
    /// </summary>
    internal static byte[] ServerPublicKey(X509Certificate2 cert) => cert.PublicKey.EncodedKeyValue.RawData;

    // ---- Offline test hooks (NLA needs a live Windows server; these validate the ASN.1) ----
    internal static (uint Version, byte[]? Nego, byte[]? PubKeyAuth, uint? Error) RoundTripForTest(
        uint version, byte[]? nego, byte[]? authInfo, byte[]? pubKeyAuth, byte[]? nonce)
    {
        TsRequest r = ParseTsRequest(BuildTsRequest(version, nego, authInfo, pubKeyAuth, nonce));
        return (r.Version, r.NegoToken, r.PubKeyAuth, r.ErrorCode);
    }

    internal static byte[] BuildCredentialsForTest(string domain, string username, string password) =>
        BuildTsCredentials(domain, username, password);

    internal static byte[] BindingHashForTest(bool clientToServer, byte[] nonce, byte[] publicKey) =>
        BindingHash(clientToServer ? ClientServerHashMagic : ServerClientHashMagic, nonce, publicKey);

    // ---- TSRequest (ASN.1 DER) ----

    private readonly record struct TsRequest(uint Version, byte[]? NegoToken, byte[]? PubKeyAuth, uint? ErrorCode);

    private static byte[] BuildTsRequest(uint version, byte[]? negoToken, byte[]? authInfo, byte[]? pubKeyAuth, byte[]? clientNonce)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteInteger(version);
            }

            if (negoToken is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (w.PushSequence())           // NegoData ::= SEQUENCE OF
                using (w.PushSequence())           // SEQUENCE { negoToken [0] OCTET STRING }
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    w.WriteOctetString(negoToken);
                }
            }
            if (authInfo is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    w.WriteOctetString(authInfo);
                }
            }

            if (pubKeyAuth is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                {
                    w.WriteOctetString(pubKeyAuth);
                }
            }

            if (clientNonce is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 5)))
                {
                    w.WriteOctetString(clientNonce);
                }
            }
        }
        return w.Encode();
    }

    private static TsRequest ParseTsRequest(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        AsnReader seq = reader.ReadSequence();
        uint version = 0;
        byte[]? negoToken = null, pubKeyAuth = null;
        uint? errorCode = null;
        while (seq.HasData)
        {
            Asn1Tag tag = seq.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific) { _ = seq.ReadEncodedValue(); continue; }
            AsnReader inner = seq.ReadSequence(tag);
            switch (tag.TagValue)
            {
                case 0:
                    version = ToUInt32(inner.ReadInteger());
                    break;
                case 1:
                    AsnReader negoData = inner.ReadSequence();   // SEQUENCE OF
                    if (negoData.HasData)
                    {
                        AsnReader item = negoData.ReadSequence(); // SEQUENCE { negoToken [0] }
                        AsnReader tokenCtx = item.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                        negoToken = tokenCtx.ReadOctetString();
                    }
                    break;
                case 2:
                    _ = inner.ReadOctetString(); // authInfo (server never sends to client)
                    break;
                case 3:
                    pubKeyAuth = inner.ReadOctetString();
                    break;
                case 4:
                    errorCode = ToUInt32(inner.ReadInteger());
                    break;
                case 5:
                    _ = inner.ReadOctetString(); // clientNonce echoed (unused on the client)
                    break;
            }
        }
        return new TsRequest(version, negoToken, pubKeyAuth, errorCode);
    }

    /// <summary>
    /// Maps an ASN.1 INTEGER onto a 32-bit code. NTSTATUS/HRESULT error codes have the top bit set, and
    /// servers encode them as <b>signed</b> INTEGERs — STATUS_LOGON_FAILURE 0xC000006D arrives as
    /// -1073741715 — so a direct <c>(uint)</c> conversion of the <see cref="BigInteger" /> throws
    /// <see cref="OverflowException" />, masking the very error the server is reporting.
    /// </summary>
#pragma warning disable IDE0078 // 使用模式匹配
    private static uint ToUInt32(BigInteger value) =>
        // Plain comparisons, not a relational pattern: BigInteger bounds are not constants (CS9135).
        value >= int.MinValue && value <= uint.MaxValue
            ? unchecked((uint)(long)value)
            : throw new RdpProtocolException("An NLA integer is outside the 32-bit range.");
#pragma warning restore IDE0078 // 使用模式匹配

    internal static (uint Version, uint? Error) ParseForTest(byte[] der)
    {
        TsRequest r = ParseTsRequest(der);
        return (r.Version, r.ErrorCode);
    }

    private static byte[] BuildTsCredentials(string domain, string username, string password)
    {
        var creds = new AsnWriter(AsnEncodingRules.DER);
        using (creds.PushSequence())   // TSPasswordCreds
        {
            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                creds.WriteOctetString(Encoding.Unicode.GetBytes(domain));
            }

            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                creds.WriteOctetString(Encoding.Unicode.GetBytes(username));
            }

            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                byte[] secret = Encoding.Unicode.GetBytes(password);
                creds.WriteOctetString(secret);
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        byte[] passwordCreds = creds.Encode();

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())       // TSCredentials
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteInteger(1); // credType = password
            }

            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                w.WriteOctetString(passwordCreds);
                CryptographicOperations.ZeroMemory(passwordCreds);
            }
        }
        return w.Encode();
    }

    // ---- Framing ----

    private static async Task WriteAsync(SslStream tls, byte[] der, CancellationToken ct)
    {
        await tls.WriteAsync(der, ct).ConfigureAwait(false);
        await tls.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads exactly one DER SEQUENCE (TSRequest) from the TLS stream and returns the whole element.</summary>
    private static async Task<byte[]> ReadDerAsync(SslStream tls, CancellationToken ct)
    {
        byte[] header = await ReadExactAsync(tls, 2, ct).ConfigureAwait(false);
        if (header[0] != 0x30)
        {
            throw new RdpProtocolException("Expected a DER SEQUENCE in the NLA exchange.");
        }

        var prefix = new List<byte> { header[0], header[1] };
        int contentLength;
        if ((header[1] & 0x80) == 0)
        {
            contentLength = header[1];
        }
        else
        {
            int count = header[1] & 0x7F;
            if (count is < 1 or > 4)
            {
                throw new RdpProtocolException("Unsupported DER length in NLA exchange.");
            }

            byte[] lengthBytes = await ReadExactAsync(tls, count, ct).ConfigureAwait(false);
            prefix.AddRange(lengthBytes);
            long length = 0;
            foreach (byte b in lengthBytes)
            {
                length = (length << 8) | b;
            }

            // The length is the server's word: cap it before allocating (it used to allocate up to 2 GB, or throw
            // an OverflowException for a negative int).
            if (length > MaxTsRequest)
            {
                throw new RdpProtocolException("NLA message too large.");
            }

            contentLength = (int)length;
        }
        byte[] content = await ReadExactAsync(tls, contentLength, ct).ConfigureAwait(false);
        byte[] element = new byte[prefix.Count + content.Length];
        prefix.CopyTo(element);
        content.CopyTo(element, prefix.Count);
        return element;
    }

    private static async Task<byte[]> ReadExactAsync(SslStream tls, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int next = await tls.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (next == 0)
            {
                throw new RdpProtocolException("The server closed the connection during NLA.");
            }

            read += next;
        }
        return buffer;
    }
}
