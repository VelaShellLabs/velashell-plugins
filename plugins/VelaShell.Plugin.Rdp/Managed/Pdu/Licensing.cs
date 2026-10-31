using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Pdu;

/// <summary>
/// Processes the server licensing message that precedes capability exchange. Handles the
/// common "valid client / no license required" path (MS-RDPELE 2.2.2.4) used by xrdp and
/// by Windows servers without RDS licensing. A genuine licence negotiation is not supported.
/// </summary>
internal static class Licensing
{
    private const byte ErrorAlert = 0xFF;
    private const uint StatusValidClient = 0x00000007;
    private const uint ErrorNoLicenseServer = 0x00000006;
    private const uint ErrorNoLicense = 0x00000005;

    /// <summary>Returns true when licensing is complete; throws if the server demands a real licence.</summary>
    internal static bool Process(ReadOnlySpan<byte> mcsPayload)
    {
        var r = new ByteReader(mcsPayload);
        ushort flags = r.U16LE();         // basic security header flags
        r.U16LE();                        // flagsHi
        if ((flags & SecurityFlags.License) == 0)
            throw new RdpProtocolException("Expected a licensing PDU.");
        byte msgType = r.U8();
        r.U8();                           // flags/version
        r.U16LE();                        // wMsgSize
        if (msgType == ErrorAlert)
        {
            uint errorCode = r.U32LE();
            r.U32LE();                    // dwStateTransition
            // Valid client, or the server has no licensing configured: proceed.
            if (errorCode is StatusValidClient or ErrorNoLicenseServer or ErrorNoLicense) return true;
            return true; // Any licence error alert ends the phase; capabilities follow.
        }
        throw new RdpProtocolException($"RDP licensing negotiation (msg 0x{msgType:X2}) is not supported.");
    }
}
