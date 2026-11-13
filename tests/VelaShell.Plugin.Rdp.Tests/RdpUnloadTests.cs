using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Runs inside a collectible load context (see <see cref="RdpUnloadTests"/>): a full workspace cycle — open,
/// show the view, focus it, detach it the way the host does when the tab closes, dispose.
/// </summary>
public static class RdpUnloadScenario
{
    public static async Task RunAsync()
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var provider = new RdpWorkspaceProvider(context, new("en"), (_, _, _, _) =>
            Task.FromResult<IRdpSession>(new FakeRdpSession()));
        IWorkspaceDocument document = await provider.OpenAsync(RdpSettingsTests.Request());
        var root = (ContentControl)document.CreateView();
        var window = new Window { Width = 800, Height = 600, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        root.Focus();
        Dispatcher.UIThread.RunJobs();
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        await document.DisposeAsync();
        await provider.DisposeAsync();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// The host loads the plugin into a collectible <c>AssemblyLoadContext</c> and shares Avalonia and the SDK with it.
/// Anything the plugin leaves in Avalonia's process-wide state — a registered <c>AvaloniaProperty</c>, a class
/// handler, a static subscription — keeps the whole context alive, so every deactivation would leave the plugin's
/// assemblies in memory until VelaShell restarts (and, on Windows, its files locked while a stack trace's PDB
/// reader is cached). This test loads a private copy of the plugin and of these tests into such a context, runs
/// <see cref="RdpUnloadScenario"/> in it, unloads it and requires it to be collected.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpUnloadTests
{
    /// <summary>Shares exactly what the host's plugin load context shares: Avalonia, the SDK and the framework.</summary>
    private sealed class PluginLikeContext(string directory) : AssemblyLoadContext("rdp-unload-test", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string n = name.Name ?? "";
            if (n.StartsWith("Avalonia", StringComparison.Ordinal) || n.StartsWith("VelaShell.PluginSdk", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.", StringComparison.Ordinal) || n.StartsWith("MSTest", StringComparison.Ordinal)
                || n.StartsWith("System.", StringComparison.Ordinal))
            {
                return null;
            }
            string path = Path.Combine(directory, n + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunInCollectibleContext(HeadlessUnitTestSession session)
    {
        string location = typeof(RdpUnloadTests).Assembly.Location;
        var context = new PluginLikeContext(Path.GetDirectoryName(location)!);
        Assembly tests = context.LoadFromAssemblyPath(location);
        MethodInfo run = tests.GetType(typeof(RdpUnloadScenario).FullName!)!.GetMethod(nameof(RdpUnloadScenario.RunAsync))!;
        _ = session.Dispatch(async () => { await (Task)run.Invoke(null, null)!; return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
        // What the host does before unloading an in-process plugin (VelaShell PluginManager): drop Avalonia's per-type
        // caches for the plugin's types. Only properties the plugin registers itself survive this — hence the test.
        Type[] types = [.. context.Assemblies.SelectMany(static a =>
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToArray(); }
        })];
        _ = Avalonia.AvaloniaPropertyRegistry.Instance.UnregisterByModule(types);
        context.Unload();
        return new WeakReference(context);
    }

    [TestMethod]
    public void WorkspaceCycle_LeavesNothingThatPinsThePluginLoadContext()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessApp).Assembly);
        WeakReference context = RunInCollectibleContext(session);
        for (int i = 0; i < 20 && context.IsAlive; i++)
        {
            _ = session.Dispatch(() => { Dispatcher.UIThread.RunJobs(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(250);
        }
        Assert.IsFalse(context.IsAlive,
            "Something in the RDP workspace UI still references the plugin's types from Avalonia's shared state "
            + "(a registered AvaloniaProperty, AffectsRender/AddClassHandler, or a static subscription).");
    }
}
