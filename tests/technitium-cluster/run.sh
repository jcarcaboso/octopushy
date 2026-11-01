#!/usr/bin/env bash
# A Technitium cluster on two real servers, driven the way a cluster run drives it: the agent's
# technitium executor, and real OpenTofu with the real provider in its container. Only the
# controller is not here; this script does what it decides, from inspect's outputs and the
# plan's changes. It proves nothing about Proxmox, LXC, or its firewall.
#
# The servers sit on a private network with no way out, with fake_upstream.py for resolver and
# block list. Downloads happen first, on the default network: the images and the provider.
# Every resource is named octopushy-technitium-cluster-* and removed by that exact name.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/technitium-cluster
name=octopushy-technitium-cluster
network=$name
tofu_image=ghcr.io/opentofu/opentofu:1.13.1
image=docker.io/technitium/dns-server:15.6.2
python_image=docker.io/library/python:3-alpine
export TECHNITIUM_PASSWORD='A test password, with & = + % in it'
primary=10.199.1.11
secondary=10.199.1.12
upstream=10.199.1.30
action_binary=$root/agent/target/debug/examples/technitium_action
passed=()

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
scenario() { passed+=("$1"); echo "scenario passed: $1"; }
cleanup() {
  podman rm -f -v -t 0 "$name-dns1" "$name-dns2" "$name-upstream" >/dev/null 2>&1 || true
  podman network rm "$network" >/dev/null 2>&1 || true
  podman unshare rm -rf "$work/dns1" "$work/dns2" 2>/dev/null || true
}
[[ -n ${KEEP:-} ]] || trap cleanup EXIT

in_network() { podman unshare --rootless-netns "$@"; }
node() { in_network python3 -I "$root/tests/technitium-cluster/node.py" "$@"; }
dns() { node dns "$@"; }

expect_dns() { # address name expected
  local got
  got=$(dns "$1" "$2")
  [[ $got == "$3" ]] || fail "$2 at $1 is $got, expected $3"
  echo "$1: $2 is $got"
}

eventually_dns() { # address name expected
  for i in $(seq 1 45); do
    [[ $(dns "$1" "$2") == "$3" ]] && { echo "$1: $2 is $3 after about $((i * 2 - 2)) s"; return; }
    sleep 2
  done
  fail "$2 at $1 never became $3"
}

start_node() { # name address
  # The certificate the recipe's install makes: EC, ten years, naming the address.
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 -subj "/CN=$2" \
    -addext "subjectAltName=IP:$2" -addext "basicConstraints=critical,CA:FALSE" -keyout "$work/$1/tls.key" -out "$work/$1/tls.crt" 2>/dev/null
  openssl pkcs12 -export -inkey "$work/$1/tls.key" -in "$work/$1/tls.crt" -out "$work/$1/https.pfx" \
    -certpbe AES-256-CBC -keypbe AES-256-CBC -macalg sha256 -passout pass:test-only
  rm -f "$work/$1/tls.key"
  podman run -d --name "$name-$1" --hostname "$1" --network "$network" --ip "$2" \
    -v "$work/$1:/etc/dns:Z" -e DNS_SERVER_DOMAIN="$1" -e DNS_SERVER_ADMIN_PASSWORD="$TECHNITIUM_PASSWORD" \
    -e DNS_SERVER_WEB_SERVICE_ENABLE_HTTPS=true -e DNS_SERVER_WEB_SERVICE_TLS_CERTIFICATE_PATH=/etc/dns/https.pfx \
    -e DNS_SERVER_WEB_SERVICE_TLS_CERTIFICATE_PASSWORD=test-only -e DNS_SERVER_WEB_SERVICE_HTTPS_PORT=53443 "$image" >/dev/null
  for _ in $(seq 1 60); do
    node call "$2" /api/admin/cluster/state >/dev/null 2>&1 && return
    sleep 1
  done
  fail "$1 did not start"
}

