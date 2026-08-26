using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// 串口的 <see cref="IProtocolTerminal" /> 实现,兼 <see cref="IProtocolChoiceSource" /> ——
/// 后者是连接对话框里那个端口下拉的数据来源。
/// <para>
/// 两件事收在同一个类里不是图省事:候选项与"能不能连上"是同一份知识
/// (端口叫什么、在不在),分开就得让两处各自去猜本机有哪些串口。
/// </para>
/// </summary>
/// <param name="context">插件上下文(取日志)。</param>
/// <param name="sessions">活动会话表(命令面板里的 Break / DTR 要按它找目标)。</param>
/// <param name="open">打开端口的工厂;单测注入替身。</param>
internal sealed class SerialTerminal(
    IPluginContext context,
    SerialSessionRegistry sessions,
    Func<SerialConfig, ISerialPortHandle>? open = null) : IProtocolTerminal, IProtocolChoiceSource
{
    /// <inheritdoc />
    public Task<IProtocolTerminalSession> ConnectAsync(
        ProtocolConnectRequest request,
        ProtocolTerminalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loc = new Loc(context.Host.Locale);
        SerialConfig config = SerialConfig.Parse(request);
        if (config.PortName.Length == 0)
        {
            // 唯一一条"连都不用试"的判据。宿主对声明了 NoEndpoint 的协议不再校验主机非空,
            // 所以这道拦截必须由我们自己来 —— 否则 SerialPort 会抛一个
            // "PortName cannot be empty" 的英文 ArgumentException 给用户看。
            throw new ProtocolConnectionException(loc["Serial_ErrNoPort"]);
        }
        try
        {
            SerialSession session = SerialSession.Connect(config, context.Log, open);
            sessions.Add(session);
            return Task.FromResult<IProtocolTerminalSession>(session);
        }
        catch (Exception ex)
        {
            throw new ProtocolConnectionException(Explain(ex, config.PortName, loc), ex);
        }
    }

    /// <summary>
    /// 端口下拉的候选项。
    /// <para>
    /// 每次打开连接对话框、以及用户点刷新时各调一次 —— 这正是
    /// <see cref="ProtocolSettingKind.DynamicChoice" /> 存在的理由:USB 转串口是热插拔设备,
    /// 而插件的注册只发生一次。
    /// </para>
    /// </summary>
    /// <param name="fieldKey">字段键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>候选项。</returns>
    public Task<IReadOnlyList<ProtocolSettingChoice>> GetChoicesAsync(
        string fieldKey,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(fieldKey, ProtocolDescriptor.HostFieldKey, StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<ProtocolSettingChoice>>([]);
        }
        IReadOnlyList<SerialPortInfo> ports = SerialPortEnumerator.List();
        context.Log.Debug($"Serial: enumerated {ports.Count} port(s).");
        return Task.FromResult<IReadOnlyList<ProtocolSettingChoice>>(
            [.. ports.Select(port => new ProtocolSettingChoice(port.PortName, port.Label))]);
    }

    /// <summary>
    /// 把打开失败翻成用户能据以行动的话。
    /// <para>
    /// 这三条覆盖了现实中几乎全部的失败:被占用、没权限、拔掉了。
    /// 尤其是 Linux 的 dialout 组 —— 那是新用户第一次用串口时**必然**撞上的一堵墙,
    /// 而系统给的原文是一句干巴巴的 "Access to the port is denied"。
    /// </para>
    /// </summary>
    private static string Explain(Exception ex, string portName, Loc loc) => ex switch
    {
        UnauthorizedAccessException => string.Format(loc["Serial_ErrBusy"], portName),
        FileNotFoundException => string.Format(loc["Serial_ErrMissing"], portName),
        // Windows 上端口不存在给的是 IOException("系统找不到指定的文件"),
        // Linux 上给的是 FileNotFoundException —— 两条都要认。
        IOException io when io.Message.Contains("not exist", StringComparison.OrdinalIgnoreCase)
                            || io.Message.Contains("找不到", StringComparison.Ordinal)
            => string.Format(loc["Serial_ErrMissing"], portName),
        _ => string.Format(loc["Serial_ErrOpen"], portName, ex.Message)
    };
}
