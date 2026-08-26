namespace VelaShell.Plugin.Serial;

/// <summary>
/// 串口的"行规程":换行改写与本地回显。全部是纯字节变换,不碰硬件 ——
/// 于是这一层可以脱离真实串口单测,而这恰恰是串口最难验证的部分
/// (没有 telnetd 那样的环回可用,调研文档 §五.4 记的就是这条)。
/// <para>
/// 为什么串口需要这层而 SSH / Telnet 不需要:那两者的另一端是 PTY 或有协议契约的对端,
/// 换行语义是谈好的;串口没有任何协议层,线上就是一串字节 —— 大量嵌入式设备只发裸 CR
/// (于是每行盖在上一行上),另一些只发裸 LF(于是输出成阶梯状)。
/// PuTTY / Tera Term / minicom 无一例外都提供这两个开关,原因就在这里。
/// </para>
/// </summary>
internal sealed class SerialLineDiscipline(SerialConfig config)
{
    private const byte Cr = 0x0D;
    private const byte Lf = 0x0A;

    /// <summary>上一个**输入**字节是不是 CR(跨块保持:CRLF 会被读取切在中间)。</summary>
    private bool _prevWasCr;

    /// <summary>
    /// 上一块的末尾是个悬空的 CR,我们已经替它补过 LF 了;因此本块若以 LF 开头,那个 LF 要吞掉。
    /// <para>
    /// 为什么不干脆把悬空的 CR 扣下来等下一块:设备发完 <c>"hello\r"</c> 就不说话了是常态
    /// (提示符、进度行),扣住等于那一行永远不出现。宁可先补、再按需吞掉一个 LF。
    /// </para>
    /// </summary>
    private bool _swallowLeadingLf;

    /// <summary>
    /// 入方向:按 <see cref="SerialConfig.ImplicitLf" /> / <see cref="SerialConfig.ImplicitCr" />
    /// 补齐换行。两项都关时**原样返回同一段内存**(零拷贝,这是默认路径)。
    /// </summary>
    /// <param name="data">刚从串口读到的字节。</param>
    /// <returns>喂给宿主终端的字节。</returns>
    public ReadOnlyMemory<byte> Receive(ReadOnlyMemory<byte> data)
    {
        if (!config.ImplicitLf && !config.ImplicitCr)
        {
            return data;
        }
        ReadOnlySpan<byte> input = data.Span;
        // 最坏情况每个字节都要补一个,再加上末尾可能补的那一个。
        var output = new List<byte>(input.Length * 2 + 1);
        int start = 0;
        if (_swallowLeadingLf)
        {
            _swallowLeadingLf = false;
            if (input.Length > 0 && input[0] == Lf)
            {
                start = 1;
            }
        }
        for (int i = start; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == Lf)
            {
                // 裸 LF(前面不是 CR)会让 VT 引擎只下移一行而不回到行首 —— 就是"阶梯"。
                if (config.ImplicitCr && !_prevWasCr)
                {
                    output.Add(Cr);
                }
                output.Add(Lf);
            }
            else
            {
                // 上一个 CR 后面跟的不是 LF:那是一次"回到行首"而非"换行",
                // 开了 ImplicitLf 就替它换行。注意 b 自己也可能是 CR(连续 CR),
                // 所以这个判断必须在写出 b **之前**做。
                if (config.ImplicitLf && _prevWasCr)
                {
                    output.Add(Lf);
                }
                output.Add(b);
            }
            _prevWasCr = b == Cr;
        }
        // 本块以 CR 收尾:先补上 LF,并记下"下一块若以 LF 开头就吞掉它"。
        if (config.ImplicitLf && _prevWasCr)
        {
            output.Add(Lf);
            _swallowLeadingLf = true;
            // 这个 CR 已经**结清**了 —— 不清掉标记的话,下一块第一个非 LF 字节前会再补一个 LF,
            // 于是每一个跨块的裸 CR 都多出一个空行。
            _prevWasCr = false;
        }
        return output.ToArray();
    }

    /// <summary>
    /// 出方向:按 <see cref="SerialConfig.EnterMode" /> 改写回车。
    /// <see cref="SerialEnterMode.Cr" />(默认)原样返回同一段内存。
    /// <para>
    /// <b>作用域警告</b>:传输层看到的不只是"用户按的回车",还有粘贴的内容与 ZMODEM 帧,
    /// 而 <c>IProtocolTerminalSession.WriteAsync</c> 只有一个入口、分不出来。
    /// 因此改写一旦打开就是对**整条出方向流**生效的 —— 传文件前请把它调回
    /// <see cref="SerialEnterMode.Cr" />。默认不改写正是为了让这个代价永远不会
    /// 在用户没要求的时候发生(与 PuTTY 串口默认发 CR 一致)。
    /// </para>
    /// <para>
    /// 粘贴的 CRLF 文本不会被打成 <c>CR LF LF</c>:CR 后面已经跟着 LF 时不再补。
    /// 这个判断按**本次写入**的缓冲区做,不跨调用 —— 一次粘贴是一次写入,
    /// 要跨调用切开 CRLF 得宿主正好在这两个字节之间断开,不会发生。
    /// </para>
    /// </summary>
    /// <param name="data">用户输入的字节。</param>
    /// <returns>真正写上线的字节。</returns>
    public ReadOnlyMemory<byte> Transmit(ReadOnlyMemory<byte> data)
    {
        if (config.EnterMode == SerialEnterMode.Cr)
        {
            return data;
        }
        ReadOnlySpan<byte> input = data.Span;
        var output = new List<byte>(input.Length + 8);
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b != Cr)
            {
                output.Add(b);
                continue;
            }
            bool followedByLf = i + 1 < input.Length && input[i + 1] == Lf;
            switch (config.EnterMode)
            {
                case SerialEnterMode.Lf when followedByLf:
                    break; // CRLF → LF:扔掉 CR,下一轮把 LF 原样写出。
                case SerialEnterMode.Lf:
                    output.Add(Lf);
                    break;
                case SerialEnterMode.CrLf when followedByLf:
                    output.Add(Cr); // 已经是 CRLF,别补成 CR LF LF。
                    break;
                case SerialEnterMode.CrLf:
                    output.Add(Cr);
                    output.Add(Lf);
                    break;
                default:
                    output.Add(Cr);
                    break;
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// 本地回显要往屏幕上打的字节。
    /// <para>
    /// 取的是**改写之后**要上线的那一份,并且把裸 CR 展开成 CR LF ——
    /// 屏幕上的"回车"必须同时回到行首并下移一行,只回不移的话用户看到的是
    /// 自己敲的下一行盖在上一行上。这一步只影响显示,不影响线上字节。
    /// </para>
    /// </summary>
    /// <param name="transmitted">已按 <see cref="Transmit" /> 改写过的字节。</param>
    /// <returns>喂回终端的字节;无需回显时为空。</returns>
    public static ReadOnlyMemory<byte> BuildEcho(ReadOnlyMemory<byte> transmitted)
    {
        ReadOnlySpan<byte> input = transmitted.Span;
        var output = new List<byte>(input.Length + 4);
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            output.Add(b);
            if (b == Cr && (i + 1 >= input.Length || input[i + 1] != Lf))
            {
                output.Add(Lf);
            }
        }
        return output.ToArray();
    }
}
