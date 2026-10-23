"""Exercise the harness-only trust helper against a fake HTTP boundary."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
COMMIT = "a" * 40


class FixtureTrustTests(unittest.TestCase):
    def run_helper(self, *, source="/fixture", target=COMMIT, truncated=False,
                   can_trust=True, trusted=False, base=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            revision = {"id": "revision-id", "commit": target, "isCurrentSource": True,
                        "trustedAt": "2026-10-23T00:00:00Z" if trusted else None}
            (path / "repository.json").write_text(json.dumps({
                "url": source, "branch": "main", "headCommit": target, "revision": revision,
            }))
            (path / "review.json").write_text(json.dumps({
                "revision": revision, "baseCommit": base, "diff": "+fixture",
                "truncated": truncated, "canTrust": can_trust, "blockedReason": None,
            }))
            script = r"""
set -euo pipefail
source "$1/tests/revision-trust/fixture.sh"
api=http://fixture.invalid
data=$2
sleep() { :; }
call() {
  if [[ $1 == "$api/api/repository" ]]; then
    cat "$data/repository.json"
  elif [[ $1 == "$api/api/repository/revisions/revision-id/review" ]]; then
    cat "$data/review.json"
  elif [[ $1 == -X && $2 == POST && $3 == "$api/api/repository/revisions/revision-id/trust" ]]; then
    printf '%s' "$5" > "$data/request.json"
    printf '{}'
  else
    printf 'Unexpected fixture API call\n' >&2
    return 1
  fi
}
trust_fixture_revision /fixture "$3"
"""
            result = subprocess.run(
                ["bash", "-c", script, "--", str(ROOT), str(path), COMMIT],
                capture_output=True, text=True, timeout=5,
            )
            request = path / "request.json"
            return result, json.loads(request.read_text()) if request.exists() else None

    def test_trusts_only_the_expected_fixture_commit_with_reviewed_base(self):
        result, request = self.run_helper(base="b" * 40)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(request, {"commit": COMMIT, "baseCommit": "b" * 40})

    def test_first_trust_keeps_null_review_base(self):
        result, request = self.run_helper()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(request, {"commit": COMMIT, "baseCommit": None})

    def test_never_trusts_a_changed_source_or_unexpected_commit(self):
        for change in ({"source": "/other"}, {"target": "c" * 40}):
            with self.subTest(change=change):
                result, request = self.run_helper(**change)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(request)

    def test_never_trusts_an_incomplete_or_ineligible_review(self):
        for change in ({"truncated": True}, {"can_trust": False}):
            with self.subTest(change=change):
                result, request = self.run_helper(**change)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(request)

    def test_already_trusted_fixture_does_not_submit_again(self):
        result, request = self.run_helper(trusted=True, can_trust=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(request)


if __name__ == "__main__":
    unittest.main()
