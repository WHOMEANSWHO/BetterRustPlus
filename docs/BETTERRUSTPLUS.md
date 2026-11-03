# BetterRustPlus

Owner: WHOMEANSWHO. Repository: https://github.com/WHOMEANSWHO/BetterRustPlus. Initial audit: Pronwan/rustplus-desktop v10.3.0, commit fbf5119d86b789ad89540a323f9c6a1da14d8f99. Development branch now includes upstream master 177db4d1, whose changes since that release affect the README and a screenshot only.

## Product direction

### Development progress — 3 November 2026

The desktop opens with an isolated test profile. The integrated desktop suite passed 204 tests with one existing skip after the image and camera changes. The public companion-account library passed 22 offline tests. Eight focused adapter/browser tests pass, but real Steam/Rust+ pairing and companion relay have not been verified. Login initialization respects window closure/cancellation, incomplete pairing files are preserved for recovery, and repair distinguishes invalid pairing from unavailable services.

The listener includes an optional paired Rust+ source, protected team snapshots and event rules. Offline checks passed 75 alert/source cases, 45 bot-policy/delivery cases and 17 local HTTP cases, without game or Discord connections. Malformed null configuration collections produce validation errors instead of crashes. Team deaths distinguish last-seen grids from exact death locations, and source health distinguishes disabled, degraded, stale and disconnected states. Alarm processing runs independently of slow polling, with bounded retry scheduling that preserves event IDs. Native deployment checks passed 35 setup tests on Windows, with two POSIX-specific tests reserved for Linux CI; the installer also successfully published the minimal self-contained Linux payload.

Genetics Lab builds with Vite 6.4.5 and Vitest 4.1.12; npm audit reports zero vulnerabilities. Its public suite passed 441 tests with 28 skips: 16 existing skips and 12 explicitly reported cases requiring ten unpublished captures. All 17 published capture files remain mandatory. A strict capture mode fails if the unpublished files are absent. Generated geometry tests supplement the available real captures without claiming to reproduce the missing game captures.

ImageSharp has been removed completely, including the camera package's transitive reference. MIT-licensed SkiaSharp handles icons, animated WebP and camera PNG encoding. Public camera source is pinned with provenance/licenses; five real captured camera sequences pass pixel-exact golden checks. The full desktop dependency audit reports no known vulnerable packages from the configured sources. Live login, Discord delivery, source verification and replacement map parsing remain release requirements.

The Windows desktop compiles with public companion adapters. Genetics Lab builds separately. Initial navigation improvements preserve settings location and add keyboard/focus support. BetterRustPlus uses separate data folders and a separate protocol, with an isolated workspace profile available for testing.

A separate .NET 8 service accepts authenticated supplied events and optionally connects to one paired Rust+ server. It observes available team/death state, new cargo markers and configured Smart Alarm changes; universal airdrop/Deep Sea detection is not implemented. An optional group Discord bot provides private status, alert-rule and team commands and can deliver alerts to approved channels; official webhooks are an alternative. Live integrations stay disabled until configured. The default VPS installer now uses a native Linux systemd service without Docker, with private configuration, versioned releases, startup identity checks and rollback. Linux CI exercises the native install path. Delivery state remains memory-only and resets on restart; this is not yet a production alert guarantee.

The GitHub fork and development branch exist. No installer has been released. The [full feature checklist](BETTERRUSTPLUS-FEATURES.md) includes all 46 screenshot rows and the owner's additional requirements, with inherited, partial and missing behavior distinguished. Live acceptance, durable delivery, a group dashboard, complete command parity and 3D map data remain outstanding.

Preserve the upstream feature set and improve how people find and configure it. Keep C#/.NET 8 and WPF for the Windows desktop application. Keep the embedded Genetics Lab frontend in its existing TypeScript stack. A language rewrite would put feature parity at risk without solving navigation.

Initial changes enable sidebar labels and expand Tools for new layouts. Explicit saved preferences and existing folder arrangements remain respected. The updater targets WHOMEANSWHO/BetterRustPlus; its first release must exist before updates can work. This is an initial development change, not a completed redesign or release.

## Group requirements confirmed 31 October 2026

BetterRustPlus is for WHOMEANSWHO and their friends. Prioritize private group configuration and a shared Discord server; public subscriptions and commercial account flows are not required. The GitHub fork remains public unless separately changed.

