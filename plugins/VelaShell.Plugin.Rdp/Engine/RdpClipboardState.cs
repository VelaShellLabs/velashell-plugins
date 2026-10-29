using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>States of the transport-independent text-only cliprdr session.</summary>
internal enum RdpClipboardPhase
{
    Disabled,
    Negotiating,
    Ready,
    Faulted
}

/// <summary>Result of matching a remote clipboard response to its request token.</summary>
internal enum RdpClipboardTextResponseResult
{
    Accepted,
    Failed,
    Stale
}

/// <summary>
/// A request token couples a decoded FormatDataResponse with the connection and request
/// that caused it. The token is deliberately not part of the wire protocol.
/// </summary>
internal readonly record struct RdpClipboardRemoteTextRequest(long Session, long Id, byte[] Packet);

/// <summary>
/// Bounded, transport-independent state for the text subset of MS-RDPECLIP.
///
/// This class owns protocol packets, but not a socket, native clipboard, or clipboard
/// thread. Callers feed decoded messages through <see cref="Receive"/> and send the
/// packets returned by <see cref="DrainOutbound"/> through their transport. All calls
/// must be serialized. IsFaulted requires the caller to close this clipboard channel;
/// Disable/Enable is only safe after the old channel and its callbacks are retired.
/// Returned packets/text belong to the caller and must not be sent/applied after disable.
/// </summary>
internal sealed class RdpClipboardState
{
    internal const int MaximumQueuedMessages = 12;

    private readonly Queue<byte[]> _outbound = new();

    private RdpClipboardPhase _phase;
    private bool _peerMonitorReady;
    private bool _peerCapabilities;
    private bool _peerLongNames;
    private bool _remoteOffersUnicode;
    private long _session;
    private long _nextRequestId;
    private RdpClipboardRemoteTextRequest? _outstandingRequest;
    private bool _outstandingResultInvalidated;

    private string? _localText;
    private bool _localOfferSuperseded;
    private long _localVersion;
    private long _advertisedVersion = -1;
    private long? _formatListInFlight;

    internal RdpClipboardPhase Phase => _phase;
    internal bool IsReady => _phase == RdpClipboardPhase.Ready;
    internal bool IsFaulted => _phase == RdpClipboardPhase.Faulted;
    internal bool RemoteOffersUnicodeText => _remoteOffersUnicode;
    internal int PendingOutboundCount => _outbound.Count;
    internal bool LocalAdvertisementSettled => IsReady && _formatListInFlight is null && _advertisedVersion == _localVersion;
    internal bool LocalAdvertisementAccepted { get; private set; }

    /// <summary>
    /// Starts a new connection epoch. MONITOR_READY is server-to-client; the client waits
    /// for it and sends no negotiation packet here.
    /// </summary>
    internal void Enable()
    {
        if (_phase != RdpClipboardPhase.Disabled)
            return;

        _session++;
        _phase = RdpClipboardPhase.Negotiating;
        _peerMonitorReady = false;
        _peerCapabilities = false;
        _peerLongNames = false; // legacy short-name fallback until capabilities arrive
        _remoteOffersUnicode = false;
        _outstandingRequest = null;
        _outstandingResultInvalidated = false;
        _advertisedVersion = -1; // MONITOR_READY requires an initial empty/list advertisement
        _formatListInFlight = null;
    }

    /// <summary>
    /// Stops the session. Queued packets, request tokens, and all text references are
    /// discarded. A token from this epoch cannot match a later connection.
    /// </summary>
    internal void Disable()
    {
        _phase = RdpClipboardPhase.Disabled;
        _peerMonitorReady = false;
        _peerCapabilities = false;
        _peerLongNames = false;
        _remoteOffersUnicode = false;
        _outstandingRequest = null;
        _outstandingResultInvalidated = false;
        _formatListInFlight = null;
        _localText = null;
        _localOfferSuperseded = false;
        LocalAdvertisementAccepted = false;
        _localVersion = 0;
        _advertisedVersion = -1;
        ClearQueue();
    }

