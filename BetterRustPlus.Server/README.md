# BetterRustPlus alert service foundation

A .NET 8 service for Windows or Linux, independent of the WPF desktop application. This includes an optional paired Rust+ source and alert delivery foundation, **not a deployed service or a production-ready bot**.

## What works

- `GET /health` reports service operation, dry-run state, configured source capabilities and current source connection state.
- `POST /events` accepts authenticated, validated observations for cargo, airdrop, Deep Sea, raid alarms and teammate deaths.
- Rules select event kinds, servers, delivery target, optional everyone mentions and cooldown.
- Duplicate suppression, event-age checks and server allowlisting prevent simple replays or mixing server events.
- Dry-run is enabled by default and never sends event alerts. Optional live delivery uses an allowlisted bot text channel or an official Discord webhook address, with explicit mention restrictions. Separately enabling the command bot connects it to Discord even in dry-run mode.
- An opt-in paired Rust+ source polls team state/death transitions and newly visible cargo markers, and consumes Smart Alarm push updates with periodic resync. Credentials and a server exposing those data are required; this path has not been tested against a live account/server.
- `GET /team` requires the ingest bearer key and returns timestamped last-observed team positions with online/alive flags. Missing or stale observations are explicit; no team positions are exposed through public health.
- An opt-in Discord bot offers private, read-only `/brp-status`, `/brp-alerts` and `/brp-team` commands in configured group channels. It uses only the non-privileged Guilds intent and does not read message content.

There is no universal airdrop/Deep Sea detector, Steam friends/server-presence integration or VPS provisioning here. Discord app creation and invitations are manual; the bot does not yet edit rules or acknowledge alarms. Native service deployment is provided, but it has not been installed on a real VPS here.

## Keep the service running on a VPS

Use the [native VPS setup guide](deploy/README.md). The listener installs directly as `betterrustplus.service` under systemd, with automatic boot startup, crash recovery and a restricted service user. Docker is not required. Configuration is prepared interactively, and installation needs sudo.

From the checked-out repository after installing Python 3 and the .NET 8 SDK:

```sh
python3 BetterRustPlus.Server/deploy/setup.py --configure-only
sudo python3 BetterRustPlus.Server/deploy/setup.py --install
```

Installed binaries live under `/opt/betterrustplus`; private settings live in `/etc/betterrustplus/listener.env`. Installation and updates preserve existing settings, validate startup against the service's actual process ID, and reselect the prior release if an updated release fails startup. The service binds only to `127.0.0.1:5088`. No domain or incoming public API port is needed for the built-in source and bot.

The service can run while every desktop is off. Hosting does not grant unavailable game data: raids mean configured paired Smart Alarm transitions, deaths use available team state, and airdrop/Deep Sea events need another verified producer. `/health` reports source state and freshness; service liveness is not proof of a working detector. Data and retry state remain in memory.

### Connect your own paired server

The disabled `RustSource` fields in `.env.example` are optional. The native wizard writes the selected values to the installed `/etc/betterrustplus/listener.env`. To enable polling, set `RustSource__Enabled=true` and supply `ServerId` matching the alert allowlist, the paired `Host`/companion `Port`, your `SteamId` and paired `PlayerToken`. If your connection requires the companion relay, supply both `CompanionServerId` and `AuthToken`; these are distinct from your ingest bearer key. Use values from your own valid Rust+ pairing. No login credentials should be guessed or copied from another player. The service has no account-login/export UI yet, so transferring valid pairing values is currently a manual setup step.

Add only your paired Smart Alarm entity IDs using `RustSource__AlarmEntityIds__0`, `__1`, etc. `PollSeconds` accepts 5–300, default 15. Cargo is detected when a cargo marker first appears after a baseline; an alarm must transition from observed inactive to active. The initial snapshot and reconnect baseline do not generate historical alarms. Death notices use a fresh death timestamp or observed live-to-dead transition and label the previous observed grid **last seen**. They cannot report the killer, weapon or an exact unobserved death position. Observations missed between polls or while disconnected may be unavailable. Live source behavior still needs verification on a supported paired server before relying on it for raids.

