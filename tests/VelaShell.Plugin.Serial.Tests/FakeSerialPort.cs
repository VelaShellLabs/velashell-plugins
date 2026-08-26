using System.Collections.Concurrent;

namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// <see cref="ISerialPortHandle" /> 的替身。
/// <para>
/// 串口是本仓库里唯一没法在 CI 上真跑的传输 —— Telnet 有环回 <c>TcpListener</c>,
/// S3 有环回 HTTP 服务器,串口要么真插一根线,要么 <c>socat</c> / <c>com0com</c>。
/// 所以真正会出错的地方(读循环、EOF 归一化、写序列化、控制线动作)全靠这个替身钉住。
/// </para>
/// <para>
/// 它刻意复刻真实实现的两条关键行为:**无数据时抛 <see cref="TimeoutException" />**
/// (读循环靠它周期性回头看是否该收摊),以及**拔线时抛 <see cref="IOException" />**。
/// 替身在这两点上放水,单测就测不到会话最重要的那条路径。
/// </para>
/// </summary>
internal sealed class FakeSerialPort : ISerialPortHandle
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private readonly List<byte> _written = [];
    private readonly Lock _gate = new();
    private Exception? _failure;

    /// <inheritdoc />
    public string PortName { get; init; } = "COM-TEST";

    /// <inheritdoc />
    public bool CanControlRts { get; init; } = true;

    /// <inheritdoc />
    public bool DtrEnable { get; set; }

    /// <inheritdoc />
    public bool RtsEnable { get; set; }

    /// <summary>端口是否已被关掉(读线程退出时应当自己关)。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Break 的置位/撤位序列。</summary>
    public List<bool> BreakStates { get; } = [];

    /// <summary>已写上线的字节(按顺序拼起来)。</summary>
    public byte[] Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    /// <summary>喂一块"设备发来的"数据。</summary>
    /// <param name="data">数据。</param>
    public void Feed(params byte[] data) => _incoming.Add(data);

    /// <summary>让下一次读抛出指定异常(模拟拔线/断电/驱动卸载)。</summary>
    /// <param name="failure">异常。</param>
    public void Fail(Exception failure)
    {
        _failure = failure;
        _incoming.Add([]); // 唤醒可能正卡在等待里的读。
    }

    /// <inheritdoc />
    public int Read(byte[] buffer, int offset, int count)
    {
        if (_failure is { } failure)
        {
            throw failure;
        }
        if (!_incoming.TryTake(out byte[]? chunk, TimeSpan.FromMilliseconds(20)))
        {
            throw new TimeoutException();
        }
        if (_failure is { } late)
        {
            throw late;
        }
        int taken = Math.Min(count, chunk.Length);
        Array.Copy(chunk, 0, buffer, offset, taken);
        return taken;
    }

    /// <inheritdoc />
    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _written.AddRange(buffer.AsSpan(offset, count));
        }
    }

    /// <inheritdoc />
    public void SetBreak(bool on) => BreakStates.Add(on);

    /// <inheritdoc />
    public void Dispose() => IsDisposed = true;
}
