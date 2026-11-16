using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using VelaShell.Vnc.Audio;
using VelaShell.Vnc.FileTransfer;
using VelaShell.Vnc.Graphics;
using VelaShell.Vnc.Security;
using VelaShell.Vnc.Wire;

namespace VelaShell.Vnc;

/// <summary>
/// A VNC client session (RFC 6143): handshake, then a receive loop that decodes framebuffer updates into a local
/// framebuffer and requests the next incremental update after each one. Input and clipboard messages are written under
/// one lock, each in a single write.
/// </summary>
public sealed class VncSession : IVncSession
{
    // RFC 6143 §7.1.2 security types, plus VeNCrypt (19) and Apple Remote Desktop (30) from the IANA registry.
    internal const int SecNone = 1, SecVnc = 2, SecVeNCrypt = 19, SecArd = 30;
    internal const int VeNCryptPlain = 256, VeNCryptX509None = 260, VeNCryptX509Vnc = 261, VeNCryptX509Plain = 262;
    private const int MaxReason = 64 * 1024, MaxCutText = 20 * 1024 * 1024;

    private readonly VncClientOptions _options;
    private readonly Stream _transport;
    private Stream _stream;
    private readonly RfbReader _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly VncFramebuffer _framebuffer = new();
    private readonly ExtendedClipboard _clipboard = new();
    private readonly CancellationTokenSource _lifetime = new();
    private RectangleDecoder? _decoder;
    private Task? _receive;
    private VncConnectStage _stage = VncConnectStage.ProtocolVersion;
    private int _minor;
    private string _security = "";
    private bool _tls;
    private string _desktopName = "";
    private uint _screenId, _screenFlags;
    private volatile bool _visible = true, _requestOutstanding, _connected;
    private int _disposed;
    private volatile string? _abortReason;
    private readonly UltraFileTransfer? _files;
    private readonly Lock _audioGate = new();
    private VncAudioPlayer? _audio;
    private Exception? _audioFailure;
    private Action<Exception>? _audioHandlers;
    private const int MaxAudioData = 1024 * 1024;

    private VncSession(VncClientOptions options, Stream transport)
    {
        _options = options;
        _transport = transport;
        _stream = transport;
        _reader = new RfbReader(transport);
        _files = options.FileTransfer ? new UltraFileTransfer(SendConnectedAsync, options.Log) : null;
    }

    public event Action<Exception>? AudioFailed
    {
        add
        {
            Exception? failure;
            lock (_audioGate)
            {
                _audioHandlers += value;
                failure = _audioFailure;
            }
            if (failure is not null)
            {
                try { value?.Invoke(failure); } catch { /* observers cannot break the session */ }
            }
        }
        remove { lock (_audioGate) { _audioHandlers -= value; } }
    }

    public bool AudioActive { get; private set; }
    public IVncFileTransfer? FileTransfer => _files;
    internal UltraFileTransfer? Files => _files;

    public event Action? FrameAvailable;
    public event Action<Exception?>? Disconnected;
    public event Action<VncPointer>? PointerChanged;
    public event Action<string>? ClipboardReceived;
    public event Action? InfoChanged;

    public bool IsConnected => _connected;
    public VncSessionInfo Info => new($"3.{_minor}", _security, _desktopName, _tls);
    public string LastEncoding { get; private set; } = "";
    public bool CanResize { get; private set; }
    public bool SupportsScanCodes { get; private set; }

    /// <summary>The desktop size from ServerInit (and later resizes).</summary>
    public int DesktopWidth => _framebuffer.Width;
    public int DesktopHeight => _framebuffer.Height;

