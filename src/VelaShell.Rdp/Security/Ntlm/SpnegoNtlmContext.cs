using System.Formats.Asn1;

namespace VelaShell.Rdp.Security.Ntlm;

/// <summary>
/// SPNEGO (RFC 4178, with the Windows rules of MS-SPNG) carrying <see cref="NtlmV2Client" /> as its only mechanism.
/// <list type="number">
/// <item>negTokenInit: mechTypes = { NTLMSSP }, mechToken = NEGOTIATE.</item>
/// <item>The server's negTokenResp (accept-incomplete or request-mic, supportedMech NTLMSSP) carries the CHALLENGE;
/// the client answers with the AUTHENTICATE and a mechListMIC over its DER-encoded mechTypes. The NTLM AUTHENTICATE
/// always carries a MIC, and MS-SPNG then requires the mechListMIC.</item>
/// <item>The server's final negTokenResp (accept-completed with its mechListMIC) is checked by <see cref="Finish" />.
/// In CredSSP it arrives together with the server's public-key echo.</item>
/// </list>
/// <para>
/// MS-SPNG 3.2.5.1: both mechListMICs are computed on a copy of the RC4 state, so the first sealed message in each
/// direction uses the same RC4 state as the mechListMIC did; sequence numbers keep counting (the mechListMIC is
/// number 0, the first sealed message number 1). The client applies this immediately after producing its MIC, so
/// pubKeyAuth — which CredSSP seals before the server's mechListMIC can arrive — is sealed correctly.
/// </para>
/// </summary>
internal sealed class SpnegoNtlmContext : INlaSecurityContext
{
    internal const string SpnegoOid = "1.3.6.1.5.5.2";
    internal const string NtlmOid = "1.3.6.1.4.1.311.2.2.10";

    private enum NegState { AcceptCompleted = 0, AcceptIncomplete = 1, Reject = 2, RequestMic = 3 }

    private readonly NtlmV2Client _ntlm;
    private byte[]? _mechTypes;
    private bool _finished;

    internal SpnegoNtlmContext(NtlmV2Client ntlm) => _ntlm = ntlm;

    public bool IsComplete => _ntlm.IsComplete;

    public byte[] Step(byte[] incoming)
    {
        if (_mechTypes is null)
        {
            if (incoming.Length != 0)
            {
                throw new RdpProtocolException("SPNEGO: the first leg takes no input.");
            }
            return BuildNegTokenInit(_ntlm.Step([]));
        }
        if (IsComplete)
        {
            throw new RdpProtocolException("SPNEGO: unexpected token after the AUTHENTICATE message.");
        }

        NegTokenResp reply = ParseNegTokenResp(incoming);
        if (reply.State == NegState.Reject)
        {
            throw new RdpProtocolException("SPNEGO: the server rejected the negotiation.");
        }
        if (reply.State is not (NegState.AcceptIncomplete or NegState.RequestMic))
        {
            throw new RdpProtocolException($"SPNEGO: unexpected negState {reply.State} before the CHALLENGE.");
        }
        if (reply.SupportedMech is not null and not NtlmOid)
        {
            throw new RdpProtocolException($"SPNEGO: the server selected {reply.SupportedMech}, but only NTLM was offered.");
        }
        byte[] challenge = reply.ResponseToken ?? throw new RdpProtocolException("SPNEGO: the server sent no CHALLENGE.");

        byte[] authenticate = _ntlm.Step(challenge);
        byte[] mechListMic = _ntlm.ClientSealing.WithRc4StatePreserved(sealing => sealing.Sign(_mechTypes));
        return BuildNegTokenResp(authenticate, mechListMic);
    }

    /// <summary>
    /// The server's final negTokenResp. Its mechListMIC, when present, must verify (it is computed on a copy of the
    /// server's RC4 state, like the client's); a missing one is tolerated, as the mechanism list held a single entry.
    /// </summary>
    public void Finish(byte[] serverToken)
    {
        if (!IsComplete || _finished)
        {
            throw new RdpProtocolException("SPNEGO: unexpected final token.");
        }
        NegTokenResp reply = ParseNegTokenResp(serverToken);
        if (reply.State != NegState.AcceptCompleted)
        {
            throw new RdpProtocolException($"SPNEGO: the server ended the negotiation with {reply.State}.");
        }
        if (reply.ResponseToken is { Length: > 0 })
        {
            throw new RdpProtocolException("SPNEGO: NTLM has no token after AUTHENTICATE.");
        }
        if (reply.MechListMic is { } mic && !_ntlm.ServerSealing.WithRc4StatePreserved(sealing => sealing.Verify(_mechTypes, mic)))
        {
            throw new RdpProtocolException("SPNEGO: the server's mechListMIC did not verify.");
        }
        _finished = true;
    }

    public byte[] Seal(byte[] plaintext) => _ntlm.Seal(plaintext);

    public byte[] Unseal(byte[] wrapped) => _ntlm.Unseal(wrapped);

    public void Dispose() => _ntlm.Dispose();

    // ---- DER (RFC 4178 4.2) ----

    private byte[] BuildNegTokenInit(byte[] mechToken)
    {
        var mechTypes = new AsnWriter(AsnEncodingRules.DER);
        using (mechTypes.PushSequence())
        {
            mechTypes.WriteObjectIdentifier(NtlmOid);
        }
        _mechTypes = mechTypes.Encode();

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 0)))
        {
            writer.WriteObjectIdentifier(SpnegoOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))   // negTokenInit
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    writer.WriteEncodedValue(_mechTypes);
                }
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    writer.WriteOctetString(mechToken);
                }
            }
        }
        return writer.Encode();
    }

    private static byte[] BuildNegTokenResp(byte[] responseToken, byte[] mechListMic)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))   // negTokenResp
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                writer.WriteOctetString(responseToken);
            }
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
            {
                writer.WriteOctetString(mechListMic);
            }
        }
        return writer.Encode();
    }

    private readonly record struct NegTokenResp(NegState? State, string? SupportedMech, byte[]? ResponseToken, byte[]? MechListMic);

    private static NegTokenResp ParseNegTokenResp(byte[] token)
    {
        try
        {
            var outer = new AsnReader(token, AsnEncodingRules.DER);
            AsnReader body = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
            outer.ThrowIfNotEmpty();
            NegState? state = null;
            string? mech = null;
            byte[]? response = null, mic = null;
            int last = -1;
            while (body.HasData)
            {
                Asn1Tag tag = body.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue <= last || tag.TagValue > 3)
                {
                    throw new AsnContentException("Unexpected negTokenResp element.");
                }
                last = tag.TagValue;
                AsnReader field = body.ReadSequence(tag);
                switch (tag.TagValue)
                {
                    case 0: state = field.ReadEnumeratedValue<NegState>(); break;
                    case 1: mech = field.ReadObjectIdentifier(); break;
                    case 2: response = field.ReadOctetString(); break;
                    case 3: mic = field.ReadOctetString(); break;
                }
                field.ThrowIfNotEmpty();
            }
            return new NegTokenResp(state, mech, response, mic);
        }
        catch (AsnContentException ex)
        {
            throw new RdpProtocolException("SPNEGO: the server's negTokenResp is malformed.", ex);
        }
    }
}
