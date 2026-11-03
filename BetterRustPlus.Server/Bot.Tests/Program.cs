using BetterRustPlus.Alerts;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var options = new DiscordBotOptions { Enabled = true, Token = "PRIVATE_BOT_TOKEN", GuildId = 123, AllowedChannelIds = [456] };
var alerts = new AlertOptions
{
    AllowedServerIds = ["our-server"],
    Rules = [new AlertRule { Id = "raid", Kinds = ["raid"], MentionEveryone = true, CooldownSeconds = 60 }],
    Targets = new() { ["primary"] = new DeliveryTarget { WebhookUrl = "https://discord.com/api/webhooks/PRIVATE/WEBHOOK" } }
};
var status = new BotRuntimeStatus(true, "disabled", null, false, []);
BotCommandReply Reply(string command = "brp-status", ulong? guild = 123, ulong channel = 456) =>
    DiscordBotCommands.Reply(command, guild, channel, options, alerts, status);
Check(options.Validate() is null, "Valid options rejected.");
Check(new DiscordBotOptions().Validate() is null, "Disabled bot must not require credentials.");
Check(new DiscordBotOptions { Enabled = true }.Validate() is not null, "Missing token accepted.");
Check(new DiscordBotOptions { Enabled = true, Token = "two words", GuildId = 123, AllowedChannelIds = [456] }.Validate() is not null, "Whitespace token accepted.");
Check(new DiscordBotOptions { Enabled = true, Token = "token", AllowedChannelIds = [456] }.Validate() is not null, "Missing guild accepted.");
Check(new DiscordBotOptions { Enabled = true, Token = "token", GuildId = 123 }.Validate() is not null, "Empty channel allowlist accepted.");
Check(new DiscordBotOptions { Enabled = true, Token = "token", GuildId = 123, AllowedChannelIds = null! }.Validate() is not null, "Null allowlist accepted.");
Check(new DiscordBotOptions { Enabled = true, Token = "token", GuildId = 123, AllowedChannelIds = [0] }.Validate() is not null, "Invalid channel accepted.");
Check(Reply(guild: null).Text.Contains("unavailable"), "Direct messages authorized.");
Check(Reply(guild: 999).Text.Contains("unavailable"), "Wrong guild authorized.");
Check(Reply(channel: 999).Text.Contains("unavailable"), "Wrong channel authorized.");
options.Enabled = false;
Check(Reply().Text.Contains("unavailable"), "Disabled bot authorized.");
options.Enabled = true;
Check(Reply().Text.Contains("dry-run") && Reply().Text.Contains("no fresh poll"), "Disabled/stale status misrepresented.");
Check(Reply().Text.Contains("unavailable") && Reply().Text.Contains("memory-only"), "Missing poll or durability limitation omitted.");
status = new(false, "degraded", new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), true, ["team-status"]);
Check(Reply().Text.Contains("enabled") && Reply().Text.Contains("degraded") && Reply().Text.Contains("recent poll"), "Live degraded source misrepresented.");
Check(Reply().Text.Contains("2026-11-01 23:00:00 UTC"), "Poll timestamp not visible in UTC.");
Check(Reply("brp-alerts").Text.Contains("raid") && Reply("brp-alerts").Text.Contains("everyone mention on"), "Rule settings missing.");
Check(Reply("brp-alerts").Text.Contains("Dry-run") && Reply("brp-alerts").Text.Contains("not detector availability"), "Rules incorrectly prove detector or delivery.");
Check(Reply("unknown").Text.Contains("Unknown"), "Unknown command accepted silently.");
foreach (var command in new[] { "brp-status", "brp-alerts", "brp-team", "unknown" })
{
    var reply = Reply(command);
    Check(reply.Ephemeral && !reply.Text.Contains("PRIVATE") && !reply.Text.Contains("WEBHOOK") && !reply.Text.Contains("@everyone"), "Response leaks credentials, pings or is public.");
}
alerts.Rules[0].Id = "@everyone `unsafe`\nrule";
alerts.Rules[0].Kinds = ["@here"];
Check(!Reply("brp-alerts").Text.Contains('@') && !Reply("brp-alerts").Text.Contains('`'), "Configured display values can insert mentions/formatting.");
alerts.Rules = Enumerable.Range(0, 32).Select(i => new AlertRule { Id = new string('r', 80) + i, Kinds = ["cargo", "raid", "airdrop", "deepsea"], ServerIds = [new string('s', 128)] }).ToList();
Check(Reply("brp-alerts").Text.Length <= 1900 && Reply("brp-alerts").Text.Contains("more rules"), "Large rule list exceeds Discord limits or hides truncation.");
alerts.Rules.Clear();
Check(Reply("brp-alerts").Text.Contains("No alert rules"), "Empty state missing.");
Check(Reply("brp-team").Text.Contains("unavailable"), "Unobserved team reported available.");
var team = new BotTeamSnapshot(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero),
    [new("Nathan @everyone", true, false, "G14"), new("Friend", false, true, null)]);
