using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VelaShell.PluginSdk.Packaging;

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersion();

    [TestMethod]
    public void FinalVpxContainsTheVerifiedNativeExtension()
    {
        string? package = Environment.GetEnvironmentVariable("VELA_RDP_VERIFY_VPX");
        if (string.IsNullOrWhiteSpace(package))
        {
            Assert.Inconclusive("Set VELA_RDP_VERIFY_VPX to verify a final package after packing.");
            return;
        }
        Assert.IsTrue(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        string expected = Environment.GetEnvironmentVariable("VELA_RDP_EXPECTED_NATIVE")
            ?? throw new InvalidOperationException("VELA_RDP_EXPECTED_NATIVE must identify the freshly built extension.");
        using Stream payload = VpxContainer.OpenPayload(package);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        string[] nativePaths =
        [
            "runtimes/win-x64/native/DevolutionsIronRdp.dll", "runtimes/win-arm64/native/DevolutionsIronRdp.dll",
            "runtimes/linux-x64/native/libDevolutionsIronRdp.so", "runtimes/linux-arm64/native/libDevolutionsIronRdp.so",
            "runtimes/osx-x64/native/libDevolutionsIronRdp.dylib", "runtimes/osx-arm64/native/libDevolutionsIronRdp.dylib"
        ];
        Assert.IsTrue(nativePaths.ToHashSet(StringComparer.Ordinal).SetEquals(
            archive.Entries.Where(e => e.FullName.StartsWith("runtimes/", StringComparison.Ordinal) && e.Length > 0).Select(e => e.FullName)));
        foreach (var entry in archive.Entries)
        {
            string name = Path.GetFileName(entry.FullName);
            Assert.IsFalse(name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                || name.Equals("VelaShell.PluginSdk.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MicroCom.Runtime.dll", StringComparison.OrdinalIgnoreCase));
        }
        foreach (string license in new[] { "VelaShell-LICENSE", "IronRDP-LICENSE-MIT", "IronRDP-LICENSE-APACHE", "IronRDP-THIRD_PARTY_NOTICES", "IronRDP-SOURCES.txt" })
            Assert.IsNotNull(archive.GetEntry("Licenses/" + license));
        var native = archive.GetEntry(nativePaths[0]);
        Assert.IsNotNull(native);
        // Extract only the known library, never archive-controlled paths.
        string directory = Path.Combine(Path.GetTempPath(), "vela-rdp-vpx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string library = Path.Combine(directory, "DevolutionsIronRdp.dll");
        try
        {
            native.ExtractToFile(library);
            using var builtStream = File.OpenRead(expected);
            using var packedStream = File.OpenRead(library);
            CollectionAssert.AreEqual(SHA256.HashData(builtStream), SHA256.HashData(packedStream));
            IntPtr handle = NativeLibrary.Load(library);
            try
            {
                string[] exports = ["VelaClipboard_abi_version", "VelaClipboard_attach", "VelaClipboard_next", "VelaClipboard_send",
                    "VelaRdpdr_poll", "WindowsRdpdrConfig_new", "WindowsRdpdrConfig_destroy", "WindowsRdpdrConfig_add_drive",
                    "ClientConnector_with_static_channel_windows_rdpdr", "VelaClipboard_fault_isolation_version"];
                foreach (string export in exports) Assert.IsTrue(NativeLibrary.TryGetExport(handle, export, out _), export);
                var version = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(handle, exports[0]));
                Assert.AreEqual(1u, version());
                var isolation = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(handle, exports[^1]));
                Assert.AreEqual(1u, isolation());
            }
            finally { NativeLibrary.Free(handle); }
        }
        finally { Directory.Delete(directory, recursive: true); }
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
