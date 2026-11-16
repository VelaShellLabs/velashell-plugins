using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>Runs inside a collectible load context: open, show, focus, detach (tab closed), dispose.</summary>
public static class VncUnloadScenario
{
    public static async Task RunAsync()
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var provider = new VncWorkspaceProvider(context, new("en"), (_, _, _, _) => Task.FromResult<IVncSession>(new FakeVncSession()));
        IWorkspaceDocument document = await provider.OpenAsync(VncPluginTests.Request());
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
/// The host loads the plugin into a collectible <c>AssemblyLoadContext</c> sharing Avalonia and the SDK. Anything left in
/// Avalonia's process-wide state (a registered property, a class handler, a static subscription) keeps the context
/// alive after deactivation. Same check as the RDP plugin's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class VncUnloadTests
{
    private sealed class PluginLikeContext(string directory) : AssemblyLoadContext("vnc-unload-test", isCollectible: true)
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
        string location = typeof(VncUnloadTests).Assembly.Location;
        var context = new PluginLikeContext(Path.GetDirectoryName(location)!);
        Assembly tests = context.LoadFromAssemblyPath(location);
        MethodInfo run = tests.GetType(typeof(VncUnloadScenario).FullName!)!.GetMethod(nameof(VncUnloadScenario.RunAsync))!;
        _ = session.Dispatch(async () => { await (Task)run.Invoke(null, null)!; return true; }, CancellationToken.None).GetAwaiter().GetResult();
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
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(VncHeadlessApp).Assembly);
        WeakReference context = RunInCollectibleContext(session);
        for (int i = 0; i < 20 && context.IsAlive; i++)
        {
            _ = session.Dispatch(() => { Dispatcher.UIThread.RunJobs(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(250);
        }
        Assert.IsFalse(context.IsAlive, "Something in the VNC workspace UI still references the plugin's types from Avalonia's shared state.");
    }
}
