using System.Formats.Asn1;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Offline checks for the CredSSP/NLA ASN.1 envelope. Live NLA needs a Windows server
/// (xrdp has no NLA), so these validate the TSRequest/TSCredentials encoding and the
/// public-key binding hash, which are the most error-prone, untestable-against-xrdp parts.
/// </summary>
[TestClass]
public sealed class CredSspAsn1Tests
{
    [TestMethod]
    public void TsRequestRoundTripsNegoTokenAndVersion()
    {
        byte[] nego = [1, 2, 3, 4, 5];
        (uint version, byte[]? token, byte[]? pub, uint? error) = CredSsp.RoundTripForTest(6, nego, null, null, null);
        Assert.AreEqual(6u, version);
        Assert.IsNotNull(token);
        Assert.AreSequenceEqual(nego, token);
        Assert.IsNull(pub);
        Assert.IsNull(error);
    }

    [TestMethod]
    public void TsRequestRoundTripsPubKeyAuthAndNonce()
    {
        byte[] pubKeyAuth = [9, 8, 7, 6];
        byte[] nonce = new byte[32];
        (uint version, byte[]? token, byte[]? pub, uint? error) = CredSsp.RoundTripForTest(6, [0xAA], null, pubKeyAuth, nonce);
        Assert.AreEqual(6u, version);
        Assert.AreSequenceEqual(new byte[] { 0xAA }, token);
        Assert.IsNotNull(pub);
        Assert.AreSequenceEqual(pubKeyAuth, pub);
        Assert.IsNull(error);
    }

    [TestMethod]
    public void TsCredentialsEncodesPasswordCredsAsUtf16()
    {
        byte[] der = CredSsp.BuildCredentialsForTest("CORP", "alice", "secret");
        // TSCredentials ::= SEQUENCE { credType [0] INTEGER, credentials [1] OCTET STRING }
        AsnReader outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        int credType = (int)outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadInteger();
        Assert.AreEqual(1, credType);
        byte[] inner = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadOctetString();

        // TSPasswordCreds ::= SEQUENCE { domainName [0], userName [1], password [2] } (each UTF-16LE OCTET STRING)
        AsnReader creds = new AsnReader(inner, AsnEncodingRules.DER).ReadSequence();
        byte[] domain = creds.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
        byte[] user = creds.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadOctetString();
        byte[] pass = creds.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadOctetString();
        Assert.AreEqual("CORP", Encoding.Unicode.GetString(domain));
        Assert.AreEqual("alice", Encoding.Unicode.GetString(user));
        Assert.AreEqual("secret", Encoding.Unicode.GetString(pass));
    }

    [TestMethod]
    public void BindingHashMatchesSpecMagicAndIsDirectional()
    {
        byte[] nonce = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        byte[] key = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] clientToServer = CredSsp.BindingHashForTest(true, nonce, key);
        byte[] serverToClient = CredSsp.BindingHashForTest(false, nonce, key);
        Assert.HasCount(32, clientToServer);
        CollectionAssert.AreNotEqual(clientToServer, serverToClient, "Client/server binding hashes must differ.");

        // Recompute the client→server hash independently: SHA256(magic‖nonce‖key), magic incl. trailing NUL.
        byte[] magic = Encoding.ASCII.GetBytes("CredSSP Client-To-Server Binding Hash\0");
        byte[] expected = System.Security.Cryptography.SHA256.HashData([.. magic, .. nonce, .. key]);
        Assert.AreSequenceEqual(expected, clientToServer);
    }

    private static byte[] TsRequestWithError(System.Numerics.BigInteger errorCode)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteInteger(6);
            }

            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
            {
                w.WriteInteger(errorCode);
            }
        }
        return w.Encode();
    }

    [TestMethod]
    public void ServerErrorCodesParseWhetherEncodedSignedOrUnsigned()
    {
        // Servers encode NTSTATUS as a signed INTEGER: STATUS_LOGON_FAILURE 0xC000006D arrives as
        // -1073741715. Converting that BigInteger straight to uint threw OverflowException and hid the
        // server's actual answer ("wrong password") behind "Arithmetic operation resulted in an overflow".
        (uint version, uint? signed) = CredSsp.ParseForTest(TsRequestWithError(unchecked((int)0xC000006D)));
        Assert.AreEqual(6u, version);
        Assert.AreEqual(0xC000006Du, signed);

        // A server that encodes the same 32 bits as a positive INTEGER must give the same value.
        (_, uint? unsigned) = CredSsp.ParseForTest(TsRequestWithError(0x8009030CU));
        Assert.AreEqual(0x8009030Cu, unsigned);

        _ = Assert.ThrowsExactly<RdpProtocolException>(() => CredSsp.ParseForTest(TsRequestWithError(System.Numerics.BigInteger.Pow(2, 40))));
    }
}
