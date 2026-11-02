using System.Security.Cryptography;
using System.Text;
using VelaShell.Rdp.Security;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Pdu;

/// <summary>MS-RDPELE new-license negotiation inside the authenticated TLS transport.</summary>
internal sealed class Licensing(string username, string machineName) : IDisposable
{
    private enum Phase { AwaitRequest, AwaitChallenge, AwaitLicense, Complete, Failed }
    private Phase _phase;
    private LicenseEncryption? _encryption;
    private byte[]? _lastResponse;
    private byte _version = 3;
    private int _messages;
    private int _retries;

    internal bool CanActivate => _messages == 0 || _phase == Phase.Complete;
    internal RdpProtocolException? Failure { get; private set; }

    /// <summary>Returns a complete security-header-wrapped reply, or null when no reply is required.</summary>
    internal byte[]? Process(ReadOnlySpan<byte> mcsPayload)
    {
        if (++_messages > 16 || _phase is Phase.Complete or Phase.Failed)
        {
            throw new RdpProtocolException("Unexpected or excessive RDP licensing messages.");
        }

        var r = new ByteReader(mcsPayload);
        ushort flags = r.U16LE();
        _ = r.U16LE();
        if ((flags & SecurityFlags.License) == 0 || (flags & 0x0008) != 0)
        {
            throw new RdpProtocolException("Expected an unencrypted licensing PDU inside TLS.");
        }
        byte type = r.U8();
        _version = (byte)(r.U8() & 0x0F);
        if (_version is not (2 or 3) || r.U16LE() != mcsPayload.Length - 4)
        {
            throw new RdpProtocolException("Invalid RDP licensing preamble or message length.");
        }

        byte[]? response = type switch
        {
            0x01 => Request(ref r),
            0x02 => Challenge(ref r),
            0x03 or 0x04 => IssuedLicense(ref r),
            0xFF => Error(ref r),
            _ => throw new RdpProtocolException($"Unexpected RDP licensing message 0x{type:X2}.")
        };
        End(ref r);
        if (response is not null)
        {
            _lastResponse = response;
        }
        return response;
    }

    private byte[] Request(ref ByteReader r)
    {
        Require(Phase.AwaitRequest);
        byte[] serverRandom = r.Take(32).ToArray();
        _ = r.U32LE(); // product version
        _ = ReadString(ref r, unicode: true);
        _ = ReadString(ref r, unicode: true);
        var algorithms = new ByteReader(Blob(ref r, 0x000D));
        bool rsa = false;
        while (algorithms.Remaining > 0)
        {
            rsa |= algorithms.U32LE() == 1;
        }
        if (!rsa)
        {
            throw new RdpProtocolException("The RDP license server did not offer RSA key exchange.");
        }
        byte[] certificate = Blob(ref r, 3).ToArray();
        uint scopes = r.U32LE();
        if (scopes > r.Remaining / 4)
        {
            throw new RdpProtocolException("Invalid RDP licensing scope count.");
        }
        for (uint i = 0; i < scopes; i++)
        {
            _ = Blob(ref r, 0x000E);
        }
        End(ref r);

        try { _encryption = new LicenseEncryption(certificate, serverRandom); }
        catch (RdpProtocolException ex)
        {
            Failure = ex;
            _phase = Phase.Failed;
            var error = new ByteWriter();
            error.U32LE(1); // ERR_INVALID_SERVER_CERTIFICATE
            error.U32LE(1); // ST_TOTAL_ABORT
            WriteBlob(error, 4, []);
            return Packet(0xFF, error.Span);
        }
        var body = new ByteWriter();
        body.U32LE(1); // KEY_EXCHANGE_ALG_RSA
        body.U32LE(0); // OS_ID_OTHER / ISV_ID_UNKNOWN
        body.Bytes(_encryption.ClientRandom);
        WriteBlob(body, 2, _encryption.EncryptedPreMasterSecret);
        WriteBlob(body, 15, Ansi(username));
        WriteBlob(body, 16, Ansi(machineName));
        _phase = Phase.AwaitChallenge;
        return Packet(0x13, body.Span);
    }

    private byte[] Challenge(ref ByteReader r)
    {
        Require(Phase.AwaitChallenge);
        _ = r.U32LE(); // reserved ConnectFlags
        byte[] challenge = DecryptAuthenticated(ref r);
        var response = new ByteWriter();
        response.U16LE(0x0100);
        response.U16LE(0xFF00); // OTHER_PLATFORMCHALLENGE_TYPE
        response.U16LE(3); // LICENSE_DETAIL_DETAIL
        response.U16LE((ushort)challenge.Length);
        response.Bytes(challenge);
        var hardware = new ByteWriter(20);
        hardware.U32LE(0);
        hardware.Bytes(SHA256.HashData(Encoding.UTF8.GetBytes(machineName)).AsSpan(0, 16));

        var body = new ByteWriter();
        WriteBlob(body, 9, _encryption!.Crypt(response.Span));
        WriteBlob(body, 9, _encryption.Crypt(hardware.Span));
        body.Bytes(_encryption.Mac([.. response.Span, .. hardware.Span]));
        _phase = Phase.AwaitLicense;
        return Packet(0x15, body.Span);
    }

