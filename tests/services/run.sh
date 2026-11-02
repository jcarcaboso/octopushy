#!/usr/bin/env bash
# The service layer, end to end, on the ha-db example: the backend, a real agent, real OpenTofu with
# the real proxmox-lxc root and bpg provider, a real ansible-playbook, and real PostgreSQL and
# VictoriaMetrics in a throwaway systemd container made from the pinned Debian 13 template. Only
# Proxmox is a stand-in (tests/technitium-lxc/fake_proxmox.py), so this proves nothing about
# Proxmox itself, or about an unprivileged LXC container.
#
# E2E_TOOL_RUNTIME=local (the default) runs Ansible and OpenTofu as the internal agent does, and needs
# /tmp on tmpfs. =podman starts the Ansible image built from agent/ansible, and OpenTofu's.
#
# This installs for real, so it is not an offline test: it needs rootless podman with outbound
# access to Debian's mirrors, github.com, the OpenTofu registry and download.proxmox.com. It has its own
# PostgreSQL and free ports, and removes what it starts. KEEP=1 leaves the containers for a look.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/services
runtime=${E2E_TOOL_RUNTIME:-local}
free_port() { python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'; }
api_port=$(free_port)
agent_port=$(free_port)
proxmox_port=$(free_port)
pg_port=$(free_port)
ssh_port=$(free_port)
api=http://127.0.0.1:$api_port
name=octopushy-services
subnet=10.199.3
target_ip=$subnet.40
allowed_ip=$subnet.50
stranger_ip=$subnet.51
image=$name:template
ansible_image=localhost/octopushy-ansible:services
guest=home/ziost/ha-db
postgresql=$guest/postgresql
victoriametrics=$guest/victoriametrics
# A quote, a backslash, a colon and a brace: what a careless template or pgpass would break.
pg_password="pg'Pass\\word:0123{{ x }}456"
vm_password='vm-Pass_word.0123+/=:@456'
pg_rolled='pg-Rolled_word.0123456789'
template_url=$(sed -n 's/^  url: //p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")
template_sha512=$(sed -n 's/^  checksum: sha512://p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")
pids=()

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
  podman rm -f -t 0 "$name-target" "$name-allowed" "$name-stranger" "$name-proxmox" "$name-pg" >/dev/null 2>&1 || true
  podman ps -q --filter label=io.octopushy.attempt=true | xargs -r podman rm -f >/dev/null 2>&1 || true
  podman network rm "$name" >/dev/null 2>&1 || true
  podman image rm "$image" >/dev/null 2>&1 || true
  # Files of the container's own users are outside this user's reach.
  podman unshare rm -rf "${work:?}/share" 2>/dev/null || true
}
trap '[[ ${KEEP:-0} == 1 ]] || cleanup' EXIT

in_target() { podman exec "$name-target" sh -c "$1"; }

log "Disposable resources"
cleanup
mkdir -p "$work"
rm -rf "${work:?}/lab" "${work:?}/server" "${work:?}/agent" "${work:?}/keys" "${work:?}/bin"
rm -f "${work:?}"/*.log "${work:?}"/*.json "${work:?}/service-logs" "${work:?}/service-evidence" "${work:?}/agent-samples" "${work:?}/stop-sampling"
mkdir -p "$work/keys" "$work/bin" "$work/share/ssh"
chmod 0700 "$work/share/ssh"
template=$work/$(basename "$template_url")
[[ -f $template ]] || curl -fsS -o "$template" "$template_url"
sha512sum -c --quiet - <<<"$template_sha512  $template"
podman import -q --change 'CMD ["/sbin/init"]' "$template" "$image" >/dev/null
podman network create --subnet "$subnet.0/24" "$name" >/dev/null

log "Building"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin octopushy)
agent_args=(--tool-runtime "$runtime")
if [[ $runtime == local ]]; then
  [[ $(stat -f -c %T /tmp) == tmpfs ]] || fail "the local runtime needs /tmp on tmpfs"
  python3 -m venv "$work/venv"
  "$work/venv/bin/pip" install --quiet --no-deps --require-hashes -r "$root/agent/ansible/requirements.lock"
  printf '#!/bin/sh\nexec "%s" "%s" "$@"\n' "$work/venv/bin/python3" "$root/agent/ansible/octopushy-ansible" >"$work/bin/octopushy-ansible"
  chmod 0755 "$work/bin/octopushy-ansible"
  podman run --rm --entrypoint cat ghcr.io/opentofu/opentofu:1.13.1 /usr/local/bin/tofu >"$work/bin/tofu"
  chmod 0755 "$work/bin/tofu"
  agent_args+=(--ansible-binary "$work/bin/octopushy-ansible" --tofu-binary "$work/bin/tofu")
else
  podman build -q -f "$root/agent/ansible/Containerfile" -t "$ansible_image" "$root" >/dev/null
  agent_args+=(--container-network host)
fi

log "The stand-in for Proxmox, and the machine it would create"
ssh-keygen -q -t ed25519 -N '' -C octopushy@ha-db -f "$work/keys/deploy"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/share/tls.key" -out "$work/share/tls.crt" -days 1 \
  -subj /CN=fake-proxmox -addext subjectAltName=IP:127.0.0.1 2>/dev/null
podman run -d --name "$name-proxmox" --network "$name" --ip "$subnet.10" -p "127.0.0.1:$proxmox_port:8006" -v "$work/share:/share:Z" \
  -v "$root/tests/technitium-lxc/fake_proxmox.py:/fake_proxmox.py:ro,Z" "$image" \
  python3 -I /fake_proxmox.py /share /share/tls.crt /share/tls.key >/dev/null
# Proxmox makes the host keys when it creates a container; this container's start stands for `pct create`.
podman run -d --name "$name-target" --hostname ha-db --network "$name" --ip "$target_ip" -p "127.0.0.1:$ssh_port:22" \
  --privileged --systemd=always -v "$work/share/ssh:/root/.ssh:z" "$image" /sbin/init >/dev/null
sleep 5
in_target 'ssh-keygen -A >/dev/null; systemctl enable --now ssh >/dev/null 2>&1'
for client in allowed stranger; do
  ip=$allowed_ip
  [[ $client == stranger ]] && ip=$stranger_ip
  podman run -d --name "$name-$client" --network "$name" --ip "$ip" "$image" sleep infinity >/dev/null
  podman exec "$name-$client" sh -c 'apt-get update -q >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get install -yq --no-install-recommends postgresql-client curl ca-certificates >/dev/null'
done

log "Octopushy's database"
podman run -d --name "$name-pg" -p "127.0.0.1:$pg_port:5432" --tmpfs /var/lib/postgresql \
  -e POSTGRES_USER=octopushy -e POSTGRES_PASSWORD=octopushy -e POSTGRES_DB=octopushy \
  docker.io/library/postgres:18-alpine >/dev/null
for _ in $(seq 1 60); do
  podman exec "$name-pg" pg_isready -q -h 127.0.0.1 -U octopushy -d octopushy && break
  sleep 1
done

log "The ha-db example, pointed at these"
lab=$work/lab
mkdir -p "$lab/labs/home/proxmox"
cp "$root/examples/lab/octopushy.yaml" "$lab/"
cp "$root/examples/lab/labs/home/lab.yaml" "$lab/labs/home/"
cp -r "$root/examples/lab/labs/home/proxmox/ziost" "$lab/labs/home/proxmox/"
cp -r "$root/examples/lab/recipes" "$root/examples/lab/templates" "$root/examples/lab/modules" "$lab/"
guest_dir=$lab/labs/home/proxmox/ziost/guests/ha-db
python3 -I - "$lab" "$proxmox_port" "$ssh_port" "$(cat "$work/keys/deploy.pub")" "$allowed_ip" <<'PY'
import pathlib, re, sys
lab, proxmox_port, ssh_port, public_key, allowed = sys.argv[1:]
root = pathlib.Path(lab)
def edit(path, *pairs):
    file = root / path
    text = file.read_text()
    for old, new in pairs:
        assert re.search(old, text), (path, old)
        text = re.sub(old, new, text)
    file.write_text(text)
edit("labs/home/proxmox/ziost/installation.yaml", (r"endpoint: \S+", f"endpoint: https://127.0.0.1:{proxmox_port}/"))
guest = "labs/home/proxmox/ziost/guests/ha-db"
edit(f"{guest}/guest.yaml",
     (r"    - ssh-ed25519 .*", f"    - {public_key}"),
     (r"  user: root .*", f"  address: 127.0.0.1\n  port: {ssh_port}\n  user: root"))
edit(f"{guest}/services/postgresql/service.yaml", (r"sources: \[10\.15\.40\.10\]", f"sources: [{allowed}]"))
edit("templates/debian-13-lxc/template.yaml", (r"availableOn: .*", "availableOn: [home/ziost]"))
PY
git -C "$lab" init -q -b main
commit() { git -C "$lab" add -A; git -C "$lab" -c user.name=e2e -c user.email=e2e@example.invalid commit -q -m "$1"; }
commit "The ha-db example"

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
  Octopushy__Tools__AnsibleImage=$ansible_image \
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

log "Account, repository, variables; the host key is not set yet"
code=$(tr -d '\n' <"$work/server/setup-code")
call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"e2e\",\"password\":\"an end-to-end password\"}" >/dev/null
set_variable proxmox/ziost/read-only-token 'octopushy-reader@pve!read=00000000-0000-4000-8000-0000000000e1' secret
set_variable proxmox/ziost/privileged-token 'octopushy-writer@pve!write=00000000-0000-4000-8000-0000000000e2' secret
set_variable ha-db/ssh-key "$(cat "$work/keys/deploy")" secret
set_variable ha-db/postgres-password "$pg_password" secret
set_variable ha-db/victoriametrics-password "$vm_password" secret
for key in ha-db/postgres-password ha-db/victoriametrics-password; do
  jq -n --arg key "$key" '{key: $key, protected: true}' | call -X POST "$api/api/variables/protection" --data-binary @- >/dev/null
done
call -X PUT "$api/api/repository" -d "{\"url\":\"$lab\",\"branch\":\"main\",\"pollSeconds\":3600,\"credentialKind\":\"none\"}" >/dev/null

log "Agent"
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"services-agent","labels":["home"]}' | jq -r .token)
OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
  --server "https://127.0.0.1:$agent_port" --name services-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
"$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" "${agent_args[@]}" >>"$work/agent.log" 2>&1 &
pids+=($!)

inventory_guest() { call "$api/api/inventory" | jq -c --arg a "$guest" '[.labs[].installations[].guests[] | select(.address==$a)][0]'; }
guest_id() { inventory_guest | jq -r .id; }
guest_status() { inventory_guest | jq -r .status; }
service_field() { inventory_guest | jq -r --arg a "$1" --arg f "$2" '.services[] | select(.address==$a) | .[$f]'; }
service_id() { service_field "$1" id; }
service_status() { service_field "$1" status; }
runs_of() { call "$api/api/runs?limit=200" | jq -c --arg a "$1" --arg o "$2" '[.[] | select(.address==$a and .operation==$o)]'; }
latest_run() { runs_of "$1" "$2" | jq -r '.[0].id // empty'; }
run_count() { runs_of "$1" "$2" | jq length; }
run_status() { call "$api/api/runs/$1" | jq -r .run.status; }
show_run() { call "$api/api/runs/$1" | jq '{status: .run.status, detail: .run.detail, steps: [.steps[] | {name, status, detail, attempts: [.attempts[] | {status, error}]}]}'; }
wait_run() {
  local run=$1 want=$2 status=""
  for _ in $(seq 1 300); do
    status=$(run_status "$run")
    [[ $status == "$want" ]] && { echo "run is $status"; return 0; }
    [[ $status =~ ^(succeeded|failed|cancelled|uncertain)$ ]] && break
    sleep 2
  done
  show_run "$run"
  fail "expected run $run to be $want, but it is $status"
}
# Waits for a condition, given as a shell command; a command here runs in this shell, so it sees these functions.
wait_for() {
  local what=$1
  shift
  for _ in $(seq 1 300); do
    eval "$*" >/dev/null 2>&1 && return 0
    sleep 2
  done
  call "$api/api/runs?limit=8" | jq -c '.[] | {address, operation, status, detail}'
  fail "timed out waiting: $what"
}
sync() { call -X POST "$api/api/repository/sync" >/dev/null; }
# The run a commit proposes: count before it, wait for one more, and print it.
next_run() { # address, operation, count before
  wait_for "a new $2 run of $1" "[[ \$(run_count $1 $2) -gt $3 ]]"
  latest_run "$1" "$2"
}
trust() {
  local detail
  detail=$(call "$api/api/services/$1")
  call -X POST "$api/api/resources/$1/trust-configure" --data-binary "$(jq -c '{codeDigest: .trust.codeDigest}' <<<"$detail")" >/dev/null
}
# A changed definition is a changed digest, so a protected secret needs trusting again.
retrust() { # run, service id
  wait_for "the run to stop for trust" "[[ \$(run_status $1) == blocked ]]"
  trust "$2"
}
approve() { # run, step name
  local gate
  gate=$(call "$api/api/runs/$1" | jq -c --arg s "$2" '.steps[] | select(.name==$s)')
  call -X POST "$api/api/runs/$1/steps/$(jq -r .id <<<"$gate")/approve" --data-binary "$(jq -c '{planSha256: .approval.planSha256}' <<<"$gate")" >/dev/null
}
logs_of() {
  local attempt
  for attempt in $(call "$api/api/runs/$1" | jq -r '.steps[].attempts[].id'); do
    call "$api/api/attempts/$attempt/log?limit=10000" | jq -r '.lines[].text'
  done
}
pg_from() { # client, password, then psql arguments
  printf '%s\n' "$2" | podman exec -i "$name-$1" sh -c 'IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -X -q -At -w "$@"' sh -h "$target_ip" -U homeassistant -d homeassistant "${@:3}"
}
vm_as() { # password, then curl's arguments, from the allowed client
  printf 'user = "homeassistant:%s"\n' "$1" | podman exec -i "$name-allowed" curl -sS -K - "${@:2}"
}
vm_code() { vm_as "$1" -o /dev/null -w '%{http_code}' "${@:2}" || true; }
vm_args() { in_target 'cat /proc/$(pidof victoria-metrics-prod)/cmdline | tr "\0" " "'; }
vm_url=http://$target_ip:8428

log "Provisioning with real OpenTofu; setup waits for its pin, and services wait for setup"
sync
trust_fixture_installations "$lab" "$(git -C "$lab" rev-parse HEAD)"
wait_for "the machine is provisioned" '[[ $(runs_of $guest provision | jq -r ".[0].status") == succeeded ]]'
grep -qF "$(cut -d' ' -f2 "$work/keys/deploy.pub")" "$work/share/ssh/authorized_keys" || fail "Proxmox was not given the login key"
grep -q 'order=1,up=30' "$work/share/requests.log" || fail "the create request has no startup order"
grep -q 'nesting=1' "$work/share/requests.log" || fail "the create request has no nesting"
wait_for "setup is proposed" '[[ -n $(latest_run $guest configure) ]]'
setup=$(latest_run "$guest" configure)
wait_for "setup blocks on the pin" "[[ \$(run_status $setup) == blocked ]]"
call "$api/api/runs/$setup" | jq -r .run.detail | grep -q 'ha-db/host-key' || { show_run "$setup"; fail "the run does not say which pin is missing"; }
[[ $(service_status "$postgresql") == waiting && $(service_status "$victoriametrics") == waiting ]] || fail "services should wait for setup"
service_field "$postgresql" statusDetail | grep -q "guest's setup" || fail "a waiting service should say what it waits for"
[[ $(run_count "$postgresql" configure) == 0 ]] || fail "a service ran before its guest's setup"

log "The first pin, read the way pct exec reads it"
host_key=$(in_target 'cat /etc/ssh/ssh_host_ed25519_key.pub')
set_variable ha-db/host-key "$host_key" plain
wait_run "$setup" succeeded
in_target 'command -v python3 >/dev/null && systemctl is-enabled cron >/dev/null' || fail "setup did not prepare the machine"

log "Protected secrets wait for trust"
wait_for "postgresql is proposed" '[[ -n $(latest_run $postgresql configure) ]]'
wait_for "victoriametrics is proposed" '[[ -n $(latest_run $victoriametrics configure) ]]'
pg_run=$(latest_run "$postgresql" configure)
vm_run=$(latest_run "$victoriametrics" configure)
pg_id=$(service_id "$postgresql")
vm_id=$(service_id "$victoriametrics")
wait_for "the service blocks on trust" "[[ \$(run_status $pg_run) == blocked ]]"
call "$api/api/runs/$pg_run" | jq -r .run.detail | grep -q 'Waiting for trust' || { show_run "$pg_run"; fail "the service should wait for trust"; }
trust "$pg_id"
trust "$vm_id"
wait_run "$pg_run" succeeded
wait_run "$vm_run" succeeded

log "They took turns, and each has its own history"
call "$api/api/runs/$pg_run" >"$work/pg-run.json"
call "$api/api/runs/$vm_run" >"$work/vm-run.json"
python3 -I - "$work/pg-run.json" "$work/vm-run.json" <<'PY'
import json, sys
def window(path):
    step = next(s for s in json.load(open(path))["steps"] if s["name"] == "playbook")
    return step["startedAt"], step["finishedAt"]
a, b = window(sys.argv[1]), window(sys.argv[2])
assert a[1] <= b[0] or b[1] <= a[0], (a, b)
PY
[[ $(service_status "$postgresql") == in-sync && $(service_status "$victoriametrics") == in-sync ]] || fail "both services should be in sync"
[[ $(call "$api/api/services/$pg_id" | jq '.runs | length') == 1 ]] || fail "a service's history should be its own"
call "$api/api/runs/$pg_run" | jq -e '[.steps[] | select(.name=="preview")][0].configurePreview | .ports[0].sources[0] and (.secrets | length) >= 2' >/dev/null \
  || fail "the preview should show the ports and name the secrets"

log "Real logins, and a real refusal from the wrong address"
[[ $(pg_from allowed "$pg_password" -c 'select 1') == 1 ]] || fail "the allowed address cannot log in"
out=$(pg_from stranger "$pg_password" -c 'select 1' 2>&1 || true)
grep -q 'no pg_hba.conf entry' <<<"$out" || { echo "$out"; fail "the wrong address was not refused by pg_hba"; }
echo "from the wrong address, with the right password: $(tail -1 <<<"$out")"
if pg_from allowed 'another-password-0123456789' -c 'select 1' >/dev/null 2>&1; then fail "a wrong password was accepted"; fi
pg_from allowed "$pg_password" -c 'create table readings (note text); insert into readings values ($$kept$$)' >/dev/null
[[ $(in_target 'grep -vc "^#" /etc/postgresql/17/main/pg_hba.conf') == 3 ]] || fail "pg_hba.conf lists more than postgres, the role by socket, and one client"
[[ $(podman exec "$name-allowed" curl -s -o /dev/null -w '%{http_code}' "$vm_url/health") == 200 ]] || fail "VictoriaMetrics does not answer"
[[ $(podman exec "$name-allowed" curl -s -o /dev/null -w '%{http_code}' "$vm_url/api/v1/query?query=1") == 401 ]] || fail "VictoriaMetrics answers without a password"
[[ $(vm_code 'another-password-0123456789' "$vm_url/api/v1/query?query=1") == 401 ]] || fail "a wrong password was accepted"
[[ $(vm_code "$vm_password" "$vm_url/api/v1/query?query=1") == 200 ]] || fail "the password does not work"
vm_as "$vm_password" -o /dev/null --data-binary 'ha_value{entity="x"} 1
ha_attribute{entity="x"} 2' "$vm_url/api/v1/import/prometheus"
sleep 3
names=$(vm_as "$vm_password" "$vm_url/api/v1/label/__name__/values?start=$(($(date +%s) - 3600))")
grep -q '"ha_value"' <<<"$names" || { echo "$names"; fail "ha_value was dropped"; }
if grep -q '"ha_attribute"' <<<"$names"; then fail "the relabel rule kept ha_attribute"; fi
grep -qF -- -retentionPeriod=2y <<<"$(vm_args)" || fail "VictoriaMetrics lacks its retention"
if grep -qF 'Pass_word' <<<"$(vm_args)"; then fail "the password is in VictoriaMetrics' command line"; fi
[[ $(in_target 'stat -c %a /etc/victoria-metrics/auth.env') == 600 ]] || fail "the credentials file is not private"

log "The dump"
[[ $(in_target "grep -v '^#' /etc/cron.d/postgresql-dump") == '15 5 * * * postgres /usr/local/sbin/postgresql-dump' ]] || fail "unexpected cron line"
in_target 'runuser -u postgres -- /usr/local/sbin/postgresql-dump; runuser -u postgres -- /usr/local/sbin/postgresql-dump'
[[ $(in_target 'ls /var/backups/postgresql') =~ ^homeassistant-(Mon|Tue|Wed|Thu|Fri|Sat|Sun)\.dump$ ]] || fail "unexpected dumps"
in_target 'runuser -u postgres -- createdb restored && runuser -u postgres -- pg_restore -d restored /var/backups/postgresql/homeassistant-*.dump'
[[ $(in_target 'runuser -u postgres -- psql -X -q -At -d restored -c "select note from readings"') == kept ]] || fail "the dump does not restore"
in_target 'runuser -u postgres -- dropdb restored'

log "Running again changes nothing and loses nothing"
sums='sha256sum /etc/postgresql/17/main/pg_hba.conf /etc/postgresql/17/main/conf.d/octopushy.conf /etc/victoria-metrics/* /etc/systemd/system/victoria-metrics.service /usr/local/sbin/postgresql-dump /etc/cron.d/postgresql-dump'
before=$(in_target "$sums")
pids_before=$(in_target 'pidof victoria-metrics-prod; systemctl show -p MainPID --value postgresql@17-main')
# The password stays out of everything while it is used: sampled on the guest, and here.
podman exec -d "$name-target" sh -c 'while [ ! -e /root/stop-sampling ]; do ps -eww -o args >>/root/samples; sleep 0.05; done' >/dev/null
(while [[ ! -e $work/stop-sampling ]]; do ps -eww -o args; sleep 0.05; done >"$work/agent-samples") &
sampler=$!
run=$(call -X POST "$api/api/services/$pg_id/run" -d '{"action":"deploy"}' | jq -r .runId)
wait_run "$run" succeeded
run=$(call -X POST "$api/api/services/$vm_id/run" -d '{"action":"deploy"}' | jq -r .runId)
wait_run "$run" succeeded
# Stopped before anything searches for the passwords, whose command lines hold what they look for.
touch "$work/stop-sampling"
in_target 'touch /root/stop-sampling; sleep 1'
wait "$sampler"
[[ $(in_target "$sums") == "$before" ]] || fail "a second run changed a file"
[[ $(in_target 'pidof victoria-metrics-prod; systemctl show -p MainPID --value postgresql@17-main') == "$pids_before" ]] || fail "a second run restarted something"
[[ $(pg_from allowed "$pg_password" -c 'select note from readings') == kept ]] || fail "the data did not survive a second run"

log "No password anywhere it should not be"
for run in "$pg_run" "$vm_run"; do
  logs_of "$run" >>"$work/service-logs"
  call "$api/api/runs/$run" >>"$work/service-evidence"
done
[[ -s $work/service-logs ]] || fail "no logs to search"
grep -q 'Set the password' "$work/service-logs" || fail "the logs do not look like the playbook's"
for secret in "$pg_password" "$vm_password" 'Pass\word' 'Pass_word' "$(sed -n 2p "$work/keys/deploy")"; do
  for file in "$work/service-logs" "$work/service-evidence" "$work/agent.log" "$work/backend.log" "$work/agent-samples"; do
    if grep -qF -- "$secret" "$file"; then fail "a secret reached $file"; fi
  done
  if grep -rqF -- "$secret" "$work/agent" "$lab" 2>/dev/null; then fail "a secret reached the agent's disk or the repository"; fi
  if grep -rqF -- "$secret" "$work/server/logs" 2>/dev/null; then fail "a secret reached the server's logs"; fi
done
if call "$api/api/guests/$(guest_id)/state" | grep -qF -e 'Pass_word' -e 'Pass\word'; then fail "a password reached the state"; fi
if podman exec "$name-pg" pg_dump -U octopushy octopushy | grep -qF -e 'Pass_word' -e 'Pass\word'; then fail "a password is readable in Octopushy's database"; fi
if in_target "grep -F -e 'Pass_word' -e 'Pass\\word' /root/samples" >/dev/null; then fail "a password was on a command line on the guest"; fi
[[ $(in_target 'wc -l </root/samples') -gt 100 ]] || fail "the sampling saw nothing"
if in_target "grep -rlF -e 'Pass\\word' /etc /run /var /root /tmp /opt /usr/local /home 2>/dev/null"; then fail "the PostgreSQL password is on the guest in the clear"; fi
[[ $(in_target "grep -rlF -e '$vm_password' /etc /run /var /root /tmp /opt /usr/local /home 2>/dev/null") == /etc/victoria-metrics/auth.env ]] \
  || fail "the VictoriaMetrics password is somewhere but its one file"
if in_target 'journalctl --no-pager 2>/dev/null | grep -F -e Pass_word'; then fail "a password reached the guest's journal"; fi
if in_target 'grep -rlF Pass /var/log/postgresql 2>/dev/null'; then fail "a password reached PostgreSQL's log"; fi
if compgen -G '/tmp/octopushy-attempt-*' >/dev/null; then fail "a secret directory stayed on the agent"; fi

log "A change to one service runs that service only"
vm_runs=$(run_count "$victoriametrics" configure)
setup_runs=$(run_count "$guest" configure)
pg_runs=$(run_count "$postgresql" configure)
sed -i 's/max_connections: "20"/max_connections: "21"/' "$guest_dir/services/postgresql/service.yaml"
commit "More connections"
sync
run=$(next_run "$postgresql" configure "$pg_runs")
retrust "$run" "$pg_id"
wait_run "$run" succeeded
[[ $(in_target 'runuser -u postgres -- psql -X -At -c "show max_connections"') == 21 ]] || fail "the setting did not reach PostgreSQL"
[[ $(run_count "$victoriametrics" configure) == "$vm_runs" ]] || fail "a change to one service ran another"
[[ $(run_count "$guest" configure) == "$setup_runs" ]] || fail "a change to a service configured its guest again"

log "A failed service marks neither its guest nor its sibling"
pg_runs=$(run_count "$postgresql" configure)
sed -i 's/postgresql_version: 17/postgresql_version: 99/' "$guest_dir/services/postgresql/service.yaml"
commit "A version Debian does not have"
sync
run=$(next_run "$postgresql" configure "$pg_runs")
retrust "$run" "$pg_id"
wait_run "$run" failed
[[ $(service_status "$postgresql") == failed ]] || fail "the service should be failed"
[[ $(service_status "$victoriametrics") == in-sync ]] || fail "the sibling should stay in sync"
[[ $(guest_status) == in-sync ]] || fail "the guest should stay in sync"
[[ $(pg_from allowed "$pg_password" -c 'select note from readings') == kept ]] || fail "the database should still answer"
pg_runs=$(run_count "$postgresql" configure)
sed -i 's/postgresql_version: 99/postgresql_version: 17/' "$guest_dir/services/postgresql/service.yaml"
commit "Back to a real version"
sync
run=$(next_run "$postgresql" configure "$pg_runs")
retrust "$run" "$pg_id"
wait_run "$run" succeeded

log "Lowering retention is refused by deploy, and destructive on its own action"
vm_runs=$(run_count "$victoriametrics" configure)
sed -i 's/victoriametrics_retention: 2y/victoriametrics_retention: 1y/' "$guest_dir/services/victoriametrics/service.yaml"
commit "Less retention"
sync
run=$(next_run "$victoriametrics" configure "$vm_runs")
retrust "$run" "$vm_id"
wait_run "$run" failed
logs_of "$run" | grep -q 'Run the shrink-retention action' || fail "the refusal does not say what to do"
grep -qF -- -retentionPeriod=2y <<<"$(vm_args)" || fail "deploy changed the retention"
run=$(call -X POST "$api/api/services/$vm_id/run" -d '{"action":"shrink-retention"}' | jq -r .runId)
wait_run "$run" awaiting-approval
call "$api/api/runs/$run" | jq -r .run.detail | grep -q 'destructive' || { show_run "$run"; fail "it should say it is destructive"; }
approve "$run" approval
wait_run "$run" succeeded
grep -qF -- -retentionPeriod=1y <<<"$(vm_args)" || fail "the retention did not change"
[[ $(service_status "$victoriametrics") == in-sync ]] || fail "the service should be in sync after its destructive action"

log "A password rolls with a new store version and a commit"
pg_runs=$(run_count "$postgresql" configure)
version=$(jq -n --arg value "$pg_rolled" '{key: "ha-db/postgres-password", value: $value}' | call -X POST "$api/api/variables/candidates" --data-binary @- | jq -r .version)
jq -n --argjson version "$version" '{key: "ha-db/postgres-password", version: $version, expectedCurrentVersion: ($version - 1), verificationNote: "End to end."}' |
  call -X POST "$api/api/variables/promote" --data-binary @- >/dev/null
sleep 5
[[ $(run_count "$postgresql" configure) == "$pg_runs" ]] || fail "a store change alone should start nothing"
sed -i 's/credentials_revision: 1/credentials_revision: 2/' "$guest_dir/services/postgresql/service.yaml"
commit "Roll the password"
sync
run=$(next_run "$postgresql" configure "$pg_runs")
retrust "$run" "$pg_id"
wait_run "$run" succeeded
[[ $(pg_from allowed "$pg_rolled" -c 'select note from readings') == kept ]] || fail "the new password does not work, or the data is gone"
if pg_from allowed "$pg_password" -c 'select 1' >/dev/null 2>&1; then fail "the old password still works"; fi

log "The guard stops a real plan that would replace the stateful container"
provisions=$(run_count "$guest" provision)
sed -i 's/datastore: vm-ssd/datastore: other-ssd/' "$guest_dir/guest.yaml"
commit "Move the root disk"
sync
run=$(next_run "$guest" provision "$provisions")
wait_run "$run" failed
detail=$(call "$api/api/runs/$run" | jq -r .run.detail)
grep -q 'is stateful, and this plan would destroy container 102' <<<"$detail" || { show_run "$run"; fail "the guard did not refuse it"; }
if grep -q 'exclude' <<<"$detail"; then fail "the refusal offers a way round it"; fi
[[ $(call "$api/api/runs/$run" | jq '[.steps[] | select(.name=="apply")][0].attempts | length') == 0 ]] || fail "an apply ran"
if grep -q '"method": "DELETE"' "$work/share/requests.log"; then fail "something touched the container"; fi
[[ $(pg_from allowed "$pg_rolled" -c 'select note from readings') == kept ]] || fail "the data should be untouched"
git -C "$lab" revert --no-edit HEAD >/dev/null
sync
wait_for "the guest settles" '[[ $(guest_status) == in-sync ]]'

log "Deleting a service's folder waits for a person, and leaves the data"
vm_runs=$(run_count "$victoriametrics" configure)
rm -rf "${guest_dir:?}/services/victoriametrics"
commit "No more metrics"
sync
run=$(next_run "$victoriametrics" configure "$vm_runs")
wait_run "$run" awaiting-approval
[[ $(in_target 'systemctl is-active victoria-metrics') == active ]] || fail "nothing should have stopped yet"
approve "$run" approval
wait_run "$run" succeeded
if [[ $(in_target 'systemctl is-active victoria-metrics || true') == active ]]; then fail "the service should be stopped"; fi
in_target 'test -d /var/lib/victoria-metrics/data' || fail "the data should stay on disk"
[[ $(inventory_guest | jq --arg a "$victoriametrics" '[.services[] | select(.address==$a)] | length') == 0 ]] || fail "a removed service should leave the inventory"

log "Passed ($runtime)"
