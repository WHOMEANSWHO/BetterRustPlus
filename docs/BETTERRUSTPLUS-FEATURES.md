# BetterRustPlus feature checklist

The supplied screenshot matches the feature comparison on [Rustalytics' official page](https://rustalytics.com/start), checked 1 November 2026. The labels below are normalized for our implementation checklist. This is a list of requested behavior, not a claim that Rustalytics' hosted service, code or paid access is part of this fork. BetterRustPlus is intended for the owner's group without Basic/Pro tiers.

**Inherited** means relevant desktop code exists and still needs real-server acceptance checks. **Partial** means only some behavior exists. **Needed** means no verified equivalent has been identified. All service-backed behavior requires the group's own services and credentials.

| Screenshot feature | Current evidence / work remaining |
| --- | --- |
| Settings | Inherited; settings navigation/focus improved. |
| Historical data | Partial: desktop wipe tracker; group-owned server history needed. |
| Web access | Needed: group dashboard. |
| Friend sharing | Partial: inherited upstream cloud integration; group-owned sharing needed. |
| Operation with app closed | Partial: VPS worker foundation; deployment and live connection acceptance needed. |
| Discord bot invitation | Partial: own bot worker and manual application/invite setup are provided; live setup remains unverified. |
| Discord raid alerts | Partial: paired Smart Alarm transitions can route through an approved bot channel or webhook; live acceptance needed. |
| Discord offline deaths | Partial: paired team death transitions/timestamps produce last-seen-grid notices while the listener runs; live offline-death acceptance needed. |
| Discord smart alarms | Partial: independent push/resync processing, retry and delivery routing implemented; real-server acceptance needed. |
| Discord Rust/server statistics | Partial: inherited bot adapter; own bot needed. |
| Cargo: spawn, crates, docking | Partial: desktop event/routing code; stages depend on available server data. |
| Oil rig: crate, heavies, radiation | Partial: inherited event code; verify each stage independently. |
| Vendor: spawn/restock | Partial: inherited status code; verified restock source needed. |
| Helicopter: spawn/crash/time | Partial: inherited status/position code; distinguish confirmed crash from disappearance. |
| Team joins/leaves | Inherited team-state handling; server acceptance needed. |
| Death: lifespan/location | Partial: death statistics and grid alert; exact death location needs source. |
| AFK: location/duration | Inherited movement-based team AFK estimate; label estimate. |
| Online/offline location | Inherited team state; timestamp last-known positions. |
| English | Inherited resources. |
| Russian | Inherited resources; new strings need translation review. |
| Spanish | Inherited resources; new strings need translation review. |
| Event toggles | Inherited desktop settings; VPS rule configuration exists. |
| Chat message delay | Inherited per-profile command/response delays. |
| Special-character filtering | Needed: verify existing formatting and add explicit filter control. |
| Unlimited switches | Inherited device collection; practical server limits still apply. |
| Switch groups | Partial: multiple-device command mappings; group UX needs review. |
| Alarm chat messages | Inherited device/automation handling; real alarm acceptance needed. |
| Alarm Discord messages | Partial: own delivery foundation; end-to-end wiring needed. |
| `!pop` | Inherited population command. |
| `!time` | Inherited time command. |
| `!wipe` | Needed: verified command equivalent. |
| `!oil` / `!smoil` / `!loil` | Partial: configurable oil command exists; aliases/stages need parity. |
| `!cargo` | Inherited, source-dependent. |
| `!heli` | Inherited, source-dependent. |
| `!promote` / `!leader` | Partial: configurable promote command exists; alias/permissions need parity. |
| `!team` | Needed: explicit command equivalent. |
| `!switch` | Partial: named command mappings exist; common command syntax needed. |
| `!buy` / `!sell` | Partial: vending/shop functionality exists; command syntax/source availability need parity. |
| `!craft` | Partial: calculators exist; command handler needed. |
| `!recycle` | Partial: calculator exists; command handler needed. |
| `!timer` | Inherited configurable custom timers; existing five-timer cap differs. |
| `!chinook` | Partial: event kind exists; command handler needed. |
| `!tracked` | Partial: player tracker exists; command/privacy semantics need review. |
| `!version` | Needed: explicit version command. |
| `!switches` | Needed: paired-switch list command. |
| `!vendor` | Inherited, source-dependent. |

## Code evidence

- Desktop commands: `RustPlusDesktop/Views/MainWindow/Map/MainWindow.Map.ChatCommands.cs`; profile delay/command mappings: `RustPlusDesktop/Models/ServerProfile.cs`.
- Team and death statistics: `RustPlusDesktop/Views/MainWindow/Team/MainWindow.Team.Core.cs`, `MainWindow.PlayerWipeTracker.cs`, `DeathStatsView.xaml.cs`.
- Devices/automation: `RustPlusDesktop/Views/MainWindow/Devices/MainWindow.Devices.cs`, `MainWindow.DeviceAutomation.cs`, `MainWindow.LogicEngine.cs`.
- Event restrictions and marker support: `RustPlusDesktop/Services/EventCapabilities.cs`. Unavailable data must remain visibly unavailable, never a fabricated inactive state or countdown.
- Existing upstream service adapters: `RustPlusDesktop/Services/DiscordBotListenerService.cs`, `Services/Cloud/CloudDiscordAdapter.cs`, `Services/Cloud/CloudChatCommandSync.cs`.
- Group alert foundation: `BetterRustPlus.Server/Program.cs`, `Core/AlertEngine.cs`. Dry-run is the default; live Discord delivery is not verified.
- Languages: `RustPlusDesktop/Properties/lang/ru-RU/Resources.ru-RU.resx`, `lang/es-ES/Resources.es-ES.resx`.

## Additional owner requirements

- [ ] Whole-map team display using positions actually supplied by the server.
- [ ] Death grid messages with exact versus last-known position labels.
- [ ] Airdrop and Deep Sea alerts with explicit source/evidence and estimates.
- [ ] Group-owned Discord bot with per-rule channels, recipient selection, optional raid `@everyone`, cooldowns and deduplication.
- [ ] Steam identity linking and server presence where available; never infer current play from a saved server.
- [ ] 3D terrain/build scouting from accessible matching map data; clear 2D fallback.
- [ ] Ore/stone potential-density overlay; never imply live unmined-node scans or measured respawn rates without data.
- [ ] Cleaner navigation and onboarding with loading, empty, error and recovery states.
- [ ] Always-on VPS service, durable state and operational recovery tests.

Mark a feature complete only after its public-source build, meaningful offline tests and applicable live acceptance checks pass. The screenshot's checkmarks describe the reference product, not BetterRustPlus completion.