    private byte[]? IssuedLicense(ref ByteReader r)
    {
        Require(Phase.AwaitLicense);
        byte[] plaintext = DecryptAuthenticated(ref r);
        try
        {
            var license = new ByteReader(plaintext);
            _ = license.U32LE();
            _ = ReadString(ref license, unicode: false);
            _ = ReadString(ref license, unicode: true);
            _ = ReadString(ref license, unicode: true);
            uint length = license.U32LE();
            if (length == 0 || length > license.Remaining)
            {
                throw new RdpProtocolException("Invalid RDP issued license length.");
            }
            license.Skip((int)length);
            End(ref license);
            _phase = Phase.Complete;
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] DecryptAuthenticated(ref ByteReader r)
    {
        // Windows leaves these blob types undefined; only the length and MAC are meaningful.
        ReadOnlySpan<byte> encrypted = Blob(ref r);
        ReadOnlySpan<byte> mac = r.Take(16);
        End(ref r);
        byte[] plaintext = _encryption!.Crypt(encrypted);
        if (!CryptographicOperations.FixedTimeEquals(_encryption.Mac(plaintext), mac))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new RdpProtocolException("Invalid RDP licensing MAC.");
        }
        return plaintext;
    }

    private byte[]? Error(ref ByteReader r)
    {
        uint code = r.U32LE();
        uint transition = r.U32LE();
        _ = Blob(ref r, 4);
        End(ref r);
        if (code == 7 && transition == 2)
        {
            _phase = Phase.Complete;
            return null;
        }
        if (transition is 3 or 4 && ++_retries <= 3)
        {
            if (transition == 4 && _lastResponse is not null)
            {
                return _lastResponse;
            }
            if (transition == 3)
            {
                _encryption?.Dispose();
                _encryption = null;
                _lastResponse = null;
                _phase = Phase.AwaitRequest;
                return null;
            }
        }
        string reason = code switch
        {
            3 => "invalid message authentication code",
            4 => "invalid license scope",
            6 => "no license server available",
            8 => "invalid client",
            11 => "invalid product ID",
            12 => "invalid message length",
            _ => "license negotiation rejected"
        };
        throw new RdpProtocolException($"RDP licensing failed: {reason} (error 0x{code:X8}, transition 0x{transition:X8}).");
    }

    private void Require(Phase phase)
    {
        if (_phase != phase)
        {
            throw new RdpProtocolException("RDP licensing message arrived out of sequence.");
        }
    }

    private static void End(ref ByteReader r)
    {
        if (r.Remaining != 0)
        {
            throw new RdpProtocolException("Trailing data in RDP licensing message.");
        }
    }

    private static ReadOnlySpan<byte> Blob(ref ByteReader r, ushort? expected = null)
    {
        ushort type = r.U16LE();
        ushort length = r.U16LE();
        // Windows also uses BB_ANY_BLOB for these payloads (MS-RDPELE product behavior).
        if (expected.HasValue && type != expected && type != 0)
        {
            throw new RdpProtocolException($"Unexpected RDP licensing blob type 0x{type:X4}.");
        }
        return r.Take(length);
    }

    private static ReadOnlySpan<byte> ReadString(ref ByteReader r, bool unicode)
    {
        uint length = r.U32LE();
        int terminator = unicode ? 2 : 1;
        if (length > r.Remaining || length < terminator || (unicode && length % 2 != 0))
        {
            throw new RdpProtocolException("Invalid RDP licensing string length.");
        }
        ReadOnlySpan<byte> value = r.Take((int)length);
        if (value[^1] != 0 || (unicode && value[^2] != 0))
        {
            throw new RdpProtocolException("Unterminated RDP licensing string.");
        }
        return value;
    }

    private static byte[] Ansi(string value)
    {
        if (value.Length > 255 || value.Contains('\0'))
        {
            throw new RdpProtocolException("Invalid client name for RDP licensing.");
        }
        return Encoding.Latin1.GetBytes(value + '\0');
    }

    private static void WriteBlob(ByteWriter w, ushort type, ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new RdpProtocolException("RDP licensing blob is too large.");
        }
        w.U16LE(type);
        w.U16LE((ushort)value.Length);
        w.Bytes(value);
    }

    private byte[] Packet(byte type, ReadOnlySpan<byte> body)
    {
        if (body.Length > ushort.MaxValue - 4)
        {
            throw new RdpProtocolException("RDP licensing response is too large.");
        }
        var w = new ByteWriter(body.Length + 8);
        w.U16LE(SecurityFlags.License);
        w.U16LE(0);
        w.U8(type);
        w.U8(_version);
        w.U16LE((ushort)(body.Length + 4));
        w.Bytes(body);
        return w.ToArray();
    }

    public void Dispose() => _encryption?.Dispose();
}