Alarm updates are processed independently of polling. Failed/busy alarm deliveries keep their original IDs in a bounded in-memory retry queue with a one-second backoff, allowing other ready observations to proceed. Events expire after five minutes; buffer overflow is reported as degraded operation. Each actual delivery attempt still depends on target response times, so this is not a hard latency or exactly-once guarantee.

Restart betterrustplus.service after changing the installed source settings and inspect `/health` and logs. Source failures produce a generic warning without printing paired credentials and reconnect after a delay. Current retries are bounded and in memory; no delivery guarantee is implied. Verify source availability with every desktop off before assuming the VPS replaces a running game client for a particular server.

For a temporary private connection from your own computer, an SSH tunnel can expose the loopback endpoint locally:

```sh
ssh -N -L 5088:127.0.0.1:5088 your-user@your-vps
```

An approved producer can then send to `http://127.0.0.1:5088/events` on your computer with its bearer key. This tunnel depends on that computer; the optional built-in paired source runs directly on the VPS and does not use this tunnel. Any other always-on producer needs an independently available connection. Do not change the port mapping to public unencrypted HTTP. A public endpoint requires a configured HTTPS reverse proxy and access controls; no domain, certificates or firewall changes are provisioned here.

To enable Discord webhook delivery later, create a webhook in the intended Discord channel, add its URL using `Alerts__Targets__primary__WebhookUrl`, then deliberately set `Alerts__DryRun=false`. Or configure bot delivery as described below. Set only the raid rule's `MentionEveryone=true` if the group wants raid pings, and ensure the selected Discord sender has the required mention permission. This starts real messages for matching events. Keep dry-run enabled until you have reviewed the server ID and rules.

### Add the optional command bot to your Discord