BotCommandReply TeamReply(ulong? guild = 123, ulong channel = 456) =>
    DiscordBotCommands.Reply("brp-team", guild, channel, options, alerts, status, team);
Check(TeamReply().Text.Contains("last seen at G14") && TeamReply().Text.Contains("online, dead"), "Team grid/status missing.");
Check(!TeamReply().Text.Contains('@') && TeamReply().Ephemeral, "Team name can ping or response is public.");
Check(TeamReply().Text.Contains("position unavailable") && TeamReply().Text.Contains("offline"), "Unknown positions hidden or inferred.");
Check(TeamReply().Text.Contains("2026-11-01 23:00:00 UTC"), "Team observation timestamp missing.");
Check(TeamReply(guild: null).Text.Contains("unavailable") && !TeamReply(guild: null).Text.Contains("Nathan"), "Team leaked into direct messages.");
Check(TeamReply(channel: 999).Text.Contains("unavailable") && !TeamReply(channel: 999).Text.Contains("G14"), "Team leaked into unauthorized channel.");
status = status with { SourceFresh = false };
Check(TeamReply().Text.Contains("stale or disconnected"), "Stale team locations presented as live.");
team = team with { Members = Enumerable.Range(0, 100).Select(i => new BotTeamMember(new string('n', 120) + i, true, true, "AA100")).ToArray() };
Check(TeamReply().Text.Length <= 1900 && TeamReply().Text.Contains("more members"), "Large team response unbounded or silently truncated.");
team = team with { Members = [] };
Check(TeamReply().Text.Contains("no members"), "Empty observed team state missing.");
var delivery = new DiscordBotDelivery(options);
var alert = new AlertMessage("Raid alarm", false);
Check(!(await delivery.SendAsync(456, alert, default)).Success, "Unavailable bot claimed delivery.");
var transport = new FakeBotTransport();
delivery.SetTransport(transport);
Check(!(await delivery.SendAsync(999, alert, default)).Success && transport.Calls == 0, "Unapproved channel reached Discord transport.");
options.Enabled = false;
Check(!(await delivery.SendAsync(456, alert, default)).Success && transport.Calls == 0, "Disabled bot sent an alert.");
options.Enabled = true;
Check((await delivery.SendAsync(456, alert, default)).Success && transport.Calls == 1, "Configured bot failed delivery.");
Check(transport.Guild == 123 && transport.Channel == 456 && transport.Message?.MentionEveryone == false, "Delivery bypassed guild/channel routing or changed mention policy.");
Check((await delivery.SendAsync(456, alert with { MentionEveryone = true }, default)).Success && transport.Message?.MentionEveryone == true, "Explicit everyone policy lost.");
transport.Throw = true;
var failed = await delivery.SendAsync(456, alert, default);
Check(!failed.Success && failed.Error is not null && !failed.Error.Contains("PRIVATE"), "Transport exception leaked credentials or claimed success.");
transport.Throw = false;
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    bool cancelled = false;
    try { await delivery.SendAsync(456, alert, cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Caller cancellation swallowed.");
}
delivery.SetTransport(null);
Check(!(await delivery.SendAsync(456, alert, default)).Success, "Disconnected bot claimed delivery.");
Console.WriteLine($"Passed {checks} offline Discord bot policy checks. No Discord connection or messages were made.");

sealed class FakeBotTransport : IDiscordBotTransport
{
    public int Calls;
    public ulong Guild;
    public ulong Channel;
    public AlertMessage? Message;
    public bool Throw;
    public Task<DeliveryResult> SendAsync(ulong guildId, ulong channelId, AlertMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw) throw new InvalidOperationException("PRIVATE token or transport request details");
        Calls++;
        Guild = guildId;
        Channel = channelId;
        Message = message;
        return Task.FromResult(new DeliveryResult(true));
    }
}
