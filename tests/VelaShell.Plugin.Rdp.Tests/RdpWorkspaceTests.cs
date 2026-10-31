using System.Text.Json;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpWorkspaceTests
{
    [TestMethod]
    public async Task LazyManifestMatchesRegistrationAndLocaleKeepsProvider()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rdp-plugin.json")));
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        context.HostInfo.Locale = "en";
        var plugin = new RdpPlugin();
        await plugin.ActivateAsync(context, default);
        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        Assert.AreEqual(manifest.RootElement.GetProperty("id").GetString(), descriptor.Id);
        Assert.AreEqual("onWorkspace:" + descriptor.Id, manifest.RootElement.GetProperty("activationEvents")[0].GetString());
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel));
        Assert.IsTrue(descriptor.Fields.Single(field => field.Key == descriptor.TrustedThumbprintSettingKey).IsHidden);
        Assert.AreEqual("false", descriptor.Fields.Single(field => field.Key == "clipboard").DefaultValue);
        IWorkspaceProvider? before = context.RecordingWorkspaces.GetProvider(descriptor.Id);
        context.HostEvents.RaiseLocaleChanged("zh-Hant");
        Assert.AreSame(before, context.RecordingWorkspaces.GetProvider(descriptor.Id));
        Assert.AreEqual("伺服器位址", context.RecordingWorkspaces.Registered.Single().HostLabel);
        await plugin.DeactivateAsync(default);
        Assert.IsEmpty(context.RecordingWorkspaces.Registered);
    }
    [TestMethod]
    public async Task ReconnectReplacesSessionAndDisposeWorksWithoutCreatingView()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using var provider = new RdpWorkspaceProvider(context, new("en"), (_, _, _, _) =>
        {
            var session = new FakeRdpSession();
            sessions.Add(session);
            return Task.FromResult<IRdpSession>(session);
        });
        IWorkspaceDocument document = await provider.OpenAsync(RdpSettingsTests.Request());
        sessions[0].Disconnect();
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        await document.ReconnectAsync();
        Assert.AreEqual(1, sessions[0].DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        await document.DisposeAsync();
        await document.DisposeAsync();
        Assert.AreEqual(1, sessions[1].DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Closed, document.Status.State);
    }
    [TestMethod]
    public async Task ProviderShutdownCancelsInflightConnectionAndClosesExistingDocuments()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var session = new FakeRdpSession();
        int attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RdpWorkspaceProvider(context, new("en"), async (_, _, _, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return session;
            }

            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new FakeRdpSession();
        });
        IWorkspaceDocument document = await provider.OpenAsync(RdpSettingsTests.Request());
        Task<IWorkspaceDocument> opening = provider.OpenAsync(RdpSettingsTests.Request());
        await entered.Task;
        await provider.DisposeAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => opening);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Closed, document.Status.State);
    }
}
