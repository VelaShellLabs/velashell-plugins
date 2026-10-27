using System.Text.Json;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpPluginActivationTests
{
    [TestMethod]
    public async Task ActivationDescriptorMatchesManifestAndHostCredentials()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var plugin = new RdpPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        Assert.AreEqual("velashell.rdp", descriptor.Id);
        Assert.AreEqual(3389, descriptor.DefaultPort);
        Assert.IsNotNull(descriptor.Icon);
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.CertificateTrust));
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.SshTunnel));
        Assert.IsFalse(descriptor.Features.HasFlag(WorkspaceFeatures.AnonymousAccess));
        Assert.IsFalse(descriptor.Features.HasFlag(WorkspaceFeatures.NoCredentials));
        Assert.IsTrue(descriptor.Fields.Single(f => f.Key == descriptor.TrustedThumbprintSettingKey).IsHidden);
        await plugin.DeactivateAsync(CancellationToken.None);
        Assert.IsEmpty(context.RecordingWorkspaces.Registered);
    }

    [TestMethod]
    public async Task LocaleReregistrationReusesProviderAndDeactivationRemovesSubscription()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var plugin = new RdpPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        IWorkspaceProvider? before = context.RecordingWorkspaces.GetProvider(context.PluginId);
        context.HostEvents.RaiseLocaleChanged("zh-Hans");
        Assert.AreSame(before, context.RecordingWorkspaces.GetProvider(context.PluginId));
        Assert.AreEqual("Windows 主机", context.RecordingWorkspaces.Registered.Single().HostLabel);
        await plugin.DeactivateAsync(CancellationToken.None);
        context.HostEvents.RaiseLocaleChanged("en");
        Assert.IsEmpty(context.RecordingWorkspaces.Registered);
    }

    [TestMethod]
    public void ManifestDeclaresLazyEmbeddedRdpAndExistingReleaseTrain()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "VelaShell.Plugins.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugins", "VelaShell.Plugin.Rdp", "plugin.json")));
        JsonElement data = manifest.RootElement;
        Assert.AreEqual("velashell.rdp", data.GetProperty("id").GetString());
        string train = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants("VelaPluginsVersion").Single().Value;
        Assert.AreEqual(train, data.GetProperty("version").GetString());
        Assert.AreEqual("inProcess", data.GetProperty("hostMode").GetString());
        Assert.AreEqual("2.0.4", data.GetProperty("minSdkVersion").GetString());
        Assert.AreEqual("onWorkspace:velashell.rdp", data.GetProperty("activationEvents")[0].GetString());
        Assert.AreEqual(3389, data.GetProperty("contributes").GetProperty("workspaces")[0].GetProperty("defaultPort").GetInt32());
    }
}