    /// <summary>
    /// Sets the text available to answer remote requests. Invalid text is treated as
    /// unavailable rather than retained or sent. The return value reports validity.
    /// </summary>
    internal bool SetLocalText(string? text)
    {
        if (_phase is RdpClipboardPhase.Disabled or RdpClipboardPhase.Faulted)
            return false;

        bool valid = text is null || IsValidText(text);
        string? next = valid ? text : null;
        if (!_localOfferSuperseded && string.Equals(next, _localText, StringComparison.Ordinal)) return valid;
        _localOfferSuperseded = false;
        _localText = next;
        LocalAdvertisementAccepted = false;
        _localVersion++;
        PumpAdvertisement();
        return valid;
    }

    /// <summary>Feeds one already-decoded clipboard message into the state machine.</summary>
    internal void Receive(RdpClipboardProtocol.Message message)
    {
        if (_phase is RdpClipboardPhase.Disabled or RdpClipboardPhase.Faulted)
            return;

        switch (message.Type)
        {
            case RdpClipboardProtocol.MonitorReady:
                RequireEmpty(message, RdpClipboardProtocol.MonitorReady);
                if (_peerMonitorReady)
                    return;
                _peerMonitorReady = true;
                QueueOrFault(RdpClipboardProtocol.CreateCapabilities());
                // Capabilities normally arrives first. If it does not, use the legacy
                // short-name layout and still become ready on MONITOR_READY.
                if (!_peerCapabilities)
                    _peerLongNames = false;
                _phase = RdpClipboardPhase.Ready;
                PumpAdvertisement(); // includes the required initial empty list
                break;

            case RdpClipboardProtocol.Capabilities:
                _peerLongNames = RdpClipboardProtocol.ReadLongFormatNames(message);
                _peerCapabilities = true;
                if (_peerMonitorReady)
                    PumpAdvertisement();
                break;

            case RdpClipboardProtocol.FormatList:
                if (!IsReady)
                    return;
                _remoteOffersUnicode = RdpClipboardProtocol.OffersUnicodeText(message, _peerLongNames);
                _localOfferSuperseded = true;
                LocalAdvertisementAccepted = false;
                // A FormatList supersedes the semantic result, but the wire request must
                // remain outstanding until its response is drained: cliprdr has no IDs.
                if (_outstandingRequest is not null)
                    _outstandingResultInvalidated = true;
                QueueOrFault(RdpClipboardProtocol.Encode(
                    RdpClipboardProtocol.FormatListResponse, RdpClipboardProtocol.ResponseOk, []));
                break;

            case RdpClipboardProtocol.FormatListResponse:
                if (!IsReady || _formatListInFlight is null)
                    return;
                if (message.Payload.Length != 0 ||
                    (message.Flags is not (RdpClipboardProtocol.ResponseOk or RdpClipboardProtocol.ResponseFail)))
                    throw new IOException("Invalid RDP clipboard format-list acknowledgement.");
                long acknowledgedVersion = _formatListInFlight.Value;
                _formatListInFlight = null;
                // A failure is terminal for this exact version. Only a later local text
                // version may trigger another advertisement, avoiding an auto-retry loop.
                _advertisedVersion = acknowledgedVersion;
                LocalAdvertisementAccepted = message.Flags == RdpClipboardProtocol.ResponseOk && acknowledgedVersion == _localVersion;
                PumpAdvertisement();
                break;

            case RdpClipboardProtocol.FormatDataRequest:
                if (!IsReady)
                    return;
                uint requestedFormat = RdpClipboardProtocol.ReadRequestedFormat(message);
                // Unsupported formats receive an explicit failure response. This prevents
                // the peer from waiting forever while keeping every response ordered.
                QueueOrFault(RdpClipboardProtocol.CreateTextResponse(
                    requestedFormat == RdpClipboardProtocol.UnicodeText ? _localText : null));
                break;

            case RdpClipboardProtocol.FormatDataResponse:
                // Responses are consumed through ReceiveRemoteTextResponse with the token
                // returned by RequestRemoteText; there is no wire request ID to decode here.
                break;
        }
    }

