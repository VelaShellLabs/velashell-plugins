using System.Buffers.Binary;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpClipboardStateTests
{
    [TestMethod]
    public void ClientWaitsForServerMonitorReadyAndSendsCapabilitiesBeforeInitialEmptyList()
    {
        var state = new RdpClipboardState();
        state.Enable();
        Assert.AreEqual(RdpClipboardPhase.Negotiating, state.Phase);
        Assert.AreEqual(0, state.DrainOutbound().Count);
        state.Receive(Message(RdpClipboardProtocol.CreateCapabilities()));
        Assert.IsFalse(state.IsReady);
        state.Receive(MonitorReady());
        Assert.IsTrue(state.IsReady);

        var packets = state.DrainOutbound();
        Assert.AreEqual(2, packets.Count);
        Assert.AreEqual(RdpClipboardProtocol.Capabilities, Message(packets[0]).Type);
        Assert.AreEqual(RdpClipboardProtocol.FormatList, Message(packets[1]).Type);
        Assert.IsTrue(Message(packets[1]).Payload.IsEmpty);
        state.Receive(MonitorReady());
        Assert.AreEqual(0, state.DrainOutbound().Count);
    }

    [TestMethod]
    public void LegacyServerWithoutCapabilitiesUsesShortNames()
    {
        var state = new RdpClipboardState();
        state.Enable();
        state.SetLocalText("legacy");
        state.Receive(MonitorReady());
        Assert.IsTrue(state.IsReady);
        var packets = state.DrainOutbound();
        Assert.AreEqual(36, Message(packets[1]).Payload.Length);
        Assert.IsTrue(RdpClipboardProtocol.OffersUnicodeText(Message(packets[1]), false));
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(false, true)));
        Assert.IsNotNull(state.RequestRemoteText());
    }

    [TestMethod]
    public void UnicodeRequestRequiresRemoteAdvertisementAndOnlyOneWireRequestIsAllowed()
    {
        var state = ReadyState();
        Assert.IsFalse(state.RemoteOffersUnicodeText);
        Assert.IsNull(state.RequestRemoteText());
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        var request = state.RequestRemoteText();
        Assert.IsNotNull(request);
        Assert.AreEqual(RdpClipboardProtocol.FormatDataRequest, Message(request.Value.Packet).Type);
        Assert.IsNull(state.RequestRemoteText());
    }

    [TestMethod]
    public void LocalTextAdvertisementsCoalesceUntilFormatListAcknowledgement()
    {
        var state = ReadyState();
        state.SetLocalText("one");
        state.SetLocalText("two");
        state.SetLocalText("three");
        var packets = state.DrainOutbound();
        Assert.AreEqual(1, packets.Count);
        Assert.IsTrue(RdpClipboardProtocol.OffersUnicodeText(Message(packets[0]), true));
        state.SetLocalText("latest");
        Assert.AreEqual(0, state.DrainOutbound().Count);
        state.Receive(Acknowledge());
        Assert.AreEqual(1, state.DrainOutbound().Count);
        state.Receive(Acknowledge());
        Assert.AreEqual(0, state.DrainOutbound().Count);
        state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        Assert.AreEqual("latest", RdpClipboardProtocol.ReadTextResponse(Message(state.DrainOutbound()[0])));
    }

    [TestMethod]
    public void FailedAcknowledgementDoesNotRetryUnchangedAdvertisement()
    {
        var state = ReadyState();
        state.SetLocalText("one");
        state.DrainOutbound();
        state.Receive(Acknowledge(false));
        Assert.AreEqual(0, state.DrainOutbound().Count);
        Assert.AreEqual(0, state.DrainOutbound().Count);
        state.SetLocalText("two");
        Assert.AreEqual(1, state.DrainOutbound().Count);
    }

    [TestMethod]
    public void FailedAcknowledgementStillAllowsCoalescedNewVersion()
    {
        var state = ReadyState();
        state.SetLocalText("one");
        state.DrainOutbound();
        state.SetLocalText("two");
        state.Receive(Acknowledge(false));
        Assert.AreEqual(1, state.DrainOutbound().Count);
        state.Receive(Acknowledge(false));
        Assert.AreEqual(0, state.DrainOutbound().Count);
    }

    [TestMethod]
    public void NewRemoteListInvalidatesResultButBlocksNewRequestUntilOldWireResponseDrains()
    {
        var state = ReadyState();
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        RdpClipboardRemoteTextRequest first = state.RequestRemoteText()!.Value;
        state.DrainOutbound();
        for (int i = 0; i < 4; i++)
            state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        Assert.IsNull(state.RequestRemoteText());
        Assert.AreEqual(RdpClipboardTextResponseResult.Stale,
            state.ReceiveRemoteTextResponse(first, Message(RdpClipboardProtocol.CreateTextResponse("old")), out string? stale));
        Assert.IsNull(stale);

        RdpClipboardRemoteTextRequest second = state.RequestRemoteText()!.Value;
        Assert.AreEqual(RdpClipboardTextResponseResult.Stale,
            state.ReceiveRemoteTextResponse(first, Message(RdpClipboardProtocol.CreateTextResponse("duplicate")), out _));
        Assert.IsNull(state.RequestRemoteText());
        Assert.AreEqual(RdpClipboardTextResponseResult.Accepted,
            state.ReceiveRemoteTextResponse(second, Message(RdpClipboardProtocol.CreateTextResponse("fresh")), out string? text));
        Assert.AreEqual("fresh", text);
    }

    [TestMethod]
    public void RemovalOfRemoteUnicodeOfferDrainsRequestWithoutIssuingAnother()
    {
        var state = ReadyState();
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        var request = state.RequestRemoteText()!.Value;
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, false)));
        Assert.AreEqual(RdpClipboardTextResponseResult.Stale,
            state.ReceiveRemoteTextResponse(request, Message(RdpClipboardProtocol.CreateTextResponse("old")), out _));
        Assert.IsNull(state.RequestRemoteText());
    }

    [TestMethod]
    public void UnsupportedFormatsReceiveFailureInWireOrderWithEveryFormatListAcknowledgement()
    {
        var state = ReadyState();
        state.SetLocalText("answer");
        state.DrainOutbound();
        byte[] unsupported = RdpClipboardProtocol.CreateTextRequest();
        BinaryPrimitives.WriteUInt32LittleEndian(unsupported.AsSpan(8), 1);
        state.Receive(Message(unsupported));
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, false)));
        var packets = state.DrainOutbound();
        Assert.AreEqual(4, packets.Count);
        Assert.AreEqual(RdpClipboardProtocol.FormatDataResponse, Message(packets[0]).Type);
        Assert.IsNull(RdpClipboardProtocol.ReadTextResponse(Message(packets[0])));
        Assert.AreEqual(RdpClipboardProtocol.FormatListResponse, Message(packets[1]).Type);
        Assert.AreEqual("answer", RdpClipboardProtocol.ReadTextResponse(Message(packets[2])));
        Assert.AreEqual(RdpClipboardProtocol.FormatListResponse, Message(packets[3]).Type);
    }

    [TestMethod]
    public void OverflowExplicitlyFaultsChannelAndClearsAllQueuedResponses()
    {
        var state = ReadyState();
        state.SetLocalText("secret");
        state.DrainOutbound();
        for (int i = 0; i < RdpClipboardState.MaximumQueuedMessages; i++)
            state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        Assert.AreEqual(RdpClipboardState.MaximumQueuedMessages, state.PendingOutboundCount);
        state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        Assert.IsTrue(state.IsFaulted);
        Assert.IsFalse(state.IsReady);
        Assert.AreEqual(0, state.PendingOutboundCount);
        Assert.AreEqual(0, state.DrainOutbound().Count);
        Assert.IsFalse(state.SetLocalText("other"));
        state.Enable();
        Assert.IsTrue(state.IsFaulted);
        state.Disable();
        state.Enable();
        Assert.AreEqual(RdpClipboardPhase.Negotiating, state.Phase);
    }

    [TestMethod]
    public void FormatListAcknowledgementFloodAlsoFaultsInsteadOfCoalescing()
    {
        var state = ReadyState();
        for (int i = 0; i < RdpClipboardState.MaximumQueuedMessages; i++)
            state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        Assert.AreEqual(RdpClipboardState.MaximumQueuedMessages, state.PendingOutboundCount);
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        Assert.IsTrue(state.IsFaulted);
    }

    [TestMethod]
    public void DisableClearsPacketsTextAndRejectsOldTokenAfterReconnect()
    {
        var state = ReadyState();
        state.SetLocalText("secret");
        state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        var old = state.RequestRemoteText()!.Value;
        state.Disable();
        Assert.AreEqual(0, state.PendingOutboundCount);
        Assert.IsFalse(state.SetLocalText("disabled"));
        state.Receive(MonitorReady());
        Assert.AreEqual(0, state.DrainOutbound().Count);
        state.Enable();
        state.Receive(Message(RdpClipboardProtocol.CreateCapabilities()));
        state.Receive(MonitorReady());
        state.DrainOutbound();
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        var current = state.RequestRemoteText()!.Value;
        Assert.AreEqual(RdpClipboardTextResponseResult.Stale,
            state.ReceiveRemoteTextResponse(old, Message(RdpClipboardProtocol.CreateTextResponse("late")), out _));
        Assert.IsNull(state.RequestRemoteText());
        Assert.AreEqual(RdpClipboardTextResponseResult.Accepted,
            state.ReceiveRemoteTextResponse(current, Message(RdpClipboardProtocol.CreateTextResponse("current")), out _));
        state.DrainOutbound();
        state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
        Assert.IsNull(RdpClipboardProtocol.ReadTextResponse(Message(state.DrainOutbound()[0])));
    }

    [TestMethod]
    public void InvalidAndOversizedLocalTextWithdrawsTextInsteadOfLeakingPreviousValue()
    {
        foreach (string invalid in new[] { "a\0b", "\ud800", new string('x', RdpClipboardProtocol.MaximumTextBytes / 2) })
        {
            var state = ReadyState();
            state.SetLocalText("previous");
            state.DrainOutbound();
            Assert.IsFalse(state.SetLocalText(invalid));
            state.Receive(Acknowledge());
            Assert.IsTrue(Message(state.DrainOutbound()[0]).Payload.IsEmpty);
            state.Receive(Message(RdpClipboardProtocol.CreateTextRequest()));
            Assert.IsNull(RdpClipboardProtocol.ReadTextResponse(Message(state.DrainOutbound()[0])));
        }
    }

    [TestMethod]
    public void FailedRemoteResponseReleasesOutstandingRequestWithoutReturningText()
    {
        var state = ReadyState();
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        var request = state.RequestRemoteText()!.Value;
        Assert.AreEqual(RdpClipboardTextResponseResult.Failed,
            state.ReceiveRemoteTextResponse(request, Message(RdpClipboardProtocol.CreateTextResponse(null)), out string? text));
        Assert.IsNull(text);
        Assert.IsNotNull(state.RequestRemoteText());
    }

    [TestMethod]
    public void RemoteCopyRequiresReadvertisingEvenIdenticalLocalText()
    {
        var state = ReadyState();
        state.SetLocalText("A"); state.DrainOutbound(); state.Receive(Acknowledge());
        Assert.IsTrue(state.LocalAdvertisementAccepted);
        state.Receive(Message(RdpClipboardProtocol.CreateFormatList(true, true)));
        state.DrainOutbound();
        Assert.IsFalse(state.LocalAdvertisementAccepted);
        state.SetLocalText("A");
        Assert.IsFalse(state.LocalAdvertisementSettled);
        Assert.AreEqual(1, state.DrainOutbound().Count);
        state.Receive(Acknowledge());
        Assert.IsTrue(state.LocalAdvertisementAccepted);
    }

    private static RdpClipboardProtocol.Message Message(byte[] packet) => RdpClipboardProtocol.Decode(packet);
    private static RdpClipboardProtocol.Message MonitorReady() => Message(
        RdpClipboardProtocol.Encode(RdpClipboardProtocol.MonitorReady, 0, []));
    private static RdpClipboardProtocol.Message Acknowledge(bool success = true) => Message(
        RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatListResponse,
            success ? RdpClipboardProtocol.ResponseOk : RdpClipboardProtocol.ResponseFail, []));

    private static RdpClipboardState ReadyState()
    {
        var state = new RdpClipboardState();
        state.Enable();
        state.Receive(Message(RdpClipboardProtocol.CreateCapabilities()));
        state.Receive(MonitorReady());
        state.DrainOutbound();
        state.Receive(Acknowledge());
        return state;
    }
}
