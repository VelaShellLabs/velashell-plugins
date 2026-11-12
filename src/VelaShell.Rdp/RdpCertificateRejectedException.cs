namespace VelaShell.Rdp;

/// <summary>
/// The server TLS certificate was not trusted, or no longer matches the pin the caller supplied. Carries the
/// leaf PEM so a caller can show its SHA-256 and offer to pin it.
/// </summary>
public sealed class RdpCertificateRejectedException : Exception
{
    /// <summary>An untrusted certificate offered for approval.</summary>
    public RdpCertificateRejectedException(byte[] pem, Exception inner) : this(pem, inner, pinMismatch: false)
    {
    }

    /// <summary>
    /// A certificate offered for approval; <paramref name="pinMismatch" /> when a pin was supplied and the server
    /// now presents a different certificate.
    /// </summary>
    public RdpCertificateRejectedException(byte[] pem, Exception inner, bool pinMismatch)
        : base(pinMismatch
            ? "The RDP server certificate changed since it was approved and requires host approval again."
            : "The RDP server certificate requires host approval.", inner)
    {
        Pem = pem;
        PinMismatch = pinMismatch;
    }

    /// <summary>The server's leaf certificate in PEM form.</summary>
    public byte[] Pem { get; }

    /// <summary>
    /// <see langword="true" /> when a pin was supplied and the certificate no longer matches it (for example a
    /// self-signed RDP certificate that Windows renewed), so the caller can say the certificate changed rather
    /// than present it as a first-time approval.
    /// </summary>
    public bool PinMismatch { get; }
}
