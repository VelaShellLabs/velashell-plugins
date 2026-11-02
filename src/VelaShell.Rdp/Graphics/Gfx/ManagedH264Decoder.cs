using System.Buffers.Binary;
using ManagedCodecs;
using ManagedCodecs.H264;

namespace VelaShell.Rdp.Graphics;

internal sealed class ManagedH264Decoder(int width, int height, Action<long>? reserve = null)
{
    private H264Decoder? _decoder;
    private byte[]? _sps;
    private byte[]? _pps;
    private long _timestamp;

    internal VideoFrame Decode(ReadOnlySpan<byte> source)
    {
        try { return DecodeCore(source); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException or OverflowException)
        {
            throw new RdpProtocolException("Invalid or unsupported GFX AVC bitstream.", error);
        }
    }

    private VideoFrame DecodeCore(ReadOnlySpan<byte> source)
    {
        var units = SplitAnnexB(source);
        if (units.Count == 0) throw new RdpProtocolException("Empty AVC bitstream.");
        int length = 0;
        foreach (var unit in units)
        {
            int type = unit[0] & 31;
            if ((unit[0] & 0x80) != 0 || type == 0)
                throw new RdpProtocolException("Invalid AVC NAL header.");
            if (type == 7)
            {
                long budget = ValidateSps(unit, width, height);
                reserve?.Invoke(budget);
                _sps = unit;
            }
            else if (type == 8)
            {
                if (unit.Length > ushort.MaxValue) throw new RdpProtocolException("AVC PPS is too large.");
                _pps = unit;
            }
            length = checked(length + 4 + unit.Length);
        }
        if (_decoder is null)
        {
            if (_sps is null || _pps is null) throw new RdpProtocolException("AVC stream has no initial SPS/PPS.");
            byte[] config = new byte[11 + _sps.Length + _pps.Length];
            config[0] = 1;
            _sps.AsSpan(1, 3).CopyTo(config.AsSpan(1));
            config[4] = 0xFF;
            config[5] = 0xE1;
            BinaryPrimitives.WriteUInt16BigEndian(config.AsSpan(6), (ushort)_sps.Length);
            _sps.CopyTo(config, 8);
            int offset = 8 + _sps.Length;
            config[offset++] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(config.AsSpan(offset), (ushort)_pps.Length);
            _pps.CopyTo(config, offset + 2);
            _decoder = new H264Decoder(config) { Threads = 1, BorrowFrames = true };
        }
        byte[] sample = new byte[length];
        int position = 0;
        foreach (var unit in units)
        {
            BinaryPrimitives.WriteInt32BigEndian(sample.AsSpan(position), unit.Length);
            unit.CopyTo(sample, position + 4);
            position += 4 + unit.Length;
        }
        long timestamp = _timestamp++;
        List<VideoFrame> frames = [];
        try
        {
            frames.AddRange(_decoder.Decode(sample, timestamp));
            // RDP supplies one complete picture per substream, in presentation order. Flush
            // drains only the output queue in ManagedCodecs 0.6; it retains prediction references.
            frames.AddRange(_decoder.Flush());
            if (frames.Count != 1) throw new RdpProtocolException("AVC update must produce exactly one picture.");
            var frame = frames[0];
            if (frame.Format != PixelFormat.Yuv420P || frame.IsDamaged || frame.Pts != timestamp ||
                frame.Width < width || frame.Height < height ||
                frame.Width > ((width + 15) & ~15) || frame.Height > ((height + 15) & ~15))
                throw new RdpProtocolException("AVC picture does not match its GFX surface.");
            return frame;
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
    }

    private static List<byte[]> SplitAnnexB(ReadOnlySpan<byte> source)
    {
        List<byte[]> result = [];
        int start = -1;
        for (int i = 0; i + 2 < source.Length; i++)
        {
            if (source[i] != 0 || source[i + 1] != 0 || source[i + 2] != 1) continue;
            int end = i;
            while (end > 0 && source[end - 1] == 0) end--;
            if (start >= 0)
            {
                if (end <= start) throw new RdpProtocolException("Empty AVC NAL unit.");
                result.Add(source[start..end].ToArray());
            }
            else
            {
                for (int j = 0; j < end; j++)
                    if (source[j] != 0) throw new RdpProtocolException("Invalid AVC Annex-B prefix.");
            }
            if (result.Count >= 65536) throw new RdpProtocolException("Too many AVC NAL units.");
            start = i + 3;
            i += 2;
        }
        if (start < 0 || start >= source.Length) throw new RdpProtocolException("Missing AVC Annex-B NAL unit.");
        int last = source.Length;
        while (last > start && source[last - 1] == 0) last--;
        if (last <= start) throw new RdpProtocolException("Empty AVC NAL unit.");
        result.Add(source[start..last].ToArray());
        return result;
    }

    internal static long ValidateSps(ReadOnlySpan<byte> nal, int width, int height)
    {
        if (nal.Length < 5 || nal.Length > ushort.MaxValue)
            throw new RdpProtocolException("Invalid AVC SPS length.");
        var bits = new RbspReader(nal[1..]);
        int profile = bits.Read(8);
        bits.Read(8);
        bits.Read(8);
        if (bits.Unsigned() > 31) throw new RdpProtocolException("Invalid AVC SPS ID.");
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            int chroma = bits.Unsigned();
            if (chroma != 1 || bits.Unsigned() != 0 || bits.Unsigned() != 0)
                throw new RdpProtocolException("GFX AVC requires 8-bit YUV420.");
            bits.Read(1);
            if (bits.Read(1) != 0)
                for (int i = 0; i < 8; i++)
                {
                    if (bits.Read(1) == 0) continue;
                    int last = 8, next = 8;
                    for (int j = 0; j < (i < 6 ? 16 : 64); j++)
                    {
                        if (next != 0) next = (last + bits.Signed()) & 255;
                        if (next != 0) last = next;
                    }
                }
        }
        if (bits.Unsigned() > 12) throw new RdpProtocolException("Invalid AVC frame number size.");
        int poc = bits.Unsigned();
        if (poc == 0)
        {
            if (bits.Unsigned() > 12) throw new RdpProtocolException("Invalid AVC picture order size.");
        }
        else if (poc == 1)
        {
            bits.Read(1);
            bits.Signed();
            bits.Signed();
            int count = bits.Unsigned();
            if (count > 255) throw new RdpProtocolException("Invalid AVC picture order cycle.");
            for (int i = 0; i < count; i++) bits.Signed();
        }
        else if (poc != 2) throw new RdpProtocolException("Invalid AVC picture order type.");
        int references = bits.Unsigned();
        if (references > 16) throw new RdpProtocolException("Too many AVC reference frames.");
        bits.Read(1);
        long codedWidth = ((long)bits.Unsigned() + 1) * 16;
        long codedHeight = ((long)bits.Unsigned() + 1) * 16;
        int frameOnly = bits.Read(1);
        codedHeight *= 2 - frameOnly;
        if (codedWidth > ((width + 15L) & ~15L) || codedHeight > ((height + 15L) & ~15L))
            throw new RdpProtocolException("AVC coded dimensions exceed the GFX surface.");
        if (frameOnly == 0) bits.Read(1);
        bits.Read(1);
        long cropX = 0, cropY = 0;
        if (bits.Read(1) != 0)
        {
            cropX = ((long)bits.Unsigned() + bits.Unsigned()) * 2;
            cropY = ((long)bits.Unsigned() + bits.Unsigned()) * 2 * (2 - frameOnly);
        }
        if (codedWidth - cropX < width || codedHeight - cropY < height)
            throw new RdpProtocolException("AVC cropped dimensions do not cover the GFX surface.");
        long budget = codedWidth * codedHeight * (references + 3) * 8 + (long)width * height * 4;
        if (budget > 1024L * 1024 * 1024) throw new RdpProtocolException("AVC reference storage exceeds the memory budget.");
        return budget;
    }