| Requirement | Intended behavior | Dependency or limit |
| --- | --- | --- |
| Team across the whole map | Display available in-game team positions regardless of distance from the paired player. Include names, grid coordinates, follow-player and fit-team actions, plus last-update/stale indicators. | Requires an authorized Rust+ connection and positions supplied by that server. Discord membership alone does not provide game positions. Do not invent locations or promise continuous real-time updates. |
| Resource heatmaps | Separate stone, metal, sulfur and HQM layers; density threshold, legend and farm-route planning. | Parse map/resource placement data using a replacement for the missing parser. Label potential spawn density clearly; it is not a scan of currently unmined nodes or a measured live respawn rate. Modded server behavior can differ. |
| Discord bot | Install a group-owned bot in the group's Discord. Configurable channels and per-rule recipients, including @everyone for configured raid alerts. Support acknowledgements, cooldowns, deduplication and test alerts without an everyone ping by default. | Needs a Discord application, appropriate channel/mention permissions, and protected bot credentials. No bot has been created or invited yet; no messages have been sent. |
| Situational alerts | Rules with trigger, optional conditions, action, recipient and cooldown. Initial candidates: paired Smart Alarm, storage-monitor upkeep threshold, team death, teammate online/offline and supported timer events. | Each trigger must be verified against actual data availability. Raid detection initially means a configured in-game Smart Alarm event, not universal detection of damage to any building. |
| Alerts while PCs are off | Separate the always-on alert listener/bot worker from the desktop UI. | An always-on host is required; closing the only running listener cannot preserve alert delivery. Hosting and credentials are not provisioned. |
| 3D build planning across servers | Select the correct map for each server/wipe; render terrain and monuments with slope, elevation and building-restriction overlays where source data supports them. Save/share candidate build spots. | Requires accessible matching map data and replacement parsing/rendering components. Support custom maps where their data is available. Do not promise every server or guaranteed valid placement from a 2D map image. Fall back to 2D with a clear explanation. |

### Steam linking and teammate death messages

- Link group members to their Steam identity, profile name/avatar and optional Discord identity. The fork contains Steam login/OpenID and profile-service code, but it has not been runtime-verified in BetterRustPlus.
- Show the server a teammate is currently playing on when a supported source exposes it. Potential sources include visible Steam game/server presence and an opted-in member's BetterRustPlus client reporting its verified active server. Validate Steam/Rust integration rather than assuming generic Steamworks capabilities are available to this third-party app or the VPS.
- Steam OpenID verifies identity; signing in does not by itself grant friend-list access, private activity or server details. Respect available activity/privacy settings and display "Server unavailable" or an explicitly timestamped last-seen server when live data is absent. Never treat selection of a saved paired server as proof the player is currently playing there.
- Keep Steam identity linking distinct from Rust+ companion authorization and per-server pairing.
- Teammate death messages should state the grid, for example "Nathan died at G14", with a map marker and configurable Discord/team-chat destinations. Use "Last seen at G14" when only a last-known position exists; do not silently label it the death location.
- Killer name and weapon are optional fields requiring a verified source, potentially player-side death-screen recognition while the game is running. Otherwise show unknown; neither Steam linking nor a VPS supplies these fields automatically.

### Event alert detection requirements

Owner requests cargo, airdrop and Deep Sea notifications using available detection methods. These are priority requirements, not implemented new capabilities.

- Cargo: arrival/spawn, harbour/docking and departure alerts where an authoritative source actually supplies those stages. Do not infer exact ship location or docking times from an event announcement alone.
- Airdrops: flight/drop notifications and locations only when a source provides that information. Distinguish a plane observation from a confirmed supply drop and a confirmed landing. Airdrops are not present in the inspected RustEventKind catalog; they require a new event type and source integration.
- Deep Sea: opening/closing notifications and supported advance reminders. Show estimates explicitly; do not promise exact future transitions or entry locations without data.
- Also inventory support for patrol helicopter, Chinook, oil rigs and other server events, enabling each alert only when a usable source is configured.

Detection source order: actual Rust+ data when exposed by the connected server; administrator-approved event plugin/feed when the group controls a server or its admin provides access; then clearly attributed manual teammate reports and estimates. Investigate player-side audio or on-screen event recognition as a separate feasibility task only for information available to a running player client; it is not a guaranteed inherited feature or an always-on VPS source.

