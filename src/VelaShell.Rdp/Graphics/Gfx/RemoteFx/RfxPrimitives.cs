// Entropy decoding and wavelet reconstruction adapted from Microsoft's MIT-licensed
// WindowsProtocolTestSuites, commit 29ddb4238a5443b7499e82934c90cc898861c5e7:
// ProtoSDK/MS-RDPRFX/Codecs/RemoteFXCodec and MS-RDPEGFX/Codecs/RemoteFXProgressiveCodec.
// Sources: RLGRDecoder.cs, RemoteFXDecoder.cs, RfxProgressiveDecoder.cs, SRLDecoder.cs.
// Copyright (c) Microsoft. All rights reserved. See THIRD-PARTY-NOTICES.txt.
using System.Numerics;

namespace VelaShell.Rdp.Graphics;

internal static class RfxPrimitives
{
    internal static ReadOnlySpan<int> BandLengths(bool reduced) => reduced
        ? [1023, 1023, 961, 272, 272, 256, 72, 72, 64, 81]
        : [1024, 1024, 1024, 256, 256, 256, 64, 64, 64, 64];

    internal static int[] Quant(ref RfxReader reader, bool progressive, bool extra = false)
    {
        ReadOnlySpan<byte> packed = reader.Take(5);
        ReadOnlySpan<int> order = progressive ? [7, 8, 9, 4, 5, 6, 1, 2, 3, 0] : [8, 7, 9, 5, 4, 6, 2, 1, 3, 0];
        int[] result = new int[10];
        for (int i = 0; i < 10; i++)
        {
            int nibble = order[i];
            int value = (packed[nibble / 2] >> ((nibble & 1) * 4)) & 15;
            RfxGuard.Require(extra ? value <= 8 : progressive || value >= 6, "Invalid RemoteFX quantization factor.");
            result[i] = value;
        }
        return result;
    }

    internal static int[] Rlgr(ReadOnlySpan<byte> data, bool rlgr3)
    {
        var bits = new RfxBits(data);
        int[] output = new int[4096];
        int position = 0, kp = 8, krp = 8;
        while (position < output.Length)
        {
            int k = kp >> 3;
            if (k != 0)
            {
                while (bits.Read(1) == 0)
                {
                    position = Math.Min(output.Length, position + (1 << k));
                    kp = Math.Min(80, kp + 4);
                    k = kp >> 3;
                    if (position == output.Length)
                    {
                        return output;
                    }
                }
                int run = bits.Read(k);
                position = Math.Min(output.Length, position + run);
                if (position == output.Length)
                {
                    return output;
                }

                int sign = bits.Read(1);
                int magnitude = Rice(ref bits, ref krp) + 1;
                RfxGuard.Require(magnitude <= (sign == 0 ? 32767 : 32768), "RemoteFX coefficient overflow.");
                output[position++] = sign == 0 ? magnitude : -magnitude;
                kp = Math.Max(0, kp - 6);
            }
            else
            {
                int value = Rice(ref bits, ref krp);
                if (!rlgr3)
                {
                    output[position++] = Signed(value);
                    kp = Math.Clamp(kp + (value == 0 ? 3 : -3), 0, 80);
                }
                else
                {
                    int first = bits.Read(value == 0 ? 0 : BitOperations.Log2((uint)value) + 1);
                    RfxGuard.Require(first <= value, "Invalid RemoteFX RLGR3 pair.");
                    int second = value - first;
                    output[position++] = Signed(first);
                    if (position < output.Length)
                    {
                        output[position++] = Signed(second);
                    }

                    if (first != 0 && second != 0)
                    {
                        kp = Math.Max(0, kp - 6);
                    }
                    else if (first == 0 && second == 0)
                    {
                        kp = Math.Min(80, kp + 6);
                    }
                }
            }
        }
        return output;
    }

    private static int Signed(int value)
    {
        RfxGuard.Require(value <= 65535, "RemoteFX coefficient overflow.");
        return (value & 1) == 0 ? value >> 1 : -((value + 1) >> 1);
    }

    private static int Rice(ref RfxBits bits, ref int krp)
    {
        int kr = krp >> 3, quotient = 0;
        while (bits.Read(1) != 0)
        {
            quotient++;
            RfxGuard.Require(quotient <= (131070 >> kr), "Invalid RemoteFX Golomb code.");
        }
        int result = (quotient << kr) | bits.Read(kr);
        if (quotient == 0)
        {
            krp = Math.Max(0, krp - 2);
        }
        else if (quotient > 1)
        {
            krp = Math.Min(80, krp + quotient);
        }

        return result;
    }

    internal static void Differential(int[] component, bool reduced)
    {
        int start = reduced ? 4015 : 4032;
        for (int i = start + 1; i < component.Length; i++)
        {
            component[i] = unchecked((short)(component[i] + component[i - 1]));
        }
    }

    internal static int Scale(int value, int bitPosition)
    {
        // Preserve five fractional bits until the final YCbCr conversion.
        int shift = bitPosition - 1;
        RfxGuard.Require(shift is >= -1 and <= 22, "Invalid RemoteFX coefficient bit position.");
        long result = shift < 0 ? value >> 1 : (long)value << shift;
        RfxGuard.Require(result is >= short.MinValue and <= short.MaxValue, "RemoteFX dequantized coefficient overflow.");
        return (int)result;
    }

