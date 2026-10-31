using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Graphics;

/// <summary>Parses TS_UPDATE_BITMAP (MS-RDPBCGR 2.2.9.1.1.3.1.2) and paints it into the framebuffer.</summary>
internal static class BitmapUpdate
{
    private const ushort BitmapCompression = 0x0001;
    private const ushort NoCompressionHeader = 0x0400;

    /// <summary>Reads the bitmap update body (the reader is positioned just after the 2-byte updateType). Returns the rect count.</summary>
    internal static int Apply(ref ByteReader r, Framebuffer framebuffer)
    {
        int rectangles = r.U16LE();
        for (int i = 0; i < rectangles; i++)
        {
            int left = r.U16LE();
            int top = r.U16LE();
            int right = r.U16LE();
            int bottom = r.U16LE();
            int width = r.U16LE();
            int height = r.U16LE();
            int bpp = r.U16LE();
            ushort flags = r.U16LE();
            int bitmapLength = r.U16LE();
            _ = right; _ = bottom;

            bool compressed = (flags & BitmapCompression) != 0;
            ReadOnlySpan<byte> data;
            if (compressed && (flags & NoCompressionHeader) == 0)
            {
                // TS_CD_HEADER: cbCompFirstRowSize(2)=0, cbCompMainBodySize(2), cbScanWidth(2), cbUncompressedSize(2)
                r.U16LE();
                int mainBody = r.U16LE();
                r.U16LE();
                r.U16LE();
                data = r.Take(mainBody);
            }
            else
            {
                data = r.Take(bitmapLength);
            }
            framebuffer.ApplyRect(left, top, width, height, bpp, compressed, data);
        }
        return rectangles;
    }
}
