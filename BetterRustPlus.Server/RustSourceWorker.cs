using BetterRustPlus.Alerts;
using RustPlusApi;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class RustSourceOptions
{
    public bool Enabled { get; set; }
    public string ServerId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 28082;
    public ulong SteamId { get; set; }
    public int? PlayerToken { get; set; }
    public string? CompanionServerId { get; set; }
    public string? AuthToken { get; set; }
    public uint[] AlarmEntityIds { get; set; } = [];
    public int PollSeconds { get; set; } = 15;

    public string? Validate(AlertOptions alerts)
    {
        if (!Enabled) return null;
        if (!alerts.AllowedServerIds.Contains(ServerId)) return "RustSource ServerId must be allowlisted.";
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || SteamId == 0 || !PlayerToken.HasValue)
            return "RustSource requires paired Host, Port, SteamId and PlayerToken.";
        if (PollSeconds is < 5 or > 300 || AlarmEntityIds is null || AlarmEntityIds.Length > 32 || AlarmEntityIds.Any(id => id == 0))
            return "RustSource requires a 5–300 second poll interval and at most 32 valid alarms.";
        if (string.IsNullOrWhiteSpace(CompanionServerId) != string.IsNullOrWhiteSpace(AuthToken))
            return "Companion relay requires both CompanionServerId and AuthToken.";
        if (!string.IsNullOrWhiteSpace(CompanionServerId) && !Guid.TryParse(CompanionServerId, out _))
            return "CompanionServerId must be the paired server UUID.";
        return null;
    }
}

internal sealed record RustSourceSnapshot(string State, DateTimeOffset? LastSuccessfulPoll, string[] Capabilities, int StaleAfterSeconds = 30)
{
    public bool IsFresh => State is "connected" or "degraded" && LastSuccessfulPoll is { } last &&
        DateTimeOffset.UtcNow - last <= TimeSpan.FromSeconds(StaleAfterSeconds);
}
internal sealed class RustSourceState(RustSourceOptions options)
{
    private RustSourceSnapshot snapshot = new("disabled", null, []);
    private TeamPositionSnapshot team = new(null, []);
    public RustSourceSnapshot Snapshot => Volatile.Read(ref snapshot);
    public TeamPositionSnapshot Team => Volatile.Read(ref team);
    public void SetTeam(IEnumerable<TeamObservation> members, double mapSize, DateTimeOffset receivedAt) =>
        Volatile.Write(ref team, new(receivedAt, members.Select(m => new TeamPosition(m.SteamId.ToString(), m.Name,
            m.IsAlive, m.IsOnline, double.IsFinite(m.X) ? m.X : null, double.IsFinite(m.Y) ? m.Y : null,
            ObservationTracker.MapGrid(m.X, m.Y, mapSize))).ToArray()));
    public void Set(string state, DateTimeOffset? lastPoll = null)
    {
        var previous = Snapshot;
        Volatile.Write(ref snapshot, new(state, lastPoll ?? previous.LastSuccessfulPoll,
            ["team-status", "teammate-death-last-seen-grid", "cargo-marker-arrival", "configured-smart-alarm"],
            Math.Max(30, options.PollSeconds + 60)));
    }
}
internal sealed record TeamPosition(string SteamId, string Name, bool IsAlive, bool IsOnline, double? X, double? Y, string? Grid);
internal sealed record TeamPositionSnapshot(DateTimeOffset? ObservedAt, TeamPosition[] Members);

