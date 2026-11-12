using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The drawing-order parser and bitmap cache (<see cref="OrderDecoder"/>), driven with wire bytes built by hand
/// from the MS-RDPEGDI 2.2.2 layouts. A recording <see cref="IOrderTarget"/> captures
/// what the decoder asks the surface to do.
/// </summary>
[TestClass]
public sealed class OrderDecoderTests
{
    // Top-down BGRA of the 2×2 test bitmap: blue, white / red, green.
    private const string TopDown2x2 = "FF0000FF" + "FFFFFFFF" + "0000FFFF" + "00FF00FF";

    /// <summary>TS_CACHE_BITMAP_REV2, uncompressed 2×2 at 16 bpp, stored at cache 1 index 3.</summary>
    private static readonly byte[] CacheV2Uncompressed16 =
    [
        0x03,                       // controlFlags: TS_STANDARD | TS_SECONDARY
        0x04, 0x00,                 // orderLength = 4 → 4 + 7 = 11 bytes follow orderType
        0xA1, 0x00,                 // extraFlags: cacheId 1 (bits 0-2) | bitsPerPixelId 4 = 16 bpp (0x20) | CBR2_HEIGHT_SAME_AS_WIDTH (0x01 << 7)
        0x04,                       // orderType: TS_CACHE_BITMAP_UNCOMPRESSED_REV2
        0x02,                       // bitmapWidth (2-byte unsigned, 1-byte form) = 2; height = width
        0x08,                       // bitmapLength (4-byte unsigned, 1-byte form) = 8
        0x03,                       // cacheIndex (2-byte unsigned, 1-byte form) = 3
        0x00, 0xF8, 0xE0, 0x07,     // bottom row first (DIB order): red 0xF800, green 0x07E0
        0x1F, 0x00, 0xFF, 0xFF,     // top row: blue 0x001F, white 0xFFFF
    ];

    /// <summary>MEMBLT with every field: cache 1 index 3 onto 40,50 2×2, SRCCOPY.</summary>
    private static readonly byte[] MemBltCache1Index3 =
    [
        0x09,                       // controlFlags: TS_STANDARD | TS_TYPE_CHANGE
        0x0D,                       // orderType: TS_ENC_MEMBLT_ORDER
        0xFF, 0x01,                 // fieldFlags (2 bytes LE): fields 1-9
        0x01, 0x05,                 // cacheId = 0x0501: cache 1 (low byte), colour-table index 5 (high byte, ignored)
        0x28, 0x00,                 // nLeftRect = 40
        0x32, 0x00,                 // nTopRect = 50
        0x02, 0x00,                 // nWidth = 2
        0x02, 0x00,                 // nHeight = 2
        0xCC,                       // bRop = SRCCOPY
        0x00, 0x00,                 // nXSrc = 0
        0x00, 0x00,                 // nYSrc = 0
        0x03, 0x00,                 // cacheIndex = 3
    ];

    /// <summary>TS_CACHE_BITMAP_REV2 compressed 4×2 at 16 bpp without TS_CD_HEADER, cache 0 index 0.</summary>
    private static readonly byte[] CacheV2RleNoHeader =
    [
        0x03,                       // TS_STANDARD | TS_SECONDARY
        0x03, 0x00,                 // orderLength = 3 → 10 bytes follow
        0x20, 0x04,                 // extraFlags 0x0420: cacheId 0 | 16 bpp (0x20) | CBR2_NO_BITMAP_COMPRESSION_HDR (0x08 << 7)
        0x05,                       // TS_CACHE_BITMAP_COMPRESSED_REV2
        0x04,                       // bitmapWidth = 4
        0x02,                       // bitmapHeight = 2
        0x06,                       // bitmapLength = 6
        0x00,                       // cacheIndex = 0
        0x64, 0x00, 0xF8,           // REGULAR_COLOR_RUN (0x03 << 5 | run 4): 4 × red — the bottom row
        0x64, 0x1F, 0x00,           // REGULAR_COLOR_RUN run 4: 4 × blue — the top row
    ];

    /// <summary>TS_CACHE_BITMAP_REV2 compressed 4×2 at 16 bpp with a TS_CD_HEADER, cache 0 index 1.</summary>
    private static readonly byte[] CacheV2RleWithHeader =
    [
        0x03,                       // TS_STANDARD | TS_SECONDARY
        0x0B, 0x00,                 // orderLength = 11 → 18 bytes follow
        0x20, 0x00,                 // extraFlags: cacheId 0 | 16 bpp; compression header present
        0x05,                       // TS_CACHE_BITMAP_COMPRESSED_REV2
        0x04,                       // bitmapWidth = 4
        0x02,                       // bitmapHeight = 2
        0x0E,                       // bitmapLength = 14 (8-byte header + 6)
        0x01,                       // cacheIndex = 1
        0x00, 0x00,                 // cbCompFirstRowSize = 0
        0x06, 0x00,                 // cbCompMainBodySize = 6 (the data length)
        0x08, 0x00,                 // cbScanWidth = 8
        0x10, 0x00,                 // cbUncompressedSize = 16
        0x64, 0xE0, 0x07,           // 4 × green (bottom row)
        0x64, 0xFF, 0xFF,           // 4 × white (top row)
    ];

    /// <summary>MEMBLT with every field: cache 0 index 0 onto 0,0 4×2.</summary>
    private static readonly byte[] MemBltCache0Index0 =
    [
        0x09, 0x0D,                 // TS_STANDARD | TS_TYPE_CHANGE, TS_ENC_MEMBLT_ORDER
        0xFF, 0x01,                 // fields 1-9
        0x00, 0x00,                 // cacheId = 0
        0x00, 0x00,                 // nLeftRect = 0
        0x00, 0x00,                 // nTopRect = 0
        0x04, 0x00,                 // nWidth = 4
        0x02, 0x00,                 // nHeight = 2
        0xCC,                       // SRCCOPY
        0x00, 0x00, 0x00, 0x00,     // nXSrc = 0, nYSrc = 0
        0x00, 0x00,                 // cacheIndex = 0
    ];

    /// <summary>MEMBLT repeating the previous one except cacheIndex = 1.</summary>
    private static readonly byte[] MemBltIndex1 =
    [
        0x01,                       // TS_STANDARD (order type carried over)
        0x00, 0x01,                 // fieldFlags: field 9 only
        0x01, 0x00,                 // cacheIndex = 1
    ];

