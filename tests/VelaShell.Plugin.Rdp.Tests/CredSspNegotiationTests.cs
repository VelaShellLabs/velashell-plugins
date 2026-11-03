using System.Formats.Asn1;
using System.Net.Security;
using System.Net.Sockets;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class CredSspNegotiationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstCredentialProofRejectionNeverRequestsTlsFallback(bool disconnect)
    {
        using var auth = new FirstTokenContext(SpnegoInitial("1.2.840.113554.1.2.2", [0x60, 0x00]));
        Exception? error = null;
        try
        {
            await RunAsync(async ssl =>
            {
                _ = await FakeRdpConnection.ReadDerAsync(ssl);
                if (!disconnect) await FakeRdpConnection.SendErrorCodeAsync(ssl, 0x8009030C);
            }, auth);
        }
        catch (Exception ex) { error = ex; }
        Assert.IsNotNull(error);
        Assert.IsFalse(error is RdpNlaRefusedException, "A first Kerberos proof must not authorize TLS downgrade.");
        if (!disconnect) Assert.IsInstanceOfType<RdpAuthenticationException>(error);
        Assert.AreEqual(1, auth.Steps);
    }

    [TestMethod]
    public async Task ImmediatelyCompletedContextSendsTokenOnceWithModernBinding()
    {
        using var auth = new FirstTokenContext([1, 2, 3], complete: true);
        _ = await Assert.ThrowsAsync<RdpAuthenticationException>(() => RunAsync(async ssl =>
        {
            byte[] packet = await FakeRdpConnection.ReadDerAsync(ssl);
            AsnReader request = new AsnReader(packet, AsnEncodingRules.DER).ReadSequence();
            _ = request.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            _ = request.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
            byte[] binding = request.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3)).ReadOctetString();
            Assert.HasCount(32, binding, "Use the nonce-bound SHA-256 binding even before the first reply.");
            await FakeRdpConnection.SendErrorCodeAsync(ssl, 0x8009030C);
        }, auth));
        Assert.AreEqual(1, auth.Steps);
        Assert.AreEqual(1, auth.Seals);
    }

    [TestMethod]
    public void OnlyUnambiguousNtlmTypeOneAllowsFallback()
    {
        using var ntlm = new NegotiateSecurityContext("Domain", "User", "password", "127.0.0.1", "NTLM");
        byte[] type1 = ntlm.Step([]);
        byte[] spnego = SpnegoInitial("1.3.6.1.4.1.311.2.2.10", type1);
        Assert.IsTrue(CredSsp.IsNtlmNegotiate(type1));
        Assert.IsTrue(CredSsp.IsNtlmNegotiate(spnego));
        Assert.IsFalse(CredSsp.IsNtlmNegotiate(SpnegoInitial("1.2.840.113554.1.2.2", type1)));
        Assert.IsFalse(CredSsp.IsNtlmNegotiate([.. spnego, 0]));
        Assert.IsFalse(CredSsp.IsNtlmNegotiate(spnego[..^1]));
        type1[8] = 3;
        Assert.IsFalse(CredSsp.IsNtlmNegotiate(type1));
        Assert.IsFalse(CredSsp.IsNtlmNegotiate(SpnegoInitial("1.3.6.1.4.1.311.2.2.10", type1)));
        Assert.IsFalse(CredSsp.IsNtlmNegotiate([]));
    }

    private static byte[] SpnegoInitial(string mechanism, byte[] token)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 0)))
        {
            writer.WriteObjectIdentifier("1.3.6.1.5.5.2");
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(mechanism);
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    writer.WriteOctetString(token);
            }
        }
        return writer.Encode();
    }

    internal static async Task RunAsync(Func<SslStream, Task> script, INlaSecurityContext auth,
        string domain = "Domain", string username = "User", string password = "password")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new FakeRdpServer(async connection =>
        {
            await connection.SelectAsync(FakeRdpConnection.Hybrid);
            using SslStream ssl = await connection.StartTlsAsync();
            await script(ssl);
        });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port, cts.Token);
        NetworkStream net = tcp.GetStream();
        await net.WriteAsync(new byte[] { 3, 0, 0, 19, 14, 0xE0, 0, 0, 0, 0, 0, 1, 0, 8, 0, 3, 0, 0, 0 }, cts.Token);
        await net.ReadExactlyAsync(new byte[19], cts.Token);
        using var tls = new SslStream(net, false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cts.Token);
        await CredSsp.AuthenticateAsync(tls, domain, username, password, auth, cts.Token);
    }

    private sealed class FirstTokenContext(byte[] token, bool complete = false) : INlaSecurityContext
    {
        public bool IsComplete { get; private set; }
        internal int Steps { get; private set; }
        internal int Seals { get; private set; }
        public byte[] Step(byte[] incoming)
        {
            Assert.AreEqual(0, Steps++);
            IsComplete = complete;
            return token;
        }
        public byte[] Seal(byte[] plaintext) { Seals++; return plaintext; }
        public byte[] Unseal(byte[] wrapped) => throw new AssertFailedException("Unexpected unseal.");
        public void Dispose() { }
    }
}