    internal static void Dequantize(int[] component, int[] quant, bool reduced)
    {
        ReadOnlySpan<int> lengths = BandLengths(reduced);
        int offset = 0;
        for (int band = 0; band < 10; band++)
        {
            for (int i = 0; i < lengths[band]; i++, offset++)
            {
                component[offset] = Scale(component[offset], quant[band]);
            }
        }
    }

    internal static byte[] Pixels(int[][] components, bool reduced)
    {
        int[] y = InverseDwt(components[0], reduced);
        int[] cb = InverseDwt(components[1], reduced);
        int[] cr = InverseDwt(components[2], reduced);
        byte[] result = new byte[64 * 64 * 4];
        for (int i = 0; i < 4096; i++)
        {
            long luma = ((long)y[i] + 4096) << 16;
            result[i * 4] = Color((luma + 115992L * cb[i]) >> 21);
            result[i * 4 + 1] = Color((luma - 22527L * cb[i] - 46819L * cr[i]) >> 21);
            result[i * 4 + 2] = Color((luma + 91916L * cr[i]) >> 21);
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    private static byte Color(long value) => (byte)Math.Clamp(value, 0, 255);

    private static int[] InverseDwt(int[] component, bool reduced)
    {
        int[] matrix = new int[4096];
        int offset = 0;
        for (int level = 1; level <= 3; level++)
        {
            int low = (64 >> level) + (reduced ? 1 : 0);
            int high = (64 >> level) - (reduced && level == 1 ? 1 : 0);
            PutBand(component, matrix, ref offset, low, 0, high, low);
            PutBand(component, matrix, ref offset, 0, low, low, high);
            PutBand(component, matrix, ref offset, low, low, high, high);
        }
        int ll = reduced ? 9 : 8;
        PutBand(component, matrix, ref offset, 0, 0, ll, ll);
        Span<int> input = stackalloc int[65];
        Span<int> output = stackalloc int[65];
        for (int level = 3; level >= 1; level--)
        {
            int length = (64 >> (level - 1)) + (reduced && level != 1 ? 1 : 0);
            for (int row = 0; row < length; row++)
            {
                matrix.AsSpan(row * 64, length).CopyTo(input);
                InverseLine(input, output, length, reduced);
                output[..length].CopyTo(matrix.AsSpan(row * 64));
            }
            for (int col = 0; col < length; col++)
            {
                for (int row = 0; row < length; row++)
                {
                    input[row] = matrix[row * 64 + col];
                }

                InverseLine(input, output, length, reduced);
                for (int row = 0; row < length; row++)
                {
                    matrix[row * 64 + col] = output[row];
                }
            }
        }
        return matrix;
    }

    private static void PutBand(int[] source, int[] target, ref int offset, int x, int y, int width, int height)
    {
        for (int row = y; row < y + height; row++)
        {
            source.AsSpan(offset, width).CopyTo(target.AsSpan(row * 64 + x, width));
            offset += width;
        }
    }

    private static void InverseLine(Span<int> input, Span<int> output, int length, bool reduced)
    {
        int low = length / 2 + (reduced ? 1 : 0);
        int high = length - low;
        int expanded = length;
        if (reduced && length == 64)
        {
            input[64] = 0;
            high++;
            expanded++;
        }
        for (int i = 0; i < low; i++)
        {
            int left = input[low + Math.Max(0, i - 1)];
            int right = input[low + Math.Min(i, high - 1)];
            output[2 * i] = input[i] - ((left + right + 1) >> 1);
        }
        for (int i = 0; i < high; i++)
        {
            int next = Math.Min(2 * i + 2, expanded - (reduced ? 1 : 2));
            output[2 * i + 1] = 2 * input[low + i] + ((output[2 * i] + output[next]) >> 1);
        }
    }

    internal static void Copy(byte[] pixels, int tileX, int tileY, RfxRect rect, Span<byte> destination, int stride)
    {
        int x = tileX * 64, y = tileY * 64;
        int left = Math.Max(x, rect.X), top = Math.Max(y, rect.Y);
        int right = Math.Min(x + 64, rect.X + rect.Width), bottom = Math.Min(y + 64, rect.Y + rect.Height);
        if (left >= right || top >= bottom)
        {
            return;
        }

        for (int row = top; row < bottom; row++)
        {
            pixels.AsSpan(((row - y) * 64 + left - x) * 4, (right - left) * 4)
                .CopyTo(destination.Slice(row * stride + left * 4, (right - left) * 4));
        }
    }
}

internal ref struct RfxSrl(ReadOnlySpan<byte> data)
{
    private RfxBits _bits = new(data);
    private int _kp = 8;
    private int _zeroes;
    private bool _unary;

    internal int Read(int bitCount)
    {
        if (_zeroes > 0) { _zeroes--; return 0; }
        if (!_unary)
        {
            int k = _kp >> 3;
            if (_bits.Read(1) == 0)
            {
                _zeroes = (1 << k) - 1;
                _kp = Math.Min(80, _kp + 4);
                return 0;
            }
            _zeroes = _bits.Read(k);
            _unary = true;
            if (_zeroes > 0) { _zeroes--; return 0; }
        }
        int sign = _bits.Read(1), magnitude = 1, max = (1 << bitCount) - 1;
        while (magnitude < max && _bits.Read(1) == 0)
        {
            magnitude++;
        }

        _kp = Math.Max(0, _kp - 6);
        _unary = false;
        return sign == 0 ? magnitude : -magnitude;
    }

    internal readonly void Finish() => _bits.Finish(allowExtraByte: true);
}
