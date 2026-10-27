using System.Reflection;
using System.Runtime.CompilerServices;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Hosting;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpPluginUnloadTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void ActivationLocaleChangeAndDeactivationReleaseCollectiblePluginContext()
    {
        // Keep the host/test context alive after unloading: subscriptions cannot hide in a disposed host.
        using var host = new TestPluginContext { PluginId = "velashell.rdp" };
        var retainedForm = new List<WorkspaceDescriptor>();
        WeakReference collectible = ActivateDeactivateAndUnload(host, retainedForm);
        Assert.IsTrue(retainedForm.Count > 0);
        WorkspaceDescriptor form = retainedForm[0];
        TestContext.WriteLine($"Form fields: {form.Fields.GetType().AssemblyQualifiedName}");
        foreach (var field in form.Fields)
            TestContext.WriteLine($"Choices for {field.Key}: {field.Choices.GetType().AssemblyQualifiedName}");
        Assert.IsFalse(form.Fields.GetType().Assembly.GetName().Name == "VelaShell.Plugin.Rdp"
            || form.Fields.Any(field => field.Choices.GetType().Assembly.GetName().Name == "VelaShell.Plugin.Rdp"),
            "Host form collections retain compiler-generated plugin assembly wrappers.");
        for (int i = 0; i < 10 && collectible.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.IsFalse(collectible.IsAlive, "RDP retained its collectible ALC after no-view activation/deactivation.");
        host.HostEvents.RaiseLocaleChanged("en");
        Assert.IsEmpty(host.RecordingWorkspaces.Registered);
        GC.KeepAlive(retainedForm);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ActivateDeactivateAndUnload(TestPluginContext host, List<WorkspaceDescriptor> retainedForm)
    {
        string entry = Path.Combine(AppContext.BaseDirectory, "alc-payload", "VelaShell.Plugin.Rdp.dll");
        var loadContext = new PluginAssemblyLoadContext("velashell.rdp.lifecycle-smoke", entry);
        Assembly assembly = loadContext.LoadFromAssemblyPath(entry);
        var plugin = (IVelaPlugin)Activator.CreateInstance(assembly.GetType("VelaShell.Plugin.Rdp.RdpPlugin", throwOnError: true)!)!;
        plugin.ActivateAsync(host, CancellationToken.None).GetAwaiter().GetResult();
        host.HostEvents.RaiseLocaleChanged("zh-Hans");
        retainedForm.Add(host.RecordingWorkspaces.Registered.Single());
        plugin.DeactivateAsync(CancellationToken.None).GetAwaiter().GetResult();
        var weak = new WeakReference(loadContext);
        loadContext.Unload();
        return weak;
    }
}
