using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.Rdp;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// A session left in a background tab for hours: the link is kept alive (TCP keep-alive, plus a keep-alive PDU while
/// display updates are paused), a network loss is told apart from the server ending the session, and a lost
/// connection is re-established automatically — also while the tab is hidden.
/// </summary>
[TestClass]
public sealed class RdpBackgroundSessionTests
{
    // ---- Library: keep-alive and the cause of a disconnect ----

    [TestMethod]
    public async Task TcpKeepAliveProbesAQuietConnection()
    {
        (TcpClient client, TcpClient server) = await ConnectedPairAsync();
        using (client)
        using (server)
        {
            RdpConnection.ConfigureKeepAlive(client.Client, TimeSpan.FromSeconds(30));
            // A boolean option: Windows and Linux read back 1, macOS the SO_KEEPALIVE flag value (8).
            Assert.AreNotEqual(0, (int)client.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            Assert.AreEqual(30, (int)client.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
            Assert.AreEqual(10, (int)client.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
            Assert.AreEqual(RdpConnection.KeepAliveProbes, (int)client.Client.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
        }
    }

    [TestMethod]
    public async Task ZeroKeepAliveLeavesTheSocketAlone()
    {
        (TcpClient client, TcpClient server) = await ConnectedPairAsync();
        using (client)
        using (server)
        {
            RdpConnection.ConfigureKeepAlive(client.Client, TimeSpan.Zero);
            Assert.AreEqual(0, (int)client.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        }
    }

    [TestMethod]
    public void TheDefaultKeepAliveIsThirtySeconds() =>
        Assert.AreEqual(TimeSpan.FromSeconds(30), new RdpClientOptions { Host = "h" }.KeepAliveInterval);

    [TestMethod]
    public async Task TheServerClosingTcpIsAConnectionLoss()
    {
        var transport = new RdpTransport(new MemoryStream([]));
        RdpConnectionLostException lost = await Assert.ThrowsAsync<RdpConnectionLostException>(() => transport.ReadPduAsync(default));
        _ = Assert.IsInstanceOfType<IOException>(lost);
    }

    [TestMethod]
    public void AReasonTheServerSentWinsOverTheConnectionClosingAfterIt()
    {
        var lost = new RdpConnectionLostException("The RDP server closed the connection.");
        Assert.AreEqual(0x0Cu, ((RdpDisconnectException)RdpManagedSession.DisconnectCause(lost, 0x0C)!).ErrorInfo);
        Assert.AreEqual(0x02u, ((RdpDisconnectException)RdpManagedSession.DisconnectCause(null, 0x02)!).ErrorInfo);
        var graphics = (RdpDisconnectException)RdpManagedSession.DisconnectCause(lost, 0x112F)!;
        Assert.Contains("GRAPHICS_SUBSYSTEM_FAILED", graphics.Message);
        Assert.Contains("0x0000112F", graphics.Message);
        Assert.AreSame(lost, RdpManagedSession.DisconnectCause(lost, 0));
        Assert.IsNull(RdpManagedSession.DisconnectCause(null, 0));
        var protocol = new IOException("RDP dynamic-channel processing failed; reconnect the session.");
        Assert.AreSame(protocol, RdpManagedSession.DisconnectCause(protocol, 0x0C), "a local failure is reported as itself");
    }

    // ---- Plugin: automatic reconnect ----

    [TestMethod]
    public async Task ANetworkLossReconnectsAutomatically()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using RdpWorkspaceProvider provider = Provider(context, sessions);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(10)];

        sessions[0].Disconnect(new RdpConnectionLostException("The RDP server closed the connection."));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.HasCount(2, sessions);
        Assert.AreEqual(1, sessions[0].DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        Assert.Contains(e => e.Message.Contains("reconnected automatically (attempt 1)"), context.CollectingLog.Entries);
        await document.DisposeAsync();
        Assert.AreEqual(1, sessions[1].DisposeCount);
    }

    [TestMethod]
    public async Task ASessionTheServerEndedIsNotReconnectedAutomatically()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using RdpWorkspaceProvider provider = Provider(context, sessions);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(10)];

        sessions[0].Disconnect(new RdpDisconnectException(0x0C));   // signed out on the remote
        Assert.IsNull(document.AutoReconnectTask);
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.HasCount(1, sessions);

        sessions.Clear();
        var second = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        sessions[0].Disconnect(new IOException("RDP drive request queue capacity exceeded; reconnect the session."));
        Assert.IsNull(second.AutoReconnectTask, "a local failure is not a network loss");
        Assert.Contains(e => e.Message.Contains("IOException: RDP drive request queue"), context.CollectingLog.Entries);
    }

    [TestMethod]
    public async Task AutomaticReconnectRetriesThenGivesUpToTheButton()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        int calls = 0;
        var first = new FakeRdpSession();
        await using var provider = new RdpWorkspaceProvider(context, new("en"), (_, _, _, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromResult<IRdpSession>(first)
                : throw new ProtocolConnectionException("unreachable"));
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(5)];
        document.AutoReconnectAttempts = 3;

        first.Disconnect(new RdpConnectionLostException("reset"));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(4, calls, "the first connection and three automatic attempts");
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.AreEqual(new Loc("en")["AutoReconnectFailed"], document.Status.Message);
        Assert.AreEqual(0, first.DisposeCount, "the lost session stays until a reconnect succeeds or the tab closes");
        await document.ReconnectAsync().ContinueWith(_ => { });   // the manual button still works (and fails here)
        Assert.AreEqual(5, calls);
    }

    [TestMethod]
    public async Task ChangedCredentialsStopAutomaticReconnectAtOnce()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        int calls = 0;
        var first = new FakeRdpSession();
        await using var provider = new RdpWorkspaceProvider(context, new("en"), (_, _, _, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromResult<IRdpSession>(first)
                : throw new ProtocolAuthenticationException("The password was changed."));
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(5)];

        first.Disconnect(new RdpConnectionLostException("reset"));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(2, calls);
        Assert.AreEqual("The password was changed.", document.Status.Message);
    }

