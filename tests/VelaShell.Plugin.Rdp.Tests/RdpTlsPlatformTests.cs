using System.Linq;
using System.Net.Security;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpTlsPlatformTests
{
    [TestMethod]
    public void MacOsAvoidsStaticRsaGcmTlsSuites()
    {
        SslClientAuthenticationOptions options = RdpTls.CreateClientAuthenticationOptions("rdp.example", pinned: false);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.IsNull(options.CipherSuitesPolicy, "Only macOS needs the RDP cipher compatibility policy.");
            return;
        }

        Assert.IsNotNull(options.CipherSuitesPolicy);
        TlsCipherSuite[] allowed = options.CipherSuitesPolicy!.AllowedCipherSuites.ToArray();
        CollectionAssert.Contains(allowed, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384);
        CollectionAssert.DoesNotContain(allowed, TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384);
    }
}
