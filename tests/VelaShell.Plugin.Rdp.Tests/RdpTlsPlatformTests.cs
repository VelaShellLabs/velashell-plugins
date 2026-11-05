namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpTlsPlatformTests
{
    [TestMethod]
    public void MacOsUsesNetworkFrameworkTlsBackend()
    {
        bool enabled = AppContext.TryGetSwitch("System.Net.Security.UseNetworkFramework", out bool value) && value;
        if (OperatingSystem.IsMacOS())
        {
            Assert.IsTrue(enabled, "RDP must select Network.framework before creating an SslStream on macOS.");
            return;
        }

        Assert.IsFalse(enabled, "The macOS-only Network.framework switch must remain disabled on Linux and Windows.");
    }
}