    private ref struct RbspReader
    {
        private readonly ReadOnlySpan<byte> _source;
        private int _position, _current, _remaining, _zeros;
        internal RbspReader(ReadOnlySpan<byte> source) => _source = source;
        internal int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                if (_remaining == 0)
                {
                    if (_position >= _source.Length) throw new RdpProtocolException("Truncated AVC SPS.");
                    int current = _source[_position++];
                    if (_zeros >= 2 && current == 3)
                    {
                        if (_position >= _source.Length || _source[_position] > 3)
                            throw new RdpProtocolException("Invalid AVC emulation prevention.");
                        _zeros = 0;
                        current = _source[_position++];
                    }
                    _zeros = current == 0 ? _zeros + 1 : 0;
                    _current = current;
                    _remaining = 8;
                }
                value = (value << 1) | ((_current >> --_remaining) & 1);
            }
            return value;
        }
        internal int Unsigned()
        {
            int zeros = 0;
            while (Read(1) == 0)
                if (++zeros > 30) throw new RdpProtocolException("AVC Exp-Golomb value is too large.");
            return ((1 << zeros) - 1) + Read(zeros);
        }
        internal int Signed()
        {
            int value = Unsigned();
            return (value & 1) == 0 ? -(value / 2) : value / 2 + 1;
        }
    }
}
