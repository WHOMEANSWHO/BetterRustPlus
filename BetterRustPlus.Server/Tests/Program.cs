using BetterRustPlus.Alerts;

var now = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
var clock = new TestClock(now);
var options = new AlertOptions
{
    DryRun = false,
    AllowedServerIds = ["group-server", "other-server"],
    Rules = [new AlertRule { Id = "raid", Kinds = ["raid", "teammate-death"], CooldownSeconds = 60 }],
    Targets = new() { ["primary"] = new() }
};
var sender = new RecordingSender();
var engine = new AlertEngine(options, sender, clock);
GameEvent Event(string kind = "raid", string server = "group-server") => new(Guid.NewGuid(), server, kind, clock.GetUtcNow(), "confirmed");
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

Check(AlertEngine.ValidateConfiguration(options) is null, "Valid configuration rejected.");
Check(AlertEngine.ValidateConfiguration(new AlertOptions { Rules = null! }) is not null, "Null rules should be rejected cleanly.");
Check(AlertEngine.ValidateConfiguration(new AlertOptions { Rules = [null!] }) is not null, "Null rule should be rejected cleanly.");
Check(AlertEngine.ValidateConfiguration(new AlertOptions { Rules = [new AlertRule { Kinds = null! }] }) is not null, "Null event kinds should be rejected cleanly.");
Check(AlertEngine.ValidateConfiguration(new AlertOptions { Targets = new() { ["primary"] = null! } }) is not null, "Null target should be rejected cleanly.");
Check((await engine.ProcessAsync(Event() with { ServerId = "unknown" })).Status == "rejected", "Unknown server accepted.");
Check((await engine.ProcessAsync(Event() with { OccurredAt = now.AddMinutes(-6) })).Status == "rejected", "Stale event accepted.");
Check((await engine.ProcessAsync(Event() with { OccurredAt = now.AddMinutes(1) })).Status == "rejected", "Future event accepted.");
Check((await engine.ProcessAsync(Event() with { Evidence = "guess" })).Status == "rejected", "Invalid evidence accepted.");
Check((await engine.ProcessAsync(Event() with { Kind = "made-up" })).Status == "rejected", "Unsupported event accepted.");
Check((await engine.ProcessAsync(Event("teammate-death") with { PlayerName = "Nathan", Grid = "G14<script>" })).Status == "rejected", "Invalid grid accepted.");
var first = Event();
Check((await engine.ProcessAsync(first)).Delivered == 1, "First delivery failed.");
Check((await engine.ProcessAsync(first)).Duplicate == 1 && sender.Messages.Count == 1, "Duplicate sent twice.");
var suppressed = Event();
Check((await engine.ProcessAsync(suppressed)).CoolingDown == 1, "Cooldown not applied.");
Check((await engine.ProcessAsync(Event(server: "other-server"))).Delivered == 1, "One server suppressed another.");
clock.Advance(TimeSpan.FromSeconds(61));
Check((await engine.ProcessAsync(suppressed)).Duplicate == 1, "Suppressed event replayed after cooldown.");
sender.Fail = true;
var retry = Event();
Check((await engine.ProcessAsync(retry)).Failed == 1, "Delivery failure not reported.");
sender.Fail = false;
Check((await engine.ProcessAsync(retry)).Delivered == 1, "Failed delivery incorrectly consumed event.");
var dryOptions = new AlertOptions { DryRun = true, AllowedServerIds = ["group-server"], Rules = options.Rules };
var drySender = new RecordingSender();
var dryEngine = new AlertEngine(dryOptions, drySender, clock);
Check((await dryEngine.ProcessAsync(Event())).Simulated == 1 && drySender.Messages.Count == 0, "Dry-run contacted sender.");
var simultaneous = Event("teammate-death") with { PlayerName = "Nathan", Grid = "G14" };
var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => dryEngine.ProcessAsync(simultaneous)));
Check(concurrentResults.Sum(result => result.Simulated) == 1 && concurrentResults.Sum(result => result.Duplicate) == 9,
    "Concurrent copies were not suppressed.");
var death = Event("teammate-death") with { PlayerName = "Nathan @everyone", Grid = "G14", PositionIsLastKnown = true };
var message = AlertEngine.Format(death, false);
Check(message.Content.Contains("Last seen at G14") && !message.Content.Contains("@everyone"), "Last-known position or mention handling failed.");
Check(AlertEngine.Format(death, true).Content.StartsWith("@everyone "), "Opt-in mention missing.");
Check(AlertEngine.Validate(death with { Grid = null }, options, clock.GetUtcNow()) is not null, "Last-known position without grid accepted.");
var blockingSender = new BlockingSender();
var busyEngine = new AlertEngine(options, blockingSender, clock);
var heldEvent = Event();
var heldDelivery = busyEngine.ProcessAsync(heldEvent);
await blockingSender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
var unrelatedEvent = Event(server: "other-server");
try
{
    var busy = await busyEngine.ProcessAsync(unrelatedEvent).WaitAsync(TimeSpan.FromSeconds(1));
    Check(busy.Status == "busy" && busy.Delivered == 0, "Slow delivery blocked an unrelated request instead of returning busy promptly.");
}
finally { blockingSender.Release.TrySetResult(); }
Check((await heldDelivery).Delivered == 1, "Held delivery failed after release.");
Check((await busyEngine.ProcessAsync(unrelatedEvent)).Delivered == 1 && blockingSender.Sends == 2,
    "Busy response consumed the event or prevented retrying its original ID.");
