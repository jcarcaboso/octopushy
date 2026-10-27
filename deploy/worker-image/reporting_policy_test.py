#!/usr/bin/env python3
"""Source regressions only. These do NOT evaluate Nix, build or boot an image."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class NativeReportingPolicy(unittest.TestCase):
    def test_reporter_is_built_with_the_image_and_referenced_from_the_store(self):
        flake = (ROOT / "flake.nix").read_text()
        image = (ROOT / "deploy/worker-image/image.nix").read_text()
        self.assertIn('cargoBuildFlags = [ "-p" "octopushy" "-p" "octopushy-disk-reporter" ];', flake)
        self.assertIn('${octopushyPackage}/bin/octopushy-disk-reporter', image)
        self.assertIn('name = "octopushy-worker-reporting";', image)
        self.assertIn('agent managed-reporting', image)

    def test_runtime_credentials_hardening_and_persistent_hourly_cadence(self):
        image = (ROOT / "deploy/worker-image/image.nix").read_text()
        for expected in [
            'systemd.services.octopushy-disk-reporter',
            'systemd.timers.octopushy-disk-reporter',
            'ConditionPathExists = "/var/lib/octopushy-disk-reporter/current/token"',
            'LoadCredential = [',
            'token:/var/lib/octopushy-disk-reporter/current/token',
            'DynamicUser = true', 'ProtectSystem = "strict"',
            'NoNewPrivileges = true', 'StandardOutput = "null"', 'StandardError = "null"',
            'OnUnitActiveSec = "3600s"', 'AccuracySec = "1s"',
            'TimeoutStartSec = "30s"', 'LimitCORE = 0',
        ]:
            self.assertIn(expected, image)
        self.assertNotIn('disk-reporter.token', image)
        self.assertNotIn('Bearer ', image)


if __name__ == "__main__":
    unittest.main()
