using VelaShell.Plugin.DockerPanel.Ui;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 「从 .env 导入」。
/// <para>
/// dotenv 的方言各家不同(插值、多行、转义)。面板只认最朴素的那一档,
/// 认不出来的原样跳过并**把跳过的条数报出来** —— 猜错一次就等于悄悄改了用户的配置。
/// </para>
/// </summary>
[TestClass]
public class DotEnvImportTests
{
    private static PairListField Field() => new("环境变量");

    [TestMethod]
    public void ReadsPlainAssignments()
    {
        var field = Field();

        (var imported, var skipped) = field.ImportDotEnv("APP_MODE=prod\nPORT=8080");

        Assert.AreEqual(2, imported);
        Assert.AreEqual(0, skipped);
        Assert.AreEqual("APP_MODE", field.Rows[0].Key);
        Assert.AreEqual("prod", field.Rows[0].Value);
        Assert.AreEqual("8080", field.Rows[1].Value);
    }

    [TestMethod]
    public void SkipsBlanksAndComments()
    {
        var field = Field();

        (var imported, var skipped) = field.ImportDotEnv("# 注释\n\nA=1\n   \n# 又一条\nB=2");

        Assert.AreEqual(2, imported);
        // 空行与注释不算"跳过" —— 它们本来就不是配置。
        Assert.AreEqual(0, skipped);
    }

    [TestMethod]
    public void StripsSurroundingQuotes()
    {
        var field = Field();

        field.ImportDotEnv("""
            A="hello world"
            B='single'
            C=no-quotes
            """);

        Assert.AreEqual("hello world", field.Rows[0].Value);
        Assert.AreEqual("single", field.Rows[1].Value);
        Assert.AreEqual("no-quotes", field.Rows[2].Value);
    }

    [TestMethod]
    public void HandlesExportPrefix()
    {
        var field = Field();

        field.ImportDotEnv("export DATABASE_URL=postgres://app@db:5432/shop");

        Assert.AreEqual("DATABASE_URL", field.Rows[0].Key);
        Assert.AreEqual("postgres://app@db:5432/shop", field.Rows[0].Value);
    }

    [TestMethod]
    public void KeepsEqualsSignsInsideTheValue()
    {
        var field = Field();

        // base64 与连接串里满是 = ,只能按**第一个** = 切。
        field.ImportDotEnv("TOKEN=YWJjZA==");

        Assert.AreEqual("TOKEN", field.Rows[0].Key);
        Assert.AreEqual("YWJjZA==", field.Rows[0].Value);
    }

    [TestMethod]
    public void ReportsLinesItCouldNotUnderstand()
    {
        var field = Field();

        (var imported, var skipped) = field.ImportDotEnv("A=1\n这一行没有等号\n=没有键\nB=2");

        Assert.AreEqual(2, imported);
        // 报出来而不是静默丢掉:用户得知道有东西没进来。
        Assert.AreEqual(2, skipped);
    }

    [TestMethod]
    public void LaterAssignmentsWinJustLikeDotEnvItself()
    {
        var field = Field();

        field.ImportDotEnv("A=first\nA=second");

        Assert.HasCount(1, field.Rows);
        Assert.AreEqual("second", field.Rows[0].Value);
    }

    [TestMethod]
    public void DropsTheEmptyPlaceholderRow()
    {
        var field = Field();
        field.AddCommand.Execute(null);

        field.ImportDotEnv("A=1");

        // 导完之后留着一个空占位行只会让"等效命令"多一个空的 -e。
        Assert.HasCount(1, field.Rows);
        Assert.AreEqual("A", field.Rows[0].Key);
    }
}