    /// <summary>Connects, authenticates and starts receiving. Failures are <see cref="VncConnectionException"/>,
    /// <see cref="VncAuthenticationException"/>, <see cref="VncSecurityUnsupportedException"/> or
    /// <see cref="VncCertificateRejectedException"/>.</summary>
    public static async Task<VncSession> ConnectAsync(VncClientOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return await ConnectOnceAsync(options, avoidVeNCrypt: false, ct).ConfigureAwait(false);
        }
        catch (VeNCryptUnusableException ex) when (options.Security == VncSecurity.Auto)
        {
            // Auto chose VeNCrypt, but the server only offers subtypes this client cannot complete (anonymous TLS, which
            // .NET's TLS stacks do not implement): connect again and take the next type the server offered.
            options.Log?.Invoke($"VNC {options.Host}:{options.Port}: {ex.Message}; reconnecting with another security type.");
            return await ConnectOnceAsync(options, avoidVeNCrypt: true, ct).ConfigureAwait(false);
        }
    }

    private static async Task<VncSession> ConnectOnceAsync(VncClientOptions options, bool avoidVeNCrypt, CancellationToken ct)
    {
        Stream stream;
        try
        {
            stream = options.Dialer is { } dial
                ? await dial(options.Host, options.Port, ct).ConfigureAwait(false)
                : await DialDirectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VncConnectionException(VncConnectStage.Transport, ex.Message, ex);
        }

        var session = new VncSession(options, stream);
        try
        {
            await session.HandshakeAsync(avoidVeNCrypt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException or VncConnectionException or VncAuthenticationException
                or VncSecurityUnsupportedException or VncCertificateRejectedException or VeNCryptUnusableException)
            {
                throw;
            }
            throw new VncConnectionException(session._stage, ex is EndOfStreamException ? "the server closed the connection" : ex.Message, ex);
        }

        session.Start();
        return session;
    }

    private static async Task<Stream> DialDirectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- handshake

    private async Task HandshakeAsync(bool avoidVeNCrypt, CancellationToken ct)
    {
        _stage = VncConnectStage.ProtocolVersion;
        byte[] version = await _reader.BytesAsync(12, ct).ConfigureAwait(false);
        string text = Encoding.ASCII.GetString(version);
        if (!text.StartsWith("RFB ", StringComparison.Ordinal) || text[7] != '.' || text[11] != '\n'
            || !int.TryParse(text.AsSpan(4, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(text.AsSpan(8, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            throw new VncConnectionException(VncConnectStage.ProtocolVersion, "the server is not a VNC (RFB) server");
        }

        // 3.889 is Apple's 3.8; 3.4–3.6 (UltraVNC and friends) speak 3.3. Anything newer than 3.8 is answered with 3.8.
        _minor = major > 3 || minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        await SendAsync(Encoding.ASCII.GetBytes($"RFB 003.00{_minor}\n"), ct).ConfigureAwait(false);

        _stage = VncConnectStage.Security;
        int type;
        if (_minor == 3)
        {
            type = (int)await _reader.U32Async(ct).ConfigureAwait(false);
            if (type == 0)
            {
                throw new VncConnectionException(VncConnectStage.Security, await ReasonAsync(ct).ConfigureAwait(false));
            }
            if (Choose([type], avoidVeNCrypt) != type)
            {
                throw Unsupported([type]);
            }
        }
        else
        {
            int count = await _reader.U8Async(ct).ConfigureAwait(false);
            if (count == 0)
            {
                throw new VncConnectionException(VncConnectStage.Security, await ReasonAsync(ct).ConfigureAwait(false));
            }

            byte[] offered = await _reader.BytesAsync(count, ct).ConfigureAwait(false);
            int[] types = [.. offered.Select(static b => (int)b)];
            type = Choose(types, avoidVeNCrypt);
            if (type == 0)
            {
                throw Unsupported(types);
            }
            await SendAsync(new[] { (byte)type }, ct).ConfigureAwait(false);
        }

        _stage = VncConnectStage.Authentication;
        bool result = true;
        switch (type)
        {
            case SecNone:
                _security = "None";
                result = _minor == 8;   // 3.3 and 3.7 send no SecurityResult for None
                break;
            case SecVnc:
                _security = "VNC";
                await VncChallengeAsync(ct).ConfigureAwait(false);
                break;
            case SecArd:
                _security = "Apple Remote Desktop";
                await AppleAsync(ct).ConfigureAwait(false);
                break;
            case SecVeNCrypt:
                await VeNCryptAsync(ct).ConfigureAwait(false);
                break;
        }

        if (result)
        {
            uint status = await _reader.U32Async(ct).ConfigureAwait(false);
            if (status != 0)
            {
                string? reason = _minor == 8 ? await ReasonAsync(ct).ConfigureAwait(false) : null;
                throw new VncAuthenticationException(status == 2 && reason is null ? "too many authentication attempts" : reason);
            }
        }

        _stage = VncConnectStage.Initialization;
        await SendAsync(new[] { _options.Shared ? (byte)1 : (byte)0 }, ct).ConfigureAwait(false);
        int width = await _reader.U16Async(ct).ConfigureAwait(false), height = await _reader.U16Async(ct).ConfigureAwait(false);
        await _reader.SkipAsync(16, ct).ConfigureAwait(false);   // the server's pixel format: replaced below
        _desktopName = await StringAsync(ct).ConfigureAwait(false);
        _framebuffer.Resize(Math.Max(1, width), Math.Max(1, height));
        _decoder = new RectangleDecoder(_framebuffer, _options.JpegDecoder);

        await SendAsync(SetPixelFormat(), ct).ConfigureAwait(false);
        await SendAsync(SetEncodings(), ct).ConfigureAwait(false);
        await RequestUpdateAsync(incremental: false, ct).ConfigureAwait(false);
        _connected = true;
        _options.Log?.Invoke($"VNC {_options.Host}:{_options.Port}: RFB 3.{_minor}, security {_security}{(_tls ? " over TLS" : "")}, desktop {width}×{height} \"{_desktopName}\".");
    }

    /// <summary>
    /// The security type to use (0 = none acceptable). Auto prefers VeNCrypt (TLS with a certificate), then Apple
    /// Remote Desktop when a user name is given, then VNC authentication, then None, then ARD without a user name (it
    /// will ask for one).
    /// </summary>
    internal int Choose(IReadOnlyList<int> offered, bool avoidVeNCrypt)
    {
        bool Has(int t) => offered.Contains(t);
        return _options.Security switch
        {
            VncSecurity.None => Has(SecNone) ? SecNone : 0,
            VncSecurity.VncAuthentication => Has(SecVnc) ? SecVnc : 0,
            VncSecurity.VeNCrypt => Has(SecVeNCrypt) && _minor >= 7 ? SecVeNCrypt : 0,
            VncSecurity.AppleRemoteDesktop => Has(SecArd) && _minor >= 7 ? SecArd : 0,
            _ when Has(SecVeNCrypt) && _minor >= 7 && !avoidVeNCrypt => SecVeNCrypt,
            _ when Has(SecArd) && _minor >= 7 && _options.Username.Length > 0 => SecArd,
            _ when Has(SecVnc) => SecVnc,
            _ when Has(SecNone) => SecNone,
            _ when Has(SecArd) && _minor >= 7 => SecArd,
            _ => 0
        };
    }

    private VncSecurityUnsupportedException Unsupported(IReadOnlyList<int> offered) => new(offered,
        $"The server offers security type(s) {string.Join(", ", offered.Select(SecurityName))}, none of which {(_options.Security == VncSecurity.Auto ? "this client supports" : "matches the chosen security")}.");

    internal static string SecurityName(int type) => type switch
    {
        SecNone => "None (1)",
        SecVnc => "VNC authentication (2)",
        5 or 6 => $"RA2 ({type})",
        16 => "Tight (16)",
        17 => "UltraVNC (17)",
        18 => "TLS (18)",
        SecVeNCrypt => "VeNCrypt (19)",
        SecArd => "Apple Remote Desktop (30)",
        113 => "MS-Logon II (113)",
        _ => type.ToString(CultureInfo.InvariantCulture)
    };

    private async Task VncChallengeAsync(CancellationToken ct)
    {
        if (_options.Password.Length == 0)
        {
            throw new VncAuthenticationException(null, credentialsMissing: true);
        }

        byte[] challenge = await _reader.BytesAsync(16, ct).ConfigureAwait(false);
        await SendAsync(VncAuthentication.Respond(challenge, _options.Password), ct).ConfigureAwait(false);
    }

    private async Task AppleAsync(CancellationToken ct)
    {
        if (_options.Username.Length == 0 || _options.Password.Length == 0)
        {
            throw new VncAuthenticationException(null, credentialsMissing: true, usernameRequired: true);
        }

        ushort generator = await _reader.U16Async(ct).ConfigureAwait(false);
        int keyLength = await _reader.U16Async(ct).ConfigureAwait(false);
        if (keyLength is < 16 or > 1024)
        {
            throw new VncProtocolException("Apple Remote Desktop key length is out of range.");
        }

        byte[] prime = await _reader.BytesAsync(keyLength, ct).ConfigureAwait(false);
        byte[] serverKey = await _reader.BytesAsync(keyLength, ct).ConfigureAwait(false);
        await SendAsync(AppleRemoteDesktop.Respond(generator, prime, serverKey, _options.Username, _options.Password), ct).ConfigureAwait(false);
    }

    /// <summary>VeNCrypt (security type 19): version 0.2, a subtype list, then optionally TLS and the inner authentication.</summary>
    private async Task VeNCryptAsync(CancellationToken ct)
    {
        int major = await _reader.U8Async(ct).ConfigureAwait(false), minor = await _reader.U8Async(ct).ConfigureAwait(false);
        if (major != 0 || minor < 2)
        {
            throw new VncConnectionException(VncConnectStage.Security, $"VeNCrypt version {major}.{minor} is not supported (0.2 is required)");
        }

        await SendAsync(new byte[] { 0, 2 }, ct).ConfigureAwait(false);
        if (await _reader.U8Async(ct).ConfigureAwait(false) != 0)
        {
            throw new VncConnectionException(VncConnectStage.Security, "the server refused VeNCrypt version 0.2");
        }

        int count = await _reader.U8Async(ct).ConfigureAwait(false);
        var subtypes = new List<int>(count);
        for (int i = 0; i < count; i++) { subtypes.Add((int)await _reader.U32Async(ct).ConfigureAwait(false)); }

        bool hasUser = _options.Username.Length > 0, hasPassword = _options.Password.Length > 0;
        bool Has(int t) => subtypes.Contains(t);
        // TLS with an X.509 certificate first, matching the credentials given; anonymous TLS (257–259) cannot be done
        // with .NET's TLS stacks, and Plain (256) would send the password in the clear, so neither is chosen.
        int chosen = hasUser && Has(VeNCryptX509Plain) ? VeNCryptX509Plain
            : hasPassword && Has(VeNCryptX509Vnc) ? VeNCryptX509Vnc
            : Has(VeNCryptX509None) ? VeNCryptX509None
            : Has(VeNCryptX509Vnc) ? VeNCryptX509Vnc
            : Has(VeNCryptX509Plain) ? VeNCryptX509Plain
            : _options.Security == VncSecurity.Auto && Has(SecVnc) ? SecVnc
            : _options.Security == VncSecurity.Auto && Has(SecNone) ? SecNone
            : 0;
        if (chosen == 0)
        {
            string list = string.Join(", ", subtypes.Select(static s => s.ToString(CultureInfo.InvariantCulture)));
            throw _options.Security == VncSecurity.Auto
                ? new VeNCryptUnusableException($"VeNCrypt offers only subtypes {list} (anonymous TLS or plain text)")
                : new VncSecurityUnsupportedException(subtypes, $"VeNCrypt offers subtypes {list}; this client needs an X.509 TLS subtype (260–262).");
        }

        await SendAsync(new RfbWriter(4).U32((uint)chosen).ToArray(), ct).ConfigureAwait(false);
        if (chosen >= VeNCryptX509None)
        {
            if (await _reader.U8Async(ct).ConfigureAwait(false) != 1)
            {
                throw new VncConnectionException(VncConnectStage.Tls, "the server refused the TLS subtype it offered");
            }

            _stage = VncConnectStage.Tls;
            SslStream tls = await VncTls.AuthenticateAsync(_transport, _options.CertificateHost ?? _options.Host, _options.CertificatePin, ct).ConfigureAwait(false);
            _stream = tls;
            _reader.Rebase(tls);
            _tls = true;
            _stage = VncConnectStage.Authentication;
        }

        switch (chosen)
        {
            case VeNCryptX509None:
            case SecNone:
                _security = "VeNCrypt (None)";
                break;
            case VeNCryptX509Vnc:
            case SecVnc:
                _security = "VeNCrypt (VNC)";
                await VncChallengeAsync(ct).ConfigureAwait(false);
                break;
            case VeNCryptX509Plain:
                _security = "VeNCrypt (Plain)";
                if (!hasUser) { throw new VncAuthenticationException(null, credentialsMissing: true, usernameRequired: true); }
                byte[] user = Encoding.UTF8.GetBytes(_options.Username), password = Encoding.UTF8.GetBytes(_options.Password);
                await SendAsync(new RfbWriter(8 + user.Length + password.Length).U32((uint)user.Length).U32((uint)password.Length).Bytes(user).Bytes(password).ToArray(), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<string> ReasonAsync(CancellationToken ct)
    {
        string reason = await StringAsync(ct).ConfigureAwait(false);
        return reason.Length == 0 ? "no reason given" : reason;
    }

    /// <summary>A u32-length string: UTF-8 when valid (as current servers send), Latin-1 otherwise.</summary>
    private async Task<string> StringAsync(CancellationToken ct)
    {
        uint length = await _reader.U32Async(ct).ConfigureAwait(false);
        if (length > MaxReason)
        {
            throw new VncProtocolException($"The server sent a {length}-byte string.");
        }

        byte[] bytes = await _reader.BytesAsync((int)length, ct).ConfigureAwait(false);
        return Decode(bytes);
    }

    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    // ---------------------------------------------------------------- client messages

    /// <summary>32 bpp, depth 24, little-endian true colour, 8 bits per channel, red at 16: B, G, R, X in memory.</summary>
    internal static byte[] SetPixelFormat() => new RfbWriter(20).U8(0).Zeros(3)
        .U8(32).U8(24).U8(0).U8(1).U16(255).U16(255).U16(255).U8(16).U8(8).U8(0).Zeros(3).ToArray();

    internal int[] Encodings()
    {
        var list = new List<int> { VncEncoding.CopyRect };
        int[] preferred = _options.Encoding switch
        {
            VncEncodingPreference.Zrle => [VncEncoding.Zrle, VncEncoding.Tight, VncEncoding.Hextile, VncEncoding.Rre],
            VncEncodingPreference.Hextile => [VncEncoding.Hextile, VncEncoding.Zrle, VncEncoding.Tight, VncEncoding.Rre],
            VncEncodingPreference.Raw => [],
            _ => [VncEncoding.Tight, VncEncoding.Zrle, VncEncoding.Hextile, VncEncoding.Rre]
        };
        list.AddRange(preferred);
        list.Add(VncEncoding.Raw);
        if (_options.JpegDecoder is not null && _options.JpegQuality is { } quality && _options.Encoding != VncEncodingPreference.Raw)
        {
            list.Add(VncEncoding.JpegQuality0 + Math.Clamp(quality, 0, 9));
        }
        list.Add(VncEncoding.CompressLevel0 + Math.Clamp(_options.CompressionLevel, 0, 9));
        list.AddRange([VncEncoding.ExtendedDesktopSize, VncEncoding.DesktopSize, VncEncoding.LastRect, VncEncoding.Cursor,
            VncEncoding.DesktopName, VncEncoding.QemuExtendedKey, VncEncoding.ExtendedClipboard]);
        if (_options.AudioPlayback) { list.Add(VncEncoding.QemuAudio); }
        return [.. list];
    }

    private byte[] SetEncodings()
    {
        int[] encodings = Encodings();
        RfbWriter w = new RfbWriter(4 + encodings.Length * 4).U8(2).Zeros(1).U16(encodings.Length);
        foreach (int encoding in encodings) { w.S32(encoding); }
        return w.ToArray();
    }

    private async Task RequestUpdateAsync(bool incremental, CancellationToken ct)
    {
        _requestOutstanding = true;
        await SendAsync(new RfbWriter(10).U8(3).U8(incremental ? 1 : 0).U16(0).U16(0)
            .U16(_framebuffer.Width).U16(_framebuffer.Height).ToArray(), ct).ConfigureAwait(false);
    }

    private async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(message, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _ = _writeGate.Release(); }
    }

    private Task SendConnectedAsync(byte[] message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_connected)
        {
            throw new InvalidOperationException("The VNC session is not connected.");
        }
        return SendAsync(message, ct);
    }

    public Task KeyAsync(uint keysym, uint scanCode, bool down, CancellationToken ct = default)
    {
        if (SupportsScanCodes && scanCode != 0)
        {
            // QEMU Extended Key Event: the XT scan code, with an E0 prefix carried as the high bit of the low byte.
            uint xt = (scanCode & 0x100) != 0 ? 0x80 | (scanCode & 0x7F) : scanCode & 0x7F;
            return SendConnectedAsync(new RfbWriter(12).U8(255).U8(0).U16(down ? 1 : 0).U32(keysym).U32(xt).ToArray(), ct);
        }
        return SendConnectedAsync(new RfbWriter(8).U8(4).U8(down ? 1 : 0).Zeros(2).U32(keysym).ToArray(), ct);
    }

    public Task PointerAsync(VncButtons buttons, int x, int y, CancellationToken ct = default)
    {
        int px = Math.Clamp(x, 0, Math.Max(0, _framebuffer.Width - 1)), py = Math.Clamp(y, 0, Math.Max(0, _framebuffer.Height - 1));
        return SendConnectedAsync(new RfbWriter(6).U8(5).U8((byte)buttons).U16(px).U16(py).ToArray(), ct);
    }

    public Task SendClipboardAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_clipboard.Enabled)
        {
            _clipboard.LocalText = text;
            // Announce and let the server pull it; a server without Notify takes the text at once.
            if ((_clipboard.ServerActions & ExtendedClipboard.Notify) != 0)
            {
                return SendConnectedAsync(ExtendedClipboard.NotifyMessage(true), ct);
            }
            if ((_clipboard.ServerActions & ExtendedClipboard.Provide) != 0)
            {
                return SendConnectedAsync(_clipboard.ProvideMessage(text), ct);
            }
        }

        // ClientCutText is Latin-1 with LF line endings; characters outside Latin-1 become '?'.
        string latin = new([.. text.ReplaceLineEndings("\n").Select(static c => c <= 'ÿ' ? c : '?')]);
        byte[] bytes = Encoding.Latin1.GetBytes(latin);
        return SendConnectedAsync(new RfbWriter(8 + bytes.Length).U8(6).Zeros(3).U32((uint)bytes.Length).Bytes(bytes).ToArray(), ct);
    }

    public Task RequestResizeAsync(int width, int height, CancellationToken ct = default)
    {
        if (!CanResize || width < 1 || height < 1 || (width == _framebuffer.Width && height == _framebuffer.Height))
        {
            return Task.CompletedTask;
        }

        width = Math.Min(width, VncFramebuffer.MaxSide);
        height = Math.Min(height, VncFramebuffer.MaxSide);
        // SetDesktopSize (251) with one screen covering the whole desktop, keeping the id the server gave its screen.
        byte[] message = new RfbWriter(24).U8(251).Zeros(1).U16(width).U16(height).U8(1).Zeros(1)
            .U32(_screenId).U16(0).U16(0).U16(width).U16(height).U32(_screenFlags).ToArray();
        return SendConnectedAsync(message, ct);
    }

    public async Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default)
    {
        _visible = visible;
        if (visible && _connected && !_requestOutstanding)
        {
            await RequestUpdateAsync(incremental: true, ct).ConfigureAwait(false);
        }
    }

    public VncFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false) =>
        _framebuffer.CopyTo(destination, stride, width, height, full);

    // ---------------------------------------------------------------- receive loop

    private void Start() => _receive = Task.Run(ReceiveLoopAsync);

    private async Task ReceiveLoopAsync()
    {
        CancellationToken ct = _lifetime.Token;
        Exception? failure;
        try
        {
            while (true)
            {
                int type = await _reader.U8Async(ct).ConfigureAwait(false);
                switch (type)
                {
                    case 0: await FramebufferUpdateAsync(ct).ConfigureAwait(false); break;
                    case 1:   // SetColourMapEntries: never used with a true-colour pixel format; skip it
                        await _reader.SkipAsync(1, ct).ConfigureAwait(false);
                        _ = await _reader.U16Async(ct).ConfigureAwait(false);
                        await _reader.SkipAsync(await _reader.U16Async(ct).ConfigureAwait(false) * 6L, ct).ConfigureAwait(false);
                        break;
                    case 2: break;   // Bell
                    case 3: await ServerCutTextAsync(ct).ConfigureAwait(false); break;
                    case UltraFileTransfer.MessageType when _files is not null:
                        await _files.ReceiveAsync(_reader, ct).ConfigureAwait(false);
                        break;
                    case 150: break;   // EndOfContinuousUpdates
                    case 255: await QemuServerMessageAsync(ct).ConfigureAwait(false); break;
                    default: throw new VncProtocolException($"The server sent message type {type}, which this client does not know.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _files?.Close(null); return; }
        catch (ObjectDisposedException) when (_disposed != 0) { _files?.Close(null); return; }
        catch (Exception ex) when (_files?.AccessPending == true && ex is EndOfStreamException or IOException or SocketException && _abortReason is null)
        {
            // Closed right after the file-transfer request: the server does not have it (TigerVNC, QEMU) or has it off
            // (LibVNC drops the client). Not a network failure, so the tab does not reconnect on its own.
            if (_disposed != 0) { _files.Close(null); return; }
            failure = new VncFileTransferUnsupportedException("The server closed the connection when file transfer was requested.");
        }
        catch (Exception) when (_abortReason is { } reason)
        {
            // Abort closed the transport under the read: report the reason as a lost connection, so the tab reconnects.
            if (_disposed != 0) { return; }
            failure = new VncConnectionLostException(reason);
        }
        catch (EndOfStreamException) { failure = null; }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            if (_disposed != 0) { return; }
            failure = new VncConnectionLostException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            if (_disposed != 0) { return; }
            failure = ex is VncProtocolException ? ex : new VncProtocolException(ex.Message, ex);
        }

        _connected = false;
        _files?.Close(null);
        if (_disposed == 0)
        {
            try { Disconnected?.Invoke(failure); } catch { /* observers must not break the loop's exit */ }
        }
    }

    // ---------------------------------------------------------------- QEMU audio

    /// <summary>
    /// The server confirmed the QEMU Audio pseudo-encoding (a rectangle with encoding -259; QEMU fills in the desktop size,
    /// which means nothing here): set the format, then enable capture. Only once per connection.
    /// </summary>
    private async Task EnableAudioAsync(CancellationToken ct)
    {
        if (!_options.AudioPlayback || _audio is not null || _audioFailure is not null) { return; }
        _audio = new VncAudioPlayer(_options.AudioOutputFactory ?? (() => AudioOutput.Create(_options.Log)), FailAudio, _options.Log);
        // QEMU audio client message: 255, submessage 1, operation u16 (2 set-format: u8 sample format 3 = S16, u8 channels,
        // u32 frequency; 0 enable). Samples come little-endian, QEMU's default.
        AudioFormat format = VncAudioPlayer.Format;
        await SendAsync(new RfbWriter(10).U8(255).U8(1).U16(2).U8(3).U8(format.Channels).U32(format.SampleRate).ToArray(), ct).ConfigureAwait(false);
        await SendAsync(new RfbWriter(4).U8(255).U8(1).U16(0).ToArray(), ct).ConfigureAwait(false);
        AudioActive = true;
        _options.Log?.Invoke($"VNC {_options.Host}:{_options.Port}: QEMU audio enabled (S16 stereo {format.SampleRate} Hz).");
        InfoChanged?.Invoke();
    }

    /// <summary>QEMU server message (255): submessage 1 is audio — operation 0 end, 1 begin, 2 data (u32 length + PCM).</summary>
    private async Task QemuServerMessageAsync(CancellationToken ct)
    {
        int submessage = await _reader.U8Async(ct).ConfigureAwait(false);
        if (submessage != 1)
        {
            throw new VncProtocolException($"The server sent QEMU server message {submessage}, which this client does not know.");
        }

        int operation = await _reader.U16Async(ct).ConfigureAwait(false);
        switch (operation)
        {
            case 0: _audio?.Flush(); break;
            case 1: break;
            case 2:
                uint length = await _reader.U32Async(ct).ConfigureAwait(false);
                if (length > MaxAudioData)
                {
                    throw new VncProtocolException($"The server sent a {length}-byte audio buffer.");
                }
                if (length % (uint)VncAudioPlayer.Format.BlockAlign != 0)
                {
                    // Not whole frames: the stream can no longer be played in step, but the desktop is fine.
                    await _reader.SkipAsync(length, ct).ConfigureAwait(false);
                    if (_audio is not null) { FailAudio(new VncProtocolException($"The server sent a {length}-byte audio buffer, which is not whole frames.")); }
                    break;
                }
                if (_audio is { } player && _audioFailure is null)
                {
                    player.Push(await _reader.BytesAsync((int)length, ct).ConfigureAwait(false));
                }
                else
                {
                    await _reader.SkipAsync(length, ct).ConfigureAwait(false);
                }
                break;
            default:
                throw new VncProtocolException($"The server sent QEMU audio operation {operation}, which this client does not know.");
        }
    }

    private void FailAudio(Exception error)
    {
        Action<Exception>? handlers;
        lock (_audioGate)
        {
            if (_audioFailure is not null) { return; }
            _audioFailure = error;
            handlers = _audioHandlers;
        }
        AudioActive = false;
        _options.Log?.Invoke($"VNC {_options.Host}:{_options.Port}: audio playback stopped: {error.Message}");
        // Stop the server capturing for nobody; the desktop goes on.
        if (_connected && _disposed == 0)
        {
            try
            {
                _ = SendAsync(new RfbWriter(4).U8(255).U8(1).U16(1).ToArray(), _lifetime.Token)
                    .ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (ObjectDisposedException) { /* disposed meanwhile: nothing left to stop */ }
        }
        try { handlers?.Invoke(error); } catch { /* observers cannot break the session */ }
        try { InfoChanged?.Invoke(); } catch { /* ditto */ }
    }

    private async Task FramebufferUpdateAsync(CancellationToken ct)
    {
        _requestOutstanding = false;
        await _reader.SkipAsync(1, ct).ConfigureAwait(false);
        int count = await _reader.U16Async(ct).ConfigureAwait(false);
        bool pixels = false;
        for (int i = 0; count == 0xFFFF || i < count; i++)
        {
            int x = await _reader.U16Async(ct).ConfigureAwait(false), y = await _reader.U16Async(ct).ConfigureAwait(false);
            int width = await _reader.U16Async(ct).ConfigureAwait(false), height = await _reader.U16Async(ct).ConfigureAwait(false);
            int encoding = await _reader.S32Async(ct).ConfigureAwait(false);
            switch (encoding)
            {
                case VncEncoding.Raw or VncEncoding.CopyRect or VncEncoding.Rre or VncEncoding.Hextile or VncEncoding.Zrle or VncEncoding.Tight:
                    await _decoder!.DecodeAsync(_reader, encoding, x, y, width, height, ct).ConfigureAwait(false);
                    if (encoding != VncEncoding.CopyRect) { LastEncoding = VncEncoding.Name(encoding); }
                    pixels = true;
                    break;
                case VncEncoding.LastRect:
                    break;   // ends the update (below): its rectangle count was 0xFFFF
                case VncEncoding.DesktopSize:
                    _framebuffer.Resize(width, height);
                    pixels = true;
                    break;
                case VncEncoding.ExtendedDesktopSize:
                    await ExtendedDesktopSizeAsync(x, y, width, height, ct).ConfigureAwait(false);
                    pixels = true;
                    break;
                case VncEncoding.Cursor:
                    VncPointer pointer = await RectangleDecoder.CursorAsync(_reader, x, y, width, height, ct).ConfigureAwait(false);
                    PointerChanged?.Invoke(pointer);
                    break;
                case VncEncoding.QemuExtendedKey:
                    SupportsScanCodes = true;
                    break;
                case VncEncoding.QemuAudio:
                    await EnableAudioAsync(ct).ConfigureAwait(false);
                    break;
                case VncEncoding.DesktopName:
                    _desktopName = await StringAsync(ct).ConfigureAwait(false);
                    InfoChanged?.Invoke();
                    break;
                default:
                    throw new VncProtocolException($"The server used encoding {encoding}, which this client did not ask for.");
            }

            if (encoding == VncEncoding.LastRect) { break; }
        }

        if (pixels) { FrameAvailable?.Invoke(); }
        if (_visible) { await RequestUpdateAsync(incremental: true, ct).ConfigureAwait(false); }
    }

    /// <summary>
    /// ExtendedDesktopSize: x is the reason (0 server, 1 this client's request, 2 another client), y the status
    /// (0 success). The first one answers our SetEncodings and says the server takes SetDesktopSize.
    /// </summary>
    private async Task ExtendedDesktopSizeAsync(int reason, int status, int width, int height, CancellationToken ct)
    {
        int screens = await _reader.U8Async(ct).ConfigureAwait(false);
        await _reader.SkipAsync(3, ct).ConfigureAwait(false);
        for (int i = 0; i < screens; i++)
        {
            uint id = await _reader.U32Async(ct).ConfigureAwait(false);
            await _reader.SkipAsync(8, ct).ConfigureAwait(false);
            uint flags = await _reader.U32Async(ct).ConfigureAwait(false);
            if (i == 0) { (_screenId, _screenFlags) = (id, flags); }
        }

        CanResize = true;
        if (status == 0)
        {
            _framebuffer.Resize(width, height);
        }
        else if (reason == 1)
        {
            _options.Log?.Invoke($"VNC {_options.Host}:{_options.Port}: the server refused to resize the desktop (status {status}).");
        }
    }

    private async Task ServerCutTextAsync(CancellationToken ct)
    {
        await _reader.SkipAsync(3, ct).ConfigureAwait(false);
        int length = await _reader.S32Async(ct).ConfigureAwait(false);
        int size = length == int.MinValue ? int.MaxValue : Math.Abs(length);
        if (size > MaxCutText)
        {
            throw new VncProtocolException($"The server sent {size} bytes of clipboard data.");
        }

        byte[] data = await _reader.BytesAsync(size, ct).ConfigureAwait(false);
        if (length >= 0)
        {
            ClipboardReceived?.Invoke(Encoding.Latin1.GetString(data).ReplaceLineEndings());
            return;
        }

        (List<byte[]> replies, string? received) = _clipboard.Handle(data, ClipboardReceived is not null);
        foreach (byte[] reply in replies) { await SendAsync(reply, ct).ConfigureAwait(false); }
        if (received is not null) { ClipboardReceived?.Invoke(received); }
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>Ends the session from this side (e.g. input overflow) and reports it as a lost connection.</summary>
    public void Abort(string reason)
    {
        if (_disposed != 0 || !_connected) { return; }
        _abortReason = reason;
        _connected = false;
        try { _transport.Dispose(); } catch { /* already closed */ }
        _options.Log?.Invoke($"VNC {_options.Host}:{_options.Port}: {reason}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _connected = false;
        _lifetime.Cancel();
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* closing a broken stream */ }
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { /* ditto */ }
        if (_receive is { } receive)
        {
            try { await receive.ConfigureAwait(false); } catch { /* reported through Disconnected */ }
        }
        _files?.Close(null);
        if (_audio is { } audio) { await audio.DisposeAsync().ConfigureAwait(false); }
        _decoder?.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Auto picked VeNCrypt, which turned out to offer nothing usable: retried without it.</summary>
    private sealed class VeNCryptUnusableException(string message) : Exception(message);
}
