#!/usr/bin/env bash
# End to end: the backend, a real agent, a mock Proxmox, and a real ansible-playbook configuring
# a throwaway SSH container. Nothing here touches real infrastructure.
#
# E2E_TOOL_RUNTIME=podman (the default) starts the Ansible image built from agent/ansible.
# E2E_TOOL_RUNTIME=local runs the same program directly, as the internal agent does; it needs
# a /tmp on tmpfs. Needs the development PostgreSQL (`just dev-db`). Leaves logs in .dev/ansible.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/ansible
runtime=${E2E_TOOL_RUNTIME:-podman}
api=http://127.0.0.1:18080
port=12222
target=octopushy-ansible-target
image=localhost/octopushy-ansible:e2e
pg=(podman exec octopushy-dev-pg psql -q -U octopushy -d octopushy -v ON_ERROR_STOP=1)
guest=e2e/korriban/e2e-vm-01
password='pa"ss\word-with-{{ braces }}'
pids=()

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
  podman rm -f "$target" >/dev/null 2>&1 || true
  podman ps -q --filter label=io.octopushy.attempt=true | xargs -r podman rm -f >/dev/null 2>&1 || true
}
trap cleanup EXIT

rm -rf "$work" && mkdir -p "$work/keys" "$work/bin"
log "Building"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin octopushy --bin mock-proxmox)
podman build -q -f "$root/tests/ansible/target/Containerfile" -t localhost/octopushy-ansible-target:e2e "$root/tests/ansible/target" >/dev/null
agent_args=(--tool-runtime "$runtime")
if [[ $runtime == local ]]; then
  # What agent/Containerfile installs, at a path of this checkout.
  python3 -m venv "$work/venv"
  "$work/venv/bin/pip" install --quiet --no-deps --require-hashes -r "$root/agent/ansible/requirements.lock"
  printf '#!/bin/sh\nexec "%s" "%s" "$@"\n' "$work/venv/bin/python3" "$root/agent/ansible/octopushy-ansible" >"$work/bin/octopushy-ansible"
  chmod 0755 "$work/bin/octopushy-ansible"
  podman run --rm --entrypoint cat ghcr.io/opentofu/opentofu:1.13.1 /usr/local/bin/tofu >"$work/bin/tofu"
  chmod 0755 "$work/bin/tofu"
  agent_args+=(--ansible-binary "$work/bin/octopushy-ansible" --tofu-binary "$work/bin/tofu")
else
  podman build -q -f "$root/agent/ansible/Containerfile" -t "$image" "$root" >/dev/null
  agent_args+=(--container-network host)
fi

