using System.Security.Cryptography;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal static class LocalPrinters
{
    internal const string KeyPrefix = "printer:";

    internal static string Key(PrinterMapping printer) => KeyPrefix + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(printer.PrinterName.ToUpperInvariant())));

    internal static IReadOnlyList<PrinterMapping> Selected(WorkspaceConnectRequest request, Loc loc,
        Func<IReadOnlyList<PrinterMapping>> enumerate)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, string value) in request.Settings)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) { continue; }
            if (!bool.TryParse(value, out bool enabled))
            {
                throw new ProtocolConnectionException(loc.Format("Invalid", loc["Printers"]));
            }
            if (enabled) { selected.Add(key); }
        }
        if (selected.Count == 0) { return []; }
        if (selected.Count > 16) { throw new ProtocolConnectionException(loc.Format("Invalid", loc["Printers"])); }
        try
        {
            PrinterMapping[] printers = [.. enumerate().Where(printer => selected.Contains(Key(printer)))];
            if (printers.Length != selected.Count) { throw new IOException("A selected printer is unavailable."); }
            return printers;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            throw new ProtocolConnectionException(loc["PrintersUnavailable"], ex);
        }
    }
}
