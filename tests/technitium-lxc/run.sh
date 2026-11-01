#!/usr/bin/env bash
# The technitium-lxc root against disposable local containers. OpenTofu and the bpg/proxmox
# provider are real, and so is the install: a systemd container made from the pinned Debian
# template, with real Technitium in it. Only Proxmox is a stand-in (fake_proxmox.py), so this
# proves nothing about Proxmox itself, its firewall, or an unprivileged LXC container.
#
# Plans and applies in separate steps, as the agent does, and checks that the password is not
# in the saved plan, the state, the logs, or the container. Every resource is named
# octopushy-technitium-lxc-* and removed by that exact name.
#
# This installs for real, so it is not an offline test: it needs rootless podman on a network
# that reaches the sites `reachable` lists below, and it stops with a clear message if it
# cannot, including on an --internal network. tests/technitium-cluster is the one that runs with
# no way out.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/technitium-lxc
name=octopushy-technitium-lxc
image=$name:template
tofu_image=ghcr.io/opentofu/opentofu:1.13.1
module=modules/opentofu/technitium-lxc
password='A test password, with & = + % in it'
token='root@pam!fake=00000000-0000-4000-8000-000000000000'
address=10.199.0.20
template_url=$(sed -n 's/^  url: //p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")
template_sha512=$(sed -n 's/^  checksum: sha512://p' "$root/examples/lab/templates/debian-13-lxc/template.yaml")

log() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
cleanup() {
  podman rm -f -t 0 "$name-ct" "$name-proxmox" >/dev/null 2>&1 || true
  podman network rm "$name" >/dev/null 2>&1 || true
  podman image rm "$image" >/dev/null 2>&1 || true
  # Files of the container's own users are outside this user's reach.
  podman unshare rm -rf "$work/etc-dns" "$work/share" 2>/dev/null || true
}
trap cleanup EXIT

in_ct() { podman exec "$name-ct" sh -c "$1"; }

tofu() {
  podman run --rm --name "$name-tofu" --log-driver=none --cap-drop=all --security-opt=no-new-privileges \
    --read-only --tmpfs /tmp:rw,mode=1777 --userns=keep-id --network "$name" \
    -v "$work/run:/w:Z" -w "/w/repo/$module" -e HOME=/w/home -e TF_IN_AUTOMATION=1 -e TF_INPUT=0 \
    -e TF_PLUGIN_CACHE_DIR=/w/cache -e PROXMOX_VE_ENDPOINT=https://10.199.0.10:8006/ -e PROXMOX_VE_INSECURE=true \
    -e PROXMOX_VE_API_TOKEN="$token" ${tofu_env:-} "$tofu_image" "$@"
}
plan() { tofu plan -input=false -no-color -detailed-exitcode -out=/w/out/plan.bin "$@" || [[ $? == 2 ]]; }
apply() {
  # The password is in the environment of podman, which copies it by name.
  TF_VAR_admin_password=${1-$password} tofu_env="-e TF_VAR_admin_password" \
    tofu apply -input=false -no-color /w/out/plan.bin 2>&1 | tee -a "$work/apply.log"
  return "${PIPESTATUS[0]}"
}

installs() {
  apply | grep -E 'Technitium [0-9]|technitium-lxc:' || { tail -20 "$work/apply.log"; fail "the apply failed"; }
}

start_container() {
  podman run -d --name "$name-ct" --hostname krb-dns-01 --network "$name" --ip "$address" --privileged --systemd=always \
    -v "$work/etc-dns:/etc/dns:Z" -v "$work/share/ssh:/root/.ssh:z" "$image" /sbin/init >/dev/null
  sleep 5
  in_ct 'systemctl enable --now ssh >/dev/null 2>&1'
}

identity() { in_ct 'cd /etc/dns && sha256sum self-signed-cert.pfx && ls -1'; }

# What the container and OpenTofu download from, as docs/technitium-lxc.md lists them.
reachable() {
  local check='
import socket, sys
missing = []
for host, port in (("registry.opentofu.org", 443), ("github.com", 443), ("deb.debian.org", 80),
                   ("builds.dotnet.microsoft.com", 443), ("download.technitium.com", 443)):
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
    echo "It cannot pass on an --internal network. tests/technitium-cluster needs none." >&2
    exit 2
  fi
  echo "the download sites answer"
}

log "Disposable resources"
cleanup
mkdir -p "$work/run/home" "$work/run/cache" "$work/run/out" "$work/etc-dns" "$work/share/ssh"
chmod 0700 "$work/share/ssh"
rm -f "$work/apply.log"
template=$work/$(basename "$template_url")
[[ -f $template ]] || curl -fsS -o "$template" "$template_url"
sha512sum -c --quiet - <<<"$template_sha512  $template"
podman import -q --change 'CMD ["/sbin/init"]' "$template" "$image" >/dev/null
podman network create --subnet 10.199.0.0/24 "$name" >/dev/null
reachable
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/share/tls.key" -out "$work/share/tls.crt" -days 1 \
  -subj /CN=fake-proxmox -addext subjectAltName=IP:10.199.0.10 2>/dev/null
podman run -d --name "$name-proxmox" --network "$name" --ip 10.199.0.10 -v "$work/share:/share:Z" \
  -v "$root/tests/technitium-lxc/fake_proxmox.py:/fake_proxmox.py:ro,Z" docker.io/library/python:3-alpine \
  python3 -I /fake_proxmox.py /share /share/tls.crt /share/tls.key >/dev/null
start_container

log "Repository, as the agent lays it out"
rm -rf "$work/run/repo" && mkdir -p "$work/run/repo/modules/opentofu"
cp -r "$root"/examples/lab/modules/opentofu/{proxmox-lxc,technitium-lxc} "$work/run/repo/modules/opentofu/"
cat >"$work/run/repo/$module/octopushy.auto.tfvars.json" <<JSON
{
  "octopushy": { "address": "home/korriban/krb-dns-01", "stack": "home.korriban.krb-dns-01", "lab": "home",
    "installation": "korriban", "revision": "test" },
  "guest": { "id": "krb-dns-01", "vmid": 320, "host": "korriban",
    "hardware": { "cores": 2, "memory": 2048, "swap": 512, "disks": [{ "size": 8 }],
      "mountPoints": [{ "path": "/etc/dns", "size": 4 }] },
    "network": { "bridge": "vmbr0", "mode": "static", "address": "$address/24", "gateway": "10.199.0.1",
      "dns": ["10.199.0.1"] },
    "inputs": { "startup": { "order": 1 } } },
  "host": { "id": "korriban", "datastores": { "images": "local", "disks": "local-lvm" } },
  "template": { "id": "debian-13-lxc", "os": "debian", "source": { "url": "$template_url",
    "checksum": "$template_sha512", "checksumAlgorithm": "sha512", "fileName": "$(basename "$template_url")" } }
}
JSON
printf '{"terraform":{"backend":{"local":{"path":"../../../state/terraform.tfstate"}}}}' \
  >"$work/run/repo/$module/octopushy_backend.tf.json"
tofu init -input=false -no-color -lockfile=readonly | tail -1

log "First apply"
plan | tail -1
installs
in_ct 'systemctl is-enabled dns.service dns-http-guard.service; systemctl is-active dns.service dns-http-guard.service'
[[ $(plan | grep -c 'No changes') == 1 ]] || fail "a second plan should have no changes"

log "The request Proxmox received"
create=$(grep '"/nodes/korriban/lxc"' "$work/share/requests.log")
for setting in '"unprivileged": "1"' '"features": "nesting=1"' '"protection": "1"' '"onboot": "1"' \
  '"mp0": "backup=1,mp=/etc/dns,volume=local-lvm:4"' '"startup": "order=1"' '"nameserver": "10.199.0.1"'; do
  grep -qF "$setting" <<<"$create" || fail "the create request has no $setting"
done
if grep -q UNHANDLED "$work/share/requests.log"; then fail "the provider made a request the stand-in does not know"; fi

log "The server"
in_ct "curl -s --data 'user=admin&pass=admin' http://127.0.0.1:5380/api/user/login" | grep -q '"status":"error"' \
  || fail "admin/admin works"
podman exec "$name-proxmox" python3 -I -c "
import socket
for port, expected in ((5380, False), (53443, True), (53, True), (22, True)):
    s = socket.socket(); s.settimeout(3)
    try:
        s.connect(('$address', port)); reached = True
    except OSError:
        reached = False
    assert reached == expected, (port, reached)
    print(port, 'reachable' if reached else 'not reachable')
"
[[ $(in_ct 'cat /etc/resolv.conf' | grep -c '^nameserver 10.199.0.1$') == 1 ]] || fail "resolv.conf changed"

log "What anyone answering on SSH would have received"
# OpenTofu does not check the host key, so this is what an impostor gets. A stand-in for
# install.sh keeps the upload, which is otherwise gone by the time a test could look.
cp "$work/run/repo/$module/install.sh" "$work/install.sh.kept"
printf '#!/bin/sh\ncp /run/octopushy-technitium/admin-password.sealed /root/captured\n' >"$work/run/repo/$module/install.sh"
plan | tail -1
apply >/dev/null
in_ct 'cat /root/captured; rm /root/captured' >"$work/captured"
cp "$work/install.sh.kept" "$work/run/repo/$module/install.sh"
python3 -I - "$password" "$work/captured" <<'PY'
import base64, sys
secret, captured = sys.argv[1].encode(), open(sys.argv[2], "rb").read().strip()
assert len(captured) > 20, captured
encoded = base64.b64encode(secret).rstrip(b"=")
assert secret not in captured and encoded not in captured and encoded[:8] not in captured, "the upload is readable"
print("only ciphertext crossed SSH")
PY
in_ct 'grep -q "octopushy-bootstrap=" /root/.ssh/authorized_keys'
plan | tail -1
installs

log "The certificate can be pinned"
in_ct "openssl s_client -connect 127.0.0.1:53443 </dev/null 2>/dev/null | openssl x509 -noout -ext subjectAltName" | grep -q "IP Address:$address" \
  || fail "the certificate does not name the address"

log "A failed guard keeps DNS down, rather than exposing the admin port"
in_ct 'systemctl cat dns.service' | grep -q '^Requires=dns-http-guard.service' || fail "dns.service does not require the guard"
in_ct 'systemctl stop dns.service; mv /etc/dns-http-guard.nft /etc/dns-http-guard.nft.kept; systemctl restart dns-http-guard.service || true; systemctl start dns.service || true'
sleep 2
[[ $(in_ct 'systemctl is-active dns.service || true') != active ]] || fail "DNS started without its guard"
in_ct 'mv /etc/dns-http-guard.nft.kept /etc/dns-http-guard.nft; systemctl restart dns-http-guard.service; systemctl start dns.service'
sleep 3
echo "DNS waits for the guard"

log "The password is nowhere"
scan() {
  tofu show -json /w/out/plan.bin >"$work/plan.json"
  python3 -I - "$password" "$work" <<'PY'
import sys, zipfile
secret, work = sys.argv[1].encode(), sys.argv[2]
places = {"tofu show -json": open(f"{work}/plan.json", "rb").read(),
          "state": open(f"{work}/run/repo/state/terraform.tfstate", "rb").read(),
          "apply log": open(f"{work}/apply.log", "rb").read(),
          "saved plan": open(f"{work}/run/out/plan.bin", "rb").read()}
plan = zipfile.ZipFile(f"{work}/run/out/plan.bin")
places.update({f"saved plan, {name}": plan.read(name) for name in plan.namelist()})
assert len(places["tofu show -json"]) > 1000
for place, content in places.items():
    assert secret not in content, place
print(f"not in {len(places)} places")
PY
}
plan -replace=terraform_data.install | tail -1
scan
# Octopushy gives the plan no password. Being ephemeral, it would stay out even if it did.
TF_VAR_admin_password=$password tofu_env="-e TF_VAR_admin_password" plan -replace=terraform_data.install | tail -1
scan
in_ct "! grep -rlF -- '$password' /etc /run /var/tmp /tmp /var/log /root 2>/dev/null"
in_ct '! test -e /run/octopushy-technitium && ! test -e /run/systemd/system/dns.service.d'
echo "not in the container either"

log "Installing again changes nothing"
before=$(identity)
service=$(in_ct 'systemctl show -p MainPID --value dns.service')
installs
[[ $(identity) == "$before" ]] || fail "/etc/dns changed"
[[ $(in_ct 'systemctl show -p MainPID --value dns.service') == "$service" ]] || fail "the service restarted"

log "A wrong password fails the apply and stays out of the guest"
plan -replace=terraform_data.install | tail -1
if apply 'another password here' >"$work/wrong.log"; then fail "a wrong password was accepted"; fi
grep -q 'the intended admin password does not work' "$work/wrong.log" || fail "unexpected failure"
in_ct 'systemctl is-active dns.service; ! test -e /run/octopushy-technitium'

log "No password fails the apply"
plan -replace=terraform_data.install | tail -1
if TF_VAR_admin_password= tofu apply -input=false -no-color /w/out/plan.bin >/dev/null 2>&1; then
  fail "an apply without the password succeeded"
fi

log "A first start that falls back to admin/admin fails, and the next one is a real first start"
in_ct 'systemctl stop dns.service; rm /etc/dns/auth.config'
podman exec "$name-ct" sh -c 'mkdir -p /run/octopushy-technitium && printf "\nrest" >/run/octopushy-technitium/admin-password'
podman cp "$work/run/repo/$module/install.sh" "$name-ct:/run/octopushy-technitium/install.sh"
if podman exec -e TECHNITIUM_VERSION=15.6.2 \
  -e TECHNITIUM_SHA256=51f2d4ebaf156a85035e820afde5e1d428f1920a37d7f0c65203fb1766917e08 -e ASPNETCORE_VERSION=10.0.14 \
  -e ASPNETCORE_SHA512=dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031 \
  -e GUEST_ADDRESS="$address" -e NAMESERVERS=10.199.0.1 "$name-ct" bash /run/octopushy-technitium/install.sh; then
  fail "the fallback to admin/admin was not noticed"
fi
in_ct '! test -e /etc/dns/auth.config; ! test -e /run/octopushy-technitium'
plan -replace=terraform_data.install | tail -1
installs

log "A new root disk with the same /etc/dns"
before=$(identity)
podman rm -f -t 0 "$name-ct" >/dev/null
start_container
plan -replace=terraform_data.install | tail -1
installs
[[ $(identity) == "$before" ]] || fail "/etc/dns changed"

log "A reboot needs nothing but the container"
podman restart -t 5 "$name-ct" >/dev/null
sleep 12
in_ct 'systemctl is-active dns.service dns-http-guard.service; nft list tables'
podman exec "$name-proxmox" python3 -I -c "
import socket, struct
query = struct.pack('>HHHHHH', 0x1234, 0x0100, 1, 0, 0, 0) + b'\x09localhost\x00' + struct.pack('>HH', 1, 1)
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(5)
s.sendto(query, ('$address', 53))
assert s.recvfrom(512)[0][:2] == b'\x12\x34'
print('UDP 53 answers')
"

log "The firewall, when asked for"
python3 -I - "$work/run/repo/$module/octopushy.auto.tfvars.json" <<'PY'
import json, sys
variables = json.load(open(sys.argv[1]))
variables["guest"]["inputs"]["firewall"] = {"enabled": True, "adminSources": ["10.199.0.0/24"]}
json.dump(variables, open(sys.argv[1], "w"))
PY
plan | tail -1
TF_VAR_admin_password=$password tofu_env="-e TF_VAR_admin_password" tofu apply -input=false -no-color /w/out/plan.bin >"$work/firewall.log" 2>&1 \
  || { tail -20 "$work/firewall.log"; fail "the apply failed"; }
requests=$work/share/requests.log
grep -q '"/nodes/korriban/lxc/320/firewall/options"' "$requests" || fail "the firewall was not enabled"
grep '/firewall/options' "$requests" | grep -q '"policy_in": "DROP"' || fail "the policy does not drop"
[[ $(grep -c '/firewall/rules"' "$requests") == 4 ]] || fail "expected four rules"
if grep '/firewall/rules"' "$requests" | grep -q 5380; then fail "port 5380 was opened"; fi
grep '/firewall/rules"' "$requests" | grep '"dport": "53443"' | grep -q '"source": "10.199.0.0/24"' || fail "53443 is not limited"
grep -q 'firewall=1' "$requests" || fail "net0 does not use the firewall"
if grep -q UNHANDLED "$requests"; then fail "the provider made a request the stand-in does not know"; fi
echo "four rules, and everything else dropped"

log "proxmox-lxc as a root of its own"
rm -rf "$work/run/repo/modules/opentofu/proxmox-lxc-root"
cp -r "$work/run/repo/modules/opentofu/proxmox-lxc" "$work/run/repo/modules/opentofu/proxmox-lxc-root"
sed -e 's/"mode": "static", "address": "[^"]*", "gateway": "[^"]*"/"mode": "dhcp"/' -e 's/"dns": \["10.199.0.1"\]/"dns": []/' \
  "$work/run/repo/$module/octopushy.auto.tfvars.json" >"$work/run/repo/modules/opentofu/proxmox-lxc-root/octopushy.auto.tfvars.json"
module=modules/opentofu/proxmox-lxc-root
tofu init -input=false -no-color -lockfile=readonly | tail -1
tofu plan -input=false -no-color -detailed-exitcode | tail -3 || [[ $? == 2 ]]

log "Passed"