Important source finding: historical README release notes describe shared audio detection, but the inspected EventCapabilities.cs explicitly says "no shared audio reports", restricts fallback trackable events to OilRig, and disables cargo/Deep Sea announcement templates without API event markers. Audio capture utilities alone do not establish a working detector. Correct any earlier assumption that inherited audio detection is ready to use.

VPS limitation: continuous bot uptime does not supply unavailable game events. When every game client is off, only sources independently available to the VPS can generate fresh event detections. A server plugin requires owner/admin cooperation and cannot be installed by an ordinary player on an arbitrary server.

UI: one Events page listing each event, source, last update and state (confirmed, estimated, teammate-reported, unavailable). Per-event controls select Discord channel, mention target including optional @everyone, alert stages, reminder offset, cooldown and duplicate handling. Keep unknown/unavailable distinct from "event inactive". Preserve server/wipe identity and ignore stale duplicate reports after reconnect.

Acceptance: verify each event with real data on a supported server, independently test missed/duplicate/stale detections and server switches, and test each source with all desktops off. No automatic event alerts or Discord delivery have been validated in BetterRustPlus yet.

### Language and performance approach

Keep the existing C#/WPF desktop app while resolving public-build dependencies and improving navigation. A whole-app rewrite in the Rust programming language has no demonstrated performance benefit for this project and would require replacing the existing UI/integrations. Measure startup, UI responsiveness, memory, map parse time and 3D frame times first. Rust remains a candidate for an isolated parser or other measured CPU bottleneck; network update frequency and missing server data are not solved by a language change. This is an engineering recommendation, not a benchmark result.

### Acceptance before claiming these features work

- Verify two teammates far apart remain visible using actual paired server data; distinguish stale/disconnected state.
- Validate heatmaps against known map data and distinguish spawn candidates from live resource availability.
- Trigger a real paired Smart Alarm, receive exactly the configured Discord alert, acknowledge it, and verify cooldown/deduplication. Test outage recovery and stale queued events before enabling everyone mentions.
- Verify the same workflow with the desktop closed and the separate worker running.
- Compare rendered terrain against a known procedural map and an accessible custom map, switch servers and wipes, and ensure cached data never crosses map identities.
- Keep a feature inventory so no original option disappears during the navigation redesign.

These are requirements and acceptance criteria; they have not been implemented or verified yet.

## Navigation proposal

### Ease-of-use redesign direction

The owner explicitly prioritizes a less clunky app that is easy to understand and navigate. Apply the latest session AGENTS instructions: platform conventions, accessible hierarchy and feedback, resizable layouts and coordinated parallel review for substantial independent work. These instructions are being followed in this chat; no persistent global settings were changed.

Two agents reviewed navigation and usability independently. This is source review, not a screenshot-based audit: the current checkout cannot launch until missing authentication dependencies are resolved.

Use a task-oriented shell with a persistent server selector, connection status and separate VPS-listener status. Proposed destinations:

- Overview: connection health, active alarms and remaining setup steps.
- Map: team positions, labelled layers, 2D/3D, resources, routes and saved build spots.
- Base: switches, alarms, storage/upkeep, groups and cameras.
- Team: members, clan/team chat, activity, wipe history and deaths.
- Alerts & Automation: rules, Discord delivery, history, acknowledgement and test alerts.
- Tools: genetics, raid/recycling calculators, console helper and overlay configuration.
- Settings at the bottom: accounts, pairing, appearance, performance, integrations and backup.

Keep all inherited features; this proposal supersedes the earlier grouping below. Map every existing entry point to a destination before changing navigation.

Interaction requirements:

1. Use readable names beside icons. Everyday actions must not require right-click or double-click discovery; provide visible menus and retain shortcuts as alternatives.
2. Present one clear connection action and explain progress in terms of what becomes available. Keep specialized connection modes under Advanced.
3. Guide first-run setup: account, server pairing, local/VPS listener, Discord and test alert. Allow steps that are not currently relevant to be skipped.
4. Put alert setup in one flow: trigger, conditions, destination/recipient, cooldown and test. Link to integration details rather than making the user hunt through settings categories.
5. Give each setting one canonical home. Preserve settings search and link users to the relevant control.
6. Keep common options visible and advanced options expandable. Explain unavailable features and show a useful next action; do not imply unsupported data is live.
7. Make loading, no-server, no-devices, disconnected, stale-data, failed-connection and success states explicit. Distinguish desktop connectivity from VPS operation.
8. Preserve the last settings category and scroll position; reopening settings should not lose the user's place. Escape should close the topmost noncritical panel and restore focus to its opener, after checking existing handlers.
9. Use constrained icon/text columns for navigation labels, reflow settings rows when narrow, and retain rail scrolling. Disabled integration actions need a persistent reason and direct setup action.
10. Verify keyboard focus, named controls, non-color status indicators, wrapping and scrolling at 1280x720, 1920x1080 and 125%, 150% and 200% Windows scaling before describing the redesign as tested. Report limits when scaling makes the available logical area smaller than the app's minimum window size.

