using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Pdu;

/// <summary>
/// The connection-finalization Data PDUs the client sends after Confirm Active
/// (MS-RDPBCGR 1.3.1.1, "Capabilities Exchange" → "Connection Finalization").
/// </summary>
internal static class ControlPdus
{
    private const ushort ServerChannel = 1002;
    private const ushort CtrlActionRequestControl = 0x0001;
    private const ushort CtrlActionCooperate = 0x0004;

    internal static byte[] Synchronize(uint shareId, ushort userId)
    {
        var body = new ByteWriter(4);
        body.U16LE(1);              // messageType = SYNCMSGTYPE_SYNC
        body.U16LE(ServerChannel);  // targetUser
        return ShareControl.BuildDataPdu(shareId, ShareDataType.Synchronize, userId, body.Span);
    }

    internal static byte[] ControlCooperate(uint shareId, ushort userId) => Control(shareId, userId, CtrlActionCooperate);
    internal static byte[] RequestControl(uint shareId, ushort userId) => Control(shareId, userId, CtrlActionRequestControl);

    private static byte[] Control(uint shareId, ushort userId, ushort action)
    {
        var body = new ByteWriter(8);
        body.U16LE(action);
        body.U16LE(0);              // grantId
        body.U32LE(0);              // controlId
        return ShareControl.BuildDataPdu(shareId, ShareDataType.Control, userId, body.Span);
    }

    internal static byte[] FontList(uint shareId, ushort userId)
    {
        var body = new ByteWriter(8);
        body.U16LE(0);              // numberFonts
        body.U16LE(0);              // totalNumFonts
        body.U16LE(0x0003);         // listFlags = FONTLIST_FIRST | FONTLIST_LAST
        body.U16LE(0x0032);         // entrySize
        return ShareControl.BuildDataPdu(shareId, ShareDataType.FontList, userId, body.Span);
    }
}
