# Easy VPS setup without Docker

This installs the BetterRustPlus listener and optional Discord bot directly as a Linux background service. It runs while your PC is asleep or off, starts at VPS boot and restarts after a crash. It does not run the Windows desktop application.

## 1. Prepare the VPS

Use a Linux VPS with systemd, such as Ubuntu 24.04 LTS, on x64 or ARM64. In your SSH terminal on Ubuntu 24.04:

```sh
sudo apt-get update
sudo apt-get install -y git python3 dotnet-sdk-8.0
```

The .NET SDK builds the listener once during installation. The installed application includes its runtime. See [Microsoft's Ubuntu instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install) for other distributions or missing packages. No Docker, GPU, domain or public API port is required. Resource use has not been benchmarked; building needs more resources than running the listener.

## 2. Download only the listener

```sh
git clone --depth 1 --filter=blob:none --sparse --branch betterrustplus/navigation https://github.com/WHOMEANSWHO/BetterRustPlus.git
cd BetterRustPlus
git sparse-checkout set BetterRustPlus.Server packages-local
```

This is the development branch, not a released version. The sparse checkout retains root build configuration while leaving out the Windows application.

## 3. Configure and install

```sh
python3 BetterRustPlus.Server/deploy/setup.py --configure-only
sudo python3 BetterRustPlus.Server/deploy/setup.py --install
```

The wizard asks for a short server name, optional Rust+ pairing values/Smart Alarm IDs, and optional Discord webhook or bot details. Private values use hidden prompts; an ingest key is generated without displaying it. You can leave the optional integrations disabled to test the service first. This does not detect game events until a valid source is configured. Enabling the command bot connects it to Discord even while event alerts remain in test mode.

Installation builds the native application, installs it under `/opt/betterrustplus`, copies the private settings to `/etc/betterrustplus/listener.env`, and installs `betterrustplus.service`. The service runs with a restricted temporary user rather than root. Its API stays on `127.0.0.1:5088`. Settings and unknown existing service files are preserved; an update never silently overwrites installed credentials.

The installer checks startup before reporting success. If an updated release fails, it reselects the previous release. This is a startup rollback, not a guarantee that live pairing or Discord delivery works. The initial configuration uses test mode and disables everyone mentions.

## 4. Check, restart or stop

```sh
python3 BetterRustPlus.Server/deploy/setup.py --status
sudo systemctl status betterrustplus --no-pager
sudo journalctl -u betterrustplus -n 50 --no-pager
sudo systemctl restart betterrustplus
sudo systemctl stop betterrustplus
```

Use `sudo systemctl disable --now betterrustplus` if you also want to stop automatic startup. Check prerequisites with `python3 BetterRustPlus.Server/deploy/setup.py --check`.

## 5. Enable real alerts after testing

Use the [Discord setup instructions](../README.md#add-the-optional-command-bot-to-your-discord) to create/invite your own bot or configure a webhook. Rust+ credentials must come from your own valid pairing; headless Steam login and automatic desktop credential export are not implemented.

To change the installed settings:

```sh
sudo nano /etc/betterrustplus/listener.env
sudo systemctl restart betterrustplus
```

After verifying the source, destination and rules, set `Alerts__DryRun=false` for real event messages. For raid pings, enable only `Alerts__Rules__1__MentionEveryone=true` and give the Discord sender the required permission. Keep the installed settings file owned by root with mode 600. Editing the checkout's `.env` after installation does not change the installed settings.

Discord can deliver alerts to your phone according to its notification settings. Alexa is not configured here. Airdrop/Deep Sea detection still requires a verified source; exact unobserved death locations and durable delivery are not implemented. See the [feature checklist](../../docs/BETTERRUSTPLUS-FEATURES.md).

## Updates

Keep a protected backup of `/etc/betterrustplus/listener.env`. Pull the intended reviewed branch as your normal user, then install the new release:

```sh
git pull --ff-only
sudo python3 BetterRustPlus.Server/deploy/setup.py --install
```

The installer builds before changing the selected release and preserves installed settings. Old releases are retained for recovery; they are not automatically deleted. Runtime cooldown/retry history is currently memory-only and resets on restart.
