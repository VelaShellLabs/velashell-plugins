using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Graphics;

/// <summary>
/// A rectangle in desktop pixels with an exclusive right/bottom edge. Rectangles handed to an
/// <see cref="IOrderTarget"/> always have a positive width and height (a bounds rectangle that encloses nothing
/// suppresses the drawing instead of being delivered).
/// </summary>
internal readonly record struct OrderRect(int X, int Y, int Width, int Height);

/// <summary>
/// The surface an <see cref="OrderDecoder"/> draws on. Coordinates are desktop pixels and may be negative or reach
/// past the desktop edge (servers clip with bounds, not with the desktop), so the target clips every operation to
/// itself and to <c>clip</c> — the order's effective bounds rectangle, or null when the order carried none. The
/// decoder never calls the target for an operation that is empty after clipping to the desktop and the bounds.
/// </summary>
internal interface IOrderTarget
{
    /// <summary>Desktop width in pixels, read once per <see cref="OrderDecoder.Process"/> call.</summary>
    int Width { get; }

    /// <summary>Desktop height in pixels, read once per <see cref="OrderDecoder.Process"/> call.</summary>
    int Height { get; }

    /// <summary>Solid fill: OpaqueRect, MultiOpaqueRect and PatBlt with a solid brush and PATCOPY (0xF0).</summary>
    void FillRect(OrderRect rect, uint bgra, OrderRect? clip);

    /// <summary>DstBlt: 0x00 BLACKNESS, 0x55 DSTINVERT, 0xFF WHITENESS (the target may ignore other ROPs).</summary>
    void DstRop(OrderRect rect, byte rop3, OrderRect? clip);

    /// <summary>PatBlt with a hatched / pattern / null brush, or a solid brush with a ROP other than PATCOPY.</summary>
    void PatBlt(OrderRect rect, byte rop3, OrderBrush brush, OrderRect? clip);

    /// <summary>
    /// ScrBlt: the desktop area of <paramref name="dest"/>'s size at (<paramref name="srcX"/>, <paramref name="srcY"/>)
    /// lands on <paramref name="dest"/>. Source and destination may overlap; the target must copy as if through a
    /// temporary.
    /// </summary>
    void ScreenCopy(int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip);

    /// <summary>MemBlt: <paramref name="bitmap"/> pixels starting at (<paramref name="srcX"/>, <paramref name="srcY"/>) land on <paramref name="dest"/>.</summary>
    void CachedBlit(CachedBitmap bitmap, int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip);
}

/// <summary>
/// An 8×8 monochrome brush for <see cref="IOrderTarget.PatBlt"/>. <see cref="Pattern8x8"/> holds eight rows, row 0
/// at the top, bit 7 the leftmost pixel; a set bit paints <see cref="BackBgra"/> and a clear bit
/// <see cref="ForeBgra"/> (FreeRDP <c>freerdp_image_copy_from_monochrome</c>). Desktop pixel (x, y) uses row
/// (y − <see cref="OriginY"/>) mod 8 and bit 7 − ((x − <see cref="OriginX"/>) mod 8) (FreeRDP
/// <c>gdi_get_brush_pointer</c>). <see cref="Style"/> is BS_SOLID 0, BS_NULL 1, BS_HATCHED 2 or BS_PATTERN 3; hatched
/// brushes carry FreeRDP's hatch bitmap for <see cref="Hatch"/>, solid and null brushes an all-zero pattern (fore
/// colour everywhere). A null brush paints nothing in GDI; the target decides what to do with it.
/// </summary>
internal sealed record OrderBrush(int OriginX, int OriginY, int Style, int Hatch, byte[] Pattern8x8, uint ForeBgra, uint BackBgra);

/// <summary>A bitmap-cache entry, decoded to top-down BGRA (stride <see cref="Width"/> × 4, alpha 0xFF).</summary>
internal sealed class CachedBitmap
{
    internal CachedBitmap(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Bgra { get; }
}

/// <summary>
/// Drawing-order parser and bitmap cache (MS-RDPEGDI 2.2.2, TS_UPDATE_ORDERS). Ported from FreeRDP's
/// <c>libfreerdp/core/orders.c</c> (<c>update_recv_order</c>, <c>update_recv_primary_order</c>,
/// <c>update_read_field_flags</c>, <c>update_read_bounds</c>, <c>update_read_coord</c>, the DSTBLT / PATBLT / SCRBLT /
/// OPAQUE_RECT / MULTI_OPAQUE_RECT / MEMBLT readers, <c>update_read_brush</c>, <c>update_read_delta_rects</c>,
/// <c>update_recv_secondary_order</c>, <c>update_read_cache_bitmap_order</c>,
/// <c>update_read_cache_bitmap_v2_order</c> with its 2-/4-byte variable-length integers, and the alternate secondary
/// readers), the cache rules of <c>libfreerdp/cache/bitmap.c</c> and the draw semantics of
/// <c>libfreerdp/gdi/gdi.c</c> / <c>gdi/graphics.c</c> (Apache-2.0).
/// <para>
/// Primary-order state persists across <see cref="Process"/> calls exactly as in FreeRDP: the last order type
/// (initially PATBLT), the last bounds (shared by every order type), and each order type's last field values, so
/// field-flag elision, TS_ZERO_FIELD_BYTE_BIT0/1, delta coordinates and TS_ZERO_BOUNDS_DELTAS all resolve against
/// what the server sent before. Only DSTBLT, PATBLT, SCRBLT, OPAQUE_RECT, MULTI_OPAQUE_RECT and MEMBLT are decoded;
/// any other primary order means the server ignored our capabilities, and since primary orders carry no length the
/// stream cannot be resynchronised, so it raises <see cref="RdpProtocolException"/>.
/// </para>
/// <para>
/// Secondary orders: CACHE_BITMAP (revision 1) and CACHE_BITMAP_V2 are decoded into the cache; every other
/// secondary type is stepped over by its orderLength. Alternate secondary orders are stepped over where FreeRDP
/// can determine their size (frame marker, switch surface, offscreen bitmap, stream bitmap, nine-grid, GDI+, window);
/// anything else is rejected.
/// </para>
/// <para>
/// Hostile input: every read is bounds-checked, cached bitmaps are capped at <see cref="MaxCachedBitmapSide"/> per
/// side, the whole cache at <see cref="MaxCacheBytes"/>, and each <see cref="Process"/> call has a pixel work
/// budget (drawn area clipped to the desktop plus decoded cache pixels) of 16 desktops, at least 2^24 pixels.
/// Malformed input only ever raises <see cref="RdpProtocolException"/>. No closures; the only allocations are the
/// cached bitmaps themselves and the brush records handed to <see cref="IOrderTarget.PatBlt"/>.
/// </para>
/// </summary>
internal sealed class OrderDecoder
{
    /// <summary>Largest cached bitmap side accepted from the wire.</summary>
    internal const int MaxCachedBitmapSide = 4096;

