# BetterRustPlus alert service foundation

A .NET 8 service for Windows or Linux, independent of the WPF desktop application. This is an early local development foundation, **not a deployed Rust+ listener or a production-ready bot**.

## What works

- `GET /health` reports service operation, dry-run state and explicitly that no game source is connected.
- `POST /events` accepts authenticated, validated observations for cargo, airdrop, Deep Sea, raid alarms and teammate deaths.
- Rules select event kinds, servers, delivery target, optional everyone mentions and cooldown.
- Duplicate suppression, event-age checks and server allowlisting prevent simple replays or mixing server events.
- Dry-run is enabled by default and never calls Discord. Optional live delivery uses official Discord webhook addresses with explicit mention restrictions.

There are no Rust+ connections, automatic detectors, Steam integration, VPS provisioning or Discord application/invite flows here. A webhook is a delivery adapter, not the eventual interactive Discord bot.

## Run locally

Install the .NET 8 SDK, then from this folder:

```sh
dotnet build
dotnet run --no-build
```

The default address is `http://127.0.0.1:5088`. Health is public on this loopback interface. Ingestion returns **503** until `BETTERRUSTPLUS_INGEST_KEY` is set to a random value at least 32 characters long. It then requires `Authorization: Bearer <key>` and returns **401** for an invalid key. Never commit the key or webhook credentials. Supply them through the host's environment/secret facility.

Configuration uses standard .NET environment names. For example, in a development shell, set a newly generated random ingest key and:

```text
Alerts__AllowedServerIds__0=our-server
Alerts__Rules__0__Id=raid-alert
Alerts__Rules__0__Kinds__0=raid
Alerts__Rules__0__CooldownSeconds=60
Alerts__Rules__0__Target=primary
Alerts__Rules__0__MentionEveryone=false
```

Keep `Alerts__DryRun=true` while testing. Once explicitly ready to send real messages, configure `Alerts__Targets__primary__WebhookUrl` with your Discord webhook URL and set `Alerts__DryRun=false`. Everyone mentions require per-rule `MentionEveryone=true`; player-provided names cannot insert a real mention. Discord permissions and user notification settings still govern actual notifications. No live delivery has been tested by the agent.

`ASPNETCORE_URLS` can override binding, but do not expose this development HTTP endpoint publicly. A real deployment needs TLS termination, network restrictions, securely stored credentials, persistent state, monitoring and operational review. The per-process ingestion limit is 60 requests/minute, with no request queue and a 16 KiB body limit.

## Event contract

```json
{
  "id": "21bddfaa-4fd3-467e-a38c-f18b9908973e",
  "serverId": "our-server",
  "kind": "teammate-death",
  "occurredAt": "2026-10-31T22:00:00Z",
  "evidence": "confirmed",
  "playerName": "Nathan",
  "grid": "G14",
  "positionIsLastKnown": false
}
```

Use a fresh UTC timestamp for tests; this example is illustrative. Event IDs must be nonempty and stable across retries. `kind` is `cargo`, `airdrop`, `deepsea`, `raid` or `teammate-death`. `evidence` is `confirmed`, `estimated` or `teammate-report`; this labels caller-supplied evidence and does not verify the caller's game observation. Events older than five minutes or more than 30 seconds in the future are rejected. Only configured server IDs are accepted. Grid is optional; missing locations say unavailable, and `positionIsLastKnown=true` explicitly says "Last seen at G14" rather than claiming a death location. Teammate deaths require a player name.

Results report counts for delivered, simulated, duplicate, cooling-down and failed rules. Delivery failures return **502** and can be retried with the same ID; successful rules are not sent again. Unknown servers, unsupported event types and invalid timestamps return **400**. Cooldowns are per rule, server and event kind; use a separate rule with a zero cooldown for death messages if every death should be delivered. Suppressed events are consumed so replaying them after cooldown does not send a delayed alarm.

Only one event owns the delivery pipeline at a time. If another event arrives during a slow webhook request, it returns **503**, `status: busy`, and `Retry-After: 1` immediately. No event state is consumed by a busy response. The caller must retain and retry the **same event ID** with bounded backoff while it remains fresh; this service has no durable queue. A busy response is distinct from the unconfigured-ingestion 503, which requires configuration rather than automatic retries. Event freshness is rechecked before each matching rule; if an earlier slow target causes the event to expire, no further target begins delivery, and the response returns **400** with `status: expired` and counts of completed work. Each Discord request has a ten-second timeout. A later priority-aware queue is still needed for production raid-alert guarantees.

## Verification

```sh
dotnet run --project Tests/BetterRustPlus.Alerts.Tests.csproj
```

The self-contained tests cover validation, freshness, server isolation, replay suppression, cooldown, failure/retry, mention formatting and dry-run behavior without contacting Discord. A controlled blocked sender also verifies that an unrelated event receives a prompt busy response and can later retry the same ID, and that slow rule processing does not begin a new stale delivery. From PowerShell 7 after a Debug build:

```powershell
./Tests/Smoke-Test.ps1 -DotnetPath /absolute/path/to/dotnet
```

This launches and stops temporary loopback hosts and tests health, missing configuration, authentication, event processing and duplicate suppression. It never enables live delivery.

## Remaining production work

- Actual Rust+ pairing/connection and verified event sources; unavailable game data cannot be created by keeping this service online.
- Persistent delivery/deduplication and cooldown state: current state is memory-only and resets on restart.
- A durable queue/outbox, crash recovery and handling Discord rate limits/retries; delivery is not guaranteed exactly once when a connection fails after Discord receives a message.
- Interactive Discord bot commands, acknowledgements, role selection and a desktop configuration UI.
- Hosting, TLS, operational health, key rotation, secure backups and deployment packaging.

No secrets or game credentials belong in the repository. No external messages are sent during the included tests.