tofu_in() { # network, then arguments
  local on=$1; shift
  podman run --rm --name "$name-tofu" --log-driver=none --cap-drop=all --security-opt=no-new-privileges \
    --read-only --tmpfs /tmp:rw,mode=1777 --userns=keep-id --network "$on" -v "$work/run:/w:Z" -w /w/root \
    -e HOME=/w/home -e TF_IN_AUTOMATION=1 -e TF_INPUT=0 -e TF_PLUGIN_CACHE_DIR=/w/cache \
    -e TECHNITIUM_USERNAME=admin -e TECHNITIUM_PASSWORD -e TECHNITIUM_CACERT=octopushy-primary.pem "$tofu_image" "$@"
}
tofu() { tofu_in "$network" "$@"; }

# What the controller keeps: what cluster.yaml declares. Everything derives from this file.
render() {
  python3 -I - "$work" <<'PY'
import json, sys
work = sys.argv[1]
d = json.load(open(f"{work}/desired.json"))
trusted = d.get("trusted", {})
def member(guest, role, address):
    endpoint = f"https://{address}:53443"
    return {"guest": guest, "role": role, "address": address, "endpoint": endpoint,
            "certificateSha256": trusted.get(endpoint, {}).get("sha256")}
members = [member("home/korriban/dns1", "primary", d["primary"])] + [
    member(guest, "secondary", address) for guest, address in d["secondaries"].items()
]
primary_pem = trusted.get(f"https://{d['primary']}:53443", {}).get("pem")
if primary_pem:
    open(f"{work}/run/root/octopushy-primary.pem", "w").write(primary_pem)
blocking = d["blocking"]
json.dump({
    "octopushy": {"address": "home/clusters/technitium", "stack": "home.clusters.technitium", "lab": "home",
                  "cluster": "technitium", "revision": "test"},
    "cluster": {"id": "technitium", "domain": d["domain"],
                "primary": {"guest": "home/korriban/dns1", "address": d["primary"]},
                "secondaries": {g: {"address": a} for g, a in d["secondaries"].items()},
                "settings": {"recursion": d["recursion"], "forwardZones": d["forwardZones"],
                             "blocking": {k: v for k, v in blocking.items() if k != "listProbes"}}},
}, open(f"{work}/run/root/octopushy.auto.tfvars.json", "w"))
base = {
    "cluster": "home/clusters/technitium", "clusterDomain": d["domain"], "minimumVersion": "15.6.2",
    "members": members, "formed": d["formed"], "syncWaitSeconds": 60, "control": d["control"],
    "settings": {"enableBlocking": blocking["enabled"], "blockingType": blocking["type"], "blockListUrls": blocking["lists"],
                 "blockListUpdateIntervalHours": blocking["updateIntervalHours"], "recursion": d["recursion"]["policy"],
                 "recursionNetworkACL": d["recursion"]["networks"]},
    "blocked": blocking["blocked"], "allowed": blocking["allowed"], "listProbes": blocking["listProbes"],
    "forwardZones": [z["zone"] for z in d["forwardZones"]],
}
json.dump(base, open(f"{work}/input.json", "w"))
PY
}

declare_() { # a Python statement over d
  python3 -I - "$work/desired.json" "$1" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
exec(sys.argv[2])
json.dump(d, open(sys.argv[1], "w"))
PY
  render
}

action() { # action, then stdout goes to $work/$1.json; the exit status is the executor's
  in_network env TECHNITIUM_PASSWORD="$TECHNITIUM_PASSWORD" "$action_binary" "$1" "$work/input.json" >"$work/$1.json"
}

state_of() { # guest, from the last inspect
  python3 -I -c "
import json, sys
print(next(m['state'] for m in json.load(open('$work/inspect.json'))['members'] if m['guest'] == sys.argv[1]))" "$1"
}

# What Trust cluster records: what an agent read, as an operator saw it.
trust_certificates() {
  action certificates
  python3 -I - "$work" <<'PY'
import json, sys
work = sys.argv[1]
d = json.load(open(f"{work}/desired.json"))
for c in json.load(open(f"{work}/certificates.json"))["certificates"]:
    d.setdefault("trusted", {})[c["endpoint"]] = {"sha256": c["sha256"], "pem": c["pem"]}
json.dump(d, open(f"{work}/desired.json", "w"))
PY
  render
}

