using System.Text.RegularExpressions;

namespace BetterRustPlus.Alerts;

public sealed record GameEvent(Guid Id, string ServerId, string Kind, DateTimeOffset OccurredAt,
    string Evidence, string? PlayerName = null, string? Grid = null, bool PositionIsLastKnown = false);

public sealed class AlertOptions
{
    public bool DryRun { get; set; } = true;
    public string[] AllowedServerIds { get; set; } = [];
    public List<AlertRule> Rules { get; set; } = [];
    public Dictionary<string, DeliveryTarget> Targets { get; set; } = new();
}

public sealed class DeliveryTarget { public string WebhookUrl { get; set; } = ""; }

public sealed class AlertRule
{
    public string Id { get; set; } = "";
    public string[] Kinds { get; set; } = [];
    public string[] ServerIds { get; set; } = [];
    public string Target { get; set; } = "primary";
    public int CooldownSeconds { get; set; } = 60;
    public bool MentionEveryone { get; set; }
}

public sealed record AlertMessage(string Content, bool MentionEveryone);
public sealed record DeliveryResult(bool Success, string? Error = null);
public interface IAlertSender
{
    Task<DeliveryResult> SendAsync(string target, AlertMessage message, CancellationToken cancellationToken);
}
public sealed record ProcessingResult(string Status, int Delivered = 0, int Simulated = 0,
    int Duplicate = 0, int CoolingDown = 0, int Failed = 0, string? Error = null);

/// <summary>Processes supplied observations; it does not connect to Rust or detect game events.</summary>
public sealed class AlertEngine(AlertOptions options, IAlertSender sender, TimeProvider? timeProvider = null)
{
    public static readonly string[] SupportedKinds = ["cargo", "airdrop", "deepsea", "raid", "teammate-death"];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(string Rule, string Server, Guid Event), DateTimeOffset> delivered = new();
    private readonly Dictionary<(string Rule, string Server, string Kind), DateTimeOffset> lastDelivery = new();
    private static readonly Regex GridPattern = new("^[A-Z]{1,3}[0-9]{1,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex ServerPattern = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);

    public static string? Validate(GameEvent item, AlertOptions options, DateTimeOffset now)
    {
        if (item.Id == Guid.Empty) return "A nonempty event ID is required.";
        if (item.ServerId is null || !ServerPattern.IsMatch(item.ServerId)) return "Invalid server ID.";
        if (!options.AllowedServerIds.Contains(item.ServerId, StringComparer.Ordinal)) return "Server is not configured.";
        if (!SupportedKinds.Contains(item.Kind, StringComparer.Ordinal)) return "Unsupported event kind.";
        if (item.Evidence is not ("confirmed" or "estimated" or "teammate-report")) return "Invalid evidence type.";
        if (item.OccurredAt < now.AddMinutes(-5)) return "Event is stale (older than five minutes).";
        if (item.OccurredAt > now.AddSeconds(30)) return "Event timestamp is in the future.";
        if (item.Grid is not null && !GridPattern.IsMatch(item.Grid)) return "Invalid map grid.";
        if (item.PlayerName is { Length: > 80 } || item.PlayerName?.Any(char.IsControl) == true)
            return "Invalid player name.";
        if (item.Kind == "teammate-death" && string.IsNullOrWhiteSpace(item.PlayerName))
            return "A teammate name is required for death events.";
        if (item.PositionIsLastKnown && item.Grid is null) return "A last-known position requires a grid.";
        return null;
    }

    public static string? ValidateConfiguration(AlertOptions options)
    {
        if (options.AllowedServerIds.Length > 128 || options.Rules.Count > 32 || options.Targets.Count > 32)
            return "Configuration supports at most 128 servers, 32 rules and 32 targets.";
        if (options.AllowedServerIds.Any(id => id is null || !ServerPattern.IsMatch(id))) return "Invalid configured server ID.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in options.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 80 || !ids.Add(rule.Id)) return "Rule IDs must be unique and nonempty.";
            if (rule.CooldownSeconds is < 0 or > 86400) return "Cooldown must be between 0 and 86400 seconds.";
            if (rule.Kinds.Length == 0 || rule.Kinds.Any(kind => !SupportedKinds.Contains(kind))) return "Rule has unsupported event kinds.";
            if (rule.ServerIds.Any(id => !options.AllowedServerIds.Contains(id, StringComparer.Ordinal))) return "Rule refers to an unconfigured server.";
            if (!options.DryRun && !options.Targets.ContainsKey(rule.Target)) return "Rule delivery target is missing.";
        }
        return null;
    }

