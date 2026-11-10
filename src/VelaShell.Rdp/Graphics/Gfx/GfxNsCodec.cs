namespace VelaShell.Rdp.Graphics.Gfx;

// MS-RDPNSC plane layout and RLE, following FreeRDP nsc.c (Apache-2.0).
internal static class GfxNsCodec
{
    internal static void Decode(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        var reader = new GfxClearReader(source);
        int[] lengths = [reader.Length(), reader.Length(), reader.Length(), reader.Length()];
        int loss = reader.Byte(), subsampling = reader.Byte();
        _ = reader.Take(2);
        if (loss is < 1 or > 7 || subsampling > 1)
        {
            throw new RdpProtocolException("Invalid NSCodec color format.");
        }

        int roundedWidth = (width + 7) & ~7;
        int yStride = subsampling != 0 ? roundedWidth : width;
        int cStride = subsampling != 0 ? roundedWidth / 2 : width;
        int chromaHeight = subsampling != 0 ? (height + 1) / 2 : height;
        int[] sizes = [checked(yStride * height), checked(cStride * chromaHeight), checked(cStride * chromaHeight), checked(width * height)];
        byte[][] planes = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            planes[i] = new byte[sizes[i]];
            ReadOnlySpan<byte> encoded = reader.Take(lengths[i]);
            if (encoded.IsEmpty)
            {
                planes[i].AsSpan().Fill(255);
            }
            else if (encoded.Length < sizes[i])
            {
                DecodeRle(encoded, planes[i]);
            }
            else if (encoded.Length == sizes[i])
            {
                encoded.CopyTo(planes[i]);
            }
            else
            {
                throw new RdpProtocolException("NSCodec plane exceeds its uncompressed size.");
            }
        }
        if (!reader.Empty)
        {
            throw new RdpProtocolException("Trailing NSCodec data.");
        }

        int shift = loss - 1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int c = subsampling != 0 ? (y / 2) * cStride + x / 2 : y * cStride + x;
                int luma = planes[0][y * yStride + x];
                int co = unchecked((sbyte)(planes[1][c] << shift));
                int cg = unchecked((sbyte)(planes[2][c] << shift));
                int index = y * stride + x * 4;
                destination[index] = (byte)Math.Clamp(luma - co - cg, 0, 255);
                destination[index + 1] = (byte)Math.Clamp(luma + cg, 0, 255);
                destination[index + 2] = (byte)Math.Clamp(luma + co - cg, 0, 255);
                destination[index + 3] = planes[3][y * width + x];
            }
        }
    }

    private static void DecodeRle(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int input = 0, output = 0;
        while (destination.Length - output > 4)
        {
            if (input >= source.Length)
            {
                throw new RdpProtocolException("Truncated NSCodec plane.");
            }

            byte value = source[input++];
            int run = 1;
            if (destination.Length - output != 5)
            {
                if (input >= source.Length)
                {
                    throw new RdpProtocolException("Truncated NSCodec run.");
                }

                if (source[input] == value)
                {
                    input++;
                    var reader = new GfxClearReader(source[input..]);
                    int factor = reader.Byte();
                    input++;
                    if (factor == 255)
                    {
                        run = reader.Length();
                        input += 4;
                    }
                    else
                    {
                        run = factor + 2;
                    }
                }
            }
            if (run == 0 || run > destination.Length - output - 4)
            {
                throw new RdpProtocolException("NSCodec run exceeds its plane.");
            }

            destination.Slice(output, run).Fill(value);
            output += run;
        }
        if (destination.Length - output != 4 || source.Length - input != 4)
        {
            throw new RdpProtocolException("Invalid NSCodec plane tail.");
        }

        source[input..].CopyTo(destination[output..]);
    }
}
