using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Live NLA (CredSSP over TLS, NTLM) checks against a server that requires it — xrdp has no NLA, so
/// point these at an NLA-enforcing server (a Windows host, or another CredSSP-capable test server).
/// Skipped unless VELA_RDP_TEST_NLA_TARGET is set (host:port); credentials come from
/// VELA_RDP_TEST_USER / VELA_RDP_TEST_PASS. Never runs in CI.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpNlaInteropTests
{
    [TestInitialize]
    public void DescribeRuntime() =>
        Console.WriteLine($"NLA runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");

    private static (string Host, int Port)? NlaTarget()
    {
        string? raw = Environment.GetEnvironmentVariable("VELA_RDP_TEST_NLA_TARGET");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] parts = raw.Split(':');
        return (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 3389);
    }

    /// <summary>Learns the leaf pin, offering NLA too — an NLA-only server refuses a plain-TLS request.</summary>
    private static async Task<byte[]> LearnPinAsync((string Host, int Port) target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = timeout.Token;
        using var probe = new TcpClient();
        Console.WriteLine("NLA probe: connecting TCP, negotiating RDP and reading the TLS leaf certificate.");
        await probe.ConnectAsync(target.Host, target.Port, ct);
        var transport = new RdpTransport(probe.GetStream());
        await transport.SendTpktAsync(RdpTransport.BuildConnectionRequest(RdpManagedInteropTests.User, SecurityProtocol.Ssl | SecurityProtocol.Hybrid), ct);
        _ = await transport.ReadConnectionConfirmAsync(ct);
        try
        {
            await using var tls = await RdpTls.AuthenticateAsync(probe.GetStream(), target.Host, null, ct);
            Assert.IsNotNull(tls.RemoteCertificate);
            return SHA256.HashData(tls.RemoteCertificate.GetRawCertData());
        }
        catch (RdpCertificateRejectedException ex)
        {
            using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(ex.Pem));
            return SHA256.HashData(leaf.RawData);
        }
    }

    private static WorkspaceConnectRequest Request((string Host, int Port) target, byte[] pin, string password) =>
        RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)), ("width", "1024"), ("height", "768")) with
        { Host = target.Host, Port = target.Port, Username = RdpManagedInteropTests.User, Password = password };

    private static string Chain(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }
        return string.Join("  ->  ", parts);
    }

    [TestMethod]
    public Task NlaAuthenticatesAndReachesTheDesktop() => AuthenticateAndReach(forceManaged: false);

    // On Windows this alternate path still uses SSPI's NTLM package: it does not prove Unix compatibility.
    // ManagedNtlmTests also runs on Linux/macOS to exercise .NET's actual managed NTLM provider.
    [TestMethod]
    public Task ManagedNtlmAlsoAuthenticatesAndReachesTheDesktop() => AuthenticateAndReach(forceManaged: true);

    private static async Task AuthenticateAndReach(bool forceManaged)
    {
        if (NlaTarget() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_NLA_TARGET not set."); return; }
        byte[] pin = await LearnPinAsync(target);
        Assert.HasCount(32, pin, "Could not learn the server's leaf certificate.");
        WorkspaceConnectRequest request = Request(target, pin, RdpManagedInteropTests.Pass);
        var loc = new Loc("en");
        var settings = RdpSettings.From(request, loc) with { Security = RdpSecurity.Nla };

        IRdpSession session;
        Console.WriteLine($"NLA login: package={(forceManaged ? "NTLM" : "Negotiate")}; backend={(OperatingSystem.IsWindows() ? "SSPI" : ".NET Unix authentication")}; security=NLA required.");
        NlaSecurityContext.ForceManagedForTest = forceManaged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            session = await RdpConnector.ConnectAsync(request, settings, loc, timeout.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NLA connect failed (forceManaged={forceManaged}): " + Chain(ex));
            Console.WriteLine(ex.ToString());
            throw;
        }
        finally
        {
            NlaSecurityContext.ForceManagedForTest = false;
        }

        try
        {
            RdpFrame? latest = null;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && latest is null)
            {
                latest = session.TakeFrame();
                await Task.Delay(150, CancellationToken.None);
            }
            Console.WriteLine($"NLA session (forceManaged={forceManaged}) connected={session.IsConnected}, frame={latest?.Width}x{latest?.Height}");
            Assert.IsTrue(session.IsConnected, "The session dropped after NLA.");
            Assert.IsNotNull(latest, "No desktop frame after NLA.");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task NlaWrongPasswordIsReportedAsAnAuthenticationFailure()
    {
        if (NlaTarget() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_NLA_TARGET not set."); return; }
        byte[] pin = await LearnPinAsync(target);
        WorkspaceConnectRequest request = Request(target, pin, "definitely-not-the-password");
        var loc = new Loc("en");
        var settings = RdpSettings.From(request, loc);
        Exception? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            IRdpSession session = await RdpConnector.ConnectAsync(request, settings, loc, timeout.Token);
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            failure = ex;
            Console.WriteLine("Wrong-password result: " + Chain(ex));
        }
        Assert.IsNotNull(failure, "A wrong password must not connect.");
        // Still a credentials error under the default Auto security layer: a rejected password is never
        // retried with TLS (the retry is only for servers that refuse NLA before credentials are sent).
        _ = Assert.IsInstanceOfType<ProtocolAuthenticationException>(failure, Chain(failure));
    }

    [TestMethod]
    public async Task TlsOnlyIsRefusedByAServerThatRequiresNla()
    {
        if (NlaTarget() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_NLA_TARGET not set."); return; }
        var options = new RdpClientOptions
        {
            Host = target.Host,
            Port = target.Port,
            Username = RdpManagedInteropTests.User,
            Password = RdpManagedInteropTests.Pass,
            CertificatePin = await LearnPinAsync(target),
            Security = RdpSecurity.Tls
        };
        RdpConnectionException? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using RdpManagedSession session = await RdpManagedSession.ConnectAsync(options, timeout.Token);
        }
        catch (RdpConnectionException ex)
        {
            failure = ex;
            Console.WriteLine("TLS-only result: " + Chain(ex));
        }
        Assert.IsNotNull(failure, "An NLA-only server must refuse a TLS-only client.");
        Assert.AreEqual(RdpNegotiationFailure.HybridRequiredByServer, failure.NegotiationFailure);
    }
}
