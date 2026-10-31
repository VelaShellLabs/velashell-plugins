using System.Buffers;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Security;

/// <summary>
/// CredSSP / NLA (MS-CSSP) over the established TLS channel, for servers that select
/// PROTOCOL_HYBRID (Windows with Network Level Authentication). NTLM and message sealing
/// come from <see cref="NegotiateAuthentication"/>; the TSRequest envelope and the public-key
/// binding (echo for peer version &lt; 5, SHA-256 hash for &gt;= 5) follow MS-CSSP and mirror
/// FreeRDP's nla.c. Credentials are sent only after the server's public-key echo verifies.
/// </summary>
internal static class CredSsp
{
    private const uint ClientVersion = 6;
    private static readonly byte[] ClientServerHashMagic =
        "CredSSP Client-To-Server Binding Hash\0"u8.ToArray();
    private static readonly byte[] ServerClientHashMagic =
        "CredSSP Server-To-Client Binding Hash\0"u8.ToArray();

    internal static async Task AuthenticateAsync(SslStream tls, string targetHost, string domain, string username,
        string password, CancellationToken ct)
    {
        byte[] publicKey = ServerPublicKey(tls);
        byte[] nonce = RandomNumberGenerator.GetBytes(32);

        using var auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "NTLM",
            Credential = new NetworkCredential(username, password, domain),
            TargetName = $"TERMSRV/{targetHost}",
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign
        });

        // Leg 1: client NTLM NEGOTIATE.
        byte[]? token = auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out NegotiateAuthenticationStatusCode status);
        if (status is not (NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed))
            throw new RdpProtocolException($"NLA negotiation failed to start ({status}).");
        await WriteAsync(tls, BuildTsRequest(ClientVersion, token, null, null, null), ct).ConfigureAwait(false);

        // Leg 2: server NTLM CHALLENGE.
        TsRequest challenge = ParseTsRequest(await ReadDerAsync(tls, ct).ConfigureAwait(false));
        uint peerVersion = challenge.Version;

        // Leg 3: client NTLM AUTHENTICATE + pubKeyAuth (+ clientNonce for v5+).
        token = auth.GetOutgoingBlob(challenge.NegoToken ?? [], out status);
        if (status != NegotiateAuthenticationStatusCode.Completed)
            throw new ProtocolAuthFailure($"NLA authentication did not complete ({status}).");

        byte[] pubKeyAuth = peerVersion < 5
            ? Seal(auth, publicKey)
            : Seal(auth, BindingHash(ClientServerHashMagic, nonce, publicKey));
        byte[]? sentNonce = peerVersion < 5 ? null : nonce;
        await WriteAsync(tls, BuildTsRequest(ClientVersion, token, null, pubKeyAuth, sentNonce), ct).ConfigureAwait(false);

        // Leg 4: server public-key echo — verify before sending credentials.
        TsRequest echo = ParseTsRequest(await ReadDerAsync(tls, ct).ConfigureAwait(false));
        if (echo.ErrorCode is { } error && error != 0)
            throw new ProtocolAuthFailure($"The server rejected NLA (0x{error:X8}).");
        if (echo.PubKeyAuth is null) throw new RdpProtocolException("NLA public-key echo missing.");
        byte[] serverEcho = Unseal(auth, echo.PubKeyAuth);
        VerifyServerEcho(peerVersion, serverEcho, publicKey, nonce);

        // Leg 5: send the credentials, sealed.
        byte[] credentials = Seal(auth, BuildTsCredentials(domain, username, password));
        await WriteAsync(tls, BuildTsRequest(ClientVersion, null, credentials, null, null), ct).ConfigureAwait(false);
    }

    private static void VerifyServerEcho(uint peerVersion, byte[] serverEcho, byte[] publicKey, byte[] nonce)
    {
        if (peerVersion < 5)
        {
            // The server returns the public key with the first byte incremented (little-endian +1).
            byte[] expected = (byte[])publicKey.Clone();
            for (int i = 0; i < expected.Length; i++) { if (++expected[i] != 0) break; }
            if (!CryptographicOperations.FixedTimeEquals(serverEcho, expected))
                throw new ProtocolAuthFailure("The server's public-key echo did not match (possible MITM).");
        }
        else
        {
            byte[] expected = BindingHash(ServerClientHashMagic, nonce, publicKey);
            if (!CryptographicOperations.FixedTimeEquals(serverEcho, expected))
                throw new ProtocolAuthFailure("The server's public-key hash did not match (possible MITM).");
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
        using var cert = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
        using RSA? rsa = cert.GetRSAPublicKey();
        if (rsa is null) throw new RdpProtocolException("NLA requires an RSA server certificate.");
        return rsa.ExportRSAPublicKey();   // matches OpenSSL i2d_PublicKey (PKCS#1 RSAPublicKey)
    }

    private static byte[] Seal(NegotiateAuthentication auth, byte[] data)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = auth.Wrap(data, output, requestEncryption: true, out _);
        if (status != NegotiateAuthenticationStatusCode.Completed) throw new RdpProtocolException($"NLA wrap failed ({status}).");
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Unseal(NegotiateAuthentication auth, byte[] data)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = auth.Unwrap(data, output, out _);
        if (status != NegotiateAuthenticationStatusCode.Completed) throw new RdpProtocolException($"NLA unwrap failed ({status}).");
        return output.WrittenSpan.ToArray();
    }

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
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteInteger(version);
            if (negoToken is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (w.PushSequence())           // NegoData ::= SEQUENCE OF
                using (w.PushSequence())           // SEQUENCE { negoToken [0] OCTET STRING }
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    w.WriteOctetString(negoToken);
            }
            if (authInfo is not null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2))) w.WriteOctetString(authInfo);
            if (pubKeyAuth is not null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3))) w.WriteOctetString(pubKeyAuth);
            if (clientNonce is not null)
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 5))) w.WriteOctetString(clientNonce);
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
            if (tag.TagClass != TagClass.ContextSpecific) { seq.ReadEncodedValue(); continue; }
            AsnReader inner = seq.ReadSequence(tag);
            switch (tag.TagValue)
            {
                case 0:
                    version = (uint)inner.ReadInteger();
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
                    inner.ReadOctetString(); // authInfo (server never sends to client)
                    break;
                case 3:
                    pubKeyAuth = inner.ReadOctetString();
                    break;
                case 4:
                    errorCode = (uint)inner.ReadInteger();
                    break;
                case 5:
                    inner.ReadOctetString(); // clientNonce echoed (unused on the client)
                    break;
            }
        }
        return new TsRequest(version, negoToken, pubKeyAuth, errorCode);
    }

    private static byte[] BuildTsCredentials(string domain, string username, string password)
    {
        var creds = new AsnWriter(AsnEncodingRules.DER);
        using (creds.PushSequence())   // TSPasswordCreds
        {
            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) creds.WriteOctetString(Encoding.Unicode.GetBytes(domain));
            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1))) creds.WriteOctetString(Encoding.Unicode.GetBytes(username));
            using (creds.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2))) creds.WriteOctetString(Encoding.Unicode.GetBytes(password));
        }
        byte[] passwordCreds = creds.Encode();

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())       // TSCredentials
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteInteger(1); // credType = password
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1))) w.WriteOctetString(passwordCreds);
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
        if (header[0] != 0x30) throw new RdpProtocolException("Expected a DER SEQUENCE in the NLA exchange.");
        var prefix = new List<byte> { header[0], header[1] };
        int contentLength;
        if ((header[1] & 0x80) == 0)
        {
            contentLength = header[1];
        }
        else
        {
            int count = header[1] & 0x7F;
            if (count is < 1 or > 4) throw new RdpProtocolException("Unsupported DER length in NLA exchange.");
            byte[] lengthBytes = await ReadExactAsync(tls, count, ct).ConfigureAwait(false);
            prefix.AddRange(lengthBytes);
            contentLength = 0;
            foreach (byte b in lengthBytes) contentLength = (contentLength << 8) | b;
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
            if (next == 0) throw new RdpProtocolException("The server closed the connection during NLA.");
            read += next;
        }
        return buffer;
    }
}

/// <summary>NLA rejected the supplied credentials.</summary>
internal sealed class ProtocolAuthFailure(string message) : Exception(message);
