using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class CameraChannelTests
{
    private static readonly CameraMediaType Mjpg = new(2, 640, 480, 30, 1, 1, 1, 1);
    private sealed class FakeCamera : ICameraBackend
    {
        internal int Activations, Reads, Stops, Disposals;
        internal TaskCompletionSource<byte[]> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CameraMediaType> MediaTypes => [Mjpg];
        public CameraMediaType CurrentMediaType => Mjpg;
        public void Activate() => Activations++;
        public void Start(CameraMediaType type) { }
        public Task<byte[]> ReadSampleAsync(CancellationToken ct) { Reads++; return Next.Task.WaitAsync(ct); }
        public void Stop() => Stops++;
        public void Deactivate() { }
        public void Dispose() => Disposals++;
    }
    private sealed class Harness
    {
        internal readonly ConcurrentQueue<byte[]> Sent = new();
        internal readonly FakeCamera Backend = new();
        internal readonly DrdynvcChannel Dvc;
        internal Harness()
        {
            Dvc = new((bytes, _) => { Sent.Enqueue(bytes); return Task.CompletedTask; }, CancellationToken.None);
            _ = new CameraChannel(Dvc, new("selected-only", "Colour Camera"), () => Backend);
        }
        internal async Task OpenAsync()
        {
            await Dvc.ProcessAsync(Create(1, CameraChannel.EnumeratorName));
            Assert.AreEqual(0, Backend.Activations);
            Assert.AreSequenceEqual(new byte[] { 0x30, 1, 1, 3 }, Sent.Last());
            await Dvc.ProcessAsync([0x30, 1, 1, 4]);
            byte[] added = Sent.Last();
            Assert.AreEqual((byte)5, added[3]);
            int offset = 4;
            while (added[offset] != 0 || added[offset + 1] != 0) offset += 2;
            offset += 2;
            string name = Encoding.ASCII.GetString(added.AsSpan(offset, added.Length - offset - 1));
            await Dvc.ProcessAsync(Create(2, name));
        }
        internal Task Request(params byte[] message) => Dvc.ProcessAsync([0x30, 2, .. message]);
        internal byte[] LastMessage => Sent.Last()[2..];
    }
    private static byte[] Create(byte id, string name) => [0x10, id, .. Encoding.ASCII.GetBytes(name), 0];

    [TestMethod]
    public void AsyncCallbackExposesNativeComInterfaceWithoutOpeningCamera()
    {
        if (!OperatingSystem.IsWindows()) return;
        var callback = new WindowsCameraBackend.ReaderCallback();
        nint unknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(callback);
        try
        {
            Guid iid = typeof(IMfCameraReaderCallback).GUID;
            int result = System.Runtime.InteropServices.Marshal.QueryInterface(unknown, in iid, out nint native);
            Assert.AreEqual(0, result);
            if (native != 0) System.Runtime.InteropServices.Marshal.Release(native);
        }
        finally { System.Runtime.InteropServices.Marshal.Release(unknown); }
    }

    [TestMethod]
    public async Task EnumeratesWithoutActivationAndNegotiatesOneRealFormat()
    {
        var h = new Harness(); await h.OpenAsync();
        await h.Request(1, 9);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 0, 0, 0 }, h.LastMessage);
        await h.Request(1, 7);
        Assert.AreEqual(1, h.Backend.Activations);
        await h.Request(1, 9);
        Assert.AreSequenceEqual(new byte[] { 1, 10, 1, 0, 1, 0, 0 }, h.LastMessage);
        await h.Request(1, 11, 0);
        byte[] listed = [1, 12, .. Mjpg.Encode()];
        Assert.AreSequenceEqual(listed, h.LastMessage);
        await h.Request(1, 13, 0);
        byte[] current = [1, 14, .. Mjpg.Encode()];
        Assert.AreSequenceEqual(current, h.LastMessage);
        await h.Dvc.CloseAsync(); Assert.AreEqual(1, h.Backend.Disposals);
    }

    [TestMethod]
    public async Task SampleIsRequestedOnceAndStopCancelsPendingCapture()
    {
        var h = new Harness(); await h.OpenAsync(); await h.Request(1, 7);
        await h.Request([1, 15, 0, .. Mjpg.Encode()]);
        Assert.AreEqual(0, h.Backend.Reads);
        await h.Request(1, 17, 0);
        Assert.AreEqual(1, h.Backend.Reads);
        await h.Request(1, 17, 0);
        Assert.AreSequenceEqual(new byte[] { 1, 19, 0, 4, 0, 0, 0 }, h.LastMessage);
        h.Backend.Next.SetResult([0xff, 0xd8, 0xff, 0xd9]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!h.Sent.Any(x => x.Length > 3 && x[3] == 18)) await Task.Delay(10, timeout.Token);
        Assert.AreSequenceEqual(new byte[] { 1, 18, 0, 0xff, 0xd8, 0xff, 0xd9 }, h.LastMessage);
        h.Backend.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Request(1, 17, 0); await h.Request(1, 16);
        Assert.AreEqual(1, h.Backend.Stops);
        Assert.AreEqual(2, h.Sent.Count(x => x.Length > 3 && x[3] == 19));
        Assert.AreSequenceEqual(new byte[] { 1, 1 }, h.LastMessage);
        int before = h.Sent.Count;
        h.Backend.Next.SetResult([8, 9]);
        await h.Request(1, 8);
        Assert.AreEqual(before + 1, h.Sent.Count);
        Assert.AreEqual(1, h.Backend.Disposals);
        await h.Dvc.CloseAsync();
    }

    [TestMethod]
    public async Task ChannelCloseCancelsPendingSampleWithoutSendingAfterClose()
    {
        var h = new Harness(); await h.OpenAsync(); await h.Request(1, 7);
        await h.Request([1, 15, 0, .. Mjpg.Encode()]);
        await h.Request(1, 17, 0);
        int before = h.Sent.Count;
        await h.Dvc.ProcessAsync([0x40, 2]);
        Assert.AreEqual(before, h.Sent.Count);
        Assert.AreEqual(1, h.Backend.Disposals);
        h.Backend.Next.SetResult([1, 2, 3]);
        await h.Dvc.CloseAsync();
        Assert.AreEqual(before, h.Sent.Count);
    }

    [TestMethod]
    public async Task RejectsMalformedStreamAndUnadvertisedFormat()
    {
        var h = new Harness(); await h.OpenAsync(); await h.Request(1, 7);
        await h.Request(1, 11, 1);
        Assert.AreEqual(5u, BinaryPrimitives.ReadUInt32LittleEndian(h.LastMessage.AsSpan(2)));
        await h.Request(1, 15);
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(h.LastMessage.AsSpan(2)));
        await h.Request([1, 15, 0, .. (Mjpg with { Width = 1280 }).Encode()]);
        Assert.AreEqual(6u, BinaryPrimitives.ReadUInt32LittleEndian(h.LastMessage.AsSpan(2)));
        Assert.AreEqual(0, h.Backend.Reads);
        await h.Dvc.CloseAsync();
    }
}
