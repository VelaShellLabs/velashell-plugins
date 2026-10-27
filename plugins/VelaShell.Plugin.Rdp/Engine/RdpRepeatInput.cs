namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>
/// The formal binding exposes no FastPathInputEvent constructor. InputDatabase synthesizes an up/down
/// pair for duplicate presses, which is wrong for held modifiers. Encode only this single press here.
/// MS-RDPBCGR 2.2.8.1.2.1 (TS_FP_INPUT_PDU) and 2.2.8.1.2.2.1 (TS_FP_KEYBOARD_EVENT):
/// action=0, numEvents=1 in bits 2..5, encryptionFlags=0 (the transport is TLS), total length=4,
/// eventCode=SCANCODE(0), RELEASE=0, EXTENDED=0x02. No native state is changed on a repeat.
/// Also checked against the exact package commit ironrdp-pdu/src/input/fast_path.rs encoder.
/// </summary>
internal static class RdpRepeatInput
{
    public static byte[] EncodePress(ushort scancode)
    {
        if (scancode == 0 || scancode > 0x1ff) throw new ArgumentOutOfRangeException(nameof(scancode));
        return [0x04, 0x04, (byte)((scancode & 0x100) != 0 ? 0x02 : 0x00), (byte)scancode];
    }
}
