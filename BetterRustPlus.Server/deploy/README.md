# Easy VPS setup

This runs the BetterRustPlus listener and optional Discord bot while your PC is asleep or off. It does not run the Windows desktop app. This is a development deployment; real Rust+ pairing and Discord delivery still need acceptance testing.

## 1. Prepare the VPS

Use a Linux VPS with Python 3, Git, Docker Engine and the Docker Compose plugin. Ubuntu 24.04 LTS is a supported Docker host. If your host offers a Docker-ready Ubuntu image, it saves the Docker installation step. Otherwise follow [Docker's official Ubuntu instructions](https://docs.docker.com/engine/install/ubuntu/), including its verification step. Docker must start at boot.

The listener does not need a GPU or the desktop application's assets. The initial container build uses more resources than simply running the listener. Resource requirements have not been benchmarked; do not assume an arbitrary smallest VPS plan can build or run it reliably.

Run the setup as the account that can access Docker. If Docker reports a permissions error, use your host's documented Docker-access setup; the wizard will not change user permissions or install system software.

## 2. Download just the listener and run setup

In your VPS terminal:

```sh
git clone --depth 1 --filter=blob:none --sparse --branch betterrustplus/navigation https://github.com/WHOMEANSWHO/BetterRustPlus.git
cd BetterRustPlus
git sparse-checkout set BetterRustPlus.Server packages-local
python3 BetterRustPlus.Server/deploy/setup.py
```

This uses the development branch, not a released version. The sparse checkout retains the root NuGet configuration needed for the container build, while leaving out the Windows application.

The wizard asks for:

- A short name identifying your group's paired server.
- Optional Rust+ pairing values and Smart Alarm entity IDs.
- An optional Discord webhook or your own Discord bot's token, server ID and channel ID.

Private values are entered through hidden prompts. The ingest key is generated automatically, not pasted into the terminal output. The wizard writes a private `.env` beside the Compose file and refuses to replace an existing one. Keep that file on the VPS and out of GitHub/chat.

The initial configuration uses **test mode**: event alerts are simulated, and everyone mentions are off. A separately enabled Discord bot still connects and answers commands in test mode. Leave both optional integrations disabled if you only want a local service test.

The wizard asks before building and starting the container, then checks service health. It distinguishes a running service from a connected Rust+ source. Without valid pairing, a running container cannot detect game events.

## 3. Check or restart it later

From the repository folder:

```sh
python3 BetterRustPlus.Server/deploy/setup.py --status
python3 BetterRustPlus.Server/deploy/setup.py --check
python3 BetterRustPlus.Server/deploy/setup.py --start
```

`--status` checks the local service. `--check` checks deployment prerequisites. `--start` uses your existing private configuration and builds/starts the service without regenerating keys. To create configuration without starting anything, use `--configure-only`.

For recent service messages:

```sh
cd BetterRustPlus.Server/deploy
docker compose logs --tail=50 alerts
```

The container restarts after process failures or Docker restarts using `unless-stopped`. Its API port is only available on the VPS itself; the paired source and bot make outgoing connections, so this setup does not require a domain or public web port.

## 4. Enable real alerts after testing

Use the [service guide](../README.md#add-the-optional-command-bot-to-your-discord) to create/invite your own bot, or configure a channel webhook. Rust+ values must come from your own valid pairing; the wizard does not perform a Steam login on the VPS or export desktop credentials automatically.

After checking the paired source, rules and destination, edit `BetterRustPlus.Server/deploy/.env` privately and set `Alerts__DryRun=false`. To ping everyone for raids, change only `Alerts__Rules__1__MentionEveryone=true` and grant the sender the relevant Discord permission. Recreate the service with `--start` to apply changes. This enables real messages.

Discord can deliver the alerts to your phone according to its notification settings. Alexa is not configured by this wizard. Universal airdrop/Deep Sea detection, durable delivery and exact unobserved death positions are not implemented; see the [feature checklist](../../docs/BETTERRUSTPLUS-FEATURES.md).

The source code and private `.env` remain separate. Before updates, keep a protected backup of `.env`. A restart resets this development version's in-memory cooldown/retry history.
