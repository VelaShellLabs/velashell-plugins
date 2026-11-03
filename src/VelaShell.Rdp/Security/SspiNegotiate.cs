using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Rdp.Security;

/// <summary>
/// Drives the native Windows <b>Negotiate</b> security package for CredSSP/NLA, exactly as Windows' own RDP
/// client does. The difference from forcing the NTLM package is the native negotiation itself: with a
/// DNS host name it targets <c>TERMSRV/&lt;host&gt;</c> so Negotiate can use Kerberos for domain servers; with an
/// IP literal it keeps the null target (Kerberos is impossible there and Negotiate settles on NTLM either way —
/// observed: explicit credentials give a bare NTLM first token even with the null target). Sealing uses the same
/// context's EncryptMessage/DecryptMessage.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SspiNegotiate : INlaSecurityContext
{
    private const int SECPKG_CRED_OUTBOUND = 2;
    private const int SECURITY_NATIVE_DREP = 0x10;
    private const int SECPKG_ATTR_SIZES = 0;
    private const int SECBUFFER_VERSION = 0;
    private const int SECBUFFER_EMPTY = 0, SECBUFFER_DATA = 1, SECBUFFER_TOKEN = 2, SECBUFFER_PADDING = 9, SECBUFFER_STREAM = 10;
    private const int SEC_WINNT_AUTH_IDENTITY_UNICODE = 2;
    private const uint SEC_E_OK = 0;
    private const uint SEC_I_CONTINUE_NEEDED = 0x00090312;
    private const uint SEC_I_COMPLETE_AND_CONTINUE = 0x00090314;
    private const uint SEC_I_COMPLETE_NEEDED = 0x00090313;

    // Match what Windows' RDP client / FreeRDP request so the server offers the same features.
    private const int ContextReq = 0x00000001 /*DELEGATE*/ | 0x00000002 /*MUTUAL_AUTH*/ | 0x00000004 /*REPLAY_DETECT*/
        | 0x00000008 /*SEQUENCE_DETECT*/ | 0x00000010 /*CONFIDENTIALITY*/ | 0x00000020 /*USE_SESSION_KEY*/ | 0x00010000 /*INTEGRITY*/;

    private readonly IntPtr _cred = Marshal.AllocHGlobal(IntPtr.Size * 2);
    private readonly IntPtr _ctx = Marshal.AllocHGlobal(IntPtr.Size * 2);
    private bool _hasContext;
    private readonly bool _credValid;
    private bool _ctxValid;
    private int _trailer;
    private int _blockSize;
    private bool _disposed;

    private readonly string? _target;

    /// <summary>
    /// <paramref name="targetHost"/> as a DNS name becomes the service target <c>TERMSRV/&lt;host&gt;</c>, which is
    /// what lets Negotiate pick Kerberos for a domain server (as mstsc does) instead of always NTLM. An IP literal
    /// keeps the null target: Kerberos cannot work for it anyway, and the null target is the established behaviour.
    /// </summary>
    internal unsafe SspiNegotiate(string domain, string username, string password, string targetHost = "")
    {
        _target = targetHost.Length > 0 && !System.Net.IPAddress.TryParse(targetHost.Trim('[', ']'), out _) ? $"TERMSRV/{targetHost}" : null;
        new Span<byte>((void*)_cred, IntPtr.Size * 2).Clear();
        new Span<byte>((void*)_ctx, IntPtr.Size * 2).Clear();

        int rc;
        fixed (char* userPointer = username, domainPointer = domain, passwordPointer = password)
        {
            var identity = new SEC_WINNT_AUTH_IDENTITY
            {
                User = userPointer,
                UserLength = username.Length,
                Domain = domainPointer,
                DomainLength = domain.Length,
                Password = passwordPointer,
                PasswordLength = password.Length,
                Flags = SEC_WINNT_AUTH_IDENTITY_UNICODE
            };
            rc = AcquireCredentialsHandle(null, "Negotiate", SECPKG_CRED_OUTBOUND, IntPtr.Zero, ref identity,
                IntPtr.Zero, IntPtr.Zero, _cred, out _);
        }
        if (rc != 0)
        {
            throw new RdpProtocolException($"NLA could not acquire credentials (0x{rc:X8}).");
        }

        _credValid = true;
    }

    public bool IsComplete { get; private set; }

    public unsafe byte[] Step(byte[] incoming)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Output token: let the package size it; Negotiate's max is well under 64 KiB.
        byte[] outBytes = new byte[65536];
        fixed (byte* outPtr = outBytes)
        fixed (byte* inPtr = incoming.Length > 0 ? incoming : new byte[1])
        {
            var outBuf = new SecBuffer { cbBuffer = outBytes.Length, BufferType = SECBUFFER_TOKEN, pvBuffer = (IntPtr)outPtr };
            var outDesc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 1, pBuffers = (IntPtr)(&outBuf) };

            var inBuf = new SecBuffer { cbBuffer = incoming.Length, BufferType = SECBUFFER_TOKEN, pvBuffer = (IntPtr)inPtr };
            var inDesc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 1, pBuffers = (IntPtr)(&inBuf) };

            // The service target enables Kerberos for DNS names; IP literals keep the null target (see constructor).
            uint rc = (uint)InitializeSecurityContext(_cred, _hasContext ? _ctx : IntPtr.Zero, _target, ContextReq, 0,
                SECURITY_NATIVE_DREP, _hasContext ? (IntPtr)(&inDesc) : IntPtr.Zero, 0, _ctx, ref outDesc, out _, out _);

            _hasContext = true;
            _ctxValid = true;
            if (rc is SEC_E_OK)
            {
                IsComplete = true;
                QuerySizes();
            }
            else if (rc is not (SEC_I_CONTINUE_NEEDED or SEC_I_COMPLETE_AND_CONTINUE or SEC_I_COMPLETE_NEEDED))
            {
                throw new RdpProtocolException($"NLA InitializeSecurityContext failed (0x{rc:X8}).");
            }

            return outBuf.cbBuffer == 0 ? [] : outBytes.AsSpan(0, outBuf.cbBuffer).ToArray();
        }
    }

    private unsafe void QuerySizes()
    {
        Span<int> sizes = stackalloc int[4];   // cbMaxToken, cbMaxSignature, cbBlockSize, cbSecurityTrailer
        fixed (int* p = sizes)
        {
            int rc = QueryContextAttributes(_ctx, SECPKG_ATTR_SIZES, (IntPtr)p);
            if (rc != 0)
            {
                throw new RdpProtocolException($"NLA could not query context sizes (0x{rc:X8}).");
            }
        }
        _blockSize = sizes[2];
        _trailer = sizes[3];
    }

    public unsafe byte[] Seal(byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] token = new byte[_trailer];
        byte[] data = (byte[])plaintext.Clone();
        byte[] padding = new byte[Math.Max(_blockSize, 16)];
        fixed (byte* tPtr = token.Length > 0 ? token : new byte[1])
        fixed (byte* dPtr = data.Length > 0 ? data : new byte[1])
        fixed (byte* pPtr = padding)
        {
            Span<SecBuffer> buffers =
            [
                new() { cbBuffer = token.Length, BufferType = SECBUFFER_TOKEN, pvBuffer = (IntPtr)tPtr },
                new() { cbBuffer = data.Length, BufferType = SECBUFFER_DATA, pvBuffer = (IntPtr)dPtr },
                new() { cbBuffer = padding.Length, BufferType = SECBUFFER_PADDING, pvBuffer = (IntPtr)pPtr }
            ];
            fixed (SecBuffer* bp = buffers)
            {
                var desc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 3, pBuffers = (IntPtr)bp };
                int rc = EncryptMessage(_ctx, 0, ref desc, 0);
                if (rc != 0)
                {
                    throw new RdpProtocolException($"NLA EncryptMessage failed (0x{rc:X8}).");
                }

                // CredSSP carries the sealed value as signature(token) || ciphertext(data) [|| padding].
                byte[] result = new byte[buffers[0].cbBuffer + buffers[1].cbBuffer + buffers[2].cbBuffer];
                int o = 0;
                foreach (SecBuffer b in buffers)
                {
                    new Span<byte>((void*)b.pvBuffer, b.cbBuffer).CopyTo(result.AsSpan(o));
                    o += b.cbBuffer;
                }

                return result;
            }
        }
    }

    public unsafe byte[] Unseal(byte[] wrapped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] buffer = (byte[])wrapped.Clone();
        fixed (byte* bPtr = buffer.Length > 0 ? buffer : new byte[1])
        {
            Span<SecBuffer> buffers =
            [
                new() { cbBuffer = buffer.Length, BufferType = SECBUFFER_STREAM, pvBuffer = (IntPtr)bPtr },
                new() { cbBuffer = 0, BufferType = SECBUFFER_DATA, pvBuffer = IntPtr.Zero }
            ];
            fixed (SecBuffer* bp = buffers)
            {
                var desc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 2, pBuffers = (IntPtr)bp };
                int rc = DecryptMessage(_ctx, ref desc, 0, out _);
                if (rc != 0)
                {
                    throw new RdpProtocolException($"NLA DecryptMessage failed (0x{rc:X8}).");
                }

                SecBuffer dataBuf = buffers[1].BufferType == SECBUFFER_DATA && buffers[1].cbBuffer > 0 ? buffers[1] : buffers[0];
                return new Span<byte>((void*)dataBuf.pvBuffer, dataBuf.cbBuffer).ToArray();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ctxValid)
        {
            _ = DeleteSecurityContext(_ctx);
        }
        if (_credValid)
        {
            _ = FreeCredentialsHandle(_cred);
        }
        Marshal.FreeHGlobal(_ctx);
        Marshal.FreeHGlobal(_cred);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBuffer
    {
        public int cbBuffer;
        public int BufferType;
        public IntPtr pvBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBufferDesc
    {
        public int ulVersion;
        public int cBuffers;
        public IntPtr pBuffers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SEC_WINNT_AUTH_IDENTITY
    {
        public char* User;
        public int UserLength;
        public char* Domain;
        public int DomainLength;
        public char* Password;
        public int PasswordLength;
        public int Flags;
    }

    [LibraryImport("secur32.dll", EntryPoint = "AcquireCredentialsHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int AcquireCredentialsHandle(string? pszPrincipal, string pszPackage, int fCredentialUse,
        IntPtr pvLogonID, ref SEC_WINNT_AUTH_IDENTITY pAuthData, IntPtr pGetKeyFn, IntPtr pvGetKeyArgument, IntPtr phCredential, out long ptsExpiry);

    [LibraryImport("secur32.dll", EntryPoint = "InitializeSecurityContextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int InitializeSecurityContext(IntPtr phCredential, IntPtr phContext, string? pszTargetName,
        int fContextReq, int Reserved1, int TargetDataRep, IntPtr pInput, int Reserved2, IntPtr phNewContext,
        ref SecBufferDesc pOutput, out int pfContextAttr, out long ptsExpiry);

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int EncryptMessage(IntPtr phContext, uint fQOP, ref SecBufferDesc pMessage, uint MessageSeqNo);

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int DecryptMessage(IntPtr phContext, ref SecBufferDesc pMessage, uint MessageSeqNo, out uint pfQOP);

    [LibraryImport("secur32.dll", EntryPoint = "QueryContextAttributesW", SetLastError = true)]
    private static partial int QueryContextAttributes(IntPtr phContext, int ulAttribute, IntPtr pBuffer);

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int DeleteSecurityContext(IntPtr phContext);

    [LibraryImport("secur32.dll", SetLastError = true)]
    private static partial int FreeCredentialsHandle(IntPtr phCredential);
}
