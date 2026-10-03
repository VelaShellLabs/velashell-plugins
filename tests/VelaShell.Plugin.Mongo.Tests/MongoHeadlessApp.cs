using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using VelaShell.Plugin.Mongo.Tests;

[assembly: AvaloniaTestApplication(typeof(MongoHeadlessApp))]

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 面板 UI 测试共用的 headless 宿主。与 Redis 插件的测试宿主不同,这里**装了宿主的 VelaDark 令牌**
/// 并走 Skia 真渲染:MongoDB 工作台的界面要逐屏和设计稿比,没有令牌截出来是一张透明图。
/// "令牌缺席时照样能装载"那条由 <c>MongoStylesTests</c> 单独守。
/// </summary>
public class MongoHeadlessApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        foreach (string name in new[] { "VelaShellTokens", "VelaTokens", "DarkTheme" })
        {
            Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Mongo.Tests/"))
            {
                Source = new Uri($"avares://VelaShell.Plugin.Mongo.Tests/HostTheme/{name}.axaml")
            });
        }
    }

    /// <summary>headless 宿主的构建入口(由 Avalonia 的测试基建反射调用)。</summary>
    /// <returns>应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MongoHeadlessApp>()
                  .UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
