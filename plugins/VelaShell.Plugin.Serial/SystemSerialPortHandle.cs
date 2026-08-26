using System.IO.Ports;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// <see cref="ISerialPortHandle" /> 的真实实现:<c>System.IO.Ports.SerialPort</c>。
/// 这里只做三件事 —— 按配置开口子、把两个已知的坑绕开、把异常翻成人话。
/// </summary>
internal sealed class SystemSerialPortHandle : ISerialPortHandle
{
    /// <summary>
    /// 读超时。**必须是有限值**,而且这是整个会话生命周期管理的地基:
    /// 读循环靠它每 250ms 回头看一眼"是不是该收摊了",于是关闭时不需要去唤醒一个
    /// 阻塞中的读 —— 而"唤醒阻塞中的读"正是 dotnet/runtime#20362(<c>Close()</c> 在
    /// 硬件流控卡住时永久阻塞)与 #44952(关闭竞态 NRE)两个至今 open 的 issue 的来源。
    /// 让**读线程自己**在退出时关端口,这两条就都碰不到了。
    /// </summary>
    private const int ReadTimeoutMs = 250;

    /// <summary>
    /// 写超时。RTS/CTS 流控下对端一直不拉 CTS,写会一直卡着 —— 无限等的话
    /// 用户看到的是"终端不响应了",而且关会话时还要多一条卡死路径。
    /// 超时后由上层记一条能看懂的日志。
    /// </summary>
    private const int WriteTimeoutMs = 5000;

    private readonly SerialPort _port;

    private SystemSerialPortHandle(SerialPort port)
    {
        _port = port;
        CanControlRts = port.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff);
    }

    /// <inheritdoc />
    public string PortName => _port.PortName;

    /// <inheritdoc />
    public bool CanControlRts { get; }

    /// <inheritdoc />
    public bool DtrEnable
    {
        get => _port.DtrEnable;
        set => _port.DtrEnable = value;
    }

    /// <inheritdoc />
    public bool RtsEnable
    {
        get => _port.RtsEnable;
        // 流控制取 RTS/CTS 时 setter 会抛 InvalidOperationException:RTS 归驱动。
        // 静默忽略而不是让它抛 —— 调用方(命令面板里的「切换 RTS」)按 CanControlRts 拦过一道,
        // 这里是第二道保险。
        set
        {
            if (CanControlRts)
            {
                _port.RtsEnable = value;
            }
        }
    }

    /// <summary>
    /// 按配置打开一个串口。
    /// </summary>
    /// <param name="config">会话配置。</param>
    /// <returns>已打开的句柄。</returns>
    /// <exception cref="UnauthorizedAccessException">端口被占用,或(Linux)当前用户不在 dialout 组。</exception>
    /// <exception cref="IOException">端口不存在或参数被驱动拒绝。</exception>
    public static ISerialPortHandle Open(SerialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var port = new SerialPort(config.PortName, config.BaudRate, config.Parity, config.DataBits, config.StopBits)
        {
            Handshake = config.Handshake,
            ReadTimeout = ReadTimeoutMs,
            WriteTimeout = WriteTimeoutMs,
            // 8 位透明的前提。DiscardNull 会把线上的 0x00 吃掉 —— 二进制协议
            // (ZMODEM、固件烧写)首当其冲,而且症状是"偶尔损坏",极难定位。
            DiscardNull = false,
            // 缓冲区给大一些:USB 转串口在高波特率下一次能吐很多,默认 4096 在
            // 921600 上会溢出(表现为 RXOVER / 丢字节)。
            ReadBufferSize = 65536,
            WriteBufferSize = 65536
        };
        try
        {
            port.Open();
            // ── DTR / RTS 必须在 Open 之后置 ──────────────────────────────────
            // Open 之前设是不生效的(没有句柄可下发)。顺序也有讲究:先 DTR 后 RTS,
            // 与 esptool 之类工具一致;反过来在部分 ESP32 板子上会撞进下载模式。
            port.DtrEnable = config.Dtr;
            if (port.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
            {
                port.RtsEnable = config.Rts;
            }
            // 打开前残留在驱动缓冲里的东西不是本次会话的内容(上一次会话没读完的、
            // 或者设备在没人连的时候一直在吐的日志)。留着会让终端一开就是一屏乱码。
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
            port.Dispose();
            throw;
        }
        return new SystemSerialPortHandle(port);
    }

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int count) => _port.BaseStream.Read(buffer, offset, count);

    /// <inheritdoc />
    public void Write(byte[] buffer, int offset, int count) => _port.BaseStream.Write(buffer, offset, count);

    /// <inheritdoc />
    public void SetBreak(bool on) => _port.BreakState = on;

    /// <inheritdoc />
    public void Dispose() => _port.Dispose();
}