    /// <summary>OPAQUE_RECT with bounds 0,0..99,49 (inclusive): 10,10 20×20 in RGB565 red.</summary>
    private static readonly byte[] OpaqueRectWithBounds16 =
    [
        0x0D,                       // TS_STANDARD | TS_BOUNDS | TS_TYPE_CHANGE
        0x0A,                       // TS_ENC_OPAQUERECT_ORDER
        0x7F,                       // fieldFlags (1 byte): fields 1-7
        0x0F,                       // bounds flags: BOUND_LEFT | TOP | RIGHT | BOTTOM, all absolute
        0x00, 0x00, 0x00, 0x00,     // left = 0, top = 0
        0x63, 0x00, 0x31, 0x00,     // right = 99, bottom = 49
        0x0A, 0x00, 0x0A, 0x00,     // nLeftRect = 10, nTopRect = 10
        0x14, 0x00, 0x14, 0x00,     // nWidth = 20, nHeight = 20
        0x00, 0xF8, 0x00,           // colour bytes (fields 5-7): 0xF800 = RGB565 red
    ];

    /// <summary>MULTI_OPAQUE_RECT with three delta-encoded rectangles in 0xAABBCC.</summary>
    private static readonly byte[] MultiOpaqueRect =
    [
        0x09, 0x12,                 // TS_STANDARD | TS_TYPE_CHANGE, TS_ENC_MULTI_OPAQUERECT_ORDER
        0xFF, 0x01,                 // fields 1-9
        0x00, 0x00, 0x00, 0x00,     // bounding rect left 0, top 0 (not drawn)
        0x64, 0x00, 0x64, 0x00,     // bounding rect width 100, height 100
        0xAA, 0xBB, 0xCC,           // RedOrPaletteIndex, Green, Blue
        0x03,                       // nDeltaEntries = 3
        0x0D, 0x00,                 // cbData = 13
        0x0A,                       // zero bits: rect 0 nibble 0x0 (all present); rect 1 nibble 0xA (left, width absent)
        0x10,                       // zero bits: rect 2 nibble 0x1 (height absent); low nibble padding
        0x0A, 0x14, 0x1E, 0x05,     // rect 0: left 10, top 20, width 30, height 5
        0x0A, 0x07,                 // rect 1: top +10 → 30, height 7 (left +0 → 10, width repeats 30)
        0xFF, 0x9C,                 // rect 2: left delta, 2-byte form: (sign-extended 0x3F << 8) | 0x9C = -100 → -90
        0x76,                       // rect 2: top delta, 1-byte negative: 0x40 | (-10 & 0x3F) → 20
        0x80, 0x8C,                 // rect 2: width, 2-byte form = 140 (height repeats 7)
    ];

    /// <summary>PATBLT with a solid brush and PATCOPY, then one with an 8×8 pattern brush and PATINVERT.</summary>
    private static readonly byte[] PatBltSolidThenPattern =
    [
        0x09, 0x01,                 // TS_STANDARD | TS_TYPE_CHANGE, TS_ENC_PATBLT_ORDER
        0xFF, 0x0F,                 // fieldFlags: fields 1-12
        0x00, 0x00, 0x00, 0x00,     // nLeftRect = 0, nTopRect = 0
        0x10, 0x00, 0x08, 0x00,     // nWidth = 16, nHeight = 8
        0xF0,                       // bRop = PATCOPY
        0x00, 0x00, 0x00,           // BackColor
        0x12, 0x34, 0x56,           // ForeColor: R 0x12, G 0x34, B 0x56
        0x00, 0x00,                 // BrushOrgX, BrushOrgY
        0x00,                       // BrushStyle = BS_SOLID
        0x00,                       // BrushHatch
        0, 0, 0, 0, 0, 0, 0,        // BrushExtra
        0x01,                       // TS_STANDARD (PATBLT carried over)
        0xF0, 0x0F,                 // fieldFlags: fields 5-12
        0x5A,                       // bRop = PATINVERT
        0xFF, 0xFF, 0xFF,           // BackColor = white
        0x00, 0x00, 0x80,           // ForeColor: R 0, G 0, B 0x80
        0x03, 0x05,                 // BrushOrgX = 3, BrushOrgY = 5
        0x03,                       // BrushStyle = BS_PATTERN
        0xAA,                       // BrushHatch = pattern row 0 (top)
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, // BrushExtra[0..6] = rows 7..1
    ];

    /// <summary>SCRBLT with every field, then a delta-coordinate SCRBLT changing left and the source.</summary>
    private static readonly byte[] ScrBltFullThenDelta =
    [
        0x09, 0x02,                 // TS_STANDARD | TS_TYPE_CHANGE, TS_ENC_SCRBLT_ORDER
        0x7F,                       // fields 1-7
        0x64, 0x00, 0x32, 0x00,     // nLeftRect = 100, nTopRect = 50
        0xC8, 0x00, 0x64, 0x00,     // nWidth = 200, nHeight = 100
        0xCC,                       // bRop = SRCCOPY
        0x6E, 0x00, 0x28, 0x00,     // nXSrc = 110, nYSrc = 40
        0x11,                       // TS_STANDARD | TS_DELTA_COORDINATES
        0x61,                       // fields 1, 6, 7
        0x0A,                       // nLeftRect += 10 → 110
        0xF6,                       // nXSrc += -10 → 100
        0x02,                       // nYSrc += 2 → 42
    ];

    /// <summary>DSTBLT with every field: 5,6 7×8 DSTINVERT.</summary>
    private static readonly byte[] DstBltFull =
    [
        0x09, 0x00,                 // TS_STANDARD | TS_TYPE_CHANGE, TS_ENC_DSTBLT_ORDER
        0x1F,                       // fields 1-5
        0x05, 0x00, 0x06, 0x00,     // nLeftRect = 5, nTopRect = 6
        0x07, 0x00, 0x08, 0x00,     // nWidth = 7, nHeight = 8
        0x55,                       // bRop = DSTINVERT
    ];

    /// <summary>TS_CACHE_GLYPH (not decoded): skipped by its orderLength.</summary>
    private static readonly byte[] CacheGlyphSkipped =
    [
        0x03,                       // TS_STANDARD | TS_SECONDARY
        0x02, 0x00,                 // orderLength = 2 → 9 bytes follow
        0x00, 0x00,                 // extraFlags
        0x03,                       // TS_CACHE_GLYPH
        0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05,
    ];

    /// <summary>TS_ALTSEC_FRAME_MARKER: controlFlags (0x0D &lt;&lt; 2) | TS_SECONDARY, action TS_FRAME_START.</summary>
    private static readonly byte[] FrameMarker = [0x36, 0x00, 0x00, 0x00, 0x00];

    // ── Primary orders ───────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void OpaqueRectDecodesFullFieldsThenCarriesStateForDeltasAndElidedFields()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        byte[] first =
        [
            0x09,                   // TS_STANDARD | TS_TYPE_CHANGE
            0x0A,                   // TS_ENC_OPAQUERECT_ORDER
            0x7F,                   // fields 1-7
            0x0A, 0x00,             // nLeftRect = 10
            0x14, 0x00,             // nTopRect = 20
            0x1E, 0x00,             // nWidth = 30
            0x28, 0x00,             // nHeight = 40
            0x11,                   // RedOrPaletteIndex
            0x22,                   // Green
            0x33,                   // Blue
        ];
        decoder.Process(first, 1);
        AssertCalls(target, "Fill 10,20 30x40 FF112233");

