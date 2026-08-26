using System.IO.Ports;
using System.Text;

namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// 行规程:串口最容易出错、也最容易被"看着好像对了"糊弄过去的一层。
/// 期望值一律按字节手写,不是"跑一遍看输出写什么"。
/// </summary>
[TestClass]
public sealed class SerialLineDisciplineTests
{
    private const byte Cr = 0x0D;
    private const byte Lf = 0x0A;

    private static SerialConfig Config(
        bool implicitLf = false,
        bool implicitCr = false,
        SerialEnterMode enterMode = SerialEnterMode.Cr) =>
        new("COM1", 115200, 8, StopBits.One, Parity.None, Handshake.None,
            Dtr: true, Rts: true, enterMode, implicitLf, implicitCr, LocalEcho: false,
            TxDelayPerByte: TimeSpan.Zero, TxDelayPerLine: TimeSpan.Zero);

    private static byte[] Receive(SerialLineDiscipline discipline, params byte[] data) =>
        discipline.Receive(data).ToArray();

    // ── 入方向 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Receive_WithBothSwitchesOff_PassesBytesThrough()
    {
        var discipline = new SerialLineDiscipline(Config());

        byte[] result = Receive(discipline, (byte)'a', Cr, (byte)'b', Lf);

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Cr, (byte)'b', Lf }, result);
    }

    [TestMethod]
    public void Receive_ImplicitLf_TerminatesABareCr()
    {
        // 症状:设备只发 CR,于是每一行都盖在上一行上。这是串口最常见的一条抱怨。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        byte[] result = Receive(discipline, (byte)'a', Cr, (byte)'b');

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Cr, Lf, (byte)'b' }, result);
    }

    [TestMethod]
    public void Receive_ImplicitLf_LeavesAProperCrLfAlone()
    {
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        byte[] result = Receive(discipline, (byte)'a', Cr, Lf, (byte)'b');

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Cr, Lf, (byte)'b' }, result);
    }

    [TestMethod]
    public void Receive_ImplicitLf_HandlesConsecutiveCrs()
    {
        // CR CR 是"回行首两次"。开了补 LF 就该是两次换行 —— 漏掉第一次是很容易犯的循环写法错误。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        byte[] result = Receive(discipline, Cr, Cr, (byte)'x');

        CollectionAssert.AreEqual(new byte[] { Cr, Lf, Cr, Lf, (byte)'x' }, result);
    }

    [TestMethod]
    public void Receive_ImplicitLf_TerminatesACrThatEndsTheChunk()
    {
        // 设备发完 "hello\r" 就不说话了(提示符、进度行)是常态。
        // 把这个 CR 扣下来等下一块,那一行就永远不出现。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        byte[] result = Receive(discipline, (byte)'h', Cr);

        CollectionAssert.AreEqual(new byte[] { (byte)'h', Cr, Lf }, result);
    }

    [TestMethod]
    public void Receive_ImplicitLf_DoesNotDoubleWhenCrLfIsSplitAcrossChunks()
    {
        // 上一条的代价:先补了 LF,那么紧接着到货的那个 LF 必须吞掉,
        // 否则一次 CRLF 被读取切开就变成了空行。**跨块状态**正是这层最容易漏的地方。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        byte[] first = Receive(discipline, (byte)'h', Cr);
        byte[] second = Receive(discipline, Lf, (byte)'i');

        CollectionAssert.AreEqual(new byte[] { (byte)'h', Cr, Lf }, first);
        CollectionAssert.AreEqual(new byte[] { (byte)'i' }, second);
    }

    [TestMethod]
    public void Receive_ImplicitLf_SwallowsOnlyTheImmediateLf()
    {
        // 吞掉的必须只有紧挨着的那一个;第二块开头是别的字节时,一个字节都不能少。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));

        Receive(discipline, Cr);
        byte[] second = Receive(discipline, (byte)'x', Lf);

        CollectionAssert.AreEqual(new byte[] { (byte)'x', Lf }, second,
            "上一块末尾那个 CR 已经结清,第二块开头不该再补 LF;而这里的裸 LF 也不该被吞");
    }

    [TestMethod]
    public void Receive_ImplicitCr_FixesTheStaircase()
    {
        // 症状:设备只发 LF,VT 引擎只下移不回行首,输出一路往右下走成阶梯。
        var discipline = new SerialLineDiscipline(Config(implicitCr: true));

        byte[] result = Receive(discipline, (byte)'a', Lf, (byte)'b');

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Cr, Lf, (byte)'b' }, result);
    }

    [TestMethod]
    public void Receive_ImplicitCr_LeavesAProperCrLfAlone()
    {
        var discipline = new SerialLineDiscipline(Config(implicitCr: true));

        byte[] result = Receive(discipline, Cr, Lf);

        CollectionAssert.AreEqual(new byte[] { Cr, Lf }, result);
    }

    [TestMethod]
    public void Receive_ImplicitCr_LeavesACrLfSplitAcrossChunksAlone()
    {
        var discipline = new SerialLineDiscipline(Config(implicitCr: true));

        Receive(discipline, (byte)'a', Cr);
        byte[] second = Receive(discipline, Lf, (byte)'b');

        CollectionAssert.AreEqual(new byte[] { Lf, (byte)'b' }, second,
            "上一块以 CR 收尾,这个 LF 就是它的另一半,不该再补一个 CR");
    }

    // ── 出方向 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Transmit_Cr_IsTheIdentity()
    {
        // 默认路径。传输层不碰字节 —— 粘贴内容与 ZMODEM 帧因此天然安全。
        var discipline = new SerialLineDiscipline(Config());
        byte[] payload = [0x00, 0xFF, Cr, Lf, 0x18];

        CollectionAssert.AreEqual(payload, discipline.Transmit(payload).ToArray());
    }

    [TestMethod]
    public void Transmit_CrLf_ExpandsABareCr()
    {
        var discipline = new SerialLineDiscipline(Config(enterMode: SerialEnterMode.CrLf));

        byte[] result = discipline.Transmit(new byte[] { (byte)'l', (byte)'s', Cr }).ToArray();

        CollectionAssert.AreEqual(new byte[] { (byte)'l', (byte)'s', Cr, Lf }, result);
    }

    [TestMethod]
    public void Transmit_CrLf_DoesNotDoubleAnExistingCrLf()
    {
        // 粘贴一段 Windows 换行的文本时,不做这条判断就会把每一行都打成 CR LF LF。
        var discipline = new SerialLineDiscipline(Config(enterMode: SerialEnterMode.CrLf));

        byte[] result = discipline.Transmit(new byte[] { (byte)'a', Cr, Lf, (byte)'b' }).ToArray();

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Cr, Lf, (byte)'b' }, result);
    }

    [TestMethod]
    public void Transmit_Lf_ReplacesCrAndCollapsesCrLf()
    {
        var discipline = new SerialLineDiscipline(Config(enterMode: SerialEnterMode.Lf));

        CollectionAssert.AreEqual(new byte[] { (byte)'a', Lf },
            discipline.Transmit(new byte[] { (byte)'a', Cr }).ToArray());
        CollectionAssert.AreEqual(new byte[] { (byte)'a', Lf },
            discipline.Transmit(new byte[] { (byte)'a', Cr, Lf }).ToArray());
    }

    // ── 本地回显 ────────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildEcho_TurnsABareCrIntoCrLfSoTheCursorAdvances()
    {
        // 屏幕上的"回车"必须同时回行首并下移;只回不移的话用户敲的下一行会盖住上一行。
        byte[] echo = SerialLineDiscipline.BuildEcho(new byte[] { (byte)'h', (byte)'i', Cr }).ToArray();

        CollectionAssert.AreEqual(new byte[] { (byte)'h', (byte)'i', Cr, Lf }, echo);
    }

    [TestMethod]
    public void BuildEcho_LeavesAnExistingCrLfAlone()
    {
        byte[] echo = SerialLineDiscipline.BuildEcho(new byte[] { Cr, Lf }).ToArray();

        CollectionAssert.AreEqual(new byte[] { Cr, Lf }, echo);
    }

    [TestMethod]
    public void Receive_IsByteTransparent()
    {
        // 8 位透明:0x00 与 0xFF 必须原样过去。ZMODEM 与固件烧写全靠这一条,
        // 而破坏它的表现是"平时都好、传文件时随机损坏"。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true, implicitCr: true));
        byte[] payload = [.. Enumerable.Range(0, 256).Select(i => (byte)i).Where(b => b is not (Cr or Lf))];

        byte[] result = Receive(discipline, payload);

        CollectionAssert.AreEqual(payload, result);
    }

    [TestMethod]
    public void Receive_HandlesUtf8SplitAcrossChunks()
    {
        // 行规程只认 CR/LF 两个字节,多字节字符被切开也不该受影响 ——
        // 解码是宿主 VT 引擎的事,这一层碰它就是越界。
        var discipline = new SerialLineDiscipline(Config(implicitLf: true));
        byte[] utf8 = Encoding.UTF8.GetBytes("中文");

        byte[] first = Receive(discipline, utf8[..3]);
        byte[] second = Receive(discipline, utf8[3..]);

        CollectionAssert.AreEqual(utf8, first.Concat(second).ToArray());
    }
}
