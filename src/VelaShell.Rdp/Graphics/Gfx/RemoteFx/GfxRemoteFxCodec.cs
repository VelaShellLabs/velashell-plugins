namespace VelaShell.Rdp.Graphics.Gfx.RemoteFx;

/// <summary>MS-RDPRFX bitmap streams carried by RDPGFX CAVIDEO (codec 3).</summary>
internal sealed class GfxRemoteFxCodec
{
    private bool _hasContext;
    private bool _rlgr3;

    internal void Reset()
    {
        _hasContext = false;
        _rlgr3 = false;
    }

    internal void Decode(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        RfxGuard.Destination(width, height, destination, stride);
        RfxGuard.Require(!source.IsEmpty, "Empty RemoteFX stream.");
        var reader = new RfxReader(source);
        bool hasContext = _hasContext, rlgr3 = _rlgr3, inFrame = false;
        int regionsRemaining = 0, tileSetsRemaining = 0;
        RfxRect[] rectangles = [];
        var decoded = new List<(int X, int Y, byte[] Pixels, RfxRect[] Rectangles)>();
        while (reader.Remaining > 0)
        {
            RfxReader block = reader.Block(out ushort type);
            if (type is >= 0xCCC3 and <= 0xCCC7)
            {
                RfxGuard.Require(block.Byte() == 1, "Invalid RemoteFX codec identifier.");
                int channel = block.Byte();
                RfxGuard.Require(channel == 0 || (type == 0xCCC3 && channel == 255), "Invalid RemoteFX channel identifier.");
            }
            switch (type)
            {
                case 0xCCC0:
                    RfxGuard.Require(!inFrame && block.UInt32() == 0xCACCACCA && block.UInt16() == 0x0100, "Invalid RemoteFX synchronization block.");
                    break;
                case 0xCCC1:
                    RfxGuard.Require(!inFrame && block.Byte() == 1 && block.Byte() == 1 && block.UInt16() == 0x0100, "Invalid RemoteFX codec version.");
                    break;
                case 0xCCC2:
                    RfxGuard.Require(!inFrame && block.Byte() == 1 && block.Byte() == 0, "Invalid RemoteFX channels block.");
                    int channelWidth = block.UInt16(), channelHeight = block.UInt16();
                    RfxGuard.Require(channelWidth > 0 && channelHeight > 0, "Invalid RemoteFX channel dimensions.");
                    break;
                case 0xCCC3:
                    RfxGuard.Require(!inFrame && block.Byte() == 0 && block.UInt16() == 64, "Invalid RemoteFX context.");
                    int properties = block.UInt16();
                    int entropy = (properties >> 9) & 15;
                    RfxGuard.Require(((properties >> 3) & 3) == 1 && ((properties >> 5) & 15) == 1 &&
                        entropy is 1 or 4 && ((properties >> 13) & 3) == 1, "Unsupported RemoteFX transform properties.");
                    rlgr3 = entropy == 4;
                    hasContext = true;
                    break;
                case 0xCCC4:
                    RfxGuard.Require(hasContext && !inFrame, "RemoteFX frame has no context or is nested.");
                    _ = block.UInt32();
                    regionsRemaining = block.UInt16();
                    tileSetsRemaining = 0;
                    inFrame = true;
                    break;
                case 0xCCC5:
                    RfxGuard.Require(inFrame && regionsRemaining == 0 && tileSetsRemaining == 0, "Incomplete RemoteFX frame.");
                    inFrame = false;
                    break;
                case 0xCCC6:
                    RfxGuard.Require(inFrame && regionsRemaining > 0 && tileSetsRemaining == 0, "Unexpected RemoteFX region.");
                    _ = block.Byte();
                    int rectangleCount = block.UInt16();
                    RfxGuard.Require(rectangleCount <= block.Remaining / 8, "Truncated RemoteFX rectangles.");
                    rectangles = rectangleCount == 0 ? [new RfxRect(0, 0, width, height)] : new RfxRect[rectangleCount];
                    for (int i = 0; i < rectangleCount; i++)
                    {
                        rectangles[i] = RfxRect.Read(ref block, width, height);
                    }

                    RfxGuard.Require(block.UInt16() == 0xCAC1, "Invalid RemoteFX region type.");
                    tileSetsRemaining = block.UInt16();
                    RfxGuard.Require(tileSetsRemaining == 1, "Invalid RemoteFX tile set count.");
                    regionsRemaining--;
                    break;
                case 0xCCC7:
                    RfxGuard.Require(inFrame && tileSetsRemaining > 0, "Unexpected RemoteFX tile set.");
                    DecodeTiles(ref block, width, height, rlgr3, rectangles, decoded);
                    tileSetsRemaining--;
                    break;
                default:
                    throw new RdpProtocolException("Unknown RemoteFX block type.");
            }
            block.End();
        }
        RfxGuard.Require(!inFrame, "Truncated RemoteFX frame.");
        foreach ((int X, int Y, byte[] Pixels, RfxRect[] Rectangles) in decoded)
        {
            foreach (RfxRect rectangle in Rectangles)
            {
                RfxPrimitives.Copy(Pixels, X, Y, rectangle, destination, stride);
            }
        }

        _hasContext = hasContext;
        _rlgr3 = rlgr3;
    }

