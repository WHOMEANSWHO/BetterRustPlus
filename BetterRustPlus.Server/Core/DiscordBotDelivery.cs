namespace BetterRustPlus.Alerts;

public interface IDiscordBotTransport
{
    Task<DeliveryResult> SendAsync(ulong guildId, ulong channelId, AlertMessage message, CancellationToken cancellationToken);
}

/// <summary>Bounded bot delivery with an explicit channel allowlist and replaceable live transport.</summary>
public sealed class DiscordBotDelivery(DiscordBotOptions options)
{
    private IDiscordBotTransport? transport;
    public void SetTransport(IDiscordBotTransport? value) => Volatile.Write(ref transport, value);

    public async Task<DeliveryResult> SendAsync(ulong channelId, AlertMessage message, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.GuildId == 0 || options.AllowedChannelIds?.Contains(channelId) != true)
            return new(false, "Bot delivery channel is not configured.");
        var current = Volatile.Read(ref transport);
        if (current is null) return new(false, "Discord bot is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await current.SendAsync(options.GuildId, channelId, message, timeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "Discord bot delivery failed."); }
    }
}
