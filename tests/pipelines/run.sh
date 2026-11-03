#!/usr/bin/env bash
# End to end: a service's pipeline through the backend, a real agent, a real ansible-playbook and
# real OpenTofu, against a throwaway SSH container. The guest itself is provisioned against a mock
# Proxmox. Nothing here touches real infrastructure.
#
# E2E_TOOL_RUNTIME=podman (the default) starts the images; local runs the same programs directly
# and needs a /tmp on tmpfs. It has its own PostgreSQL and ports. Leaves logs in .dev/pipelines.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/pipelines
runtime=${E2E_TOOL_RUNTIME:-podman}
free_port() { python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'; }
api_port=$(free_port)
agent_port=$(free_port)
proxmox_port=$(free_port)
pg_port=$(free_port)
port=$(free_port)
api=http://127.0.0.1:$api_port
target=octopushy-pipelines-target
database=octopushy-pipelines-pg
image=localhost/octopushy-ansible:e2e
service=e2e/korriban/e2e-vm-01/web
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
cp "$work/keys/octopushy.pub" "$work/keys/authorized_keys"
podman run -d --replace --name "$target" -p "127.0.0.1:$port:22" -v "$work/keys:/keys:ro,Z" \
  --cap-add SYS_CHROOT --cap-add AUDIT_WRITE localhost/octopushy-ansible-target:e2e >/dev/null
for _ in $(seq 1 30); do (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null && break; sleep 1; done
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
cp -r "$root/tests/pipelines/lab/." "$work/lab/"
grep -rlF 18006 "$work/lab" | xargs -r sed -i "s/18006/$proxmox_port/g"
guest_dir=$work/lab/labs/e2e/proxmox/korriban/guests/e2e-vm-01
service_dir=$guest_dir/services/web
mkdir -p "$service_dir"
echo 'server_name web;' >"$service_dir/site.conf"
echo 'From the guest folder.' >"$guest_dir/motd"
sed -i 's/^workflow: test-vm@1$/workflow: test-config@1/' "$guest_dir/guest.yaml"
cat >>"$guest_dir/guest.yaml" <<YAML
inputs: { greeting: hello }
secrets:
  app_password: { store: e2e/web-password }
secretFiles:
  deploy_key_src: { store: e2e/deploy-key }
access:
  address: 127.0.0.1
  port: $port
  user: ops
  sshKey: { store: e2e/ssh-key }
  hostKey: { store: e2e/host-key }
YAML
# The web workflow has three stages, two of them OpenTofu, so a state of one stage cannot pass for the other's.
write_pipeline() {
  cat >"$service_dir/service.yaml" <<YAML
schemaVersion: 1
workflow: web@1
policy: { approval: $1 }
inputs: { port: $2, fail_until_allowed: $3 }
secrets:
  web_password: { store: e2e/web-password }
YAML
}
write_pipeline manual 8080 false
commit() {
  git -C "$work/lab" add -A
  git -C "$work/lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "$1"
}
git -C "$work/lab" init -q -b main
commit "Initial definitions"

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
call() { curl -sf -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
# As call, but a refusal is an answer: prints the body and never fails.
ask() { curl -s -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
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
set_variable e2e/web-password "$password" secret
set_variable e2e/deploy-key "$(cat "$work/keys/octopushy")" secret
call -X PUT "$api/api/repository" -d "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null

log "Agent"
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"e2e-agent","labels":["e2e"]}' | jq -r .token)
OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
  --server "https://127.0.0.1:$agent_port" --name e2e-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
"$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" "${agent_args[@]}" >>"$work/agent.log" 2>&1 &
pids+=($!)

sync_repository() {
  git -C "$work/lab" rev-parse HEAD >"$work/head"
  call -X POST "$api/api/repository/sync" >/dev/null
  for _ in $(seq 1 30); do
    [[ $(call "$api/api/repository" | jq -r .revision.commit) == "$(cat "$work/head")" ]] && return 0
    sleep 1
  done
  fail "the commit was not loaded"
}
guest_id() { call "$api/api/inventory" | jq -r --arg a "$guest" '[.labs[].installations[].guests[] | select(.address==$a)][0].id'; }
service_id() { call "$api/api/guests/$(guest_id)" | jq -r --arg a "$service" '.services[] | select(.address==$a) | .id'; }
service_view() { call "$api/api/services/$(service_id)"; }
latest_pipeline() { call "$api/api/runs?limit=50" | jq -r '[.[] | select(.operation=="pipeline")][0].id // empty'; }
pipeline_count() { call "$api/api/runs?limit=50" | jq '[.[] | select(.operation=="pipeline")] | length'; }
run_view() { call "$api/api/runs/$1"; }
wait_run() {
  local run=$1 want=$2 status=""
  for _ in $(seq 1 150); do
    status=$(run_view "$run" | jq -r .run.status)
    [[ $status == "$want" ]] && { echo "run is $status"; return 0; }
    if [[ $status =~ ^(succeeded|failed|cancelled|uncertain)$ ]]; then break; fi
    sleep 2
  done
  run_view "$run" | jq '{status: .run.status, detail: .run.detail, steps: [.steps[] | {stage, name, status, detail, attempts: [.attempts[] | {status, error, waitingReason}]}]}'
  fail "expected run $run to be $want, but it is $status"
}
wait_new_pipeline() {
  local previous=$1 run=""
  for _ in $(seq 1 90); do
    run=$(latest_pipeline)
    [[ -n $run && $run != "$previous" ]] && { echo "$run"; return 0; }
    sleep 2
  done
  call "$api/api/runs?limit=8" | jq '.[] | {operation, status, detail}' >&2
  fail "no pipeline run was proposed"
}
target_has() { podman exec "$target" test -e "$1"; }
stage_state() { call "$api/api/guests/$(service_id)/state?stage=$1" | jq -r .headVersion; }
logs_of() {
  local attempt
  for attempt in $(run_view "$1" | jq -r '.steps[].attempts[].id'); do
    call "$api/api/attempts/$attempt/log?limit=10000" | jq -r '.lines[].text'
  done
}

log "The guest comes first: provision, then setup, and the service waits and says so"
call -X POST "$api/api/repository/sync" >/dev/null
trust_fixture_installations "$work/lab" "$(git -C "$work/lab" rev-parse HEAD)"
for _ in $(seq 1 30); do [[ -n $(service_id) ]] && break; sleep 1; done
service_view | jq -e '.service.status == "waiting" or (.service.activeRun != null)' >/dev/null || { service_view | jq .service; fail "the service does not say it waits"; }
service_view | jq -r '.service.statusDetail'

log "Every stage is planned and previewed, and nothing changes before the one approval"
run=$(wait_new_pipeline "")
wait_run "$run" awaiting-approval
run_view "$run" | jq -r '.steps[] | "\(.stage // "-") \(.name): \(.status)"'
[[ $(run_view "$run" | jq -c '[.steps[] | [.stage, .name]]') == '[["expose","plan"],["deploy","preview"],["audit","plan"],[null,"approval"],["expose","apply"],["deploy","playbook"],["audit","apply"]]' ]] \
  || fail "the timeline is not plan, preview, approval, apply"
if target_has /var/lib/web-deployed; then fail "something ran before the approval"; fi
[[ $(stage_state expose) == 0 ]] || fail "state changed before the approval"
run_view "$run" | jq -e '[.steps[].attempts[] | select(.mutating)] | length == 0' >/dev/null || fail "a change was started before the approval"
gate=$(run_view "$run" | jq -c '.steps[] | select(.name=="approval")')
jq -e '.approval.document.stages | map(.name) == ["expose","deploy","audit"] and (map(.artifact) | all)' <<<"$gate" >/dev/null \
  || fail "the approval does not name a document of every stage: $gate"

log "A refused digest changes nothing; the right one applies every stage in order"
refused=$(ask -o /dev/null -w '%{http_code}' -X POST "$api/api/runs/$run/steps/$(jq -r .id <<<"$gate")/approve" -d '{"planSha256":"0000"}')
[[ $refused != 204 ]] || fail "a wrong digest was accepted"
call -X POST "$api/api/runs/$run/steps/$(jq -r .id <<<"$gate")/approve" \
  --data-binary "$(jq -c '{planSha256: .approval.planSha256}' <<<"$gate")" >/dev/null
wait_run "$run" succeeded
[[ $(podman exec "$target" cat /var/lib/web-deployed) == "8080 $service" ]] || fail "the stage did not get its inputs"
[[ $(podman exec "$target" cat /var/lib/web-password) == "$password" ]] || fail "the secret did not arrive intact"
[[ $(podman exec "$target" cat /var/lib/web-site.conf) == 'server_name web;' ]] || fail "the service's folder did not reach the playbook"
[[ $(stage_state expose) == 1 && $(stage_state audit) == 1 ]] || fail "each OpenTofu stage should have one state version"
service_view | jq -e '.service.status == "in-sync"' >/dev/null || fail "the service is not in sync"
call "$api/api/guests/$(guest_id)" | jq -e '.guest.status == "in-sync" and (.services | length == 1)' >/dev/null || fail "the guest page does not list the service"

log "Nothing secret in logs or evidence"
logs_of "$run" >"$work/pipeline.log"
for secret in 'word-with-' "$(sed -n 2p "$work/keys/octopushy")"; do
  if grep -qF -- "$secret" "$work/pipeline.log"; then fail "a secret reached the attempt log"; fi
  if run_view "$run" | grep -qF -- "$secret"; then fail "a secret reached the run's evidence"; fi
  if grep -rqF -- "$secret" "$work/agent" "$work/agent.log" "$work/backend.log" 2>/dev/null; then fail "a secret reached the agent's disk or a log"; fi
done

log "One run per input hash"
sleep 7
[[ $(latest_pipeline) == "$run" ]] || fail "a second automatic run appeared"

log "With no manual policy a new commit deploys by itself, and a failed stage stops the rest"
previous=$run
write_pipeline automatic 9090 true
commit "Move the port, and stop at deploy"
sync_repository
run=$(wait_new_pipeline "$previous")
wait_run "$run" failed
run_view "$run" | jq -r '.run.detail'
run_view "$run" | jq -e '.run.detail | contains("Stage deploy failed")' >/dev/null || fail "the failed stage is not named"
run_view "$run" | jq -e '[.steps[] | select(.name=="approval")][0].approval.decidedBy == "policy"' >/dev/null || fail "this should have been automatic"
[[ $(run_view "$run" | jq -r '[.steps[] | select(.stage=="audit" and .name=="apply")][0].status') == skipped ]] || fail "a later stage ran after a failure"
[[ $(stage_state expose) == 2 ]] || fail "the stage before the failure should have applied, and stays applied"
service_view | jq -e '.service.status == "failed"' >/dev/null || fail "the service does not show its failure"
call "$api/api/guests/$(guest_id)" | jq -e '.guest.status == "in-sync"' >/dev/null || fail "a failed service marked its guest"

log "Retrying continues from that stage, with no new approval"
podman exec "$target" touch /var/lib/web-may-go
call -X POST "$api/api/runs/$run/retry-stage" >/dev/null
wait_run "$run" succeeded
[[ $(podman exec "$target" cat /var/lib/web-deployed) == "9090 $service" ]] || fail "the retried stage did not run"
run_view "$run" | jq -e '[.steps[] | select(.name=="apply" and .stage=="expose")][0].attempts | length == 1' >/dev/null || fail "the stage before it ran again"
run_view "$run" | jq -e '[.steps[] | select(.name=="playbook")][0].attempts | length == 2' >/dev/null || fail "the failed stage did not run again"
service_view | jq -e '.service.status == "in-sync"' >/dev/null || fail "the service is not in sync after the retry"

log "Deleting the folder shows it as missing and removes nothing"
rm -rf "$service_dir"
commit "Delete the service"
sync_repository
sleep 5
service_id=$(call "$api/api/guests/$(guest_id)" | jq -r '.services[0].id')
call "$api/api/services/$service_id" | jq -e '.service.status == "missing"' >/dev/null || fail "the service is not shown as missing"
[[ $(latest_pipeline) == "$run" ]] || fail "deleting the folder started a run"
target_has /var/lib/web-deployed || fail "something removed what the service deployed"

log "Passed ($runtime)"