1. Create a group-owned application in the [Discord developer portal](https://discord.com/developers/applications) and create its bot. Keep the bot token private; it is different from the application/client ID, webhook URL and Rust+ credentials.
2. In the portal's OAuth2 URL generator, select `bot` and `applications.commands`. Request only **View Channels** and **Send Messages** for the command channel. Do not request Administrator, Message Content, Server Members or Presence intents. Group members need **Use Application Commands** in that channel. Invite the app to your group's server using the generated URL.
3. Enable Discord's Developer Mode to copy the group's server ID and command channel ID. In `/etc/betterrustplus/listener.env`, set `DiscordBot__Enabled=true`, `DiscordBot__Token`, `DiscordBot__GuildId`, and `DiscordBot__AllowedChannelIds__0`. Add `__1`, etc. for other approved channels, up to 32. At least one channel is required. Only the configured guild and channels are authorized; direct messages and other guilds are denied.
4. Run `sudo systemctl restart betterrustplus`. Enabling the bot connects to Discord and registers this app's three guild commands. It does not register global commands or remove unrelated commands. If registration fails, check the configured guild, invitation scopes and connectivity; logs deliberately omit tokens and Discord request details. No live bot login or registration has been verified by the agent.

All command replies are **ephemeral** (visible to the invoking member), suppress mentions, and truncate large lists with an explicit remaining count:

| Command | Result |
| --- | --- |
| `/brp-status` | Delivery mode, source state/freshness, last successful team poll and rule count. A connected/degraded source is not proof every detector works. |
| `/brp-alerts` | Configured trigger kinds, server filters, cooldowns and everyone-mention settings. It does not expose webhook credentials or modify rules. |
| `/brp-team` | Latest observed member names, online/alive state and last-seen grids, with observation time and stale/unavailable labels. It cannot reveal withheld positions or an exact unobserved death location. |

The bot stays online with the VPS while your desktops are off. Source data still requires valid server pairing and the server exposing the requested information. Read-only commands do not change configuration, invite members, send a test alarm or ping everyone. For now, edit rules on the VPS.

To deliver event alerts **through the bot** instead of a webhook, configure a target with an approved guild text-channel ID:

```text
Alerts__Targets__primary__DiscordChannelId=YOUR_ALERT_CHANNEL_ID
```

Add that channel to `DiscordBot__AllowedChannelIds` and ensure the bot can view/send there. Remove `Alerts__Targets__primary__WebhookUrl` for this target; a target must have exactly one delivery route. Keep `Alerts__DryRun=true` to check rules without alert messages, then deliberately switch it to false when ready. For real raid pings, set the raid rule's `MentionEveryone=true` and grant the bot **Mention @everyone, @here and All Roles** in the alert channel. This permission is unnecessary for ordinary alerts or slash-command replies. The sender permits everyone mentions only for explicitly opted-in alerts; command replies suppress all mentions. Bot delivery supports guild text channels, not direct messages, forums or threads. If the bot disconnects, delivery reports failure for the caller's existing bounded retry path; it does not pretend a message was sent. No live bot alert has been sent by the agent.

Bot-specific checks run offline, without a token or Discord connection:

```sh
dotnet run --project Bot.Tests/BetterRustPlus.Bot.Tests.csproj
```

The bot uses [Discord.Net's guild application commands](https://docs.discordnet.dev/guides/int_basics/application-commands/slash-commands/creating-slash-commands.html) and Discord's [application-command scopes and permissions](https://docs.discord.com/developers/docs/interactions/slash-commands).

After editing `/etc/betterrustplus/listener.env`, apply settings with:

```sh
sudo systemctl restart betterrustplus
sudo journalctl -u betterrustplus -n 50 --no-pager
```

To update code, pull the intended reviewed branch and run the native installer with `--install`; installed credentials are preserved. Stop it with `sudo systemctl stop betterrustplus`, or disable boot startup with `sudo systemctl disable --now betterrustplus`. Runtime replay/cooldown state resets on restart. Protect and back up the installed settings; no actual VPS has been configured from this checkout.

The deployment follows [Microsoft's Linux service-hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-8.0) and [systemd execution settings](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html). The native installer builds only the listener and its public package feed, not the Windows desktop or private map parser.
## Run locally

Install the .NET 8 SDK, then from this folder:

```sh
dotnet build
dotnet run --no-build
```

The default address is `http://127.0.0.1:5088`. Health is public on this loopback interface. Event ingestion and the `/team` snapshot endpoint return **503** until `BETTERRUSTPLUS_INGEST_KEY` is set to a random value at least 32 characters long. They then require `Authorization: Bearer <key>` and return **401** for an invalid key. Never commit the key, pairing credentials, bot token or webhook URL. Supply them through the host's environment/secret facility.

Configuration uses standard .NET environment names. For example, in a development shell, set a newly generated random ingest key and:

```text
Alerts__AllowedServerIds__0=our-server
Alerts__Rules__0__Id=raid-alert
Alerts__Rules__0__Kinds__0=raid
Alerts__Rules__0__CooldownSeconds=60
Alerts__Rules__0__Target=primary
Alerts__Rules__0__MentionEveryone=false
```

Keep `Alerts__DryRun=true` while testing event processing. Once explicitly ready to send real messages, configure one delivery route per target (webhook URL or enabled/allowlisted bot channel) and set `Alerts__DryRun=false`. Everyone mentions require per-rule `MentionEveryone=true`; player-provided names cannot insert a real mention. Discord permissions and user notification settings still govern actual notifications. Dry-run does not disable an explicitly enabled bot's Discord connection or command replies. No live delivery has been tested by the agent.

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

- Live verification of the optional paired source, a pairing setup UI and additional verified event sources; unavailable game data cannot be created by keeping this service online.
- Persistent delivery/deduplication and cooldown state: current state is memory-only and resets on restart.
- A durable queue/outbox, crash recovery and handling Discord rate limits/retries; delivery is not guaranteed exactly once when a connection fails after Discord receives a message.
- Live validation of the read-only Discord bot, editable alert commands, acknowledgements, role selection and a desktop configuration UI.
- Actual hosting, TLS, operational monitoring, key rotation and secure backups. Container packaging is provided, but it still needs runtime verification on the target host.

No secrets or game credentials belong in the repository. No external messages are sent during the included tests.
