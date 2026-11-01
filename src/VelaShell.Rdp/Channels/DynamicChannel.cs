namespace VelaShell.Rdp.Channels;

internal interface IDynamicChannelHandler
{
    Task OnOpenAsync();
    Task OnMessageAsync(byte[] message);
    Task OnCloseAsync();
}

internal sealed class DynamicChannel(DrdynvcChannel owner, uint id)
{
    internal uint Id => id;
    internal Task CloseAsync() => owner.CloseChannelAsync(this);
    internal Task SendAsync(byte[] message, CancellationToken ct = default) => owner.SendAsync(this, message, ct);
}
