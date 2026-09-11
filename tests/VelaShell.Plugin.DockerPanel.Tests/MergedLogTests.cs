using VelaShell.Plugin.DockerPanel.Ui;
using VelaShell.Plugin.DockerPanel.Ui.Pages;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 合并日志的分行。
/// <para>
/// 这里盯的是"别把日志本身切坏"。合并日志的服务名着色是个锦上添花的东西,
/// 而切错前缀会让正文少掉一截 —— 用户看到的日志与远端真实吐出来的不一样,
/// 那比没有颜色严重得多。
/// </para>
/// </summary>
[TestClass]
public sealed class MergedLogTests
{
    private static bool Known(string name) => name is "web" or "easilynet-mongo-1";

    [TestMethod]
    public void Split_TakesTheComposePrefixAndTheSpaceAfterTheBar()
    {
        (var source, var body) = MergedLog.Split("web    | listening on :8080", Known);

        Assert.AreEqual("web", source);
        Assert.AreEqual("listening on :8080", body);
    }

    [TestMethod]
    public void Split_KeepsBarsInsideTheBody()
    {
        // 只切第一根竖线 —— 正文里的竖线是正文的一部分。
        (var source, var body) = MergedLog.Split("web | a | b | c", Known);

        Assert.AreEqual("web", source);
        Assert.AreEqual("a | b | c", body);
    }

    [TestMethod]
    public void Split_LeavesTimestampedFormatsAlone()
    {
        // 这是一行**没有** compose 前缀、自己带竖线的日志。把 2026-08-23 当成服务名的话,
        // 用户看到的正文就少了日期,而且会多出一个莫名其妙的"服务"。
        (var source, var body) = MergedLog.Split("2026-08-23 | INFO | ok", Known);

        Assert.AreEqual("", source);
        Assert.AreEqual("2026-08-23 | INFO | ok", body);
    }

    [TestMethod]
    public void Split_LeavesComposesOwnChatterAlone()
    {
        (var source, var body) = MergedLog.Split("Attaching to web, db", Known);

        Assert.AreEqual("", source);
        Assert.AreEqual("Attaching to web, db", body);
    }

    [TestMethod]
    public void Split_AcceptsContainerNamesToo()
    {
        // compose 的前缀有时是服务名、有时是容器名,取决于版本与 container_name。
        (var source, var body) = MergedLog.Split("easilynet-mongo-1  | waiting for connections", Known);

        Assert.AreEqual("easilynet-mongo-1", source);
        Assert.AreEqual("waiting for connections", body);
    }

    [TestMethod]
    public void OutputLine_ReadsTheLevelFromTheBodyAndCallsStderrAnError()
    {
        Assert.AreEqual(LogLevel.Warn, new OutputLine("00:00", "WARN slow query", false, false).Level);
        Assert.AreEqual(LogLevel.None, new OutputLine("00:00", "starting up", false, false).Level);
        // 正文里没有 ERROR 字样,但它走的是 stderr —— 那件事本身就是信息。
        Assert.AreEqual(RowTone.Danger, new OutputLine("00:00", "starting up", true, false).Tone);
    }
}