    private static void DecodeTiles(ref RfxReader block, int width, int height, bool rlgr3, RfxRect[] rectangles,
        List<(int X, int Y, byte[] Pixels, RfxRect[] Rectangles)> decoded)
    {
#pragma warning disable IDE0078 // 使用模式匹配(可能会更改代码含义)
        RfxGuard.Require(block.UInt16() == 0xCAC2 && block.UInt16() == 0, "Invalid RemoteFX tile set header.");
#pragma warning restore IDE0078 // 使用模式匹配(可能会更改代码含义)
        int properties = block.UInt16();
        int entropy = (properties >> 10) & 15;
        RfxGuard.Require((properties & 1) == 1 && ((properties >> 4) & 3) == 1 && ((properties >> 6) & 15) == 1 &&
            entropy == (rlgr3 ? 4 : 1) && ((properties >> 14) & 3) == 1, "Inconsistent RemoteFX tile set properties.");
        int quantCount = block.Byte();
        RfxGuard.Require(quantCount > 0 && block.Byte() == 64, "Invalid RemoteFX tile set quantization or tile size.");
        int tileCount = block.UInt16();
        int dataSize = block.Length32();
        int[][] quants = new int[quantCount][];
        for (int i = 0; i < quantCount; i++)
        {
            quants[i] = RfxPrimitives.Quant(ref block, progressive: false);
        }

        var tiles = new RfxReader(block.Take(dataSize));
        RfxGuard.Require(tileCount <= tiles.Remaining / 19, "Truncated RemoteFX tile set.");
        RfxGuard.Require((long)decoded.Count + tileCount <= 8192, "RemoteFX decoded tile memory limit exceeded.");
        for (int i = 0; i < tileCount; i++)
        {
            RfxReader tile = tiles.Block(out ushort type);
            RfxGuard.Require(type == 0xCAC3, "Invalid RemoteFX tile type.");
            int qy = tile.Byte(), qcb = tile.Byte(), qcr = tile.Byte();
            RfxGuard.Require(qy < quantCount && qcb < quantCount && qcr < quantCount, "Invalid RemoteFX quantization index.");
            int x = tile.UInt16(), y = tile.UInt16();
            RfxGuard.Tile(x, y, width, height);
            int yLength = tile.UInt16(), cbLength = tile.UInt16(), crLength = tile.UInt16();
            int[][] components = [
                RfxPrimitives.Rlgr(tile.Take(yLength), rlgr3),
                RfxPrimitives.Rlgr(tile.Take(cbLength), rlgr3),
                RfxPrimitives.Rlgr(tile.Take(crLength), rlgr3)];
            tile.End();
            int[] indices = [qy, qcb, qcr];
            for (int c = 0; c < 3; c++)
            {
                RfxPrimitives.Differential(components[c], reduced: false);
                RfxPrimitives.Dequantize(components[c], quants[indices[c]], reduced: false);
            }
            decoded.Add((x, y, RfxPrimitives.Pixels(components, reduced: false), rectangles));
        }
        tiles.End();
    }
}