    public async Task<ProcessingResult> ProcessAsync(GameEvent item, CancellationToken cancellationToken = default)
    {
        var error = Validate(item, options, clock.GetUtcNow());
        if (error is not null) return new("rejected", Error: error);
        // There is deliberately no in-memory waiting queue. Callers retain the event and retry
        // a busy response with the same ID, so one slow webhook cannot hold unrelated requests.
        if (!await gate.WaitAsync(0, cancellationToken)) return new("busy", Error: "Delivery is busy. Retry the same event ID after a short delay.");
        try
        {
            var now = clock.GetUtcNow();
            // Recheck freshness after acquiring delivery ownership.
            error = Validate(item, options, now);
            if (error is not null) return new("rejected", Error: error);
            foreach (var key in delivered.Where(entry => entry.Value < now.AddMinutes(-10)).Select(entry => entry.Key).ToArray()) delivered.Remove(key);
            foreach (var key in lastDelivery.Where(entry => entry.Value < now.AddDays(-1)).Select(entry => entry.Key).ToArray()) lastDelivery.Remove(key);
            int sent = 0, simulated = 0, duplicates = 0, cooling = 0, failed = 0, matches = 0;
            foreach (var rule in options.Rules)
            {
                if (!rule.Kinds.Contains(item.Kind) || (rule.ServerIds.Length > 0 && !rule.ServerIds.Contains(item.ServerId))) continue;
                now = clock.GetUtcNow();
                // A series of slow targets must not begin new deliveries for expired events.
                error = Validate(item, options, now);
                if (error is not null) return new("expired", sent, simulated, duplicates, cooling, failed, error);
                matches++;
                var eventKey = (rule.Id, item.ServerId, item.Id);
                var cooldownKey = (rule.Id, item.ServerId, item.Kind);
                if (delivered.ContainsKey(eventKey)) { duplicates++; continue; }
                if (lastDelivery.TryGetValue(cooldownKey, out var last) && now < last.AddSeconds(rule.CooldownSeconds))
                {
                    cooling++;
                    // Consume suppressed events: replaying one after cooldown must not create a new alarm.
                    delivered[eventKey] = now;
                    continue;
                }
                if (options.DryRun) simulated++;
                else
                {
                    DeliveryResult result;
                    try { result = await sender.SendAsync(rule.Target, Format(item, rule.MentionEveryone), cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { result = new(false, "Delivery failed."); }
                    if (!result.Success) { failed++; continue; }
                    sent++;
                }
                delivered[eventKey] = now;
                lastDelivery[cooldownKey] = now;
            }
            return new(matches == 0 ? "no-matching-rules" : failed > 0 ? "delivery-failed" : "processed",
                sent, simulated, duplicates, cooling, failed);
        }
        finally { gate.Release(); }
    }

    public static AlertMessage Format(GameEvent item, bool everyone)
    {
        static string Safe(string value) => value.Replace("@", "＠").Replace("`", "'").Replace("*", "").Replace("_", "");
        string location = item.Grid is null ? "Location unavailable." : item.PositionIsLastKnown
            ? $"Last seen at {item.Grid}." : $"At grid {item.Grid}.";
        string description = item.Kind == "teammate-death"
            ? $"{Safe(item.PlayerName!)} died. {location}"
            : $"{item.Kind switch { "deepsea" => "Deep Sea", "airdrop" => "Airdrop", "cargo" => "Cargo", "raid" => "Raid alarm", _ => item.Kind }} event. {location}";
        return new($"{(everyone ? "@everyone " : "")}[{item.Evidence}] {description} Server: {Safe(item.ServerId)}. {item.OccurredAt:yyyy-MM-dd HH:mm:ss} UTC", everyone);
    }
}