log "A throwaway guest"
ssh-keygen -q -t ed25519 -N '' -f "$work/keys/octopushy"
ssh-keygen -q -t ed25519 -N '' -f "$work/keys/deploy"
ssh-keygen -q -t ed25519 -N '' -f "$work/keys/stranger"
cp "$work/keys/octopushy.pub" "$work/keys/authorized_keys"
start_target() {
  podman run -d --replace --name "$target" -p "127.0.0.1:$port:22" -v "$work/keys:/keys:ro,Z" \
    --cap-add SYS_CHROOT --cap-add AUDIT_WRITE localhost/octopushy-ansible-target:e2e >/dev/null
  for _ in $(seq 1 30); do
    (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null && return 0
    sleep 1
  done
  fail "the target did not start"
}
start_target
# Read through the container runtime: this test's trusted channel, as Proxmox is for a real VM.
for _ in $(seq 1 30); do
  host_key=$(podman exec "$target" cat /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null) && break
  sleep 1
done
[[ -n ${host_key:-} ]] || fail "no host key"

log "Databases"
"${pg[@]}" -c "DROP DATABASE IF EXISTS ansible_octopushy" -c "CREATE DATABASE ansible_octopushy"

log "Lab repository"
cp -r "$root/tests/e2e/lab" "$work/lab"
cp -r "$root/tests/ansible/lab/." "$work/lab/"
guest_dir=$work/lab/labs/e2e/proxmox/korriban/guests/e2e-vm-01
echo 'From the guest folder.' >"$guest_dir/motd"
cat >>"$guest_dir/guest.yaml" <<YAML
setup:
  recipe: test-config
  inputs: { greeting: hello }
  secrets:
    app_password: { store: e2e/app-password }
  secretFiles:
    deploy_key_src: { store: e2e/deploy-key }
access:
  address: 127.0.0.1
  port: $port
  user: ops
  sshKey: { store: e2e/ssh-key }
  hostKey: { store: e2e/host-key }
YAML
git -C "$work/lab" init -q -b main
git -C "$work/lab" add -A
git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "Initial definitions"

log "Mock Proxmox"
"$root/agent/target/debug/mock-proxmox" --listen 127.0.0.1:18006 --fixture "$root/tests/e2e/proxmox.json" \
  --tls-self-signed >"$work/mock.log" 2>&1 &
pids+=($!)

log "Backend"
(
  cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=ansible_octopushy;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" \
  Octopushy__ApiPort=18080 \
  Octopushy__AgentChannel__Port=18443 \
  Octopushy__AgentChannel__BindAddress=127.0.0.1 \
  Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:18443 \
  Octopushy__MigrateOnStart=true \
  Octopushy__Scheduling__VerifyWaitSeconds=30 \
  Octopushy__Tools__AnsibleImage=$image \
  exec ./octopushy-server
) >"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
curl -sf "$api/api/health" >/dev/null || fail "the backend did not start; see $work/backend.log"

jar=$work/cookies
call() { curl -sf -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
source "$root/tests/installation-trust/fixture.sh"

set_variable() {
  jq -n --arg key "$1" --arg value "$2" --arg visibility "$3" '{key: $key, value: $value, visibility: $visibility}' |
    call -X PUT "$api/api/variables" --data-binary @- >/dev/null
}

log "Account, repository, variables"
code=$(tr -d '\n' <"$work/server/setup-code")
call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"an end-to-end password\"}" >/dev/null
set_variable proxmox/korriban/read-only-token 'octopushy-reader@pve!e2e-read-only=00000000-0000-4000-8000-0000000000e1' secret
set_variable proxmox/korriban/privileged-token 'octopushy-writer@pve!e2e-privileged=00000000-0000-4000-8000-0000000000e2' secret
set_variable e2e/ssh-key "$(cat "$work/keys/octopushy")" secret
set_variable e2e/host-key "$host_key" plain
set_variable e2e/app-password "$password" secret
set_variable e2e/deploy-key "$(cat "$work/keys/deploy")" secret
call -X PUT "$api/api/repository" -d "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null

log "Agent"
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"e2e-agent","labels":["e2e"]}' | jq -r .token)
OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
  --server https://127.0.0.1:18443 --name e2e-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
start_agent() {
  "$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" "${agent_args[@]}" >>"$work/agent.log" 2>&1 &
  agent_pid=$!
  pids+=("$agent_pid")
}
start_agent

guest_id() { call "$api/api/inventory" | jq -r --arg a "$guest" '[.labs[].installations[].guests[] | select(.address==$a)][0].id'; }
guest_status() {
  call "$api/api/inventory" | jq -r --arg a "$guest" '[.labs[]?.installations[]?.guests[]? | select(.address==$a) | .status][0] // "absent"'
}
latest_configure() { call "$api/api/runs?limit=50" | jq -r '[.[] | select(.operation=="configure")][0].id // empty'; }
run_status() { call "$api/api/runs/$1" | jq -r .run.status; }
wait_run() {
  local run=$1 want=$2 status=""
  for _ in $(seq 1 150); do
    status=$(run_status "$run")
    [[ $status == "$want" ]] && { echo "run is $status"; return 0; }
    if [[ $want != "$status" && $status =~ ^(succeeded|failed|cancelled|uncertain)$ ]]; then break; fi
    sleep 2
  done
  call "$api/api/runs/$run" | jq '{status: .run.status, detail: .run.detail, steps: [.steps[] | {name, status, detail, attempts: [.attempts[] | {status, error}]}]}'
  fail "expected run $run to be $want, but it is $status"
}
start_action() {
  call -X POST "$api/api/guests/$(guest_id)/configure" -d "{\"action\":\"$1\"}" | jq -r .runId
}
logs_of() {
  local attempt
  for attempt in $(call "$api/api/runs/$1" | jq -r '.steps[].attempts[].id'); do
    call "$api/api/attempts/$attempt/log?limit=10000" | jq -r '.lines[].text'
  done
}
target_has() { podman exec "$target" test -e "$1"; }

log "Provisioning, then configuring by itself"
call -X POST "$api/api/repository/sync" >/dev/null
trust_fixture_installations "$work/lab" "$(git -C "$work/lab" rev-parse HEAD)"
for _ in $(seq 1 120); do
  run=$(latest_configure)
  [[ -n $run ]] && break
  sleep 2
done
[[ -n ${run:-} ]] || { call "$api/api/runs?limit=5" | jq '.[] | {operation, status, detail}'; fail "no configure run was proposed"; }
wait_run "$run" succeeded
call "$api/api/runs/$run" | jq -r '.steps[] | "\(.name): \(.status)"'
[[ $(podman exec "$target" cat /var/lib/configured) == '1.2.3 hello' ]] || fail "the playbook did not use what was prepared"
[[ $(podman exec "$target" cat /var/lib/motd) == 'From the guest folder.' ]] || fail "the guest's folder did not reach the playbook"
[[ $(podman exec "$target" cat /var/lib/app-password) == "$password" ]] || fail "the password did not arrive intact"
[[ $(podman exec "$target" cat /var/lib/deploy-key) == "$(cat "$work/keys/deploy")" ]] || fail "the secret file did not arrive intact"
call "$api/api/runs/$run" | jq -e '[.steps[] | select(.name=="preview")][0].configurePreview
  | .action == "configure" and (.tasks | length > 3) and .prepared.release_version == "1.2.3"
    and (.hostKeyFingerprints[0] | startswith("ssh-ed25519 SHA256:"))' >/dev/null || fail "the preview does not show the run"

log "Nothing secret in logs, evidence, or on the agent"
logs_of "$run" >"$work/configure.log"
grep -q 'the password is \*\*\*' "$work/configure.log" || { tail -30 "$work/configure.log"; fail "the careless task was not masked"; }
key_line=$(sed -n 2p "$work/keys/deploy")
for secret in 'word-with-' "$key_line" "$(sed -n 2p "$work/keys/octopushy")"; do
  if grep -rqF -- "$secret" "$work/configure.log"; then fail "a secret reached the attempt log"; fi
  if call "$api/api/runs/$run" | grep -qF -- "$secret"; then fail "a secret reached the run's evidence"; fi
  if grep -rqF -- "$secret" "$work/agent" "$work/agent.log" "$work/backend.log" 2>/dev/null; then fail "a secret reached the agent's disk or a log"; fi
  if grep -rqF -- "$secret" "$work/server/logs" 2>/dev/null; then fail "a secret reached the server's logs"; fi
done
if podman exec "$target" sh -c 'grep -rlF word-with- /var/log /home/ops/.ansible 2>/dev/null'; then fail "a secret stayed on the guest"; fi
if compgen -G '/tmp/octopushy-attempt-*' >/dev/null; then fail "a secret directory stayed on the agent"; fi
[[ ! -e "$work/lab/modules/ansible/test/ansible.log" ]] || fail "the repository's log_path was honoured"

log "One run per commit"
sleep 7
[[ $(latest_configure) == "$run" ]] || fail "a second automatic run appeared"

log "A declared outcome waits, and approving runs it once more"
run=$(start_action defer)
wait_run "$run" awaiting-approval
gate=$(call "$api/api/runs/$run" | jq -c '.steps[] | select(.name=="outcome-approval")')
jq -e '.detail | contains("Three threads") and contains("allow_busy")' <<<"$gate" >/dev/null || fail "the outcome is not shown: $gate"
if target_has /var/lib/activated; then fail "a deferred run activated"; fi
call -X POST "$api/api/runs/$run/steps/$(jq -r .id <<<"$gate")/approve" \
  --data-binary "$(jq -c '{planSha256: .approval.planSha256}' <<<"$gate")" >/dev/null
wait_run "$run" succeeded
target_has /var/lib/activated || fail "the approved run did not activate"

log "An action that reuses what was prepared"
run=$(start_action update-system)
wait_run "$run" succeeded
target_has /var/lib/updated || fail "the snapshot action did not run"

log "A failed task is a known failure"
run=$(start_action fail)
wait_run "$run" failed
call "$api/api/runs/$run" | jq -e '.run.detail | contains("A task that fails")' >/dev/null || fail "the failed task is not named"

log "A wrong pin is refused and nothing runs"
stranger=$(cut -d' ' -f1-2 "$work/keys/stranger.pub")
set_variable e2e/host-key "$stranger" plain
podman exec "$target" rm -f /var/lib/configured
run=$(start_action configure)
wait_run "$run" failed
call "$api/api/runs/$run" | jq -e '.run.detail | contains("not the pinned one")' >/dev/null \
  || { call "$api/api/runs/$run" | jq .run; fail "a wrong pin is not reported as one"; }
if target_has /var/lib/configured; then fail "a playbook ran against an unpinned host"; fi
set_variable e2e/host-key "$host_key" plain

log "A killed agent leaves the run uncertain and the guest's lock alone"
run=$(start_action hang)
for _ in $(seq 1 90); do target_has /run/lock/test-workflow && break; sleep 1; done
target_has /run/lock/test-workflow || fail "the lock was never taken"
sleep 3
# While it runs, nothing secret is on a command line.
if ps -eo args | grep -v grep | grep -qF -- 'word-with-'; then fail "a secret is in argv"; fi
if ps -eo args | grep -v grep | grep -qF -- "$key_line"; then fail "a key is in argv"; fi
kill -9 "$agent_pid"
wait "$agent_pid" 2>/dev/null || true
start_agent
wait_run "$run" uncertain
target_has /run/lock/test-workflow || fail "something removed the guest's lock"
call -X POST "$api/api/runs/$run/close" >/dev/null
podman ps -q --filter label=io.octopushy.attempt=true | xargs -r podman rm -f >/dev/null 2>&1 || true

log "The lock still stops the next run, as a known failure"
run=$(start_action hang)
wait_run "$run" failed
podman exec "$target" sh -c 'pkill sleep; rmdir /run/lock/test-workflow'

log "A guest lost during a task is uncertain"
run=$(start_action hang)
for _ in $(seq 1 90); do target_has /run/lock/test-workflow && break; sleep 1; done
podman stop -t 0 "$target" >/dev/null
wait_run "$run" uncertain
call -X POST "$api/api/runs/$run/close" >/dev/null

log "Passed ($runtime)"
