using System.Text;

namespace VelaShell.Vnc.Security;

/// <summary>VNC authentication (RFC 6143 §7.2.2): the 16-byte challenge DES-encrypted with the password as key.</summary>
internal static class VncAuthentication
{
    /// <summary>
    /// The response to <paramref name="challenge"/>. The key is the first 8 bytes of the password (zero-padded) with the
    /// bits of every byte mirrored — the historical quirk all VNC servers implement. Characters beyond Latin-1 are sent as
    /// '?', which is what servers that store the password as bytes compare against.
    /// </summary>
    internal static byte[] Respond(ReadOnlySpan<byte> challenge, string password)
    {
        if (challenge.Length != 16)
        {
            throw new VncProtocolException($"VNC authentication challenge has {challenge.Length} bytes instead of 16.");
        }

        Span<byte> key = stackalloc byte[8];
        byte[] raw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < raw.Length; i++)
        {
            key[i] = Mirror(raw[i]);
        }

        byte[] response = challenge.ToArray();
        Des.EncryptEcb(key, response);
        return response;
    }

    private static byte Mirror(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            r = (r << 1) | ((b >> i) & 1);
        }
        return (byte)r;
    }
}