# ClusterBinding, from what inspect reported: who is joined, by what identity.
bind_peers() {
  python3 -I - "$work" <<'PY'
import json, sys
work = sys.argv[1]
d = json.load(open(f"{work}/desired.json"))
for m in json.load(open(f"{work}/inspect.json"))["members"]:
    if m.get("peerId") is not None:
        d.setdefault("bindings", {})[m["guest"]] = {"guest": m["guest"], "address": m["address"], "peerId": m["peerId"], "peerName": m["peerName"]}
json.dump(d, open(f"{work}/desired.json", "w"))
PY
}

# ClusterPlan.Replacements
replacements() {
  python3 -I - "$work/inspect.json" <<'PY'
import json, sys
i = json.load(open(sys.argv[1]))
for m in i["members"]:
    if m["state"] == "absent":
        print('-replace=terraform_data.secondary["%s"]' % m["guest"])
if i["undeclared"]["blocked"] or i["undeclared"]["allowed"]:
    print("-replace=terraform_data.undeclared_entries")
PY
}

plan() {
  action inspect
  bind_peers
  mapfile -t replace < <(replacements)
  set +e
  tofu plan -input=false -no-color -detailed-exitcode -out=/w/out/plan.bin "${replace[@]}" >"$work/plan.log" 2>&1
  planned=$?
  set -e
  [[ $planned == 0 || $planned == 2 ]] || { tail -30 "$work/plan.log"; fail "the plan failed"; }
  tofu show -json /w/out/plan.bin >"$work/plan.json"
  python3 -I - "$work" <<'PY'
import json, re, sys
work = sys.argv[1]
changes = [c for c in json.load(open(f"{work}/plan.json")).get("resource_changes", [])
           if c["change"]["actions"] not in (["no-op"], ["read"])]
# ClusterPlan: what the approved plan changed.
secondary = re.compile(r'^terraform_data\.secondary\["([^"]+)"\]$')
join, remove = [], []
for c in changes:
    m = secondary.match(c["address"])
    if m:
        (remove if c["change"]["actions"] == ["delete"] else join).append(m.group(1))
d = json.load(open(f"{work}/desired.json"))
pruning = any(c["address"] == "terraform_data.undeclared_entries" for c in changes)
undeclared = json.load(open(f"{work}/inspect.json"))["undeclared"]
out = {
    "addresses": [c["address"] for c in changes],
    "actions": {c["address"]: c["change"]["actions"] for c in changes},
    "join": join, "remove": [d.get("bindings", {}).get(g, {"guest": g}) for g in remove],
    # AssignmentBuilder: what inspection named, as the approval showed it, and nothing else.
    **{f"prune{field.capitalize()}": undeclared[field] if pruning else [] for field in ("blocked", "allowed")},
    # ClusterPlan.ListsDownloaded: what was downloaded is kept apart from the plan.
    "lists": d["blocking"]["lists"] != d.get("refreshed", []),
}
json.dump(out, open(f"{work}/changes.json", "w"))
print("plan:", ", ".join(f"{c['address']} {'+'.join(c['change']['actions'])}" for c in changes) or "no changes")
PY
}

field() { python3 -I -c "import json; print(json.dumps(json.load(open('$work/changes.json'))['$1']))"; }

no_changes() { [[ $planned == 0 ]] || { rg 'will be|must be' "$work/plan.log" || true; fail "$1: expected no changes"; }; }

scan_password() {
  python3 -I - "$TECHNITIUM_PASSWORD" "$work" <<'PY'
import sys, zipfile
secret, work = sys.argv[1].encode(), sys.argv[2]
places = {"tofu show -json": open(f"{work}/plan.json", "rb").read(),
          "plan log": open(f"{work}/plan.log", "rb").read(),
          "state": open(f"{work}/run/state/terraform.tfstate", "rb").read() if __import__("os").path.exists(f"{work}/run/state/terraform.tfstate") else b"",
          "saved plan": open(f"{work}/run/out/plan.bin", "rb").read()}
plan = zipfile.ZipFile(f"{work}/run/out/plan.bin")
places.update({f"saved plan, {name}": plan.read(name) for name in plan.namelist()})
try:
    places["apply log"] = open(f"{work}/apply.log", "rb").read()
except OSError:
    pass
assert len(places["tofu show -json"]) > 500
for place, content in places.items():
    assert secret not in content, place
print(f"the password is in none of {len(places)} places")
PY
}

