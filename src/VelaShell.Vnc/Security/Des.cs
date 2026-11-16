namespace VelaShell.Vnc.Security;

/// <summary>
/// Single-DES block encryption (FIPS 46-3), the cipher of VNC authentication (RFC 6143 §7.2.2).
/// </summary>
/// <remarks>
/// Managed on purpose: <c>System.Security.Cryptography.DES</c> goes through the platform library, and OpenSSL 3 moved
/// single DES to its "legacy" provider, which most Linux distributions do not load — VNC authentication would work on
/// Windows and macOS and fail on Linux. One implementation keeps the three platforms identical. Only encryption of
/// whole 8-byte blocks is needed; this is not a general-purpose cipher.
/// </remarks>
internal static class Des
{
    private static readonly byte[] Ip =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7
    ];

    private static readonly byte[] Fp =
    [
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25
    ];

    private static readonly byte[] E =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1
    ];

    private static readonly byte[] P =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25
    ];

    private static readonly byte[] Pc1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18,
        10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22,
        14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4
    ];

    private static readonly byte[] Pc2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32
    ];

    private static readonly byte[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly byte[][] S =
    [
        [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
         4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
        [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
         0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
        [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
         13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
        [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
         10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
        [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
         4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
        [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
         9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
        [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
         1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
        [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
         7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11]
    ];

    /// <summary>Output bit i (MSB first) is input bit table[i] (1-based, MSB first) of an <paramref name="width"/>-bit value.</summary>
    private static ulong Permute(ulong input, int width, byte[] table)
    {
        ulong output = 0;
        foreach (byte position in table)
        {
            output = (output << 1) | ((input >> (width - position)) & 1);
        }
        return output;
    }

    private static ulong[] Subkeys(ReadOnlySpan<byte> key)
    {
        ulong k = Permute(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key), 64, Pc1);   // 56 bits
        uint c = (uint)(k >> 28) & 0x0FFFFFFF, d = (uint)k & 0x0FFFFFFF;
        ulong[] subkeys = new ulong[16];
        for (int round = 0; round < 16; round++)
        {
            for (int s = 0; s < Shifts[round]; s++)
            {
                c = ((c << 1) | (c >> 27)) & 0x0FFFFFFF;
                d = ((d << 1) | (d >> 27)) & 0x0FFFFFFF;
            }
            subkeys[round] = Permute(((ulong)c << 28) | d, 56, Pc2);   // 48 bits
        }
        return subkeys;
    }

    private static uint Feistel(uint r, ulong subkey)
    {
        ulong x = Permute(r, 32, E) ^ subkey;
        uint output = 0;
        for (int box = 0; box < 8; box++)
        {
            int six = (int)(x >> (42 - 6 * box)) & 0x3F;
            int row = ((six & 0x20) >> 4) | (six & 1), column = (six >> 1) & 0xF;
            output = (output << 4) | S[box][row * 16 + column];
        }
        return (uint)Permute(output, 32, P);
    }

    /// <summary>Encrypts each 8-byte block of <paramref name="data"/> in place with ECB mode.</summary>
    internal static void EncryptEcb(ReadOnlySpan<byte> key, Span<byte> data)
    {
        if (key.Length != 8 || data.Length % 8 != 0)
        {
            throw new ArgumentException("DES takes an 8-byte key and whole 8-byte blocks.");
        }

        ulong[] subkeys = Subkeys(key);
        for (int offset = 0; offset < data.Length; offset += 8)
        {
            Span<byte> block = data.Slice(offset, 8);
            ulong v = Permute(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(block), 64, Ip);
            uint l = (uint)(v >> 32), r = (uint)v;
            for (int round = 0; round < 16; round++)
            {
                (l, r) = (r, l ^ Feistel(r, subkeys[round]));
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(block, Permute(((ulong)r << 32) | l, 64, Fp));
        }
    }
}
