namespace VelaShell.Plugin.Serial;

/// <summary>
/// 活动串口会话表。命令面板里的 Break / DTR / RTS 需要一个目标,而 SDK 的终端协议
/// **不带会话标识** —— 会话的生命周期由终端标签持有,插件这边只在建立时见过它一面。
/// <para>
/// 于是"当前是哪条串口"只能由插件自己推断,判据是<b>最近一次收到用户输入的那条</b>:
/// 用户要按 Break,总是先在那个标签页里敲过东西(哪怕只是回车看提示符)。
/// 这比"最后打开的那条"准得多 —— 开着两条串口时,后开的未必是正在看的那条。
/// </para>
/// <para>
/// 为什么不做得更准:宿主目前没有把"当前聚焦的终端标签"这件事开放给插件,
/// 而为一个命令去扩一条焦点通知的 SDK 面,代价与收益不成比例。
/// 每条命令都会把实际作用的端口名写进插件日志,真搞错了也查得出来。
/// </para>
/// </summary>
internal sealed class SerialSessionRegistry
{
    private readonly Lock _gate = new();
    private readonly List<SerialSession> _sessions = [];
    private SerialSession? _lastActive;

    /// <summary>登记一条新会话,并订阅它的输入事件。</summary>
    /// <param name="session">会话。</param>
    public void Add(SerialSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Activity += Touch;
        lock (_gate)
        {
            _sessions.Add(session);
            _lastActive ??= session;
        }
    }

    /// <summary>把某条会话标为"当前"(它刚收到用户输入)。</summary>
    /// <param name="session">会话。</param>
    public void Touch(SerialSession session)
    {
        lock (_gate)
        {
            _lastActive = session;
        }
    }

    /// <summary>
    /// 取命令应当作用的会话。
    /// <para>
    /// 顺带清掉已经关掉的:没有"会话结束"的回调可订阅(<c>DisposeAsync</c> 是宿主调的,
    /// 而它不通知我们),所以只能在每次取用时按 <see cref="SerialSession.IsOpen" /> 扫一遍。
    /// 会话数以个位计,这点开销无所谓。
    /// </para>
    /// </summary>
    /// <returns>当前会话;一条都没开时为 <see langword="null" />。</returns>
    public SerialSession? Current()
    {
        lock (_gate)
        {
            _sessions.RemoveAll(session => !session.IsOpen);
            if (_lastActive is { IsOpen: true } active && _sessions.Contains(active))
            {
                return active;
            }
            _lastActive = _sessions.Count > 0 ? _sessions[^1] : null;
            return _lastActive;
        }
    }
}
