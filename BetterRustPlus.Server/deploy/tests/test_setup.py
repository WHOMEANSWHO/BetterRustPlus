import contextlib
import importlib.util
import io
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
import urllib.error
import urllib.request
import warnings
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "setup_docker.py"
SPEC = importlib.util.spec_from_file_location("vps_setup", SCRIPT)
setup = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(setup)


class SetupTests(unittest.TestCase):
    def test_secret_prompt_refuses_echoing_fallback(self):
        def fallback(_):
            warnings.warn("Password input may be echoed", setup.getpass.GetPassWarning)
            return "PRIVATE"
        with patch.object(setup.getpass, "getpass", side_effect=fallback), patch("builtins.input") as plain_input:
            with self.assertRaisesRegex(RuntimeError, "interactive SSH terminal"):
                setup.prompt("Private token", secret=True)
            plain_input.assert_not_called()

    def test_health_redirects_are_rejected_without_external_request(self):
        request = urllib.request.Request(setup.HEALTH_URL)
        with self.assertRaises(urllib.error.HTTPError):
            setup.NoHealthRedirects().redirect_request(request, None, 302, "Found", {}, "https://external.invalid/")

    def test_health_opener_uses_no_proxy_and_no_redirects(self):
        with patch.object(setup.urllib.request, "build_opener") as build:
            build.return_value.open.return_value.__enter__.return_value.read.return_value = b'{"status":"running"}'
            self.assertEqual("running", setup.read_health()["status"])
            self.assertEqual({}, build.call_args.args[0].proxies)
            self.assertIsInstance(build.call_args.args[1], setup.NoHealthRedirects)
            self.assertEqual(setup.HEALTH_URL, build.return_value.open.call_args.args[0])

    def test_configuration_generates_private_keys_and_safe_defaults(self):
        first, second = setup.base_config("our-server"), setup.base_config("our-server")
        self.assertRegex(first["BETTERRUSTPLUS_INGEST_KEY"], r"^[0-9a-f]{64}$")
        self.assertNotEqual(first["BETTERRUSTPLUS_INGEST_KEY"], second["BETTERRUSTPLUS_INGEST_KEY"])
        self.assertEqual("true", first["Alerts__DryRun"])
        self.assertEqual("false", first["RustSource__Enabled"])
        self.assertEqual("false", first["DiscordBot__Enabled"])
        self.assertTrue(all(v == "false" for k, v in first.items() if k.endswith("MentionEveryone")))

    def test_validation_rejects_invalid_server_host_ids_and_tokens(self):
        for value in ("", "bad server", "x\nOTHER=value", "x" * 129):
            with self.assertRaises(ValueError):
                setup.server_id(value)
        for value in ("https://host", "host:123", "-bad.example", "host/path", "a..b"):
            with self.assertRaises(ValueError):
                setup.host(value)
        self.assertEqual("::1", setup.host("::1"))
        self.assertEqual("rust.example", setup.host("rust.example"))
        with self.assertRaises(ValueError):
            setup.integer("18446744073709551616")
        self.assertEqual("0", setup.integer("0", -(2**31), 2**31 - 1))
        with self.assertRaises(ValueError):
            setup.relay_id("not-a-uuid")

    def test_alarm_ids_are_bounded_and_unique(self):
        self.assertEqual([], setup.alarm_ids(""))
        self.assertEqual(["42", "7"], setup.alarm_ids("42, 7,42"))
        for value in ("0", "4294967296", ",", ",".join(str(n) for n in range(1, 34))):
            with self.assertRaises(ValueError):
                setup.alarm_ids(value)

    def test_webhook_rejects_external_and_ambiguous_addresses(self):
        self.assertEqual("https://discord.com/api/webhooks/123/private", setup.webhook("https://discord.com/api/webhooks/123/private"))
        for value in ("http://discord.com/api/webhooks/123/private", "https://evil.example/api/webhooks/123/private",
                      "https://discord.com.evil.example/api/webhooks/123/private", "https://user@discord.com/api/webhooks/123/private",
                      "https://discord.com:8443/api/webhooks/123/private", "https://discord.com/api/webhooks/123/private?wait=true",
                      "https://discord.com/api/webhooks/123/private#fragment"):
            with self.assertRaises(ValueError):
                setup.webhook(value)

    def test_env_literal_quoting_and_newline_injection_rejection(self):
        self.assertEqual("'token$literal#not-comment'", setup.quote_env("token$literal#not-comment"))
        for value in ("bad\nNEW=value", "bad\r", "bad\x00", "bad'quote", "bad\\escape"):
            with self.assertRaises(ValueError):
                setup.quote_env(value)

    def test_write_is_private_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            setup.write_config(path, {"PRIVATE": "secret$literal"})
            self.assertIn("PRIVATE='secret$literal'", path.read_text())
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            with self.assertRaises(FileExistsError):
                setup.write_config(path, {"PRIVATE": "replacement"})
            self.assertIn("secret$literal", path.read_text())
            self.assertEqual([".env"], [p.name for p in Path(directory).iterdir()])

    def test_bad_key_cannot_create_config(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            with self.assertRaises(ValueError):
                setup.write_config(path, {"INJECT\nNEXT": "value"})
            self.assertFalse(path.exists())

    def test_check_missing_docker_is_read_only(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(setup, "DEPLOY_DIR", Path(directory)), \
                patch.object(setup.shutil, "which", return_value=None), patch.object(setup, "collect_config") as collect, \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, setup.main(["--check"]))
            collect.assert_not_called()
            self.assertEqual([], list(Path(directory).iterdir()))

    def test_configure_only_never_starts_docker_or_echoes_key(self):
        values = setup.base_config("group")
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory, patch.object(setup, "DEPLOY_DIR", Path(directory)), \
                patch.object(setup, "collect_config", return_value=values), patch.object(setup, "start_service") as start, \
                patch.object(setup, "prerequisites") as prerequisites, contextlib.redirect_stdout(output):
            self.assertEqual(0, setup.main(["--configure-only"]))
            start.assert_not_called()
            prerequisites.assert_not_called()
            self.assertTrue((Path(directory) / ".env").is_file())
        self.assertNotIn(values["BETTERRUSTPLUS_INGEST_KEY"], output.getvalue())

    def test_existing_config_preserved_before_prompting(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(setup, "DEPLOY_DIR", Path(directory)), \
                patch.object(setup, "collect_config") as collect, contextlib.redirect_stdout(io.StringIO()):
            path = Path(directory) / ".env"
            path.write_text("original")
            self.assertEqual(2, setup.main(["--configure-only"]))
            collect.assert_not_called()
            self.assertEqual("original", path.read_text())

    def test_prerequisites_never_use_shell_or_emit_docker_secrets(self):
        with patch.object(setup.shutil, "which", return_value="docker"), \
                patch.object(setup.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, stderr="PRIVATE")) as run:
            self.assertNotIn("PRIVATE", setup.prerequisites())
            self.assertNotIn("shell", run.call_args.kwargs)
            self.assertIsInstance(run.call_args.args[0], list)

    def test_start_prerequisite_failure_never_runs_compose(self):
        with patch.object(setup, "prerequisites", return_value="Docker unavailable"), patch.object(setup.subprocess, "run") as run:
            with self.assertRaisesRegex(RuntimeError, "Docker unavailable"):
                setup.start_service()
            run.assert_not_called()

    def test_start_failure_redacts_output(self):
        with patch.object(setup, "prerequisites", return_value=None), \
                patch.object(setup.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, stdout="PRIVATE", stderr="PRIVATE")):
            with self.assertRaises(RuntimeError) as error:
                setup.start_service()
            self.assertNotIn("PRIVATE", str(error.exception))

    def test_health_wait_is_bounded_and_ready_does_not_mean_source_connected(self):
        clock = [0]
        def sleep(seconds):
            clock[0] += seconds
        with patch.object(setup, "prerequisites", return_value=None), \
                patch.object(setup.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)):
            with self.assertRaisesRegex(RuntimeError, "90 seconds"):
                setup.start_service(health_reader=lambda: {}, sleep=sleep, monotonic=lambda: clock[0])
            self.assertEqual(90, clock[0])
            health = setup.start_service(health_reader=lambda: {"status": "running", "mode": "dry-run", "gameSourceConnected": False})
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                setup.show_health(health)
            self.assertIn("not connected", output.getvalue())
            self.assertIn("dry-run", output.getvalue())

    def test_status_does_not_start_or_rewrite(self):
        with patch.object(setup, "read_health", return_value={"status": "running", "mode": "dry-run"}), \
                patch.object(setup, "start_service") as start, patch.object(setup, "write_config") as write, \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, setup.main(["--status"]))
            start.assert_not_called()
            write.assert_not_called()

    def test_start_uses_existing_config_without_rewriting(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(setup, "DEPLOY_DIR", Path(directory)), \
                patch.object(setup, "start_service", return_value={"status": "running", "mode": "discord-delivery-enabled"}) as start, \
                patch.object(setup, "write_config") as write, contextlib.redirect_stdout(io.StringIO()) as output:
            (Path(directory) / ".env").write_text("private-existing")
            self.assertEqual(0, setup.main(["--start"]))
            start.assert_called_once()
            write.assert_not_called()
            self.assertIn("live delivery", output.getvalue())


if __name__ == "__main__":
    unittest.main()
