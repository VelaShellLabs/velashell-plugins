using System.Security.Cryptography;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp;

internal static class LocalWpdDevices
{
    internal const string KeyPrefix = "wpd:";
    internal static string Key(RdpWpdDevice device) => KeyPrefix + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(device.Id.ToUpperInvariant())));

    internal static IReadOnlyList<string> Selected(WorkspaceConnectRequest request, Loc loc,
        Func<IReadOnlyList<RdpWpdDevice>> enumerate)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, string value) in request.Settings)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
            if (!bool.TryParse(value, out bool enabled))
                throw new ProtocolConnectionException(loc.Format("Invalid", loc["WpdDevices"]));
            if (enabled) selected.Add(key);
        }
        if (selected.Count == 0) return [];
        if (selected.Count > 32) throw new ProtocolConnectionException(loc.Format("Invalid", loc["WpdDevices"]));
        try
        {
            string[] ids = enumerate().Where(device => selected.Contains(Key(device))).Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length != selected.Count) throw new IOException("A selected WPD interface is unavailable.");
            return ids;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            throw new ProtocolConnectionException(loc["WpdUnavailable"], ex);
        }
    }
}