internal sealed class RustSourceWorker(RustSourceOptions options, RustSourceState state, AlertEngine engine,
    ILogger<RustSourceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var pending = new List<GameEvent>();
        while (!stoppingToken.IsCancellationRequested)
        {
            state.Set("connecting");
            RustPlus? api = null;
            CancellationTokenSource? alarmLifetime = null;
            Task? alarmReader = null;
            Action? stopAlarms = null;
            try
            {
                var connection = new RustPlusConnection(options.Host, options.Port, options.SteamId, options.PlayerToken!.Value)
                {
                    ServerId = Guid.TryParse(options.CompanionServerId, out var serverUuid) ? serverUuid : null,
                    AuthToken = options.AuthToken
                };
                api = new RustPlus(connection, new RustPlusSocketOptions { RequestTimeout = TimeSpan.FromSeconds(6) });
                await api.ConnectAsync(stoppingToken).WaitAsync(TimeSpan.FromSeconds(12), stoppingToken);
                var info = await api.GetInfoAsync(stoppingToken).WaitAsync(TimeSpan.FromSeconds(8), stoppingToken);
                if (!info.IsSuccess || info.Data is null) throw new InvalidOperationException("Source authentication failed.");
                double mapSize = info.Data.MapSize ?? 0;
                var tracker = new ObservationTracker(options.ServerId);
                var alarmIds = options.AlarmEntityIds.Distinct().ToHashSet();
                var alarmChanges = Channel.CreateBounded<(uint Id, bool Active, DateTimeOffset Time)>(new BoundedChannelOptions(1024)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
                });
                // Serialize both writers with their timestamp. Poll responses must not run
                // ahead of already received broadcasts, or a short false->true->false pulse
                // can be reversed or replayed as a second trigger.
                var alarmGate = new object();
                var broadcastVersions = new Dictionary<uint, long>();
                bool bufferOverflow = false;
                bool acceptingAlarms = true;
                int alarmReaderFailed = 0;
                void QueueAlarm(uint id, bool active)
                {
                    if (!alarmChanges.Writer.TryWrite((id, active, DateTimeOffset.UtcNow)))
                    {
                        bufferOverflow = true;
                        logger.LogWarning("Alarm observation buffer full; an update was dropped.");
                    }
                }
                api.OnEntityChanged += (_, changed) =>
                {
                    if (changed.Id > uint.MaxValue || changed.Value is not { } active || !alarmIds.Contains((uint)changed.Id)) return;
                    lock (alarmGate)
                    {
                        if (!acceptingAlarms) return;
                        uint id = (uint)changed.Id;
                        broadcastVersions[id] = broadcastVersions.GetValueOrDefault(id) + 1;
                        QueueAlarm(id, active);
                    }
                };
                alarmLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var alarmCancellation = alarmLifetime.Token;
                stopAlarms = () =>
                {
                    lock (alarmGate) { acceptingAlarms = false; alarmChanges.Writer.TryComplete(); }
                    alarmLifetime.Cancel();
                };
                // This reader starts before subscriptions are polled and runs independently
                // of slow marker/device requests. It is the sole alarm tracker consumer.
                alarmReader = ReadAlarmsAsync();
                async Task ReadAlarmsAsync()
                {
                    try
                    {
                        var retries = new EventRetryQueue();
                        while (!alarmCancellation.IsCancellationRequested)
                        {
                            while (alarmChanges.Reader.TryRead(out var change))
                            {
                                if (tracker.ObserveAlarm(change.Id, change.Active, change.Time) is not { } observed) continue;
                                if (!retries.Add(observed, change.Time))
                                {
                                    lock (alarmGate) bufferOverflow = true;
                                    logger.LogWarning("Alarm retry capacity exceeded; an observation was dropped.");
                                }
                            }
                            if (retries.TryTake(DateTimeOffset.UtcNow, out var next))
                            {
                                var result = await engine.ProcessAsync(next!, alarmCancellation);
                                // Put a failed event behind other ready observations with a
                                // one-second backoff. Its ID and original freshness window
                                // remain unchanged; one failure cannot monopolize the reader.
                                if (result.Status is "busy" or "delivery-failed")
                                    retries.Add(next!, DateTimeOffset.UtcNow.AddSeconds(1));
                                continue;
                            }
                            if (retries.Count == 0)
                            {
                                if (!await alarmChanges.Reader.WaitToReadAsync(alarmCancellation)) break;
                            }
                            else await Task.Delay(TimeSpan.FromMilliseconds(100), alarmCancellation);
                        }
                    }
                    catch (OperationCanceledException) when (alarmCancellation.IsCancellationRequested) { }
                    catch
                    {
                        Interlocked.Exchange(ref alarmReaderFailed, 1);
                        state.Set("degraded");
                        logger.LogWarning("Alarm observation processing unavailable; reconnecting after the current poll.");
                    }
                }
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref alarmReaderFailed) != 0) throw new InvalidOperationException("Alarm reader stopped.");
                    var team = await api.GetTeamInfoAsync(stoppingToken).WaitAsync(TimeSpan.FromSeconds(8), stoppingToken);
                    if (!team.IsSuccess || team.Data is null) throw new InvalidOperationException("Team read failed.");
                    var teamReadAt = DateTimeOffset.UtcNow;
                    var members = (team.Data.Members ?? []).Select(m => new TeamObservation(
                        m.SteamId, m.Name ?? "", m.IsAlive, m.IsOnline, m.X, m.Y, m.LastDeathTime)).ToArray();
                    state.SetTeam(members, mapSize, teamReadAt);
                    pending.AddRange(tracker.ObserveTeam(members, mapSize, teamReadAt));
                    // Preserve transport/authentication failure handling for team reads, but a
                    // missing marker/alarm capability must not hide successful team updates.
                    bool degraded = false;
                    await Task.Delay(250, stoppingToken);
                    try
                    {
                        var markers = await api.GetMapMarkersAsync(stoppingToken).WaitAsync(TimeSpan.FromSeconds(8), stoppingToken);
                        if (markers.IsSuccess && markers.Data is not null)
                            pending.AddRange(tracker.ObserveCargo(markers.Data.CargoShipMarkers.Select(m =>
                                new CargoObservation(m.Key, m.Value.X ?? double.NaN, m.Value.Y ?? double.NaN)), mapSize, DateTimeOffset.UtcNow));
                        else degraded = true;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch { degraded = true; }
                    foreach (uint id in alarmIds)
                    {
                        await Task.Delay(250, stoppingToken);
                        long before;
                        lock (alarmGate) before = broadcastVersions.GetValueOrDefault(id);
                        try
                        {
                            var alarm = await api.GetSmartDeviceInfoAsync(id, stoppingToken).WaitAsync(TimeSpan.FromSeconds(8), stoppingToken);
                            if (alarm.IsSuccess && alarm.Data is not null)
                            {
                                lock (alarmGate)
                                {
                                    // A broadcast during the request is newer than an unknown
                                    // server-side snapshot time; do not overwrite it with a poll.
                                    if (broadcastVersions.GetValueOrDefault(id) == before)
                                        QueueAlarm(id, alarm.Data.IsActive);
                                }
                            }
                            else degraded = true;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch { degraded = true; }
                    }
                    lock (alarmGate) degraded |= bufferOverflow;
                    degraded |= Volatile.Read(ref alarmReaderFailed) != 0;
                    state.Set(degraded ? "degraded" : "connected", teamReadAt);
                    // Oldest observations get the first delivery opportunity. A failed
                    // target stays retryable without blocking all other event categories.
                    pending.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
                    for (int i = 0; i < pending.Count;)
                    {
                        var result = await engine.ProcessAsync(pending[i], stoppingToken);
                        if (result.Status == "busy") break;
                        if (result.Status == "delivery-failed") i++;
                        else pending.RemoveAt(i);
                    }
                    pending.RemoveAll(e => e.OccurredAt < DateTimeOffset.UtcNow.AddMinutes(-5));
                    if (pending.Count > 1024) { pending.RemoveRange(0, pending.Count - 1024); logger.LogWarning("Rust source retry capacity exceeded; oldest observations discarded."); }
                    await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Exception details may contain paired server/account credentials.
                state.Set("disconnected");
                logger.LogWarning("Rust source unavailable. Check pairing and server access; reconnecting in 15 seconds.");
            }
            finally
            {
                stopAlarms?.Invoke();
                if (alarmReader is not null) await alarmReader;
                alarmLifetime?.Dispose();
                if (api is not null)
                {
                    try { await api.DisconnectAsync(true).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                    try { api.Dispose(); } catch { }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        state.Set("stopped");
    }
}
