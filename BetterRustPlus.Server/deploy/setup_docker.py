#!/usr/bin/env python3
"""Legacy Docker setup and shared private configuration helpers. Native setup is setup.py."""

import argparse
import getpass
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import warnings


DEPLOY_DIR = Path(__file__).resolve().parent
HEALTH_URL = "http://127.0.0.1:5088/health"


def private_value(value):
    if not value or len(value) > 4096 or any(ord(c) < 32 or ord(c) > 126 or c in "'\\" for c in value):
        raise ValueError("Use a nonempty value without quotes, backslashes or control characters.")
    return value


def server_id(value):
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", value):
        raise ValueError("Use 1–128 letters, numbers, dots, underscores or hyphens.")
    return value


def integer(value, minimum=1, maximum=2**64 - 1):
    if not re.fullmatch(r"-?[0-9]+", value):
        raise ValueError("Enter a whole number.")
    number = int(value)
    if not minimum <= number <= maximum:
        raise ValueError("The number is outside the supported range.")
    return str(number)


def host(value):
    try:
        ipaddress.ip_address(value)
        return value
    except ValueError:
        if len(value) <= 253 and all(re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label)
                                     for label in value.split(".")):
            return value
    raise ValueError("Enter the paired server's hostname or IP address, without http:// or a port.")


def relay_id(value):
    try:
        return str(uuid.UUID(value))
    except ValueError:
        raise ValueError("Enter the paired companion server UUID.") from None


def webhook(value):
    private_value(value)
    try:
        parsed = urllib.parse.urlsplit(value)
        parts = parsed.path.split("/")
        valid = (parsed.scheme == "https" and parsed.hostname in ("discord.com", "discordapp.com")
                 and parsed.port in (None, 443) and parsed.username is None and parsed.password is None
                 and not parsed.fragment and not parsed.query and len(parts) == 5
                 and parts[1:3] == ["api", "webhooks"] and parts[3].isdigit() and bool(parts[4]))
    except ValueError:
        valid = False
    if not valid:
        raise ValueError("Use an official HTTPS Discord webhook URL, without extra query parameters.")
    return value


def alarm_ids(value):
    if not value.strip():
        return []
    values = list(dict.fromkeys(integer(part.strip(), 1, 2**32 - 1) for part in value.split(",")))
    if len(values) > 32:
        raise ValueError("At most 32 paired Smart Alarms are supported.")
    return values


def quote_env(value):
    # Compose single quotes suppress interpolation of $ and preserve # literally.
    # Values with ambiguous quote/backslash syntax are rejected rather than escaped.
    return "'" + private_value(str(value)) + "'"


def base_config(group_id):
    values = {
        "BETTERRUSTPLUS_INGEST_KEY": secrets.token_hex(32),
        "Alerts__DryRun": "true",
        "Alerts__AllowedServerIds__0": server_id(group_id),
        "RustSource__Enabled": "false",
        "DiscordBot__Enabled": "false",
    }
    for index, (name, kinds, cooldown) in enumerate([
        ("team-deaths", ["teammate-death"], "0"),
        ("raid-alarm", ["raid"], "60"),
        ("world-events", ["cargo", "airdrop", "deepsea"], "60"),
    ]):
        prefix = "Alerts__Rules__" + str(index) + "__"
        values.update({prefix + "Id": name, prefix + "CooldownSeconds": cooldown,
                       prefix + "Target": "primary", prefix + "MentionEveryone": "false"})
        for kind_index, kind in enumerate(kinds):
            values[prefix + "Kinds__" + str(kind_index)] = kind
    return values


