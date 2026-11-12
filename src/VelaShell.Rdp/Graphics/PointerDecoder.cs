using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Graphics;

/// <summary>
/// Pointer (mouse cursor) updates — MS-RDPBCGR 2.2.9.1.1.4 (slow path) and 2.2.9.1.2.1.5–.11 (fast path) — decoded
/// into <see cref="RdpPointer"/> images and kept in the pointer cache the client advertised. The AND/XOR mask handling is
/// adapted from FreeRDP <c>libfreerdp/codec/color.c</c> (Apache-2.0; see THIRD-PARTY-NOTICES.txt):
/// masks are bottom-up (except 1 bpp), rows are padded to an even byte count, and an "inverted" pixel (AND set, XOR
/// white) becomes a black/white checkerboard because no platform cursor can invert what is underneath it.
/// </summary>
internal sealed class PointerDecoder(int cacheSize)
{
    /// <summary>Largest cursor side accepted (TS_POINTERATTRIBUTE allows 96; large pointers are not advertised).</summary>
    internal const int MaxSide = 96;

    private readonly RdpPointer?[] _cache = new RdpPointer?[Math.Clamp(cacheSize, 1, 64)];

    // Fast-path update codes (2.2.9.1.2.1).
    internal const int FastPathNull = 0x5, FastPathDefault = 0x6, FastPathPosition = 0x8, FastPathColor = 0x9,
        FastPathCached = 0xA, FastPathNew = 0xB;

    /// <summary>A fast-path pointer update; null when the code is not a pointer change (position) or not decodable.</summary>
    internal RdpPointer? FastPath(int code, ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        return code switch
        {
            FastPathNull => RdpPointer.Hidden,
            FastPathDefault => RdpPointer.Default,
            FastPathColor => ColorPointer(ref r, xorBpp: 24),
            FastPathNew => ColorPointer(ref r, xorBpp: r.U16LE()),
            FastPathCached => Cached(r.U16LE()),
            _ => null
        };
    }

    /// <summary>A slow-path Pointer PDU body (TS_POINTER_PDU, after the share data header).</summary>
    internal RdpPointer? SlowPath(ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        ushort messageType = r.U16LE();
        r.Skip(2);
        return messageType switch
        {
            1 => r.U32LE() == 0 ? RdpPointer.Hidden : RdpPointer.Default,   // TS_PTRMSGTYPE_SYSTEM: SYSPTR_NULL / SYSPTR_DEFAULT
            6 => ColorPointer(ref r, xorBpp: 24),                            // TS_PTRMSGTYPE_COLOR
            7 => Cached(r.U16LE()),                                          // TS_PTRMSGTYPE_CACHED
            8 => ColorPointer(ref r, xorBpp: r.U16LE()),                     // TS_PTRMSGTYPE_POINTER
            _ => null                                                        // TS_PTRMSGTYPE_POSITION and others
        };
    }

    private RdpPointer? Cached(int index) => index < _cache.Length ? _cache[index] : null;

    /// <summary>TS_COLORPOINTERATTRIBUTE: decodes, caches at its index and returns the pointer (null if undecodable).</summary>
    private RdpPointer? ColorPointer(ref ByteReader r, int xorBpp)
    {
        int cacheIndex = r.U16LE();
        int hotX = r.U16LE(), hotY = r.U16LE();
        int width = r.U16LE(), height = r.U16LE();
        int andLength = r.U16LE(), xorLength = r.U16LE();
        ReadOnlySpan<byte> xorMask = r.Take(xorLength);
        ReadOnlySpan<byte> andMask = r.Take(andLength);
        RdpPointer? pointer = Decode(width, height, Math.Min(hotX, Math.Max(0, width - 1)), Math.Min(hotY, Math.Max(0, height - 1)), xorBpp, xorMask, andMask);
        if (pointer is not null && cacheIndex < _cache.Length)
        {
            _cache[cacheIndex] = pointer;
        }

        return pointer;
    }

