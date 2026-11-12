using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// An in-process RDP endpoint scripted per TCP connection: each script receives the connection after the
/// client's X.224 Connection Request has been read (its requested protocols are recorded) and decides what
/// to select, refuse or send. Enough to drive the security-layer negotiation and the first CredSSP reply
/// without a real server.
/// </summary>
internal sealed class FakeRdpServer : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> SharedCertificate = new(static () =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=vela-fake-rdp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel cannot serve TLS from an ephemeral key; a PFX round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    });

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<uint> _requested = [];
    private readonly Task _serving;
    private readonly X509Certificate2 _certificate;

    internal FakeRdpServer(params Func<FakeRdpConnection, Task>[] scripts) : this(null, scripts)
    {
    }

    /// <summary>
    /// A server with its own certificate (pin it with <see cref="OwnPin" />), for tests that must not share TLS state
    /// with the others. Note that clients also resume TLS sessions per target name; use a unique
    /// <c>CertificateHost</c> as well when the negotiated protocol version matters.
    /// </summary>
    internal FakeRdpServer(X509Certificate2? certificate, params Func<FakeRdpConnection, Task>[] scripts)
    {
        _certificate = certificate ?? SharedCertificate.Value;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = ServeAsync(scripts);
    }

    internal int Port { get; }

    /// <summary>SHA-256 of the shared leaf certificate, for <c>CertificatePin</c>.</summary>
    internal static byte[] Pin => SHA256.HashData(SharedCertificate.Value.RawData);

    /// <summary>SHA-256 of this server's leaf certificate.</summary>
    internal byte[] OwnPin => SHA256.HashData(_certificate.RawData);

    /// <summary>A fresh self-signed RSA certificate with a key the platform TLS stack can serve.</summary>
    internal static X509Certificate2 CreateCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>The requestedProtocols of every connection, in order.</summary>
    internal IReadOnlyList<uint> RequestedProtocols
    {
        get
        {
            lock (_requested)
            {
                return [.. _requested];
            }
        }
    }

    private async Task ServeAsync(Func<FakeRdpConnection, Task>[] scripts)
    {
        foreach (Func<FakeRdpConnection, Task> script in scripts)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            var connection = new FakeRdpConnection(client.GetStream(), _certificate);
            uint requested = await connection.ReadConnectionRequestAsync();
            lock (_requested)
            {
                _requested.Add(requested);
            }
            await script(connection);
        }
    }

    /// <summary>Stops listening and surfaces any failure inside a script.</summary>
    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            await _serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
            // Stopped while waiting for a connection the client never made.
        }
    }
}

/// <summary>One accepted connection of a <see cref="FakeRdpServer" />.</summary>
internal sealed class FakeRdpConnection(NetworkStream net, X509Certificate2 certificate)
{
    internal const uint Ssl = 0x1, Hybrid = 0x2;

    internal NetworkStream Net => net;

    internal async Task<uint> ReadConnectionRequestAsync()
    {
        byte[] head = new byte[4];
        await net.ReadExactlyAsync(head);
        byte[] body = new byte[((head[2] << 8) | head[3]) - 4];
        await net.ReadExactlyAsync(body);
        // RDP_NEG_REQ is the last 8 bytes: type 0x01, flags, length 8, requestedProtocols (LE).
        return body.Length >= 8 && body[^8] == 0x01 ? BitConverter.ToUInt32(body, body.Length - 4) : 0;
    }

    /// <summary>X.224 Connection Confirm with RDP_NEG_RSP selecting <paramref name="protocol" />.</summary>
    internal Task SelectAsync(uint protocol, byte flags = 0) => SendNegotiationAsync(0x02, protocol, flags);

    /// <summary>X.224 Connection Confirm with RDP_NEG_FAILURE <paramref name="code" />.</summary>
    internal Task RefuseAsync(uint code) => SendNegotiationAsync(0x03, code);

    private async Task SendNegotiationAsync(byte type, uint value, byte flags = 0)
    {
        byte[] pdu = [3, 0, 0, 19, 14, 0xD0, 0, 0, 0, 0, 0, type, flags, 8, 0, 0, 0, 0, 0];
        _ = BitConverter.TryWriteBytes(pdu.AsSpan(15), value);
        await net.WriteAsync(pdu);
    }

    internal async Task<SslStream> StartTlsAsync(SslProtocols protocols = SslProtocols.Tls12)
    {
        var ssl = new SslStream(net, leaveInnerStreamOpen: true);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = protocols
        });
        return ssl;
    }

    /// <summary>Reads one DER element (a TSRequest) from the TLS channel.</summary>
    internal static async Task<byte[]> ReadDerAsync(SslStream ssl)
    {
        byte[] head = new byte[2];
        await ssl.ReadExactlyAsync(head);
        int length = head[1];
        byte[] lengthBytes = [];
        if ((head[1] & 0x80) != 0)
        {
            lengthBytes = new byte[head[1] & 0x7F];
            await ssl.ReadExactlyAsync(lengthBytes);
            length = lengthBytes.Aggregate(0, static (sum, b) => (sum << 8) | b);
        }
        byte[] body = new byte[length];
        await ssl.ReadExactlyAsync(body);
        return [.. head, .. lengthBytes, .. body];
    }

    /// <summary>A TSRequest carrying only version 6 and <paramref name="error" />, encoded signed as Windows does.</summary>
    internal static async Task SendErrorCodeAsync(SslStream ssl, uint error)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteInteger(6);
            }
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
            {
                w.WriteInteger(unchecked((int)error));
            }
        }
        await ssl.WriteAsync(w.Encode());
        await ssl.FlushAsync();
    }
}
