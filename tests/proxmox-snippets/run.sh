#!/usr/bin/env bash
# Runs deploy/proxmox/snippets.sh against a stand-in host: Debian with systemd and OpenSSH, in a
# disposable podman container, with pvesh and pvesm stubbed. It checks what the account can and
# cannot do over SSH, that root's sshd settings do not change, that a second run changes nothing,
# that a broken sshd configuration is put back, and that --remove leaves the host as it was.
#
#   tests/proxmox-snippets/run.sh [bookworm|trixie ...]   (default: both)
#
# Proxmox VE 8 is on bookworm and 9 on trixie. Nothing of Proxmox itself runs here.
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd -- "$here/../.." && pwd)
script=$repo/deploy/proxmox/snippets.sh
work=$(mktemp -d "${TMPDIR:-/tmp}/octopushy-snippets.XXXXXX")
port=${OCTOPUSHY_SNIPPETS_TEST_PORT:-22987}
name=octopushy-snippets-test-$$
trap 'podman rm -f "$name" >/dev/null 2>&1 || true; rm -rf -- "$work"' EXIT

fail() { printf '\033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }
pass() { printf '\033[32mok\033[0m   %s\n' "$*"; }
log() { printf '\033[1m== %s\033[0m\n' "$*"; }

ssh-keygen -q -t ed25519 -N '' -C 'octopushy-snippets@home/pve01' -f "$work/id"
ssh-keygen -q -t ed25519 -N '' -C 'octopushy-snippets@home/pve01' -f "$work/id2"
opts=(-o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o "UserKnownHostsFile=$work/known_hosts" -o LogLevel=ERROR -o BatchMode=yes -o ConnectTimeout=10)
sftp_as() { sftp -i "$1" "${opts[@]}" -P "$port" -b - octopushy-snippets@127.0.0.1; }
in_host() { podman exec "$name" "$@"; }
setup() { podman exec -e OCTOPUSHY_SNIPPETS_KEY="$(cat "$1")" "$name" bash /root/snippets.sh; }

start() {
  podman run -d --name "$name" --privileged --systemd=always -p "127.0.0.1:$port:22" \
    -v "$script:/root/snippets.sh:ro,z" "localhost/octopushy-snippets-host:$1" >/dev/null
  timeout 60 sh -c "until podman exec $name systemctl is-active ssh >/dev/null 2>&1; do sleep 1; done" || fail "sshd did not start"
}

run_on() {
  local debian=$1
  log "Debian $debian"
  rm -f -- "$work/known_hosts"
  podman build -q --build-arg "DEBIAN=$debian" -t "localhost/octopushy-snippets-host:$debian" "$here" >/dev/null
  start "$debian"
  in_host sshd -T -C user=root,host=x,addr=127.0.0.1 >"$work/root-before"
  in_host sh -c 'cat /etc/passwd /etc/group; ls -1A /var/lib; find /etc/ssh /etc/systemd/system | sort' >"$work/host-before"

  setup "$work/id.pub" >"$work/first" || { cat "$work/first"; fail "the first run failed"; }
  grep -q "added snippets to the content of local" "$work/first" || fail "snippets were not added to local"
  grep -q 'iso,vztmpl,backup,import,snippets' <(in_host cat /etc/pve-stub/storage.json) || fail "the content list lost an entry"
  grep -q "$(in_host cat /etc/ssh/ssh_host_ed25519_key.pub | cut -d' ' -f2)" "$work/first" || fail "the host key was not printed"
  pass "first run adds snippets, the account, the chroot and sshd's block, and prints the host key"

  printf 'hello: secret\n' >"$work/user-data.yaml"
  printf 'put %s /var/lib/vz/snippets/guest-user-data.yaml\n' "$work/user-data.yaml" | sftp_as "$work/id" >/dev/null || fail "the upload failed"
  [[ $(in_host stat -c '%U %a' /var/lib/vz/snippets/guest-user-data.yaml) == "octopushy-snippets 600" ]] || fail "the snippet is not the account's, mode 600"
  [[ $(in_host cat /var/lib/vz/snippets/guest-user-data.yaml) == "hello: secret" ]] || fail "the snippet's content differs"
  pass "an upload to the datastore's snippets path lands in the real directory, mode 600"

  listing=$(printf 'ls /\n' | sftp_as "$work/id" 2>&1)
  [[ $(grep -cv '^sftp>' <<<"$listing") == 1 && $listing == *'/var'* ]] || fail "the chroot shows more than /var: $listing"
  printf 'put %s /var/lib/vz/escape.yaml\n' "$work/user-data.yaml" | sftp_as "$work/id" >/dev/null 2>&1 && fail "wrote outside snippets"
  printf 'get /etc/passwd %s/passwd\n' "$work" | sftp_as "$work/id" >/dev/null 2>&1 && fail "read /etc/passwd"
  pass "the chroot holds only the snippets path, and nothing outside it is writable or readable"

  out=$(ssh -i "$work/id" "${opts[@]}" -p "$port" octopushy-snippets@127.0.0.1 id 2>&1 || true)
  [[ $out == *'sftp connections only'* ]] || fail "a command ran: $out"
  out=$(ssh -i "$work/id" "${opts[@]}" -p "$port" -W 127.0.0.1:22 octopushy-snippets@127.0.0.1 </dev/null 2>&1 || true)
  [[ $out == *'prohibited'* || $out == *'forwarding failed'* ]] || fail "forwarding was allowed: $out"
  ssh -o PubkeyAuthentication=no -o PreferredAuthentications=password,keyboard-interactive "${opts[@]}" -p "$port" \
    octopushy-snippets@127.0.0.1 true >/dev/null 2>&1 && fail "a password login worked"
  printf 'ls /\n' | sftp_as "$work/id2" >/dev/null 2>&1 && fail "another key logged in"
  pass "no command, no forwarding, no password, and no other key"

  in_host sshd -T -C user=root,host=x,addr=127.0.0.1 >"$work/root-after"
  diff "$work/root-before" "$work/root-after" >/dev/null || fail "root's sshd settings changed: $(diff "$work/root-before" "$work/root-after")"
  pass "root's effective sshd settings are unchanged"

  setup "$work/id.pub" >"$work/second" || fail "the second run failed"
  grep -q 'nothing changed' "$work/second" || { cat "$work/second"; fail "the second run changed something"; }
  pass "a second run changes nothing"

  setup "$work/id2.pub" >"$work/third" || fail "the run with a new key failed"
  [[ $(grep -c 'changed:' "$work/third") == 1 ]] || { cat "$work/third"; fail "a new key changed more than the key"; }
  printf 'ls /\n' | sftp_as "$work/id" >/dev/null 2>&1 && fail "the old key still logs in"
  printf 'ls /\n' | sftp_as "$work/id2" >/dev/null || fail "the new key does not log in"
  pass "a new key replaces the old one, and changes nothing else"

  podman restart -t 10 "$name" >/dev/null
  timeout 60 sh -c "until podman exec $name systemctl is-active ssh >/dev/null 2>&1; do sleep 1; done"
  printf 'ls /var/lib/vz/snippets\n' | sftp_as "$work/id2" | grep -q guest-user-data.yaml || fail "the chroot is empty after a restart"
  pass "the bind mount comes back after a restart"

  # A configuration sshd refuses is put back, and sshd keeps running what it had.
  in_host sh -c 'rm /etc/ssh/sshd_config.d/octopushy-snippets.conf; echo "NotADirective yes" > /etc/ssh/sshd_config.d/00-broken.conf'
  if setup "$work/id2.pub" >"$work/broken" 2>&1; then fail "the run passed with a broken sshd configuration"; fi
  grep -q 'nothing in sshd was changed' "$work/broken" || { cat "$work/broken"; fail "the refusal did not say so"; }
  in_host test ! -e /etc/ssh/sshd_config.d/octopushy-snippets.conf || fail "the refused block was left in place"
  in_host systemctl is-active ssh >/dev/null || fail "sshd stopped"
  in_host rm /etc/ssh/sshd_config.d/00-broken.conf
  setup "$work/id2.pub" >/dev/null || fail "the run after the fix failed"
  pass "a configuration sshd -t refuses is put back, and sshd keeps running"

  in_host bash /root/snippets.sh --remove >"$work/remove" || { cat "$work/remove"; fail "--remove failed"; }
  printf 'ls /\n' | sftp_as "$work/id2" >/dev/null 2>&1 && fail "the account still logs in after --remove"
  in_host sh -c 'cat /etc/passwd /etc/group; ls -1A /var/lib; find /etc/ssh /etc/systemd/system | sort' >"$work/host-after"
  diff "$work/host-before" "$work/host-after" >/dev/null \
    || fail "--remove left something: $(diff "$work/host-before" "$work/host-after")"
  in_host sshd -T -C user=root,host=x,addr=127.0.0.1 >"$work/root-removed"
  diff "$work/root-before" "$work/root-removed" >/dev/null || fail "root's sshd settings differ after --remove"
  [[ $(in_host cat /var/lib/vz/snippets/guest-user-data.yaml) == "hello: secret" ]] || fail "--remove deleted a snippet"
  in_host bash /root/snippets.sh --remove | grep -q '0 change(s)' || fail "a second --remove changed something"
  pass "--remove undoes the account, chroot, mount and sshd block, and keeps the snippets"

  podman rm -f "$name" >/dev/null
}

for debian in "${@:-bookworm trixie}"; do
  for d in $debian; do run_on "$d"; done
done
log "all passed"
