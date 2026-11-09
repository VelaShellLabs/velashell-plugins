using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPERP 2.2.2: baseline Remote Programs (no local move/size or enhanced GFX).</summary>
internal sealed class RailChannel(RemoteAppOptions options, int width, int height,
    Func<byte[], CancellationToken, Task> send, Func<(int Width, int Height)>? negotiatedSize = null)
{
    private bool _initialized;
    private bool _executed;
    private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Launched => _launched.Task;
    internal void Stop(Exception? error = null)
    {
        if (error is null) { _launched.TrySetCanceled(); }
        else { _launched.TrySetException(error); }
    }

    internal async Task ProcessAsync(byte[] data, CancellationToken ct)
    {
        // Parse synchronously: no ref structs survive an asynchronous send.
        List<(ushort Type, byte[] Body)> orders = Parse(data);
        foreach ((ushort type, byte[] body) in orders)
        {
            if (type is 5 or 0x13)
            {
                if (_initialized) { throw new RdpProtocolException("Duplicate RAIL handshake."); }
                // HandshakeEx bit 0 is TS_RAIL_ORDER_HANDSHAKEEX_FLAGS_HIDEF (high-DPI support),
                // not an enhanced graphics request. Enhanced RAIL graphics are negotiated separately;
                // classic RAIL remains valid for either handshake form.
                RequireLength(body, type == 5 ? 4 : 8);
                if (negotiatedSize?.Invoke() is { Width: >= 200, Height: >= 200 } size)
                {
                    width = size.Width;
                    height = size.Height;
                }
                _initialized = true;
                await send(Packet(5, w => w.U32LE(7601)), ct).ConfigureAwait(false);
                // Advertise only features implemented by this client: server window cloaking and icon suppression.
                // In particular, do not advertise local move/size or z-order synchronization.
                await send(Packet(0x0b, w => w.U32LE(0x600)), ct).ConfigureAwait(false);
                // Keep the startup sequence and wire sizes compatible with the reference RAIL client.
                await send(Packet(3, w =>
                {
                    w.U32LE(0x43); // SPI_SET_HIGH_CONTRAST
                    w.U32LE(0x7e); // high-contrast flags used by the reference client
                    w.U32LE(0);    // no colour-scheme string
                    w.U16LE(0);
                }), ct).ConfigureAwait(false);
                foreach (uint parameter in new uint[] { 0x21, 0x45, 0x25, 0x100b })
                {
                    await send(Packet(3, w => { w.U32LE(parameter); w.U8(0); }), ct).ConfigureAwait(false);
                }
                await send(Packet(3, w => { w.U32LE(0x2f); Rectangle(w, 0, 0, width, height); }), ct).ConfigureAwait(false);
                await send(BuildExecute(options), ct).ConfigureAwait(false);
            }
            else
            {
                if (!_initialized) { throw new RdpProtocolException("RAIL order arrived before its handshake."); }
                ProcessServerOrder(type, body);
            }
        }
    }

    private void ProcessServerOrder(ushort type, byte[] body)
    {
        var r = new ByteReader(body);
        switch (type)
        {
            case 0x80:
                if (_executed) { throw new RdpProtocolException("Unexpected duplicate RAIL execute result."); }
                _ = r.U16LE();
                ushort result = r.U16LE();
                uint raw = r.U32LE();
                r.Skip(2);
                int length = r.U16LE();
                if (length is 0 or > 520 || (length & 1) != 0) { throw new RdpProtocolException("Invalid RAIL execute result name."); }
                string program = Encoding.Unicode.GetString(r.Take(length));
                if (!string.Equals(program, options.Program, StringComparison.Ordinal)) { throw new RdpProtocolException("RAIL execute result does not match the requested program."); }
                if (result != 0) { throw new RdpProtocolException($"RemoteApp launch was rejected (RAIL result 0x{result:X4}, OS result 0x{raw:X8})."); }
                _executed = true;
                break;
            case 3:
                uint parameter = r.U32LE();
                if (parameter is not (0x11 or 0x77)) { throw new RdpProtocolException("Unsupported server RAIL system parameter."); }
                _ = r.U8(); // screen saver state is remote-only; never change the host's policy
                break;
            case 0x0a:
                // Constraints describe the server-managed resize loop; client does not advertise local move/size.
                RequireLength(body, 20);
                r.Skip(20);
                break;
            case 0x09:
                RequireLength(body, 12);
                r.Skip(12); // MS-RDPERP 3.2.5.2.8: ignore when local move/size is not advertised
                break;
            default:
                throw new RdpProtocolException($"Unsupported or unnegotiated server RAIL order 0x{type:X4}.");
        }
        if (r.Remaining != 0) { throw new RdpProtocolException("Trailing bytes in RAIL order."); }
        if (type == 0x80) { _launched.TrySetResult(); }
    }

    internal Task ActivateAsync(uint id, bool active, CancellationToken ct) =>
        send(Packet(2, w => { w.U32LE(id); w.U8(active ? (byte)1 : (byte)0); }), ct);
    internal Task SystemCommandAsync(uint id, ushort command, CancellationToken ct) =>
        send(Packet(4, w => { w.U32LE(id); w.U16LE(command); }), ct);
    internal Task MoveAsync(uint id, int x, int y, int w, int h, CancellationToken ct)
    {
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || (long)x + w > width || (long)y + h > height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "RemoteApp bounds must fit the negotiated work area.");
        }
        return send(Packet(8, p => { p.U32LE(id); Rectangle(p, x, y, w, h); }), ct);
    }

    internal static byte[] BuildExecute(RemoteAppOptions options)
    {
        options.Validate();
        byte[] program = Encoding.Unicode.GetBytes(options.Program);
        byte[] directory = Encoding.Unicode.GetBytes(options.WorkingDirectory);
        byte[] arguments = Encoding.Unicode.GetBytes(options.Arguments);
        return Packet(1, w =>
        {
            w.U16LE(0);
            w.U16LE((ushort)program.Length); w.U16LE((ushort)directory.Length); w.U16LE((ushort)arguments.Length);
            w.Bytes(program); w.Bytes(directory); w.Bytes(arguments);
        });
    }

    private static List<(ushort, byte[])> Parse(byte[] data)
    {
        if (data.Length > ushort.MaxValue) { throw new RdpProtocolException("RAIL channel message exceeds its limit."); }
        var r = new ByteReader(data);
        var result = new List<(ushort, byte[])>();
        while (r.Remaining != 0)
        {
            ushort type = r.U16LE();
            int length = r.U16LE();
            if (length < 4 || result.Count >= 256) { throw new RdpProtocolException("Invalid RAIL order framing."); }
            result.Add((type, r.Take(length - 4).ToArray()));
        }
        return result;
    }
    private static void RequireLength(byte[] body, int length)
    {
        if (body.Length != length) { throw new RdpProtocolException("Invalid RAIL order size."); }
    }
    private static void Rectangle(ByteWriter w, int x, int y, int width, int height)
    {
        w.U16LE((ushort)x); w.U16LE((ushort)y); w.U16LE((ushort)(x + width)); w.U16LE((ushort)(y + height));
    }
    internal static byte[] Packet(ushort type, Action<ByteWriter> body)
    {
        var w = new ByteWriter();
        w.U16LE(type); w.U16LE(0); body(w); w.PatchU16LE(2, checked((ushort)w.Length));
        return w.ToArray();
    }
}
