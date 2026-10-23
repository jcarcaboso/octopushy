"""Offline fixture/import wiring checks; no services, builds or harness execution."""
import json
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
E2E = ROOT / "tests/e2e"


class CredentialFixtureTests(unittest.TestCase):
    def test_installations_require_two_distinct_role_keys(self):
        paths = list((ROOT / "examples/lab/labs").rglob("installation.yaml"))
        paths.append(E2E / "lab/labs/e2e/proxmox/korriban/installation.yaml")
        for path in paths:
            with self.subTest(path=str(path.relative_to(ROOT))):
                source = path.read_text()
                self.assertNotRegex(source, r"(?m)^\s+token:")
                installation = path.parent.name
                self.assertIn(f"readOnlyToken: {{ store: proxmox/{installation}/read-only-token }}", source)
                self.assertIn(f"privilegedToken: {{ store: proxmox/{installation}/privileged-token }}", source)

    def test_mock_imports_match_two_distinct_declared_identities(self):
        fixture = json.loads((E2E / "proxmox.json").read_text())
        expected = {
            "proxmox/korriban/read-only-token": fixture["token"],
            "proxmox/korriban/privileged-token": fixture["additionalTokens"][0],
        }
        self.assertEqual(len({value.split("=")[0] for value in expected.values()}), 2)
        self.assertEqual(len(set(expected.values())), 2)
        for name in ("run.sh", "demo.sh"):
            with self.subTest(script=name):
                payloads = [json.loads(value) for value in
                            re.findall(r'variables" -d \'([^\']+)\'', (E2E / name).read_text())]
                imported = {item["key"]: item["value"] for item in payloads if item["key"].startswith("proxmox/")}
                self.assertEqual(imported, expected)
                self.assertTrue(all(item["visibility"] == "secret" for item in payloads
                                    if item["key"] in expected))
                self.assertFalse(any(item["key"] == "e2e/proxmox-token" for item in payloads))

    def test_native_helpers_receive_only_the_explicit_required_role(self):
        source = (ROOT / "tests/proxmox/run.sh").read_text()
        self.assertIn('readOnlyToken: { store: proxmox/$node/read-only-token }', source)
        self.assertIn('privilegedToken: { store: proxmox/$node/privileged-token }', source)
        self.assertRegex(source, r'PVE_TOKEN_FILE=\$read_only_token_file TEST_NAME=\$name \\\n\s+"\$root/tests/fedora/run.sh"')
        self.assertRegex(source, r'PVE_TOKEN_FILE=\$privileged_token_file TEST_NAME=\$name \\\n\s+python3 "\$root/tests/portainer/live.py"')


if __name__ == "__main__":
    unittest.main()
