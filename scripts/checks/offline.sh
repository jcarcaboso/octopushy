#!/usr/bin/env bash
# Static checks and disposable Python mocks only. Never invoke the live harnesses.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.."
export PYTHONDONTWRITEBYTECODE=1
# The archived suite otherwise opts into running a real OpenTofu binary.
unset OCTOPUSHY_TEST_TOFU

python3 -m unittest discover -s tests/fedora -p 'test_*.py' -v
python3 -m unittest discover -s tests/remote-worker -p 'test_*.py' -v
python3 tests/technitium/check.py -v
(cd old && python3 -m unittest discover -s tests -v)

while IFS= read -r -d '' script; do
    bash -n "$script"
done < <(git ls-files -z -- '*.sh')
