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
#   FEDORA_WORKFLOW=1 also test the native Fedora service workflow after resizing, before retiring
#   PORTAINER_WORKFLOW=1 use an isolated NixOS service-node fixture (no resize)
#   PORTAINER_TEMPLATE_DIR  reviewed NixOS image template directory for that fixture
#   PORTAINER_CONFIG_DIR    locked native NixOS deployment flake for that fixture
#   KEEP_GUEST=1      after a successful Portainer test, leave its VM/data running
#   RESUME_SERVICE=1  resume that native test on its existing VM/state (requires KEEP_GUEST=1)
#   REMOTE_WORKER=1   reuse a dedicated controller and pre-enrolled remote worker;
#                     see tests/remote-worker/README.md for required explicit endpoints
#
# Local mode needs development PostgreSQL (`just dev-db`); logs/state: .dev/proxmox-e2e.
set -euo pipefail
umask 077

root=$(cd "$(dirname "$0")/../.." && pwd)
remote=${REMOTE_WORKER:-0}
[[ $remote == 0 || $remote == 1 ]] || { echo "REMOTE_WORKER must be 0 or 1." >&2; exit 1; }
work=$root/.dev/proxmox-e2e
api=http://127.0.0.1:18080
api_port=18080
agent_port=18443
database=proxmox_e2e
state_database=proxmox_e2e_state
template=fedora-44
if [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then
  [[ ${FEDORA_WORKFLOW:-0} != 1 ]] || { echo "Choose exactly one service workflow." >&2; exit 1; }
  work=$root/.dev/portainer-vm-e2e
  api_port=18081
  agent_port=18444
  api=http://127.0.0.1:$api_port
  database=portainer_e2e
  state_database=portainer_e2e_state
  template=nixos-service-node
fi
agent_label=proxmox-e2e
if [[ $remote == 1 ]]; then
  # Validate before touching fixtures, credentials, databases, or starting processes.
  work=${REMOTE_WORK_DIR:-$root/.dev/remote-worker-e2e}
  export REMOTE_WORK_DIR=$work
  python3 "$root/tests/remote-worker/config.py" --preflight
  api=${REMOTE_API_URL%/}
  agent_label=$REMOTE_AGENT_LABEL
fi
endpoint=${PVE_ENDPOINT:-https://10.1.0.85:8006/}
node=${PVE_NODE:-umbrel}
images=${PVE_IMAGES:-local}
disks=${PVE_DISKS:-local-lvm}
bridge=${PVE_BRIDGE:-vmbr0}
token_file=${PVE_TOKEN_FILE:-$root/.dev/secrets/umbrel-proxmox-token}
name=${TEST_NAME:-opy-e2e-01}
if [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then name=${TEST_NAME:-opy-service-01}; fi
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
    if [[ $remote == 1 ]]; then
      echo "The VM $name may still exist on $node. Preserve the fixture; rerun with the same remote inputs and CLEANUP=1 only when authorized." >&2
    elif [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then
      echo "The VM $name may still exist on $node. Retire only when authorized: PORTAINER_WORKFLOW=1 CLEANUP=1 $0" >&2
    else
      echo "The VM $name may still exist on $node. Retire it with: CLEANUP=1 $0" >&2
    fi
  fi
  exit 1
}
stop() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap stop EXIT

jar=$work/cookies
if [[ $remote == 1 ]]; then jar=$REMOTE_COOKIE_FILE; fi
cookies=(-b "$jar")
if [[ $remote == 0 ]]; then cookies+=(-c "$jar"); fi
call() { curl -sS -f "${cookies[@]}" -H 'Content-Type: application/json' "$@"; }

[[ -s $token_file ]] || fail "No API token in $token_file (see tests/proxmox/README.md)."
[[ $(stat -c %a "$token_file") =~ ^[46]00$ ]] || fail "$token_file must be readable by you only (chmod 600)."
[[ $name =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] || fail "TEST_NAME must be a valid id."
if [[ ${RESUME_SERVICE:-0} == 1 ]]; then
  [[ ${PORTAINER_WORKFLOW:-0} == 1 && ${KEEP_GUEST:-0} == 1 && ${CLEANUP:-0} != 1 ]] ||
    fail "RESUME_SERVICE requires PORTAINER_WORKFLOW=1 KEEP_GUEST=1 and is not a retirement."
  [[ -s ${PORTAINER_CONFIG_DIR:-/nonexistent}/flake.lock ]] || fail "The retained locked NixOS configuration is required."
fi
if [[ ${FEDORA_WORKFLOW:-0} == 1 && ${CLEANUP:-0} != 1 ]]; then
  [[ -s ${TEST_SSH_KNOWN_HOSTS:-/nonexistent} || ${ALLOW_TEST_SSH_TOFU:-0} == 1 ]] ||
    fail "The Fedora test needs TEST_SSH_KNOWN_HOSTS or explicit ALLOW_TEST_SSH_TOFU=1 (disposable trusted-LAN test only)."
fi

if [[ $remote == 1 ]]; then
  log "Existing controller and remote worker (no local processes or enrollment)"
  curl -sf "$api/api/health" >/dev/null || fail "The configured controller is not healthy."
  call "$api/api/agents" | jq -e --arg id "$REMOTE_AGENT_ID" --arg label "$agent_label" '
    [.[] | select(.labels | index($label))] |
    length == 1 and (.[0] | .id == $id and .connected and (.disabled | not) and
      .runningAttempts == 0 and (.executors | index("opentofu") != null and index("proxmox") != null))
  ' >/dev/null || fail "Require exactly the selected connected, idle, enabled worker with the exclusive label and opentofu/proxmox capabilities."
  fixture=$(jq -n --arg api "$api" --arg agent "$REMOTE_AGENT_ID" --arg channel "$REMOTE_AGENT_URL" \
    --arg label "$agent_label" --arg endpoint "$endpoint" --arg node "$node" --arg name "$name" \
    '{api:$api,agent:$agent,channel:$channel,label:$label,endpoint:$endpoint,node:$node,name:$name}')
  repository=$(call "$api/api/repository")
  if [[ ${CLEANUP:-0} == 1 ]]; then
    [[ -s $work/remote-fixture.json && -s $work/vmid ]] || fail "No retained remote fixture to retire."
    jq -e --argjson expected "$fixture" '. == $expected' "$work/remote-fixture.json" >/dev/null ||
      fail "Remote inputs differ from the retained fixture; preserve it and use its original inputs."
    jq -e --arg lab "$lab" '.url == $lab' <<<"$repository" >/dev/null ||
      fail "The controller no longer uses this retained fixture repository."
  else
    jq -e '.url == null and .revision == null' <<<"$repository" >/dev/null ||
      fail "Remote mode requires a dedicated controller with no existing repository/revision; nothing was replaced."
    [[ ! -e $work ]] || fail "Remote fixture directory already exists; preserve it and inspect or use CLEANUP=1."
    mkdir -p "$work"
    printf '%s\n' "$fixture" >"$work/remote-fixture.json"
  fi
elif [[ ${CLEANUP:-0} != 1 && ${RESUME_SERVICE:-0} != 1 ]]; then
  if [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then
    [[ -s ${PORTAINER_TEMPLATE_DIR:-/nonexistent}/template.yaml ]] || fail "Build and review the NixOS image template first."
    [[ -s ${PORTAINER_CONFIG_DIR:-/nonexistent}/flake.lock ]] || fail "A locked NixOS deployment is required."
    existing=$("${pg[@]}" -Atc "SELECT count(*) FROM pg_database WHERE datname IN ('$database', '$state_database')")
    [[ $existing == 0 ]] || fail "Portainer fixture databases already exist. Preserve them and inspect/resume; never silently reset state."
  fi
  [[ ! -f $work/vmid ]] || fail "A previous run left state in $work (VMID $(<"$work/vmid")). Retire it with CLEANUP=1 first, or delete $work if the VM is gone."
  rm -rf "$work" && mkdir -p "$work"
  "${pg[@]}" -c "DROP DATABASE IF EXISTS $database" -c "DROP DATABASE IF EXISTS $state_database" \
    -c "CREATE DATABASE $database" -c "CREATE DATABASE $state_database"
else
  [[ -f $work/vmid ]] || fail "Nothing to clean up: $work has no VM recorded."
fi

if [[ $remote == 0 ]]; then
  log "Building"
  dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
  (cd "$root/agent" && cargo build --quiet --bin octopushy)

  log "Backend"
  (
    cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
    ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=$database;Username=octopushy;Password=octopushy" \
    Octopushy__DataDirectory="$work/server" Octopushy__ApiPort=$api_port \
    Octopushy__AgentChannel__Port=$agent_port Octopushy__AgentChannel__BindAddress=127.0.0.1 \
    Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:$agent_port Octopushy__MigrateOnStart=true \
    exec ./octopushy-server
  ) >>"$work/backend.log" 2>&1 &
  pids+=($!)
  for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
  curl -sf "$api/api/health" >/dev/null || fail "The backend did not start; see $work/backend.log."
fi
commit() {
  git -C "$lab" add -A
  git -C "$lab" -c user.name=octopushy-e2e -c user.email=e2e@example.invalid commit -q --allow-empty -m "$1"
  call -X POST "$api/api/repository/sync" >/dev/null
}

if [[ ${CLEANUP:-0} != 1 && ${RESUME_SERVICE:-0} != 1 ]]; then
  log "Account, variables, repository, agent"
  if [[ $remote == 0 ]]; then
    code=$(tr -d '\n' <"$work/server/setup-code")
    call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"proxmox-e2e-password\"}" >/dev/null
  fi
  # The token goes from its file to the store on standard input; it is never printed.
  jq -n --rawfile token "$token_file" '{key: "proxmox/umbrel/api-token", value: ($token | rtrimstr("\n")), visibility: "secret", description: "Proxmox API token for umbrel"}' |
    call -X PUT "$api/api/variables" --data-binary @- >/dev/null
  if [[ $remote == 1 ]]; then
    jq -n --rawfile pg "$REMOTE_TOFU_PG_FILE" \
      '{key:"opentofu/pg",value:($pg | rtrimstr("\n")),visibility:"secret",description:"Remote acceptance OpenTofu state"}' |
      call -X PUT "$api/api/variables" --data-binary @- >/dev/null
  else
    call -X PUT "$api/api/variables" -d "{\"key\":\"opentofu/pg\",\"value\":\"postgres://octopushy:octopushy@127.0.0.1:55432/$state_database?sslmode=disable\",\"visibility\":\"secret\",\"description\":\"OpenTofu state for the test\"}"
  fi

  mkdir -p "$lab/labs/home/proxmox/$node" "$lab/recipes" "$lab/templates" "$lab/modules/opentofu"
  cp -r "$root/examples/lab/recipes/proxmox-vm" "$lab/recipes/"
  if [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then
    cp -r "$PORTAINER_TEMPLATE_DIR" "$lab/templates/$template"
  else
    cp -r "$root/examples/lab/templates/fedora-44" "$lab/templates/"
  fi
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
  agentLabels: [$agent_label]
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
  jq -n --arg url "$lab" '{url:$url,branch:"main",pollSeconds:10,credentialKind:"none"}' |
    call -X PUT "$api/api/repository" --data-binary @- >/dev/null

  if [[ $remote == 0 ]]; then
    token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"proxmox-e2e","labels":["proxmox-e2e"]}' | jq -r .token)
    OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
      --server "https://127.0.0.1:$agent_port" --name proxmox-e2e --state-dir "$work/agent" >"$work/enroll.log" 2>&1
  fi
fi

if [[ $remote == 0 ]]; then
  "$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" --tool-runtime podman \
    --container-network host >>"$work/agent.log" 2>&1 &
  pids+=($!)
  if [[ ${UI:-0} == 1 ]]; then
    (cd "$root/frontend" && OCTOPUSHY_API=$api exec pnpm exec vite --port 5174 --strictPort) >"$work/frontend.log" 2>&1 &
    pids+=($!)
    echo "Web interface: http://127.0.0.1:5174 (use the dedicated e2e account)"
  fi
  call -X POST "$api/api/auth/login" -d '{"username":"e2e","password":"proxmox-e2e-password"}' >/dev/null
fi

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
  local detail
  detail=$(call "$api/api/runs/$1")
  if [[ $remote == 1 ]]; then
    jq -e --arg id "$REMOTE_AGENT_ID" '
      [.steps[].attempts[] | select(.agentId != null)] | length > 0 and all(.agentId == $id)
    ' <<<"$detail" >/dev/null || fail "Run $1 did not execute exclusively on the selected remote worker."
  fi
  jq -r '.steps[] | "  \(.name): \(.status)\(if .detail then " — " + .detail else "" end)"' <<<"$detail"
}
# Approves the run's plan after checking its counts with a jq condition.
review_and_approve() {
  local run=$1 condition=$2 detail step sha counts
  # In remote mode, verify plan execution placement before approving mutations.
  if [[ $remote == 1 ]]; then show_run "$run"; fi
  detail=$(call "$api/api/runs/$run")
  step=$(jq -r '.steps[] | select(.name=="approval") | .id' <<<"$detail")
  sha=$(jq -r '.steps[] | select(.name=="approval") | .approval.planSha256' <<<"$detail")
  counts=$(jq -c '.steps[] | select(.name=="plan") | .plan.counts' <<<"$detail")
  echo "  plan: $counts"
  jq -r '.steps[] | select(.name=="plan") | .plan.changes[] | "    \(.actions | join("/")) \(.address)"' <<<"$detail"
  if [[ ${PORTAINER_WORKFLOW:-0} == 1 ]]; then
    jq -e 'all(.steps[] | select(.name=="plan") | .plan.changes[];
      .address == "proxmox_download_file.image" or .address == "proxmox_virtual_environment_vm.guest")' \
      <<<"$detail" >/dev/null || fail "The saved plan includes an unexpected resource; not approved."
  fi
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
  if [[ $remote == 1 ]]; then show_run "$run" >&2; fi
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
template: $template
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

if [[ ${CLEANUP:-0} != 1 && ${RESUME_SERVICE:-0} != 1 ]]; then
  free=$(jq --arg n "$node" '[.installation.hosts[] | select(.name==$n) | (.maxMemoryBytes - .memoryBytes)][0] // 0' <<<"$summary")
  (( free > 3 * 1024 * 1024 * 1024 )) || fail "$node has less than 3 GiB of free memory; the test needs 2 GiB plus headroom."
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

  log "1. Create: 1 core, 1 GiB, 16 GiB disk"
  write_guest 1 1024 16
  commit "Create $name"
  for _ in $(seq 1 30); do guest=$(guest_id); [[ -n $guest ]] && break; sleep 2; done
  [[ -n ${guest:-} ]] || fail "The guest never appeared; see the repository page for definition errors."
  run=$(wait_run "$guest" provision awaiting-approval 15) || fail "The create plan did not finish."
  created=1
  review_and_approve "$run" '.create == 2 and .update == 0 and .delete == 0 and .replace == 0'
  run=$(wait_run "$guest" provision succeeded 30) || fail "Creating the VM failed."
  show_run "$run"
  vm=$(observe_vm "$installation" "$(<"$work/vmid")") || fail "Could not observe the VM."
  echo "  Proxmox reports: $vm"
  jq -e '.status == "running" and .maxCpu == 1 and .maxMemoryBytes == 1073741824' <<<"$vm" >/dev/null ||
    fail "The VM does not match: expected running, 1 core, 1 GiB."

  if [[ ${PORTAINER_WORKFLOW:-0} != 1 ]]; then
  log "2. Resize: 2 cores, 2 GiB, 20 GiB disk"
  write_guest 2 2048 20
  commit "Resize $name"
  run=$(wait_run "$guest" provision awaiting-approval 15) || fail "The resize plan did not finish."
  review_and_approve "$run" '.update >= 1 and .delete == 0 and .replace == 0'
  run=$(wait_run "$guest" provision succeeded 20) || fail "Resizing the VM failed."
  show_run "$run"
  vm=$(observe_vm "$installation" "$(<"$work/vmid")") || fail "Could not observe the VM."
  echo "  Proxmox reports: $vm"
  jq -e '.status == "running" and .maxCpu == 2 and .maxMemoryBytes == 2147483648' <<<"$vm" >/dev/null ||
    fail "The VM does not match: expected running, 2 cores, 2 GiB."
  fi
else
  created=1
  guest=$(guest_id)
fi

if [[ ${FEDORA_WORKFLOW:-0} == 1 && ${CLEANUP:-0} != 1 ]]; then
  log "Fedora service workflow on the disposable VM"
  PVE_ENDPOINT=$endpoint PVE_NODE=$node PVE_TOKEN_FILE=$token_file TEST_NAME=$name \
    "$root/tests/fedora/run.sh" "$work" ||
    fail "The Fedora service workflow failed. Logs are in $work/fedora."
fi

if [[ ${PORTAINER_WORKFLOW:-0} == 1 && ${CLEANUP:-0} != 1 ]]; then
  log "Native NixOS/Docker Portainer workflow on the controller-provisioned VM"
  PVE_ENDPOINT=$endpoint PVE_NODE=$node PVE_TOKEN_FILE=$token_file TEST_NAME=$name \
    python3 "$root/tests/portainer/live.py" "$work" "$PORTAINER_CONFIG_DIR" ||
    fail "The Portainer workflow failed; preserve the VM/state and inspect $work/portainer."
  log "VM no-op plan after native service configuration"
  call -X POST "$api/api/guests/$guest/plan" >/dev/null
  run=$(wait_run "$guest" provision awaiting-approval,succeeded 15) || fail "The final VM plan did not complete."
  detail=$(call "$api/api/runs/$run")
  counts=$(jq -c '.steps[] | select(.name=="plan") | .plan.counts' <<<"$detail")
  echo "  plan: $counts"
  jq -e '.create == 0 and .update == 0 and .delete == 0 and .replace == 0' <<<"$counts" >/dev/null ||
    fail "Unexpected VM drift after native setup; inspect the saved plan. No further apply was approved."
  [[ $(jq -r .run.status <<<"$detail") == succeeded ]] ||
    fail "The no-change run did not complete automatically; inspect rather than applying."
  show_run "$run"
  if [[ ${KEEP_GUEST:-0} == 1 ]]; then
    log "Passed: leaving $name running with its state and service data intact"
    echo "Retire only when requested: PORTAINER_WORKFLOW=1 CLEANUP=1 $0"
    exit 0
  fi
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
