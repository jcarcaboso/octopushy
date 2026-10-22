import os
from pathlib import Path
import socket
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from config import validate


class RemoteConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pg = self.root / "pg"
        self.pg.write_text("postgres://tofu:DO-NOT-PRINT@state.example.test:5432/remote_state?sslmode=require\n")
        self.cookie = self.root / "cookies"
        self.cookie.write_text("# Netscape HTTP Cookie File\n")
        for path in (self.pg, self.cookie):
            path.chmod(0o600)
        self.env = {
            "REMOTE_API_URL": "https://controller.example.test",
            "REMOTE_AGENT_URL": "https://controller.example.test:8443",
            "REMOTE_AGENT_ID": "11111111-2222-4333-8444-555555555555",
            "REMOTE_AGENT_LABEL": "remote-acceptance",
            "REMOTE_COOKIE_FILE": str(self.cookie),
            "REMOTE_TOFU_PG_FILE": str(self.pg),
            "REMOTE_WORK_DIR": str(self.root / "fixture"),
            "PVE_ENDPOINT": "https://pve.example.test:8006/",
            "PVE_TOKEN_FILE": str(self.cookie),
        }

    def test_valid_config_is_offline_by_default(self):
        with patch("socket.getaddrinfo", side_effect=AssertionError("no DNS in offline validation")):
            validate(self.env)

    def test_requires_explicit_inputs(self):
        for key in self.env:
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate({k: v for k, v in self.env.items() if k != key})

    def test_rejects_unsupported_modes_and_local_endpoints(self):
        cases = {
            "REMOTE_API_URL": [
                "http://localhost:18080", "http://127.0.0.1", "http://[::1]", "http://0.0.0.0",
                "http://[::ffff:127.0.0.1]", "http://169.254.1.2", "http://host:99999",
                "http://host:0", "http://user:DO-NOT-PRINT@host", "http://host/?token=DO-NOT-PRINT",
            ],
            "REMOTE_AGENT_URL": ["http://controller.example.test:8443", "https://host:bad"],
            "PVE_ENDPOINT": ["https://pve.localhost.", "https://[ff02::1]"],
            "REMOTE_AGENT_ID": ["not-a-uuid"],
            "REMOTE_AGENT_LABEL": ["x,other", "x\nother"],
            "REMOTE_WORK_DIR": ["relative", "/"],
            "PORTAINER_WORKFLOW": ["1"],
            "FEDORA_WORKFLOW": ["1"],
            "UI": ["1"],
        }
        for key, values in cases.items():
            for value in values:
                with self.subTest(key=key, value=value), self.assertRaises(ValueError) as caught:
                    validate({**self.env, key: value})
                self.assertNotIn("DO-NOT-PRINT", str(caught.exception))

    def test_backend_endpoint_and_private_files(self):
        for pg in (
            "postgres://tofu:DO-NOT-PRINT@localhost/db",
            "postgres://tofu:DO-NOT-PRINT@state.example.test/",
            "postgres://tofu:DO-NOT-PRINT@state.example.test/db?host=127.0.0.1",
            "postgres:///db?host=/var/run/postgresql",
            "postgres://state.example.test:bad/db",
        ):
            self.pg.write_text(pg)
            with self.subTest(pg=pg), self.assertRaises(ValueError) as caught:
                validate(self.env)
            self.assertNotIn("DO-NOT-PRINT", str(caught.exception))
        self.pg.write_text("postgres://state.example.test/remote_state")
        for path in (self.pg, self.cookie):
            path.chmod(0o644)
            with self.assertRaises(ValueError):
                validate(self.env)
            path.chmod(0o600)

    def test_dns_preflight_is_explicit_and_rejects_resolved_loopback(self):
        def address(ip):
            return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", (ip, 443))]

        with patch("socket.getaddrinfo", return_value=address("10.20.30.40")) as dns:
            validate(self.env, preflight=True)
            self.assertEqual(dns.call_count, 4)
        for resolved in ("127.0.0.1", "0.0.0.0", "169.254.10.1"):
            with patch("socket.getaddrinfo", return_value=address(resolved)), self.assertRaises(ValueError):
                validate(self.env, preflight=True)
        with patch("socket.getaddrinfo", side_effect=socket.gaierror("DO-NOT-PRINT")), self.assertRaises(ValueError) as caught:
            validate(self.env, preflight=True)
        self.assertNotIn("DO-NOT-PRINT", str(caught.exception))

    def test_invalid_remote_mode_exits_before_touching_retained_fixture(self):
        fixture = Path(self.env["REMOTE_WORK_DIR"])
        fixture.mkdir()
        retained = fixture / "vmid"
        retained.write_text("9999\n")
        script = Path(__file__).resolve().parents[1] / "proxmox/run.sh"
        env = {"PATH": os.environ["PATH"], **self.env, "REMOTE_WORKER": "1"}
        del env["REMOTE_API_URL"]  # Fails before DNS, reading any real credential or calling APIs.
        result = subprocess.run(["bash", str(script)], env=env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("REMOTE_API_URL is required", result.stderr)
        self.assertEqual(retained.read_text(), "9999\n")
        self.assertEqual(list(fixture.iterdir()), [retained])
        self.assertNotIn("DO-NOT-PRINT", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
