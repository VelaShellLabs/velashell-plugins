using System.Text.Json;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpPackagingTests
{
    [TestMethod]
    public void ActualPluginPayloadHasSixDesktopNativeAssetsAndNoSharedDlls()
    {
        string payload = Path.Combine(AppContext.BaseDirectory, "alc-payload");
        Assert.IsTrue(Directory.Exists(payload));
        string[] expected =
        [
            "runtimes/win-x64/native/DevolutionsIronRdp.dll",
            "runtimes/win-arm64/native/DevolutionsIronRdp.dll",
            "runtimes/osx-x64/native/libDevolutionsIronRdp.dylib",
            "runtimes/osx-arm64/native/libDevolutionsIronRdp.dylib",
            "runtimes/linux-x64/native/libDevolutionsIronRdp.so",
            "runtimes/linux-arm64/native/libDevolutionsIronRdp.so"
        ];
        string[] actual = Directory.GetFiles(Path.Combine(payload, "runtimes"), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(payload, p).Replace('\\', '/')).ToArray();
        Assert.IsTrue(expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual), string.Join(", ", actual));
        foreach (string native in actual) Assert.IsGreaterThan(0L, new FileInfo(Path.Combine(payload, native)).Length);
        string[] dlls = Directory.GetFiles(payload, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().ToArray();
        Assert.DoesNotContain(name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("VelaShell.PluginSdk", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MicroCom.Runtime.dll", StringComparison.OrdinalIgnoreCase), dlls);
        Assert.IsTrue(File.Exists(Path.Combine(payload, "Devolutions.IronRdp.dll")));
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "VelaShell.Plugin.Rdp.deps.json")));
        Assert.IsTrue(deps.RootElement.GetProperty("libraries").TryGetProperty("Devolutions.IronRdp/2026.10.19", out _));
    }

    [TestMethod]
    public void UpstreamLicensesAndNoticeArePartOfTheActualOutput()
    {
        string licenses = Path.Combine(AppContext.BaseDirectory, "alc-payload", "Licenses");
        Assert.Contains("Copyright (c) 2023-2026 joesdu", File.ReadAllText(Path.Combine(licenses, "VelaShell-LICENSE")));
        Assert.Contains("Permission is hereby granted", File.ReadAllText(Path.Combine(licenses, "IronRDP-LICENSE-MIT")));
        Assert.Contains("Version 2.0, January 2004", File.ReadAllText(Path.Combine(licenses, "IronRDP-LICENSE-APACHE")));
        Assert.Contains("Copyright (c) 2013, Cisco Systems", File.ReadAllText(Path.Combine(licenses, "IronRDP-THIRD_PARTY_NOTICES")));
        Assert.Contains("1f69e124e6bf85608efce3495c12eaf5f6b5a95d", File.ReadAllText(Path.Combine(licenses, "IronRDP-SOURCES.txt")));
        // Presence verifies redistribution assets only, not runtime support or a legal audit.
    }
}
