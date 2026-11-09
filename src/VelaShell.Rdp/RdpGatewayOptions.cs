namespace VelaShell.Rdp;

/// <summary>RD Gateway HTTP transport over TLS/WebSocket (Windows Server 2016 or newer).</summary>
public sealed record RdpGatewayOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 443;
    public bool UseRdpCredentials { get; init; } = true;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Domain { get; init; } = "";

    /// <summary>Optional SHA-256 leaf certificate pin for the gateway only. Otherwise OS trust and host validation apply.</summary>
    public byte[]? CertificatePin { get; init; }

    /// <summary>Receives advisory RD Gateway service messages on the receive thread; exceptions are ignored.</summary>
    public Action<string>? ServiceMessageReceived { get; init; }

    public override string ToString() => $"{nameof(RdpGatewayOptions)} {{ Host = {Host}, Port = {Port} }}";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown
            || Host.Any(char.IsControl) || Port is < 1 or > 65535)
        {
            throw new ArgumentException("RD Gateway requires a host name or IP address and a port from 1 to 65535.");
        }
        if (CertificatePin is { Length: not 32 })
        {
            throw new ArgumentException("RD Gateway certificate pin must contain a SHA-256 hash.");
        }
        if (!UseRdpCredentials && (string.IsNullOrWhiteSpace(Username) || Username.Contains('\0')
            || Password.Contains('\0') || Domain.Any(char.IsControl)))
        {
            throw new ArgumentException("RD Gateway credentials are invalid.");
        }
    }
}
