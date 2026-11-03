namespace BetterRustPlus.Alerts;

public sealed record TeamObservation(ulong SteamId, string Name, bool IsAlive, bool IsOnline,
    double X, double Y, DateTime LastDeathTime);
public sealed record CargoObservation(ulong Id, double X, double Y);

/// <summary>Transitions between successful live snapshots, never historical startup events.</summary>
public sealed class ObservationTracker(string serverId)
{
    private Dictionary<ulong, TeamObservation>? team;
    private HashSet<ulong>? cargo;
    private readonly Dictionary<uint, bool> alarms = new();
    private readonly HashSet<ulong> announcedWhileDead = new();
    private readonly Dictionary<ulong, DateTimeOffset> lastAnnouncedDeath = new();

    public IReadOnlyList<GameEvent> ObserveTeam(IEnumerable<TeamObservation> members, double mapSize, DateTimeOffset now)
    {
        var current = members.GroupBy(m => m.SteamId).ToDictionary(g => g.Key, g => g.Last());
        var events = new List<GameEvent>();
        if (team is not null)
            foreach (var member in current.Values)
            {
                if (!team.TryGetValue(member.SteamId, out var previous)) continue;
                if (member.IsAlive) announcedWhileDead.Remove(member.SteamId);
                var death = new DateTimeOffset(DateTime.SpecifyKind(member.LastDeathTime, DateTimeKind.Utc));
                // Death timestamps also catch death+respawn between polls. Never infer a death
                // merely from going offline or disappearing from the team.
                bool freshDeath = member.LastDeathTime > previous.LastDeathTime &&
                    death >= now.AddMinutes(-1) && death <= now.AddSeconds(30);
                if (!freshDeath && !(previous.IsAlive && !member.IsAlive && member.IsOnline)) continue;
                if (freshDeath && lastAnnouncedDeath.TryGetValue(member.SteamId, out var announcedAt) && death <= announcedAt) continue;
                if (!member.IsAlive && !announcedWhileDead.Add(member.SteamId)) continue;
                lastAnnouncedDeath[member.SteamId] = freshDeath ? death : now;
                string? grid = MapGrid(previous.X, previous.Y, mapSize);
                events.Add(new(Guid.NewGuid(), serverId, "teammate-death", freshDeath ? death : now,
                    "confirmed", string.IsNullOrWhiteSpace(member.Name) ? member.SteamId.ToString() : member.Name,
                    grid, grid is not null));
            }
        team = current;
        announcedWhileDead.RemoveWhere(id => !current.ContainsKey(id));
        foreach (var id in lastAnnouncedDeath.Keys.Where(id => !current.ContainsKey(id)).ToArray()) lastAnnouncedDeath.Remove(id);
        return events;
    }

    public IReadOnlyList<GameEvent> ObserveCargo(IEnumerable<CargoObservation> markers, double mapSize, DateTimeOffset now)
    {
        var current = markers.GroupBy(m => m.Id).Select(g => g.Last()).ToArray();
        var events = cargo is null ? [] : current.Where(m => !cargo.Contains(m.Id)).Select(m =>
            new GameEvent(Guid.NewGuid(), serverId, "cargo", now, "confirmed", Grid: MapGrid(m.X, m.Y, mapSize))).ToArray();
        cargo = current.Select(m => m.Id).ToHashSet();
        return events;
    }

    public GameEvent? ObserveAlarm(uint entityId, bool active, DateTimeOffset now)
    {
        bool triggered = alarms.TryGetValue(entityId, out bool before) && !before && active;
        alarms[entityId] = active;
        return triggered ? new(Guid.NewGuid(), serverId, "raid", now, "confirmed") : null;
    }

    public static string? MapGrid(double x, double y, double mapSize)
    {
        if (!double.IsFinite(mapSize) || mapSize <= 0 || mapSize > 100000 ||
            !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > mapSize || y > mapSize) return null;
        int cells = (int)Math.Ceiling(mapSize / 150d);
        double size = mapSize / cells;
        int column = Math.Min(cells - 1, (int)Math.Floor(x / size));
        int row = Math.Min(cells - 1, (int)Math.Floor((mapSize - y) / size));
        string label = "";
        for (int n = column + 1; n > 0; n = (n - 1) / 26) label = (char)('A' + (n - 1) % 26) + label;
        return label + row;
    }
}
