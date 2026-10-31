using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Plugin.Rdp;

internal sealed record RdpClipboardFile(string Name, bool IsDirectory, long Length);
internal sealed record RdpClipboardImage(int Width, int Height, byte[] Bgra);

internal static class RdpClipboardCodec
{
    internal const int MaxPayload = 16 * 1024 * 1024;
    internal const int MaxFiles = 4096;
    internal const int DescriptorSize = 592;
    internal const uint UnicodeText = 13;
    internal const uint Dib = 8;
    internal const uint DibV5 = 17;
    internal const uint Png = 0xC001;
    internal const uint FileGroup = 0xC002;
    internal const uint FileContents = 0xC003;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    internal static byte[] EncodeText(string text)
    {
        text = text.ReplaceLineEndings("\r\n");
        if (text.Contains('\0') || StrictUnicode.GetByteCount(text) > MaxPayload - 2)
        {
            throw new InvalidDataException("Clipboard text is invalid or too large.");
        }
        return StrictUnicode.GetBytes(text + "\0");
    }

    internal static string DecodeText(byte[] data)
    {
        if (data.Length > MaxPayload || data.Length % 2 != 0)
        {
            throw new InvalidDataException("Invalid Unicode clipboard payload.");
        }
        string text = StrictUnicode.GetString(data);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    internal static byte[] EncodeFiles(IReadOnlyList<RdpClipboardFile> files)
    {
        if (files.Count > MaxFiles)
        {
            throw new InvalidDataException("Too many clipboard files.");
        }
        byte[] data = new byte[checked(4 + files.Count * DescriptorSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            RdpClipboardFile file = files[i];
            ValidateFileName(file.Name);
            Span<byte> entry = data.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, file.IsDirectory ? 4u : 0x44u);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], file.IsDirectory ? 0x10u : 0x80u);
            if (file.Length < 0)
            {
                throw new InvalidDataException("Invalid clipboard file size.");
            }
            BinaryPrimitives.WriteUInt32LittleEndian(entry[64..], (uint)((ulong)file.Length >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], (uint)file.Length);
            _ = StrictUnicode.GetBytes(file.Name, entry[72..]);
        }
        return data;
    }

    internal static IReadOnlyList<RdpClipboardFile> DecodeFiles(byte[] data)
    {
        if (data.Length is < 4 or > MaxPayload)
        {
            throw new InvalidDataException("Invalid file descriptor payload.");
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > MaxFiles || data.Length != 4L + count * DescriptorSize)
        {
            throw new InvalidDataException("Invalid file descriptor count.");
        }
        List<RdpClipboardFile> files = [];
        HashSet<string> names = [with(StringComparer.OrdinalIgnoreCase)];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if ((flags & 4) == 0)
            {
                throw new InvalidDataException("Clipboard file attributes are missing.");
            }
            string name = StrictUnicode.GetString(entry[72..]);
            int terminator = name.IndexOf('\0');
            if (terminator < 0)
            {
                throw new InvalidDataException("Unterminated clipboard file name.");
            }
            name = name[..terminator];
            ValidateFileName(name);
            if (!names.Add(name))
            {
                throw new InvalidDataException("Duplicate clipboard file name.");
            }
            bool directory = (BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]) & 0x10) != 0;
            ulong length = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry[64..]) << 32)
                | BinaryPrimitives.ReadUInt32LittleEndian(entry[68..]);
            if (!directory && ((flags & 0x40) == 0 || length > long.MaxValue))
            {
                throw new InvalidDataException("Clipboard file size is missing or invalid.");
            }
            files.Add(new(name, directory, directory ? 0 : (long)length));
        }
        foreach (RdpClipboardFile file in files)
        {
            string[] components = file.Name.Split('\\');
            for (int i = 1; i < components.Length; i++)
            {
                string parent = string.Join('\\', components.Take(i));
                if (files.Any(f => !f.IsDirectory && string.Equals(f.Name, parent, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Clipboard file and directory names conflict.");
                }
            }
        }
        return files;
    }

    internal static void ValidateFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length >= 260 || name.Contains('/'))
        {
            throw new InvalidDataException("Invalid clipboard file name.");
        }
        foreach (string part in name.Split('\\'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(c => char.IsControl(c) || "<>:\"/|?*".Contains(c))
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
            {
                throw new InvalidDataException("Unsafe clipboard file name.");
            }
        }
    }

    internal static byte[] EncodeDib(RdpClipboardImage image)
    {
        ValidateImageSize(image.Width, image.Height);
        int stride = checked((image.Width * 3 + 3) & ~3);
        byte[] data = new byte[checked(40 + stride * image.Height)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)(stride * image.Height));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image.Bgra.AsSpan((y * image.Width + x) * 4, 3)
                    .CopyTo(data.AsSpan(40 + (image.Height - 1 - y) * stride + x * 3));
            }
        }
        return data;
    }

    internal static RdpClipboardImage DecodeDib(byte[] data)
    {
        if (data.Length is < 40 or > MaxPayload)
        {
            throw new InvalidDataException("Invalid clipboard DIB.");
        }
        uint header = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        if (signedHeight == int.MinValue)
        {
            throw new InvalidDataException("Invalid image height.");
        }
        int height = Math.Abs(signedHeight);
        ValidateImageSize(width, height);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (header is not (40 or 108 or 124) || header > data.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) != 1
            || bits is not (24 or 32) || compression != 0 && !(compression == 3 && bits == 32))
        {
            throw new InvalidDataException("Unsupported clipboard image encoding.");
        }
        int start = (int)header;
        bool alpha = false;
        if (compression == 3)
        {
            if (data.Length < 52 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40)) != 0x00FF0000
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44)) != 0x0000FF00
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48)) != 0x000000FF)
            {
                throw new InvalidDataException("Unsupported clipboard image masks.");
            }
            start = header == 40 ? 52 : (int)header;
            alpha = header >= 108 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52)) == 0xFF000000;
        }
        uint colors = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        if (colors > 256)
        {
            throw new InvalidDataException("Invalid clipboard color table.");
        }
        start = checked(start + (int)colors * 4);
        int stride = checked((width * bits + 31) / 32 * 4);
        if (start + (long)stride * height > data.Length)
        {
            throw new InvalidDataException("Truncated clipboard image.");
        }
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sourceY = signedHeight < 0 ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int target = (y * width + x) * 4;
                data.AsSpan(start + sourceY * stride + x * (bits / 8), 3).CopyTo(pixels.AsSpan(target));
                pixels[target + 3] = alpha ? data[start + sourceY * stride + x * 4 + 3] : (byte)255;
            }
        }
        return new(width, height, pixels);
    }

    internal static void ValidatePng(byte[] data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || data.Length > MaxPayload || !data.AsSpan(0, 8).SequenceEqual(signature)
            || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("Invalid clipboard PNG.");
        }
        ValidateImageSize(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)),
            BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
    }

    internal static void ValidateImageSize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height * 4 > MaxPayload - 124)
        {
            throw new InvalidDataException("Clipboard image is too large.");
        }
    }
}
