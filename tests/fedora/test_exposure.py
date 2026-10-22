"""Small ownership/recovery checks; no SSH, firewalld, Docker, or root required."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[2] / "examples/lab/modules/ansible/fedora/files/exposure.py"
SPEC = importlib.util.spec_from_file_location("exposure", SOURCE)
exposure = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(exposure)


class FakeFirewall:
    def __init__(self):
        self.rules = set()
        self.changes = []
        self.fail_after = None

    def has(self, permanent, zone, rule):
        return (permanent, zone, rule) in self.rules

    def change(self, permanent, zone, rule, present):
        if self.has(permanent, zone, rule) == present:
            return False
        if self.fail_after is not None and len(self.changes) >= self.fail_after:
            raise RuntimeError("simulated interruption")
        item = permanent, zone, rule
        self.changes.append((item, present))
        if present:
            self.rules.add(item)
        else:
            self.rules.remove(item)
        return True


class ExposureTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.firewall = FakeFirewall()
        self.config = {"operation": "apply", "id": "app-a", "port": 8080,
                       "address": "10.1.0.200", "sources": ["10.1.0.0/24"]}

    def run_operation(self, **changes):
        return exposure.reconcile(self.config | changes, self.root, self.firewall)

    def test_idempotence_and_default_deny_is_installed_first(self):
        self.assertTrue(self.run_operation())
        self.assertTrue(self.firewall.changes[0][0][2].endswith(" drop"))
        self.assertTrue(self.firewall.changes[1][0][2].endswith(" drop"))
        self.assertFalse(self.run_operation())
        self.assertFalse(self.run_operation(operation="check"))

    def test_service_removal_does_not_touch_its_sibling(self):
        self.run_operation()
        self.run_operation(id="app-b", port=8081)
        sibling = set(r for r in self.firewall.rules if 'port="8081"' in r[2])
        self.run_operation(operation="remove")
        self.assertEqual(sibling, self.firewall.rules)
        self.assertTrue((self.root / "app-b.json").exists())
        self.assertFalse(self.run_operation(operation="remove"))

    def test_binding_conflicts_are_refused(self):
        self.run_operation()
        before = set(self.firewall.rules)
        with self.assertRaisesRegex(ValueError, "owned by"):
            self.run_operation(id="app-b")
        self.assertEqual(before, self.firewall.rules)
        self.assertFalse((self.root / "app-b.json").exists())

    def test_preexisting_manual_rules_are_not_silently_adopted(self):
        rule = exposure.desired(self.config)["rules"][0]
        self.firewall.rules.add((False, "public", rule))
        before = set(self.firewall.rules)
        with self.assertRaisesRegex(ValueError, "refusing adoption"):
            self.run_operation()
        self.assertEqual(before, self.firewall.rules)
        self.assertFalse((self.root / "app-a.json").exists())

    def test_source_update_does_not_adopt_an_unowned_manual_grant(self):
        self.run_operation()
        rule = exposure.desired(self.config | {"sources": ["10.1.0.221/32"]})["rules"][1]
        self.firewall.rules.add((True, "public", rule))
        before = set(self.firewall.rules)
        receipt = (self.root / "app-a.json").read_text()
        with self.assertRaisesRegex(ValueError, "refusing adoption"):
            self.run_operation(sources=["10.1.0.221/32"])
        self.assertEqual(before, self.firewall.rules)
        self.assertEqual(receipt, (self.root / "app-a.json").read_text())

    def test_binding_change_requires_explicit_removal(self):
        self.run_operation()
        with self.assertRaisesRegex(ValueError, "removal first"):
            self.run_operation(port=9090)

    def test_source_update_removes_the_old_grant_but_keeps_the_deny(self):
        self.run_operation()
        self.assertTrue(self.run_operation(sources=["10.1.0.221/32"]))
        self.assertFalse(any('source address="10.1.0.0/24"' in r[2] for r in self.firewall.rules))
        self.assertEqual(2, sum(r[2].endswith(" drop") for r in self.firewall.rules))

    def test_partial_effects_retain_ownership_and_can_be_reconciled(self):
        self.firewall.fail_after = 2
        with self.assertRaises(RuntimeError):
            self.run_operation()
        self.assertTrue(json.loads((self.root / "app-a.json").read_text())["pending"])
        with self.assertRaises(ValueError):
            self.run_operation(operation="check")
        self.firewall.fail_after = None
        self.assertTrue(self.run_operation())
        self.assertFalse(self.run_operation(operation="check"))

    def test_partial_source_change_can_be_removed_without_leaving_rules(self):
        self.run_operation()
        self.firewall.fail_after = len(self.firewall.changes) + 1
        with self.assertRaises(RuntimeError):
            self.run_operation(sources=["10.1.0.221/32"])
        self.firewall.fail_after = None
        self.run_operation(operation="remove")
        self.assertEqual(set(), self.firewall.rules)

    def test_check_detects_changed_inputs_and_rule_drift_without_mutating(self):
        self.run_operation()
        with self.assertRaises(ValueError):
            self.run_operation(operation="check", sources=["10.1.0.221/32"])
        self.firewall.rules.pop()
        before = set(self.firewall.rules)
        with self.assertRaisesRegex(ValueError, "drift"):
            self.run_operation(operation="check")
        self.assertEqual(before, self.firewall.rules)

    def test_rejects_unsupported_or_unsafe_inputs_before_recording_ownership(self):
        for changes in ({"sources": []}, {"sources": ["0.0.0.0/0"]},
                        {"sources": ["::/0"]}, {"port": True}, {"port": 80},
                        {"id": "../escape"}, {"address": "0.0.0.0"},
                        {"sources": ["10.1.0.5/24"]}):
            with self.subTest(changes=changes), self.assertRaises((ValueError, TypeError)):
                self.run_operation(**changes)
        self.assertFalse(list(self.root.glob("*.json")))
        self.assertEqual(set(), self.firewall.rules)


if __name__ == "__main__":
    unittest.main()
