using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Pdu;

/// <summary>Basic Security Header flag bits (MS-RDPBCGR 2.2.8.1.1.2.1).</summary>
internal static class SecurityFlags
{
    internal const ushort Exchange = 0x0001;
    internal const ushort Encrypt = 0x0008;
    internal const ushort Info = 0x0040;
    internal const ushort License = 0x0080;
}

/// <summary>Builds the Client Info PDU (MS-RDPBCGR 2.2.1.11) carrying logon information.</summary>
internal static class ClientInfo
{
    // TS_INFO_PACKET flags.
    private const uint InfoMouse = 0x00000001;
    private const uint InfoDisableCtrlAltDel = 0x00000002;
    private const uint InfoAutoLogon = 0x00000008;
    private const uint InfoUnicode = 0x00000010;
    private const uint InfoMaximizeShell = 0x00000020;
    private const uint InfoLogonNotify = 0x00000040;
    private const uint InfoEnableWindowsKey = 0x00000100;
    private const uint InfoNoAudioPlayback = 0x00080000;
    private const uint InfoCompression = 0x00000080;
    private const uint CompressionType64K = 1 << 9; // PACKET_COMPR_TYPE_64K in bits 9-12

    /// <summary>
    /// Builds the logon PDU. <paramref name="performanceFlags"/> is written to
    /// TS_EXTENDED_INFO_PACKET.performanceFlags (desktop features to suppress); when
    /// <paramref name="audioPlayback"/> is <see langword="false" />, INFO_NOAUDIOPLAYBACK is set so the server plays no audio.
    /// </summary>
    internal static byte[] Build(string domain, string username, string password, bool autoLogon, uint performanceFlags, bool audioPlayback, bool remoteApp = false)
    {
        var w = new ByteWriter(2048);   // roomy enough that the password is never copied by a buffer resize
        // Basic Security Header: SEC_INFO_PKT.
        w.U16LE(SecurityFlags.Info);
        w.U16LE(0);                       // flagsHi

        uint flags = InfoMouse | InfoDisableCtrlAltDel | InfoUnicode | InfoMaximizeShell |
                     InfoLogonNotify | InfoEnableWindowsKey | InfoCompression | CompressionType64K;
        if (remoteApp)
        {
            flags = (flags & ~InfoMaximizeShell) | 0x00008000; // INFO_RAIL; application starts through RAIL Execute
        }
        if (!audioPlayback)
        {
            flags |= InfoNoAudioPlayback;
        }

        if (autoLogon && password.Length > 0)
        {
            flags |= InfoAutoLogon;
        }

        w.U32LE(0);                       // CodePage
        w.U32LE(flags);

        byte[] domainBytes = Utf16(domain);
        byte[] userBytes = Utf16(username);
        byte[] passBytes = Utf16(password);
        byte[] shellBytes = Utf16("");
        byte[] workDirBytes = Utf16("");

        // cb* fields are byte counts WITHOUT the null terminator.
        w.U16LE((ushort)(domainBytes.Length - 2));
        w.U16LE((ushort)(userBytes.Length - 2));
        w.U16LE((ushort)(passBytes.Length - 2));
        w.U16LE((ushort)(shellBytes.Length - 2));
        w.U16LE((ushort)(workDirBytes.Length - 2));
        w.Bytes(domainBytes);
        w.Bytes(userBytes);
        w.Bytes(passBytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(passBytes);
        w.Bytes(shellBytes);
        w.Bytes(workDirBytes);

        // TS_EXTENDED_INFO_PACKET (required for RDP 5+).
        w.U16LE(2);                       // clientAddressFamily = AF_INET
        byte[] addr = Utf16("0.0.0.0");
        w.U16LE((ushort)addr.Length);     // cbClientAddress INCLUDES the null terminator
        w.Bytes(addr);
        byte[] clientDir = Utf16("");
        w.U16LE((ushort)clientDir.Length);
        w.Bytes(clientDir);
        w.Zeros(172);                     // clientTimeZone (TS_TIME_ZONE_INFORMATION), UTC/zeroed
        w.U32LE(0);                       // clientSessionId
        w.U32LE(performanceFlags);
        w.U16LE(0);                       // cbAutoReconnectCookie
        return w.ToArray();
    }

    private static byte[] Utf16(string value)
    {
        byte[] body = Encoding.Unicode.GetBytes(value);
        byte[] result = new byte[body.Length + 2];
        body.CopyTo(result, 0);
        return result;                    // null-terminated UTF-16LE
    }
}
