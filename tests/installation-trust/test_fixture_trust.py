"""Exercise the harness-only installation trust helper against a fake HTTP boundary."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
COMMIT = "a" * 40
ENDPOINT = "https://127.0.0.1:18006/"


def installation(id, *, valid=True, endpoints=(ENDPOINT,), untrusted=(ENDPOINT,)):
    return {"id": id, "valid": valid,
            "trust": {"endpoints": list(endpoints), "untrusted": list(untrusted),
                      "trustedAt": None, "trustedBy": None}}


class FixtureTrustTests(unittest.TestCase):
    def run_helper(self, *, source="/fixture", target=COMMIT, installations=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            revision = {"id": "revision-id", "commit": target, "isCurrentSource": True}
            (path / "repository.json").write_text(json.dumps({
                "url": source, "branch": "main", "headCommit": target, "revision": revision,
            }))
            (path / "inventory.json").write_text(json.dumps({
                "revision": revision,
                "labs": [{"installations": installations if installations is not None else [installation("one")]}],
            }))
            script = r"""
set -euo pipefail
source "$1/tests/installation-trust/fixture.sh"
api=http://fixture.invalid
data=$2
sleep() { :; }
call() {
  if [[ $1 == "$api/api/repository" ]]; then
    cat "$data/repository.json"
  elif [[ $1 == "$api/api/inventory" ]]; then
    cat "$data/inventory.json"
  elif [[ $1 == -X && $2 == POST && $3 =~ ^$api/api/installations/([a-z]+)/trust$ ]]; then
    printf '%s %s\n' "${BASH_REMATCH[1]}" "$5" >> "$data/requests"
    printf '{}'
  else
    printf 'Unexpected fixture API call\n' >&2
    return 1
  fi
}
trust_fixture_installations /fixture "$3"
"""
            result = subprocess.run(
                ["bash", "-c", script, "--", str(ROOT), str(path), COMMIT],
                capture_output=True, text=True, timeout=5,
            )
            requests = path / "requests"
            sent = [line.split(" ", 1) for line in requests.read_text().splitlines()] if requests.exists() else []
            return result, [(id, json.loads(body)) for id, body in sent]

    def test_trusts_the_shown_endpoints_of_each_untrusted_installation(self):
        result, requests = self.run_helper(installations=[
            installation("one"),
            installation("two", endpoints=(ENDPOINT, "https://127.0.0.2:8006/"), untrusted=("https://127.0.0.2:8006/",)),
        ])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(requests, [
            ("one", {"endpoints": [ENDPOINT]}),
            ("two", {"endpoints": [ENDPOINT, "https://127.0.0.2:8006/"]}),
        ])

    def test_never_trusts_for_a_changed_source_or_unexpected_commit(self):
        for change in ({"source": "/other"}, {"target": "c" * 40}):
            with self.subTest(change=change):
                result, requests = self.run_helper(**change)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(requests, [])

    def test_skips_trusted_and_invalid_installations(self):
        result, requests = self.run_helper(installations=[
            installation("trusted", untrusted=()),
            installation("invalid", valid=False),
        ])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(requests, [])


if __name__ == "__main__":
    unittest.main()
