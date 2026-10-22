#!/usr/bin/env bash
# The full VM lifecycle against a real Proxmox host, driven through Octopushy: create a VM,
# resize it, then delete it. Everything goes through the backend's API and a real agent
# running OpenTofu (bpg/proxmox) in rootless podman; the state lives in the development
# PostgreSQL. Every plan waits for approval, and the script checks each plan before
# approving it: creating adds resources, resizing must not recreate the VM, deleting
# removes it.
#
# Defaults target the `umbrel` host from the nodes repository. Override with:
#   PVE_ENDPOINT PVE_NODE PVE_IMAGES PVE_DISKS PVE_BRIDGE
#   PVE_TOKEN_FILE   the API token, USER@REALM!ID=SECRET (default .dev/secrets/umbrel-proxmox-token)
#   TEST_VMID        default: the first VMID from 9900 to 9999 that Proxmox does not report
#   TEST_NAME        default opy-e2e-01
#   UI=1             also serve the web interface at http://127.0.0.1:5174 while it runs
#   KEEP=1           leave everything running at the end, until Ctrl-C
#   CLEANUP=1        only retire a VM left behind by an interrupted run, reusing its state
#
# Needs the development PostgreSQL (`just dev-db`). Logs and state go to .dev/proxmox-e2e.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/proxmox-e2e
api=http://127.0.0.1:18080
endpoint=${PVE_ENDPOINT:-https://10.1.0.85:8006/}
node=${PVE_NODE:-umbrel}
images=${PVE_IMAGES:-local}
disks=${PVE_DISKS:-local-lvm}
bridge=${PVE_BRIDGE:-vmbr0}
token_file=${PVE_TOKEN_FILE:-$root/.dev/secrets/umbrel-proxmox-token}
name=${TEST_NAME:-opy-e2e-01}
lab=$work/lab
guest_dir=$lab/labs/home/proxmox/$node/guests/$name
address=home/$node/$name
pg=(podman exec octopushy-dev-pg psql -q -U octopushy -d octopushy -v ON_ERROR_STOP=1)
pids=()
created=0

log() { printf '\n== %s\n' "$*"; }
fail() {
  echo "FAILED: $*" >&2
  if [[ $created == 1 ]]; then
    echo "The VM $name may still exist on $node. Retire it with: CLEANUP=1 $0" >&2
  fi
  exit 1
}
stop() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap stop EXIT

[[ -s $token_file ]] || fail "No API token in $token_file (see tests/proxmox/README.md)."
[[ $(stat -c %a "$token_file") =~ ^[46]00$ ]] || fail "$token_file must be readable by you only (chmod 600)."
[[ $name =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] || fail "TEST_NAME must be a valid id."

if [[ ${CLEANUP:-0} != 1 ]]; then
  [[ ! -f $work/vmid ]] || fail "A previous run left state in $work (VMID $(<"$work/vmid")). Retire it with CLEANUP=1 first, or delete $work if the VM is gone."
  rm -rf "$work" && mkdir -p "$work"
  "${pg[@]}" -c "DROP DATABASE IF EXISTS proxmox_e2e" -c "DROP DATABASE IF EXISTS proxmox_e2e_state" \
    -c "CREATE DATABASE proxmox_e2e" -c "CREATE DATABASE proxmox_e2e_state"
else
  [[ -f $work/vmid ]] || fail "Nothing to clean up: $work has no VM recorded."
fi

log "Building"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin octopushy)

log "Backend"
(
  cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=proxmox_e2e;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" Octopushy__ApiPort=18080 \
  Octopushy__AgentChannel__Port=18443 Octopushy__AgentChannel__BindAddress=127.0.0.1 \
  Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:18443 Octopushy__MigrateOnStart=true \
  exec ./octopushy-server
) >>"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
curl -sf "$api/api/health" >/dev/null || fail "The backend did not start; see $work/backend.log."

jar=$work/cookies
call() { curl -sS -f -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
commit() {
  git -C "$lab" add -A
  git -C "$lab" -c user.name=octopushy-e2e -c user.email=e2e@example.invalid commit -q --allow-empty -m "$1"
  call -X POST "$api/api/repository/sync" >/dev/null
}

if [[ ${CLEANUP:-0} != 1 ]]; then
  log "Account, variables, repository, agent"
  code=$(tr -d '\n' <"$work/server/setup-code")
  call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"proxmox-e2e-password\"}" >/dev/null
  # The token goes from its file to the store on standard input; it is never printed.
  jq -n --rawfile token "$token_file" '{key: "proxmox/umbrel/api-token", value: ($token | rtrimstr("\n")), visibility: "secret", description: "Proxmox API token for umbrel"}' |
    call -X PUT "$api/api/variables" --data-binary @-
  call -X PUT "$api/api/variables" -d '{"key":"opentofu/pg","value":"postgres://octopushy:octopushy@127.0.0.1:55432/proxmox_e2e_state?sslmode=disable","visibility":"secret","description":"OpenTofu state for the test"}'

  mkdir -p "$lab/labs/home/proxmox/$node" "$lab/recipes" "$lab/templates" "$lab/modules/opentofu"
  cp -r "$root/examples/lab/recipes/proxmox-vm" "$lab/recipes/"
  cp -r "$root/examples/lab/templates/fedora-44" "$lab/templates/"
  cp -r "$root/examples/lab/modules/opentofu/proxmox-vm" "$lab/modules/opentofu/"
  cat >"$lab/octopushy.yaml" <<YAML
schemaVersion: 1
stateBackends:
  postgres:
    type: pg
    config: { schema_name: octopushy_state }
    workspace: "{stack}"
    env:
      PG_CONN_STR: { store: opentofu/pg }
YAML
  cat >"$lab/labs/home/lab.yaml" <<YAML
schemaVersion: 1
name: Home
execution:
  agentLabels: [proxmox-e2e]
policy:
  approval: manual                # the script reviews every plan before approving it
YAML
  cat >"$lab/labs/home/proxmox/$node/installation.yaml" <<YAML
schemaVersion: 1
type: standalone
connection:
  endpoint: $endpoint
  tls: { verify: false }
  token: { store: proxmox/umbrel/api-token }
hosts:
  $node:
    datastores: { images: $images, disks: $disks }
    bridges: [$bridge]
    limits: { concurrentOperations: 1 }
YAML
  git -C "$lab" init -q -b main
  commit "The $node installation"
  call -X PUT "$api/api/repository" -d "{\"url\":\"$lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null

  token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"proxmox-e2e","labels":["proxmox-e2e"]}' | jq -r .token)
  OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
    --server https://127.0.0.1:18443 --name proxmox-e2e --state-dir "$work/agent" >"$work/enroll.log" 2>&1
fi

"$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" --tool-runtime podman \
  --container-network host >>"$work/agent.log" 2>&1 &
pids+=($!)
if [[ ${UI:-0} == 1 ]]; then
  (cd "$root/frontend" && OCTOPUSHY_API=$api exec npx vite --port 5174 --strictPort) >"$work/frontend.log" 2>&1 &
  pids+=($!)
  echo "Web interface: http://127.0.0.1:5174 (user e2e, password proxmox-e2e-password)"
fi
call -X POST "$api/api/auth/login" -d '{"username":"e2e","password":"proxmox-e2e-password"}' >/dev/null

installation_id() { call "$api/api/inventory" | jq -r --arg a "home/$node" '[.labs[]?.installations[]? | select(.address==$a) | .id][0] // empty'; }
guest_id() { call "$api/api/inventory" | jq -r --arg a "$address" '[.labs[]?.installations[]?.guests[]? | select(.address==$a) | .id][0] // empty'; }

# Waits for the newest run of an operation on a resource to reach one of the given statuses.
wait_run() {
  local resource=$1 operation=$2 want=$3 minutes=${4:-25} run="" status=""
  for _ in $(seq 1 $((minutes * 12))); do
    run=$(call "$api/api/runs?resourceId=$resource&limit=10" | jq -c --arg op "$operation" '[.[] | select(.operation==$op)][0] // empty')
    status=$(jq -r '.status // empty' <<<"$run")
    if [[ -n $status && ",$want," == *",$status,"* ]]; then
      jq -r .id <<<"$run"
      return 0
    fi
    if [[ $status =~ ^(failed|uncertain|cancelled|blocked)$ ]]; then
      echo "Run #$(jq -r .number <<<"$run") is $status: $(jq -r '.detail // ""' <<<"$run")" >&2
      show_run "$(jq -r .id <<<"$run")" >&2
      return 1
    fi
    sleep 5
  done
  echo "Timed out waiting for a $operation run to become $want (last: ${status:-none})." >&2
  return 1
}
show_run() {
  call "$api/api/runs/$1" | jq -r '.steps[] | "  \(.name): \(.status)\(if .detail then " — " + .detail else "" end)"'
}
# Approves the run's plan after checking its counts with a jq condition.
review_and_approve() {
  local run=$1 condition=$2 detail step sha counts
  detail=$(call "$api/api/runs/$run")
  step=$(jq -r '.steps[] | select(.name=="approval") | .id' <<<"$detail")
  sha=$(jq -r '.steps[] | select(.name=="approval") | .approval.planSha256' <<<"$detail")
  counts=$(jq -c '.steps[] | select(.name=="plan") | .plan.counts' <<<"$detail")
  echo "  plan: $counts"
  jq -r '.steps[] | select(.name=="plan") | .plan.changes[] | "    \(.actions | join("/")) \(.address)"' <<<"$detail"
  if ! jq -e "$condition" <<<"$counts" >/dev/null; then
    call -X POST "$api/api/runs/$run/steps/$step/reject" -d '{"note":"The end-to-end test did not expect this plan."}' >/dev/null
    fail "The plan does not match what this step expects ($condition)."
  fi
  call -X POST "$api/api/runs/$run/steps/$step/approve" -d "{\"planSha256\":\"$sha\",\"note\":\"Reviewed by the end-to-end test.\"}" >/dev/null
}
# Observes the installation now and prints what Proxmox reports for the test VMID.
# Fails rather than printing nothing, so an observation that did not happen never reads as "absent".
observe_vm() {
  local installation=$1 vmid=$2 run="" status=""
  for _ in $(seq 1 10); do
    # Refused while another observe run is active; try again shortly.
    run=$(call -X POST "$api/api/installations/$installation/observe" 2>/dev/null | jq -r .runId) && break
    sleep 3
  done
  [[ -n $run ]] || { echo "Could not start an observe run." >&2; return 1; }
  for _ in $(seq 1 60); do
    status=$(call "$api/api/runs/$run" | jq -r .run.status)
    case $status in
      succeeded) break ;;
      failed | blocked | cancelled) show_run "$run" >&2; echo "Observing $node failed." >&2; return 1 ;;
    esac
    sleep 2
  done
  [[ $status == succeeded ]] || { echo "Observing $node timed out." >&2; return 1; }
  call "$api/api/installations/$installation" | jq -c --argjson v "$vmid" '[.observedGuests[] | select(.vmid==$v)][0] // empty'
}
write_guest() {
  local cores=$1 memory=$2 disk=$3 lifecycle=${4:-active}
  mkdir -p "$guest_dir"
  cat >"$guest_dir/guest.yaml" <<YAML
schemaVersion: 1
type: vm
lifecycle: $lifecycle
host: $node
vmid: $(<"$work/vmid")
template: fedora-44
hardware:
  cores: $cores
  memory: $memory
  disks:
    - { size: $disk }
network:
  bridge: $bridge
  mode: dhcp
provision:
  recipe: proxmox-vm
  state: { backend: postgres }
access:
  sshAuthorizedKeys:
    - $(<"$work/ssh-key.pub")
YAML
}