    /// <summary>Turns AND/XOR masks into a top-down, straight-alpha BGRA image; null for an unsupported or short image.</summary>
    internal static RdpPointer? Decode(int width, int height, int hotX, int hotY, int xorBpp, ReadOnlySpan<byte> xorMask, ReadOnlySpan<byte> andMask)
    {
        if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide || xorBpp is not (1 or 16 or 24 or 32))
        {
            return null;   // 8 bpp would need a palette the client never negotiated
        }

        int andStep = (width + 7) / 8;
        andStep += andStep % 2;
        int xorStep = xorBpp == 1 ? andStep : width * (xorBpp / 8);
        xorStep += xorStep % 2;
        bool hasAnd = andMask.Length > 0;
        if (xorMask.Length < xorStep * height || (hasAnd && andMask.Length < andStep * height) || (xorBpp == 1 && !hasAnd))
        {
            return null;
        }

        byte[] bgra = new byte[width * height * 4];
        bool bottomUp = xorBpp != 1;
        for (int y = 0; y < height; y++)
        {
            int row = bottomUp ? height - 1 - y : y;
            ReadOnlySpan<byte> xorRow = xorMask.Slice(row * xorStep, xorStep);
            ReadOnlySpan<byte> andRow = hasAnd ? andMask.Slice(row * andStep, andStep) : default;
            for (int x = 0; x < width; x++)
            {
                bool and = hasAnd && (andRow[x >> 3] & (0x80 >> (x & 7))) != 0;
                uint argb = xorBpp switch
                {
                    1 => Monochrome(and, (xorRow[x >> 3] & (0x80 >> (x & 7))) != 0, x, y),
                    16 => Masked(and, Rgb555(xorRow[x * 2] | (xorRow[x * 2 + 1] << 8)), x, y),
                    24 => Masked(and, Rgb24(xorRow, x, and), x, y),
                    _ => Masked(and, Bgra32(xorRow, x), x, y)
                };
                int d = (y * width + x) * 4;
                bgra[d] = (byte)argb;
                bgra[d + 1] = (byte)(argb >> 8);
                bgra[d + 2] = (byte)(argb >> 16);
                bgra[d + 3] = (byte)(argb >> 24);
            }
        }

        return new RdpPointer(RdpPointerKind.Image, width, height, hotX, hotY, bgra);
    }

    private static uint Inverted(int x, int y) => ((x + y) & 1) != 0 ? 0xFF000000u : 0xFFFFFFFFu;

    private static uint Monochrome(bool and, bool xor, int x, int y) => (and, xor) switch
    {
        (false, false) => 0xFF000000u,   // black
        (false, true) => 0xFFFFFFFFu,    // white
        (true, false) => 0x00000000u,    // transparent
        _ => Inverted(x, y)              // inverted
    };

    /// <summary>With the AND bit set, black means transparent and white means inverted (Windows cursor mask semantics).</summary>
    private static uint Masked(bool and, uint argb, int x, int y) =>
        !and ? argb : argb == 0xFF000000u ? 0u : argb == 0xFFFFFFFFu ? Inverted(x, y) : argb;

    private static uint Rgb555(int p)
    {
        int r = (p >> 10) & 0x1F, g = (p >> 5) & 0x1F, b = p & 0x1F;
        return 0xFF000000u | (uint)(((r << 3) | (r >> 2)) << 16) | (uint)(((g << 3) | (g >> 2)) << 8) | (uint)((b << 3) | (b >> 2));
    }

    private static uint Rgb24(ReadOnlySpan<byte> row, int x, bool and)
    {
        uint bgr = row[x * 3] | ((uint)row[x * 3 + 1] << 8) | ((uint)row[x * 3 + 2] << 16);
        // 24 bpp has no alpha: with the AND bit set only white survives (to become "inverted"); the rest is transparent.
        return and ? bgr == 0x00FFFFFFu ? 0xFFFFFFFFu : 0u : 0xFF000000u | bgr;
    }

    private static uint Bgra32(ReadOnlySpan<byte> row, int x) =>
        row[x * 4] | ((uint)row[x * 4 + 1] << 8) | ((uint)row[x * 4 + 2] << 16) | ((uint)row[x * 4 + 3] << 24);
}
