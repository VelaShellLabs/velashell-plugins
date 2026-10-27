using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(VelaShell.Plugin.Rdp.Tests.RdpHeadlessApp))]

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>Uses the exact SDK-shared Avalonia runtime; Vela theme tokens may be absent.</summary>
public sealed class RdpHeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RdpHeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
