#!/usr/bin/env bash
# End to end: the backend, a real agent, a mock Proxmox, and a real ansible-playbook configuring
# a throwaway SSH container. Nothing here touches real infrastructure.
#
# E2E_TOOL_RUNTIME=podman (the default) starts the Ansible image built from agent/ansible.
# E2E_TOOL_RUNTIME=local runs the same program directly, as the internal agent does; it needs
# a /tmp on tmpfs. It has its own PostgreSQL and ports, so it can run beside anything else that
# uses the development database. Leaves logs in .dev/ansible.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/ansible
runtime=${E2E_TOOL_RUNTIME:-podman}
# Free ports, so that this can run beside anything else on the machine.
free_port() { python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'; }
api_port=$(free_port)
agent_port=$(free_port)
proxmox_port=$(free_port)
pg_port=$(free_port)
port=$(free_port)
api=http://127.0.0.1:$api_port
target=octopushy-ansible-target
database=octopushy-ansible-pg
image=localhost/octopushy-ansible:e2e
guest=e2e/korriban/e2e-vm-01
password='pa"ss\word-with-{{ braces }}'
pids=()

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
  podman rm -f -v "$target" "$database" >/dev/null 2>&1 || true
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

log "Database"
podman run -d --replace --name "$database" -p "127.0.0.1:$pg_port:5432" --tmpfs /var/lib/postgresql \
  -e POSTGRES_USER=octopushy -e POSTGRES_PASSWORD=octopushy -e POSTGRES_DB=octopushy \
  docker.io/library/postgres:18-alpine >/dev/null
for _ in $(seq 1 60); do
  podman exec "$database" pg_isready -q -h 127.0.0.1 -U octopushy -d octopushy && break
  sleep 1
done
podman exec "$database" pg_isready -q -h 127.0.0.1 -U octopushy -d octopushy || fail "PostgreSQL did not start"

log "Lab repository"
cp -r "$root/tests/e2e/lab" "$work/lab"
cp -r "$root/tests/ansible/lab/." "$work/lab/"
grep -rlF 18006 "$work/lab" | xargs -r sed -i "s/18006/$proxmox_port/g"
guest_dir=$work/lab/labs/e2e/proxmox/korriban/guests/e2e-vm-01
echo 'From the guest folder.' >"$guest_dir/motd"
# The same machine, with the workflow that also sets it up.
sed -i 's/^workflow: test-vm@1$/workflow: test-config@1/' "$guest_dir/guest.yaml"
cat >>"$guest_dir/guest.yaml" <<YAML
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
"$root/agent/target/debug/mock-proxmox" --listen "127.0.0.1:$proxmox_port" --fixture "$root/tests/e2e/proxmox.json" \
  --tls-self-signed >"$work/mock.log" 2>&1 &
pids+=($!)

log "Backend"
(
  cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=$pg_port;Database=octopushy;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" \
  Octopushy__ApiPort=$api_port \
  Octopushy__AgentChannel__Port=$agent_port \
  Octopushy__AgentChannel__BindAddress=127.0.0.1 \
  Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:$agent_port \
  Octopushy__MigrateOnStart=true \
  Octopushy__Scheduling__VerifyWaitSeconds=30 \
  Octopushy__Tools__AnsibleImage=$image \
  exec ./octopushy-server
) >"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done
curl -sf "$api/api/health" >/dev/null || fail "the backend did not start; see $work/backend.log"

jar=$work/cookies
call() { curl -sf -b "$jar" -c "$jar" -H "Origin: $api" -H 'Content-Type: application/json' "$@"; }
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
  --server "https://127.0.0.1:$agent_port" --name e2e-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
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
  | .action == "setup" and (.tasks | length > 3) and .prepared.release_version == "1.2.3"
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
if call "$api/api/runs/$run" | grep -qF -- 'word-with-'; then fail "a templated task name put a secret in the run's evidence"; fi
if grep -rqF -- 'word-with-' "$work/agent" "$work/agent.log" "$work/server/logs" 2>/dev/null; then fail "a templated task name put a secret on disk"; fi
call "$api/api/runs/$run" | jq -e '[.steps[] | select(.name=="playbook")][0].outputs.recap.failures == 1' >/dev/null \
  || fail "the recap did not reach the agent"
if grep -rqF -- 'octopushy-recap' "$work/server/logs" 2>/dev/null; then fail "a recap was logged"; fi

log "A wrong pin is refused and nothing runs"
stranger=$(cut -d' ' -f1-2 "$work/keys/stranger.pub")
set_variable e2e/host-key "$stranger" plain
podman exec "$target" rm -f /var/lib/configured
run=$(start_action setup)
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

log "Octopushy's own key, given to a guest that has been using another"
# The same container, so the same host key: the pin still holds.
podman start "$target" >/dev/null
for _ in $(seq 1 30); do (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null && break; sleep 1; done
podman exec "$target" sh -c 'rmdir /run/lock/test-workflow 2>/dev/null || true'
first_provision=$(call "$api/api/runs?limit=50" | jq -r '[.[] | select(.operation=="provision")][0].id')
sed -i '/sshKey:/d' "$guest_dir/guest.yaml"
git -C "$work/lab" add -A
git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "Let Octopushy use its own key"
previous=$(latest_configure)
call -X POST "$api/api/repository/sync" >/dev/null
for _ in $(seq 1 60); do
  provision=$(call "$api/api/runs?limit=50" | jq -r '[.[] | select(.operation=="provision")][0].id')
  [[ $provision != "${first_provision:-}" ]] && break
  sleep 1
done
managed=""
for _ in $(seq 1 120); do
  managed=$(call "$api/api/guests/$(guest_id)" | jq -r '.managedAccess.publicKey // empty')
  [[ -n $managed ]] && break
  sleep 1
done
[[ -n $managed ]] || fail "the guest page shows no key of Octopushy's own"
# As cloud-init would put it there. The key Octopushy used until now goes, so nothing else can log in.
printf '%s\n' "$managed" >"$work/keys/authorized_keys"
podman exec "$target" sh -c 'cp /keys/authorized_keys /home/ops/.ssh/authorized_keys; chown ops:ops /home/ops/.ssh/authorized_keys'
wait_run "$provision" succeeded
counts=$(call "$api/api/runs/$provision" | jq -c '[.steps[] | select(.name=="plan")][0].outputs.counts // {}')
echo "plan: $counts"
jq -e '(.replace // 0) == 0 and (.delete // 0) == 0' <<<"$counts" >/dev/null || fail "adding a key must not replace the machine: $counts"
for _ in $(seq 1 60); do
  run=$(latest_configure)
  [[ -n $run && $run != "$previous" ]] && break
  sleep 2
done
[[ $run != "$previous" ]] || fail "no configure run followed the key"
wait_run "$run" succeeded
call "$api/api/guests/$(guest_id)" | jq -e '.managedAccess.lastUsedAt != null' >/dev/null || fail "the use of the key is not recorded"
logs_of "$provision" >"$work/own-key.log"
logs_of "$run" >>"$work/own-key.log"
call "$api/api/runs/$run" >>"$work/own-key.log"
call "$api/api/guests/$(guest_id)" >>"$work/own-key.log"
call "$api/api/variables" >>"$work/own-key.log"
if grep -q 'PRIVATE KEY' "$work/own-key.log"; then fail "a private key reached a log or a response"; fi
if grep -rq 'OPENSSH PRIVATE KEY' "$work/agent" 2>/dev/null; then fail "a private key stayed on the agent's disk"; fi

log "Rotating Octopushy's own key, in stages, beside a person's"
person=$(ssh-keygen -q -t ed25519 -N '' -f "$work/keys/person" </dev/null >/dev/null; cat "$work/keys/person.pub")
# A person's key whose comment quotes the key that will be removed: it has to stay.
person="$person, replaces $managed"
podman exec "$target" sh -c "printf '%s\n' '$person' >>/home/ops/.ssh/authorized_keys"
keys_on_guest() { podman exec "$target" cat /home/ops/.ssh/authorized_keys; }
decide() { # the step whose decision a person makes
  local step
  for _ in $(seq 1 60); do
    step=$(call "$api/api/runs/$1" | jq -c --arg n "$2" '.steps[] | select(.name==$n and .status=="awaiting-approval")')
    [[ -n $step ]] && break
    sleep 1
  done
  [[ -n $step ]] || fail "$2 is not waiting for a person"
  call -X POST "$api/api/runs/$1/steps/$(jq -r .id <<<"$step")/approve" \
    -d "$(jq -cn --arg sha "$(jq -r .approval.planSha256 <<<"$step")" '{planSha256: $sha, note: "End to end."}')" >/dev/null
}
run=$(call -X POST "$api/api/guests/$(guest_id)/access/rotate" | jq -r .runId)
for _ in $(seq 1 90); do [[ $(keys_on_guest | wc -l) == 3 ]] && break; sleep 1; done
[[ $(keys_on_guest | wc -l) == 3 ]] || fail "the candidate was not added beside the keys there"
decide "$run" promote-key
guest_key=$(call "$api/api/guests/$(guest_id)" | jq -r '.managedAccess.publicKey')
[[ $guest_key != "$managed" ]] || fail "promoting did not change the key in use"
has_key() { keys_on_guest | awk -v key="$1" 'index($0, key) == 1 { found = 1 } END { exit !found }'; }
has_key "${managed% *}" || fail "the old key went before its removal was approved"
decide "$run" approve-removal
wait_run "$run" succeeded
if has_key "${managed% *}"; then fail "the old key is still on the guest"; fi
keys_on_guest | grep -qF "$person" || fail "a rotation touched a person's key"
keys_on_guest | grep -qF "${guest_key% *}" || fail "the new key is not on the guest"
logs_of "$run" >"$work/rotation.log"
call "$api/api/runs/$run" >>"$work/rotation.log"
if grep -q 'PRIVATE KEY' "$work/rotation.log"; then fail "a private key reached a log or a response"; fi
run=$(start_action setup)
wait_run "$run" succeeded

log "A key sshd still takes stays on the list, wherever it is"
kept_key=$guest_key
kept_version=$(call "$api/api/guests/$(guest_id)" | jq -r .managedAccess.version)
key_state() { call "$api/api/guests/$(guest_id)" | jq -r --argjson version "$1" '.managedAccess.keys[] | select(.version==$version) | .state'; }
podman exec -i "$target" sh -c 'cat > /home/ops/.ssh/authorized_keys2; chown ops:ops /home/ops/.ssh/authorized_keys2' <<<"$kept_key"
run=$(call -X POST "$api/api/guests/$(guest_id)/access/rotate" | jq -r .runId)
for _ in $(seq 1 90); do [[ $(keys_on_guest | wc -l) == 3 ]] && break; sleep 1; done
decide "$run" promote-key
decide "$run" approve-removal
wait_run "$run" failed
[[ $(call "$api/api/runs/$run" | jq -r '.steps[] | select(.name=="confirm-refused") | .status') == failed ]] \
  || fail "the rotation did not stop at sshd's answer"
call "$api/api/runs/$run" | jq -r '.steps[] | select(.name=="confirm-refused") | .detail' | grep -q 'still accepts' \
  || fail "the run does not say sshd took the removed key"
[[ $(key_state "$kept_version") == old ]] || fail "a key sshd still takes does not read as old"
podman exec "$target" rm /home/ops/.ssh/authorized_keys2
run=$(start_action setup)
wait_run "$run" succeeded

log "With two old keys, the second is the one sshd still takes"
second_key=$(call "$api/api/guests/$(guest_id)" | jq -r .managedAccess.publicKey)
second_version=$(call "$api/api/guests/$(guest_id)" | jq -r .managedAccess.version)
podman exec -i "$target" sh -c 'cat > /home/ops/.ssh/authorized_keys2; chown ops:ops /home/ops/.ssh/authorized_keys2' <<<"$second_key"
run=$(call -X POST "$api/api/guests/$(guest_id)/access/rotate" | jq -r .runId)
for _ in $(seq 1 90); do [[ $(keys_on_guest | wc -l) == 3 ]] && break; sleep 1; done
decide "$run" promote-key
decide "$run" approve-removal
wait_run "$run" failed
call "$api/api/runs/$run" | jq -r '.steps[] | select(.name=="confirm-refused") | .detail' | grep -q 'still accepts old key 2 of 2' \
  || fail "the run does not say which old key sshd took"
[[ $(key_state "$second_version") == old ]] || fail "a key sshd still takes does not read as old"
podman exec "$target" rm /home/ops/.ssh/authorized_keys2

log "A login that fails after sshd took the key is not a refusal"
run=$(start_action setup)
wait_run "$run" succeeded
third_key=$(call "$api/api/guests/$(guest_id)" | jq -r .managedAccess.publicKey)
third_version=$(call "$api/api/guests/$(guest_id)" | jq -r .managedAccess.version)
# As a key whose forced command runs an ssh of its own would answer.
printf 'command="echo ops@inner: Permission denied \\(publickey\\). >&2; exit 255" %s\n' "$third_key" \
  | podman exec -i "$target" sh -c 'cat > /home/ops/.ssh/authorized_keys2; chown ops:ops /home/ops/.ssh/authorized_keys2'
run=$(call -X POST "$api/api/guests/$(guest_id)/access/rotate" | jq -r .runId)
for _ in $(seq 1 90); do [[ $(keys_on_guest | wc -l) == 3 ]] && break; sleep 1; done
decide "$run" promote-key
decide "$run" approve-removal
wait_run "$run" failed
call "$api/api/runs/$run" | jq -r '.steps[] | select(.name=="confirm-refused") | .detail' | grep -q 'still accepts old key 3 of 3' \
  || fail "a forced command that says it was refused was believed"
[[ $(key_state "$third_version") == old ]] || fail "a key sshd took reads as anything but old"
podman exec "$target" rm /home/ops/.ssh/authorized_keys2

log "The next rotation shows sshd refusing all four old keys it has left"
run=$(start_action setup)
wait_run "$run" succeeded
run=$(call -X POST "$api/api/guests/$(guest_id)/access/rotate" | jq -r .runId)
for _ in $(seq 1 90); do [[ $(keys_on_guest | wc -l) == 3 ]] && break; sleep 1; done
decide "$run" promote-key
decide "$run" approve-removal
wait_run "$run" succeeded
logs_of "$run" | grep -q 'old key 4 of 4' || fail "not every old key was tried"
if logs_of "$run" | grep -q 'PRIVATE KEY'; then fail "a private key reached a log"; fi
[[ $(key_state "$kept_version") == removed ]] || fail "the key left behind was not removed by the next rotation"
[[ $(call "$api/api/guests/$(guest_id)" | jq '[.managedAccess.keys[] | select(.state=="removed")] | length') == 5 ]] \
  || fail "not every old key reads as removed"

log "What a rotation removes, against a real sshd"
python3 "$root/tests/ansible/authorized_keys_sshd.py" localhost/octopushy-ansible-target:e2e

log "Passed ($runtime)"
