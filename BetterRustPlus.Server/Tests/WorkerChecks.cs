using BetterRustPlus.Alerts;

internal static class WorkerChecks
{
    public static void Run(Action<bool, string> check)
    {
        var alerts = new AlertOptions { AllowedServerIds = ["group-server"] };
        var options = new RustSourceOptions();
        check(options.Validate(alerts) is null, "Disabled source unnecessarily requires account credentials.");
        options.Enabled = true;
        check(options.Validate(alerts) is not null, "Unconfigured enabled source was accepted.");
        options.ServerId = "group-server";
        options.Host = "127.0.0.1";
        options.SteamId = 76561198000000000;
        check(options.Validate(alerts) is not null, "Missing paired player token was accepted as implicit zero.");
        options.PlayerToken = 0;
        check(options.Validate(alerts) is null, "Explicit integer zero token was rejected without evidence it is invalid.");
        options.PlayerToken = 123;
        check(options.Validate(alerts) is null, "Paired direct source config was rejected.");
        options.CompanionServerId = Guid.NewGuid().ToString();
        check(options.Validate(alerts) is not null, "Partial relay credentials were accepted.");
        options.AuthToken = "offline-test-value";
        check(options.Validate(alerts) is null, "Complete relay config was rejected.");
        options.CompanionServerId = "not-a-uuid";
        check(options.Validate(alerts) is not null, "Invalid relay server UUID was accepted.");
        options.CompanionServerId = options.AuthToken = null;
        options.PollSeconds = 1;
        check(options.Validate(alerts) is not null, "Excessive source polling was accepted.");
        options.PollSeconds = 15;
        options.AlarmEntityIds = [0];
        check(options.Validate(alerts) is not null, "Invalid alarm ID was accepted.");
        options.AlarmEntityIds = [];
        options.Port = 65536;
        check(options.Validate(alerts) is not null, "Invalid companion port was accepted.");
        options.Port = 28082;
        options.ServerId = "another-server";
        check(options.Validate(alerts) is not null, "Source outside allowlisted server was accepted.");

        var state = new RustSourceState(options);
        check(state.Snapshot.State == "disabled" && !state.Snapshot.IsFresh, "Disabled source appeared fresh.");
        var now = DateTimeOffset.UtcNow;
        state.Set("connected", now);
        check(state.Snapshot.IsFresh && state.Snapshot.StaleAfterSeconds == 75, "Successful source poll was not fresh.");
        state.Set("degraded", now);
        check(state.Snapshot.IsFresh && state.Snapshot.State == "degraded", "Partial source success lost its fresh degraded status.");
        state.Set("disconnected");
        check(!state.Snapshot.IsFresh && state.Snapshot.LastSuccessfulPoll == now, "Disconnect lost last successful poll or remained fresh.");
        state.Set("connecting");
        check(!state.Snapshot.IsFresh && state.Snapshot.LastSuccessfulPoll == now, "Reconnecting source appeared connected or lost history.");
        state.Set("connected", now.AddHours(-1));
        check(!state.Snapshot.IsFresh, "Stalled source with an old poll appeared fresh.");
        var member = new TeamObservation(76561198000000000, "Nathan", true, true, 700, 2300, now.AddHours(-1).UtcDateTime);
        state.SetTeam([member], 3000, now);
        check(state.Team.ObservedAt == now && state.Team.Members.Single().Grid == "E4" &&
            state.Team.Members.Single().SteamId == "76561198000000000", "Team snapshot lost receipt time, grid or precise Steam identity.");
        state.SetTeam([member with { X = double.NaN, Y = double.PositiveInfinity }], 3000, now);
        check(state.Team.Members.Single() is { X: null, Y: null, Grid: null },
            "Invalid team coordinates leaked non-finite JSON or produced a misleading grid.");
        state.SetTeam([member with { IsOnline = false, IsAlive = false }], 3000, now);
        check(state.Team.Members.Single() is { IsOnline: false, IsAlive: false, Grid: "E4" },
            "Last-known offline/dead team member lost its explicit status.");
        state.SetTeam([], 3000, now);
        check(state.Team.Members.Length == 0, "Empty successful team snapshot retained departed players.");

        var retries = new EventRetryQueue(2);
        var first = new GameEvent(Guid.NewGuid(), "group-server", "raid", now, "confirmed");
        var second = first with { Id = Guid.NewGuid() };
        check(retries.Add(first, now) && retries.Add(second, now), "Retry queue rejected observations within capacity.");
        check(!retries.Add(first with { Id = Guid.NewGuid() }, now), "Retry queue exceeded its memory bound.");
        check(retries.TryTake(now, out var attempt) && attempt!.Id == first.Id, "First queued observation changed identity.");
        retries.Add(attempt!, now.AddSeconds(1));
        check(retries.TryTake(now, out var later) && later!.Id == second.Id, "A failed observation blocked another ready alarm.");
        check(!retries.TryTake(now, out _), "Retry ignored its backoff.");
        check(retries.TryTake(now.AddSeconds(1), out var retry) && retry!.Id == first.Id,
            "Retry replaced its event ID instead of preserving duplicate suppression.");
        retries.Add(first, now.AddMinutes(6));
        check(!retries.TryTake(now.AddMinutes(6), out _) && retries.Count == 0, "Expired observations remained queued for delivery.");
    }
}
