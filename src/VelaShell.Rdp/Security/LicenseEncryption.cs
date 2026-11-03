using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace VelaShell.Rdp.Security;

/// <summary>MS-RDPELE 5.1: licensing keys, per-blob RC4 and the 16-byte licensing MAC.</summary>
internal sealed class LicenseEncryption : IDisposable
{
    private readonly byte[] _macKey;
    private readonly byte[] _encryptionKey;
    internal byte[] ClientRandom { get; } = RandomNumberGenerator.GetBytes(32);
    internal byte[] EncryptedPreMasterSecret { get; }

    internal LicenseEncryption(ReadOnlySpan<byte> certificate, ReadOnlySpan<byte> serverRandom)
    {
        (BigInteger modulus, BigInteger exponent, int size) = LicenseCertificate.Read(certificate);
        byte[] premaster = RandomNumberGenerator.GetBytes(48);
        try
        {
            // MS-RDPBCGR 5.3.4.1 uses raw little-endian RSA, followed by eight zero padding bytes.
            var encrypted = BigInteger.ModPow(new BigInteger(premaster, isUnsigned: true), exponent, modulus);
            EncryptedPreMasterSecret = new byte[size + 8];
            _ = encrypted.TryWriteBytes(EncryptedPreMasterSecret.AsSpan(0, size), out _, isUnsigned: true);
            (_macKey, _encryptionKey) = DeriveKeys(premaster, ClientRandom, serverRandom);
        }
        finally { CryptographicOperations.ZeroMemory(premaster); }
    }

    internal static (byte[] MacKey, byte[] EncryptionKey) DeriveKeys(ReadOnlySpan<byte> premaster,
        ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> serverRandom)
    {
        byte[] master = Salted(premaster, clientRandom, serverRandom);
        byte[] session = Salted(master, serverRandom, clientRandom);
        try
        {
            return (session[..16], MD5.HashData([.. session.AsSpan(16, 16), .. clientRandom, .. serverRandom]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            CryptographicOperations.ZeroMemory(session);
        }
    }

    private static byte[] Salted(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] result = new byte[48];
        for (int i = 0; i < 3; i++)
        {
            byte[] label = new byte[i + 1];
            label.AsSpan().Fill((byte)('A' + i));
            byte[] input = [.. label, .. secret, .. first, .. second];
            byte[] hash = SHA1.HashData(input);
            CryptographicOperations.ZeroMemory(input);
            byte[] salted = [.. secret, .. hash];
            _ = MD5.HashData(salted, result.AsSpan(i * 16, 16));
            CryptographicOperations.ZeroMemory(salted);
            CryptographicOperations.ZeroMemory(hash);
        }
        return result;
    }

    internal byte[] Mac(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)plaintext.Length);
        Span<byte> pad = stackalloc byte[48];
        pad.Fill(0x36);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(_macKey);
        sha.AppendData(pad[..40]);
        sha.AppendData(length);
        sha.AppendData(plaintext);
        byte[] digest = sha.GetHashAndReset();
        pad.Fill(0x5C);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(_macKey);
        md5.AppendData(pad);
        md5.AppendData(digest);
        CryptographicOperations.ZeroMemory(digest);
        return md5.GetHashAndReset();
    }

    internal byte[] Crypt(ReadOnlySpan<byte> input)
    {
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < state.Length; i++) { state[i] = (byte)i; }
        for (int i = 0, j = 0; i < state.Length; i++)
        {
            j = (j + state[i] + _encryptionKey[i % _encryptionKey.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        byte[] result = new byte[input.Length];
        for (int i = 0, x = 0, y = 0; i < input.Length; i++)
        {
            x = (x + 1) & 255;
            y = (y + state[x]) & 255;
            (state[x], state[y]) = (state[y], state[x]);
            result[i] = (byte)(input[i] ^ state[(state[x] + state[y]) & 255]);
        }
        CryptographicOperations.ZeroMemory(state);
        return result;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_macKey);
        CryptographicOperations.ZeroMemory(_encryptionKey);
    }
}