log "Observing $node"
for _ in $(seq 1 60); do installation=$(installation_id); [[ -n $installation ]] && break; sleep 2; done
[[ -n ${installation:-} ]] || fail "The installation never appeared; see $work/backend.log."
wait_run "$installation" observe succeeded 3 >/dev/null || fail "Octopushy could not observe $node with this token."
summary=$(call "$api/api/installations/$installation")
jq -r '"  Proxmox \(.installation.proxmoxVersion), \(.observedGuests | length) VMs visible"' <<<"$summary"
jq -r '.installation.hosts[] | "  host \(.name): online=\(.online), memory \((.memoryBytes // 0) / 1073741824 | floor)/\((.maxMemoryBytes // 0) / 1073741824 | floor) GiB"' <<<"$summary"

if [[ ${CLEANUP:-0} != 1 ]]; then
  free=$(jq --arg n "$node" '[.installation.hosts[] | select(.name==$n) | (.maxMemoryBytes - .memoryBytes)][0] // 0' <<<"$summary")
  (( free > 5 * 1024 * 1024 * 1024 )) || fail "$node has less than 5 GiB of free memory; the test needs 4 GiB."
  used=$(jq -c '[.observedGuests[].vmid]' <<<"$summary")
  if [[ -n ${TEST_VMID:-} ]]; then
    vmid=$TEST_VMID
    jq -e --argjson v "$vmid" 'any(.[]; . == $v) | not' <<<"$used" >/dev/null || fail "VMID $vmid is in use on $node."
  else
    vmid=$(jq -r '. as $used | [range(9900; 10000) | select(. as $v | $used | any(.[]; . == $v) | not)][0]' <<<"$used")
  fi
  echo "$vmid" >"$work/vmid"
  echo "  test VM: $name, VMID $vmid"
  ssh-keygen -q -t ed25519 -N '' -C "octopushy-e2e" -f "$work/ssh-key"

  log "1. Create: 2 cores, 2 GiB, 16 GiB disk"
  write_guest 2 2048 16
  commit "Create $name"
  for _ in $(seq 1 30); do guest=$(guest_id); [[ -n $guest ]] && break; sleep 2; done
  [[ -n ${guest:-} ]] || fail "The guest never appeared; see the repository page for definition errors."
  run=$(wait_run "$guest" provision awaiting-approval 15) || fail "The create plan did not finish."
  created=1
  review_and_approve "$run" '.create >= 2 and .delete == 0 and .replace == 0'
  run=$(wait_run "$guest" provision succeeded 30) || fail "Creating the VM failed."
  show_run "$run"
  vm=$(observe_vm "$installation" "$(<"$work/vmid")") || fail "Could not observe the VM."
  echo "  Proxmox reports: $vm"
  jq -e '.status == "running" and .maxCpu == 2 and .maxMemoryBytes == 2147483648' <<<"$vm" >/dev/null ||
    fail "The VM does not match: expected running, 2 cores, 2 GiB."

  log "2. Resize: 4 cores, 4 GiB, 20 GiB disk"
  write_guest 4 4096 20
  commit "Resize $name"
  run=$(wait_run "$guest" provision awaiting-approval 15) || fail "The resize plan did not finish."
  review_and_approve "$run" '.update >= 1 and .delete == 0 and .replace == 0'
  run=$(wait_run "$guest" provision succeeded 20) || fail "Resizing the VM failed."
  show_run "$run"
  vm=$(observe_vm "$installation" "$(<"$work/vmid")") || fail "Could not observe the VM."
  echo "  Proxmox reports: $vm"
  jq -e '.status == "running" and .maxCpu == 4 and .maxMemoryBytes == 4294967296' <<<"$vm" >/dev/null ||
    fail "The VM does not match: expected running, 4 cores, 4 GiB."
