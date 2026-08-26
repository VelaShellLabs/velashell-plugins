using System.Threading.Channels;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// 一条已打开的串口会话。对宿主而言它只是"字节双工 + 一个尺寸通知";
/// 内部则是三件事:一条**独占的读线程**、一把序列化写侧的锁、以及行规程。
/// <para>
/// 读为什么用独占线程而不是 <c>DataReceived</c> 事件:<c>SerialPort.DataReceived</c> 在
/// 115200 以上会丢字节(dotnet/runtime#106631,至今 open)。也不用
/// <c>BaseStream.ReadAsync</c>:它在 Windows 上不响应取消令牌(#30850),Unix 上响应 ——
/// 一个平台间行为不一致的地基,换来的只是省一条线程。
/// </para>
/// <para>
/// 一条会话一条线程在这里是合适的:用户同时开着的串口数以个位计
/// (不像 SSH 会话可能几十条),而换来的是**关闭路径上没有任何唤醒动作** ——
/// 读线程自己带着超时轮询,自己在退出时关端口。#20362(<c>Close()</c> 在硬件流控
/// 卡住时永久阻塞)与 #44952(关闭竞态 NRE)两条 issue 因此都够不着我们。
/// </para>
/// </summary>
internal sealed class SerialSession : IProtocolTerminalSession
{
    /// <summary>一次读取的上限。8KB 够 921600 波特下约 90ms 的量,足够合批而不至于增加可感延迟。</summary>
    private const int ReadChunkSize = 8192;

    /// <summary>Break 信号的持续时长。RFC 无规定,PuTTY / Tera Term 一贯是 200–300ms 量级。</summary>
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>DTR/RTS 复位脉冲的宽度。Arduino 自动复位电路与 ESP32 的 EN 脚都远快于此。</summary>
    private static readonly TimeSpan ResetPulseWidth = TimeSpan.FromMilliseconds(120);

    private readonly ISerialPortHandle _port;
    private readonly SerialConfig _config;
    private readonly SerialLineDiscipline _discipline;
    private readonly IPluginLogger _log;

