#!/usr/bin/env bash
# Built templates end to end, by hand: the backend, a mock Proxmox API, and the agent's container image,
# which builds a real NixOS image with the Nix in it. Checks that
#   - a sync queues the build of a built template a guest uses, and the guest waits for it;
#   - only an agent advertising nix takes it, and it uploads the image, which the server keeps;
#   - the guest's provision run is then proposed, and its plan fetches the image into
#     .octopushy-images/ and plans its upload to Proxmox.
#
#   OCTOPUSHY_LAB=<octopushy-lab checkout with nixos-host> tests/built-templates/run.sh
#
# Needs the development PostgreSQL (`just dev-db`; E2E_DB_CONTAINER_ENGINE, podman by default), podman, the
# agent image (AGENT_IMAGE, localhost/octopushy-agent:dev by default: podman build -f agent/Containerfile .),
# and the network: the agent fetches nixpkgs and the binary cache. NIX_VOLUME keeps the agent's Nix store
# between runs. The build takes minutes. Leaves logs in .dev/built-templates.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
: "${OCTOPUSHY_LAB:?set OCTOPUSHY_LAB to an octopushy-lab checkout whose HEAD has workflows/nixos-host}"
work=$root/.dev/built-templates
api=http://127.0.0.1:18080
image=${AGENT_IMAGE:-localhost/octopushy-agent:dev}
nix_volume=${NIX_VOLUME:-octopushy-built-templates-nix}
pg=("${E2E_DB_CONTAINER_ENGINE:-podman}" exec octopushy-dev-pg psql -q -U octopushy -d octopushy -v ON_ERROR_STOP=1)
pids=()
log() { printf '\n== %s\n' "$*"; }
fail() { printf '\033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }
pass() { printf '\033[32mok\033[0m   %s\n' "$*"; }
cleanup() {
  podman rm -f octopushy-built-templates-agent >/dev/null 2>&1 || true
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap cleanup EXIT
rm -rf "$work" && mkdir -p "$work"

log "Building"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin mock-proxmox)
"${pg[@]}" -c "DROP DATABASE IF EXISTS built_templates" -c "CREATE DATABASE built_templates"

log "Workflows repository: octopushy-lab's HEAD, with its versions as release tags"
git clone -q --bare "$OCTOPUSHY_LAB" "$work/workflows.git"
head=$(git -C "$OCTOPUSHY_LAB" rev-parse HEAD)
for wf in "$OCTOPUSHY_LAB"/workflows/*/; do
  id=$(basename "$wf"); version=$(sed -n 's/^version: "\(.*\)".*/\1/p' "$wf/workflow.yaml")
  git -C "$work/workflows.git" rev-parse -q --verify "refs/tags/$id/v$version" >/dev/null \
    || git -C "$work/workflows.git" tag "$id/v$version" "$head"
done

log "Lab repository"
mkdir -p "$work/lab/labs/e2e/proxmox/korriban/guests/e2e-nixos-01" "$work/lab/templates/nixos-base"
# The fixture lab asks for agents labelled e2e: runs honour it, builds need no label.
cp "$root/tests/e2e/lab/labs/e2e/lab.yaml" "$work/lab/labs/e2e/"
cp "$root/tests/e2e/lab/labs/e2e/proxmox/korriban/installation.yaml" "$work/lab/labs/e2e/proxmox/korriban/"
printf 'schemaVersion: 1\nworkflows:\n  repository: %s\n' "$work/workflows.git" >"$work/lab/octopushy.yaml"
cp "$OCTOPUSHY_LAB/modules/nixos/flake.lock" "$work/lab/templates/nixos-base/flake.lock"
cat >"$work/lab/templates/nixos-base/template.yaml" <<'YAML'
schemaVersion: 1
os: nixos
source:
  build: { workflow: nixos-host@1, output: base-image, lock: flake.lock }
availableOn: [e2e/korriban]
YAML
cat >"$work/lab/labs/e2e/proxmox/korriban/guests/e2e-nixos-01/guest.yaml" <<'YAML'
schemaVersion: 1
type: vm
host: korriban
vmid: 330
template: nixos-base
workflow: nixos-host@1
hardware: { cores: 2, memory: 2048, disks: [{ size: 16 }] }
network: { bridge: vmbr0, mode: static, address: 10.1.0.33/24, gateway: 10.1.0.1 }
access:
  user: ops
  hostKey: { store: proxmox/korriban/e2e-nixos-01/host-key }
YAML
git -C "$work/lab" init -q -b main && git -C "$work/lab" add -A
git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "A guest on a built template"

log "Mock Proxmox and backend"
"$root/agent/target/debug/mock-proxmox" --listen 127.0.0.1:18006 --fixture "$root/tests/e2e/proxmox.json" \
  --tls-self-signed >"$work/mock.log" 2>&1 &
pids+=($!)
server_bin=$root/backend/src/Octopushy.Server/bin/Debug/net10.0
(
  cd "$server_bin"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=built_templates;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" Octopushy__ApiPort=18080 Octopushy__AgentChannel__Port=18443 \
  Octopushy__AgentChannel__BindAddress=127.0.0.1 Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:18443 \
  Octopushy__MigrateOnStart=true exec ./octopushy-server
) >"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
curl -sf "$api/api/health" >/dev/null || fail "the backend did not start; see $work/backend.log"
jar=$work/cookies
call() { curl -sf -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
source "$root/tests/installation-trust/fixture.sh"
code=$(tr -d '\n' <"$work/server/setup-code")
call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"an end-to-end password\"}" >/dev/null
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/read-only-token","value":"octopushy-reader@pve!e2e-read-only=00000000-0000-4000-8000-0000000000e1","visibility":"secret"}'
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/privileged-token","value":"octopushy-writer@pve!e2e-privileged=00000000-0000-4000-8000-0000000000e2","visibility":"secret"}'
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/e2e-nixos-01/host-key","value":"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY","visibility":"plain"}'
call -X PUT "$api/api/repository" -d "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null
call -X POST "$api/api/repository/sync" >/dev/null
trust_fixture_installations "$work/lab" "$(git -C "$work/lab" rev-parse HEAD)"

log "The template, before any agent"
for _ in $(seq 1 30); do
  templates=$(call "$api/api/templates"); [[ $(jq -r '.[] | select(.id=="nixos-base") | .build.status // empty' <<<"$templates") != "" ]] && break; sleep 2
done
jq '.[] | select(.id=="nixos-base")' <<<"$templates" | tee "$work/template-before.json"
[[ $(jq -r '.[] | select(.id=="nixos-base") | .build.status' <<<"$templates") == queued ]] || fail "the build is not queued"
pass "a sync queues the build of a built template a guest uses"
call "$api/api/inventory" | jq -r '.. | objects | select(.address? == "e2e/korriban/e2e-nixos-01")' >"$work/guest-before.json"
grep -q 'Waiting for the image of template nixos-base' "$work/guest-before.json" || { cat "$work/guest-before.json"; fail "the guest does not say it waits for its image"; }
pass "the guest waits for the image"

log "Agent (its image, Nix and all)"
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"nix-agent","labels":["e2e"]}' | jq -r .token)
podman volume create "$nix_volume" >/dev/null 2>&1 || true
podman volume rm -f octopushy-built-templates-state >/dev/null 2>&1 || true
podman run --rm --network host --user 10001 -e OCTOPUSHY_ENROLLMENT_TOKEN="$token" \
  -v octopushy-built-templates-state:/var/lib/octopushy-agent "$image" \
  agent enroll --server https://127.0.0.1:18443 --name nix-agent >"$work/enroll.log" 2>&1 || { cat "$work/enroll.log"; fail "enroll"; }
podman run -d --name octopushy-built-templates-agent --network host --user 10001 --read-only --tmpfs /tmp:size=128m,mode=1777 \
  --cap-drop=all --security-opt no-new-privileges -v octopushy-built-templates-state:/var/lib/octopushy-agent \
  -v "$nix_volume:/nix" "$image" >/dev/null
for _ in $(seq 1 60); do
  call "$api/api/agents" | jq -e '.[] | select(.name=="nix-agent" and .connected) | .executors | index("nix")' >/dev/null && break; sleep 2
done
call "$api/api/agents" | jq '.[] | select(.name=="nix-agent") | {connected, executors}' | tee "$work/agent.json"
jq -e '.executors | index("nix")' "$work/agent.json" >/dev/null || { podman logs octopushy-built-templates-agent | tail -20; fail "the agent does not advertise nix"; }
pass "the agent, read-only with /nix a volume, advertises nix"

log "The build (minutes)"
for _ in $(seq 1 360); do
  status=$(call "$api/api/templates" | jq -r '.[] | select(.id=="nixos-base") | .build.status')
  [[ $status == built || $status == failed ]] && break; sleep 10
done
call "$api/api/templates" | jq '.[] | select(.id=="nixos-base")' | tee "$work/template-after.json"
[[ $status == built ]] || { podman logs octopushy-built-templates-agent | tail -40; fail "the build ended $status"; }
sha=$(jq -r '.build.sha256' "$work/template-after.json")
stored=$(find "$work/server/images" -maxdepth 1 -type f -name "$sha*" | head -1)
[[ -n $stored && $(sha256sum "$stored" | cut -d' ' -f1) == "$sha" ]] || fail "the server does not hold the image under its SHA-256"
pass "the agent built the image and uploaded it; the server keeps it ($(du -h "$stored" | cut -f1), $sha)"

log "The guest's provision run"
for _ in $(seq 1 60); do
  run=$(call "$api/api/runs?limit=20" | jq -r '[.[] | select(.operation=="provision" and .address=="e2e/korriban/e2e-nixos-01")][0].id // empty')
  [[ -n $run ]] && { steps=$(call "$api/api/runs/$run"); jq -e '.steps[] | select(.name=="plan" and (.status=="succeeded" or .status=="failed"))' <<<"$steps" >/dev/null && break; }
  sleep 5
done
[[ -n ${run:-} ]] || fail "no provision run was proposed once the image was built"
call "$api/api/runs/$run" | tee "$work/run.json" | jq -r '.steps[] | "\(.name): \(.status)"'
plan=$(jq -r '.steps[] | select(.name=="plan") | .attempts[-1].id' "$work/run.json")
call "$api/api/attempts/$plan/log?limit=5000" | jq -r '.lines[].text' >"$work/plan.log"
jq -e '.steps[] | select(.name=="plan") | .status == "succeeded"' "$work/run.json" >/dev/null || { tail -40 "$work/plan.log"; fail "the plan failed"; }
grep -q 'proxmox_virtual_environment_file.built_image\[0\] will be created' "$work/plan.log" || { tail -60 "$work/plan.log"; fail "the plan does not upload the built image"; }
grep -q "\./\.octopushy-images/${sha:0:12}-" "$work/plan.log" || fail "the plan does not name the fetched file"
grep -q 'proxmox_download_file' "$work/plan.log" && fail "the plan downloads something"
pass "the plan fetched the image into .octopushy-images/ and plans its upload, with no download"
printf '\033[1m== all passed\033[0m (logs in %s)\n' "$work"
