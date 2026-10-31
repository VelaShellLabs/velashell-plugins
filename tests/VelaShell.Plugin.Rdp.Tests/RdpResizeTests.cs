using System.Text;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Offline wire-format checks for dynamic resolution: the drdynvc multiplexer (MS-RDPEDYC), the
/// Display Control monitor-layout PDU (MS-RDPEDISP) and adopting the server's resolution from a
/// Demand Active. These encode the parts most error-prone to hand-roll and independent of a server.
/// </summary>
[TestClass]
public sealed class RdpResizeTests
{
    private const string DisplayControl = "Microsoft::Windows::RDS::DisplayControl";

    private static byte[] CreateRequest(uint channelId, string name) =>
        [((0x01 << 4) | 0x00), (byte)channelId, .. Encoding.ASCII.GetBytes(name), 0x00];

    private static byte[] DisplayCapsData(uint channelId, uint maxMonitors)
    {
        var caps = new ByteWriter(20);
        caps.U32LE(0x00000005); // DISPLAYCONTROL_PDU_TYPE_CAPS
        caps.U32LE(20);         // Length
        caps.U32LE(maxMonitors);
        caps.U32LE(0);          // MaxMonitorAreaFactorA
        caps.U32LE(0);          // MaxMonitorAreaFactorB
        return [((0x03 << 4) | 0x00), (byte)channelId, .. caps.Span];
    }

    [TestMethod]
    public async Task DisplayControlNegotiationResizesViaMonitorLayout()
    {
        var sent = new List<byte[]>();
        var dvc = new DrdynvcChannel((pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, CancellationToken.None);

        await dvc.ProcessAsync([0x50, 0x00, 0x03, 0x00]);          // capabilities request, version 3
        await dvc.ProcessAsync(CreateRequest(3, DisplayControl));  // create the display-control channel (id 3)
        await dvc.ProcessAsync(DisplayCapsData(3, maxMonitors: 1)); // its capabilities (no reply owed yet)
        await dvc.RequestResizeAsync(1366, 769);                   // odd height is rounded down to 768

        Assert.HasCount(3, sent); // caps response, create response, monitor layout

        // Capabilities response echoes the version at Cmd=CAPABILITIES.
        CollectionAssert.AreEqual(new byte[] { 0x50, 0x00, 0x03, 0x00 }, sent[0]);

        // Create response accepts the channel: Cmd=CREATE, same id, CreationStatus 0.
        var create = new ByteReader(sent[1]);
        Assert.AreEqual((byte)((0x01 << 4) | 0x00), create.U8());
        Assert.AreEqual(3u, create.U8());
        Assert.AreEqual(0u, create.U32LE());

        // Monitor layout rides a DATA PDU on the display-control channel.
        var layout = new ByteReader(sent[2]);
        Assert.AreEqual((byte)((0x03 << 4) | 0x00), layout.U8()); // Cmd=DATA, cbId=0
        Assert.AreEqual(3u, layout.U8());                   // channel id
        Assert.AreEqual(0x00000002u, layout.U32LE());             // Header.Type = MONITOR_LAYOUT
        Assert.AreEqual(56u, layout.U32LE());                     // Header.Length
        Assert.AreEqual(40u, layout.U32LE());                     // MonitorLayoutSize
        Assert.AreEqual(1u, layout.U32LE());                      // NumMonitors
        Assert.AreEqual(1u, layout.U32LE());                      // Flags = PRIMARY
        Assert.AreEqual(0u, layout.U32LE());                      // Left
        Assert.AreEqual(0u, layout.U32LE());                      // Top
        Assert.AreEqual(1366u, layout.U32LE());                   // Width
        Assert.AreEqual(768u, layout.U32LE());                    // Height (769 → even)
    }

    [TestMethod]
    public async Task PendingResizeFlushesWhenCapabilitiesArrive()
    {
        var sent = new List<byte[]>();
        var dvc = new DrdynvcChannel((pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, CancellationToken.None);

        await dvc.ProcessAsync(CreateRequest(7, DisplayControl)); // create response
        await dvc.RequestResizeAsync(1920, 1080);                 // buffered: caps not in yet, no send
        Assert.HasCount(1, sent);

        await dvc.ProcessAsync(DisplayCapsData(7, maxMonitors: 1)); // caps arrive → flush the buffered layout
        Assert.HasCount(2, sent);
        var layout = new ByteReader(sent[1]);
        layout.Skip(2 + 4 + 4 + 4 + 4 + 4 + 4 + 4); // to Width
        Assert.AreEqual(1920u, layout.U32LE());
        Assert.AreEqual(1080u, layout.U32LE());
    }

    [TestMethod]
    public async Task UnsupportedDynamicChannelIsDeclined()
    {
        var sent = new List<byte[]>();
        var dvc = new DrdynvcChannel((pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, CancellationToken.None);

        await dvc.ProcessAsync(CreateRequest(5, "AUDIO_PLAYBACK_DVC"));
        Assert.HasCount(1, sent);
        var reply = new ByteReader(sent[0]);
        Assert.AreEqual((byte)((0x01 << 4) | 0x00), reply.U8());
        Assert.AreEqual(5u, reply.U8());
        Assert.AreEqual(0x80004001u, reply.U32LE()); // E_NOTIMPL — the renderer keeps to classic bitmaps
    }

    [TestMethod]
    public void DemandActiveAdoptsServerResolution()
    {
        var w = new ByteWriter(64);
        w.U32LE(0x12345678);          // shareId
        w.U16LE(6);                   // lengthSourceDescriptor
        w.U16LE(0);                   // lengthCombinedCapabilities (ignored)
        w.Bytes("MSTSC\0"u8);         // sourceDescriptor
        w.U16LE(1);                   // numberCapabilities
        w.U16LE(0);                   // pad2octets
        // Bitmap Capability Set (type 2): width 1600 × height 900 at offset 8 of the body.
        w.U16LE(2);                   // capabilitySetType
        w.U16LE(4 + 18);              // lengthCapability
        w.U16LE(16);                  // preferredBitsPerPixel
        w.U16LE(0); w.U16LE(0); w.U16LE(0); // receive{1,4,8}BitsPerPixel
        w.U16LE(1600);                // desktopWidth
        w.U16LE(900);                 // desktopHeight
        w.U16LE(0); w.U16LE(0); w.U16LE(0); // trailing fields

        var r = new ByteReader(w.Span);
        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        Assert.AreEqual(0x12345678u, active.ShareId);
        Assert.AreEqual(1600, active.Width);
        Assert.AreEqual(900, active.Height);
    }
}
