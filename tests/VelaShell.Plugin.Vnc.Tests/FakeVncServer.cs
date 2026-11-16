using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>
/// A scripted RFB server on loopback. Each accepted connection runs <c>script</c> with a <see cref="ServerConnection"/>
/// that has helpers for the handshake and server messages; the test drives the client side with the real
/// <see cref="VelaShell.Vnc.VncSession"/>.
/// </summary>
internal sealed class FakeVncServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<ServerConnection, int, Task> _script;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly List<Task> _connections = [];

    internal FakeVncServer(Func<ServerConnection, int, Task> script)
    {
        _script = script;
        _listener.Start();
        _accept = AcceptAsync();
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Exceptions thrown by the scripts (asserted empty by tests that care).</summary>
    internal List<Exception> Failures { get; } = [];

    private async Task AcceptAsync()
    {
        int index = 0;
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                int number = index++;
                lock (_connections)
                {
                    _connections.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            var connection = new ServerConnection(client, _stop.Token);
                            try { await _script(connection, number); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { lock (Failures) { Failures.Add(ex); } }
                        }
                    }));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        Task[] running;
        lock (_connections) { running = [.. _connections]; }
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(2000));
        _stop.Dispose();
    }
}

internal sealed class ServerConnection(TcpClient client, CancellationToken stop = default)
{
    internal TcpClient Client { get; } = client;
    internal Stream Stream { get; set; } = client.GetStream();

    /// <summary>Keeps the connection open until the server is disposed.</summary>
    internal async Task HoldAsync()
    {
        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
    }

    internal async Task WriteAsync(params byte[][] parts)
    {
        foreach (byte[] part in parts) { await Stream.WriteAsync(part); }
        await Stream.FlushAsync();
    }

    internal async Task<byte[]> ReadAsync(int count)
    {
        byte[] buffer = new byte[count];
        await Stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    internal async Task<byte> U8Async() => (await ReadAsync(1))[0];
    internal async Task<uint> U32Async() => BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4));

    /// <summary>Version exchange; returns the client's version string.</summary>
    internal async Task<string> VersionAsync(string server = "RFB 003.008\n")
    {
        await WriteAsync(Encoding.ASCII.GetBytes(server));
        return Encoding.ASCII.GetString(await ReadAsync(12));
    }

    /// <summary>RFB 3.7/3.8 security list; returns the type the client chose.</summary>
    internal async Task<int> OfferAsync(params byte[] types)
    {
        await WriteAsync([(byte)types.Length], types);
        return await U8Async();
    }

    internal Task SecurityResultAsync(uint status = 0, string? reason = null) =>
        reason is null ? WriteAsync(U32(status)) : WriteAsync(U32(status), Str(reason));

    /// <summary>ClientInit / ServerInit; returns the client's shared flag. Then reads SetPixelFormat, SetEncodings and the first request.</summary>
    internal async Task<(bool Shared, int[] Encodings)> InitAsync(int width = 64, int height = 48, string name = "test desktop")
    {
        bool shared = await U8Async() != 0;
        byte[] pixelFormat = [32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0];
        await WriteAsync(U16(width), U16(height), pixelFormat, Str(name));
        byte[] setPixelFormat = await ReadAsync(20);
        if (setPixelFormat[0] != 0) { throw new InvalidDataException("SetPixelFormat expected"); }
        byte[] header = await ReadAsync(4);
        if (header[0] != 2) { throw new InvalidDataException("SetEncodings expected"); }
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        byte[] list = await ReadAsync(count * 4);
        int[] encodings = [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(i * 4)))];
        byte[] request = await ReadAsync(10);
        if (request[0] != 3 || request[1] != 0) { throw new InvalidDataException("A full FramebufferUpdateRequest expected"); }
        return (shared, encodings);
    }

    /// <summary>Reads the next client message whose type is <paramref name="type"/>, skipping update requests.</summary>
    internal async Task<byte[]> ExpectAsync(byte type)
    {
        while (true)
        {
            byte got = await U8Async();
            byte[] body = got switch
            {
                3 => await ReadAsync(9),
                4 => await ReadAsync(7),
                5 => await ReadAsync(5),
                6 => await CutTextAsync(),
                150 => await ReadAsync(9),           // EnableContinuousUpdates
                248 => await FenceAsync(),           // ClientFence
                251 => await DesktopSizeAsync(),
                255 => await ReadAsync(11),
                _ => throw new InvalidDataException($"Unexpected client message {got}")
            };
            if (got == type) { return body; }
        }
    }

    private async Task<byte[]> CutTextAsync()
    {
        byte[] header = await ReadAsync(7);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(3));
        return [.. header, .. await ReadAsync(Math.Abs(length))];
    }

    /// <summary>A ClientFence after its type: 3 padding bytes, u32 flags, u8 length, payload.</summary>
    private async Task<byte[]> FenceAsync()
    {
        byte[] header = await ReadAsync(8);
        return [.. header, .. await ReadAsync(header[7])];
    }

    private async Task<byte[]> DesktopSizeAsync()
    {
        byte[] header = await ReadAsync(7);
        return [.. header, .. await ReadAsync(header[5] * 16)];
    }

    /// <summary>A FramebufferUpdate header followed by rectangles (each: header + body).</summary>
    internal static byte[] Update(params byte[][] rectangles) => [0, 0, .. U16(rectangles.Length), .. rectangles.SelectMany(static r => r)];

    internal static byte[] Rect(int x, int y, int w, int h, int encoding, params byte[] body) =>
        [.. U16(x), .. U16(y), .. U16(w), .. U16(h), .. S32(encoding), .. body];

    internal static byte[] U16(int v) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    internal static byte[] U32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    internal static byte[] S32(int v) => U32(unchecked((uint)v));
    internal static byte[] Str(string s) { byte[] t = Encoding.UTF8.GetBytes(s); return [.. U32((uint)t.Length), .. t]; }

    /// <summary>A BGRX pixel in the client's pixel format.</summary>
    internal static byte[] Pixel(byte r, byte g, byte b) => [b, g, r, 0];

    internal void Abort()
    {
        // Close the socket itself: disposing the TcpClient would shut the stream down gracefully (FIN) first.
        Client.Client.LingerState = new LingerOption(true, 0);
        Client.Client.Close();
    }
}
