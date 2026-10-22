#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
# path: includes new files without staging them in the application's Git index.
# This evaluates module policy and derivations, not a VM or remote activation.
exec nix --extra-experimental-features 'nix-command flakes' flake check \
  --no-update-lock-file --no-build \
  "path:${repo_root}/examples/lab/modules/nixos"
