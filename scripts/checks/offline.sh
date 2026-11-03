#!/usr/bin/env bash
# Static checks and disposable Python mocks only. Never invoke the live harnesses.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.."
export PYTHONDONTWRITEBYTECODE=1

# The server's default image has to be the one agent/ansible builds.
tag=$(sh agent/ansible/tag.sh)
grep -q "skorcius/octopushy:$tag\"" backend/src/Octopushy.Server/Options/OctopushyOptions.cs \
    || { echo "AnsibleImage in OctopushyOptions.cs is not $tag" >&2; exit 1; }

python3 -m unittest discover -s tests/ansible -p 'test_*.py' -v
python3 -m unittest discover -s tests/remote-worker -p 'test_*.py' -v
python3 -m unittest discover -s tests/e2e -p 'test_*.py' -v
python3 -m unittest discover -s tests/installation-trust -p 'test_*.py' -v
python3 tests/technitium/check.py -v

while IFS= read -r -d '' script; do
    bash -n "$script"
done < <(git ls-files -z -- '*.sh')
