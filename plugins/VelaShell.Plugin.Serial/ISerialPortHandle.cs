using System.IO.Ports;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// 一个已打开的串口。存在的唯一理由是**可替身** ——
/// 串口是本仓库里唯一没法在 CI 上真跑的传输(Telnet 有环回 <c>TcpListener</c>,
/// S3 有环回 HTTP 服务器,串口要么真插一根线,要么 <c>socat</c> / <c>com0com</c>)。
/// 把硬件收在这一个接口后面,读循环、EOF 归一化、换行改写、发送节流这些**真正会出错的地方**
/// 就都能在没有硬件的机器上钉住。
/// <para>
/// 形状刻意贴着 <see cref="SerialPort" />(阻塞读写 + 几根控制线),不做异步化 ——
/// 异步在这里是负资产:<c>BaseStream.ReadAsync</c> 在 Windows 上不响应取消令牌
/// (dotnet/runtime#30850,微软自己提的,至今 Future 里程碑),Unix 上响应。
/// 拿一个平台间行为不一致的东西当地基,不如老老实实用带超时的阻塞读。
/// </para>
/// </summary>
internal interface ISerialPortHandle : IDisposable
{
    /// <summary>端口名(日志与错误信息用)。</summary>
    string PortName { get; }

    /// <summary>
    /// 阻塞读。至少读到 1 字节即返回;到 <c>ReadTimeout</c> 仍无数据抛
    /// <see cref="TimeoutException" />(**正常控制流**,读循环据此回头看一眼是否该收摊)。
    /// </summary>
    /// <param name="buffer">缓冲区。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="count">最多读多少。</param>
    /// <returns>读到的字节数。</returns>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>阻塞写。硬件流控卡住时到 <c>WriteTimeout</c> 抛 <see cref="TimeoutException" />。</summary>
    /// <param name="buffer">缓冲区。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="count">写多少。</param>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>DTR 线电平。</summary>
    bool DtrEnable { get; set; }

    /// <summary>RTS 线电平。<see cref="CanControlRts" /> 为 false 时读到的是驱动的值,写会被忽略。</summary>
    bool RtsEnable { get; set; }

    /// <summary>
    /// RTS 是否归我们控制。流控制取 RTS/CTS 时它归驱动 ——
    /// 此时 <see cref="SerialPort.RtsEnable" /> 的 setter 会抛
    /// <see cref="InvalidOperationException" />,不是"设了没生效"而是直接炸。
    /// </summary>
    bool CanControlRts { get; }

    /// <summary>置/撤 Break 信号(线上持续的空号)。</summary>
    /// <param name="on">是否置位。</param>
    void SetBreak(bool on);
}
