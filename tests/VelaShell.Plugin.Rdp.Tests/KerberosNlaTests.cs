using System.Buffers;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class KerberosNlaTests
{
    // scripts/Test-RdpKerberos.sh supplies an isolated KDC, realm, credentials and service keytab.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnixSpnegoKerberosCompletesCredSspWithoutNtlm(bool useUpn)
    {
        string? principal = Environment.GetEnvironmentVariable("VELA_RDP_TEST_KRB5_USER");
        string? password = Environment.GetEnvironmentVariable("VELA_RDP_TEST_KRB5_PASSWORD");
        string? host = Environment.GetEnvironmentVariable("VELA_RDP_TEST_KRB5_HOST");
        if (OperatingSystem.IsWindows() || principal is null || password is null || host is null)
        {
            Assert.Inconclusive("Requires Unix with the isolated Kerberos test realm and service keytab.");
            return;
        }
        string[] parts = principal.Split('@');
        Assert.HasCount(2, parts);
        string domain = useUpn ? "" : parts[1];
        string username = useUpn ? principal : parts[0];
        using INlaSecurityContext client = NlaSecurityContext.Create(domain, username, password, host);
        using var server = new NegotiateAuthentication(new NegotiateAuthenticationServerOptions
        {
            Package = "Negotiate",
            Credential = CredentialCache.DefaultNetworkCredentials,
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign
        });
        await CredSspNegotiationTests.RunAsync(async tls =>
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(tls.LocalCertificate!.GetRawCertData());
            using RSA rsa = certificate.GetRSAPublicKey()!;
            byte[] publicKey = rsa.ExportRSAPublicKey();
            for (int round = 0; round < 8; round++)
            {
                byte[] packet = await FakeRdpConnection.ReadDerAsync(tls);
                Dictionary<int, byte[]> request = ReadRequest(packet);
                byte[]? outgoing = null;
                if (request.TryGetValue(1, out byte[]? token))
                {
                    Assert.IsFalse(CredSsp.IsNtlmNegotiate(token), "This test must never silently fall back to NTLM.");
                    outgoing = server.GetOutgoingBlob(token, out NegotiateAuthenticationStatusCode status);
                    Assert.IsTrue(status is NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded,
                        $"Kerberos server rejected the token: {status}");
                }
                if (request.TryGetValue(3, out byte[]? binding))
                {
                    Assert.IsTrue(server.IsAuthenticated);
                    Assert.AreEqual("Kerberos", server.Package, "NTLM cannot satisfy the Kerberos interoperability test.");
                    byte[] expected = CredSsp.BindingHashForTest(true, request[5], publicKey);
                    byte[] actual = Unwrap(server, binding);
                    Assert.AreSequenceEqual(expected, actual);
                    byte[] echo = Wrap(server, CredSsp.BindingHashForTest(false, request[5], publicKey));
                    await tls.WriteAsync(Reply(outgoing, echo));
                    byte[] final = await FakeRdpConnection.ReadDerAsync(tls);
                    byte[] credentials = Unwrap(server, ReadRequest(final)[2]);
                    byte[] expectedCredentials = CredSsp.BuildCredentialsForTest(domain, username, password);
                    try { Assert.AreSequenceEqual(expectedCredentials, credentials); }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expectedCredentials);
                        CryptographicOperations.ZeroMemory(credentials);
                    }
                    return;
                }
                await tls.WriteAsync(Reply(outgoing, null));
            }
            Assert.Fail("CredSSP did not complete within eight token exchanges.");
        }, client, domain, username, password);
    }

    private static byte[] Wrap(NegotiateAuthentication auth, byte[] plain)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.AreEqual(NegotiateAuthenticationStatusCode.Completed, auth.Wrap(plain, output, true, out bool encrypted));
        Assert.IsTrue(encrypted);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Unwrap(NegotiateAuthentication auth, byte[] wrapped)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.AreEqual(NegotiateAuthenticationStatusCode.Completed, auth.Unwrap(wrapped, output, out bool encrypted));
        Assert.IsTrue(encrypted);
        return output.WrittenSpan.ToArray();
    }

    private static Dictionary<int, byte[]> ReadRequest(byte[] packet)
    {
        var fields = new Dictionary<int, byte[]>();
        AsnReader request = new AsnReader(packet, AsnEncodingRules.DER).ReadSequence();
        while (request.HasData)
        {
            Asn1Tag tag = request.PeekTag();
            AsnReader field = request.ReadSequence(tag);
            if (tag.TagValue == 0) { Assert.AreEqual(6, (int)field.ReadInteger()); continue; }
            if (tag.TagValue == 1)
                field = field.ReadSequence().ReadSequence().ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            fields.Add(tag.TagValue, field.ReadOctetString());
        }
        return fields;
    }

    private static byte[] Reply(byte[]? token, byte[]? binding)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteInteger(6);
            if (token is { Length: > 0 })
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (writer.PushSequence())
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteOctetString(token);
            if (binding is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3))) writer.WriteOctetString(binding);
        }
        return writer.Encode();
    }
}
