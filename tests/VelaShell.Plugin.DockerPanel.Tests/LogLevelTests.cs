using VelaShell.Plugin.DockerPanel.Ui;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 日志级别识别。
/// <para>
/// 这块的风险不是"认不出来"(认不出来就不着色,没人受伤),而是**误伤** ——
/// 把一条成功日志染成红色,用户会去查一个不存在的故障。所以这里的重点是
/// 各种"正文里出现 error 字样但级别不是 error"的行必须保持中性。
/// </para>
/// </summary>
[TestClass]
public class LogLevelTests
{
    [TestMethod]
    public void ReadsBracketedLevelsLikeNginx()
    {
        Assert.AreEqual(LogLevel.Warn,
            LogLevels.Detect("[warn] 29#29: *18421 upstream server temporarily disabled"));
        Assert.AreEqual(LogLevel.Error,
            LogLevels.Detect("[error] 29#29: *18421 connect() failed (111: Connection refused)"));
    }

    [TestMethod]
    public void ReadsStructuredJsonLevels()
    {
        Assert.AreEqual(LogLevel.Error,
            LogLevels.Detect("""{"level":"error","msg":"upstream unavailable","target":"payments-svc:8443"}"""));
        Assert.AreEqual(LogLevel.Info,
            LogLevels.Detect("""{"level":"info","msg":"order confirmed","order":"ord_8812"}"""));
        // 有些库用 lvl / severity。
        Assert.AreEqual(LogLevel.Warn, LogLevels.Detect("""{"lvl":"warn","msg":"pool near limit"}"""));
        Assert.AreEqual(LogLevel.Debug, LogLevels.Detect("""{"severity":"DEBUG","msg":"cache hit"}"""));
    }

    [TestMethod]
    public void ReadsPlainLeadingLevels()
    {
        Assert.AreEqual(LogLevel.Error, LogLevels.Detect("ERROR 2026-08-21 09:41:14 something broke"));
        Assert.AreEqual(LogLevel.Info, LogLevels.Detect("INFO  scheduled job started"));
        Assert.AreEqual(LogLevel.Debug, LogLevels.Detect("TRACE entering handler"));
        Assert.AreEqual(LogLevel.Error, LogLevels.Detect("FATAL cannot bind port"));
    }

    [TestMethod]
    public void DoesNotDyeASuccessLineRedJustBecauseItMentionsErrors()
    {
        // 这是这一层存在的全部理由:一条 200 的访问日志里出现 "error" 这个词,
        // 把它染成红色会让人去查一个不存在的故障。
        Assert.AreEqual(LogLevel.None,
            LogLevels.Detect("""172.20.0.1 - - "GET /api/v1/errors?page=2 HTTP/1.1" 200 4821 rt=0.043"""));
        Assert.AreEqual(LogLevel.None,
            LogLevels.Detect("""{"msg":"error budget remaining","pct":99.4,"window":"30d"}"""));
    }

    [TestMethod]
    public void OnlyLooksAtTheHeadOfTheLine()
    {
        // 一行很长的正文,末尾才出现 ERROR —— 那多半是被引用的内容,不是这一行的级别。
        string line = new string('x', 200) + " ERROR";

        Assert.AreEqual(LogLevel.None, LogLevels.Detect(line));
    }

    [TestMethod]
    public void UnknownAndEmptyStayNeutral()
    {
        Assert.AreEqual(LogLevel.None, LogLevels.Detect(""));
        Assert.AreEqual(LogLevel.None, LogLevels.Detect("Listening on 0.0.0.0:8080"));
        Assert.AreEqual("", LogLevels.Label(LogLevel.None));
    }

    [TestMethod]
    public void LabelsAndTonesLineUp()
    {
        Assert.AreEqual("ERROR", LogLevels.Label(LogLevel.Error));
        Assert.AreEqual(RowTone.Danger, LogLevels.Tone(LogLevel.Error));
        Assert.AreEqual(RowTone.Warn, LogLevels.Tone(LogLevel.Warn));
        // DEBUG 走"闲置"那一档:它比正文更不重要,不该抢注意力。
        Assert.AreEqual(RowTone.Idle, LogLevels.Tone(LogLevel.Debug));
    }
}
