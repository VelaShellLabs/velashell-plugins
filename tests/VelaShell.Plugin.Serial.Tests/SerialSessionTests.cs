using System.IO.Ports;
using System.Text;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// 会话:读循环、EOF 归一化、写侧序列化与控制线动作。
/// 全部跑在 <see cref="FakeSerialPort" /> 上 —— 串口没有环回可用,而这几条恰恰是
/// "真机上偶尔出事、事后无从复现"的地方。
/// </summary>
[TestClass]
public sealed class SerialSessionTests
{
    private static SerialConfig Config(
        SerialEnterMode enterMode = SerialEnterMode.Cr,
        bool localEcho = false,
        bool implicitLf = false,
        int delayPerByteMs = 0) =>
        new("COM-TEST", 115200, 8, StopBits.One, Parity.None, Handshake.None,
            Dtr: true, Rts: true, enterMode, implicitLf, ImplicitCr: false, localEcho,
            SerialConfig.ParseDelay(delayPerByteMs), TimeSpan.Zero);

    private static SerialSession Open(FakeSerialPort port, SerialConfig? config = null) =>
        SerialSession.Connect(config ?? Config(), new CollectingLogger(), _ => port);

    /// <summary>读满 <paramref name="expected" /> 个字节,或到超时为止。</summary>
    private static async Task<byte[]> ReadExactAsync(SerialSession session, int expected)
    {
        var got = new List<byte>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[64];
        while (got.Count < expected)
        {
            int read = await session.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }
            got.AddRange(buffer.AsSpan(0, read));
        }
        return [.. got];
    }

    [TestMethod]
    public async Task Read_DeliversWhatTheDeviceSent()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);
        port.Feed(Encoding.ASCII.GetBytes("hello"));

        byte[] got = await ReadExactAsync(session, 5);

        Assert.AreEqual("hello", Encoding.ASCII.GetString(got));
    }

    [TestMethod]
    public async Task Read_AppliesTheReceiveDiscipline()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port, Config(implicitLf: true));
        port.Feed((byte)'a', 0x0D, (byte)'b');

        byte[] got = await ReadExactAsync(session, 4);

        Assert.AreSequenceEqual(new byte[] { (byte)'a', 0x0D, 0x0A, (byte)'b' }, got);
    }

    [TestMethod]
    public async Task Read_SpansMoreThanOneCallWhenTheBufferIsSmall()
    {
        // 宿主给的缓冲区未必装得下一整块;剩下的必须留到下一次读,不能丢。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);
        port.Feed(Encoding.ASCII.GetBytes("abcdef"));

        byte[] small = new byte[2];
        var got = new List<byte>();
        for (int i = 0; i < 3; i++)
        {
            int read = await session.ReadAsync(small, CancellationToken.None);
            got.AddRange(small.AsSpan(0, read));
        }

        Assert.AreEqual("abcdef", Encoding.ASCII.GetString([.. got]));
    }

    [TestMethod]
    public async Task Read_ReturnsEofWhenTheAdapterIsUnplugged()
    {
        // 这是串口最典型的"异常":USB 转串口被拔掉,驱动层一路抛 IOException。
        // **必须归一成 EOF** —— 抛给宿主只会得到一个红色异常框,而归一成 EOF
        // 才能走到标签页那条"已断开 + 可重连"的既有路径上。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);
        port.Feed(Encoding.ASCII.GetBytes("hi"));
        await ReadExactAsync(session, 2);

        port.Fail(new IOException("The device is not connected."));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await session.ReadAsync(new byte[16], timeout.Token);
        Assert.AreEqual(0, read, "拔线应当表现为 EOF 而不是异常");
    }

    [TestMethod]
    public async Task Read_ReturnsEofAfterUnauthorizedAccess()
    {
        // 设备断电 / 驱动卸载在 Windows 上给的是 UnauthorizedAccessException,同样归一。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);
        port.Fail(new UnauthorizedAccessException());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, await session.ReadAsync(new byte[16], timeout.Token));
    }

    [TestMethod]
    public async Task Dispose_ClosesThePortFromTheReaderThread()
    {
        // 端口由**读线程自己**在退出时关 —— 这正是绕开 dotnet/runtime#20362
        // (Close() 在硬件流控卡住时永久阻塞)的做法。
        var port = new FakeSerialPort();
        SerialSession session = Open(port);

        await session.DisposeAsync();

        Assert.IsTrue(port.IsDisposed);
        Assert.IsFalse(session.IsOpen);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        var port = new FakeSerialPort();
        SerialSession session = Open(port);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.IsTrue(port.IsDisposed);
    }

    [TestMethod]
    public async Task Read_ReturnsEofAfterDispose()
    {
        var port = new FakeSerialPort();
        SerialSession session = Open(port);
        await session.DisposeAsync();

        Assert.AreEqual(0, await session.ReadAsync(new byte[16], CancellationToken.None));
    }

    [TestMethod]
    public async Task Write_PutsBytesOnTheWireUnchangedByDefault()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);

        await session.WriteAsync(new byte[] { (byte)'l', (byte)'s', 0x0D }, CancellationToken.None);

        Assert.AreSequenceEqual(new byte[] { (byte)'l', (byte)'s', 0x0D }, port.Written);
    }

    [TestMethod]
    public async Task Write_AppliesTheEnterModeRewrite()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port, Config(SerialEnterMode.CrLf));

        await session.WriteAsync(new byte[] { (byte)'x', 0x0D }, CancellationToken.None);

        Assert.AreSequenceEqual(new byte[] { (byte)'x', 0x0D, 0x0A }, port.Written);
    }

    [TestMethod]
    public async Task Write_EchoesLocallyBeforeTheDeviceAnswers()
    {
        // 对着从不回显的设备(裸 UART 模块),没有这条用户就是"打字看不见"。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port, Config(localEcho: true));

        await session.WriteAsync(new byte[] { (byte)'A', 0x0D }, CancellationToken.None);
        byte[] echoed = await ReadExactAsync(session, 3);

        Assert.AreSequenceEqual(new byte[] { (byte)'A', 0x0D, 0x0A }, echoed);
        Assert.AreSequenceEqual(new byte[] { (byte)'A', 0x0D }, port.Written, "回显只影响屏幕:线上不该多出那个补显用的 LF");
    }

    [TestMethod]
    public async Task Write_DoesNotEchoWhenEchoIsOff()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);

        await session.WriteAsync(new byte[] { (byte)'A' }, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        int read = 0;
        try
        {
            read = await session.ReadAsync(new byte[8], timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 期望的:没有东西可读。
        }
        Assert.AreEqual(0, read);
    }

    [TestMethod]
    public async Task Write_IsSerialisedAcrossConcurrentCallers()
    {
        // SDK 明写了写会被界面线程与后台任务并发调用。不序列化的后果是
        // "两次粘贴的字节互相穿插",而且节流路径下窗口更大。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port, Config(delayPerByteMs: 1));
        byte[] first = [.. Enumerable.Repeat((byte)'a', 8)];
        byte[] second = [.. Enumerable.Repeat((byte)'b', 8)];

        await Task.WhenAll(
            session.WriteAsync(first, CancellationToken.None).AsTask(),
            session.WriteAsync(second, CancellationToken.None).AsTask());

        string written = Encoding.ASCII.GetString(port.Written);
        Assert.IsTrue(written is "aaaaaaaabbbbbbbb" or "bbbbbbbbaaaaaaaa",
            $"两次写不该交织,实际写出的是 {written}");
    }

    [TestMethod]
    public async Task Write_AfterDispose_IsANoOp()
    {
        var port = new FakeSerialPort();
        SerialSession session = Open(port);
        await session.DisposeAsync();

        await session.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.AreEqual(0, port.Written.Length);
    }

    [TestMethod]
    public async Task Resize_IsANoOp()
    {
        // 串口没有窗口尺寸这回事。抛异常会让每一次拉窗口都在日志里刷一条。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);

        await session.ResizeAsync(200, 60, CancellationToken.None);
    }

    // ── 带外动作 ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendBreak_AssertsThenClearsTheLine()
    {
        // Cisco ROMMON 口令恢复、U-Boot 打断自动启动、内核 SysRq —— 都靠它。
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);

        Assert.IsTrue(await session.SendBreakAsync(CancellationToken.None));

        Assert.AreSequenceEqual([true, false], port.BreakStates);
    }

    [TestMethod]
    public async Task Pulse_ReturnsTheLineToWhereItWas()
    {
        // 复位开发板:翻转 → 等 → **翻回**。不翻回的话板子就一直按着复位不放。
        var port = new FakeSerialPort { DtrEnable = true };
        await using SerialSession session = Open(port);

        Assert.IsTrue(await session.PulseAsync(SerialControlLine.Dtr, CancellationToken.None));

        Assert.IsTrue(port.DtrEnable);
    }

    [TestMethod]
    public async Task Toggle_FlipsAndHolds()
    {
        var port = new FakeSerialPort { DtrEnable = true };
        await using SerialSession session = Open(port);

        Assert.IsTrue(await session.ToggleAsync(SerialControlLine.Dtr, CancellationToken.None));

        Assert.IsFalse(port.DtrEnable);
    }

    [TestMethod]
    public async Task Toggle_LeavesRtsAloneWhenTheDriverOwnsIt()
    {
        // 流控制取 RTS/CTS 时 RtsEnable 的 setter 会直接抛 InvalidOperationException,
        // 不是"设了没生效"。
        var port = new FakeSerialPort { CanControlRts = false, RtsEnable = true };
        await using SerialSession session = Open(port);

        await session.ToggleAsync(SerialControlLine.Rts, CancellationToken.None);

        Assert.IsTrue(port.RtsEnable);
    }

    [TestMethod]
    public async Task ControlActions_AreNoOpsAfterDispose()
    {
        var port = new FakeSerialPort();
        SerialSession session = Open(port);
        await session.DisposeAsync();

        Assert.IsFalse(await session.SendBreakAsync(CancellationToken.None));
        Assert.AreEqual(0, port.BreakStates.Count);
    }

    [TestMethod]
    public async Task Write_RaisesActivitySoCommandsCanFindTheSession()
    {
        var port = new FakeSerialPort();
        await using SerialSession session = Open(port);
        SerialSession? seen = null;
        session.Activity += s => seen = s;

        await session.WriteAsync(new byte[] { (byte)'x' }, CancellationToken.None);

        Assert.AreSame(session, seen);
    }
}
