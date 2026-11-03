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
        ProtocolAuthenticationException auth = Assert.IsInstanceOfType<ProtocolAuthenticationException>(translated);
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
        ProtocolConnectionException failure = Assert.IsInstanceOfType<ProtocolConnectionException>(translated);
        Assert.Contains("「NLA 身份验证」", failure.Message);
        Assert.Contains("The decryption operation failed", failure.Message);
        Assert.Contains("0x80090330", failure.Message);
        Assert.DoesNotContain("see inner exception", failure.Message);
    }

    [TestMethod]
    public void FailuresAfterTheHandshakeNameTheNegotiatedTlsVersion()
    {
        // The reported Windows failure: SEC_E_INTERNAL_ERROR right after the first NLA message.
        var tls = new IOException("The decryption operation failed, see inner exception.",
            new Win32Exception(unchecked((int)0x80090304), "The Local Security Authority cannot be contacted"));
        var failure = new RdpConnectionException(RdpConnectStage.Nla, tls, System.Security.Authentication.SslProtocols.Tls13);
        Assert.AreEqual(System.Security.Authentication.SslProtocols.Tls13, failure.TlsProtocol);
        Assert.EndsWith("[TLS 1.3]", failure.Detail);
        Assert.Contains("0x80090304", failure.Detail);
        Assert.Contains("[TLS 1.3]", RdpConnector.Translate(failure, new Loc("en")).Message);
        Assert.DoesNotContain("[TLS", new RdpConnectionException(RdpConnectStage.Tcp, new IOException("refused")).Detail);
    }

    [TestMethod]
    [DataRow(0x0000000Cu, "ByeLoggedOff")]   // LOGOFF_BY_USER
    [DataRow(0x00000002u, "ByeLoggedOff")]   // RPC_INITIATED_LOGOFF
    [DataRow(0x00000005u, "ByeTakenOver")]   // DISCONNECTED_BY_OTHERCONNECTION
    [DataRow(0x00000009u, "ByeDenied")]      // SERVER_INSUFFICIENT_PRIVILEGES
    [DataRow(0x0000010Au, "ByeLicense")]     // LICENSE_NO_REMOTE_CONNECTIONS
    [DataRow(0x00000102u, "ByeLicense")]     // LICENSE_NO_LICENSE
    [DataRow(0x0000112Du, "ByeGraphicsMode")]
    [DataRow(0x0000112Eu, "ByeGraphicsReset")]
    [DataRow(0x0000112Fu, "ByeGraphicsFailed")]
    public void MidSessionDisconnectReasonsAreLocalizedByErrorInfo(uint code, string key)
    {
        var loc = new Loc("en");
        string? reason = RdpConnector.DisconnectReason(new RdpDisconnectException(code), loc);
        Assert.IsNotNull(reason);
        Assert.Contains(loc[key], reason);
        Assert.Contains($"0x{code:X8}", reason);
    }

    [TestMethod]
    public void UnknownDisconnectCodeFallsBackToTheNeutralReasonAndCode()
    {
        var ex = new RdpDisconnectException(0x00000006);   // OUT_OF_MEMORY: not individually localized
        Assert.AreEqual(0x00000006u, ex.ErrorInfo);
        Assert.Contains("OUT_OF_MEMORY", ex.Message);
        string? reason = RdpConnector.DisconnectReason(ex, new Loc("zh-Hans"));
        Assert.IsNotNull(reason);
        Assert.Contains("0x00000006", reason);
    }

    [TestMethod]
    public void AnOrdinaryDropHasNoServerReason()
    {
        Assert.IsNull(RdpConnector.DisconnectReason(null, new Loc("en")));
        Assert.IsNull(RdpConnector.DisconnectReason(new IOException("connection reset"), new Loc("en")));
        Assert.IsTrue(RdpErrorInfo.IsLicensing(0x00000104));
        Assert.IsFalse(RdpErrorInfo.IsLicensing(0x0000000C));
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