    /// <summary>Upper bound on the decoded bytes held by the whole bitmap cache.</summary>
    internal const long MaxCacheBytes = 256L << 20;

    private const long WorkBudgetDesktops = 16;
    private const long MinWorkBudget = 1L << 24;

    // controlFlags (MS-RDPEGDI 2.2.2.2.1.1.2, 2.2.2.2.1.2.1.1, 2.2.2.2.1.3.1.1).
    private const byte TsStandard = 0x01;
    private const byte TsSecondary = 0x02;
    private const byte TsBounds = 0x04;
    private const byte TsTypeChange = 0x08;
    private const byte TsDeltaCoordinates = 0x10;
    private const byte TsZeroBoundsDeltas = 0x20;
    private const byte TsZeroFieldByteBit0 = 0x40;
    private const byte TsZeroFieldByteBit1 = 0x80;

    // TS_BOUNDS field flags.
    private const byte BoundLeft = 0x01, BoundTop = 0x02, BoundRight = 0x04, BoundBottom = 0x08;
    private const byte BoundDeltaLeft = 0x10, BoundDeltaTop = 0x20, BoundDeltaRight = 0x40, BoundDeltaBottom = 0x80;

    // Primary order types.
    private const byte DstBltOrder = 0x00, PatBltOrder = 0x01, ScrBltOrder = 0x02;
    private const byte OpaqueRectOrder = 0x0A, MemBltOrder = 0x0D, MultiOpaqueRectOrder = 0x12;

    // Secondary order types and flags.
    private const byte CacheBitmapUncompressed = 0x00, CacheBitmapCompressed = 0x02;
    private const byte CacheBitmapV2Uncompressed = 0x04, CacheBitmapV2Compressed = 0x05;
    private const ushort NoBitmapCompressionHdr = 0x0400;
    private const int Cbr2HeightSameAsWidth = 0x01, Cbr2PersistentKeyPresent = 0x02;
    private const int Cbr2NoBitmapCompressionHdr = 0x08, Cbr2DoNotCache = 0x10;
    private const int WaitingListIndex = 0x7FFF;

    // Alternate secondary order types (controlFlags >> 2).
    private const int AltSwitchSurface = 0x00, AltCreateOffscreenBitmap = 0x01;
    private const int AltStreamBitmapFirst = 0x02, AltStreamBitmapNext = 0x03, AltCreateNineGridBitmap = 0x04;
    private const int AltGdiPlusFirst = 0x05, AltGdiPlusNext = 0x06, AltGdiPlusEnd = 0x07;
    private const int AltGdiPlusCacheFirst = 0x08, AltGdiPlusCacheNext = 0x09, AltGdiPlusCacheEnd = 0x0A;
    private const int AltWindow = 0x0B, AltFrameMarker = 0x0D;
    private const byte StreamBitmapV2 = 0x04;

    // Brushes.
    private const byte CachedBrush = 0x80;
    private const byte BrushSolid = 0x00, BrushHatched = 0x02, BrushPattern = 0x03;
    private const byte PatCopy = 0xF0;
    private const int MaxDeltaRectangles = 45;

    /// <summary>FreeRDP <c>GDI_BS_HATCHED_PATTERNS</c>: HS_HORIZONTAL … HS_DIAGCROSS, 8 rows each, row 0 at the top.</summary>
    private static ReadOnlySpan<byte> HatchPatterns =>
    [
        0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, // HS_HORIZONTAL
        0xF7, 0xF7, 0xF7, 0xF7, 0xF7, 0xF7, 0xF7, 0xF7, // HS_VERTICAL
        0xFE, 0xFD, 0xFB, 0xF7, 0xEF, 0xDF, 0xBF, 0x7F, // HS_FDIAGONAL
        0x7F, 0xBF, 0xDF, 0xEF, 0xF7, 0xFB, 0xFD, 0xFE, // HS_BDIAGONAL
        0xF7, 0xF7, 0xF7, 0x00, 0xF7, 0xF7, 0xF7, 0xF7, // HS_CROSS
        0x7E, 0xBD, 0xDB, 0xE7, 0xE7, 0xDB, 0xBD, 0x7E, // HS_DIAGCROSS
    ];

    private readonly int _sessionBpp;
    private readonly IOrderTarget _target;
    private readonly CachedBitmap?[][] _cells;   // each cell has one extra slot for BITMAP_CACHE_WAITING_LIST_INDEX
    private long _cacheBytes;

    private int _desktopWidth, _desktopHeight;
    private long _budget;

    // Primary-order state (FreeRDP rdp_primary_update_internal).
    private byte _orderType = PatBltOrder;
    private int _boundsLeft, _boundsTop, _boundsRight, _boundsBottom;
    private DstBltState _dstBlt;
    private PatBltState _patBlt;
    private readonly byte[] _brushExtra = new byte[7];
    private ScrBltState _scrBlt;
    private OpaqueRectState _opaqueRect;
    private MultiOpaqueRectState _multiOpaqueRect;
    private readonly DeltaRect[] _deltaRects = new DeltaRect[MaxDeltaRectangles];
    private MemBltState _memBlt;