else
  created=1
  guest=$(guest_id)
fi

log "3. Delete"
if [[ ! -f $guest_dir/guest.yaml ]]; then fail "No guest definition to retire in $guest_dir."; fi
sed -i 's/^lifecycle: .*/lifecycle: retired/' "$guest_dir/guest.yaml"
commit "Retire $name"
if [[ ${CLEANUP:-0} == 1 ]]; then
  # When the definition was already retired, nothing changed in Git: start the retire run by hand.
  # A run the commit started makes this a harmless conflict.
  sleep 5
  call -X POST "$api/api/guests/$guest/plan" >/dev/null 2>&1 || true
fi
run=$(wait_run "$guest" retire awaiting-approval,succeeded 15) || fail "The delete plan did not finish."
if [[ $(call "$api/api/runs/$run" | jq -r .run.status) == awaiting-approval ]]; then
  review_and_approve "$run" '.delete >= 1 and .create == 0'
fi
run=$(wait_run "$guest" retire succeeded 15) || fail "Deleting the VM failed."
show_run "$run"
vm=$(observe_vm "$installation" "$(<"$work/vmid")") || fail "Could not observe $node after the delete."
[[ -z $vm ]] || fail "Proxmox still reports VMID $(<"$work/vmid"): $vm"
echo "  VMID $(<"$work/vmid") is gone from $node."
rm -f "$work/vmid"
created=0

if [[ ${CLEANUP:-0} == 1 ]]; then
  log "Cleaned up: $name is retired and gone from $node"
else
  log "Passed: created, resized, and deleted $name on $node"
fi
if [[ ${KEEP:-0} == 1 ]]; then
  echo "Still running (Ctrl-C to stop). Logs in $work."
  wait
fi
