using System.ComponentModel;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>How connection failures from the VelaShell.Rdp library reach the user.</summary>
[TestClass]
public sealed class RdpConnectorTests
{
    [TestMethod]
    public void RejectedCredentialsBecomeAnAuthenticationFailureWithTheReason()
    {
        var loc = new Loc("zh-Hans");
        Exception translated = RdpConnector.Translate(new RdpAuthenticationException("rejected", 0xC000006D), loc);
        var auth = Assert.IsInstanceOfType<ProtocolAuthenticationException>(translated);
        Assert.Contains("用户名或密码不正确", auth.Message);
        Assert.Contains("C000006D", auth.Message);
    }

    [TestMethod]
    [DataRow(0xC0000071u, "NlaPasswordExpired")]
    [DataRow(0xC0000224u, "NlaPasswordExpired")]
    [DataRow(0xC0000234u, "NlaAccountLocked")]
    [DataRow(0xC0000072u, "NlaAccountDisabled")]
    [DataRow(0xC000015Bu, "NlaNotAllowed")]
    [DataRow(0x8009030Cu, "NlaBadCredentials")]
    public void KnownStatusCodesGetAReadableReason(uint status, string key)
    {
        var loc = new Loc("en");
        Assert.Contains(loc[key], RdpConnector.AuthenticationMessage(status, loc));
    }

    [TestMethod]
    public void UnknownStatusOrClientSideFailureFallsBackToTheGenericMessage()
    {
        var loc = new Loc("en");
        string unknown = RdpConnector.AuthenticationMessage(0xC0001234, loc);
        Assert.Contains(loc["AuthFailed"], unknown);
        Assert.Contains("C0001234", unknown);
        Assert.AreEqual(loc["AuthFailed"], RdpConnector.AuthenticationMessage(null, loc));
    }

    [TestMethod]
    public void TlsFailuresNameTheStageAndTheSchannelCode()
    {
        // The shape of the reported failure: SslStream's IOException wrapping SChannel's Win32 error.
        var tls = new IOException("The decryption operation failed, see inner exception.",
            new Win32Exception(unchecked((int)0x80090330), "The specified data could not be decrypted."));
        Exception translated = RdpConnector.Translate(new RdpConnectionException(RdpConnectStage.Nla, tls), new Loc("zh-Hans"));
        var failure = Assert.IsInstanceOfType<ProtocolConnectionException>(translated);
        Assert.Contains("「NLA 身份验证」", failure.Message);
        Assert.Contains("The decryption operation failed", failure.Message);
        Assert.Contains("0x80090330", failure.Message);
        Assert.DoesNotContain("see inner exception", failure.Message);
    }

    [TestMethod]
    public void EveryStageHasALocalizedName()
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            var loc = new Loc(locale);
            foreach (RdpConnectStage stage in Enum.GetValues<RdpConnectStage>())
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(loc["Stage" + stage]), $"{locale}: {stage}");
            }
        }
    }
}
