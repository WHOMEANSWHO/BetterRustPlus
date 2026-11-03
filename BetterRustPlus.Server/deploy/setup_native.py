#!/usr/bin/env python3
"""Native Linux/systemd deployment. No Docker, shell execution or package installation."""

import argparse
import os
from pathlib import Path
import platform
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import uuid

import setup_docker as config

DEPLOY_DIR = Path(__file__).resolve().parent
REPO_DIR = DEPLOY_DIR.parents[1]
INSTALL_DIR = Path("/opt/betterrustplus")
CONFIG_DIR = Path("/etc/betterrustplus")
UNIT_PATH = Path("/etc/systemd/system/betterrustplus.service")
SERVICE = "betterrustplus.service"


def run(command, timeout=30, cwd=None):
    """Never include process output in errors: it can contain private configuration."""
    try:
        result = subprocess.run(command, cwd=cwd, capture_output=True, text=True,
                                timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise RuntimeError("A required operation did not finish. Private settings were preserved; inspect the VPS locally.") from None
    if result.returncode != 0:
        raise RuntimeError("A required operation failed. Private settings were preserved; inspect the VPS locally.")
    return result.stdout


def runtime_id():
    machine = platform.machine().lower()
    if machine in ("x86_64", "amd64"):
        return "linux-x64"
    if machine in ("aarch64", "arm64"):
        return "linux-arm64"
    raise RuntimeError("This installer supports x64 and ARM64 Linux VPS hosts.")


def prerequisites(build=True):
    if platform.system() != "Linux":
        return "Native deployment requires a Linux VPS using systemd. Configuration can be prepared with --configure-only."
    try:
        runtime_id()
    except RuntimeError as error:
        return str(error)
    if shutil.which("systemctl") is None or not Path("/run/systemd/system").is_dir():
        return "A running systemd host is required. This installer does not run inside a container."
    if build:
        if shutil.which("dotnet") is None:
            return "Install the .NET 8 SDK using the VPS setup guide before installation."
        try:
            sdks = run(["dotnet", "--list-sdks"])
        except RuntimeError:
            return "The .NET SDK did not respond. Check its installation on the VPS."
        if not any(line.startswith("8.") for line in sdks.splitlines()):
            return "The .NET 8 SDK is required for the current listener. Install it using the VPS setup guide."
    return None


def require_root():
    if not hasattr(os, "geteuid") or os.geteuid() != 0:
        raise RuntimeError("Installation and service control need sudo. Prepare settings normally, then run sudo python3 setup.py --install or --start.")


def regular_file(path, private=False):
    path = Path(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode):
        raise RuntimeError("A required settings or installation file is not a regular file. Symbolic links are not used.")
    if private and info.st_mode & 0o077:
        raise RuntimeError("Private settings must be owner-only. Run chmod 600 on the settings file before installation.")
    if private and hasattr(os, "geteuid") and info.st_uid not in (0, os.geteuid(), int(os.environ.get("SUDO_UID", os.geteuid()))):
        raise RuntimeError("Private settings are owned by an unexpected user. Check ownership locally.")
    return info


def private_bytes(path):
    """Validate the actual opened file, not a path that may change before reading."""
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_mode & 0o077:
            raise RuntimeError("Private settings must be a regular owner-only file. Check the settings permissions locally.")
        expected_uid = int(os.environ.get("SUDO_UID", getattr(os, "geteuid", lambda: 0)()))
        if hasattr(os, "geteuid") and info.st_uid not in (0, os.geteuid(), expected_uid):
            raise RuntimeError("Private settings are owned by an unexpected user.")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            body = stream.read(65537)
        if len(body) > 65536:
            raise RuntimeError("Private settings are unexpectedly large. Existing settings were preserved.")
        return body
    finally:
        os.close(descriptor)


def safe_directory(path):
    """Refuse symlinked deployment directories before any privileged write."""
    path = Path(path)
    for component in (path, *path.parents):
        if component.is_symlink():
            raise RuntimeError("A deployment directory is a symbolic link. Choose the documented fixed installation paths.")
        if component.exists() and not component.is_dir():
            raise RuntimeError("A deployment directory is occupied by a file.")
        if os.name == "posix" and component.exists():
            info = component.stat()
            if info.st_uid != 0 or info.st_mode & 0o022:
                raise RuntimeError("System deployment directories must be root-owned and not writable by other users.")


def write_exclusive(path, content, mode):
    """Atomically create a fixed installation file without replacing an existing path."""
    path = Path(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".betterrustplus-", delete=False) as output:
            temporary = Path(output.name)
            os.chmod(temporary, mode)
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.link(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def check_existing_installation(template):
    for path in (INSTALL_DIR, INSTALL_DIR / "releases", CONFIG_DIR, UNIT_PATH.parent):
        safe_directory(path)
    if UNIT_PATH.exists() or UNIT_PATH.is_symlink():
        info = regular_file(UNIT_PATH)
        if os.name == "posix" and (info.st_uid != 0 or info.st_mode & 0o022):
            raise RuntimeError("The existing service definition must be root-owned and protected from other users.")
        if UNIT_PATH.read_bytes() != template:
            raise RuntimeError("An existing service definition differs from the supplied template. It was preserved; review it before updating.")
    settings = CONFIG_DIR / "listener.env"
    if settings.exists() or settings.is_symlink():
        regular_file(settings, private=True)
        if hasattr(os, "geteuid") and settings.stat().st_uid != 0:
            raise RuntimeError("Installed private settings must be owned by root. Existing settings were preserved.")
    current = INSTALL_DIR / "current"
    if current.exists() or current.is_symlink():
        if not current.is_symlink():
            raise RuntimeError("The current release path is not an installer-managed symbolic link. It was preserved.")
        resolved = current.resolve()
        if resolved.parent != (INSTALL_DIR / "releases").resolve() or not resolved.is_dir():
            raise RuntimeError("The current release points outside the managed release directory or is unavailable.")


def build_release(stage):
    """Copy only public build inputs; elevated builds never modify checkout obj/bin."""
    source = stage / "source"
    service = source / "BetterRustPlus.Server"
    core = service / "Core"
    core.mkdir(parents=True)
    names = ["BetterRustPlus.Server.csproj", "Program.cs", "RustSourceWorker.cs", "DiscordBotWorker.cs", "appsettings.json"]
    inputs = [(REPO_DIR / "nuget.config", source / "nuget.config")]
    inputs += [(REPO_DIR / "BetterRustPlus.Server" / name, service / name) for name in names]
    inputs += [(path, core / path.name) for path in (REPO_DIR / "BetterRustPlus.Server" / "Core").iterdir()
               if path.suffix in (".cs", ".csproj")]
    packages = source / "packages-local"
    packages.mkdir()
    inputs += [(path, packages / path.name) for path in (REPO_DIR / "packages-local").glob("*.nupkg")]
    for original, destination in inputs:
        regular_file(original)
        shutil.copyfile(original, destination)
    output = stage / "publish"
    run(["dotnet", "publish", str(service / "BetterRustPlus.Server.csproj"), "-c", "Release",
         "-r", runtime_id(), "--self-contained", "true", "-p:UseSharedCompilation=false", "-o", str(output)],
        timeout=900, cwd=source)
    for required in ("BetterRustPlus.Server", "BetterRustPlus.Server.dll", "libcoreclr.so"):
        regular_file(output / required)
    return output


def wait_for_health(expected_pid, health_reader=config.read_health, sleep=time.sleep, monotonic=time.monotonic):
    deadline = monotonic() + 90
    while monotonic() < deadline:
        try:
            health = health_reader()
            if isinstance(health, dict) and health.get("status") == "running" and health.get("processId") == expected_pid:
                return health
        except (OSError, ValueError, urllib.error.URLError):
            pass
        sleep(2)
    raise RuntimeError("The service did not become healthy within 90 seconds. Check sudo systemctl status betterrustplus and its journal locally.")


def start_service():
    require_root()
    error = prerequisites(build=False)
    if error:
        raise RuntimeError(error)
    template_path = DEPLOY_DIR / "betterrustplus.service"
    regular_file(template_path)
    check_existing_installation(template_path.read_bytes())
    regular_file(UNIT_PATH)
    regular_file(CONFIG_DIR / "listener.env", private=True)
    regular_file(INSTALL_DIR / "current" / "BetterRustPlus.Server")
    run(["systemctl", "daemon-reload"])
    run(["systemctl", "enable", SERVICE])
    run(["systemctl", "restart", SERVICE], timeout=45)
    run(["systemctl", "is-active", "--quiet", SERVICE])
    try:
        pid = int(run(["systemctl", "show", SERVICE, "--property=MainPID", "--value"]).strip())
    except ValueError:
        raise RuntimeError("The service did not report a valid process. Inspect its systemd journal locally.") from None
    if pid <= 0:
        raise RuntimeError("The service did not start a process. Inspect its systemd journal locally.")
    return wait_for_health(pid)


def install_service():
    require_root()
    error = prerequisites()
    if error:
        raise RuntimeError(error)
    template_path = DEPLOY_DIR / "betterrustplus.service"
    regular_file(template_path)
    template = template_path.read_bytes()
    check_existing_installation(template)
    settings = CONFIG_DIR / "listener.env"
    local_settings = DEPLOY_DIR / ".env"
    prepared_settings = private_bytes(local_settings) if not settings.exists() else None
    previous = os.readlink(INSTALL_DIR / "current") if (INSTALL_DIR / "current").is_symlink() else None
    # Build and validate completely before changing system installation paths.
    with tempfile.TemporaryDirectory(prefix="betterrustplus-build-") as scratch:
        output = build_release(Path(scratch))
        check_existing_installation(template)
        (INSTALL_DIR / "releases").mkdir(parents=True, exist_ok=True)
        os.chmod(INSTALL_DIR, 0o755)
        os.chmod(INSTALL_DIR / "releases", 0o755)
        CONFIG_DIR.mkdir(parents=True, exist_ok=True, mode=0o700)
        UNIT_PATH.parent.mkdir(parents=True, exist_ok=True)
        if not settings.exists():
            latest_settings = private_bytes(local_settings)
            if latest_settings != prepared_settings:
                raise RuntimeError("Local private settings changed during the build. Existing settings were preserved; review and retry.")
            write_exclusive(settings, latest_settings, 0o600)
        if not UNIT_PATH.exists():
            write_exclusive(UNIT_PATH, template, 0o644)
        release = INSTALL_DIR / "releases" / (time.strftime("%Y%m%d%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:12])
        shutil.copytree(output, release)
        for path in release.rglob("*"):
            if path.is_symlink():
                raise RuntimeError("Published output unexpectedly contains symbolic links. The previous release remains selected.")
            os.chmod(path, 0o755 if path.is_dir() or path.name == "BetterRustPlus.Server" else 0o644)
        os.chmod(release, 0o755)
        link = INSTALL_DIR / (".current-" + uuid.uuid4().hex)
        os.symlink(str(release), link)
        os.replace(link, INSTALL_DIR / "current")
    try:
        return start_service()
    except (RuntimeError, OSError):
        if previous is not None:
            link = INSTALL_DIR / (".rollback-" + uuid.uuid4().hex)
            os.symlink(previous, link)
            os.replace(link, INSTALL_DIR / "current")
            try:
                run(["systemctl", "restart", SERVICE], timeout=45)
            except RuntimeError:
                pass
            raise RuntimeError("The new release failed its startup check. The previous release was reselected; inspect its status and journal locally.") from None
        try:
            run(["systemctl", "stop", SERVICE], timeout=45)
        except RuntimeError:
            pass
        try:
            run(["systemctl", "disable", SERVICE])
        except RuntimeError:
            pass
        raise RuntimeError("The first release failed its startup check. Stop/disable were requested; verify its status locally. Private settings and release files were preserved.") from None


def show_health(health):
    if not isinstance(health, dict) or health.get("status") != "running":
        raise RuntimeError("The service is not reporting ready. Check systemctl status betterrustplus.")
    print("BetterRustPlus is running privately at http://127.0.0.1:5088.")
    print("Rust+ source: " + ("connected" if health.get("gameSourceConnected") is True else "not connected; check pairing settings if enabled"))
    mode = health.get("mode")
    print("Alerts: " + ("test mode; no event alerts sent" if mode == "dry-run" else
                        "live delivery enabled in installed configuration" if mode == "discord-delivery-enabled" else "mode unavailable"))


def main(argv=None):
    parser = argparse.ArgumentParser(description="Guided native Linux VPS setup using systemd. No Docker or package installation.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="Check Linux/systemd and .NET 8 SDK without changing settings.")
    mode.add_argument("--configure-only", action="store_true", help="Prepare private test-mode settings without installing or starting anything.")
    mode.add_argument("--install", action="store_true", help="Build/install/update the native service with sudo; preserves installed settings.")
    mode.add_argument("--start", action="store_true", help="Restart/enable the installed service with sudo; may send alerts if configured live.")
    mode.add_argument("--status", action="store_true", help="Check local service health without changing anything.")
    args = parser.parse_args(argv)
    try:
        if args.status:
            show_health(config.read_health())
            return 0
        if args.check:
            error = prerequisites()
            print(error or "Linux, systemd and .NET 8 SDK are ready. Nothing was changed.")
            return 1 if error else 0
        if args.install or args.start:
            show_health(install_service() if args.install else start_service())
            return 0
        path = DEPLOY_DIR / ".env"
        if path.exists() or path.is_symlink():
            regular_file(path, private=True)
            print("Existing private settings were preserved.")
        else:
            values = config.collect_config()
            config.write_config(path, values)
            print("Private settings prepared in .env. Alerts start in test mode, with everyone mentions off.")
        if args.configure_only:
            return 0
        print("Installation needs sudo, Linux/systemd and the .NET 8 SDK. No Rust+/Discord connection has been attempted.")
        if config.yes("Install and start now? Enabled accounts may connect; installed live settings may send alerts"):
            if not hasattr(os, "geteuid") or os.geteuid() != 0:
                print("Run: sudo python3 " + str(DEPLOY_DIR / "setup.py") + " --install")
                return 0
            show_health(install_service())
        return 0
    except FileExistsError:
        print("An existing file was preserved. Review the local or installed configuration before continuing.")
        return 2
    except (RuntimeError, OSError, ValueError, urllib.error.URLError) as error:
        print(str(error) if isinstance(error, RuntimeError) else "Setup could not complete. Private settings were preserved; check permissions and service status locally.")
        return 1
    except (EOFError, KeyboardInterrupt):
        print("\nSetup cancelled. Completed private settings were preserved.")
        return 130


if __name__ == "__main__":
    sys.exit(main())