    /// <summary>
    /// 读线程 → <see cref="ReadAsync" /> 的传送带。本地回显也往这里写,
    /// 因此 <c>SingleWriter</c> 必须是 false。不设上限:宿主的终端桥是一刻不停在读的,
    /// 而给上限就意味着满了以后要么丢字节、要么阻塞读线程把压力顶回驱动缓冲区(RXOVER)。
    /// </summary>
    private readonly Channel<byte[]> _inbound =
        Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = false });

    /// <summary>
    /// 写侧序列化。SDK 明写了写与 Resize 会被界面线程和后台任务并发调用;
    /// 对串口而言交织的后果是"两次粘贴的字节互相穿插",而且节流路径下窗口更大。
    /// Break / DTR / RTS 也走这把锁 —— 它们与写是同一根线上的动作。
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly TaskCompletionSource _readerFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>本次 <see cref="ReadAsync" /> 没吃完的那一块。</summary>
    private ReadOnlyMemory<byte> _pending;

    private volatile bool _closing;
    private int _disposed;

    private SerialSession(ISerialPortHandle port, SerialConfig config, IPluginLogger log)
    {
        _port = port;
        _config = config;
        _log = log;
        _discipline = new(config);
        var thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"vela-serial:{port.PortName}"
        };
        thread.Start();
    }

    /// <summary>端口名(命令面板里的动作要报给用户听)。</summary>
    public string PortName => _port.PortName;

    /// <summary>会话是否还活着。</summary>
    public bool IsOpen => !_closing && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// 打开一条串口会话。
    /// </summary>
    /// <param name="config">会话配置。</param>
    /// <param name="log">插件日志。</param>
    /// <param name="open">打开端口的工厂(单测注入替身)。</param>
    /// <returns>已建立的会话。</returns>
    public static SerialSession Connect(SerialConfig config, IPluginLogger log, Func<SerialConfig, ISerialPortHandle>? open = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        ISerialPortHandle port = (open ?? SystemSerialPortHandle.Open)(config);
        log.Info($"Serial {port.PortName} opened at {config.BaudRate} {config.DataBits}{Describe(config)} " +
                 $"(flow={config.Handshake}, dtr={config.Dtr}, rts={config.Rts})");
        return new(port, config, log);
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        while (_pending.IsEmpty)
        {
            try
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0; // 读线程收摊了 —— 掉线、拔线、正常关闭,一律归一成 EOF。
                }
            }
            catch (OperationCanceledException)
            {
                // 宿主撤销读循环时同样按 EOF 交待。抛出去只会在标签关闭路径上多一条噪声异常。
                return 0;
            }
            if (_inbound.Reader.TryRead(out byte[]? chunk))
            {
                _pending = chunk;
            }
        }
        int taken = Math.Min(buffer.Length, _pending.Length);
        _pending[..taken].CopyTo(buffer);
        _pending = _pending[taken..];
        return taken;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || !IsOpen)
        {
            return;
        }
        Activity?.Invoke(this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReadOnlyMemory<byte> payload = _discipline.Transmit(data);
            if (_config.LocalEcho)
            {
                // 先回显再上线:设备的响应总是晚于我们自己的键入,顺序天然正确。
                _inbound.Writer.TryWrite(SerialLineDiscipline.BuildEcho(payload).ToArray());
            }
            if (_config.IsPaced)
            {
                await WritePacedAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte[] bytes = payload.ToArray();
                await Task.Run(() => _port.Write(bytes, 0, bytes.Length), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 会话正在关。
        }
        catch (TimeoutException)
        {
            // 只有硬件流控会走到这里:对端一直没拉起 CTS。
            _log.Warn($"Serial {_port.PortName}: write timed out — the peer is holding flow control off (CTS). Bytes were dropped.");
        }
        catch (Exception ex)
        {
            // 写失败**不抛**:会话是否还活着由读循环的 EOF 说了算(与宿主其余传输一致)。
            // 在这里抛出去,用户看到的是一次按键弹出的异常,而不是标签页上的"已断开"。
            _log.Warn($"Serial {_port.PortName}: write failed — {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 尺寸变化:串口没有对应机制,空操作。
    /// <para>
    /// 顺带说明为什么不去发 <c>stty rows/cols</c> 之类:那是**带内**的,要求对端正好是个
    /// 认这套命令的 shell。往一条可能接着 PLC、示波器或 U-Boot 的线上乱发字节,
    /// 后果由用户承担。真需要的用户自己敲 <c>stty</c> 就行。
    /// </para>
    /// </summary>
    /// <param name="columns">列数。</param>
    /// <param name="rows">行数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>用户在这条会话上敲了东西 —— 命令面板据此认定"当前这条串口"。</summary>
    public event Action<SerialSession>? Activity;

    /// <summary>发送一个 Break 信号(线上持续空号)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否发出。</returns>
    public Task<bool> SendBreakAsync(CancellationToken cancellationToken = default) =>
        OnLineAsync(async () =>
        {
            _port.SetBreak(true);
            try
            {
                await Task.Delay(BreakDuration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _port.SetBreak(false);
            }
            _log.Info($"Serial {_port.PortName}: sent BREAK ({BreakDuration.TotalMilliseconds:F0}ms).");
        }, cancellationToken);

    /// <summary>给 DTR(或 RTS)打一个复位脉冲:翻转 → 等 → 翻回。</summary>
    /// <param name="line">要脉冲的控制线。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否发出。</returns>
    public Task<bool> PulseAsync(SerialControlLine line, CancellationToken cancellationToken = default) =>
        OnLineAsync(async () =>
        {
            if (line == SerialControlLine.Rts && !_port.CanControlRts)
            {
                _log.Warn($"Serial {_port.PortName}: RTS is driven by the RTS/CTS flow control, not by us.");
                return;
            }
            bool original = line == SerialControlLine.Dtr ? _port.DtrEnable : _port.RtsEnable;
            SetLine(line, !original);
            try
            {
                await Task.Delay(ResetPulseWidth, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetLine(line, original);
            }
            _log.Info($"Serial {_port.PortName}: pulsed {line}.");
        }, cancellationToken);

    /// <summary>翻转 DTR 或 RTS 并保持。</summary>
    /// <param name="line">要翻转的控制线。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否翻转成功。</returns>
    public Task<bool> ToggleAsync(SerialControlLine line, CancellationToken cancellationToken = default) =>
        OnLineAsync(() =>
        {
            if (line == SerialControlLine.Rts && !_port.CanControlRts)
            {
                _log.Warn($"Serial {_port.PortName}: RTS is driven by the RTS/CTS flow control, not by us.");
                return Task.CompletedTask;
            }
            bool next = !(line == SerialControlLine.Dtr ? _port.DtrEnable : _port.RtsEnable);
            SetLine(line, next);
            _log.Info($"Serial {_port.PortName}: {line} → {(next ? "asserted" : "cleared")}.");
            return Task.CompletedTask;
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _closing = true;
        try
        {
            // 读线程最多再等一个读超时就会看到 _closing,然后**自己**关端口。
            // 三秒是给"超时 250ms + 关端口"留的余量;真等不到也绝不把界面拖住 ——
            // 那正是 #20362 的表现形式(硬件流控卡住时 Close 永不返回)。
            await _readerFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn($"Serial {_port.PortName}: the reader thread did not stop within 3s; " +
                      "leaving it to the process (the port is likely stuck behind hardware flow control).");
        }
        _writeGate.Dispose();
    }

    /// <summary>
    /// 读循环。**唯一**碰读侧与端口生命周期的地方。
    /// </summary>
    private void ReadLoop()
    {
        byte[] buffer = new byte[ReadChunkSize];
        try
        {
            while (!_closing)
            {
                int read;
                try
                {
                    read = _port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    // 正常控制流:这一轮没数据,回头看一眼是不是该收摊。
                    continue;
                }
                if (read <= 0)
                {
                    continue;
                }
                ReadOnlyMemory<byte> processed = _discipline.Receive(new(buffer, 0, read));
                // 必须拷贝:buffer 下一轮还要用,而 Receive 在不改写时返回的正是它的切片。
                _inbound.Writer.TryWrite(processed.ToArray());
            }
        }
        catch (Exception ex)
        {
            // 拔掉 USB 转串口、设备断电、驱动卸载 —— 全是这里冒出来的 IOException /
            // UnauthorizedAccessException / ObjectDisposedException。**一律归一成 EOF**:
            // 抛给宿主只会得到一个红色异常框,而归一成 EOF 才能走到标签页那条
            // "已断开 + 可重连"的既有路径上。
            if (!_closing)
            {
                _log.Info($"Serial {_port.PortName} closed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                _port.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Serial {_port.PortName}: closing the port failed — {ex.Message}");
            }
            _inbound.Writer.TryComplete();
            _readerFinished.TrySetResult();
        }
    }

    /// <summary>
    /// 节流发送:逐字节写,字节之间等 <see cref="SerialConfig.TxDelayPerByte" />,
    /// 每行之后再等 <see cref="SerialConfig.TxDelayPerLine" />。
    /// <para>
    /// 这是 Tera Term「送信遅延」的等价物,存在的理由很具体:对着一台**没有流控**的老设备
    /// (Cisco/华为控制台、单片机 bootloader)粘一段配置进去,不节流就是丢字符 ——
    /// 而且丢在哪儿全看运气。两个延时默认都是 0,此时这条路径整个不进。
    /// </para>
    /// </summary>
    /// <param name="payload">要写的字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task WritePacedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        ReadOnlyMemory<byte> data = payload;
        for (int i = 0; i < data.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            one[0] = data.Span[i];
            // 第一次 await 之前先让出:WriteAsync 可能是在界面线程上被调用的,
            // 而下面的 Write 是阻塞的。
            if (_config.TxDelayPerByte > TimeSpan.Zero || i == 0)
            {
                await Task.Delay(_config.TxDelayPerByte, cancellationToken).ConfigureAwait(false);
            }
            _port.Write(one, 0, 1);
            if (_config.TxDelayPerLine > TimeSpan.Zero && one[0] is 0x0A or 0x0D)
            {
                await Task.Delay(_config.TxDelayPerLine, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void SetLine(SerialControlLine line, bool value)
    {
        if (line == SerialControlLine.Dtr)
        {
            _port.DtrEnable = value;
        }
        else
        {
            _port.RtsEnable = value;
        }
    }

    /// <summary>控制线动作的公共外壳:与写共用同一把锁,关了就静默不做,异常只记日志。</summary>
    private async Task<bool> OnLineAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return false;
        }
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Serial {_port.PortName}: control-line action failed — {ex.Message}");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>把校验/停止位拼成 <c>8N1</c> 那种一眼能认的写法(日志用)。</summary>
    private static string Describe(SerialConfig config)
    {
        char parity = config.Parity switch
        {
            System.IO.Ports.Parity.Even => 'E',
            System.IO.Ports.Parity.Odd => 'O',
            System.IO.Ports.Parity.Mark => 'M',
            System.IO.Ports.Parity.Space => 'S',
            _ => 'N'
        };
        string stop = config.StopBits switch
        {
            System.IO.Ports.StopBits.OnePointFive => "1.5",
            System.IO.Ports.StopBits.Two => "2",
            _ => "1"
        };
        return $"{parity}{stop}";
    }
}

/// <summary>可由用户直接操纵的串口控制线。</summary>
internal enum SerialControlLine
{
    /// <summary>数据终端就绪。开发板的复位脚常挂在它上面(Arduino 自动复位、ESP32 的 EN)。</summary>
    Dtr,

    /// <summary>请求发送。ESP32 用它配合 DTR 决定进不进下载模式;流控制取 RTS/CTS 时归驱动。</summary>
    Rts
}
