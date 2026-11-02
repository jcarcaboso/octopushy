#!/usr/bin/env bash
# The ha-db recipe against disposable local containers. OpenTofu and the bpg/proxmox provider are
# real, and so is the install: a systemd container made from the pinned Debian template, with real
# PostgreSQL 17 and real VictoriaMetrics in it. Only Proxmox is a stand-in
# (tests/lxc-app/fake_proxmox.py), so this proves nothing about Proxmox itself, its firewall, or an
# unprivileged LXC container. It does prove what OpenTofu does with the stateful guard, which is
# OpenTofu's and not Proxmox's.
#
# Plans and applies in separate steps, as the agent does, and checks that neither password is in
# the saved plan, the state, the logs, a command line, or the container (beyond the one file that
# needs it). Every resource is named octopushy-ha-db-* and removed by that exact name.
#
# This installs for real, so it is not an offline test: it needs rootless podman on a network that
# reaches the sites `reachable` lists below, and it stops with a clear message if it cannot,
# including on an --internal network.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/ha-db
name=octopushy-ha-db
image=$name:template
tofu_image=ghcr.io/opentofu/opentofu:1.13.1
module=modules/opentofu/ha-db
token='root@pam!fake=00000000-0000-4000-8000-000000000000'
address=10.199.1.40
client=10.199.1.50
postgres_password='pg-Pass_word.0123+/=:@456'
victoriametrics_password='vm-Pass_word.0123+/=:@456'
template_url=$(sed -n 's/^  url: //p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")
template_sha512=$(sed -n 's/^  checksum: sha512://p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")
vm_sha256=7180e65913d55ab99c0be8f4a3ad17a0fafe5598a21ff6327fecba3190d7b176
requests=$work/share/requests.log

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
cleanup() {
  podman rm -f -t 0 "$name-ct" "$name-client" "$name-proxmox" >/dev/null 2>&1 || true
  podman network rm "$name" >/dev/null 2>&1 || true
  podman image rm "$image" >/dev/null 2>&1 || true
  # Files of the container's own users are outside this user's reach.
  podman unshare rm -rf "$work/share" 2>/dev/null || true
}
# KEEP=1 leaves the containers for a look at a failure; the next run removes them first.
trap '[[ ${KEEP:-0} == 1 ]] || cleanup' EXIT

in_ct() { podman exec "$name-ct" sh -c "$1"; }

# Variables are passed by name, so that neither password is on a command line: podman copies them
# from this shell's environment.
tofu() {
  podman run --rm --name "$name-tofu" --log-driver=none --cap-drop=all --security-opt=no-new-privileges \
    --read-only --tmpfs /tmp:rw,mode=1777 --userns=keep-id --network "$name" \
    -v "$work/run:/w:Z" -w "/w/repo/${dir:-$module}" -e HOME=/w/home -e TF_IN_AUTOMATION=1 -e TF_INPUT=0 \
    -e TF_PLUGIN_CACHE_DIR=/w/cache -e PROXMOX_VE_ENDPOINT=https://10.199.1.10:8006/ -e PROXMOX_VE_INSECURE=true \
    -e PROXMOX_VE_API_TOKEN="$token" ${tofu_env:-} "$tofu_image" "$@"
}
plan() { tofu plan -input=false -no-color -detailed-exitcode -out=/w/out/plan.bin "$@" || [[ $? == 2 ]]; }
passwords() { # the pair the next apply is given, or none
  export TF_VAR_postgres_password=${1-$postgres_password} TF_VAR_victoriametrics_password=${2-$victoriametrics_password}
  tofu_env="-e TF_VAR_postgres_password -e TF_VAR_victoriametrics_password"
}
apply() {
  passwords "$@"
  tofu apply -input=false -no-color /w/out/plan.bin 2>&1 | tee -a "$work/apply.log"
  return "${PIPESTATUS[0]}"
}
installs() {
  apply "$@" | grep -E 'ha-db: PostgreSQL' || { tail -25 "$work/apply.log"; fail "the apply failed"; }
}
# A plan with no password at all, as Octopushy makes it.
plan_unsealed() { tofu_env="" plan "$@"; }
summary() { grep -E '^(Plan:|No changes)' | tail -1; }

start_container() {
  podman run -d --name "$name-ct" --hostname ha-db --network "$name" --ip "$address" --privileged --systemd=always \
    -v "$work/share/ssh:/root/.ssh:z" "$image" /sbin/init >/dev/null
  sleep 5
  in_ct 'systemctl enable --now ssh >/dev/null 2>&1'
}

# What the container and OpenTofu download from, as docs/lxc-recipes.md lists them.
reachable() {
  local check='
import socket, sys
missing = []
for host, port in (("registry.opentofu.org", 443), ("github.com", 443), ("deb.debian.org", 80)):
    try:
        socket.create_connection((host, port), timeout=10).close()
    except OSError as error:
        missing.append(f"{host}:{port} ({error})")
if missing:
    print("\n".join(missing))
    sys.exit(1)
'
  if ! out=$(podman run --rm --name "$name-preflight" --network "$name" docker.io/library/python:3-alpine python3 -I -c "$check" 2>&1); then
    echo "This test installs for real and needs outbound access, which network $name does not have:" >&2
    echo "$out" >&2
    echo "It cannot pass on an --internal network." >&2
    exit 2
  fi
  echo "the download sites answer"
}

# Asks VictoriaMetrics as the test does, the password on stdin and not in the arguments.
vm() { # password, then curl's arguments
  local password=$1; shift
  printf 'user = "homeassistant:%s"\n' "$password" | podman exec -i "$name-ct" curl -sS -K - "$@"
}
vm_code() { vm "$1" -o /dev/null -w '%{http_code}' "${@:2}" || true; }
pg_as() { # password, database user, then psql arguments, from the allowed client
  printf '%s' "$1" | podman exec -i "$name-client" sh -c 'IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -X -q -At -w "$@"' sh "${@:2}"
}
as_postgres() { podman exec -i "$name-ct" runuser -u postgres -- psql -X -q -At -d homeassistant "$@"; }

log "Disposable resources"
cleanup
mkdir -p "$work/run/home" "$work/run/cache" "$work/run/out" "$work/share/ssh"
chmod 0700 "$work/share/ssh"
rm -f "$work/apply.log"
template=$work/$(basename "$template_url")
[[ -f $template ]] || curl -fsS -o "$template" "$template_url"
sha512sum -c --quiet - <<<"$template_sha512  $template"
podman import -q --change 'CMD ["/sbin/init"]' "$template" "$image" >/dev/null
podman network create --subnet 10.199.1.0/24 "$name" >/dev/null
reachable
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/share/tls.key" -out "$work/share/tls.crt" -days 1 \
  -subj /CN=fake-proxmox -addext subjectAltName=IP:10.199.1.10 2>/dev/null
podman run -d --name "$name-proxmox" --network "$name" --ip 10.199.1.10 -v "$work/share:/share:Z" \
  -v "$root/tests/lxc-app/fake_proxmox.py:/fake_proxmox.py:ro,Z" docker.io/library/python:3-alpine \
  python3 -I /fake_proxmox.py /share /share/tls.crt /share/tls.key ziost >/dev/null
start_container
# The one address pg_hba lets in.
podman run -d --name "$name-client" --network "$name" --ip "$client" "$image" sleep infinity >/dev/null
podman exec "$name-client" sh -c 'apt-get update -q >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get install -yq --no-install-recommends postgresql-client curl ca-certificates >/dev/null'

log "Repository, as the agent lays it out"
rm -rf "$work/run/repo" && mkdir -p "$work/run/repo/modules/opentofu" "$work/run/state"
cp -r "$root"/examples/lab/modules/opentofu/{proxmox-lxc,lxc-app,ha-db} "$work/run/repo/modules/opentofu/"
tfvars=$work/run/repo/$module/octopushy.auto.tfvars.json
write_vars() { # revision, cores, memory, retention, version, sha256, vmid
  cat >"$tfvars" <<JSON
{
  "octopushy": { "address": "home/ziost/ha-db", "stack": "home.ziost.ha-db", "lab": "home",
    "installation": "ziost", "revision": "test" },
  "guest": { "id": "ha-db", "vmid": ${7:-102}, "host": "ziost",
    "hardware": { "cores": ${2:-2}, "memory": ${3:-1024}, "swap": 256, "disks": [{ "size": 32, "datastore": "vm-ssd" }] },
    "network": { "bridge": "apps", "mode": "static", "address": "$address/24", "gateway": "10.199.1.1" },
    "inputs": { "startup": { "order": 1, "upDelay": 30 }, "postgresql": { "clients": ["$client"] },
      "victoriametrics": { "version": "${5:-1.155.0}", "sha256": "${6:-$vm_sha256}", "retention": "${4:-2y}" },
      "credentialsRevision": ${1:-1} } },
  "host": { "id": "ziost", "datastores": { "images": "local", "disks": "vm-ssd" }, "bridges": ["apps"] },
  "template": { "id": "debian-13-lxc", "os": "debian", "source": { "url": "$template_url",
    "checksum": "$template_sha512", "checksumAlgorithm": "sha512", "fileName": "$(basename "$template_url")" } }
}
JSON
}
write_vars
state() { printf '{"terraform":{"backend":{"local":{"path":"../../../state/%s"}}}}' "$1" >"$work/run/repo/$module/octopushy_backend.tf.json"; }
state terraform.tfstate
tofu init -input=false -no-color -lockfile=readonly | tail -1

log "Inputs that are not what they should be are refused before anything is planned"
cp "$tfvars" "$work/tfvars.kept"
refuse() { # a python expression on the variables, and the words the refusal must have
  python3 -I - "$tfvars" "$1" <<'PY'
import json, sys
variables = json.load(open(sys.argv[1]))
exec(sys.argv[2])
json.dump(variables, open(sys.argv[1], "w"))
PY
  if tofu plan -input=false -no-color >"$work/hostile.log" 2>&1; then fail "'$1' was accepted"; fi
  grep -q "$2" "$work/hostile.log" || { tail -8 "$work/hostile.log"; fail "unexpected failure for '$1'"; }
  cp "$work/tfvars.kept" "$tfvars"
}
refuse 'variables["guest"]["inputs"]["postgresql"]["clients"] = ["10.0.0.1; touch /pwned #"]' 'inputs.postgresql.clients'
refuse 'variables["guest"]["inputs"]["postgresql"]["clients"] = ["10.15.40.0/24"]' 'inputs.postgresql.clients'
refuse 'variables["guest"]["inputs"]["postgresql"]["clients"] = []' 'inputs.postgresql.clients'
refuse 'variables["guest"]["inputs"]["victoriametrics"]["retention"] = "2y; touch /pwned"' 'inputs.victoriametrics'
refuse 'variables["guest"]["inputs"]["victoriametrics"]["sha256"] = "$(touch /pwned)"' 'inputs.victoriametrics'
refuse 'variables["guest"]["inputs"]["victoriametrics"]["version"] = "1.155.0/../../x"' 'inputs.victoriametrics'
refuse 'variables["guest"]["network"]["address"] = "10.199.1.40/24; touch /pwned"' 'network.address'
refuse 'variables["guest"]["network"]["mode"] = "dhcp"' 'static'
refuse 'variables["guest"]["hardware"]["mountPoints"] = [{"path": "/data", "size": 8}]' 'no mount points'
refuse 'variables["guest"]["inputs"]["credentialsRevision"] = 0' 'credentialsRevision'
in_ct '! test -e /pwned'
for bad in 'short' "has'quote-in-it-0123456789" 'has space in it 0123456789' "$(printf 'line\nbreak0123456789ab')"; do
  if TF_VAR_postgres_password=$bad tofu_env="-e TF_VAR_postgres_password" tofu plan -input=false -no-color >"$work/hostile.log" 2>&1; then
    fail "a password of '$bad' was accepted"
  fi
  grep -q 'The PostgreSQL password needs' "$work/hostile.log" || { tail -5 "$work/hostile.log"; fail "unexpected failure for the password '$bad'"; }
done
echo "ten inputs and four passwords refused"

log "First apply"
# What a local user of the container could see while the install is under way.
in_ct 'rm -f /root/samples /root/stop-sampling
( while [ ! -e /root/stop-sampling ]; do
    ps -eww -o args
    ls -lR /run/octopushy-install /run/octopushy-ha-db 2>/dev/null
    sleep 0.05
  done >>/root/samples 2>&1 ) >/dev/null 2>&1 &'
plan | summary
installs
in_ct 'touch /root/stop-sampling; sleep 1'
[[ $(in_ct 'grep -c "apt-get" /root/samples') -gt 0 ]] || fail "the sampling did not see apt-get run"
[[ $(in_ct 'grep -c "victoria-metrics-prod" /root/samples') -gt 0 ]] || fail "the sampling did not see VictoriaMetrics start"
if in_ct "grep -F -e '$postgres_password' -e '$victoriametrics_password' -e 'Pass_word' /root/samples" >/dev/null; then
  fail "a password was in a command line while the install ran"
fi
modes=$(in_ct 'grep -E " (postgres_password|victoriametrics_password|pgpass|secrets)$" /root/samples | cut -c1-12 | sort -u')
[[ $modes == *-rw-------* ]] || { echo "$modes"; fail "a secret was not a file of mode 0600"; }
[[ $(grep -vcE '^(-rw-------|drwx------)' <<<"$modes") == 0 ]] || { echo "$modes"; fail "something was readable by others"; }
in_ct 'rm -f /root/samples /root/stop-sampling'
echo "no password in any command line, and every secret a file of mode 0600"
in_ct 'systemctl is-active postgresql@17-main victoria-metrics cron; ! test -e /run/octopushy-install; ! test -e /run/octopushy-ha-db'
[[ $(plan | grep -c 'No changes') == 1 ]] || fail "a second plan should have no changes"

log "The request Proxmox received"
create=$(grep '"/nodes/ziost/lxc"' "$requests")
for setting in '"vmid": "102"' '"unprivileged": "1"' '"features": "nesting=1"' '"protection": "1"' '"onboot": "1"' \
  '"cores": "2"' '"memory": "1024"' '"swap": "256"' '"hostname": "ha-db"' '"rootfs": "volume=vm-ssd:32"' \
  'bridge=apps' "ip=$address/24" '"startup": "order=1,up=30"'; do
  grep -qF -- "$setting" <<<"$create" || { echo "$create"; fail "the create request has no $setting"; }
done
if grep -q UNHANDLED "$requests"; then grep UNHANDLED "$requests"; fail "the provider made a request the stand-in does not know"; fi
echo "order 1 and upDelay 30 reached Proxmox as startup order=1,up=30"

# SKIP_TO_GUARD=1 goes from the first install straight to the guard, for working on it.
if [[ ${SKIP_TO_GUARD:-} != 1 ]]; then
log "PostgreSQL"
[[ $(as_postgres -c "select rolname, rolcanlogin from pg_roles where rolname = 'homeassistant'") == homeassistant\|t ]] || fail "no homeassistant role that can log in"
[[ $(as_postgres -c "select pg_catalog.pg_get_userbyid(datdba), pg_encoding_to_char(encoding) from pg_database where datname = 'homeassistant'") == homeassistant\|UTF8 ]] || fail "the database is not homeassistant's"
settings=$(for setting in shared_buffers max_connections wal_compression listen_addresses; do as_postgres -c "show $setting"; done | tr '\n' ' ')
[[ $settings == '192MB 20 pglz * ' ]] || fail "the settings of postgresql-ha.conf are not in force: $settings"
in_ct 'cat /etc/postgresql/17/main/pg_hba.conf'
[[ $(in_ct 'grep -vc "^#" /etc/postgresql/17/main/pg_hba.conf') == 3 ]] || fail "pg_hba.conf lists more than postgres, the role by socket, and one client"
grep -q "10.199.1.50/32" <(in_ct 'cat /etc/postgresql/17/main/pg_hba.conf') || fail "the client is not in pg_hba.conf"
[[ $(pg_as "$postgres_password" -h "$address" -U homeassistant -d homeassistant -c 'select current_user') == homeassistant ]] || fail "Home Assistant's address cannot log in"
if pg_as 'another-password-0123456789' -h "$address" -U homeassistant -d homeassistant -c 'select 1' >/dev/null 2>&1; then fail "a wrong password was accepted"; fi
if out=$(printf '%s' "$postgres_password" | podman exec -i "$name-ct" sh -c 'IFS= read -r PGPASSWORD; export PGPASSWORD; psql -X -q -At -w -h '"$address"' -U homeassistant -d homeassistant -c "select 1"' 2>&1); then
  fail "an address that is not Home Assistant's got in with the right password"
fi
grep -q 'no pg_hba.conf entry' <<<"$out" || { echo "$out"; fail "unexpected refusal: $out"; }
if out=$(printf '%s' "$postgres_password" | podman exec -i "$name-client" sh -c 'IFS= read -r PGPASSWORD; export PGPASSWORD; psql -X -q -At -w -h '"$address"' -U homeassistant -d postgres -c "select 1"' 2>&1); then
  fail "Home Assistant's address reached another database"
fi
echo "the right password from the right address only, and only to its own database"

log "VictoriaMetrics"
[[ $(in_ct 'systemctl show -p User --value victoria-metrics') == victoriametrics ]] || fail "it does not run as its own user"
args=$(in_ct 'tr "\0" " " </proc/$(systemctl show -p MainPID --value victoria-metrics)/cmdline')
for flag in '-retentionPeriod=2y' '-relabelConfig=/etc/victoria-metrics/relabel.yaml' '-memory.allowedBytes=256MiB' '-httpListenAddr=:8428' '-envflag.enable'; do
  grep -qF -- "$flag" <<<"$args" || { echo "$args"; fail "VictoriaMetrics lacks $flag"; }
done
if grep -qF 'Pass_word' <<<"$args"; then fail "the password is in VictoriaMetrics' command line"; fi
# /health is the one path VictoriaMetrics leaves open; reading or writing samples asks for the password.
[[ $(podman exec "$name-ct" curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8428/health) == 200 ]] || fail "/health does not answer"
[[ $(vm_code "$victoriametrics_password" 'http://127.0.0.1:8428/api/v1/query?query=1') == 200 ]] || fail "a query is not answered with the password"
for path in 'api/v1/query?query=1' 'api/v1/labels' 'write?db=ha'; do
  [[ $(podman exec "$name-ct" curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8428/$path") == 401 ]] || fail "/$path is answered without the password"
  [[ $(podman exec "$name-client" curl -s -o /dev/null -w '%{http_code}' "http://$address:8428/$path" 2>/dev/null || true) == 401 ]] || fail "/$path is answered to the network without the password"
done
[[ $(vm_code 'another-password-0123456789' 'http://127.0.0.1:8428/api/v1/query?query=1') == 401 ]] || fail "a wrong password was accepted"
echo "/health open, everything else 401 without the password, from the network too"

relabel_check() {
  # Written ten minutes back: VictoriaMetrics does not answer for the last 30 seconds (-search.latencyOffset).
  local stamp now code names
  now=$(date +%s)
  stamp=$(( (now - 600) * 1000000000 ))
  # One metric to keep, and two the rule has to drop: another field of the same measurement, which
  # is how Home Assistant writes an attribute, and another measurement.
  # curl reads its credentials from stdin, so the lines go in a file.
  printf 'ha,entity_id=sensor.a value=%s %s\nha,entity_id=sensor.a extra=2 %s\nother,entity_id=sensor.a value=3 %s\n' \
    "$1" "$stamp" "$stamp" "$stamp" | podman exec -i "$name-ct" sh -c 'cat >/root/lines'
  code=$(vm "$victoriametrics_password" -o /dev/null -w '%{http_code}' --data-binary @/root/lines 'http://127.0.0.1:8428/write?db=ha')
  in_ct 'rm -f /root/lines'
  [[ $code == 204 ]] || fail "the write was answered $code"
  vm "$victoriametrics_password" -o /dev/null "http://127.0.0.1:8428/internal/force_flush" || true
  sleep 2
  names=$(vm "$victoriametrics_password" "http://127.0.0.1:8428/api/v1/label/__name__/values?start=$((now - 3600))&end=$((now + 60))")
  grep -q '"ha_value"' <<<"$names" || { echo "$names"; fail "ha_value was dropped"; }
  for dropped in other_value ha_extra; do
    if grep -q "\"$dropped\"" <<<"$names"; then echo "$names"; fail "$dropped was kept"; fi
  done
}
relabel_check 1
echo "only ha_value was kept"

log "The dump"
as_postgres -c 'create table readings (id int primary key, note text); insert into readings values (1, $$kept$$); alter table readings owner to homeassistant' >/dev/null
cron_line=$(in_ct "grep -v '^#' /etc/cron.d/ha-db-dump")
[[ $cron_line == '15 5 * * * postgres /usr/local/sbin/ha-db-dump' ]] || fail "unexpected cron line: $cron_line"
in_ct 'runuser -u postgres -- /usr/local/sbin/ha-db-dump'
in_ct 'runuser -u postgres -- /usr/local/sbin/ha-db-dump'
dump=$(in_ct 'ls /var/backups/postgresql')
[[ $dump =~ ^homeassistant-(Mon|Tue|Wed|Thu|Fri|Sat|Sun)\.dump$ ]] || fail "unexpected dumps: $dump"
in_ct 'ls -ld /var/backups/postgresql' | grep -q '^drwx------.* postgres postgres' || fail "the dumps are not private to postgres"
in_ct 'runuser -u postgres -- createdb restored && runuser -u postgres -- pg_restore -d restored /var/backups/postgresql/homeassistant-*.dump'
[[ $(podman exec -i "$name-ct" runuser -u postgres -- psql -X -q -At -d restored -c 'select note from readings') == kept ]] || fail "the dump does not restore"
in_ct 'runuser -u postgres -- dropdb restored'
echo "one dump a weekday, whole, and it restores"

log "What anyone answering on SSH would have received"
# OpenTofu does not check the host key, so this is what an impostor gets. A stand-in for run.sh
# keeps the upload, which is otherwise gone by the time a test could look.
runner=$work/run/repo/modules/opentofu/lxc-app/guest/run.sh
cp "$runner" "$work/run.sh.kept"
printf '#!/bin/sh\ncp /run/octopushy-install/secrets.sealed /root/captured\nls /run/octopushy-install > /root/captured-listing\n' >"$runner"
plan -replace=module.app.terraform_data.install | summary
apply >/dev/null
in_ct 'cat /root/captured; rm /root/captured' >"$work/captured"
in_ct 'cat /root/captured-listing; rm /root/captured-listing' >"$work/listing"
plan -replace=module.app.terraform_data.install | summary
apply >/dev/null
in_ct 'cat /root/captured; rm /root/captured' >"$work/captured-again"
cp "$work/run.sh.kept" "$runner"
grep -qx 'secrets.sealed' "$work/listing" || fail "the secrets did not travel as one sealed file"
[[ $(grep -c sealed "$work/listing") == 1 ]] || fail "more than one sealed file was uploaded"
[[ $(cut -d. -f1 "$work/captured") != "$(cut -d. -f1 "$work/captured-again")" ]] || fail "two uploads shared a nonce"
python3 -I - "$work/captured" "$postgres_password" "$victoriametrics_password" <<'PY'
import base64, sys
captured = open(sys.argv[1], "rb").read().strip()
assert captured.count(b".") == 2 and len(captured) > 40, captured
for secret in (sys.argv[2].encode(), sys.argv[3].encode()):
    encoded = base64.b64encode(secret).rstrip(b"=")
    assert secret not in captured and encoded not in captured and encoded[:8] not in captured, "the upload is readable"
print("only ciphertext of both crossed SSH, in one seal")
PY
plan | summary
installs

log "A seal that was changed, or that comes round again, opens nothing"
by_hand() { # the file to upload as the seal
  podman exec "$name-ct" sh -c 'rm -rf /run/octopushy-install && install -d -m 0700 /run/octopushy-install /run/octopushy-install/app'
  podman cp "$work/run/repo/$module/files/." "$name-ct:/run/octopushy-install/app/"
  podman cp "$work/run/repo/modules/opentofu/lxc-app/guest/run.sh" "$name-ct:/run/octopushy-install/run.sh"
  podman cp "$work/environment" "$name-ct:/run/octopushy-install/environment"
  podman cp "$1" "$name-ct:/run/octopushy-install/secrets.sealed"
  podman exec "$name-ct" bash /run/octopushy-install/run.sh 2>&1
}
cat >"$work/environment" <<ENV
OCTOPUSHY_ADDRESS=$address
OCTOPUSHY_NONCES=/var/lib/octopushy-install/used-nonces
OCTOPUSHY_SCRIPT=install.sh
PG_CLIENTS=$client
VM_RETENTION=2y
VM_SHA256=$vm_sha256
VM_VERSION=1.155.0
ENV
sed 's/\.\(.\)/.A\1/' "$work/captured" >"$work/tampered"
if out=$(by_hand "$work/tampered"); then fail "a changed seal was opened"; fi
grep -q 'the seal does not verify' <<<"$out" || { echo "$out"; fail "unexpected failure for a changed seal"; }
by_hand "$work/captured" >/dev/null || fail "a good seal did not open"
if out=$(by_hand "$work/captured"); then fail "a seal that was used before was opened"; fi
grep -q 'nonce was used before' <<<"$out" || { echo "$out"; fail "unexpected failure for a replayed seal"; }
in_ct '! test -e /run/octopushy-install'
in_ct 'test "$(stat -c %a /var/lib/octopushy-install/used-nonces)" = 600 && test "$(wc -l </var/lib/octopushy-install/used-nonces)" -ge 3'
echo "refused both, and the record of used nonces survives an install again"

log "The passwords are nowhere they should not be"
scan() {
  tofu show -json /w/out/plan.bin >"$work/plan.json"
  python3 -I - "$work" "$@" <<'PY'
import sys, zipfile
work, secrets = sys.argv[1], [s.encode() for s in sys.argv[2:]]
places = {"tofu show -json": open(f"{work}/plan.json", "rb").read(),
          "state": open(f"{work}/run/repo/state/terraform.tfstate", "rb").read(),
          "apply log": open(f"{work}/apply.log", "rb").read(),
          "saved plan": open(f"{work}/run/out/plan.bin", "rb").read()}
plan = zipfile.ZipFile(f"{work}/run/out/plan.bin")
places.update({f"saved plan, {name}": plan.read(name) for name in plan.namelist()})
assert len(places["tofu show -json"]) > 1000
for place, content in places.items():
    for secret in secrets:
        assert secret not in content, place
print(f"neither in {len(places)} places")
PY
}
plan -replace=module.app.terraform_data.install | summary
scan "$postgres_password" "$victoriametrics_password"
# Octopushy gives the plan no password. Being ephemeral, they would stay out even if it did.
passwords
plan -replace=module.app.terraform_data.install | summary
scan "$postgres_password" "$victoriametrics_password"
tofu_env=""
in_ct "! grep -rlF -e '$postgres_password' /etc /run /var /root /tmp /opt /usr/local /home 2>/dev/null"
[[ $(in_ct "grep -rlF -e '$victoriametrics_password' /etc /run /var /root /tmp /opt /usr/local /home 2>/dev/null") == /etc/victoria-metrics/auth.env ]] \
  || fail "the VictoriaMetrics password is somewhere besides its environment file"
in_ct 'test "$(stat -c "%a %U" /etc/victoria-metrics/auth.env)" = "600 root"'
in_ct '! journalctl --no-pager 2>/dev/null | grep -F -e "Pass_word"'
in_ct '! test -e /run/octopushy-install && ! test -e /run/octopushy-ha-db'
echo "the PostgreSQL password is nowhere; the VictoriaMetrics password only in a file of mode 0600 for root"

log "Installing again changes nothing and loses nothing"
pg_pid=$(in_ct 'systemctl show -p MainPID --value postgresql@17-main')
vm_pid=$(in_ct 'systemctl show -p MainPID --value victoria-metrics')
sums=$(in_ct 'sha256sum /etc/postgresql/17/main/pg_hba.conf /etc/postgresql/17/main/conf.d/ha.conf /etc/victoria-metrics/* /etc/systemd/system/victoria-metrics.service /usr/local/sbin/ha-db-dump /etc/cron.d/ha-db-dump')
as_postgres -c "insert into readings values (2, \$\$second\$\$)" >/dev/null
plan -replace=module.app.terraform_data.install | summary
installs
[[ $(in_ct 'systemctl show -p MainPID --value postgresql@17-main') == "$pg_pid" ]] || fail "PostgreSQL restarted"
[[ $(in_ct 'systemctl show -p MainPID --value victoria-metrics') == "$vm_pid" ]] || fail "VictoriaMetrics restarted"
[[ $(in_ct 'sha256sum /etc/postgresql/17/main/pg_hba.conf /etc/postgresql/17/main/conf.d/ha.conf /etc/victoria-metrics/* /etc/systemd/system/victoria-metrics.service /usr/local/sbin/ha-db-dump /etc/cron.d/ha-db-dump') == "$sums" ]] || fail "a file changed"
[[ $(as_postgres -c 'select count(*) from readings') == 2 ]] || fail "a row was lost"
[[ $(vm "$victoriametrics_password" "http://127.0.0.1:8428/api/v1/label/__name__/values?start=$(( $(date +%s) - 3600 ))" | grep -c '"ha_value"') -ge 1 ]] || fail "the samples were lost"
echo "same processes, same files, same rows, same samples"

log "A change that is not in the plan changes nothing; a revision that is rolls both passwords"
new_pg='new-pg-Pass_word.987+/=:@654'
new_vm='new-vm-Pass_word.987+/=:@654'
passwords "$new_pg" "$new_vm"
[[ $(plan | grep -c 'No changes') == 1 ]] || fail "a password in the store alone should start nothing"
tofu_env=""
write_vars 2
plan | tee "$work/rotate.plan" | summary
grep -q 'module.app.terraform_data.install must be replaced' "$work/rotate.plan" || fail "raising credentialsRevision should install again"
[[ $(grep -c 'will be updated in-place\|must be replaced' "$work/rotate.plan") == 1 ]] || fail "only the install should change"
installs "$new_pg" "$new_vm"
[[ $(pg_as "$new_pg" -h "$address" -U homeassistant -d homeassistant -c 'select count(*) from readings') == 2 ]] || fail "the new PostgreSQL password does not work, or the data is gone"
if pg_as "$postgres_password" -h "$address" -U homeassistant -d homeassistant -c 'select 1' >/dev/null 2>&1; then fail "the old PostgreSQL password still works"; fi
[[ $(vm_code "$new_vm" 'http://127.0.0.1:8428/api/v1/query?query=1') == 200 ]] || fail "the new VictoriaMetrics password does not work"
[[ $(vm_code "$victoriametrics_password" 'http://127.0.0.1:8428/api/v1/query?query=1') == 401 ]] || fail "the old VictoriaMetrics password still works"
[[ $(in_ct 'systemctl show -p MainPID --value postgresql@17-main') == "$pg_pid" ]] || fail "PostgreSQL restarted for a password"
[[ $(in_ct 'systemctl show -p MainPID --value victoria-metrics') != "$vm_pid" ]] || fail "VictoriaMetrics did not take its new password"
postgres_password=$new_pg victoriametrics_password=$new_vm
scan "$postgres_password" "$victoriametrics_password"
relabel_check 5
[[ $(in_ct "grep -rlF -e '$postgres_password' /etc /run /var /root /tmp /opt 2>/dev/null | wc -l") == 0 ]] || fail "the new PostgreSQL password is on the disk"
echo "both passwords rolled, the rows and the relabel rule kept"

fi

# ---- The stateful guard ----
lift_protection() { # what Octopushy's unprotect step does, before it applies an approved plan
  podman exec "$name-proxmox" python3 -I -c "
import ssl, urllib.request
request = urllib.request.Request('https://127.0.0.1:8006/api2/json/nodes/ziost/lxc/102/config', data=b'protection=0', method='PUT')
urllib.request.urlopen(request, context=ssl._create_unverified_context()).read()
"
}
resources() { tofu state list | sort | tr '\n' ' '; }
action_reason() { # the action_reason OpenTofu gives a resource in the saved plan, as Octopushy reads it
  tofu show -json /w/out/plan.bin >"$work/plan.json"
  python3 -I - "$work/plan.json" "$1" <<'PY'
import json, sys
changes = {c["address"]: c for c in json.load(open(sys.argv[1]))["resource_changes"]}
change = changes[sys.argv[2]]
print(",".join(change["change"]["actions"]), change.get("action_reason", "-"))
PY
}
container=module.app.module.container.proxmox_virtual_environment_container.guest
# What the state was installed with: a password roll raised it to 2.
if [[ ${SKIP_TO_GUARD:-} == 1 ]]; then rev=1; else rev=2; fi
write_vars "$rev"
as_postgres -c 'create table if not exists guard_kept (id int primary key); insert into guard_kept values (1) on conflict do nothing; alter table guard_kept owner to homeassistant' >/dev/null
pg_pid=$(in_ct 'systemctl show -p MainPID --value postgresql@17-main')

log "A plan that would replace or destroy the container stops at the plan"
[[ $(resources) == *terraform_data.keep_data* ]] || fail "the guard is not in the state after a finished install"
if plan -replace="$container" >"$work/guard-replace.txt" 2>&1; then fail "a forced replacement of the container was planned"; fi
cat "$work/guard-replace.txt"
grep -q 'Resource instance cannot be destroyed' "$work/guard-replace.txt" || fail "the error does not say what stopped it"
grep -q 'terraform_data.keep_data' "$work/guard-replace.txt" || fail "the error does not name the guard"
grep -q 'prevent_destroy set, but the' "$work/guard-replace.txt" || fail "the error does not name the setting"
# The container's id is its VMID, which a replacement does not change: the guard follows the
# incarnation instead. An edit that Proxmox cannot make in place is stopped the same way.
write_vars "$rev" 2 1024 2y 1.155.0 "$vm_sha256" 103
if plan >"$work/guard-edit.txt" 2>&1; then fail "a changed VMID, which replaces the container, was planned"; fi
cat "$work/guard-edit.txt"
grep -q 'terraform_data.keep_data' "$work/guard-edit.txt" || fail "the replacement an edit forced was not stopped by the guard"
write_vars "$rev"
echo "a forced replacement and an edit that forces one both stop at the plan, naming terraform_data.keep_data"

log "A change in place passes"
before_requests=$(wc -l <"$requests")
write_vars "$rev" 4 1536
plan | tee "$work/inplace.plan" | summary
[[ $(plan_unsealed | summary) == 'Plan: 0 to add, 1 to change, 0 to destroy.' ]] || fail "cores and memory should be one change in place"
action_reason "$container" | grep -qx 'update -' || fail "the container should be updated in place"
grep -Eq '^ +# terraform_data\.keep_data ' "$work/inplace.plan" && fail "the guard was touched by a change in place"
apply >/dev/null
tail -n +"$((before_requests + 1))" "$requests" | grep 'PUT' | grep -q '"cores": "4"' || fail "Proxmox was not told about the cores"
[[ $(in_ct 'systemctl show -p MainPID --value postgresql@17-main') == "$pg_pid" ]] || fail "PostgreSQL restarted for a hardware change"
echo "cores and memory change in place, with the guard and the install untouched"

log "A version bump that installs again passes"
write_vars "$rev" 4 1536 3y
plan_unsealed | tee "$work/reinstall.plan" | summary
action_reason module.app.terraform_data.install | grep -qx 'delete,create replace_because_triggered_by\|delete,create replace_because_cannot_update\|delete,create -' || fail "the install should be replaced"
grep -Eq '^ +# terraform_data\.keep_data ' "$work/reinstall.plan" && fail "the guard was touched by an install"
installs
args=$(in_ct 'tr "\0" " " </proc/$(systemctl show -p MainPID --value victoria-metrics)/cmdline')
grep -qF -- '-retentionPeriod=3y' <<<"$args" || fail "the new retention is not in force"
[[ $(as_postgres -c 'select count(*) from guard_kept') == 1 ]] || fail "a row was lost"
write_vars "$rev" 4 1536 3y 1.154.0 "$(printf '%064d' 1 | tr 0 a)"
plan_unsealed | tee "$work/bump.plan" | summary
grep -q 'module.app.terraform_data.install must be replaced' "$work/bump.plan" || fail "a version bump should install again"
grep -Eq '^ +# terraform_data\.keep_data ' "$work/bump.plan" && fail "the guard was touched by a version bump"
write_vars "$rev" 4 1536 3y
echo "a new retention is applied for real; a new version is planned as an install again, and neither touches the guard"

log "Retiring: what a retire run built from the last applied revision does"
cp "$work/run/repo/state/terraform.tfstate" "$work/state.before-retire"
if plan -destroy >"$work/guard-destroy.txt" 2>&1; then fail "a destroy plan was made with the guard in place"; fi
cat "$work/guard-destroy.txt"
grep -q 'Resource instance cannot be destroyed' "$work/guard-destroy.txt" || fail "the destroy plan failed for another reason"
grep -q 'terraform_data.keep_data' "$work/guard-destroy.txt" || fail "the destroy error does not name the guard"
# The commit that removes the guard.
python3 -I - "$work/run/repo/$module/main.tf" <<'PY'
import re, sys
path = sys.argv[1]
text = open(path).read()
text, removed = re.subn(r'\n# Proxmox deletes a container.*?\nresource "terraform_data" "keep_data" \{.*?\n\}\n', "\n", text, flags=re.S)
assert removed == 1, "the guard was not found in main.tf"
assert "keep_data" not in text
open(path, "w").write(text)
PY
# Retired in the same commit: the run is built from this revision, and the state still holds the guard.
plan -destroy | summary
[[ $(tofu show -json /w/out/plan.bin | python3 -I -c "import json,sys; print(sum('delete' in c['change']['actions'] for c in json.load(sys.stdin)['resource_changes']))") -ge 4 ]] || fail "the destroy plan should delete the container and what hangs on it"
echo "retired in the commit that removes the guard: a destroy plan is made"
# Removed on its own, applied first.
plan_unsealed | tee "$work/unguard.plan" | summary
[[ $(plan_unsealed | summary) == 'Plan: 0 to add, 0 to change, 1 to destroy.' ]] || fail "removing the guard should destroy exactly the guard"
action_reason terraform_data.keep_data | grep -qx 'delete delete_because_no_resource_config' || fail "what is destroyed should be the guard"
tofu_env="" apply >/dev/null
[[ $(resources) != *keep_data* ]] || fail "the guard is still in the state"
plan -destroy | summary
if apply >"$work/unprotected.log" 2>&1; then fail "the container was deleted while Proxmox protected it"; fi
grep -q 'protection mode enabled' "$work/unprotected.log" || { tail -5 "$work/unprotected.log"; fail "the destroy failed for another reason"; }
plan -destroy | summary
lift_protection
apply >/dev/null
grep -q '"DELETE", "path": "/nodes/ziost/lxc/102"' "$requests" || fail "Proxmox was never asked to delete the container"
[[ -z $(resources) ]] || fail "the state still holds $(resources)"
echo "the guard removed and applied first, protection lifted, then the destroy goes through"

log "A first install that fails can be retried, and the guard waits for it"
# The guard is back, as in the commit before the one that removed it.
cp "$root/examples/lab/modules/opentofu/ha-db/main.tf" "$work/run/repo/$module/main.tf"
state fresh1.tfstate
tofu init -input=false -no-color -reconfigure -lockfile=readonly | tail -1
files=$work/run/repo/$module/files
cp "$files/install.sh" "$files/real-install.sh"
cat >"$files/install.sh" <<'STUB'
#!/usr/bin/env bash
[[ -e /root/allow-install ]] || { echo "ha-db: failing on purpose, as a first install can" >&2; exit 1; }
exec bash "$(dirname "$0")/real-install.sh"
STUB
in_ct 'rm -f /root/allow-install'
write_vars 1
plan | summary
if apply >"$work/failed-install.log" 2>&1; then fail "the install should have failed"; fi
grep -q 'failing on purpose' "$work/failed-install.log" || { tail -8 "$work/failed-install.log"; fail "the install failed for another reason"; }
[[ $(resources) == *proxmox_virtual_environment_container.guest* && $(resources) != *keep_data* ]] || fail "after a failed install the container exists and the guard does not: $(resources)"
tofu state show module.app.terraform_data.install | head -1 | grep -q tainted || fail "the failed install is not tainted"
in_ct 'touch /root/allow-install'
plan | tee "$work/retry.plan" | summary
grep -q 'module.app.terraform_data.install is tainted, so it must be replaced' "$work/retry.plan" || fail "the retry should replace the tainted install"
grep -q 'keep_data will be created' "$work/retry.plan" || fail "the guard should be created by the retry"
grep -q 'proxmox_virtual_environment_container.guest.*replaced' "$work/retry.plan" && fail "the container should not be touched by a retry"
installs
[[ $(resources) == *keep_data* ]] || fail "the guard was not created once the install finished"
echo "retried with nothing else changed; the guard appeared with the first install that finished"

log "A container that never finished can be replaced, and is stopped once it has"
state fresh2.tfstate
tofu init -input=false -no-color -reconfigure -lockfile=readonly | tail -1
in_ct 'rm -f /root/allow-install'
plan | summary
if apply >"$work/failed-install.log" 2>&1; then fail "the install should have failed"; fi
[[ $(resources) != *keep_data* ]] || fail "the guard exists after a failed first install"
tofu taint "$container" >/dev/null
plan | summary
action_reason "$container" | grep -qx 'delete,create replace_because_tainted' || fail "the tainted container should be replaced because it is tainted"
action_reason 'terraform_data.keep_data' | grep -qx 'create -' || fail "the guard should be created, not replaced"
if apply >"$work/protected.log" 2>&1; then fail "Proxmox deleted a protected container"; fi
grep -q 'protection mode enabled' "$work/protected.log" || { tail -5 "$work/protected.log"; fail "the replace failed for another reason"; }
lift_protection
in_ct 'touch /root/allow-install'
plan | summary
action_reason "$container" | grep -qx 'delete,create replace_because_tainted' || fail "the tainted container should still be replaced"
installs
[[ $(resources) == *keep_data* ]] || fail "the guard was not created once the install finished"
if plan -replace="$container" >"$work/guard-again.txt" 2>&1; then fail "the recovered container is not guarded"; fi
grep -q 'terraform_data.keep_data' "$work/guard-again.txt" || fail "the guard does not name itself"
cp "$files/real-install.sh" "$files/install.sh" && rm "$files/real-install.sh"
echo "a tainted container whose install never finished is replaced, and from then on guarded"

log "Passed"
