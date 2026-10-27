using System.Formats.Asn1;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Devolutions.IronRdp;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Rdp.Engine;

internal sealed class RdpSessionFactory : IRdpSessionFactory
{
    public static RdpSessionFactory Instance { get; } = new();
    public async Task<IRdpSession> ConnectAsync(RdpConnectionOptions options, RdpCredentials credentials, CancellationToken cancellationToken = default)
    {
        RdpLimits.DesktopBytes(options.DesktopWidth, options.DesktopHeight);
        if (options.ConnectTimeout < TimeSpan.FromSeconds(1) || options.ConnectTimeout > TimeSpan.FromMinutes(2))
            throw new ProtocolConnectionException("The RDP connection timeout must be between 1 and 120 seconds.");
        using var timeout = new CancellationTokenSource(options.ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var tcp = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        ActiveStage? active = null;
        DecodedImage? image = null;
        bool transferred = false;
        string phase = "TCP connection";
        try
        {
            await tcp.ConnectAsync(options.TransportHost, options.TransportPort, linked.Token).ConfigureAwait(false);
            using var builder = ConfigBuilder.New();
            builder.WithUsernameAndPassword(credentials.Username, credentials.GetPasswordForNative());
            if (credentials.Domain.Length != 0) builder.SetDomain(credentials.Domain);
            builder.SetDesktopSize(height: checked((ushort)options.DesktopHeight), width: checked((ushort)options.DesktopWidth));
            builder.SetEnableTls(true);
            builder.SetEnableCredssp(true);
            builder.SetClientName("VelaShell");
            builder.SetClientDir("C:\\Windows\\System32\\mstscax.dll");
            builder.SetKeyboardType(KeyboardType.IbmEnhanced);
            builder.SetKeyboardLayout(0x0409);
            builder.SetKeyboardFunctionalKeysCount(12);
            builder.SetEnableServerPointer(true);
            builder.SetPointerSoftwareRendering(false);
            using var performance = PerformanceFlags.NewDefault();
            builder.SetPerformanceFlags(performance);
            using var config = builder.Build();
            using var connector = ClientConnector.New(config, tcp.Client.LocalEndPoint!.ToString()!);
            using var output = WriteBuf.New();
            // Offer only HYBRID, never TLS-only/legacy RDP or automatic fallback.
            byte[] initial;
            using (var written = connector.InitiateWithSecurityProtocol(2, output)) initial = ReadWritten(written, output);
            await WritePacketAsync(tcp.GetStream(), initial, linked.Token).ConfigureAwait(false);
            phase = "RDP security negotiation";
            byte[] confirm = await RdpFraming.ReadRdpAsync(tcp.GetStream(), linked.Token).ConfigureAwait(false);
            output.Clear();
            byte[] negotiationResponse;
            using (var written = connector.Step(confirm, output)) negotiationResponse = ReadWritten(written, output);
            await WritePacketAsync(tcp.GetStream(), negotiationResponse, linked.Token).ConfigureAwait(false);
            connector.EnsureSelectedHybrid();
            if (!connector.ShouldPerformSecurityUpgrade()) throw new ProtocolConnectionException("The server did not select TLS with NLA.");
            using var trust = new RdpTlsTrust(options.TrustedSha256);
            tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (sender, certificate, chain, errors) =>
            {
                phase = "TLS certificate validation";
                return trust.Validate(sender, certificate, chain, errors);
            });
            phase = "TLS handshake";
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.TargetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                    AllowRenegotiation = false,
                    AllowTlsResume = false
                }, linked.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException) when (trust.Rejected) { throw trust.Rejection(); }
            connector.MarkSecurityUpgradeAsDone();
            if (!connector.ShouldPerformCredssp()) throw new ProtocolConnectionException("The server did not enter NLA authentication.");
            phase = "NLA authentication";
            await PerformCredsspAsync(connector, options.TargetHost, trust.ServerPublicKey, tls, output, linked.Token).ConfigureAwait(false);
            connector.MarkCredsspAsDone();
            phase = "desktop activation";
            for (int steps = 0; !IsTerminal(connector); steps++)
            {
                if (steps >= 256) throw new ProtocolConnectionException("The RDP activation sequence exceeded its step budget.");
                await StepAsync(connector, output, tls, linked.Token).ConfigureAwait(false);
            }
            using var state = connector.ConsumeAndCastToClientConnectorState();
            if (state.GetEnumType() != ClientConnectorStateType.Connected) throw new ProtocolConnectionException("The RDP desktop did not activate.");
            using var result = state.GetConnectedResult();
            int width, height;
            using (var size = result.GetDesktopSize()) { width = size.GetWidth(); height = size.GetHeight(); }
            RdpLimits.DesktopBytes(width, height);
            image = DecodedImage.New(PixelFormat.RgbA32, checked((ushort)width), checked((ushort)height));
            active = ActiveStage.New(result); // Consumes the result's Rust value, not the owning wrapper.
            var session = new RdpSession(tcp, tls, active, image, width, height);
            transferred = true;
            active = null; image = null; tls = null;
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw new TimeoutException($"The RDP connection timed out during {phase}."); }
        catch (IronRdpException ex) { throw SanitizeNative(ex); }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or CryptographicException)
        { throw new ProtocolConnectionException("The RDP network, TLS or protocol connection failed."); }
        finally
        {
            if (!transferred) tcp.Dispose();
            try { if (tls is not null) await tls.DisposeAsync().ConfigureAwait(false); }
            finally { try { image?.Dispose(); } finally { active?.Dispose(); } }
        }
    }

    internal static Exception SanitizeNative(IronRdpException exception)
    {
        using var error = exception.Inner;
        // The public CredsspError/AccessDenied buckets are not sufficient to diagnose bad credentials.
        return new ProtocolConnectionException($"The RDP protocol operation failed ({error.GetKind()}).");
    }
    internal static bool IsTerminal(ClientConnector connector)
    {
        using var state = connector.GetDynState();
        return state.IsTerminal();
    }
    internal static async Task StepAsync(ISequence sequence, WriteBuf output, Stream stream, CancellationToken ct)
    {
        bool needsInput;
        using (var hint = sequence.NextPduHint()) needsInput = hint is not null;
        byte[]? input = needsInput ? await RdpFraming.ReadRdpAsync(stream, ct).ConfigureAwait(false) : null;
        output.Clear();
        byte[] response;
        using (var written = input is null ? sequence.StepNoInput(output) : sequence.Step(input, output)) response = ReadWritten(written, output);
        await WritePacketAsync(stream, response, ct).ConfigureAwait(false);
    }
    internal static byte[] ReadWritten(Written written, WriteBuf output)
    {
        if (written.GetWrittenType() == WrittenType.Nothing) return [];
        using var optional = written.GetSize();
        int length = checked((int)optional.Get());
        RdpFraming.ValidateLength(length, 0);
        var response = new byte[length];
        output.ReadIntoBuf(response);
        return response;
    }
    internal static async Task WritePacketAsync(Stream stream, byte[] response, CancellationToken ct)
    {
        if (response.Length == 0) return;
        try { await stream.WriteAsync(response, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(response); }
    }
    private static async Task PerformCredsspAsync(ClientConnector connector, string server, byte[] publicKey, Stream stream, WriteBuf output, CancellationToken ct)
    {
        using var init = CredsspSequence.Init(connector, server, publicKey, null); // null explicitly selects NTLM, no KDC discovery.
        using var sequence = init.GetCredsspSequence();
        TsRequest? request = init.GetTsRequest();
        try
        {
            for (int step = 0; step < 32; step++)
            {
                ClientState clientState;
                // Generator borrows sequence; no borrowed native object survives the synchronous block.
                using (var generator = sequence.ProcessTsRequest(request!))
                using (var state = generator.Start())
                {
                    if (state.IsSuspended()) throw new ProtocolConnectionException("External Kerberos/KDC authentication is not enabled for this RDP session.");
                    if (!state.IsCompleted()) throw new ProtocolConnectionException("Invalid NLA authentication state.");
                    clientState = state.GetClientStateIfCompleted();
                }
                request.Dispose(); request = null;
                byte[] response;
                output.Clear();
                using (clientState)
                using (var written = sequence.HandleProcessResult(clientState, output)) response = ReadWritten(written, output);
                await WritePacketAsync(stream, response, ct).ConfigureAwait(false);
                bool more;
                using (var hint = sequence.NextPduHint()) more = hint is not null;
                if (!more) return;
                byte[] pdu = await RdpFraming.ReadCredsspAsync(stream, ct).ConfigureAwait(false);
                try
                {
                    ThrowCredentialStatus(pdu);
                    request = sequence.DecodeServerMessage(pdu);
                }
                finally { CryptographicOperations.ZeroMemory(pdu); }
                if (request is null) return;
            }
            throw new ProtocolConnectionException("The NLA authentication sequence exceeded its step budget.");
        }
        finally { request?.Dispose(); }
    }
    internal static void ThrowCredentialStatus(byte[] pdu)
    {
        try
        {
            var reader = new AsnReader(pdu, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                Asn1Tag tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 4)
                {
                    var field = sequence.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true));
                    if (field.TryReadUInt32(out uint status) && status is 0xc0000064 or 0xc000006a or 0xc000006d)
                        throw new ProtocolAuthenticationException("The RDP username or password was rejected.");
                }
                else sequence.ReadEncodedValue();
            }
        }
        catch (AsnContentException) { throw new ProtocolConnectionException("Invalid NLA authentication message."); }
    }
}

internal static class RdpLimits
{
    public static int DesktopBytes(int width, int height)
    {
        if (width is < 1 or > 7680 || height is < 1 or > 4320 || (long)width * height > 33_177_600)
            throw new ProtocolConnectionException("The RDP desktop dimensions exceed the session memory budget.");
        return checked(width * height * 4);
    }
}