var expiringClock = new TestClock(now);
var expiryOptions = new AlertOptions
{
    DryRun = false, AllowedServerIds = ["group-server"], Targets = options.Targets,
    Rules = [new AlertRule { Id = "first", Kinds = ["raid"] }, new AlertRule { Id = "second", Kinds = ["raid"] }]
};
var agingSender = new AgingSender(expiringClock);
var expiredDuringDelivery = await new AlertEngine(expiryOptions, agingSender, expiringClock)
    .ProcessAsync(new GameEvent(Guid.NewGuid(), "group-server", "raid", now, "confirmed"));
Check(expiredDuringDelivery.Status == "expired" && expiredDuringDelivery.Delivered == 1 && agingSender.Sends == 1,
    "A slow earlier delivery allowed a new target to receive a stale event.");
options.Rules.Add(new AlertRule { Id = "raid", Kinds = ["raid"] });
Check(AlertEngine.ValidateConfiguration(options) is not null, "Duplicate rule IDs accepted.");
var observations = new ObservationTracker("group-server");
var living = new TeamObservation(1, "Nathan", true, true, 700, 2300, now.AddHours(-1).UtcDateTime);
Check(observations.ObserveTeam([living], 3000, now).Count == 0, "Startup team snapshot generated a historical death.");
Check(observations.ObserveTeam([living with { IsOnline = false }], 3000, now).Count == 0, "Going offline generated a death.");
observations.ObserveTeam([living], 3000, now);
var dead = living with { IsAlive = false, X = 0, Y = 0, LastDeathTime = now.UtcDateTime };
var detectedDeath = observations.ObserveTeam([dead], 3000, now).Single();
Check(detectedDeath.Grid == "E4" && detectedDeath.PositionIsLastKnown && detectedDeath.PlayerName == "Nathan",
    "Death must carry explicitly last-seen grid, not unverified dead-player coordinates.");
Check(observations.ObserveTeam([dead], 3000, now).Count == 0, "Repeated dead snapshot duplicated an alert.");
Check(observations.ObserveTeam([dead with { LastDeathTime = now.AddSeconds(1).UtcDateTime }], 3000, now.AddSeconds(2)).Count == 0,
    "Delayed death timestamp duplicated an already-announced death.");
Check(observations.ObserveTeam([living with { LastDeathTime = now.AddSeconds(10).UtcDateTime }], 3000, now.AddSeconds(15)).Count == 1,
    "Death plus respawn between polls was missed.");
Check(observations.ObserveTeam([], 3000, now).Count == 0, "Team departure generated a death.");
Check(new ObservationTracker("new").ObserveTeam([dead], 3000, now).Count == 0, "Reconnection replayed old deaths.");
var delayedTracker = new ObservationTracker("group-server");
delayedTracker.ObserveTeam([living], 3000, now);
Check(delayedTracker.ObserveTeam([living with { IsAlive = false }], 3000, now.AddSeconds(1)).Count == 1,
    "Alive-to-dead transition with delayed death timestamp was missed.");
Check(delayedTracker.ObserveTeam([living with { LastDeathTime = now.UtcDateTime }], 3000, now.AddSeconds(2)).Count == 0,
    "Respawn with a delayed timestamp re-announced the previous inferred death.");
Check(ObservationTracker.MapGrid(0, 3000, 3000) == "A0" && ObservationTracker.MapGrid(3000, 0, 3000) == "T19",
    "Map edges use wrong grid origin or indexing.");
Check(ObservationTracker.MapGrid(4250d / 29 * 4 + 1, 4250d / 29 * 5 - 1, 4250) == "E24",
    "Non-150-divisible maps drift between grid boundaries.");
Check(ObservationTracker.MapGrid(-1, 0, 3000) is null && ObservationTracker.MapGrid(double.NaN, 10, 3000) is null &&
    ObservationTracker.MapGrid(5, 5, 0) is null, "Invalid/off-map coordinates produced a misleading grid.");
Check(observations.ObserveCargo([new CargoObservation(1, 500, 500)], 3000, now).Count == 0,
    "Already-present cargo was announced as a new arrival at startup.");
Check(observations.ObserveCargo([new CargoObservation(1, 500, 500), new CargoObservation(2, 700, 2300)], 3000, now).Single().Grid == "E4",
    "New cargo marker did not generate a located observation.");
Check(observations.ObserveCargo([new CargoObservation(2, 800, 2200)], 3000, now).Count == 0, "Cargo movement generated a duplicate arrival.");
Check(observations.ObserveAlarm(42, true, now) is null && observations.ObserveAlarm(42, true, now) is null,
    "Startup active alarm generated a historical raid.");
observations.ObserveAlarm(42, false, now);
Check(observations.ObserveAlarm(42, true, now)?.Kind == "raid", "Alarm rising edge was missed.");
WorkerChecks.Run(Check);
Console.WriteLine($"Passed {checks} alert/source checks. No Discord requests were made.");

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan interval) => current += interval;
}
sealed class RecordingSender : IAlertSender
{
    public List<AlertMessage> Messages { get; } = [];
    public bool Fail { get; set; }
    public Task<DeliveryResult> SendAsync(string target, AlertMessage message, CancellationToken cancellationToken)
    {
        if (Fail) return Task.FromResult(new DeliveryResult(false));
        Messages.Add(message);
        return Task.FromResult(new DeliveryResult(true));
    }
}
sealed class BlockingSender : IAlertSender
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Sends { get; private set; }
    public async Task<DeliveryResult> SendAsync(string target, AlertMessage message, CancellationToken cancellationToken)
    {
        Sends++;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return new(true);
    }
}
sealed class AgingSender(TestClock clock) : IAlertSender
{
    public int Sends { get; private set; }
    public Task<DeliveryResult> SendAsync(string target, AlertMessage message, CancellationToken cancellationToken)
    {
        Sends++;
        clock.Advance(TimeSpan.FromMinutes(6));
        return Task.FromResult(new DeliveryResult(true));
    }
}
