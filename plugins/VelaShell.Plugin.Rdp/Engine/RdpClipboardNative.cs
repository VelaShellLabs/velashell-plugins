using System.Runtime.InteropServices;
using Devolutions.IronRdp;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>Optional versioned exports from the same library that owns the IronRDP handles.</summary>
internal static class RdpClipboardNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AttachChannel(IntPtr connector);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NextPacket(IntPtr stage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SendPacket(IntPtr stage, IntPtr bytes, nuint length);
    private sealed record Api(AttachChannel Attach, NextPacket Next, SendPacket Send, NextPacket PollDrives);
    private static readonly Lazy<Api?> Exports = new(Load);
    internal static bool IsAvailable => Exports.Value is not null;

    private static Api? Load()
    {
        if (!NativeLibrary.TryLoad("DevolutionsIronRdp", typeof(ClientConnector).Assembly, null, out IntPtr library)) return null;
        // The generated wrappers retain pointers into this module; retain the load reference too.
        if (!NativeLibrary.TryGetExport(library, "VelaClipboard_abi_version", out IntPtr version) ||
            Marshal.GetDelegateForFunctionPointer<Version>(version)() != 1)
        {
            NativeLibrary.Free(library);
            return null;
        }
        return new(Marshal.GetDelegateForFunctionPointer<AttachChannel>(NativeLibrary.GetExport(library, "VelaClipboard_attach")),
            Marshal.GetDelegateForFunctionPointer<NextPacket>(NativeLibrary.GetExport(library, "VelaClipboard_next")),
            Marshal.GetDelegateForFunctionPointer<SendPacket>(NativeLibrary.GetExport(library, "VelaClipboard_send")),
            Marshal.GetDelegateForFunctionPointer<NextPacket>(NativeLibrary.GetExport(library, "VelaRdpdr_poll")));
    }

    internal static unsafe void Attach(ClientConnector connector)
    {
        if (Exports.Value is not { } api || api.Attach((IntPtr)connector.AsFFI()) != 0)
            throw new IOException("The RDP clipboard native bridge is unavailable.");
    }

    internal static unsafe byte[] PollDrives(ActiveStage stage)
    {
        IntPtr packet = Exports.Value!.PollDrives((IntPtr)stage.AsFFI());
        if (packet == IntPtr.Zero) throw new IOException("RDP drive completion failed.");
        return Copy(packet);
    }

    internal static unsafe byte[]? Next(ActiveStage stage)
    {
        IntPtr packet = Exports.Value!.Next((IntPtr)stage.AsFFI());
        if (packet == IntPtr.Zero) return null;
        byte[] bytes = Copy(packet);
        if (bytes.AsSpan().SequenceEqual("VELAFAT"u8)) throw new IOException("RDP clipboard native state is unavailable.");
        return bytes;
    }

    internal static unsafe byte[] Send(ActiveStage stage, byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            IntPtr packet = Exports.Value!.Send((IntPtr)stage.AsFFI(), (IntPtr)pointer, (nuint)bytes.Length);
            if (packet == IntPtr.Zero) throw new IOException("RDP clipboard channel encoding failed.");
            return Copy(packet);
        }
    }

    // This seven-byte marker is generated only by the bridge, never relayed from the wire
    // (wire messages shorter than eight bytes fault the channel before enqueue).
    internal static bool IsChannelFault(ReadOnlySpan<byte> packet) => packet.SequenceEqual("VELACB!"u8);

    private static unsafe byte[] Copy(IntPtr pointer)
    {
        using var value = new VecU8((Devolutions.IronRdp.Raw.VecU8*)pointer);
        int length = checked((int)value.GetSize());
        RdpFraming.ValidateLength(length, 0);
        byte[] bytes = new byte[length];
        value.Fill(bytes);
        return bytes;
    }
}

internal interface IRdpClipboardSession
{
    bool ClipboardEnabled { get; }
    bool ClipboardUnavailable { get; }
    void PauseClipboard();
    void SetClipboardFocus(bool focused);
    Task<bool> SynchronizeClipboardAsync(string? text, CancellationToken cancellationToken);
    event Action<string>? RemoteTextAvailable;
    event System.Action? ClipboardFailed;
}