        target.Calls.Clear();
        byte[] next =
        [
            0x11,                   // TS_STANDARD | TS_DELTA_COORDINATES (still OPAQUERECT, from the previous call)
            0x13,                   // fields 1, 2, 5
            0x05,                   // nLeftRect += 5 → 15
            0xFC,                   // nTopRect += -4 → 16
            0x44,                   // red = 0x44; green and blue kept
            0x41,                   // TS_STANDARD | TS_ZERO_FIELD_BYTE_BIT0: no field byte, the same order again
        ];
        decoder.Process(next, 2);
        AssertCalls(target, "Fill 15,16 30x40 FF442233", "Fill 15,16 30x40 FF442233");
    }

    [TestMethod]
    public void DstBltCarriesItsOrderTypeAndFieldsAcrossCalls()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        decoder.Process(DstBltFull, 1);

        byte[] delta =
        [
            0x11,                   // TS_STANDARD | TS_DELTA_COORDINATES (DSTBLT carried over)
            0x04,                   // field 3
            0x03,                   // nWidth += 3 → 10
        ];
        decoder.Process(delta, 1);

        AssertCalls(target, "DstRop 5,6 7x8 55", "DstRop 5,6 10x8 55");
    }

    [TestMethod]
    public void ScrBltDecodesSourceAndDestinationWithDeltaCoordinates()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        decoder.Process(ScrBltFullThenDelta, 2);
        AssertCalls(target, "ScreenCopy 110,40 -> 100,50 200x100 CC", "ScreenCopy 100,42 -> 110,50 200x100 CC");
    }

    [TestMethod]
    public void BoundsAreReadAbsoluteOrDeltaAndReusedByZeroBoundsDeltas()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        byte[] stream =
        [
            0x0D,                   // TS_STANDARD | TS_BOUNDS | TS_TYPE_CHANGE
            0x0A,                   // TS_ENC_OPAQUERECT_ORDER
            0x7F,                   // fields 1-7
            0x0F,                   // bounds: BOUND_LEFT | TOP | RIGHT | BOTTOM (absolute int16)
            0x00, 0x00, 0x00, 0x00, // left = 0, top = 0
            0x63, 0x00, 0x31, 0x00, // right = 99, bottom = 49 (inclusive → 100×50)
            0x00, 0x00, 0x00, 0x00, // nLeftRect = 0, nTopRect = 0
            0xC8, 0x00, 0xC8, 0x00, // nWidth = 200, nHeight = 200
            0x01, 0x02, 0x03,       // colour R 1, G 2, B 3
            0x65,                   // TS_STANDARD | TS_BOUNDS | TS_ZERO_BOUNDS_DELTAS | TS_ZERO_FIELD_BYTE_BIT0
            0x05,                   // TS_STANDARD | TS_BOUNDS
            0x00,                   // fieldFlags: none
            0x94,                   // bounds: BOUND_DELTA_LEFT | BOUND_RIGHT | BOUND_DELTA_BOTTOM
            0x05,                   // left += 5 → 5
            0xC7, 0x00,             // right = 199
            0xF7,                   // bottom += -9 → 40
            0x41,                   // no TS_BOUNDS: unclipped
            0x65,                   // TS_ZERO_BOUNDS_DELTAS again: the bounds survived the unbounded order
        ];
        decoder.Process(stream, 5);
        AssertCalls(target,
            "Fill 0,0 200x200 FF010203 clip 0,0 100x50",
            "Fill 0,0 200x200 FF010203 clip 0,0 100x50",
            "Fill 0,0 200x200 FF010203 clip 5,0 195x41",
            "Fill 0,0 200x200 FF010203",
            "Fill 0,0 200x200 FF010203 clip 5,0 195x41");

        target.Calls.Clear();
        byte[] inverted =
        [
            0x05, 0x00,             // TS_STANDARD | TS_BOUNDS, no fields
            0x04, 0x02, 0x00,       // BOUND_RIGHT = 2 < left 5: the box encloses nothing
            0x05, 0x00,             // TS_STANDARD | TS_BOUNDS, no fields
            0x04, 0x05, 0x00,       // BOUND_RIGHT = 5: a one-pixel-wide box
        ];
        decoder.Process(inverted, 2);
        AssertCalls(target, "Fill 0,0 200x200 FF010203 clip 5,0 1x41");
    }

    [TestMethod]
    public void MultiOpaqueRectDecodesItsDeltaEncodedRectangles()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        decoder.Process(MultiOpaqueRect, 1);
        AssertCalls(target, "Fill 10,20 30x5 FFAABBCC", "Fill 10,30 30x7 FFAABBCC", "Fill -90,20 140x7 FFAABBCC");

        target.Calls.Clear();
        byte[] fewer =
        [
            0x41,                   // TS_STANDARD | TS_ZERO_FIELD_BYTE_BIT0: one field byte for a two-byte order
            0x80,                   // field 8
            0x02,                   // nDeltaEntries = 2: the first two rectangles again, no list sent
        ];
        decoder.Process(fewer, 1);
        AssertCalls(target, "Fill 10,20 30x5 FFAABBCC", "Fill 10,30 30x7 FFAABBCC");

        byte[] more = [0x41, 0x80, 0x03];   // 3 rectangles without a list after shrinking to 2: rejected
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(more, 1));
    }

    [TestMethod]
    public void PatBltSolidPatCopyFillsAndOtherBrushesCarryTheirPattern()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        byte[] tail =
        [
            0x01, 0x00, 0x06,       // TS_STANDARD; fields 10, 11 (bits 9, 10 → second byte 0x06)
            0x02,                   // BrushStyle = BS_HATCHED
            0x02,                   // BrushHatch = HS_FDIAGONAL
            0x01, 0x00, 0x02,       // field 10
            0x00,                   // BrushStyle = BS_SOLID, but the ROP is still PATINVERT
            0x01, 0x00, 0x02,       // field 10
            0x01,                   // BrushStyle = BS_NULL
        ];
        decoder.Process([.. PatBltSolidThenPattern, .. tail], 5);
        AssertCalls(target,
            "Fill 0,0 16x8 FF123456",
            "PatBlt 0,0 16x8 5A style 3 hatch AA org 3,5 pattern AA07060504030201 fore FF000080 back FFFFFFFF",
            "PatBlt 0,0 16x8 5A style 2 hatch 02 org 3,5 pattern FEFDFBF7EFDFBF7F fore FF000080 back FFFFFFFF",
            "PatBlt 0,0 16x8 5A style 0 hatch 02 org 3,5 pattern 0000000000000000 fore FF000080 back FFFFFFFF",
            "PatBlt 0,0 16x8 5A style 1 hatch 02 org 3,5 pattern 0000000000000000 fore FF000080 back FFFFFFFF");
        Assert.HasCount(8, target.Brushes[0].Pattern8x8);
    }

    [TestMethod]
    [DataRow(0x81, -1, DisplayName = "cached brush (style | 0x80)")]
    [DataRow(0x04, -1, DisplayName = "unknown brush style 4")]
    [DataRow(0x02, 0x06, DisplayName = "hatch index past HS_DIAGCROSS")]
    public void PatBltRejectsBrushesItCannotRealise(int style, int hatch)
    {
        // A fresh decoder starts with PATBLT as the last order type.
        (OrderDecoder decoder, _) = Create();
        byte[] stream = hatch < 0
            ? [0x01, 0x00, 0x02, (byte)style]                       // field 10: BrushStyle
            : [0x01, 0x00, 0x06, (byte)style, (byte)hatch];         // fields 10, 11: BrushStyle, BrushHatch
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(stream, 1));
    }

    [TestMethod]
    [DataRow(32, 0x11, 0x22, 0x33, "FF112233", DisplayName = "32 bpp: BGR24, first byte red")]
    [DataRow(24, 0x11, 0x22, 0x33, "FF112233", DisplayName = "24 bpp: BGR24, first byte red")]
    [DataRow(16, 0x00, 0xF8, 0x00, "FFFF0000", DisplayName = "16 bpp: 0xF800 red")]
    [DataRow(16, 0xE0, 0x07, 0x99, "FF00FF00", DisplayName = "16 bpp: 0x07E0 green, third byte ignored")]
    [DataRow(16, 0x1F, 0x00, 0x00, "FF0000FF", DisplayName = "16 bpp: 0x001F blue")]
    [DataRow(16, 0x10, 0x84, 0x00, "FF848284", DisplayName = "16 bpp: 0x8410 expands like bitmap pixels")]
    [DataRow(15, 0x00, 0x7C, 0x00, "FFFF0000", DisplayName = "15 bpp: 0x7C00 red")]
    [DataRow(15, 0xE0, 0x03, 0x00, "FF00FF00", DisplayName = "15 bpp: 0x03E0 green")]
    [DataRow(15, 0x1F, 0x00, 0x00, "FF0000FF", DisplayName = "15 bpp: 0x001F blue")]
    [DataRow(8, 0x80, 0x00, 0x00, "FF808080", DisplayName = "8 bpp: palette index as grey")]
    public void OpaqueRectColourFollowsTheSessionDepth(int bpp, int b0, int b1, int b2, string expected)
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(bpp);
        byte[] stream =
        [
            0x09, 0x0A, 0x7F,       // TS_STANDARD | TS_TYPE_CHANGE, OPAQUERECT, fields 1-7
            0x00, 0x00, 0x00, 0x00, // 0,0
            0x01, 0x00, 0x01, 0x00, // 1x1
            (byte)b0, (byte)b1, (byte)b2,
        ];
        decoder.Process(stream, 1);
        AssertCalls(target, $"Fill 0,0 1x1 {expected}");
    }

    // ── Bitmap cache and MemBlt ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void MemBltDrawsAnUncompressed16BppCacheBitmapV2()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16);
        decoder.Process([.. CacheV2Uncompressed16, .. MemBltCache1Index3], 2);

        AssertCalls(target, "CachedBlit 2x2 0,0 -> 40,50 2x2 CC");
        Assert.AreEqual(TopDown2x2, Convert.ToHexString(target.Blits[0].Bgra));
    }

    [TestMethod]
    public void CacheBitmapV2TaggedSixteenBppIsRgb555InAFifteenBppSession()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(15);
        byte[] cache =
        [
            0x03,                   // TS_STANDARD | TS_SECONDARY
            0x04, 0x00,             // orderLength = 4 → 11 bytes follow
            0xA1, 0x00,             // cacheId 1 | bitsPerPixelId 4 (16 bpp: read as 15 in a 15 bpp session) | HEIGHT_SAME_AS_WIDTH
            0x04,                   // TS_CACHE_BITMAP_UNCOMPRESSED_REV2
            0x02,                   // width = height = 2
            0x08,                   // bitmapLength = 8
            0x03,                   // cacheIndex = 3
            0x00, 0x7C, 0xE0, 0x03, // bottom row: RGB555 red 0x7C00, green 0x03E0
            0x1F, 0x00, 0xFF, 0x7F, // top row: blue 0x001F, white 0x7FFF
        ];
        decoder.Process([.. cache, .. MemBltCache1Index3], 2);
        Assert.AreEqual(TopDown2x2, Convert.ToHexString(target.Blits[0].Bgra));
    }

    [TestMethod]
    public void MemBltToAMissingOrOutOfRangeSlotIsSkipped()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16);
        byte[] stream =
        [
            .. CacheV2Uncompressed16,   // fills cache 1 index 3 only
            0x09, 0x0D, 0xFF, 0x01,     // MEMBLT, fields 1-9
            0x01, 0x00,                 // cacheId = 1
            0x00, 0x00, 0x00, 0x00,     // 0,0
            0x02, 0x00, 0x02, 0x00,     // 2x2
            0xCC,                       // SRCCOPY
            0x00, 0x00, 0x00, 0x00,     // source 0,0
            0x02, 0x00,                 // cacheIndex = 2: never filled
            0x01, 0x00, 0x01,           // field 9
            0x09, 0x00,                 // cacheIndex = 9: past the 8-entry cell
            0x01, 0x00, 0x01,           // field 9
            0xFF, 0x7F,                 // cacheIndex = 0x7FFF: the (empty) waiting-list slot
            0x01, 0x01, 0x01,           // fields 1, 9
            0xFF, 0x00,                 // cacheId = 0xFF: offscreen cache (not negotiated)
            0x03, 0x00,                 // cacheIndex = 3
            0x41, 0x01,                 // TS_ZERO_FIELD_BYTE_BIT0, field 1
            0x05, 0x00,                 // cacheId = 5: no such cell
            0x41, 0x01,                 // field 1
            0x01, 0x00,                 // cacheId = 1 with cacheIndex 3 still in place: drawn
        ];
        decoder.Process(stream, 7);
        AssertCalls(target, "CachedBlit 2x2 0,0 -> 0,0 2x2 CC");
    }

    [TestMethod]
    public void CompressedCacheBitmapV2DecodesInterleavedRleWithAndWithoutHeader()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16);
        decoder.Process([.. CacheV2RleNoHeader, .. CacheV2RleWithHeader, .. MemBltCache0Index0, .. MemBltIndex1], 4);

        AssertCalls(target, "CachedBlit 4x2 0,0 -> 0,0 4x2 CC", "CachedBlit 4x2 0,0 -> 0,0 4x2 CC");
        Assert.AreEqual(string.Concat(Enumerable.Repeat("FF0000FF", 4)) + string.Concat(Enumerable.Repeat("0000FFFF", 4)),
            Convert.ToHexString(target.Blits[0].Bgra), "Top row blue, bottom row red.");
        Assert.AreEqual(string.Concat(Enumerable.Repeat("FFFFFFFF", 4)) + string.Concat(Enumerable.Repeat("00FF00FF", 4)),
            Convert.ToHexString(target.Blits[1].Bgra), "Top row white, bottom row green.");
    }

    [TestMethod]
    public void CacheBitmapV2VariableLengthFieldsAndTheWaitingList()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16, [8, 300, 8]);
        byte[] stream =
        [
            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x05, 0x00,                 // orderLength = 5 → 12 bytes follow
            0x21, 0x04,                 // cacheId 1 | 16 bpp | CBR2_NO_BITMAP_COMPRESSION_HDR
            0x05,                       // TS_CACHE_BITMAP_COMPRESSED_REV2
            0x80, 0x82,                 // bitmapWidth, 2-byte form: ((0x80 & 0x7F) << 8) | 0x82 = 130
            0x01,                       // bitmapHeight = 1
            0x40, 0x05,                 // bitmapLength, 4-byte unsigned 2-byte form (top bits 01): 5
            0x81, 0x01,                 // cacheIndex, 2-byte form: 0x0101 = 257
            0xF3, 0x82, 0x00, 0x1F, 0x00, // MEGA_MEGA_COLOR_RUN, run 130, blue

            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x09, 0x00,                 // orderLength = 9 → 16 bytes follow
            0xA2, 0x09,                 // cacheId 2 | 16 bpp | (DO_NOT_CACHE 0x10 | PERSISTENT_KEY 0x02 | HEIGHT_SAME 0x01) << 7
            0x04,                       // TS_CACHE_BITMAP_UNCOMPRESSED_REV2
            0x11, 0x22, 0x33, 0x44,     // key1
            0x55, 0x66, 0x77, 0x88,     // key2
            0x01,                       // bitmapWidth = bitmapHeight = 1
            0xC0, 0x00, 0x00, 0x02,     // bitmapLength, 4-byte form (top bits 11): 2
            0x05,                       // cacheIndex = 5, replaced by the waiting-list index (DO_NOT_CACHE)
            0x00, 0xF8,                 // red

            0x09, 0x0D, 0xFF, 0x01,     // MEMBLT, fields 1-9
            0x01, 0x00,                 // cacheId = 1
            0x0A, 0x00, 0x0B, 0x00,     // 10,11
            0x82, 0x00, 0x01, 0x00,     // 130x1
            0xCC,
            0x00, 0x00, 0x00, 0x00,     // source 0,0
            0x01, 0x01,                 // cacheIndex = 257
            0x01, 0x01, 0x01,           // fields 1, 9
            0x02, 0x00,                 // cacheId = 2
            0xFF, 0x7F,                 // cacheIndex = 0x7FFF (BITMAP_CACHE_WAITING_LIST_INDEX)
            0x01, 0x00, 0x01,           // field 9
            0x05, 0x00,                 // cacheIndex = 5: nothing was stored there
        ];
        decoder.Process(stream, 5);

        AssertCalls(target, "CachedBlit 130x1 0,0 -> 10,11 130x1 CC", "CachedBlit 1x1 0,0 -> 10,11 130x1 CC");
        Assert.AreEqual(string.Concat(Enumerable.Repeat("FF0000FF", 130)), Convert.ToHexString(target.Blits[0].Bgra));
        Assert.AreEqual("0000FFFF", Convert.ToHexString(target.Blits[1].Bgra));
    }

    [TestMethod]
    public void CacheBitmapRevisionOneUncompressedAndCompressed()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(24);
        byte[] stream =
        [
            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x0E, 0x00,                 // orderLength = 14 → 21 bytes follow
            0x00, 0x00,                 // extraFlags
            0x00,                       // TS_CACHE_BITMAP_UNCOMPRESSED (revision 1)
            0x02,                       // cacheId = 2
            0x00,                       // pad1Octet
            0x02, 0x02,                 // bitmapWidth = 2, bitmapHeight = 2
            0x18,                       // bitmapBpp = 24
            0x0C, 0x00,                 // bitmapLength = 12
            0x04, 0x00,                 // cacheIndex = 4
            0x10, 0x20, 0x30, 0x40, 0x50, 0x60, // bottom row: B,G,R per pixel
            0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0, // top row

            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x0E, 0x00,                 // orderLength = 14 → 21 bytes follow
            0x00, 0x00,                 // extraFlags: compression header present
            0x02,                       // TS_CACHE_BITMAP_COMPRESSED (revision 1)
            0x02, 0x00,                 // cacheId = 2, pad1Octet
            0x02, 0x02,                 // 2x2
            0x18,                       // 24 bpp
            0x0C, 0x00,                 // bitmapLength = 12, including the 8-byte TS_CD_HEADER
            0x05, 0x00,                 // cacheIndex = 5
            0x00, 0x00, 0x04, 0x00,     // TS_CD_HEADER: cbCompFirstRowSize 0, cbCompMainBodySize 4
            0x06, 0x00, 0x0C, 0x00,     //               cbScanWidth 6, cbUncompressedSize 12
            0x64, 0x11, 0x22, 0x33,     // REGULAR_COLOR_RUN run 4, pixel B 0x11 G 0x22 R 0x33

            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x06, 0x00,                 // orderLength = 6 → 13 bytes follow
            0x00, 0x04,                 // extraFlags: NO_BITMAP_COMPRESSION_HDR (0x0400)
            0x02,                       // TS_CACHE_BITMAP_COMPRESSED (revision 1)
            0x00, 0x00,                 // cacheId = 0, pad1Octet
            0x02, 0x02, 0x18,           // 2x2, 24 bpp
            0x04, 0x00,                 // bitmapLength = 4
            0x06, 0x00,                 // cacheIndex = 6
            0x64, 0x44, 0x55, 0x66,     // run 4, pixel B 0x44 G 0x55 R 0x66

            0x09, 0x0D, 0xFF, 0x01,     // MEMBLT, fields 1-9
            0x02, 0x00,                 // cacheId = 2
            0x00, 0x00, 0x00, 0x00,     // 0,0
            0x02, 0x00, 0x02, 0x00,     // 2x2
            0xCC,
            0x00, 0x00, 0x00, 0x00,     // source 0,0
            0x04, 0x00,                 // cacheIndex = 4
            0x01, 0x00, 0x01, 0x05, 0x00,       // field 9: cacheIndex = 5
            0x01, 0x01, 0x01, 0x00, 0x00, 0x06, 0x00, // fields 1, 9: cacheId = 0, cacheIndex = 6
        ];
        decoder.Process(stream, 6);

        Assert.HasCount(3, target.Blits);
        Assert.AreEqual("708090FF" + "A0B0C0FF" + "102030FF" + "405060FF", Convert.ToHexString(target.Blits[0].Bgra));
        Assert.AreEqual(string.Concat(Enumerable.Repeat("112233FF", 4)), Convert.ToHexString(target.Blits[1].Bgra));
        Assert.AreEqual(string.Concat(Enumerable.Repeat("445566FF", 4)), Convert.ToHexString(target.Blits[2].Bgra));
    }

    [TestMethod]
    public void Compressed32BppCacheBitmapIsPlanarAndForcedOpaque()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(32);
        byte[] stream =
        [
            0x03,                       // TS_STANDARD | TS_SECONDARY
            0x06, 0x00,                 // orderLength = 6 → 13 bytes follow
            0x30, 0x04,                 // cacheId 0 | bitsPerPixelId 6 = 32 bpp (0x30) | NO_BITMAP_COMPRESSION_HDR
            0x05,                       // TS_CACHE_BITMAP_COMPRESSED_REV2
            0x01, 0x02,                 // bitmapWidth = 1, bitmapHeight = 2
            0x09,                       // bitmapLength = 9
            0x00,                       // cacheIndex = 0
            0x00,                       // RDP6 planar format header: raw planes, alpha plane present, ARGB
            0x11, 0x22,                 // alpha plane (bottom row first): not kept, the cache is opaque
            0x10, 0x20,                 // red plane
            0x30, 0x40,                 // green plane
            0x50, 0x60,                 // blue plane
            0x09, 0x0D, 0xFF, 0x01,     // MEMBLT, fields 1-9
            0x00, 0x00,                 // cacheId = 0
            0x00, 0x00, 0x00, 0x00,     // 0,0
            0x01, 0x00, 0x02, 0x00,     // 1x2
            0xCC,
            0x00, 0x00, 0x00, 0x00,     // source 0,0
            0x00, 0x00,                 // cacheIndex = 0
        ];
        decoder.Process(stream, 2);

        AssertCalls(target, "CachedBlit 1x2 0,0 -> 0,0 1x2 CC");
        Assert.AreEqual("604020FF" + "503010FF", Convert.ToHexString(target.Blits[0].Bgra));
    }

    [TestMethod]
    public void ResetPrimaryStateRestoresDefaultsAndKeepsTheBitmapCache()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16);
        decoder.Process([.. CacheV2Uncompressed16, .. OpaqueRectWithBounds16], 2);
        AssertCalls(target, "Fill 10,10 20x20 FFFF0000 clip 0,0 100x50");

        decoder.ResetPrimaryState();
        target.Calls.Clear();
        byte[] stream =
        [
            0x41,                       // TS_STANDARD | TS_ZERO_FIELD_BYTE_BIT0: the last order type is PATBLT again
            0x0F,                       // fields 1-4
            0x01, 0x00, 0x02, 0x00,     // 1,2
            0x03, 0x00, 0x04, 0x00,     // 3x4 (rop, colours and brush are zero: BLACKNESS, solid black)
            0x3D,                       // TS_STANDARD | TS_BOUNDS | TS_TYPE_CHANGE | TS_DELTA_COORDINATES | TS_ZERO_BOUNDS_DELTAS
            0x0A,                       // OPAQUERECT
            0x0F,                       // fields 1-4
            0x00, 0x00, 0x02, 0x02,     // deltas from the zeroed state: 0,0 2x2; the bounds are 0,0..0,0
            .. MemBltCache1Index3,      // the cache survived
        ];
        decoder.Process(stream, 3);
        AssertCalls(target,
            "PatBlt 1,2 3x4 00 style 0 hatch 00 org 0,0 pattern 0000000000000000 fore FF000000 back FF000000",
            "Fill 0,0 2x2 FF000000 clip 0,0 1x1",
            "CachedBlit 2x2 0,0 -> 40,50 2x2 CC");
    }

    // ── Skipping and rejection ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void UndecodedSecondaryAndAlternateSecondaryOrdersAreSkippedByLength()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        byte[] stream =
        [
            .. CacheGlyphSkipped,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0,           // TS_CACHE_BITMAP_V3, 7 bytes
            0x03, 0x01, 0x00, 0x00, 0x00, 0x07, 1, 2, 3, 4, 5, 6, 7, 8,        // TS_CACHE_BRUSH, 8 bytes
            0x03, 0x00, 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0,           // TS_CACHE_COLOR_TABLE, 7 bytes
            0x03, 0x00, 0x00, 0x00, 0x00, 0x06, 9, 9, 9, 9, 9, 9, 9,           // undefined secondary type 6, 7 bytes
            .. FrameMarker,
            0x02, 0xFF, 0xFF,           // TS_ALTSEC_SWITCH_SURFACE (type 0): bitmapId 0xFFFF
            0x06,                       // TS_ALTSEC_CREATE_OFFSCREEN_BITMAP (type 1)
            0x05, 0x80,                 // flags: id 5 | deleteListPresent
            0x40, 0x00, 0x20, 0x00,     // cx 64, cy 32
            0x02, 0x00, 0x01, 0x00, 0x02, 0x00, // cIndices 2: 1, 2
            0x2E,                       // TS_ALTSEC_WINDOW (type 0x0B)
            0x0B, 0x00,                 // orderSize = 11, controlFlags included
            0x01, 0x00, 0x00, 0x01,     // fieldsPresentFlags
            0xAA, 0xBB, 0xCC, 0xDD,     // window order body
            0x0A,                       // TS_ALTSEC_STREAM_BITMAP_FIRST (type 2)
            0x00, 0x20,                 // bitmapFlags (2-byte bitmapSize), bitmapBpp 32
            0x00, 0x00, 0x02, 0x00, 0x02, 0x00, // bitmapType, width 2, height 2
            0x10, 0x00,                 // bitmapSize
            0x03, 0x00, 0x01, 0x02, 0x03, // bitmapBlockSize 3, block
            0x1A,                       // TS_ALTSEC_GDIP_NEXT (type 6)
            0x00, 0x02, 0x00, 0xAB, 0xCD, // pad1Octet, cbSize 2, emfRecords
            0x09, 0x0A, 0x7F,           // OPAQUERECT, fields 1-7
            0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, // 1,2 3x4
            0x11, 0x22, 0x33,
        ];
        decoder.Process(stream, 12);
        AssertCalls(target, "Fill 1,2 3x4 FF112233");
    }

    [TestMethod]
    [DataRow("09090000", DisplayName = "LINE_TO")]
    [DataRow("090E000000", DisplayName = "MEM3BLT")]
    [DataRow("091B000000", DisplayName = "GLYPH_INDEX")]
    [DataRow("0916", DisplayName = "POLYLINE")]
    [DataRow("0903", DisplayName = "undefined primary type 3")]
    [DataRow("091F", DisplayName = "primary type past ELLIPSE_CB")]
    [DataRow("32", DisplayName = "alternate secondary COMPDESK_FIRST")]
    [DataRow("3A", DisplayName = "alternate secondary type 0x0E")]
    [DataRow("FE", DisplayName = "alternate secondary type 0x3F")]
    [DataRow("03FEFF000003", DisplayName = "secondary order with a negative length")]
    [DataRow("031000000003FF", DisplayName = "secondary order longer than the data")]
    [DataRow("2E0300", DisplayName = "window order shorter than its header")]
    [DataRow("0601000000100000", DisplayName = "offscreen bitmap with zero width")]
    [DataRow("091280012E0000", DisplayName = "MultiOpaqueRect list of 46 rectangles")]
    public void MalformedOrUnsupportedOrdersAreRejected(string hex)
    {
        (OrderDecoder decoder, _) = Create();
        byte[] stream = Convert.FromHexString(hex);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(stream, 1));
    }

    [TestMethod]
    public void CacheBitmapFieldsAreParsedInsideTheOrderLength()
    {
        // bitmapLength 127 points past the 7-byte body even though the outer buffer has plenty left: the
        // orderLength bound fails the order.
        (OrderDecoder decoder, _) = Create(16);
        byte[] stream =
        [
            0x03, 0x00, 0x00,           // TS_STANDARD | TS_SECONDARY, orderLength 0 → 7 bytes follow
            0x20, 0x00, 0x04,           // cacheId 0 | 16 bpp, TS_CACHE_BITMAP_UNCOMPRESSED_REV2
            0x02, 0x02,                 // width 2, height 2
            0x7F,                       // bitmapLength = 127
            0x00,                       // cacheIndex 0
            0x00, 0x00, 0x00,           // the rest of the body
            .. new byte[200],
        ];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(stream, 1));
    }

    [TestMethod]
    [DataRow(0, 3, false, DisplayName = "index past the cell")]
    [DataRow(0, 2, true, DisplayName = "index equal to the cell size: the waiting-list slot")]
    [DataRow(2, 0, true, DisplayName = "cacheId one past the cells: tolerated extra cell, dropped")]
    [DataRow(2, 1, false, DisplayName = "cacheId one past the cells with a non-zero index")]
    [DataRow(3, 0, false, DisplayName = "cacheId beyond the cells")]
    public void CacheStoresAreConfinedToTheNegotiatedCells(int cacheId, int cacheIndex, bool accepted)
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16, [2, 2]);
        byte[] stream =
        [
            0x03, 0x04, 0x00,           // TS_STANDARD | TS_SECONDARY, orderLength 4 → 11 bytes follow
            (byte)(0xA0 | cacheId), 0x00, // cacheId | 16 bpp | HEIGHT_SAME_AS_WIDTH
            0x04,                       // TS_CACHE_BITMAP_UNCOMPRESSED_REV2
            0x02, 0x08,                 // 2x2, bitmapLength 8
            (byte)cacheIndex,           // cacheIndex
            0x00, 0xF8, 0xE0, 0x07, 0x1F, 0x00, 0xFF, 0xFF,
        ];
        if (!accepted)
        {
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(stream, 1));
            return;
        }

        decoder.Process(stream, 1);
        if (cacheId == 0)
        {
            byte[] waiting =
            [
                0x09, 0x0D, 0xFF, 0x01, // MEMBLT, fields 1-9
                0x00, 0x00,             // cacheId 0
                0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, // 0,0 2x2
                0xCC, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0x7F,             // BITMAP_CACHE_WAITING_LIST_INDEX reaches slot 2
            ];
            decoder.Process(waiting, 1);
            AssertCalls(target, "CachedBlit 2x2 0,0 -> 0,0 2x2 CC");
        }
    }

    [TestMethod]
    // Layouts: 03 | orderLength (2) | extraFlags (2) | orderType | body (orderLength + 7 bytes).
    // Rev2 bodies: width, height, bitmapLength, cacheIndex (short forms), data. Rev1 bodies: cacheId, pad, width,
    // height, bpp, bitmapLength (2), cacheIndex (2), [TS_CD_HEADER], data.
    [DataRow("03010020040590010103006400F8", DisplayName = "width 0x9001 (2-byte form) = 4097")]
    [DataRow("030000200405010003006400F8", DisplayName = "width 1, height 0")]
    [DataRow("030000000405020103006400F8", DisplayName = "bitsPerPixelId 0")]
    [DataRow("030000100405020103006400F8", DisplayName = "bitsPerPixelId 2")]
    [DataRow("030000380405020103006400F8", DisplayName = "bitsPerPixelId 7")]
    [DataRow("030000A00004020000000000", DisplayName = "rev2 bitmapLength 0")]
    [DataRow("030200A000040206000000F8E0071F00", DisplayName = "rev2 uncompressed data shorter than 2x2x2")]
    [DataRow("030200000000000001010002000000", DisplayName = "rev1 bitmapBpp 0")]
    [DataRow("030400000000000001010402000000AABB", DisplayName = "rev1 bitmapBpp 4 (no decoder)")]
    [DataRow("030A000000020000010118040000000000000000000000", DisplayName = "rev1 bitmapLength shorter than TS_CD_HEADER")]
    public void InvalidCachedBitmapsAreRejected(string hex)
    {
        (OrderDecoder decoder, _) = Create(16);
        byte[] stream = Convert.FromHexString(hex);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(stream, 1));
    }

    [TestMethod]
    public void TheLargestCachedBitmapSideIsAccepted()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create(16, [1]);
        byte[] stream =
        [
            0x03, 0x03, 0x00,           // TS_STANDARD | TS_SECONDARY, orderLength 3 → 10 bytes follow
            0x20, 0x04, 0x05,           // cacheId 0 | 16 bpp | NO_BITMAP_COMPRESSION_HDR, COMPRESSED_REV2
            0x90, 0x00,                 // bitmapWidth, 2-byte form: 0x1000 = 4096
            0x01,                       // bitmapHeight = 1
            0x05,                       // bitmapLength = 5
            0x00,                       // cacheIndex = 0
            0xF3, 0x00, 0x10, 0x1F, 0x00, // MEGA_MEGA_COLOR_RUN, run 4096, blue
            0x09, 0x0D, 0xFF, 0x01,     // MEMBLT, fields 1-9
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // cacheId 0, 0,0
            0x00, 0x10, 0x01, 0x00,     // 4096x1
            0xCC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        decoder.Process(stream, 2);
        Assert.AreEqual(4096, target.Blits[0].Width);
        Assert.HasCount(4096 * 4, target.Blits[0].Bgra);
    }

    [TestMethod]
    public void OrderCountsThatCannotFitAreRejected()
    {
        (OrderDecoder decoder, RecordingTarget target) = Create();
        byte[] one = [0x41];            // a single one-byte PATBLT
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(one, 2));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(one, -1));
        decoder.Process([], 0);
        Assert.IsEmpty(target.Calls);
    }

    [TestMethod]
    public void EveryTruncationOfAValidStreamIsRejected()
    {
        (byte[] corpus, int count) = Corpus();
        (OrderDecoder whole, _) = Create(16, [8, 300, 8]);
        whole.Process(corpus, count);

        for (int length = 0; length < corpus.Length; length++)
        {
            byte[] prefix = corpus[..length];
            (OrderDecoder decoder, _) = Create(16, [8, 300, 8]);
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(prefix, count), $"Prefix of {length} bytes.");
        }
    }

    [TestMethod]
    public void RepaintingBeyondTheWorkBudgetIsRejected()
    {
        // 1024×768: the budget is max(16 desktops, 2^24) = 2^24 pixels, i.e. 21 full-desktop fills per call.
        (OrderDecoder decoder, RecordingTarget target) = Create(32, width: 1024, height: 768);
        byte[] first =
        [
            0x09, 0x0A, 0x7F,           // OPAQUERECT, fields 1-7
            0x00, 0x00, 0x00, 0x00,     // 0,0
            0x00, 0x04, 0x00, 0x03,     // 1024x768
            0x01, 0x02, 0x03,
        ];
        decoder.Process([.. first, .. Enumerable.Repeat((byte)0x41, 20)], 21);
        Assert.HasCount(21, target.Calls);

        decoder.Process([.. Enumerable.Repeat((byte)0x41, 21)], 21);   // the budget is per call
        byte[] tooMany = [.. Enumerable.Repeat((byte)0x41, 22)];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Process(tooMany, 22));

        // Off-desktop operations cost nothing.
        byte[] offscreen =
        [
            0x09, 0x0A, 0x0F,           // OPAQUERECT, fields 1-4
            0x00, 0x08, 0x00, 0x00,     // 2048,0: right of the desktop
            0x00, 0x04, 0x00, 0x03,     // 1024x768
            .. Enumerable.Repeat((byte)0x41, 99),
        ];
        target.Calls.Clear();
        decoder.Process(offscreen, 100);
        Assert.IsEmpty(target.Calls);
    }

    [TestMethod]
    public void FuzzedStreamsOnlyEverThrowRdpProtocolException()
    {
        var random = new Random(0x4F524452);
        (byte[] corpus, int count) = Corpus();
        int[] depths = [15, 16, 24, 32];
        var persistent = new OrderDecoder(16, new RecordingTarget(320, 200), [8, 300, 8]);

        for (int iteration = 0; iteration < 2000; iteration++)
        {
            int length = random.Next(4) == 0 ? random.Next(corpus.Length + 32) : corpus.Length;
            byte[] stream = new byte[length];
            random.NextBytes(stream);
            corpus.AsSpan(0, Math.Min(length, corpus.Length)).CopyTo(stream);
            if (length > 0)
            {
                int mutations = random.Next(1, 9);
                for (int m = 0; m < mutations; m++)
                {
                    stream[random.Next(length)] = (byte)random.Next(256);
                }
            }

            int orders = random.Next(3) == 0 ? random.Next(count + 4) : count;
            OrderDecoder decoder = (iteration & 1) == 0
                ? persistent
                : new OrderDecoder(depths[random.Next(depths.Length)], new RecordingTarget(320, 200), [8, 300, 8]);
            Exception? unexpected = null;
            try { decoder.Process(stream, orders); }
            catch (RdpProtocolException) { }
            catch (Exception ex) { unexpected = ex; }
            Assert.IsNull(unexpected, $"Iteration {iteration}, {orders} orders over {Convert.ToHexString(stream)}: {unexpected}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A valid 16 bpp stream exercising every decoded order type, the skips and the bitmap cache.</summary>
    private static (byte[] Stream, int Count) Corpus()
    {
        byte[] stream =
        [
            .. CacheV2Uncompressed16, .. MemBltCache1Index3,                        // 2
            .. CacheV2RleNoHeader, .. CacheV2RleWithHeader, .. MemBltCache0Index0, .. MemBltIndex1, // 4
            .. OpaqueRectWithBounds16,                                              // 1
            .. MultiOpaqueRect,                                                     // 1
            .. PatBltSolidThenPattern,                                              // 2
            .. ScrBltFullThenDelta,                                                 // 2
            .. DstBltFull,                                                          // 1
            .. CacheGlyphSkipped, .. FrameMarker,                                   // 2
        ];
        return (stream, 15);
    }

    private static (OrderDecoder Decoder, RecordingTarget Target) Create(int bpp = 32, int[]? cells = null, int width = 1024, int height = 768)
    {
        var target = new RecordingTarget(width, height);
        return (new OrderDecoder(bpp, target, cells ?? [8, 8, 8]), target);
    }

    private static void AssertCalls(RecordingTarget target, params string[] expected) =>
        Assert.AreEqual(string.Join("\n", expected), string.Join("\n", target.Calls));

    private sealed class RecordingTarget(int width, int height) : IOrderTarget
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public List<string> Calls { get; } = [];

        public List<CachedBitmap> Blits { get; } = [];

        public List<OrderBrush> Brushes { get; } = [];

        public void FillRect(OrderRect rect, uint bgra, OrderRect? clip) =>
            Calls.Add($"Fill {Show(rect)} {bgra:X8}{ShowClip(clip)}");

        public void DstRop(OrderRect rect, byte rop3, OrderRect? clip) =>
            Calls.Add($"DstRop {Show(rect)} {rop3:X2}{ShowClip(clip)}");

        public void PatBlt(OrderRect rect, byte rop3, OrderBrush brush, OrderRect? clip)
        {
            Brushes.Add(brush);
            Calls.Add($"PatBlt {Show(rect)} {rop3:X2} style {brush.Style} hatch {brush.Hatch:X2} org {brush.OriginX},{brush.OriginY} " +
                $"pattern {Convert.ToHexString(brush.Pattern8x8)} fore {brush.ForeBgra:X8} back {brush.BackBgra:X8}{ShowClip(clip)}");
        }

        public void ScreenCopy(int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip) =>
            Calls.Add($"ScreenCopy {srcX},{srcY} -> {Show(dest)} {rop3:X2}{ShowClip(clip)}");

        public void CachedBlit(CachedBitmap bitmap, int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip)
        {
            Blits.Add(bitmap);
            Calls.Add($"CachedBlit {bitmap.Width}x{bitmap.Height} {srcX},{srcY} -> {Show(dest)} {rop3:X2}{ShowClip(clip)}");
        }

        private static string Show(OrderRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

        private static string ShowClip(OrderRect? clip) => clip is { } c ? $" clip {Show(c)}" : "";
    }
}
