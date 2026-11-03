using BetterRustPlus.Alerts;
using Discord;
using Discord.WebSocket;

internal sealed class DiscordBotState(DiscordBotOptions options)
{
    private string state = "disabled";
    private readonly DiscordBotDelivery delivery = new(options);
    public string State => Volatile.Read(ref state);
    public void Set(string value) => Volatile.Write(ref state, value);
    public void SetTransport(IDiscordBotTransport? transport) => delivery.SetTransport(transport);
    public Task<DeliveryResult> SendAsync(ulong channelId, AlertMessage message, CancellationToken cancellationToken) =>
        delivery.SendAsync(channelId, message, cancellationToken);
}

/// <summary>Opt-in guild-only read commands. No privileged intents or message-content access.</summary>
internal sealed class DiscordBotWorker(DiscordBotOptions options, DiscordBotState state,
    AlertOptions alerts, RustSourceState source, ILogger<DiscordBotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            state.Set("connecting");
            using var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                AlwaysDownloadUsers = false,
                MessageCacheSize = 0,
                LogLevel = LogSeverity.Warning
            });
            int ready = 0;
            int registered = 0;
            client.Ready += () =>
            {
                Volatile.Write(ref ready, 1);
                state.SetTransport(new SocketBotTransport(client));
                state.Set(Volatile.Read(ref registered) == 1 ? "online" : "registering");
                return Task.CompletedTask;
            };
            client.Disconnected += _ =>
            {
                Volatile.Write(ref ready, 0);
                state.SetTransport(null);
                state.Set("disconnected");
                return Task.CompletedTask;
            };
            client.SlashCommandExecuted += async command =>
            {
                try
                {
                    var snapshot = source.Snapshot;
                    var team = source.Team;
                    var reply = DiscordBotCommands.Reply(command.Data.Name, command.GuildId, command.ChannelId.GetValueOrDefault(),
                        options, alerts, new(alerts.DryRun, snapshot.State, snapshot.LastSuccessfulPoll,
                            snapshot.IsFresh, snapshot.Capabilities), new(team.ObservedAt,
                            team.Members.Select(m => new BotTeamMember(m.Name, m.IsOnline, m.IsAlive, m.Grid)).ToArray()));
                    await command.RespondAsync(reply.Text, ephemeral: true, allowedMentions: AllowedMentions.None,
                        options: new RequestOptions { CancelToken = stoppingToken });
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception) { logger.LogWarning("Discord command response failed. Check bot channel permissions and connectivity."); }
            };

            try
            {
                await client.LoginAsync(TokenType.Bot, options.Token).WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref ready) == 1 && Volatile.Read(ref registered) == 0)
                    {
                        try
                        {
                            var guild = client.GetGuild(options.GuildId);
                            if (guild is null) throw new InvalidOperationException("Configured guild unavailable.");
                            var request = new RequestOptions { CancelToken = stoppingToken };
                            // Upsert only this app's commands; never overwrite a guild's
                            // unrelated commands or register globally.
                            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                                .WithName("brp-status").WithDescription("Show BetterRustPlus source and delivery status.").Build(), request)
                                .WaitAsync(TimeSpan.FromSeconds(20), stoppingToken);
                            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                                .WithName("brp-alerts").WithDescription("Show configured BetterRustPlus alert rules.").Build(), request)
                                .WaitAsync(TimeSpan.FromSeconds(20), stoppingToken);
                            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                                .WithName("brp-team").WithDescription("Show the latest observed team grids and status.").Build(), request)
                                .WaitAsync(TimeSpan.FromSeconds(20), stoppingToken);
                            Volatile.Write(ref registered, 1);
                            state.Set("online");
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception)
                        {
                            state.Set("registration-failed");
                            logger.LogWarning("Discord command registration failed. Check the configured guild and application-command invite scope; retrying.");
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                state.Set("unavailable");
                // Discord exception/log details can contain request credentials. Do not log them.
                logger.LogWarning("Discord bot unavailable. Check its private token, invite and connectivity; reconnecting in 15 seconds.");
            }
            finally
            {
                state.SetTransport(null);
                try { await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await client.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        state.Set("stopped");
    }

    private sealed class SocketBotTransport(DiscordSocketClient client) : IDiscordBotTransport
    {
        public async Task<DeliveryResult> SendAsync(ulong guildId, ulong channelId, AlertMessage message, CancellationToken cancellationToken)
        {
            if (client.ConnectionState != ConnectionState.Connected ||
                client.GetChannel(channelId) is not SocketTextChannel channel || channel.Guild.Id != guildId)
                return new(false, "Bot delivery channel is unavailable.");
            var mentions = message.MentionEveryone ? new AllowedMentions(AllowedMentionTypes.Everyone) : AllowedMentions.None;
            await channel.SendMessageAsync(message.Content, allowedMentions: mentions,
                options: new RequestOptions { CancelToken = cancellationToken });
            return new(true);
        }
    }
}