Implementation and visual verification are pending; this source-review direction is not a completed UI redesign.

| Area | Features to keep reachable |
| --- | --- |
| Map | 2D/3D maps, layers, heatmaps, markers, drawings, routes, base planning, events, timers, shop search and trade tools |
| Devices & Automation | Switches, alarms, storage monitors, groups, hotkeys, logic rules, CCTV and device controls |
| Team & Chat | Team, clan, player tracking, wipe history, death statistics, community and messages |
| Tools | Genetics Lab, raid and recycling calculators, console helper, overlay widgets, minimap and crosshair controls |
| Settings | General and appearance, pairing and accounts, notifications, chat commands and templates, map quality, AI companion, integrations, backup/reset and credits |

Keep the selected server and connection status visible. Use labelled navigation and consistent page headings. Put everyday actions on the page and advanced configuration in named sections. Preserve all per-setting search and localization. Do not move a feature until its current entry points and availability conditions are recorded.

## Dependencies that prevent a promise of full parity

- BUILDING.md says the proprietary MapParser is excluded from the public source. 3D maps and parser-derived map data require licensed access or an independently built replacement.
- .gitmodules and the release workflow reference JawadYzbk/rustplus-auth-private. CompanionAuthManager falls back to an unavailable registration provider without it. Pairing and authentication must be verified before claiming the app is usable.
- The owner confirmed they do not have access to either private project. Independent replacement implementations are needed unless access is obtained later. Missing functions must not be replaced with success-returning stubs to claim feature parity.
- Cloud, community, sync, Discord bot and smart-home features rely on upstream services and configuration. Forking client code does not establish independent backend infrastructure or provide access credentials.
- The upstream release workflow checks out the private MapParser unconditionally. Copying that workflow will not reproduce the maintainer's installer without access.
- A workspace-local .NET 8 SDK and npm were installed and dependencies restored. The original public build failed on missing authentication classes; compatibility adapters now allow compilation while clearly reporting unsupported authentication and repair. Genetics Lab builds separately.
- ImageSharp and its transitive camera dependency were replaced by SkiaSharp; the current desktop dependency audit and frontend audit report no known vulnerabilities from the configured sources.

## GitHub and release work remaining

1. Done: created the actual GitHub fork under WHOMEANSWHO/BetterRustPlus, preserving upstream branches and tags. Local origin points to the fork; upstream points to Pronwan/rustplus-desktop.
2. Publish the development branch and establish a passing build/test baseline after resolving missing authentication code and the frontend toolchain. Publishing is currently blocked: the GitHub connector rejected branch creation with HTTP 403 (Resource not accessible by integration) and lists no repository app installations. Git Credential Manager has no signed-in accounts; a device sign-in attempt did not produce a prompt and was cancelled. Local edits are preserved in the initial-changes patch; no PR was created.
3. Inventory features and settings against v10.3.0, including disabled, context-menu and server-dependent actions.
4. Resolve private parser/authentication access and decide how cloud services will be provided.
5. Adapt build checks and release packaging to the fork, with explicit dependency availability checks, fork-owned release URLs and credentials, distinct installer identity, storage, protocol registration and branding. Preserve upstream attribution and license files.
6. Implement and visually verify the navigation redesign in stages. Check keyboard navigation, scaling, translations, first-run setup, server switching and saved preferences.
7. Test the installer and fork-only updater on a clean Windows account before publishing a release.

## Current verification

The desktop compiles with the public pairing adapters. Offline tests pass, but authentication has not been verified with a real Steam account. Genetics Lab builds, and the alert-service tests run without sending Discord messages. Full visual verification, live pairing and an installer remain outstanding.

Latest full desktop rerun aborted after 82 passing tests when the WPF test host crashed in HwndSubclass.SubclassWndProc. The six focused pairing tests pass; full regression verification after pairing integration remains incomplete.

