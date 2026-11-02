using System.Security.Cryptography;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Security;

internal static class RdpDtls
{
    internal sealed class Client(byte[] certificateDigest) : DefaultTlsClient(new BcTlsCrypto(new SecureRandom()))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.DTLSv12];
        public override TlsAuthentication GetAuthentication() => new Authentication(certificateDigest);
        public override int GetHandshakeTimeoutMillis() => 10000;
    }

    private sealed class Authentication(byte[] certificateDigest) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            Certificate chain = serverCertificate.Certificate;
            if (chain.IsEmpty || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(chain.GetCertificateAt(0).GetEncoded()), certificateDigest))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }
        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }

    /// <summary>Cancellation reaches the blocking DTLS API through every underlying datagram operation.</summary>
    internal sealed class Adapter(RdpUdpTransport udp, CancellationToken lifetime) : DatagramTransport
    {
        public int GetReceiveLimit() => 1232;
        public int GetSendLimit() => RdpUdpState.MaxPayload;
        public void Close() { }
        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (waitMillis > 0) { timeout.CancelAfter(waitMillis); }
            try
            {
                byte[] data = udp.ReceiveAsync(timeout.Token).GetAwaiter().GetResult();
                if (data.Length > buffer.Length) { throw new IOException("DTLS receive buffer too small."); }
                data.CopyTo(buffer);
                return data.Length;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return -1; }
        }
        public void Send(byte[] buf, int off, int len) => udp.SendAsync(buf.AsMemory(off, len), lifetime).GetAwaiter().GetResult();
        public void Send(ReadOnlySpan<byte> buffer) => udp.SendAsync(buffer.ToArray(), lifetime).GetAwaiter().GetResult();
    }
}
