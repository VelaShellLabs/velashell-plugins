using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Vnc;
using VelaShell.Vnc.Security;

namespace VelaShell.Plugin.Vnc.Tests;

[TestClass]
public sealed class VncSecurityTests
{
    [TestMethod]
    public void ManagedDesMatchesTheFipsKnownAnswer()
    {
        // FIPS 46-3 / the classic worked example: key 133457799BBCDFF1, plaintext 0123456789ABCDEF.
        byte[] block = Convert.FromHexString("0123456789ABCDEF");
        Des.EncryptEcb(Convert.FromHexString("133457799BBCDFF1"), block);
        Assert.AreEqual("85E813540F0AB405", Convert.ToHexString(block));
    }

    [TestMethod]
    public void ManagedDesMatchesThePlatformCipher()
    {
        // The platform DES is only a cross-check here (Windows ships single DES in CNG; OpenSSL 3 hides it in the
        // legacy provider, which is exactly why the client carries its own).
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Single DES is not available from the platform library here."); }
        var random = new Random(1234);
        for (int i = 0; i < 64; i++)
        {
            byte[] key = new byte[8], data = new byte[16];
            random.NextBytes(key);
            random.NextBytes(data);
            using var des = DES.Create();
            des.Key = key;
            byte[] expected = des.EncryptEcb(data, PaddingMode.None);
            byte[] actual = data.ToArray();
            Des.EncryptEcb(key, actual);
            Assert.AreEqual(Convert.ToHexString(expected), Convert.ToHexString(actual), $"key {Convert.ToHexString(key)}");
        }
    }

    [TestMethod]
    public void VncAuthenticationMirrorsTheKeyBitsAndUsesEightCharacters()
    {
        byte[] challenge = [.. Enumerable.Range(0, 16).Select(static i => (byte)(i * 17))];
        // "password" mirrored byte by byte is the DES key.
        byte[] key = [.. Encoding.ASCII.GetBytes("password").Select(static b => (byte)(((b * 0x0202020202UL & 0x010884422010UL) % 1023) & 0xFF))];
        byte[] expected = challenge.ToArray();
        Des.EncryptEcb(key, expected);
        Assert.AreEqual(Convert.ToHexString(expected), Convert.ToHexString(VncAuthentication.Respond(challenge, "password")));
        // Only the first eight characters count; a shorter password is zero-padded.
        Assert.AreEqual(Convert.ToHexString(VncAuthentication.Respond(challenge, "password")),
            Convert.ToHexString(VncAuthentication.Respond(challenge, "password-and-more")));
        Assert.AreNotEqual(Convert.ToHexString(VncAuthentication.Respond(challenge, "pass")),
            Convert.ToHexString(VncAuthentication.Respond(challenge, "password")));
        _ = Assert.ThrowsExactly<VncProtocolException>(() => VncAuthentication.Respond(new byte[8], "x"));
    }

    /// <summary>The server side of Apple Remote Desktop, as macOS does it: decrypts what the client sent.</summary>
    internal static (string User, string Password) ArdServerDecrypt(byte[] response, BigInteger prime, BigInteger serverPrivate, int keyLength)
    {
        byte[] encrypted = response[..128], clientPublic = response[128..];
        Assert.HasCount(keyLength, clientPublic);
        BigInteger shared = BigInteger.ModPow(new BigInteger(clientPublic, isUnsigned: true, isBigEndian: true), serverPrivate, prime);
        byte[] sharedBytes = shared.ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] padded = new byte[keyLength];
        sharedBytes.CopyTo(padded, keyLength - sharedBytes.Length);
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(padded);
        byte[] plain = aes.DecryptEcb(encrypted, PaddingMode.None);
        static string Field(ReadOnlySpan<byte> f) => Encoding.UTF8.GetString(f[..f.IndexOf((byte)0)]);
        return (Field(plain.AsSpan(0, 64)), Field(plain.AsSpan(64, 64)));
    }

    /// <summary>RFC 2409 Oakley group 2 (1024-bit MODP), the kind of prime macOS sends.</summary>
    internal static readonly byte[] OakleyPrime = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF");

    [TestMethod]
    public void AppleRemoteDesktopCredentialsDecryptOnTheServer()
    {
        var p = new BigInteger(OakleyPrime, isUnsigned: true, isBigEndian: true);
        var serverPrivate = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true, isBigEndian: true);
        byte[] serverPublic = BigInteger.ModPow(2, serverPrivate, p).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] padded = new byte[128];
        serverPublic.CopyTo(padded, 128 - serverPublic.Length);
        byte[] response = AppleRemoteDesktop.Respond(2, OakleyPrime, padded, "joe", "sésame 密码");
        (string user, string password) = ArdServerDecrypt(response, p, serverPrivate, 128);
        Assert.AreEqual("joe", user);
        Assert.AreEqual("sésame 密码", password);
    }

    [TestMethod]
    public void AppleRemoteDesktopRejectsDegenerateServerKeys()
    {
        byte[] one = new byte[128];
        one[^1] = 1;
        _ = Assert.ThrowsExactly<VncProtocolException>(() => AppleRemoteDesktop.Respond(2, OakleyPrime, one, "u", "p"));
        _ = Assert.ThrowsExactly<VncProtocolException>(() => AppleRemoteDesktop.Respond(2, OakleyPrime, new byte[64], "u", "p"));
    }
}
