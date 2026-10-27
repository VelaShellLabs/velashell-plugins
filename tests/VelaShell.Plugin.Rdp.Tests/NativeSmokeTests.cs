using System.Reflection;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.PluginSdk.Hosting;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class NativeSmokeTests
{
    [TestMethod]
    public void CurrentPlatformNative_CreateCallDispose_IsReal()
    {
        // Missing native files or exports must fail, never become a hidden skip.
        StringAssert.Contains(RdpNativeSmoke.Run(), "passed");
    }

    [TestMethod]
    public void SdkPluginAlc_ResolvesManagedAndNativeDependencies()
    {
        // Build copies the matching configuration's actual plugin payload; never use an old Debug DLL.
        string entry = Path.Combine(AppContext.BaseDirectory, "alc-payload", "VelaShell.Plugin.Rdp.dll");
        Assert.IsTrue(File.Exists(entry), entry);
        var context = new PluginAssemblyLoadContext("velashell.rdp.native-smoke", entry);
        try
        {
            Assembly plugin = context.LoadFromAssemblyPath(entry);
            Type smoke = plugin.GetType("VelaShell.Plugin.Rdp.Engine.RdpNativeSmoke", throwOnError: true)!;
            MethodInfo run = smoke.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            string result = (string)run.Invoke(null, null)!;
            StringAssert.Contains(result, "passed");
            Assembly nativeBridge = context.Assemblies.Single(a => a.GetName().Name == "Devolutions.IronRdp");
            Assert.AreSame(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(nativeBridge));
        }
        finally
        {
            context.Unload();
        }
    }
}
