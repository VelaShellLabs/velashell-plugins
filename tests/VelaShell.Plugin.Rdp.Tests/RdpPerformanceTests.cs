using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.Plugin.Rdp.Ui;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Opt-in CPU pixel-copy measurements, not a decoder, renderer, present-FPS benchmark, or acceptance gate.
/// Run with VELASHELL_RDP_PIXEL_BENCHMARK=1 and a detailed console test logger.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpPerformanceTests
{
    private const int WarmupFrames = 32;
    private const int MeasuredFrames = 128;
    private const int Repetitions = 5;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 720, false)]
    [DataRow(1280, 720, true)]
    [DataRow(1920, 1080, false)]
    [DataRow(1920, 1080, true)]
    [DataRow(3840, 2160, false)]
    [DataRow(3840, 2160, true)]
    [TestCategory("ManualPerformance")]
    public async Task SyntheticPixelPath_ReportsSteadyStateCopiesAndAllocations(int width, int height, bool direct)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VELASHELL_RDP_PIXEL_BENCHMARK"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Manual CPU pixel-copy measurement not run. Set VELASHELL_RDP_PIXEL_BENCHMARK=1 explicitly; skipping is not performance acceptance.");
        }

        int bytes = checked(width * height * 4);
        string path = direct ? "production-direct-2copy" : "reconstructed-legacy-3copy";
        int copies = direct ? 2 : 3;
        // Unmanaged stand-ins for decoded native pixels and locked bitmap storage. No IronRdp decoding,
        // Avalonia bitmap, dispatcher, GPU, or actual presentation. Publish and direct TryCopy are production.
        IntPtr nativeSnapshot = Marshal.AllocHGlobal(bytes);
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            bitmap = Marshal.AllocHGlobal(bytes);
            var expected = new byte[bytes];
            var actual = new byte[bytes];
            for (int i = 0; i < bytes; i++) expected[i] = unchecked((byte)(i * 31 + i / 251));
            Marshal.Copy(expected, 0, nativeSnapshot, bytes);
            byte[] uiPixels = direct ? [] : new byte[bytes];
            await using var frames = new RdpFrameBuffer(width, height, static () => { });
            System.Action<byte[]> fill = target => Marshal.Copy(nativeSnapshot, target, 0, bytes);

            TestContext.WriteLine($"SYNTHETIC CPU ONLY: {width}x{height}, path={path}, runtime={RuntimeInformation.FrameworkDescription}, OS={RuntimeInformation.OSDescription}, process={RuntimeInformation.ProcessArchitecture}; warmup={WarmupFrames}, frames/repetition={MeasuredFrames}, repetitions={Repetitions}.");
            if (!direct)
                TestContext.WriteLine("LEGACY REFERENCE ONLY: removed Memory<byte> TryCopy reconstructed with production TryAcquire + Pixels.CopyTo, then production UI CopyRows. This is not the historical real pre-change production measurement and must not replace that separately captured baseline.");
            TestContext.WriteLine($"THEORETICAL {path}: frameBytes={bytes}; copiedBytes/frame={(long)copies * bytes} (payload copied, not read+write bus traffic); managedPixelCapacity={2L * bytes + uiPixels.Length} (FrameBuffer front+back and optional legacy UI array); uiIntermediateCapacity={uiPixels.Length}; bitmapPixelCapacity={bytes}; nativeSourceStandInCapacity={bytes}; harnessVerificationScratch={2L * bytes}. Fixed-size steady-state capacities exclude resize overlap, headers/allocator overhead and are not process/private-byte measurements.");

            RunFrames(frames, fill, nativeSnapshot, bitmap, uiPixels, width, height, direct, WarmupFrames);
            VerifyPixels(nativeSnapshot, bitmap, expected, actual, WarmupFrames);
            var results = new Sample[Repetitions];
            for (int repetition = 0; repetition < Repetitions; repetition++)
            {
                // No GC forcing: report actual synchronous current-thread allocations after warmup.
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                RunFrames(frames, fill, nativeSnapshot, bitmap, uiPixels, width, height, direct, MeasuredFrames);
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                results[repetition] = new(elapsed / (double)Stopwatch.Frequency, allocated);
                VerifyPixels(nativeSnapshot, bitmap, expected, actual, MeasuredFrames);
                Assert.AreEqual(width, frames.Info.Width);
                Assert.AreEqual(height, frames.Info.Height);
                Assert.AreEqual(bytes, frames.Info.ByteLength);
            }

            foreach (Sample sample in results)
            {
                double copiedGiB = (double)copies * bytes * MeasuredFrames / (1024 * 1024 * 1024);
                TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"MEASURED {path} {width}x{height}: elapsedMs={sample.Seconds * 1000:F3}; allocatedBytes={sample.AllocatedBytes}; allocatedBytes/frame={sample.AllocatedBytes / (double)MeasuredFrames:F2}; theoreticalCopiedBytes={(long)copies * bytes * MeasuredFrames}; payloadGiB/s={copiedGiB / sample.Seconds:F3}; CPU-pixel-path-iterations/s={MeasuredFrames / sample.Seconds:F2}. No timing threshold; not presentation FPS."));
            }
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Marshal.FreeHGlobal(bitmap);
            Marshal.FreeHGlobal(nativeSnapshot);
        }
    }

    private static void RunFrames(RdpFrameBuffer frames, System.Action<byte[]> fill, IntPtr nativeSnapshot,
        IntPtr bitmap, byte[] uiPixels, int width, int height, bool direct, int count)
    {
        int stride = checked(width * 4);
        int bytes = checked(stride * height);
        for (int frame = 0; frame < count; frame++)
        {
            // Change three separated pixels so retained/stale destination data cannot pass verification.
            byte marker = unchecked((byte)(frame + 1));
            Marshal.WriteByte(nativeSnapshot, 0, marker);
            Marshal.WriteByte(nativeSnapshot, bytes / 2, marker);
            Marshal.WriteByte(nativeSnapshot, bytes - 1, marker);
            if (!frames.Publish(fill)) throw new InvalidOperationException("Synthetic single-consumer publication unexpectedly failed.");
            if (direct)
            {
                if (!frames.TryCopy(bitmap, width, height, stride, bytes, out _))
                    throw new InvalidOperationException("Synthetic direct production copying unexpectedly failed.");
            }
            else
            {
                // Explicitly reconstructed legacy reference, not a retained production Memory<byte> API.
                if (!frames.TryAcquire(out var lease)) throw new InvalidOperationException("Synthetic legacy acquisition unexpectedly failed.");
                RdpFrameInfo copied;
                using (lease)
                {
                    copied = lease.Info;
                    lease.Pixels.CopyTo(uiPixels);
                }
                RdpDesktopSurface.CopyRows(uiPixels, copied, bitmap, stride);
            }
        }
    }

    private static void VerifyPixels(IntPtr nativeSnapshot, IntPtr bitmap, byte[] expected, byte[] actual, int count)
    {
        byte marker = unchecked((byte)count);
        expected[0] = marker;
        expected[expected.Length / 2] = marker;
        expected[^1] = marker;
        // Full-byte verification is deliberately outside timing/allocation windows, reusing scratch.
        Marshal.Copy(bitmap, actual, 0, actual.Length);
        Assert.AreSequenceEqual(expected, actual);
        Assert.AreEqual(marker, Marshal.ReadByte(nativeSnapshot, 0));
    }

    private readonly record struct Sample(double Seconds, long AllocatedBytes);
}