apply() { # what the steps after an approved plan do
  python3 -I - "$work" <<'PY'
import json, sys
work = sys.argv[1]
base = json.load(open(f"{work}/input.json"))
changes = json.load(open(f"{work}/changes.json"))
base.update(join=changes["join"], remove=changes["remove"], pruneBlocked=changes["pruneBlocked"], pruneAllowed=changes["pruneAllowed"])
json.dump(base, open(f"{work}/input.json", "w"))
PY
  # Removals first: once the apply has run, the plan no longer holds them.
  if [[ $(field remove) != '[]' ]]; then action detach || fail "detach failed"; fi
  tofu apply -input=false -no-color /w/out/plan.bin >>"$work/apply.log" 2>&1 || { tail -30 "$work/apply.log"; fail "the apply failed"; }
  if [[ $(field join) != '[]' || $(field pruneBlocked) != '[]' || $(field pruneAllowed) != '[]' ]]; then
    action reconcile || fail "reconcile failed"
  fi
  render
  if [[ $(field lists) == true ]]; then refresh; fi
}

refresh() {
  action refresh-block-lists || fail "refresh failed"
  declare_ 'd["refreshed"] = d["blocking"]["lists"]'
}

verified() { action verify || fail "verification failed"; echo "verified every member"; }

log "Downloads"
cleanup
rm -rf "$work/run" "$work/lists" "$work/apply.log"
mkdir -p "$work/run/home" "$work/run/cache" "$work/run/out" "$work/run/state" "$work/run/root" "$work/dns1" "$work/dns2" "$work/lists"
(cd "$root/agent" && cargo build -q -p octopushy --example technitium_action --locked)
for wanted in "$image" "$tofu_image" "$python_image"; do
  podman image exists "$wanted" || podman pull -q "$wanted" >/dev/null
done
cp "$root"/examples/lab/modules/opentofu/technitium-cluster/{*.tf,.terraform.lock.hcl} "$work/run/root/"
printf '{"terraform":{"backend":{"local":{"path":"../state/terraform.tfstate"}}}}' >"$work/run/root/octopushy_backend.tf.json"
tofu_in podman init -input=false -no-color -lockfile=readonly | tail -1

log "A private network"
podman network create --internal --subnet 10.199.1.0/24 "$network" >/dev/null
printf 'list1.lab.example.net\n' >"$work/lists/one.txt"
printf 'list2.lab.example.net\n' >"$work/lists/two.txt"
podman run -d --name "$name-upstream" --network "$network" --ip "$upstream" -v "$work/lists:/lists:ro,Z" \
  -v "$root/tests/technitium-cluster/fake_upstream.py:/fake_upstream.py:ro,Z" "$python_image" python3 -I /fake_upstream.py /lists >/dev/null
start_node dns1 "$primary"
start_node dns2 "$secondary"

cat >"$work/desired.json" <<JSON
{ "primary": "$primary", "secondaries": { "home/moraband/dns2": "$secondary" }, "domain": "cluster.lab.example.net", "formed": false,
  "control": "control.lab.example.net",
  "recursion": { "policy": "UseSpecifiedNetworkACL", "networks": ["10.199.1.0/24"] },
  "forwardZones": [{ "zone": "lab.example.net", "forwarder": "$upstream", "protocol": "Udp" }],
  "blocking": { "enabled": true, "type": "NxDomain", "updateIntervalHours": 24, "lists": ["http://$upstream:8000/one.txt"],
    "blocked": ["bad.lab.example.net"], "allowed": ["ok.lab.example.net"], "listProbes": ["list1.lab.example.net"] } }
JSON
render

log "Certificates, read with no credentials and then trusted"
trust_certificates
[[ -s $work/run/root/octopushy-primary.pem ]] || fail "no certificate was read for the primary"
python3 -I -c "
import json
assert len(json.load(open('$work/desired.json'))['trusted']) == 2"
scenario "certificates are read with no password"

