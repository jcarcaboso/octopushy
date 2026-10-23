import json
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
        self.read_only = self.root / "read-only-token"
        self.read_only.write_text("reader@pve!inventory=DO-NOT-PRINT-READ\n")
        self.privileged = self.root / "privileged-token"
        self.privileged.write_text("writer@pve!lifecycle=DO-NOT-PRINT-WRITE\n")
        for path in (self.pg, self.cookie, self.read_only, self.privileged):
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
            "PVE_READ_ONLY_TOKEN_FILE": str(self.read_only),
            "PVE_PRIVILEGED_TOKEN_FILE": str(self.privileged),
        }

    def harness_preflight(self, env, after=""):
        # Run only the actual preflight prefix, never the full/live harness.
        script = Path(__file__).resolve().parents[1] / "proxmox/run.sh"
        prefix, _ = script.read_text().split(
            "# Offline preflight boundary: all controller/local side effects start below.", 1
        )
        for default in ("proxmox-e2e", "portainer-vm-e2e"):
            prefix = prefix.replace(f"work=$root/.dev/{default}", 'work=$HARNESS_TEST_WORK')
        return subprocess.run(
            ["bash", "-c", prefix + "\n" + after, str(script)],
            env={"PATH": os.environ["PATH"], "HARNESS_TEST_WORK": str(self.root / "fixture"), **env},
            capture_output=True, text=True, timeout=10,
        )

    def test_valid_config_is_offline_by_default(self):
        with patch("socket.getaddrinfo", side_effect=AssertionError("no DNS in offline validation")):
            validate(self.env)

    def test_requires_explicit_inputs(self):
        for key in self.env:
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate({k: v for k, v in self.env.items() if k != key})

    def test_legacy_token_cannot_replace_either_role_before_dns(self):
        for missing in (("PVE_READ_ONLY_TOKEN_FILE",), ("PVE_PRIVILEGED_TOKEN_FILE",),
                        ("PVE_READ_ONLY_TOKEN_FILE", "PVE_PRIVILEGED_TOKEN_FILE")):
            env = {key: value for key, value in self.env.items() if key not in missing}
            env["PVE_TOKEN_FILE"] = str(self.privileged)
            with self.subTest(missing=missing), patch(
                "socket.getaddrinfo", side_effect=AssertionError("credentials must fail before DNS")
            ), self.assertRaisesRegex(ValueError, missing[0]):
                validate(env, preflight=True)

    def test_duplicate_proxmox_files_or_credentials_are_rejected_without_values(self):
        alias = self.root / "alias"
        alias.symlink_to(self.read_only)
        hardlink = self.root / "hardlink"
        hardlink.hardlink_to(self.read_only)
        duplicate = self.root / "duplicate"
        duplicate.write_text(self.read_only.read_text().rstrip("\n") + "\r\n\n")
        duplicate.chmod(0o600)
        for path in (self.read_only, alias, hardlink, duplicate):
            with self.subTest(path=path.name):
                with self.assertRaisesRegex(ValueError, "distinct") as caught:
                    validate({**self.env, "PVE_PRIVILEGED_TOKEN_FILE": str(path)})
                self.assertNotIn("DO-NOT-PRINT", str(caught.exception))

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

    def test_both_proxmox_files_must_be_nonempty_private_regular_files(self):
        for name, path in (("PVE_READ_ONLY_TOKEN_FILE", self.read_only),
                           ("PVE_PRIVILEGED_TOKEN_FILE", self.privileged)):
            for mode in (0o644, 0o640, 0o660, 0o444, 0o700, 0o000):
                with self.subTest(name=name, mode=oct(mode)):
                    path.chmod(mode)
                    with self.assertRaisesRegex(ValueError, name):
                        validate(self.env)
                    path.chmod(0o600)
            original = path.read_bytes()
            for value in (b"", b"\n \t\n"):
                path.write_bytes(value)
                with self.subTest(name=name, value=value), self.assertRaises(ValueError):
                    validate(self.env)
            path.write_bytes(original)
            for invalid in (self.root, self.root / "missing"):
                with self.subTest(name=name, file=invalid.name), self.assertRaisesRegex(ValueError, name):
                    validate({**self.env, name: str(invalid)})
            path.chmod(0o400)
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
        env = {"PATH": os.environ["PATH"], **self.env, "REMOTE_WORKER": "1"}
        del env["REMOTE_API_URL"]  # Fails before DNS, reading any real credential or calling APIs.
        result = self.harness_preflight(env)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("REMOTE_API_URL is required", result.stderr)
        self.assertEqual(retained.read_text(), "9999\n")
        self.assertEqual(list(fixture.iterdir()), [retained])
        self.assertNotIn("DO-NOT-PRINT", result.stdout + result.stderr)

    def test_local_harness_rejects_legacy_token_before_side_effects(self):
        result = self.harness_preflight({"PVE_TOKEN_FILE": str(self.privileged)})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("PVE_READ_ONLY_TOKEN_FILE", result.stderr)
        self.assertNotIn("DO-NOT-PRINT", result.stdout + result.stderr)
        self.assertFalse((self.root / "fixture").exists())

    def test_harness_imports_distinct_role_values_to_matching_installation_keys(self):
        requests = self.root / "imports.jsonl"
        result = self.harness_preflight(
            {**self.env, "PVE_NODE": "disposable-node", "HARNESS_REQUESTS": str(requests)},
            'call() { cat >>"$HARNESS_REQUESTS"; }\nimport_proxmox_credentials',
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        imported = [json.loads(line) for line in requests.read_text().splitlines()]
        self.assertEqual([(item["key"], item["value"], item["visibility"]) for item in imported], [
            ("proxmox/disposable-node/read-only-token", "reader@pve!inventory=DO-NOT-PRINT-READ", "secret"),
            ("proxmox/disposable-node/privileged-token", "writer@pve!lifecycle=DO-NOT-PRINT-WRITE", "secret"),
        ])
        self.assertNotIn("DO-NOT-PRINT", result.stdout + result.stderr)

    def test_cleanup_and_resume_refuse_legacy_fixture_without_rebinding_context(self):
        fixture = self.root / "fixture"
        installation = fixture / "lab/labs/home/proxmox/umbrel/installation.yaml"
        installation.parent.mkdir(parents=True)
        legacy = "connection:\n  token: { store: proxmox/umbrel/api-token }\n"
        installation.write_text(legacy)
        (fixture / "vmid").write_text("9999\n")
        context = fixture / "run-context.json"
        context.write_text('{"tokenKey":"proxmox/umbrel/api-token","revision":"historical"}\n')
        config = self.root / "nixos"
        config.mkdir()
        (config / "flake.lock").write_text("{}\n")
        for mode in ({"CLEANUP": "1"}, {
            "RESUME_SERVICE": "1", "PORTAINER_WORKFLOW": "1", "KEEP_GUEST": "1",
            "PORTAINER_CONFIG_DIR": str(config),
        }):
            with self.subTest(mode=mode):
                result = self.harness_preflight({**self.env, **mode})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("explicit split-credential migration", result.stderr)
                self.assertEqual(installation.read_text(), legacy)
                self.assertEqual((fixture / "vmid").read_text(), "9999\n")
                self.assertEqual(context.read_text(),
                                 '{"tokenKey":"proxmox/umbrel/api-token","revision":"historical"}\n')

    def test_retained_fixture_requires_the_exact_split_keys_not_an_alias(self):
        fixture = self.root / "fixture"
        installation = fixture / "lab/labs/home/proxmox/umbrel/installation.yaml"
        installation.parent.mkdir(parents=True)
        split = ("connection:\n"
                 "  readOnlyToken: { store: proxmox/umbrel/read-only-token }\n"
                 "  privilegedToken: { store: proxmox/umbrel/privileged-token }\n")
        for source, valid in (
            (split, True),
            (split.replace("privileged-token", "read-only-token"), False),
            (split + "  token: { store: proxmox/umbrel/api-token }\n", False),
        ):
            installation.write_text(source)
            with self.subTest(source=source):
                result = self.harness_preflight({**self.env, "CLEANUP": "1"})
                self.assertEqual(result.returncode == 0, valid, result.stderr)
                self.assertEqual(installation.read_text(), source)


if __name__ == "__main__":
    unittest.main()