    /// <summary>
    /// Requests the peer's Unicode text. At most one wire request can be outstanding. A
    /// null result means the peer did not advertise Unicode, another request is pending,
    /// or this state is not ready.
    /// </summary>
    internal RdpClipboardRemoteTextRequest? RequestRemoteText()
    {
        if (!IsReady || !_remoteOffersUnicode || _outstandingRequest is not null)
            return null;

        var request = new RdpClipboardRemoteTextRequest(_session, ++_nextRequestId,
            RdpClipboardProtocol.CreateTextRequest());
        _outstandingRequest = request;
        if (!QueueOrFault(request.Packet))
        {
            _outstandingRequest = null;
            return null;
        }
        return request;
    }

    /// <summary>
    /// Consumes a response only when its token belongs to the current wire request. A
    /// FormatList can invalidate the semantic result, but its response still drains the
    /// wire request before another request may be issued.
    /// </summary>
    internal RdpClipboardTextResponseResult ReceiveRemoteTextResponse(
        RdpClipboardRemoteTextRequest request,
        RdpClipboardProtocol.Message message,
        out string? text)
    {
        text = null;
        if (_phase is RdpClipboardPhase.Disabled or RdpClipboardPhase.Faulted ||
            _outstandingRequest is not RdpClipboardRemoteTextRequest current ||
            current.Session != request.Session || current.Id != request.Id)
            return RdpClipboardTextResponseResult.Stale;

        bool invalidated = _outstandingResultInvalidated;
        _outstandingRequest = null;
        _outstandingResultInvalidated = false;

        if (invalidated)
            return RdpClipboardTextResponseResult.Stale;

        text = RdpClipboardProtocol.ReadTextResponse(message);
        return text is null ? RdpClipboardTextResponseResult.Failed : RdpClipboardTextResponseResult.Accepted;
    }

    /// <summary>
    /// Returns packets currently ready for transport in exact enqueue order. The returned
    /// arrays are owned by the caller; queued arrays are wiped on Disable or overflow.
    /// </summary>
    internal IReadOnlyList<byte[]> DrainOutbound()
    {
        var result = new List<byte[]>(_outbound.Count);
        while (_outbound.Count != 0)
            result.Add(_outbound.Dequeue());
        PumpAdvertisement();
        return result;
    }

    private void PumpAdvertisement()
    {
        if (!IsReady || _formatListInFlight is not null || _advertisedVersion == _localVersion)
            return;

        long version = _localVersion;
        _formatListInFlight = version;
        if (!QueueOrFault(RdpClipboardProtocol.CreateFormatList(_peerLongNames, _localText is not null)))
            _formatListInFlight = null;
    }

    private bool QueueOrFault(byte[] packet)
    {
        if (_outbound.Count >= MaximumQueuedMessages)
        {
            CryptographicOperations.ZeroMemory(packet);
            Fault();
            return false;
        }
        _outbound.Enqueue(packet);
        return true;
    }

    private void Fault()
    {
        _phase = RdpClipboardPhase.Faulted;
        _peerMonitorReady = false;
        _peerCapabilities = false;
        _peerLongNames = false;
        _remoteOffersUnicode = false;
        _outstandingRequest = null;
        _outstandingResultInvalidated = false;
        _formatListInFlight = null;
        _localText = null;
        ClearQueue();
    }

    private static bool IsValidText(string text)
    {
        if (text.Length > RdpClipboardProtocol.MaximumTextBytes / 2 - 1 || text.Contains('\0'))
            return false;
        try
        {
            _ = new UnicodeEncoding(false, false, true).GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static void RequireEmpty(RdpClipboardProtocol.Message message, ushort type)
    {
        if (message.Type != type || message.Flags != 0 || !message.Payload.IsEmpty)
            throw new IOException("Invalid RDP clipboard control message.");
    }

    private void ClearQueue()
    {
        while (_outbound.Count != 0)
        {
            byte[] packet = _outbound.Dequeue();
            CryptographicOperations.ZeroMemory(packet);
        }
    }
}
