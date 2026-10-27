using Devolutions.IronRdp;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>Offline native create/call/dispose gate, also invoked inside the real SDK plugin ALC.</summary>
internal static class RdpNativeSmoke
{
    public static string Run()
    {
        using var builder = ConfigBuilder.New();
        builder.WithUsernameAndPassword("native-smoke", "not-a-real-credential");
        builder.SetDesktopSize(height: 200, width: 320);
        builder.SetEnableTls(true);
        builder.SetEnableCredssp(true);
        builder.SetClientName("VelaShell");
        builder.SetClientDir("C:\\Windows\\System32\\mstscax.dll");
        builder.SetKeyboardType(KeyboardType.IbmEnhanced);
        using var performance = PerformanceFlags.NewDefault();
        builder.SetPerformanceFlags(performance);
        using var config = builder.Build();
        using var connector = ClientConnector.New(config, "127.0.0.1:49152");
        using var output = WriteBuf.New();
        using var written = connector.InitiateWithSecurityProtocol(2, output);
        using var size = written.GetSize();
        int length = checked((int)size.Get());
        if (length is < 11 or > 65535) throw new InvalidOperationException("Invalid native negotiation packet.");
        var packet = new byte[length];
        output.ReadIntoBuf(packet);
        // Server selected HYBRID: TPKT/X224 Connection Confirm + RDP_NEG_RSP.
        byte[] confirm = [3, 0, 0, 19, 14, 0xd0, 0, 0, 0, 0, 0, 2, 0, 8, 0, 2, 0, 0, 0];
        output.Clear();
        using var accepted = connector.Step(confirm, output);
        connector.EnsureSelectedHybrid();
        if (!connector.ShouldPerformSecurityUpgrade()) throw new InvalidOperationException("Native TLS gate did not activate.");
        connector.MarkSecurityUpgradeAsDone();
        using var init = CredsspSequence.Init(connector, "localhost", [1, 2, 3], null);
        using var credssp = init.GetCredsspSequence();
        using var request = init.GetTsRequest();
        // Generator borrows credssp; it is destroyed before any other sequence calls.
        using (var generator = credssp.ProcessTsRequest(request))
        using (var state = generator.Start())
        {
            if (!state.IsCompleted() || state.IsSuspended()) throw new InvalidOperationException("NTLM unexpectedly required an external authentication endpoint.");
            using var clientState = state.GetClientStateIfCompleted();
        }
        connector.MarkCredsspAsDone(); // Offline state-machine smoke only: no credentials were sent to a peer.
        output.Clear();
        using (var initialSettings = connector.StepNoInput(output))
        {
            byte[] settings = RdpSessionFactory.ReadWritten(initialSettings, output);
            bool dimensionsFound = false;
            for (int offset = 0; offset + 12 <= settings.Length; offset++)
            {
                // TS_UD_CS_CORE: type 0xC001, length, version, desktopWidth, desktopHeight.
                if (settings[offset] == 1 && settings[offset + 1] == 0xc0
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(settings.AsSpan(offset + 4)) is 0x00080001 or 0x00080004)
                {
                    int width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(settings.AsSpan(offset + 8));
                    int height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(settings.AsSpan(offset + 10));
                    if (width != 320 || height != 200) throw new InvalidOperationException("Native desktop width/height were reversed.");
                    dimensionsFound = true;
                    break;
                }
            }
            if (!dimensionsFound) throw new InvalidOperationException("Native client desktop dimensions were not encoded.");
        }
        using var image = DecodedImage.New(PixelFormat.RgbA32, 2, 2);
        using (var data = image.GetData())
        {
            if (data.GetSize() != 16) throw new InvalidOperationException("Invalid native pixel layout.");
            data.Fill(new byte[16]);
        }
        using var input = InputDatabase.New();
        using var scan = Scancode.FromU16(0x1e);
        using var operation = scan.AsOperationKeyPressed();
        using var events = input.Apply(operation);
        return $"IronRDP native TLS/NLA/input/image create-call-dispose passed; X224={length}; RGBA=16";
    }
}