log "Form and join"
plan
[[ $(state_of home/moraband/dns2) == absent ]] || fail "the secondary should be absent"
scan_password
apply
declare_ 'd["formed"] = True'
scan_password
verified
scenario "form and join"

log "Entries reach the secondary"
for address in "$primary" "$secondary"; do
  expect_dns "$address" bad.lab.example.net blocked
  expect_dns "$address" control.lab.example.net resolves
  expect_dns "$address" ok.lab.example.net resolves
  eventually_dns "$address" list1.lab.example.net blocked
done
scenario "entries reach the secondary, with raw DNS from each member"

log "A later blocked entry reaches the secondary"
declare_ 'd["blocking"]["blocked"].append("later.lab.example.net")'
plan
apply
verified
expect_dns "$secondary" later.lab.example.net blocked
scenario "a later entry reaches the secondary within the bounded retry"

log "What joined is gone, and the cluster never needed it"
for address in "$primary" "$secondary"; do
  if node call "$address" /api/admin/users/list | rg -q octopushy-cluster-join; then fail "the temporary user is still on $address"; fi
done
back_up() { for _ in $(seq 1 60); do node call "$1" /api/admin/cluster/state >/dev/null 2>&1 && return; sleep 1; done; fail "$1 did not come back"; }
podman restart -t 5 "$name-dns2" >/dev/null
back_up "$secondary"
podman restart -t 5 "$name-dns1" >/dev/null
back_up "$primary"
declare_ 'd["blocking"]["blocked"].append("after-restarts.lab.example.net")'
plan
apply
verified
expect_dns "$secondary" after-restarts.lab.example.net blocked
declare_ 'd["blocking"]["blocked"].remove("after-restarts.lab.example.net")'
plan
apply
verified
scenario "the join's user is gone from both, and sync survives restarts and a later change"

log "Idempotent"
plan
no_changes "a second plan"
[[ $(state_of home/moraband/dns2) == joined ]] || fail "the secondary should be joined"
verified
scenario "idempotent plan, and verification on a no-change plan"

log "A new list URL is enforced only once it is downloaded"
expect_dns "$secondary" list2.lab.example.net resolves
declare_ 'd["blocking"]["lists"].append("http://'$upstream':8000/two.txt"); d["blocking"]["listProbes"].append("list2.lab.example.net")'
plan
[[ $(field lists) == true ]] || fail "the plan does not show the new list"
tofu apply -input=false -no-color /w/out/plan.bin >>"$work/apply.log" 2>&1
sleep 8
expect_dns "$primary" list2-before.lab.example.net resolves
# Verification asks for the probe, so it cannot pass before the list is enforced.
python3 -I -c "
import json
d = json.load(open('$work/input.json')); d['syncWaitSeconds'] = 0
json.dump(d, open('$work/input.json', 'w'))"
if action verify 2>"$work/verify.err"; then fail "verification passed on a list that was not downloaded"; fi
rg -q 'list2.lab.example.net is not blocked' "$work/verify.err" || { cat "$work/verify.err"; fail "unexpected failure"; }
render
refresh
eventually_dns "$primary" list2.lab.example.net blocked
eventually_dns "$secondary" list2.lab.example.net blocked
verified
scenario "list-URL refresh, forced on the primary alone"

log "A hand edit on the primary is reverted"
node call "$primary" /api/settings/set blockingType=AnyAddress >/dev/null
plan
rg -q 'technitium_server_settings.this' "$work/changes.json" || fail "the plan does not show the drift"
apply
verified
scenario "drift from a hand edit on the primary"

