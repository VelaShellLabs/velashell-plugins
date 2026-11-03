using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Security;

/// <summary>Licensing RSA certificates delivered by an already authenticated TLS peer.</summary>
internal static class LicenseCertificate
{
    // Published protocol signing key, MS-RDPBCGR 5.3.3.1.1. This is not a TLS trust anchor.
    private static readonly BigInteger SigningModulus = new(Convert.FromHexString(
        "3D3A5EBD72433EC94DBBC11E4ABA5FCB3E882087EFF5C1E2D7B76B9AF2524595" +
        "CE63656B583AFEEF7CE7BFFE3DF65C7D6C5E06091AF561BB2093095F056DEA87"), isUnsigned: true);

    internal static (BigInteger Modulus, BigInteger Exponent, int Size) Read(ReadOnlySpan<byte> certificate)
    {
        if (certificate.IsEmpty)
        {
            throw new RdpProtocolException("The RDP license server omitted its RSA certificate; no licensing key is available over TLS.");
        }
        try
        {
            var r = new ByteReader(certificate);
            uint version = r.U32LE() & 0x7FFFFFFF;
            (BigInteger Modulus, BigInteger Exponent, int Size) key = version switch
            {
                1 => Proprietary(certificate, ref r),
                2 => X509(ref r),
                _ => throw new RdpProtocolException($"Unsupported RDP licensing certificate version {version}.")
            };
            if (r.Remaining != 0 || key.Size is < 64 or > 512 || key.Modulus.GetBitLength() != key.Size * 8 ||
                key.Modulus.IsEven || key.Exponent < 3 || key.Exponent > uint.MaxValue || key.Exponent.IsEven)
            {
                throw new RdpProtocolException("Invalid RDP licensing RSA certificate.");
            }
            return key;
        }
        catch (CryptographicException ex)
        {
            throw new RdpProtocolException($"Invalid RDP licensing certificate: {ex.Message}");
        }
    }

    private static (BigInteger Modulus, BigInteger Exponent, int Size) Proprietary(ReadOnlySpan<byte> certificate, ref ByteReader r)
    {
        if (r.U32LE() != 1 || r.U32LE() != 1 || r.U16LE() != 6)
        {
            throw new RdpProtocolException("Invalid proprietary RDP licensing certificate algorithms.");
        }
        ushort length = r.U16LE();
        var key = new ByteReader(r.Take(length));
        uint magic = key.U32LE(), keyLength = key.U32LE(), bits = key.U32LE(), dataLength = key.U32LE();
        uint exponent = key.U32LE();
        if (magic != 0x31415352 || bits is < 512 or > 4096 || bits % 8 != 0 ||
            keyLength != bits / 8 + 8 || dataLength != bits / 8 - 1 || key.Remaining != keyLength)
        {
            throw new RdpProtocolException("Invalid proprietary RDP licensing RSA key length.");
        }
        int size = (int)(bits / 8);
        BigInteger modulus = new(key.Take(size), isUnsigned: true);
        RequireZeros(key.Take(8));
        byte[] hash = MD5.HashData(certificate[..r.Position]);
#pragma warning disable IDE0078 // 使用模式匹配(可能会更改代码含义)
        if (r.U16LE() != 8 || r.U16LE() != 72)
        {
            throw new RdpProtocolException("Invalid proprietary RDP licensing signature length.");
        }
#pragma warning restore IDE0078 // 使用模式匹配(可能会更改代码含义)
        BigInteger signature = new(r.Take(64), isUnsigned: true);
        RequireZeros(r.Take(8));
        if (signature >= SigningModulus)
        {
            throw new RdpProtocolException("Invalid proprietary RDP licensing signature.");
        }
        byte[] actual = new byte[64];
        _ = BigInteger.ModPow(signature, 0xC0887B5B, SigningModulus).TryWriteBytes(actual, out _, isUnsigned: true);
        byte[] expected = new byte[64];
        hash.CopyTo(expected, 0);
        expected.AsSpan(17, 45).Fill(0xFF);
        expected[62] = 1;
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new RdpProtocolException("Invalid proprietary RDP licensing certificate signature.");
        }
        return (modulus, exponent, size);
    }

    private static (BigInteger Modulus, BigInteger Exponent, int Size) X509(ref ByteReader r)
    {
        uint count = r.U32LE();
        if (count is < 2 or > 200 || count > r.Remaining / 4)
        {
            throw new RdpProtocolException("Invalid RDP licensing certificate chain count.");
        }
        var certificates = new List<X509Certificate2>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                uint length = r.U32LE();
                if (length == 0 || length > r.Remaining)
                {
                    throw new RdpProtocolException("Invalid RDP licensing certificate length.");
                }
                certificates.Add(X509CertificateLoader.LoadCertificate(r.Take((int)length)));
            }
            r.Skip(8 + 4 * (int)count);
            using var chain = new X509Chain();
            // The licensing CA can be private/self-signed. Its provenance is the authenticated TLS
            // channel; validate the supplied chain without installing roots or downloading certificates.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            _ = chain.ChainPolicy.CustomTrustStore.Add(certificates[0]);
            chain.ChainPolicy.ExtraStore.AddRange(certificates.ToArray());
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            if (!chain.Build(certificates[^1]))
            {
                throw new RdpProtocolException("Invalid RDP licensing X.509 certificate chain.");
            }
            using RSA rsa = certificates[^1].GetRSAPublicKey()
                ?? throw new RdpProtocolException("The RDP licensing certificate does not contain an RSA key.");
            RSAParameters parameters = rsa.ExportParameters(false);
            return (new BigInteger(parameters.Modulus!, isUnsigned: true, isBigEndian: true),
                new BigInteger(parameters.Exponent!, isUnsigned: true, isBigEndian: true), parameters.Modulus!.Length);
        }
        finally
        {
            foreach (X509Certificate2 cert in certificates) { cert.Dispose(); }
        }
    }

    private static void RequireZeros(ReadOnlySpan<byte> padding)
    {
        foreach (byte value in padding)
        {
            if (value != 0) { throw new RdpProtocolException("Invalid RDP licensing RSA padding."); }
        }
    }
}
