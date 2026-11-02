using System.Buffers.Binary;

namespace VelaShell.Rdp.Audio;

internal sealed class AudioPlayoutTiming
{
    private double? _lastArrival;
    private double _lastDuration;
    private double _jitter;
    private double _filteredError;
    private double _integral;
    private double _fractionalFrames;

    internal double TargetMilliseconds => Math.Clamp(20 + 4 * _jitter, 20, 80);
    internal double RateCorrection { get; private set; }

    internal void ObserveArrival(double arrivalMilliseconds, double durationMilliseconds)
    {
        if (_lastArrival is { } previous)
        {
            double interval = arrivalMilliseconds - previous;
            if (interval >= 0 && interval < _lastDuration + 500)
                _jitter += (Math.Abs(interval - _lastDuration) - _jitter) / 16;
        }
        _lastArrival = arrivalMilliseconds;
        _lastDuration = durationMilliseconds;
    }

    internal void ResetDrift()
    {
        _filteredError = _integral = _fractionalFrames = RateCorrection = 0;
    }

    internal double UpdateRate(double bufferedMilliseconds, double durationMilliseconds)
    {
        // Device-clock occupancy is filtered over seconds: packet jitter must not modulate pitch.
        double seconds = Math.Min(durationMilliseconds / 1000, 0.25);
        double error = Math.Clamp(bufferedMilliseconds - TargetMilliseconds, -100, 100);
        _filteredError += (error - _filteredError) * (1 - Math.Exp(-seconds / 5));
        _integral = Math.Clamp(_integral + _filteredError * seconds * 0.000002, -0.004, 0.004);
        RateCorrection = Math.Clamp(_filteredError * 0.00005 + _integral, -0.005, 0.005);
        return RateCorrection;
    }

    internal byte[] Resample(AudioFormat format, byte[] data)
    {
        int frames = data.Length / format.BlockAlign;
        if (frames == 0) return data;
        double exactFrames = frames / (1 + RateCorrection) + _fractionalFrames;
        int outputFrames = Math.Max(1, (int)exactFrames);
        _fractionalFrames = Math.Max(0, exactFrames - outputFrames);
        if (outputFrames == frames) return data;

        byte[] result = new byte[outputFrames * format.BlockAlign];
        int sampleBytes = format.BitsPerSample / 8;
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double position = (double)frame * frames / outputFrames;
            int first = (int)position;
            int second = Math.Min(first + 1, frames - 1);
            double fraction = position - first;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                int a = first * format.BlockAlign + channel * sampleBytes;
                int b = second * format.BlockAlign + channel * sampleBytes;
                int destination = frame * format.BlockAlign + channel * sampleBytes;
                if (sampleBytes == 1)
                    result[destination] = (byte)Math.Round(data[a] + (data[b] - data[a]) * fraction);
                else
                {
                    short left = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(a));
                    short right = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(b));
                    BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(destination),
                        (short)Math.Round(left + (right - left) * fraction));
                }
            }
        }
        return result;
    }
}