log "A hand edit on the secondary is reverted"
node call "$secondary" /api/settings/set blockListUpdateIntervalHours=12 >/dev/null
node call "$secondary" /api/blocked/add domain=hand.lab.example.net >/dev/null
sleep 3
python3 -I -c "
import json
d = json.load(open('$work/input.json')); d['syncWaitSeconds'] = 0
json.dump(d, open('$work/input.json', 'w'))"
if action verify 2>"$work/verify.err"; then fail "verification missed the hand edit"; fi
rg -q 'hand.lab.example.net' "$work/verify.err" || { cat "$work/verify.err"; fail "unexpected failure"; }
render
plan
python3 -I -c "
import json
c = json.load(open('$work/changes.json'))
assert 'terraform_data.undeclared_entries' in c['addresses'], c
assert 'technitium_server_settings.this' in c['addresses'], c
assert c['pruneBlocked'] == ['hand.lab.example.net'], c"
# Approved a moment ago, and here is an entry nobody has seen.
node call "$primary" /api/blocked/add domain=added-after-approval.lab.example.net >/dev/null
apply
node export "$primary" /api/blocked/export | rg -q '^added-after-approval.lab.example.net$' || fail "an entry nobody approved was removed"
if node export "$primary" /api/blocked/export | rg -q '^hand.lab.example.net$'; then fail "the approved entry stayed"; fi
render
plan
apply
verified
[[ $(node export "$primary" /api/blocked/export | sort | tr '\n' ' ') == 'bad.lab.example.net later.lab.example.net ' ]] || fail "the hand-added entry stayed"
scenario "drift from a hand edit on the secondary, including an entry OpenTofu cannot see"

log "Empty lists"
declare_ 'd["blocking"].update(lists=[], blocked=[], allowed=[], listProbes=[])'
plan
apply
verified
plan
no_changes "a plan with empty lists"
verified
expect_dns "$secondary" bad.lab.example.net resolves
scenario "an empty list, and the plan stays clean"
declare_ 'd["blocking"].update(lists=["http://'$upstream':8000/one.txt"], blocked=["bad.lab.example.net"], allowed=["ok.lab.example.net"], listProbes=["list1.lab.example.net"])'
plan
apply
verified

log "A rebuilt secondary"
podman rm -f -v -t 0 "$name-dns2" >/dev/null
podman unshare rm -rf "$work/dns2"
mkdir -p "$work/dns2"
start_node dns2 "$secondary"
sleep 35

log "Its new certificate gets nothing until it is trusted"
logins() { podman exec "$name-dns2" sh -c "cat /var/log/technitium/dns/*.log 2>/dev/null | grep -c 'logged in' || true"; }
before=$(logins)
if action inspect 2>"$work/inspect.err"; then
  [[ $(state_of home/moraband/dns2) == unverified ]] || fail "the rebuilt member was not refused"
fi
if action verify 2>"$work/verify.err"; then fail "verification passed on a certificate nobody trusted"; fi
rg -q 'another certificate than the one trusted' "$work/verify.err" || { cat "$work/verify.err"; fail "unexpected failure"; }
python3 -I -c "
import json
r = json.load(open('$work/verify.json'))
assert r['certificates'][0]['endpoint'].endswith('.12:53443'), r"
[[ $(logins) == "$before" ]] || fail "the password reached an untrusted certificate"
echo "the member was sent nothing, and its certificate is reported for review"
scenario "a changed certificate withholds the password"
trust_certificates

plan
no_changes "OpenTofu does not see a rebuilt secondary, which is why verification exists:"
[[ $(state_of home/moraband/dns2) == stale ]] || fail "the secondary should be stale"
[[ $(dns "$secondary" bad.lab.example.net) != blocked ]] || fail "a rebuilt secondary cannot be blocking"
echo "it serves DNS with no blocking, and the plan has nothing to say"
if action verify 2>"$work/verify.err"; then fail "verification passed on a rebuilt secondary"; fi
cat "$work/verify.err"
rg -q 'home/moraband/dns2' "$work/verify.err" || fail "the failure does not name the member"
if rg -q '^home/korriban/dns1|\| home/korriban/dns1' "$work/verify.err"; then fail "the primary is blamed too"; fi
id=$(sed -n 's/.*secondaryNodeId=\([0-9]*\).*/\1/p' "$work/verify.err" | head -1)
[[ -n $id ]] || fail "the failure gives no recovery"
python3 -I -c "
import json
r = json.load(open('$work/verify.json'))
assert not r['ok']
assert [m['ok'] for m in r['members']] == [True, False], r['members']"
scenario "a rebuilt secondary makes verification fail, naming it alone"

