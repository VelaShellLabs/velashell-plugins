using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class InputEngineTests
{
    [TestMethod]
    [DataRow((ushort)0x1e, (byte)0x00, (byte)0x1e)]
    [DataRow((ushort)0x153, (byte)0x02, (byte)0x53)]
    [DataRow((ushort)0x1d, (byte)0x00, (byte)0x1d)]
    [DataRow((ushort)0x11d, (byte)0x02, (byte)0x1d)]
    [DataRow((ushort)0x38, (byte)0x00, (byte)0x38)]
    [DataRow((ushort)0x138, (byte)0x02, (byte)0x38)]
    public void RepeatPress_HasOneEventAndNoRelease(ushort scan, byte flags, byte code)
    {
        byte[] actual = RdpRepeatInput.EncodePress(scan);
        CollectionAssert.AreEqual(new byte[] { 4, 4, flags, code }, actual);
        Assert.AreEqual(0, actual[2] & 1);
        using var database = Devolutions.IronRdp.InputDatabase.New();
        using var scancode = Devolutions.IronRdp.Scancode.FromU8((scan & 0x100) != 0, (byte)scan);
        using var press = scancode.AsOperationKeyPressed();
        using var first = database.Apply(press);
        // Repeats do not touch native state; the matching real release is still applied once.
        using var release = scancode.AsOperationKeyReleased();
        using var released = database.Apply(release);
    }
    [TestMethod]
    public void NotificationSubscriberExceptions_DoNotSuppressLaterSubscribers()
    {
        bool nextFrame = false, nextState = false;
        System.Action frame = () => throw new InvalidOperationException();
        frame += () => nextFrame = true;
        System.Action<RdpSessionStatus> state = _ => throw new InvalidOperationException();
        state += _ => nextState = true;
        RdpSession.NotifySafely(frame);
        RdpSession.NotifySafely(state, new(RdpSessionState.Disconnected));
        Assert.IsTrue(nextFrame); Assert.IsTrue(nextState);
    }
    [TestMethod]
    public void Credentials_AreNotPrintableAndCannotBeReusedAfterDisposal()
    {
        var credentials = new RdpCredentials("alice", "secret", "EXAMPLE");
        Assert.AreEqual("secret", credentials.GetPasswordForNative());
        Assert.AreEqual("RdpCredentials", credentials.ToString());
        credentials.Dispose(); credentials.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credentials.GetPasswordForNative());
    }
}
