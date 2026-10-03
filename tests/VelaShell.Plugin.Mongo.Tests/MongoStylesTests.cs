using System.Text.RegularExpressions;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 版式与图标表脱离连接单独装载(与 Redis 插件的 RedisPanelStylesTests 同一个用意):
/// 样式写坏、某个图标路径解析不了,这类问题不该等到真机打开面板才炸 ——
/// 图标是延迟构建的资源,一条坏路径会在**第一次用到它的那个按钮**渲染时抛,整个面板随之打不开。
/// </summary>
[TestClass]
public sealed partial class MongoStylesTests
{
    private static string ThemeFile => Path.Combine(RepoRoot(), "plugins", "VelaShell.Plugin.Mongo", "Ui", "MongoTheme.axaml");

    [TestMethod]
    public void Every_icon_geometry_parses() => Screens.OnUi(() =>
    {
        string xaml = File.ReadAllText(ThemeFile);
        MatchCollection icons = IconPattern().Matches(xaml);
        Assert.IsTrue(icons.Count > 100, "the icon table should be there");
        var failures = new List<string>();
        foreach (Match icon in icons)
        {
            try
            {
                _ = StreamGeometry.Parse(icon.Groups["path"].Value);
            }
            catch (Exception ex)
            {
                failures.Add($"{icon.Groups["key"].Value}: {ex.Message}");
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Styles_load_without_host_tokens() => Screens.OnUi(() =>
    {
        // 令牌缺席(未命中的 DynamicResource 保持默认值)时样式表照样能装载。
        var styles = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://VelaShell.Plugin.Mongo/Ui/MongoStyles.axaml"));
        Assert.IsTrue(styles.Count > 10);
        Assert.IsTrue(styles.TryGetResource("MongoPillButton", null, out object? pill) && pill is ControlTheme);
        Assert.IsTrue(styles.TryGetResource("Mongo.leaf", null, out object? leaf) && leaf is Geometry);
        return Task.CompletedTask;
    });

    internal static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        // 从本源文件所在目录往上找,而不是从输出目录:构建产物可以落在仓库之外(--artifacts-path)。
        string? dir = Path.GetDirectoryName(source);
        while (dir is not null && !File.Exists(Path.Combine(dir, "VelaShell.Plugins.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("<StreamGeometry x:Key=\"(?<key>[^\"]+)\">(?<path>[^<]+)</StreamGeometry>")]
    private static partial Regex IconPattern();
}
