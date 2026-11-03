import contextlib
import importlib.util
import io
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "setup_native.py"
sys.path.insert(0, str(SCRIPT.parent))
SPEC = importlib.util.spec_from_file_location("native_setup", SCRIPT)
native = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(native)


class NativeSetupTests(unittest.TestCase):
    def test_runtime_ids_are_explicit_and_unsupported_architectures_fail(self):
        for machine, expected in (("x86_64", "linux-x64"), ("AMD64", "linux-x64"), ("aarch64", "linux-arm64")):
            with patch.object(native.platform, "machine", return_value=machine):
                self.assertEqual(expected, native.runtime_id())
        with patch.object(native.platform, "machine", return_value="mips"):
            with self.assertRaises(RuntimeError):
                native.runtime_id()

    def test_non_linux_check_never_runs_commands(self):
        with patch.object(native.platform, "system", return_value="Windows"), patch.object(native, "run") as run:
            self.assertIn("Linux", native.prerequisites())
            run.assert_not_called()

    def test_sdk_check_requires_target_sdk(self):
        with patch.object(native.platform, "system", return_value="Linux"), \
                patch.object(native.platform, "machine", return_value="x86_64"), \
                patch.object(native.shutil, "which", return_value="installed"), \
                patch.object(native.Path, "is_dir", return_value=True), patch.object(native, "run", return_value="10.0.1 [/sdk]"):
            self.assertIn(".NET 8", native.prerequisites())
        with patch.object(native.platform, "system", return_value="Linux"), \
                patch.object(native.platform, "machine", return_value="x86_64"), \
                patch.object(native.shutil, "which", return_value="installed"), \
                patch.object(native.Path, "is_dir", return_value=True), patch.object(native, "run", return_value="8.0.408 [/sdk]"):
            self.assertIsNone(native.prerequisites())

    def test_service_only_check_never_requires_sdk(self):
        with patch.object(native.platform, "system", return_value="Linux"), \
                patch.object(native.platform, "machine", return_value="x86_64"), \
                patch.object(native.shutil, "which", return_value="installed"), \
                patch.object(native.Path, "is_dir", return_value=True), patch.object(native, "run") as run:
            self.assertIsNone(native.prerequisites(build=False))
            run.assert_not_called()

    def test_install_without_root_never_builds_or_writes(self):
        with patch.object(native.os, "geteuid", return_value=1000, create=True), \
                patch.object(native, "build_release") as build, patch.object(native, "write_exclusive") as write:
            with self.assertRaisesRegex(RuntimeError, "sudo"):
                native.install_service()
            build.assert_not_called()
            write.assert_not_called()

    def test_run_uses_argument_array_and_redacts_process_errors(self):
        with patch.object(native.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, stdout="PRIVATE", stderr="PRIVATE")) as run:
            with self.assertRaises(RuntimeError) as error:
                native.run(["systemctl", "restart", native.SERVICE])
            self.assertNotIn("PRIVATE", str(error.exception))
            self.assertIsInstance(run.call_args.args[0], list)
            self.assertNotIn("shell", run.call_args.kwargs)

    def test_status_is_read_only(self):
        with patch.object(native.config, "read_health", return_value={"status": "running", "mode": "dry-run"}), \
                patch.object(native, "install_service") as install, patch.object(native, "start_service") as start, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, native.main(["--status"]))
            install.assert_not_called()
            start.assert_not_called()
            self.assertIn("not connected", output.getvalue())
            self.assertNotIn("Docker", output.getvalue())

    def test_configure_only_creates_test_settings_without_service_commands(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, "DEPLOY_DIR", Path(directory)), \
                patch.object(native.config, "collect_config", return_value=native.config.base_config("our-server")), \
                patch.object(native, "install_service") as install, patch.object(native, "run") as run, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, native.main(["--configure-only"]))
            body = (Path(directory) / ".env").read_text()
            self.assertIn("Alerts__DryRun='true'", body)
            self.assertIn("RustSource__Enabled='false'", body)
            self.assertNotIn(body.split("BETTERRUSTPLUS_INGEST_KEY=")[1].splitlines()[0], output.getvalue())
            install.assert_not_called()
            run.assert_not_called()

    def test_existing_configure_only_preserves_settings(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, "DEPLOY_DIR", Path(directory)), \
                patch.object(native, "regular_file"), patch.object(native.config, "collect_config") as collect, \
                contextlib.redirect_stdout(io.StringIO()):
            path = Path(directory) / ".env"
            path.write_text("existing-private-settings")
            self.assertEqual(0, native.main(["--configure-only"]))
            self.assertEqual("existing-private-settings", path.read_text())
            collect.assert_not_called()

    def test_private_file_read_checks_actual_descriptor_and_bounds(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_bytes(b"private-settings")
            with patch.object(native.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)):
                self.assertEqual(b"private-settings", native.private_bytes(path))
            with patch.object(native.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o644, st_uid=0)):
                with self.assertRaisesRegex(RuntimeError, "owner-only"):
                    native.private_bytes(path)
            path.write_bytes(b"x" * 65537)
            with patch.object(native.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)):
                with self.assertRaisesRegex(RuntimeError, "large"):
                    native.private_bytes(path)

    def test_health_rejects_wrong_process_and_has_bounded_wait(self):
        clock = [0]
        def sleep(seconds):
            clock[0] += seconds
        with self.assertRaisesRegex(RuntimeError, "90 seconds"):
            native.wait_for_health(123, health_reader=lambda: {"status": "running", "processId": 999},
                                   sleep=sleep, monotonic=lambda: clock[0])
        self.assertEqual(90, clock[0])
        ready = {"status": "running", "processId": 123, "mode": "dry-run"}
        self.assertEqual(ready, native.wait_for_health(123, health_reader=lambda: ready))

    def test_start_confirms_systemd_process_and_expected_health(self):
        commands = []
        def run(command, **kwargs):
            commands.append(command)
            return "123\n" if "show" in command else ""
        with patch.object(native, "require_root"), patch.object(native, "prerequisites", return_value=None), \
                patch.object(native, "regular_file"), patch.object(native, "check_existing_installation"), patch.object(native, "run", side_effect=run), \
                patch.object(native, "wait_for_health", return_value={"status": "running"}) as health:
            native.start_service()
            health.assert_called_once_with(123)
            self.assertIn(["systemctl", "is-active", "--quiet", native.SERVICE], commands)
            self.assertFalse(any("docker" in command for command in commands))

    def test_start_rejects_missing_main_pid(self):
        with patch.object(native, "require_root"), patch.object(native, "prerequisites", return_value=None), \
                patch.object(native, "regular_file"), patch.object(native, "check_existing_installation"), patch.object(native, "run", return_value="0"), \
                patch.object(native, "wait_for_health") as health:
            with self.assertRaisesRegex(RuntimeError, "process"):
                native.start_service()
            health.assert_not_called()

    def test_different_existing_unit_is_not_replaced(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, "UNIT_PATH", Path(directory) / "existing.service"), \
                patch.object(native, "safe_directory"), patch.object(native, "regular_file", return_value=SimpleNamespace(st_uid=0, st_mode=0o644)):
            native.UNIT_PATH.write_bytes(b"different existing service")
            with self.assertRaisesRegex(RuntimeError, "differs"):
                native.check_existing_installation(b"our template")
            self.assertEqual(b"different existing service", native.UNIT_PATH.read_bytes())

    @unittest.skipUnless(os.name == "posix", "Native symlink/permission transaction is exercised on Linux CI")
    def test_install_is_atomic_preserves_settings_and_rolls_back_bad_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            deploy, install, settings, unit = root / "deploy", root / "opt", root / "etc", root / "unit.service"
            deploy.mkdir()
            settings.mkdir()
            (deploy / "betterrustplus.service").write_bytes(b"our template")
            (settings / "listener.env").write_bytes(b"installed-private-config")
            (settings / "listener.env").chmod(0o600)
            old = install / "releases" / "old"
            old.mkdir(parents=True)
            (install / "current").symlink_to(old)
            def build(stage):
                output = stage / "publish"
                output.mkdir()
                (output / "BetterRustPlus.Server").write_bytes(b"app")
                return output
            with patch.object(native, "DEPLOY_DIR", deploy), patch.object(native, "INSTALL_DIR", install), \
                    patch.object(native, "CONFIG_DIR", settings), patch.object(native, "UNIT_PATH", unit), \
                    patch.object(native, "require_root"), patch.object(native, "prerequisites", return_value=None), \
                    patch.object(native, "check_existing_installation"), patch.object(native, "build_release", side_effect=build), \
                    patch.object(native, "start_service", side_effect=RuntimeError("failed")), patch.object(native, "run"):
                with self.assertRaisesRegex(RuntimeError, "previous release"):
                    native.install_service()
                self.assertEqual(old, (install / "current").resolve())
                self.assertEqual(b"installed-private-config", (settings / "listener.env").read_bytes())
                self.assertEqual(0o755, stat.S_IMODE(install.stat().st_mode))
                self.assertEqual(0o755, stat.S_IMODE((install / "releases").stat().st_mode))

    def test_build_failure_never_switches_release_or_installs_unit(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(native, "DEPLOY_DIR", Path(directory)), \
                patch.object(native, "require_root"), patch.object(native, "prerequisites", return_value=None), \
                patch.object(native, "regular_file"), patch.object(native, "check_existing_installation"), \
                patch.object(native, "private_bytes", return_value=b"private"), \
                patch.object(native, "build_release", side_effect=RuntimeError("failed build")), \
                patch.object(native, "write_exclusive") as write, patch.object(native.os, "replace") as replace:
            (Path(directory) / "betterrustplus.service").write_bytes(b"template")
            with self.assertRaisesRegex(RuntimeError, "failed build"):
                native.install_service()
            write.assert_not_called()
            replace.assert_not_called()

    def test_changed_local_settings_during_build_never_install_private_data(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            deploy, install, settings = root / "deploy", root / "opt", root / "etc"
            deploy.mkdir()
            (deploy / "betterrustplus.service").write_bytes(b"template")
            with patch.object(native, "DEPLOY_DIR", deploy), patch.object(native, "INSTALL_DIR", install), \
                    patch.object(native, "CONFIG_DIR", settings), patch.object(native, "UNIT_PATH", root / "unit.service"), \
                    patch.object(native, "require_root"), patch.object(native, "prerequisites", return_value=None), \
                    patch.object(native, "regular_file"), patch.object(native, "check_existing_installation"), \
                    patch.object(native, "private_bytes", side_effect=[b"before", b"after"]), \
                    patch.object(native, "build_release", return_value=root), patch.object(native, "write_exclusive") as write, \
                    patch.object(native.os, "replace") as replace:
                with self.assertRaisesRegex(RuntimeError, "changed during"):
                    native.install_service()
                write.assert_not_called()
                replace.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "O_NOFOLLOW is exercised on Linux CI")
    def test_private_settings_symlink_is_not_followed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "secret"
            target.write_bytes(b"PRIVATE")
            target.chmod(0o600)
            link = root / "linked.env"
            link.symlink_to(target)
            with self.assertRaises(OSError):
                native.private_bytes(link)


if __name__ == "__main__":
    unittest.main()
