using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Rdp.Engine;

internal sealed class RdpTlsTrust(string trustedSha256) : IDisposable
{
    private X509Certificate2? _certificate;
    private SslPolicyErrors _errors;
    private bool _pinMismatch;
    public bool Rejected { get; private set; }
    public byte[] ServerPublicKey => _certificate?.GetPublicKey() ?? throw new ProtocolConnectionException("The RDP server did not provide a TLS certificate.");
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        _certificate?.Dispose();
        _certificate = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        _errors = errors;
        if (_certificate is null) { Rejected = true; return false; }
        if (trustedSha256.Length != 0)
        {
            byte[] actual = _certificate.GetCertHash(HashAlgorithmName.SHA256);
            byte[] expected = Convert.FromHexString(trustedSha256);
            _pinMismatch = !CryptographicOperations.FixedTimeEquals(actual, expected);
            Rejected = _pinMismatch;
            return !Rejected; // An explicitly trusted certificate, not a generic validation bypass.
        }
        Rejected = errors != SslPolicyErrors.None;
        return !Rejected;
    }
    public Exception Rejection()
    {
        if (_certificate is null) return new ProtocolConnectionException("The RDP server did not provide a TLS certificate.");
        string reason = _pinMismatch ? "The server certificate changed; the saved SHA-256 pin does not match." : _errors.ToString();
        reason += $" Valid from {_certificate.NotBefore.ToUniversalTime():u}.";
        return new ProtocolCertificateTrustException("The RDP server certificate requires confirmation.",
            _certificate.Subject, _certificate.Issuer, new DateTimeOffset(_certificate.NotAfter.ToUniversalTime()),
            _certificate.GetCertHashString(HashAlgorithmName.SHA256), reason);
    }
    public void Dispose() { _certificate?.Dispose(); _certificate = null; }
}
