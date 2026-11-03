using System.Buffers.Binary;
using VelaShell.Rdp.Audio;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class AudioPlayoutTimingTests
{
    [TestMethod]
    public void JitterBufferGrowsForBurstsThenReturnsToLowLatency()
    {
        var timing = new AudioPlayoutTiming();
        double arrival = 0;
        for (int i = 0; i < 100; i++)
        {
            timing.ObserveArrival(arrival += 20, 20);
        }

        double stable = timing.TargetMilliseconds;
        for (int i = 0; i < 100; i++)
        {
            timing.ObserveArrival(arrival += i % 2 == 0 ? 5 : 35, 20);
        }

        Assert.IsTrue(timing.TargetMilliseconds > stable + 30 && timing.TargetMilliseconds <= 80);
        for (int i = 0; i < 200; i++)
        {
            timing.ObserveArrival(arrival += 20, 20);
        }

        Assert.IsTrue(timing.TargetMilliseconds < stable + 1);
        timing.ObserveArrival(arrival + 10000, 20);
        Assert.IsTrue(timing.TargetMilliseconds < stable + 1, "Idle time is not network jitter.");
    }

    [TestMethod]
    [DataRow(-0.001)]
    [DataRow(0.001)]
    public void DeviceClockFeedbackStabilizesTenMinutesOfClockDrift(double clockError)
    {
        var timing = new AudioPlayoutTiming();
        double queued = timing.TargetMilliseconds;
        for (int i = 0; i < 30000; i++)
        {
            double rate = timing.UpdateRate(queued, 20);
            queued += 20 / (1 + rate) - 20 / (1 + clockError);
            Assert.IsTrue(queued is > 0 and < 80, $"Buffer escaped its operating range at packet {i}: {queued} ms.");
            Assert.IsTrue(Math.Abs(rate) <= 0.005);
        }
        Assert.AreEqual(clockError, timing.RateCorrection, 0.0001);
        Assert.AreEqual(timing.TargetMilliseconds, queued, 2);
        timing.ResetDrift();
        Assert.AreEqual(0, timing.RateCorrection);
    }

    [TestMethod]
    [DataRow(1, 8, 100)]
    [DataRow(2, 16, 100)]
    [DataRow(1, 8, 0)]
    [DataRow(2, 16, 0)]
    public void FractionalResamplingCorrectsSmallPacketsWithoutMixingChannels(int channels, int bits, int occupancy)
    {
        var timing = new AudioPlayoutTiming();
        var format = new AudioFormat((ushort)channels, 48000, (ushort)bits);
        for (int i = 0; i < 10000; i++)
        {
            _ = timing.UpdateRate(occupancy, 20);
        }

        byte[] input = new byte[32 * format.BlockAlign];
        if (bits == 8)
        {
            Array.Fill(input, (byte)128);
        }
        else
        {
            for (int i = 0; i < 32; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 4), 30000);
                BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 4 + 2), -30000);
            }
        }
        int totalFrames = 0;
        for (int packet = 0; packet < 100; packet++)
        {
            byte[] output = timing.Resample(format, input);
            Assert.AreEqual(0, output.Length % format.BlockAlign);
            totalFrames += output.Length / format.BlockAlign;
            if (bits == 8)
            {
                Assert.IsTrue(output.All(value => value == 128));
            }
            else
            {
                for (int i = 0; i < output.Length; i += 4)
                {
                    Assert.AreEqual((short)30000, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i)));
                    Assert.AreEqual((short)-30000, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i + 2)));
                }
            }
        }
        Assert.AreEqual(3200 / (1 + timing.RateCorrection), totalFrames, 1.0);
        Assert.AreNotEqual(3200, totalFrames);
    }
}
