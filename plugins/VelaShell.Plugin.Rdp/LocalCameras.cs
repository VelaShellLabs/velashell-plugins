using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp;

internal static class LocalCameras
{
    internal const string SettingKey = "camera";
    internal static string Key(RdpCameraDevice camera) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(camera.Id)));

    internal static RdpCameraDevice? Selected(WorkspaceConnectRequest request, Loc loc, Func<IReadOnlyList<RdpCameraDevice>> enumerate)
    {
        string selection = request.GetString(SettingKey);
        if (selection.Length == 0)
        {
            return null;
        }

        if (selection.Length != 64 || !selection.All(char.IsAsciiHexDigit))
        {
            throw new ProtocolConnectionException(loc.Format("Invalid", loc["Camera"]));
        }

        try
        {
            return enumerate().FirstOrDefault(camera => string.Equals(Key(camera), selection, StringComparison.Ordinal))
                ?? throw new IOException("Selected camera is unavailable.");
        }
        catch (Exception ex) when (ex is IOException or COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ProtocolConnectionException(loc["CameraUnavailable"], ex);
        }
    }
}
