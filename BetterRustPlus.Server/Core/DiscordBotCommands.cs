using System.Text;

namespace BetterRustPlus.Alerts;

public sealed class DiscordBotOptions
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
    public ulong[] AllowedChannelIds { get; set; } = [];

    public string? Validate()
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(Token) || Token.Length > 4096 || Token.Any(char.IsWhiteSpace))
            return "DiscordBot requires a valid bot token supplied privately.";
        if (GuildId == 0 || AllowedChannelIds is null || AllowedChannelIds.Length is < 1 or > 32 || AllowedChannelIds.Any(id => id == 0))
            return "DiscordBot requires one guild and 1–32 allowed channel IDs.";
        return null;
    }
}

public sealed record BotRuntimeStatus(bool DryRun, string SourceState, DateTimeOffset? LastPoll,
    bool SourceFresh, IReadOnlyList<string> SourceCapabilities);
public sealed record BotCommandReply(string Text, bool Ephemeral = true);
public sealed record BotTeamMember(string Name, bool Online, bool Alive, string? Grid);
public sealed record BotTeamSnapshot(DateTimeOffset? ObservedAt, IReadOnlyList<BotTeamMember> Members);

/// <summary>Pure, read-only command policy. Never renders bot, pairing or webhook credentials.</summary>
public static class DiscordBotCommands
{
    public static BotCommandReply Reply(string command, ulong? guildId, ulong channelId,
        DiscordBotOptions options, AlertOptions alerts, BotRuntimeStatus status, BotTeamSnapshot? team = null)
    {
        if (!options.Enabled || guildId != options.GuildId || options.GuildId == 0 ||
            options.AllowedChannelIds?.Contains(channelId) != true)
            return new("BetterRustPlus commands are unavailable in this server or channel.");

        return command switch
        {
            "brp-status" => new(Status(alerts, status)),
            "brp-alerts" => new(Rules(alerts)),
            "brp-team" => new(Team(team, status.SourceFresh)),
            _ => new("Unknown BetterRustPlus command. Use /brp-status, /brp-alerts or /brp-team.")
        };
    }

    private static string Status(AlertOptions alerts, BotRuntimeStatus status)
    {
        var text = new StringBuilder("BetterRustPlus\n");
        text.AppendLine(status.DryRun ? "Delivery: dry-run (no alert messages sent)." : "Delivery: enabled for configured targets.");
        text.Append("Rust+ source: ").Append(Safe(status.SourceState)).AppendLine(status.SourceFresh ? " (recent poll)." : " (no fresh poll).");
        text.AppendLine(status.LastPoll is { } poll ? $"Last successful team poll: {poll.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC." : "Last successful team poll: unavailable.");
        text.AppendLine($"Configured servers: {alerts.AllowedServerIds.Length}. Alert rules: {alerts.Rules.Count}.");
        if (status.SourceCapabilities.Count > 0)
            text.AppendLine("Configured source capabilities: " + string.Join(", ", status.SourceCapabilities.Select(Safe)) + ".");
        text.Append("Capabilities are not proof every server exposes them. Airdrop and Deep Sea need a verified event source. Delivery/retry state is memory-only.");
        return Limit(text.ToString());
    }

    private static string Rules(AlertOptions alerts)
    {
        if (alerts.Rules.Count == 0) return "No alert rules are configured. Configure the VPS service and recreate its container to apply changes.";
        var text = new StringBuilder("Configured BetterRustPlus alert rules\n");
        text.AppendLine(alerts.DryRun ? "Dry-run is ON; alerts are simulated." : "Live delivery is enabled.");
        int shown = 0;
        foreach (var rule in alerts.Rules)
        {
            string servers = rule.ServerIds.Length == 0 ? "all allowlisted servers" : string.Join(", ", rule.ServerIds.Select(Safe));
            string line = $"• {Safe(rule.Id)}: {string.Join(", ", rule.Kinds.Select(Safe))}; {servers}; cooldown {rule.CooldownSeconds}s; everyone mention {(rule.MentionEveryone ? "on" : "off")}.\n";
            if (text.Length + line.Length > 1550) break;
            text.Append(line);
            shown++;
        }
        if (shown < alerts.Rules.Count) text.AppendLine($"{alerts.Rules.Count - shown} more rules are configured on the VPS.");
        text.Append("Rules describe delivery, not detector availability. Changes are read-only here; edit the VPS configuration to change rules.");
        return Limit(text.ToString());
    }

    private static string Team(BotTeamSnapshot? team, bool fresh)
    {
        if (team?.ObservedAt is not { } observed)
            return "Team positions are unavailable. Configure the paired Rust+ source and wait for a successful team poll.";
        var text = new StringBuilder(fresh ? "Latest observed team positions\n" : "Last-known team positions — source is stale or disconnected\n");
        text.AppendLine($"Observed: {observed.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC.");
        if (team.Members.Count == 0) text.AppendLine("The last team response contained no members.");
        int shown = 0;
        foreach (var member in team.Members)
        {
            string name = string.IsNullOrWhiteSpace(member.Name) ? "Unnamed teammate" : Safe(member.Name);
            string location = string.IsNullOrWhiteSpace(member.Grid) ? "position unavailable" : $"last seen at {Safe(member.Grid)}";
            string line = $"• {name}: {(member.Online ? "online" : "offline")}, {(member.Alive ? "alive" : "dead")}; {location}.\n";
            if (text.Length + line.Length > 1650) break;
            text.Append(line);
            shown++;
        }
        if (shown < team.Members.Count) text.AppendLine($"{team.Members.Count - shown} more members are present in the latest snapshot.");
        text.Append("Coordinates are observations, not exact death locations. Offline members may have older positions; the server may withhold data.");
        return Limit(text.ToString());
    }

    private static string Safe(string value) => new(value.Take(128).Select(c => char.IsControl(c) || "@`*_~|<>".Contains(c) ? ' ' : c).ToArray());
    private static string Limit(string value) => value.Length <= 1900 ? value : value[..1850] + "\nSee the VPS configuration for the remaining details.";
}