    /// <param name="sessionBpp">The session colour depth (8, 15, 16, 24 or 32): it decides how order colours decode.</param>
    /// <param name="target">The surface to draw on.</param>
    /// <param name="cellCacheSizes">Entries per bitmap-cache cell, as advertised in the bitmap cache capability.</param>
    internal OrderDecoder(int sessionBpp, IOrderTarget target, int[] cellCacheSizes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(cellCacheSizes);
        if (sessionBpp is not (8 or 15 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionBpp), sessionBpp, "Session colour depth must be 8, 15, 16, 24 or 32.");
        }

        _sessionBpp = sessionBpp;
        _target = target;
        _cells = new CachedBitmap?[cellCacheSizes.Length][];
        for (int i = 0; i < cellCacheSizes.Length; i++)
        {
            int entries = cellCacheSizes[i];
            if (entries is < 0 or >= WaitingListIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(cellCacheSizes), entries, "A bitmap cache cell holds 0 to 32766 entries.");
            }

            _cells[i] = new CachedBitmap?[entries + 1];
        }
    }

    /// <summary>
    /// Decodes <paramref name="orderCount"/> orders from a TS_UPDATE_ORDERS payload that starts at the first order
    /// (the caller strips the slow-path / fast-path update header). Bytes after the last order are ignored, as in
    /// FreeRDP.
    /// </summary>
    internal void Process(ReadOnlySpan<byte> ordersData, int orderCount)
    {
        // Every order is at least its one-byte controlFlags.
        if (orderCount < 0 || orderCount > ordersData.Length)
        {
            throw new RdpProtocolException($"{orderCount} drawing orders cannot fit in {ordersData.Length} bytes.");
        }

        _desktopWidth = Math.Max(0, _target.Width);
        _desktopHeight = Math.Max(0, _target.Height);
        _budget = Math.Max(WorkBudgetDesktops * _desktopWidth * _desktopHeight, MinWorkBudget);

        var r = new ByteReader(ordersData);
        for (int i = 0; i < orderCount; i++)
        {
            byte controlFlags = r.U8();
            if ((controlFlags & TsStandard) == 0)
            {
                SkipAltSecondary(ref r, controlFlags);
            }
            else if ((controlFlags & TsSecondary) != 0)
            {
                DecodeSecondary(ref r);
            }
            else
            {
                DecodePrimary(ref r, controlFlags);
            }
        }
    }

    /// <summary>
    /// Forgets all primary-order state (FreeRDP <c>update_reset_state</c>): last order type back to PATBLT, bounds
    /// and every order's fields zeroed. Call it on deactivation-reactivation. The bitmap cache is kept (a MemBlt
    /// into an empty slot just draws nothing).
    /// </summary>
    public void ResetPrimaryState()
    {
        _orderType = PatBltOrder;
        _boundsLeft = _boundsTop = _boundsRight = _boundsBottom = 0;
        _dstBlt = default;
        _patBlt = default;
        Array.Clear(_brushExtra);
        _scrBlt = default;
        _opaqueRect = default;
        _multiOpaqueRect = default;
        Array.Clear(_deltaRects);
        _memBlt = default;
    }

    // ── Primary orders ──────────────────────────────────────────────────────────────────────────

    private void DecodePrimary(ref ByteReader r, byte controlFlags)
    {
        byte orderType = (controlFlags & TsTypeChange) != 0 ? r.U8() : _orderType;
        int fieldBytes = orderType switch
        {
            DstBltOrder or ScrBltOrder or OpaqueRectOrder => 1,
            PatBltOrder or MemBltOrder or MultiOpaqueRectOrder => 2,
            _ => throw new RdpProtocolException($"Unsupported primary drawing order 0x{orderType:X2}."),
        };
        _orderType = orderType;

        // update_read_field_flags: the zero-byte bits drop trailing (high) field-flag bytes.
        if ((controlFlags & TsZeroFieldByteBit0) != 0)
        {
            fieldBytes--;
        }

        if ((controlFlags & TsZeroFieldByteBit1) != 0)
        {
            fieldBytes = fieldBytes > 1 ? fieldBytes - 2 : 0;
        }

        uint fieldFlags = 0;
        for (int i = 0; i < fieldBytes; i++)
        {
            fieldFlags |= (uint)r.U8() << (8 * i);
        }

        OrderRect? clip = null;
        if ((controlFlags & TsBounds) != 0)
        {
            if ((controlFlags & TsZeroBoundsDeltas) == 0)
            {
                ReadBounds(ref r);
            }

            // Bounds are inclusive (gdi_set_bounds: right - left + 1); an empty or inverted box draws nothing.
            clip = new OrderRect(_boundsLeft, _boundsTop,
                Math.Max(0, _boundsRight - _boundsLeft + 1), Math.Max(0, _boundsBottom - _boundsTop + 1));
        }

        bool delta = (controlFlags & TsDeltaCoordinates) != 0;
        switch (orderType)
        {
            case DstBltOrder:
                DstBlt(ref r, fieldFlags, delta, clip);
                break;
            case PatBltOrder:
                PatBlt(ref r, fieldFlags, delta, clip);
                break;
            case ScrBltOrder:
                ScrBlt(ref r, fieldFlags, delta, clip);
                break;
            case OpaqueRectOrder:
                OpaqueRect(ref r, fieldFlags, delta, clip);
                break;
            case MultiOpaqueRectOrder:
                MultiOpaqueRect(ref r, fieldFlags, delta, clip);
                break;
            default:
                MemBlt(ref r, fieldFlags, delta, clip);
                break;
        }
    }

    private void ReadBounds(ref ByteReader r)
    {
        byte flags = r.U8();
        _boundsLeft = ReadBound(ref r, flags, BoundLeft, BoundDeltaLeft, _boundsLeft);
        _boundsTop = ReadBound(ref r, flags, BoundTop, BoundDeltaTop, _boundsTop);
        _boundsRight = ReadBound(ref r, flags, BoundRight, BoundDeltaRight, _boundsRight);
        _boundsBottom = ReadBound(ref r, flags, BoundBottom, BoundDeltaBottom, _boundsBottom);
    }

    private static int ReadBound(ref ByteReader r, byte flags, byte absolute, byte delta, int value)
    {
        if ((flags & absolute) != 0)
        {
            return ReadCoord(ref r, false, value);
        }

        return (flags & delta) != 0 ? ReadCoord(ref r, true, value) : value;
    }

    private void DstBlt(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        ref DstBltState s = ref _dstBlt;
        ReadCoordField(ref r, f, 1, delta, ref s.Left);
        ReadCoordField(ref r, f, 2, delta, ref s.Top);
        ReadCoordField(ref r, f, 3, delta, ref s.Width);
        ReadCoordField(ref r, f, 4, delta, ref s.Height);
        ReadByteField(ref r, f, 5, ref s.Rop);

        if (Admit(s.Left, s.Top, s.Width, s.Height, clip))
        {
            _target.DstRop(new OrderRect(s.Left, s.Top, s.Width, s.Height), s.Rop, clip);
        }
    }

    private void PatBlt(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        ref PatBltState s = ref _patBlt;
        ReadCoordField(ref r, f, 1, delta, ref s.Left);
        ReadCoordField(ref r, f, 2, delta, ref s.Top);
        ReadCoordField(ref r, f, 3, delta, ref s.Width);
        ReadCoordField(ref r, f, 4, delta, ref s.Height);
        ReadByteField(ref r, f, 5, ref s.Rop);
        ReadColorField(ref r, f, 6, ref s.BackColor);
        ReadColorField(ref r, f, 7, ref s.ForeColor);

        // update_read_brush: fields 8–12 are BrushOrgX, BrushOrgY, BrushStyle, BrushHatch, BrushExtra.
        uint brushFields = (f >> 7) & 0x1F;
        if ((brushFields & 0x01) != 0)
        {
            s.OriginX = r.U8();
        }

        if ((brushFields & 0x02) != 0)
        {
            s.OriginY = r.U8();
        }

        if ((brushFields & 0x04) != 0)
        {
            s.Style = r.U8();
        }

        if ((brushFields & 0x08) != 0)
        {
            s.Hatch = r.U8();
        }

        if ((brushFields & 0x10) != 0)
        {
            r.Take(7).CopyTo(_brushExtra);
        }

        if ((s.Style & CachedBrush) != 0)
        {
            throw new RdpProtocolException("PatBlt uses a cached brush, but the brush cache was not negotiated.");
        }

        if (s.Style > BrushPattern)
        {
            throw new RdpProtocolException($"Unsupported PatBlt brush style 0x{s.Style:X2}.");
        }

        if (s.Style == BrushHatched && s.Hatch >= HatchPatterns.Length / 8)
        {
            throw new RdpProtocolException($"Invalid PatBlt brush hatch {s.Hatch}.");
        }

        if (!Admit(s.Left, s.Top, s.Width, s.Height, clip))
        {
            return;
        }

        var rect = new OrderRect(s.Left, s.Top, s.Width, s.Height);
        uint fore = ToBgra(s.ForeColor);
        if (s.Style == BrushSolid && s.Rop == PatCopy)
        {
            _target.FillRect(rect, fore, clip);
            return;
        }

        var brush = new OrderBrush(s.OriginX, s.OriginY, s.Style, s.Hatch, BuildPattern(s.Style, s.Hatch), fore, ToBgra(s.BackColor));
        _target.PatBlt(rect, s.Rop, brush, clip);
    }

    /// <summary>
    /// The 8×8 pattern, row 0 at the top. For BS_PATTERN FreeRDP stores BrushHatch as row 0 and BrushExtra[0..6] as
    /// rows 7..1 (the wire carries the rows bottom-up, the last one in BrushHatch).
    /// </summary>
    private byte[] BuildPattern(byte style, byte hatch)
    {
        byte[] pattern = new byte[8];
        if (style == BrushHatched)
        {
            HatchPatterns.Slice(hatch * 8, 8).CopyTo(pattern);
        }
        else if (style == BrushPattern)
        {
            pattern[0] = hatch;
            for (int row = 1; row < 8; row++)
            {
                pattern[row] = _brushExtra[7 - row];
            }
        }

        return pattern;
    }

    private void ScrBlt(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        ref ScrBltState s = ref _scrBlt;
        ReadCoordField(ref r, f, 1, delta, ref s.Left);
        ReadCoordField(ref r, f, 2, delta, ref s.Top);
        ReadCoordField(ref r, f, 3, delta, ref s.Width);
        ReadCoordField(ref r, f, 4, delta, ref s.Height);
        ReadByteField(ref r, f, 5, ref s.Rop);
        ReadCoordField(ref r, f, 6, delta, ref s.SrcX);
        ReadCoordField(ref r, f, 7, delta, ref s.SrcY);

        if (Admit(s.Left, s.Top, s.Width, s.Height, clip))
        {
            _target.ScreenCopy(s.SrcX, s.SrcY, new OrderRect(s.Left, s.Top, s.Width, s.Height), s.Rop, clip);
        }
    }

    private void OpaqueRect(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        ref OpaqueRectState s = ref _opaqueRect;
        ReadCoordField(ref r, f, 1, delta, ref s.Left);
        ReadCoordField(ref r, f, 2, delta, ref s.Top);
        ReadCoordField(ref r, f, 3, delta, ref s.Width);
        ReadCoordField(ref r, f, 4, delta, ref s.Height);
        ReadColorBytes(ref r, f, ref s.Color);

        if (Admit(s.Left, s.Top, s.Width, s.Height, clip))
        {
            _target.FillRect(new OrderRect(s.Left, s.Top, s.Width, s.Height), ToBgra(s.Color), clip);
        }
    }

    private void MultiOpaqueRect(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        // Fields 1–4 are the bounding rectangle; FreeRDP's gdi_multi_opaque_rect draws only the delta list.
        ref MultiOpaqueRectState s = ref _multiOpaqueRect;
        ReadCoordField(ref r, f, 1, delta, ref s.Left);
        ReadCoordField(ref r, f, 2, delta, ref s.Top);
        ReadCoordField(ref r, f, 3, delta, ref s.Width);
        ReadCoordField(ref r, f, 4, delta, ref s.Height);
        ReadColorBytes(ref r, f, ref s.Color);

        int count = s.Count;
        if (HasField(f, 8))
        {
            count = r.U8();
        }

        if (HasField(f, 9))
        {
            // cbData: like FreeRDP, the rectangles are decoded straight from the stream.
            _ = r.U16LE();
            ReadDeltaRects(ref r, count);
            s.Count = count;
        }
        else if (count > s.Count)
        {
            throw new RdpProtocolException($"MultiOpaqueRect grows to {count} rectangles without sending them (had {s.Count}).");
        }
        else
        {
            s.Count = count;
        }

        uint bgra = ToBgra(s.Color);
        for (int i = 0; i < s.Count; i++)
        {
            DeltaRect d = _deltaRects[i];
            if (Admit(d.Left, d.Top, d.Width, d.Height, clip))
            {
                _target.FillRect(new OrderRect(d.Left, d.Top, d.Width, d.Height), bgra, clip);
            }
        }
    }

    /// <summary>
    /// TS_DELTA_RECTS (FreeRDP <c>update_read_delta_rects</c>): a nibble of zero bits per rectangle (left, top,
    /// width, height absent), then the present fields as variable-length deltas. Left/top accumulate from the
    /// previous rectangle (the first is relative to 0,0); an absent width/height repeats the previous one.
    /// </summary>
    private void ReadDeltaRects(ref ByteReader r, int count)
    {
        if (count > MaxDeltaRectangles)
        {
            throw new RdpProtocolException($"Delta rectangle list of {count} entries exceeds {MaxDeltaRectangles}.");
        }

        ReadOnlySpan<byte> zeroBits = r.Take((count + 1) / 2);
        Span<DeltaRect> rects = _deltaRects.AsSpan(0, count);
        rects.Clear();
        int flags = 0;
        for (int i = 0; i < count; i++)
        {
            if ((i & 1) == 0)
            {
                flags = zeroBits[i / 2];
            }

            ref DeltaRect d = ref rects[i];
            if ((flags & 0x80) == 0)
            {
                d.Left = ReadDelta(ref r);
            }

            if ((flags & 0x40) == 0)
            {
                d.Top = ReadDelta(ref r);
            }

            if ((flags & 0x20) == 0)
            {
                d.Width = ReadDelta(ref r);
            }
            else if (i > 0)
            {
                d.Width = rects[i - 1].Width;
            }

            if ((flags & 0x10) == 0)
            {
                d.Height = ReadDelta(ref r);
            }
            else if (i > 0)
            {
                d.Height = rects[i - 1].Height;
            }

            if (i > 0)
            {
                d.Left += rects[i - 1].Left;
                d.Top += rects[i - 1].Top;
            }

            flags = (flags << 4) & 0xFF;
        }
    }

    /// <summary>FreeRDP <c>update_read_delta</c>: 1 byte (7-bit signed) or 2 bytes (15-bit signed) when bit 7 is set.</summary>
    private static int ReadDelta(ref ByteReader r)
    {
        int first = r.U8();
        int value = (first & 0x40) != 0 ? first | ~0x3F : first & 0x3F;
        if ((first & 0x80) != 0)
        {
            value = (value << 8) | r.U8();
        }

        return value;
    }

    private void MemBlt(ref ByteReader r, uint f, bool delta, OrderRect? clip)
    {
        ref MemBltState s = ref _memBlt;
        ReadUInt16Field(ref r, f, 1, ref s.CacheId);
        ReadCoordField(ref r, f, 2, delta, ref s.Left);
        ReadCoordField(ref r, f, 3, delta, ref s.Top);
        ReadCoordField(ref r, f, 4, delta, ref s.Width);
        ReadCoordField(ref r, f, 5, delta, ref s.Height);
        ReadByteField(ref r, f, 6, ref s.Rop);
        ReadCoordField(ref r, f, 7, delta, ref s.SrcX);
        ReadCoordField(ref r, f, 8, delta, ref s.SrcY);
        ReadUInt16Field(ref r, f, 9, ref s.CacheIndex);

        // The high byte of cacheId is a colour-table index (palette sessions only); it is ignored. A slot that
        // was never filled is skipped: FreeRDP notes XP-SP2 servers ask for cached bitmaps they never defined.
        CachedBitmap? bitmap = Lookup(s.CacheId & 0xFF, s.CacheIndex);
        if (bitmap is not null && Admit(s.Left, s.Top, s.Width, s.Height, clip))
        {
            _target.CachedBlit(bitmap, s.SrcX, s.SrcY, new OrderRect(s.Left, s.Top, s.Width, s.Height), s.Rop, clip);
        }
    }

    // ── Field helpers ───────────────────────────────────────────────────────────────────────────

    private static bool HasField(uint fieldFlags, int field) => (fieldFlags & (1u << (field - 1))) != 0;

    /// <summary>
    /// FreeRDP <c>update_read_coord</c>: an absolute int16, or an int8 delta added to the previous value. Results
    /// wrap to int16 (a legitimate server never leaves that range), so no later arithmetic can overflow.
    /// </summary>
    private static int ReadCoord(ref ByteReader r, bool delta, int value) =>
        delta ? unchecked((short)(value + (sbyte)r.U8())) : unchecked((short)r.U16LE());

    private static void ReadCoordField(ref ByteReader r, uint f, int field, bool delta, ref int value)
    {
        if (HasField(f, field))
        {
            value = ReadCoord(ref r, delta, value);
        }
    }

    private static void ReadByteField(ref ByteReader r, uint f, int field, ref byte value)
    {
        if (HasField(f, field))
        {
            value = r.U8();
        }
    }

    private static void ReadUInt16Field(ref ByteReader r, uint f, int field, ref ushort value)
    {
        if (HasField(f, field))
        {
            value = r.U16LE();
        }
    }

    /// <summary>FreeRDP <c>update_read_color</c>: three bytes, the first in bits 0–7.</summary>
    private static void ReadColorField(ref ByteReader r, uint f, int field, ref uint value)
    {
        if (HasField(f, field))
        {
            ReadOnlySpan<byte> c = r.Take(3);
            value = c[0] | ((uint)c[1] << 8) | ((uint)c[2] << 16);
        }
    }

    /// <summary>OpaqueRect / MultiOpaqueRect colour: fields 5, 6, 7 each replace one byte of the previous colour.</summary>
    private static void ReadColorBytes(ref ByteReader r, uint f, ref uint color)
    {
        if (HasField(f, 5))
        {
            color = (color & 0x00FFFF00) | r.U8();
        }

        if (HasField(f, 6))
        {
            color = (color & 0x00FF00FF) | ((uint)r.U8() << 8);
        }

        if (HasField(f, 7))
        {
            color = (color & 0x0000FFFF) | ((uint)r.U8() << 16);
        }
    }

    /// <summary>
    /// Wire colour to opaque BGRA (FreeRDP <c>gdi_decode_color</c>): 24/32 bpp sessions send BGR24 (first byte red),
    /// 16 bpp RGB565 and 15 bpp RGB555 in the low 16 bits, 8 bpp a palette index (grey here, as for bitmaps). 15/16
    /// bpp expand through <see cref="Framebuffer.ConvertRow"/> so fills match bitmap pixels exactly.
    /// </summary>
    private uint ToBgra(uint wire)
    {
        if (_sessionBpp >= 24)
        {
            return 0xFF000000u | ((wire & 0xFF) << 16) | (wire & 0xFF00) | ((wire >> 16) & 0xFF);
        }

        Span<byte> source = [(byte)wire, (byte)(wire >> 8)];
        Span<byte> pixel = stackalloc byte[4];
        Framebuffer.ConvertRow(_sessionBpp == 8 ? source[..1] : source, pixel, _sessionBpp);
        return BinaryPrimitives.ReadUInt32LittleEndian(pixel);
    }

    /// <summary>
    /// Whether an operation reaches the desktop through its bounds; charges the visible area to the work budget.
    /// </summary>
    private bool Admit(int x, int y, int width, int height, OrderRect? clip)
    {
        if (width <= 0 || height <= 0)
        {
            return false;   // gdi_CRgnToRect: older Windows servers send empty rectangles
        }

        long left = Math.Max(x, 0L), top = Math.Max(y, 0L);
        long right = Math.Min((long)x + width, _desktopWidth), bottom = Math.Min((long)y + height, _desktopHeight);
        if (clip is { } c)
        {
            left = Math.Max(left, c.X);
            top = Math.Max(top, c.Y);
            right = Math.Min(right, (long)c.X + c.Width);
            bottom = Math.Min(bottom, (long)c.Y + c.Height);
        }

        if (right <= left || bottom <= top)
        {
            return false;
        }

        Spend((right - left) * (bottom - top));
        return true;
    }

    private void Spend(long pixels)
    {
        _budget -= pixels;
        if (_budget < 0)
        {
            throw new RdpProtocolException("Drawing orders exceed the per-update work budget.");
        }
    }

    // ── Secondary orders ────────────────────────────────────────────────────────────────────────

    private void DecodeSecondary(ref ByteReader r)
    {
        int orderLength = unchecked((short)r.U16LE());
        ushort extraFlags = r.U16LE();
        byte orderType = r.U8();

        // MS-RDPEGDI 2.2.2.2.1.2.1.1: orderLength is the order size minus 13, so orderLength + 7 bytes follow the
        // 6-byte header. FreeRDP rejects a negative length rather than reading a header-only order.
        if (orderLength < 0)
        {
            throw new RdpProtocolException($"Secondary drawing order 0x{orderType:X2} has a negative length {orderLength}.");
        }

        int bodyLength = orderLength + 7;
        if (bodyLength > r.Remaining)
        {
            throw new RdpProtocolException($"Secondary drawing order 0x{orderType:X2} needs {bodyLength} bytes, {r.Remaining} left.");
        }

        // Parsing inside the body slice enforces FreeRDP's "read too much" check; any shortfall is skipped.
        var body = new ByteReader(r.Take(bodyLength));
        switch (orderType)
        {
            case CacheBitmapUncompressed:
            case CacheBitmapCompressed:
                CacheBitmapV1(ref body, orderType == CacheBitmapCompressed, extraFlags);
                break;
            case CacheBitmapV2Uncompressed:
            case CacheBitmapV2Compressed:
                CacheBitmapV2(ref body, orderType == CacheBitmapV2Compressed, extraFlags);
                break;
            default:
                // Cache Bitmap V3, color table, glyph, brush and unknown types are not negotiated; skip them.
                break;
        }
    }

    /// <summary>TS_CACHE_BITMAP_ORDER (FreeRDP <c>update_read_cache_bitmap_order</c>).</summary>
    private void CacheBitmapV1(ref ByteReader r, bool compressed, ushort extraFlags)
    {
        int cacheId = r.U8();
        r.Skip(1);                    // pad1Octet
        int width = r.U8();
        int height = r.U8();
        int bpp = r.U8();
        if (bpp is < 1 or > 32)
        {
            throw new RdpProtocolException($"Invalid CACHE_BITMAP bitmapBpp {bpp}.");
        }

        int bitmapLength = r.U16LE();
        int cacheIndex = r.U16LE();
        if (compressed && (extraFlags & NoBitmapCompressionHdr) == 0)
        {
            r.Skip(8);                // TS_CD_HEADER, counted in bitmapLength
            if (bitmapLength < 8)
            {
                throw new RdpProtocolException("CACHE_BITMAP length is shorter than its compression header.");
            }

            bitmapLength -= 8;
        }

        if (bitmapLength == 0)
        {
            throw new RdpProtocolException("CACHE_BITMAP carries no bitmap data.");
        }

        StoreBitmap(cacheId, cacheIndex, width, height, bpp, compressed, r.Take(bitmapLength));
    }

    /// <summary>TS_CACHE_BITMAP_REV2_ORDER (FreeRDP <c>update_read_cache_bitmap_v2_order</c>).</summary>
    private void CacheBitmapV2(ref ByteReader r, bool compressed, ushort extraFlags)
    {
        // extraFlags: cacheId bits 0–2, bitsPerPixelId bits 3–6, CBR2 flags bits 7–15.
        int cacheId = extraFlags & 0x0007;
        int flags = (extraFlags & 0xFF80) >> 7;
        int bitsPerPixelId = (extraFlags & 0x0078) >> 3;
        int bpp = bitsPerPixelId switch
        {
            3 => 8,
            4 => 16,
            5 => 24,
            6 => 32,
            _ => throw new RdpProtocolException($"Invalid CACHE_BITMAP_REV2 bitsPerPixelId {bitsPerPixelId}."),
        };

        // update_gdi_cache_bitmap_v2: a 15 bpp session sends its 5-5-5 bitmaps tagged as 16 bpp.
        if (bpp == 16 && _sessionBpp == 15)
        {
            bpp = 15;
        }

        if ((flags & Cbr2PersistentKeyPresent) != 0)
        {
            r.Skip(8);                // key1, key2
        }

        int width = ReadTwoByteUnsigned(ref r);
        int height = (flags & Cbr2HeightSameAsWidth) != 0 ? width : ReadTwoByteUnsigned(ref r);
        int bitmapLength = ReadFourByteUnsigned(ref r);
        int cacheIndex = ReadTwoByteUnsigned(ref r);
        if ((flags & Cbr2DoNotCache) != 0)
        {
            cacheIndex = WaitingListIndex;
        }

        if (compressed && (flags & Cbr2NoBitmapCompressionHdr) == 0)
        {
            // TS_CD_HEADER: cbCompFirstRowSize, cbCompMainBodySize, cbScanWidth, cbUncompressedSize. FreeRDP
            // takes the main body size as the data length.
            r.Skip(2);
            bitmapLength = r.U16LE();
            r.Skip(4);
        }

        if (bitmapLength == 0)
        {
            throw new RdpProtocolException("CACHE_BITMAP_REV2 carries no bitmap data.");
        }

        StoreBitmap(cacheId, cacheIndex, width, height, bpp, compressed, r.Take(bitmapLength));
    }

    /// <summary>FreeRDP <c>update_read_2byte_unsigned</c>: 7 bits, or 15 bits when bit 7 of the first byte is set.</summary>
    private static int ReadTwoByteUnsigned(ref ByteReader r)
    {
        int first = r.U8();
        return (first & 0x80) != 0 ? ((first & 0x7F) << 8) | r.U8() : first;
    }

    /// <summary>FreeRDP <c>update_read_4byte_unsigned</c>: the top two bits count extra bytes, big-endian, 30 bits max.</summary>
    private static int ReadFourByteUnsigned(ref ByteReader r)
    {
        int first = r.U8();
        int value = first & 0x3F;
        for (int extra = first >> 6; extra > 0; extra--)
        {
            value = (value << 8) | r.U8();
        }

        return value;
    }

    /// <summary>
    /// Decodes and stores one cache entry under FreeRDP's <c>bitmap_cache_put</c> rules: each cell has one extra
    /// slot reached by BITMAP_CACHE_WAITING_LIST_INDEX (0x7FFF), and an id one past the last cell is FreeRDP's
    /// over-allocated cell for off-by-one servers (MemBlt can never read it, so the bitmap is decoded and dropped).
    /// </summary>
    private void StoreBitmap(int cacheId, int cacheIndex, int width, int height, int bpp, bool compressed, ReadOnlySpan<byte> data)
    {
        CachedBitmap?[]? cell = null;
        int slot = 0;
        if (cacheId < _cells.Length)
        {
            cell = _cells[cacheId];
            int last = cell.Length - 1;
            slot = cacheIndex == WaitingListIndex ? last : cacheIndex;
            if (slot > last)
            {
                throw new RdpProtocolException($"Bitmap cache index {cacheIndex} is outside cell {cacheId} ({last} entries).");
            }
        }
        else if (cacheId > _cells.Length || (cacheIndex != 0 && cacheIndex != WaitingListIndex))
        {
            throw new RdpProtocolException($"Bitmap cache cell {cacheId} index {cacheIndex} was not negotiated.");
        }

        CheckBitmapSize(width, height);
        long added = (long)width * height * 4 - (cell?[slot]?.Bgra.Length ?? 0);
        if (cell is not null && _cacheBytes + added > MaxCacheBytes)
        {
            throw new RdpProtocolException("Bitmap cache exceeds its memory cap.");
        }

        CachedBitmap bitmap = DecodeBitmap(width, height, bpp, compressed, data);
        if (cell is null)
        {
            return;
        }

        _cacheBytes += added;
        cell[slot] = bitmap;
    }

    private CachedBitmap? Lookup(int cacheId, int cacheIndex)
    {
        // bitmap_cache_get: unknown cell (including 0xFF, the offscreen cache) or index past the cell → nothing.
        if (cacheId >= _cells.Length)
        {
            return null;
        }

        CachedBitmap?[] cell = _cells[cacheId];
        int slot = cacheIndex == WaitingListIndex ? cell.Length - 1 : cacheIndex;
        return slot < cell.Length ? cell[slot] : null;
    }

    private static void CheckBitmapSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxCachedBitmapSide || height > MaxCachedBitmapSide)
        {
            throw new RdpProtocolException($"Invalid cached bitmap size {width}x{height}.");
        }
    }

    /// <summary>
    /// FreeRDP <c>gdi_Bitmap_Decompress</c>: uncompressed rows are bottom-up in the bitmap's own depth; compressed
    /// 32 bpp is RDP 6.0 planar (decoded top-down), anything shallower interleaved RLE (bottom-up).
    /// </summary>
    private CachedBitmap DecodeBitmap(int width, int height, int bpp, bool compressed, ReadOnlySpan<byte> data)
    {
        int bytesPerPixel = bpp switch { 8 => 1, 15 => 2, 16 => 2, 24 => 3, 32 => 4, _ => 0 };
        if (bytesPerPixel == 0)
        {
            throw new RdpProtocolException($"Unsupported cached bitmap colour depth {bpp}.");
        }

        CheckBitmapSize(width, height);
        Spend((long)width * height);

        // width, height ≤ 4096: every size below is at most 64 MiB and fits an int.
        int scanline = width * bytesPerPixel;
        int needed = scanline * height;
        if (!compressed)
        {
            if (data.Length < needed)
            {
                throw new RdpProtocolException($"Uncompressed cached bitmap needs {needed} bytes, got {data.Length}.");
            }

            byte[] pixels = new byte[width * height * 4];
            ConvertBottomUp(data, pixels, width, height, bpp, scanline);
            return new CachedBitmap(width, height, pixels);
        }

        if (bpp == 32)
        {
            byte[] planar = new byte[width * height * 4];
            Planar.Decompress(data, planar, width, height);
            foreach (ref uint pixel in MemoryMarshal.Cast<byte, uint>(planar.AsSpan()))
            {
                pixel |= 0xFF000000u;   // the cache contract is opaque; planar keeps a decoded alpha plane
            }

            return new CachedBitmap(width, height, planar);
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(needed);
        try
        {
            Span<byte> decoded = scratch.AsSpan(0, needed);
            int written = InterleavedRle.Decompress(data, decoded, width, height, bytesPerPixel);
            // A stream that ends early leaves the rest black, never stale pooled memory (as Framebuffer does).
            decoded[written..].Clear();
            byte[] pixels = new byte[width * height * 4];
            ConvertBottomUp(decoded, pixels, width, height, bpp, scanline);
            return new CachedBitmap(width, height, pixels);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ConvertBottomUp(ReadOnlySpan<byte> bottomUp, byte[] bgra, int width, int height, int bpp, int scanline)
    {
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            Framebuffer.ConvertRow(bottomUp.Slice((height - 1 - y) * scanline, scanline), bgra.AsSpan(y * stride, stride), bpp);
        }
    }

    // ── Alternate secondary orders ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Steps over an alternate secondary order using the sizes FreeRDP's readers consume (none is negotiated, so
    /// nothing is drawn). Types whose size cannot be determined — composed desktop orders, which FreeRDP accepts
    /// without consuming anything, and unknown types — are rejected.
    /// </summary>
    private static void SkipAltSecondary(ref ByteReader r, byte controlFlags)
    {
        int orderType = controlFlags >> 2;
        switch (orderType)
        {
            case AltSwitchSurface:
                r.Skip(2);                            // bitmapId
                break;
            case AltCreateOffscreenBitmap:
                {
                    int flags = r.U16LE();            // id (15 bits) | deleteListPresent (0x8000)
                    int cx = r.U16LE();
                    int cy = r.U16LE();
                    if (cx == 0 || cy == 0)
                    {
                        throw new RdpProtocolException($"Invalid offscreen bitmap size {cx}x{cy}.");
                    }

                    if ((flags & 0x8000) != 0)
                    {
                        r.Skip(2 * r.U16LE());        // cIndices, indices
                    }

                    break;
                }
            case AltStreamBitmapFirst:
                {
                    byte bitmapFlags = r.U8();
                    int bpp = r.U8();
                    if (bpp is < 1 or > 32)
                    {
                        throw new RdpProtocolException($"Invalid stream bitmap bpp {bpp}.");
                    }

                    r.Skip(6);                        // bitmapType, bitmapWidth, bitmapHeight
                    r.Skip((bitmapFlags & StreamBitmapV2) != 0 ? 4 : 2);   // bitmapSize
                    r.Skip(r.U16LE());                // bitmapBlockSize, bitmapBlock
                    break;
                }
            case AltStreamBitmapNext:
                r.Skip(3);                            // bitmapFlags, bitmapType
                r.Skip(r.U16LE());                    // bitmapBlockSize, bitmapBlock
                break;
            case AltCreateNineGridBitmap:
                {
                    int bpp = r.U8();
                    if (bpp is < 1 or > 32)
                    {
                        throw new RdpProtocolException($"Invalid nine-grid bitmap bpp {bpp}.");
                    }

                    r.Skip(18);                       // bitmapId, TS_NINEGRID_BITMAP_INFO
                    break;
                }
            case AltGdiPlusFirst:
            case AltGdiPlusEnd:
                {
                    r.Skip(1);                        // pad1Octet
                    int size = r.U16LE();             // cbSize
                    r.Skip(8);                        // cbTotalSize, cbTotalEmfSize
                    r.Skip(size);                     // emfRecords
                    break;
                }
            case AltGdiPlusNext:
                r.Skip(1);                            // pad1Octet
                r.Skip(r.U16LE());                    // cbSize, emfRecords
                break;
            case AltGdiPlusCacheFirst:
            case AltGdiPlusCacheEnd:
                {
                    r.Skip(5);                        // flags, cacheType, cacheIndex
                    int size = r.U16LE();             // cbSize
                    r.Skip(4);                        // cbTotalSize
                    r.Skip(size);                     // emfRecords
                    break;
                }
            case AltGdiPlusCacheNext:
                r.Skip(5);                            // flags, cacheType, cacheIndex
                r.Skip(r.U16LE());                    // cbSize, emfRecords
                break;
            case AltWindow:
                {
                    // TS_WINDOW_ORDER_HEADER (MS-RDPERP): orderSize counts the whole order, controlFlags included.
                    int orderSize = r.U16LE();
                    if (orderSize < 7)
                    {
                        throw new RdpProtocolException($"Window order size {orderSize} is shorter than its header.");
                    }

                    r.Skip(orderSize - 3);
                    break;
                }
            case AltFrameMarker:
                r.Skip(4);                            // action
                break;
            default:
                throw new RdpProtocolException($"Unsupported alternate secondary drawing order 0x{orderType:X2}.");
        }
    }

    // ── Per-order state ─────────────────────────────────────────────────────────────────────────

    private struct DstBltState
    {
        public int Left, Top, Width, Height;
        public byte Rop;
    }

    private struct PatBltState
    {
        public int Left, Top, Width, Height;
        public byte Rop;
        public uint BackColor, ForeColor;
        public byte OriginX, OriginY, Style, Hatch;
    }

    private struct ScrBltState
    {
        public int Left, Top, Width, Height;
        public byte Rop;
        public int SrcX, SrcY;
    }

    private struct OpaqueRectState
    {
        public int Left, Top, Width, Height;
        public uint Color;
    }

    private struct MultiOpaqueRectState
    {
        public int Left, Top, Width, Height;
        public uint Color;
        public int Count;
    }

    private struct MemBltState
    {
        public ushort CacheId;
        public int Left, Top, Width, Height;
        public byte Rop;
        public int SrcX, SrcY;
        public ushort CacheIndex;
    }

    private struct DeltaRect
    {
        public int Left, Top, Width, Height;
    }
}