def write_config(path, values):
    """Atomically install a complete owner-only file, never replacing any existing path."""
    path = Path(path)
    body = "# Generated locally by BetterRustPlus setup. Keep private.\n"
    body += "# Alerts stay in dry-run until you deliberately enable live delivery.\n"
    for key, value in values.items():
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", key):
            raise ValueError("Unsupported configuration key.")
        body += key + "=" + quote_env(value) + "\n"
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=path.parent,
                                         prefix=".betterrustplus-", delete=False) as output:
            temporary = Path(output.name)
            os.chmod(temporary, 0o600)
            output.write(body)
            output.flush()
            os.fsync(output.fileno())
        # Same-directory hard link is atomic and fails if the destination exists,
        # including symlinks. A failed/racing setup never overwrites private settings.
        os.link(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def prerequisites(directory=DEPLOY_DIR):
    if shutil.which("docker") is None:
        return "Docker Engine is missing. Install Docker and its Compose plugin using the setup guide."
    for command in (["docker", "compose", "version"], ["docker", "info"]):
        try:
            result = subprocess.run(command, cwd=directory, capture_output=True, text=True, timeout=20, check=False)
        except (OSError, subprocess.TimeoutExpired):
            return "Docker did not respond. Check that Docker is installed, running and accessible to your user."
        if result.returncode != 0:
            return "Docker or its Compose plugin is unavailable. Check the daemon and your user's access."
    return None


class NoHealthRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        raise urllib.error.HTTPError(request.full_url, code, "Health redirects are disabled.", headers, response)


def read_health():
    # Ignore environment proxy settings: this request must stay on the VPS loopback.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoHealthRedirects())
    with opener.open(HEALTH_URL, timeout=3) as response:
        data = response.read(65537)
        if len(data) > 65536:
            raise ValueError("Health response is too large.")
        health = json.loads(data)
        if not isinstance(health, dict):
            raise ValueError("Invalid health response.")
        return health


def start_service(directory=DEPLOY_DIR, health_reader=read_health, sleep=time.sleep, monotonic=time.monotonic):
    error = prerequisites(directory)
    if error:
        raise RuntimeError(error)
    try:
        result = subprocess.run(["docker", "compose", "up", "-d", "--build"], cwd=directory,
                                capture_output=True, text=True, timeout=900, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise RuntimeError("Container startup did not finish. Your private settings are preserved; inspect Docker locally.") from None
    if result.returncode != 0:
        # Docker errors can include configuration values. Do not echo captured output.
        raise RuntimeError("Container startup failed. Your settings are preserved; run docker compose logs locally.")
    deadline = monotonic() + 90
    while monotonic() < deadline:
        try:
            health = health_reader()
            if isinstance(health, dict) and health.get("status") == "running":
                return health
        except (OSError, ValueError, urllib.error.URLError):
            pass
        sleep(2)
    raise RuntimeError("The container started but health did not become ready within 90 seconds. Inspect docker compose ps and logs locally.")


def prompt(label, validate=private_value, default=None, secret=False):
    while True:
        suffix = " [" + default + "]" if default is not None else ""
        if secret:
            # getpass otherwise falls back to an echoing stdin prompt on some terminals.
            # Abort before fallback input is read, rather than risk exposing credentials.
            with warnings.catch_warnings():
                warnings.simplefilter("error", getpass.GetPassWarning)
                try:
                    value = getpass.getpass(label + ": ").strip()
                except getpass.GetPassWarning:
                    raise RuntimeError("A private password prompt is unavailable. Run setup in an interactive SSH terminal; no secret was read.") from None
        else:
            value = input(label + suffix + ": ").strip()
        if not value and default is not None:
            value = default
        try:
            return validate(value)
        except ValueError as error:
            print(error)


def yes(label, default=False):
    return prompt(label, lambda value: value.lower() if value.lower() in ("y", "n", "yes", "no")
                  else (_ for _ in ()).throw(ValueError("Enter yes or no.")), "no" if not default else "yes") in ("yes", "y")


def collect_config():
    print("BetterRustPlus VPS setup\nSecrets are hidden. Alerts start in dry-run, with everyone mentions OFF.")
    group_id = prompt("Short name for your paired server", server_id, "our-server")
    values = base_config(group_id)
    if yes("Do you already have this server's private Rust+ pairing values?"):
        values.update({"RustSource__Enabled": "true", "RustSource__ServerId": group_id,
                       "RustSource__Host": prompt("Paired Rust server hostname or IP", host),
                       "RustSource__Port": prompt("Companion port", lambda v: integer(v, 1, 65535), "28082"),
                       "RustSource__SteamId": prompt("Your Steam ID64", integer),
                       "RustSource__PlayerToken": prompt("Private paired player token", lambda v: integer(v, -(2**31), 2**31 - 1), secret=True),
                       "RustSource__PollSeconds": "15"})
        if yes("Does your pairing use the companion relay?"):
            values["RustSource__CompanionServerId"] = prompt("Companion server UUID", relay_id)
            values["RustSource__AuthToken"] = prompt("Private companion auth token", secret=True)
        for index, value in enumerate(prompt("Paired Smart Alarm IDs, comma-separated (blank for none)", alarm_ids, "")):
            values["RustSource__AlarmEntityIds__" + str(index)] = value
    else:
        print("Rust+ stays disabled. The service can accept supplied events, but will not detect game events yet.")

    route = prompt("Discord route: none, webhook, or bot", lambda v: v.lower() if v.lower() in ("none", "webhook", "bot")
                   else (_ for _ in ()).throw(ValueError("Choose none, webhook, or bot.")), "none")
    if route == "webhook":
        values["Alerts__Targets__primary__WebhookUrl"] = prompt("Private Discord webhook URL", webhook, secret=True)
    elif route == "bot":
        print("Enabling a bot connects it to Discord and registers read-only commands even while alert delivery is in dry-run.")
        values["DiscordBot__Enabled"] = "true"
        values["DiscordBot__Token"] = prompt("Private Discord bot token", secret=True)
        values["DiscordBot__GuildId"] = prompt("Discord server ID", integer)
        channel = prompt("Discord alert/command text-channel ID", integer)
        values["DiscordBot__AllowedChannelIds__0"] = channel
        values["Alerts__Targets__primary__DiscordChannelId"] = channel
    return values


def show_health(health):
    if health.get("status") != "running":
        raise RuntimeError("The service is not reporting ready. Inspect docker compose ps and logs locally.")
    print("Service is running at http://127.0.0.1:5088 (on this VPS only).")
    print("Rust+ source: " + ("connected" if health.get("gameSourceConnected") is True else "not connected; check pairing/source settings if enabled"))
    print("Alert delivery: " + ("dry-run; no event alerts sent" if health.get("mode") == "dry-run" else "live delivery is enabled in the service configuration"))


def main(argv=None):
    parser = argparse.ArgumentParser(description="Guided private setup for a small Linux VPS. No packages are installed or public ports opened.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="Check Docker/Compose and daemon access without writing settings or starting anything.")
    mode.add_argument("--configure-only", action="store_true", help="Write private test-mode settings without offering to start containers.")
    mode.add_argument("--start", action="store_true", help="Start/recreate from existing private settings. Enabled accounts may connect and live alerts may send.")
    mode.add_argument("--status", action="store_true", help="Check the local service without changing settings or starting anything.")
    args = parser.parse_args(argv)
    if args.check:
        error = prerequisites()
        print(error or "Docker Engine and Compose are ready. No settings were changed.")
        return 1 if error else 0
    path = DEPLOY_DIR / ".env"
    if args.start or args.status:
        try:
            if args.start and (not path.is_file() or path.is_symlink()):
                print("No regular .env settings file was found. Run setup first; symbolic links are not used.")
                return 2
            health = start_service() if args.start else read_health()
            show_health(health)
            return 0
        except (OSError, ValueError, urllib.error.URLError, RuntimeError) as error:
            print(str(error) if isinstance(error, RuntimeError) else "The local service is unavailable. Check docker compose ps and logs locally.")
            return 1
    if path.exists() or path.is_symlink():
        print("Existing .env settings were preserved. Edit them locally; use python3 setup.py --start when ready, or --status to check it.")
        return 2
    if not args.configure_only:
        error = prerequisites()
        if error:
            print(error + "\nUse --configure-only if you want to prepare settings before Docker is installed.")
            return 1
    try:
        values = collect_config()
        write_config(path, values)
        print("Private .env created with owner-only access. The generated ingest key was saved without displaying it.")
        print("Alert mode: dry-run. Everyone mentions: off. Service access: VPS loopback only.")
        print("Review .env privately. No Rust+ or Discord connection has been attempted by this setup yet.")
        if args.configure_only or not yes("Start the configured service now? This may connect enabled Rust+/Discord accounts"):
            print("Prepared only. Start later from this folder with: docker compose up -d --build")
            return 0
        print("Building and starting the service. The first build can take several minutes...")
        health = start_service()
        show_health(health)
        print("Alerts remain in dry-run. Follow the guide to verify observations before enabling real delivery.")
        return 0
    except FileExistsError:
        print("Another setup created .env. Existing settings were preserved.")
        return 2
    except (OSError, RuntimeError) as error:
        # Do not print arbitrary OS errors that may include sensitive file contents.
        print(str(error) if isinstance(error, RuntimeError) else "Could not save or access private settings. Check folder permissions.")
        return 1
    except (EOFError, KeyboardInterrupt):
        print("\nSetup cancelled. Any completed private settings file is preserved.")
        return 130


if __name__ == "__main__":
    sys.exit(main())