log "The documented recovery"
node call "$primary" /api/admin/cluster/primary/deleteSecondary "secondaryNodeId=$id" >/dev/null
plan
[[ $(state_of home/moraband/dns2) == absent ]] || fail "the secondary should be absent now"
[[ $(field join) == '["home/moraband/dns2"]' ]] || fail "the plan does not rejoin it"
apply
verified
eventually_dns "$secondary" bad.lab.example.net blocked
scenario "the documented recovery restores it"

log "Primary down"
podman stop -t 5 "$name-dns1" >/dev/null
expect_dns "$secondary" bad.lab.example.net blocked
expect_dns "$secondary" control.lab.example.net resolves
if action inspect 2>"$work/inspect.err"; then fail "inspect passed without a primary"; fi
rg -q 'The primary home/korriban/dns1' "$work/inspect.err" || { cat "$work/inspect.err"; fail "unexpected failure"; }
rg -q 'secondaries keep answering' "$work/inspect.err" || fail "it does not say the secondaries keep answering"
if tofu plan -input=false -no-color >"$work/down.log" 2>&1; then fail "a plan worked without a primary"; fi
if action verify 2>"$work/verify.err"; then fail "verification passed without a primary"; fi
python3 -I -c "
import json
r = json.load(open('$work/verify.json'))
assert [m['ok'] for m in r['members']] == [False, True], r['members']"
echo "the secondary keeps blocking, and only the primary is blamed"
podman restart -t 5 "$name-dns2" >/dev/null
for _ in $(seq 1 60); do node call "$secondary" /api/admin/cluster/state >/dev/null 2>&1 && break; sleep 1; done
expect_dns "$secondary" bad.lab.example.net blocked
echo "it survives a restart"
podman start "$name-dns1" >/dev/null
for _ in $(seq 1 60); do node call "$primary" /api/admin/cluster/state >/dev/null 2>&1 && break; sleep 1; done
sleep 35
plan
no_changes "a plan after the primary came back"
verified
scenario "primary down: the secondary serves, planning fails, and the plan is clean afterwards"

log "A pin on the wrong certificate stops the provider too"
python3 -I - "$work" <<'PY'
import json, sys
work = sys.argv[1]
d = json.load(open(f"{work}/desired.json"))
open(f"{work}/run/root/octopushy-primary.pem", "w").write(d["trusted"][f"https://{d['secondaries']['home/moraband/dns2']}:53443"]["pem"])
PY
if tofu plan -input=false -no-color >"$work/wrong-pin.log" 2>&1; then fail "the provider talked to a certificate it was not pinned to"; fi
rg -q 'unknown authority' "$work/wrong-pin.log" || { tail -5 "$work/wrong-pin.log"; fail "unexpected failure"; }
render
scenario "the provider refuses any certificate but the pinned one"

log "Removing a member"
declare_ 'd["secondaries"] = {}'
plan
python3 -I -c "
import json
c = json.load(open('$work/changes.json'))
assert c['actions']['terraform_data.secondary[\"home/moraband/dns2\"]'] == ['delete'], c"
apply
node call "$primary" /api/admin/cluster/state | python3 -I -c "
import json, sys
nodes = json.load(sys.stdin)['response']['clusterNodes']
assert [n['type'] for n in nodes] == ['Primary'], nodes"
scenario "removing a member is a delete in the plan, and it leaves the cluster"

log "What stays refused"
if tofu plan -destroy -input=false -no-color >"$work/destroy.log" 2>&1; then fail "a destroy was planned"; fi
rg -q 'prevent_destroy' "$work/destroy.log" || { tail "$work/destroy.log"; fail "unexpected failure"; }
declare_ 'd["domain"] = "other.lab.example.net"'
if tofu plan -input=false -no-color >"$work/domain.log" 2>&1; then fail "a new domain was planned"; fi
rg -q 'prevent_destroy' "$work/domain.log" || { tail "$work/domain.log"; fail "unexpected failure"; }
scan_password
scenario "destroying the cluster and changing its domain are refused"

log "Passed"
printf '  %s\n' "${passed[@]}"
