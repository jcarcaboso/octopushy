#!/usr/bin/env bash
# End to end: the backend, a real agent running OpenTofu, its state kept by the backend, and a
# mock Proxmox API. Provisions the fixture guest, then retires it. OpenTofu runs in rootless
# podman on the runtime's default network unless E2E_TOOL_RUNTIME says docker or local.
# Needs the development PostgreSQL (`just dev-db`), in E2E_DB_CONTAINER_ENGINE (podman by
# default). Leaves logs in .dev/e2e.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/e2e
api=http://127.0.0.1:18080
pg=("${E2E_DB_CONTAINER_ENGINE:-podman}" exec octopushy-dev-pg psql -q -U octopushy -d octopushy -v ON_ERROR_STOP=1)
pids=()

log() { printf '\n== %s\n' "$*"; }
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap cleanup EXIT

rm -rf "$work" && mkdir -p "$work"

log "Building"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin octopushy --bin mock-proxmox)

log "Databases"
"${pg[@]}" -c "DROP DATABASE IF EXISTS e2e_octopushy" -c "CREATE DATABASE e2e_octopushy"

log "Lab repository"
cp -r "$root/tests/e2e/lab" "$work/lab"
git -C "$work/lab" init -q -b main
git -C "$work/lab" add -A
git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "Initial definitions"

log "Mock Proxmox"
"$root/agent/target/debug/mock-proxmox" --listen 127.0.0.1:18006 --fixture "$root/tests/e2e/proxmox.json" \
  --tls-self-signed >"$work/mock.log" 2>&1 &
pids+=($!)

log "Backend"
server_bin=$root/backend/src/Octopushy.Server/bin/Debug/net10.0
(
  cd "$server_bin"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=e2e_octopushy;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" \
  Octopushy__ApiPort=18080 \
  Octopushy__AgentChannel__Port=18443 \
  Octopushy__AgentChannel__BindAddress=127.0.0.1 \
  Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:18443 \
  Octopushy__MigrateOnStart=true \
  Octopushy__Scheduling__VerifyWaitSeconds=30 \
  exec ./octopushy-server
) >"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
curl -sf "$api/api/health" >/dev/null || { echo "The backend did not start; see $work/backend.log"; exit 1; }

jar=$work/cookies
call() { curl -sf -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
source "$root/tests/installation-trust/fixture.sh"

log "Account, repository, variables"
code=$(tr -d '\n' <"$work/server/setup-code")
call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"an end-to-end password\"}" >/dev/null
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/read-only-token","value":"octopushy-reader@pve!e2e-read-only=00000000-0000-4000-8000-0000000000e1","visibility":"secret"}'
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/privileged-token","value":"octopushy-writer@pve!e2e-privileged=00000000-0000-4000-8000-0000000000e2","visibility":"secret"}'
call -X PUT "$api/api/repository" -d "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null

log "Agent"
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"e2e-agent","labels":["e2e"]}' | jq -r .token)
OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
  --server https://127.0.0.1:18443 --name e2e-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
# No host networking: the state reaches OpenTofu through the workspace, not over a network.
"$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" --tool-runtime "${E2E_TOOL_RUNTIME:-podman}" \
  >"$work/agent.log" 2>&1 &
pids+=($!)

guest_status() {
  # Empty until the first revision is loaded.
  call "$api/api/inventory" | jq -r '[.labs[]?.installations[]?.guests[]? | select(.address=="e2e/korriban/e2e-vm-01") | .status][0] // "absent"'
}
wait_for() {
  local want=$1 status=""
  for _ in $(seq 1 120); do
    status=$(guest_status)
    [[ $status == "$want" ]] && { echo "guest is $status"; return 0; }
    [[ $status == failed || $status == uncertain || $status == blocked ]] && break
    sleep 2
  done
  echo "Expected the guest to be $want, but it is $status."
  call "$api/api/runs?limit=5" | jq '.[] | {number, operation, status, detail, currentStep}'
  exit 1
}

log "Provisioning"
call -X POST "$api/api/repository/sync" >/dev/null
trust_fixture_installations "$work/lab" "$(git -C "$work/lab" rev-parse HEAD)"
wait_for in-sync
run=$(call "$api/api/runs?limit=20" | jq -r '[.[] | select(.operation=="provision")][0].id')
call "$api/api/runs/$run" | jq -r '.steps[] | "\(.name): \(.status)"'
installation=$(call "$api/api/inventory" | jq '.labs[0].installations[0] | {observedAt, unmanagedCount, hosts: [.hosts[].name]}')
echo "$installation"
[[ $(jq -r .unmanagedCount <<<"$installation") == 1 ]] || { echo "Expected one unmanaged VM."; exit 1; }
[[ $(call "$api/api/runs?limit=500" | jq '[.[] | select(.operation=="observe")] | length') == 0 ]] \
  || { echo "Observing an installation must not start runs."; exit 1; }
guest=$(call "$api/api/inventory" | jq -r '[.labs[].installations[].guests[] | select(.address=="e2e/korriban/e2e-vm-01")][0].id')
state=$(call "$api/api/guests/$guest/state")
echo "$state" | jq '{headVersion, lockHeld, versions: [.versions[] | {version, serial, resources}]}'
[[ $(jq -r .headVersion <<<"$state") == 1 && $(jq '.versions[0].resources' <<<"$state") -gt 0 ]] \
  || { echo "The guest's state is missing."; exit 1; }
[[ $(jq -r .lockHeld <<<"$state") == false ]] || { echo "The state lock is still held."; exit 1; }

log "Retiring"
printf 'lifecycle: retired\n' >>"$work/lab/labs/e2e/proxmox/korriban/guests/e2e-vm-01/guest.yaml"
git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -am "Retire e2e-vm-01"
# The mock does not run OpenTofu's provider, so the VM disappears by hand.
curl -sk -X DELETE https://127.0.0.1:18006/mock/vms/310 >/dev/null
call -X POST "$api/api/repository/sync" >/dev/null
wait_for retired
run=$(call "$api/api/runs?limit=20" | jq -r '[.[] | select(.operation=="retire")][0].id')
call "$api/api/runs/$run" | jq -r '.steps[] | "\(.name): \(.status)"'
state=$(call "$api/api/guests/$guest/state")
[[ $(jq -r .headVersion <<<"$state") == 2 && $(jq '.versions[0].resources' <<<"$state") == 0 ]] \
  || { echo "The retirement's state is missing; the guest's earlier state must stay as a version."; exit 1; }

log "Passed"