    [TestMethod]
    public async Task ASessionThatDropsRightAfterReconnectingContinuesTheSeries()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using RdpWorkspaceProvider provider = Provider(context, sessions);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(5)];
        document.AutoReconnectAttempts = 2;
        document.AutoReconnectStableAfter = TimeSpan.FromHours(1);   // every session counts as short-lived

        for (int drop = 0; drop < 3; drop++)
        {
            sessions[^1].Disconnect(new RdpConnectionLostException("reset"));
            await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.HasCount(3, sessions, "two reconnects, then the series is spent");
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
    }

    [TestMethod]
    public async Task AManualReconnectDuringTheWaitWinsAndClosingTheTabStopsTheWait()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using RdpWorkspaceProvider provider = Provider(context, sessions);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromHours(1)];

        sessions[0].Disconnect(new RdpConnectionLostException("reset"));
        Task waiting = document.AutoReconnectTask!;
        for (int i = 0; i < 100 && document.Status.Message is null; i++) { await Task.Delay(10); }
        Assert.AreEqual(new Loc("en").Format("AutoReconnecting", 1, 20), document.Status.Message);

        await document.ReconnectAsync();
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        Assert.IsFalse(waiting.IsCompleted);

        await document.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(waiting.IsCompleted);
        Assert.HasCount(2, sessions);
        Assert.AreEqual(ProtocolSessionState.Closed, document.Status.State);
    }

    [TestMethod]
    public void AReconnectWhileTheTabIsHiddenStartsWithUpdatesPaused() => RdpHeadlessTests.Run(async () =>
    {
        // The view's debounced resize records its size for the next connection; do not leak it into later tests.
        RdpViewMetrics.ResetForTest();
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using RdpWorkspaceProvider provider = Provider(context, sessions);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(10)];
        var root = (ContentControl)document.CreateView();
        var window = new Window { Width = 800, Height = 600, Content = root };
        window.Show();
        window.UpdateLayout();
        await Settle();

        window.Content = null;   // the user switched to another tab
        await Settle();
        sessions[0].Disconnect(new RdpConnectionLostException("reset"));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await Settle();
        Assert.HasCount(2, sessions);
        Assert.AreSequenceEqual(new[] { false }, [.. sessions[1].DisplayUpdates], "nobody sees it yet: paused");

        window.Content = root;   // back to the tab
        window.UpdateLayout();
        await Settle();
        Assert.AreSequenceEqual(new[] { false, true }, [.. sessions[1].DisplayUpdates]);
        Assert.IsTrue(((RdpWorkspaceView)root.Content!).Desktop.IsPresenting);
        await document.DisposeAsync();
        window.Close();
        RdpViewMetrics.ResetForTest();
    });

    private static RdpWorkspaceProvider Provider(TestPluginContext context, List<FakeRdpSession> sessions) =>
        new(context, new("en"), (_, _, _, _) =>
        {
            var session = new FakeRdpSession();
            lock (sessions) { sessions.Add(session); }
            return Task.FromResult<IRdpSession>(session);
        });

    private static async Task<(TcpClient Client, TcpClient Server)> ConnectedPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return (client, await accept);
        }
        finally { listener.Stop(); }
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }
}
