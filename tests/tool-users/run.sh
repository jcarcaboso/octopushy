#!/usr/bin/env bash
# The agent's root-only tests: each attempt's tools as a user of its own (tools/users.rs). They
# need root and the tool users, so they run as root in a container of tests/tool-users/Containerfile:
#   tests/tool-users/run.sh [test name filter]
# With AGENT_IMAGE set (a build of agent/Containerfile), it also checks that image: it starts as
# root, has the tool users, has no folder everyone can write to that the agent does not sweep,
# and probes its tools as nobody.
# ENGINE picks docker or podman (docker when it is installed). SCRATCH holds the build cache
# (.dev/tool-users by default).
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
engine=${ENGINE:-$(command -v docker >/dev/null && echo docker || echo podman)}
scratch=${SCRATCH:-$root/.dev/tool-users}
image=localhost/octopushy-tool-users-test:dev
mkdir -p "$scratch/target" "$scratch/registry"

echo "== Building the test image ($engine)"
"$engine" build -q -f "$root/tests/tool-users/Containerfile" -t "$image" "$root" >/dev/null

echo "== Root-only tests, as root"
# No --security-opt no-new-privileges: the tests check that the agent sets it on the tool itself.
"$engine" run --rm --user 0 \
  -v "$root:/src:ro" -v "$scratch/target:/target" -v "$scratch/registry:/usr/local/cargo/registry" \
  -e CARGO_TARGET_DIR=/target -w /src/agent "$image" \
  cargo test --locked -p octopushy --test tool_users -- --ignored --test-threads=1 "$@"

if [[ -n ${AGENT_IMAGE:-} ]]; then
  echo "== The agent image $AGENT_IMAGE"
  # shellcheck disable=SC2016 # the script runs in the image
  "$engine" run --rm --entrypoint /bin/sh "$AGENT_IMAGE" -euc '
    [ "$(id -u)" = 0 ] || { echo "the image does not start as root" >&2; exit 1; }
    for n in 0 15; do id "octopushy-tool-$n" >/dev/null; done
    [ "$(stat -c %u /nix/store)" = 10001 ] || { echo "/nix/store is not 10001s" >&2; exit 1; }
    # Every folder everyone can write to must be one the agent sweeps (SHARED_WRITABLE).
    extra=$(find / -xdev \( -path /proc -o -path /sys \) -prune -o -type d -perm -0002 -print \
      | grep -vxE "/tmp|/var/tmp|/dev/shm|/dev/mqueue|/run/lock" || true)
    [ -z "$extra" ] || { echo "folders everyone can write to: $extra" >&2; exit 1; }
    # Setuid and setgid programs are harmless under no_new_privs; listed for the record.
    find / -xdev \( -path /proc -o -path /sys \) -prune -o -type f -perm /6000 -print
    # Probes run as nobody (Nix as the store owner): each tool must still answer. Nix may lack the
    # free space it asks for, so only its probe is looked at.
    out=$(octopushy agent doctor --tool-runtime local || true)
    printf "%s\n" "$out"
    for tool in opentofu ansible; do
      printf "%s\n" "$out" | grep -q "^$tool *available" || { echo "$tool is not available" >&2; exit 1; }
    done
    printf "%s\n" "$out" | grep -qE "^nix *(available|UNAVAILABLE: the Nix store has)" || { echo "nix does not run" >&2; exit 1; }
  '
fi
echo "ok"
