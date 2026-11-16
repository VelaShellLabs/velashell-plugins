using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Vnc.Security;

/// <summary>
/// Apple Remote Desktop authentication (RFB security type 30), used by macOS Screen Sharing: an anonymous
/// Diffie-Hellman exchange whose shared secret, hashed with MD5, is the AES-128 key that encrypts a 128-byte block
/// holding the user name (bytes 0–63) and the password (bytes 64–127), each NUL-terminated.
/// </summary>
internal static class AppleRemoteDesktop
{
    internal const int CredentialFieldLength = 64;

    /// <summary>
    /// Computes the client's reply to the server's parameters: the encrypted credentials followed by the client's public
    /// key (both sent as they are). <paramref name="privateKey"/> is a test seam; normally a fresh random value.
    /// </summary>
    internal static byte[] Respond(ushort generator, ReadOnlySpan<byte> prime, ReadOnlySpan<byte> serverPublicKey,
        string username, string password, byte[]? privateKey = null, byte[]? padding = null)
    {
        int keyLength = prime.Length;
        if (keyLength is < 16 or > 1024 || serverPublicKey.Length != keyLength || generator < 2)
        {
            throw new VncProtocolException("Apple Remote Desktop parameters are malformed.");
        }

        var p = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(serverPublicKey, isUnsigned: true, isBigEndian: true);
        if (p <= 3 || y <= 1 || y >= p - 1)
        {
            throw new VncProtocolException("Apple Remote Desktop public key is out of range.");
        }

        byte[] secretBytes = privateKey ?? RandomNumberGenerator.GetBytes(keyLength);
        BigInteger x = new BigInteger(secretBytes, isUnsigned: true, isBigEndian: true) % (p - 2) + 1;
        byte[] publicKey = Fixed(BigInteger.ModPow(generator, x, p), keyLength);
        byte[] shared = Fixed(BigInteger.ModPow(y, x, p), keyLength);
        byte[] aesKey = MD5.HashData(shared);

        byte[] credentials = padding?.ToArray() ?? RandomNumberGenerator.GetBytes(2 * CredentialFieldLength);
        Put(credentials.AsSpan(0, CredentialFieldLength), username);
        Put(credentials.AsSpan(CredentialFieldLength, CredentialFieldLength), password);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        byte[] encrypted = aes.EncryptEcb(credentials, PaddingMode.None);
        return [.. encrypted, .. publicKey];
    }

    /// <summary>UTF-8, NUL-terminated, at most 63 bytes (cut on a character boundary).</summary>
    private static void Put(Span<byte> field, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int length = Math.Min(bytes.Length, field.Length - 1);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }
        bytes.AsSpan(0, length).CopyTo(field);
        field[length] = 0;
    }

    private static byte[] Fixed(BigInteger value, int length)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length)
        {
            return bytes;
        }

        byte[] padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return padded;
    }
}
