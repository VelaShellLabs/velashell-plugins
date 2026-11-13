using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VelaShell.Rdp;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The window between licensing and the Demand Active PDU, and the client's goodbye. A server still taking a session
/// over from a previous connection may send Share Data PDUs (Set Error Info) or a Deactivate All first; the client
/// waits for the Demand Active instead of failing. Closing sends the MCS Disconnect Provider Ultimatum so the server
/// disconnects the session at once (MS-RDPBCGR 1.3.1.4.1).
/// </summary>
[TestClass]
public sealed class ConnectionPhaseTests
{
    private static readonly byte[] DemandActive = [6, 0, 0x11, 0, 0xEA, 3];

    [TestMethod]
    public async Task SetErrorInfoAndDeactivateAllBeforeTheDemandActiveAreTolerated()
    {
        byte[] input =
        [
            .. LicensingTests.Frame(LicensingTests.Alert(7, 2)),   // STATUS_VALID_CLIENT
            .. LicensingTests.Frame(SetErrorInfo(0x0000000C)),
            .. LicensingTests.Frame(DeactivateAll()),
            .. LicensingTests.Frame(DemandActive)
        ];
        using var wire = new LicensingTests.ScriptedStream(input);
        await using RdpConnection connection = LicensingTests.Connection(wire);
        byte[] actual = await LicensingTests.Receive(connection);
        Assert.AreSequenceEqual(DemandActive, actual);
        Assert.AreEqual(0x0000000Cu, connection.EarlyErrorInfo);
    }

    [TestMethod]
    public async Task AnUnexpectedSharePduNamesItsTypeAndFirstBytes()
    {
        byte[] confirmActive = [10, 0, 0x13, 0, 0xEA, 3, 1, 2, 3, 4];
        using var wire = new LicensingTests.ScriptedStream([.. LicensingTests.Frame(LicensingTests.Alert(7, 2)), .. LicensingTests.Frame(confirmActive)]);
        await using RdpConnection connection = LicensingTests.Connection(wire);
        RdpProtocolException failure = await Assert.ThrowsAsync<RdpProtocolException>(() => LicensingTests.Receive(connection));
        StringAssert.Contains(failure.Message, "PDU type 0x0013");
        StringAssert.Contains(failure.Message, "0A001300EA0301020304");
    }

    [TestMethod]
    public void TheDisconnectProviderUltimatumIsEncodedAsT125AndRecognisedOnReceipt()
    {
        byte[] mcs = McsLayer.BuildDisconnectUltimatum();
        Assert.AreSequenceEqual(new byte[] { 0x21, 0x80 }, mcs);   // choice 8, reason rn-user-requested (3)
        Assert.IsNull(McsLayer.ParseSendDataIndication([2, 0xF0, 0x80, .. mcs]));
    }

    [TestMethod]
    public async Task ClosingAConnectedSessionSendsTheUltimatumBeforeDroppingTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient server = await listener.AcceptTcpClientAsync();

        RdpConnection connection = LicensingTests.Connection(client.GetStream());
        typeof(RdpConnection).GetField("_tcp", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, client);
        await connection.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] received = new byte[9];
        await server.GetStream().ReadExactlyAsync(received, cts.Token);
        // TPKT (length 9), X.224 Data TPDU, MCS Disconnect Provider Ultimatum.
        Assert.AreSequenceEqual(new byte[] { 3, 0, 0, 9, 2, 0xF0, 0x80, 0x21, 0x80 }, received);
        Assert.AreEqual(0, await server.GetStream().ReadAsync(new byte[1], cts.Token), "TCP is closed after the goodbye.");
    }

    private static byte[] SetErrorInfo(uint errorInfo)
    {
        var w = new ByteWriter();
        w.U16LE(22);       // totalLength
        w.U16LE(0x0017);   // PDUTYPE_DATAPDU | TS_PROTOCOL_VERSION
        w.U16LE(0x03EA);   // pduSource
        w.U32LE(0x000103EA);   // shareId
        w.U8(0);           // pad1
        w.U8(1);           // streamId
        w.U16LE(4);        // uncompressedLength
        w.U8(47);          // PDUTYPE2_SET_ERROR_INFO_PDU
        w.U8(0);           // compressedType
        w.U16LE(0);        // compressedLength
        w.U32LE(errorInfo);
        return w.ToArray();
    }

    private static byte[] DeactivateAll()
    {
        byte[] pdu = [0, 0, 0x16, 0, 0xEA, 3, 0xEA, 3, 1, 0, 1, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(pdu, (ushort)pdu.Length);
        return pdu;
    }
}
