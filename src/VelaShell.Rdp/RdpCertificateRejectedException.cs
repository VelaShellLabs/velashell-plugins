namespace VelaShell.Rdp;

/// <summary>
/// The server TLS certificate was not trusted and no matching pin was supplied. Carries the leaf
/// PEM so a caller can show its SHA-256 and offer to pin it.
/// </summary>
public sealed class RdpCertificateRejectedException(byte[] pem, Exception inner)
    : Exception("The RDP server certificate requires host approval.", inner)
{
    /// <summary>The server's leaf certificate in PEM form.</summary>
    public byte[] Pem { get; } = pem;
}
