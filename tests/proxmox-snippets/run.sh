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

# Everything the script may change, so a refusal can be shown to have changed none of it.
snapshot() {
  in_host sh -c 'cat /etc/passwd /etc/group; tr -d "\n" < /etc/pve-stub/storage.json; echo; ls -1A /var/lib; stat -c "%n %U %G %a" /var/lib/vz /var/lib/vz/* 2>/dev/null
    find /etc/ssh /etc/systemd/system | sort; for f in /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*; do [ -f "$f" ] && sha256sum "$f"; done
    for a in 127.0.0.1 192.0.2.1 198.51.100.7; do for u in root alice octopushy-snippets; do echo "== $u $a"; sshd -T -C user=$u,host=x,addr=$a; done; done; true' 2>&1
}
# A run that has to be refused: it fails, says why, and leaves everything as it was.
refused() {
  local why=$1 what=$2; shift 2
  snapshot >"$work/state-before"
  if "$@" >"$work/refused" 2>&1; then cat "$work/refused"; fail "$what: the run passed"; fi
  grep -q -- "$why" "$work/refused" || { cat "$work/refused"; fail "$what: the refusal does not say '$why'"; }
  grep -q 'changed:' "$work/refused" && { cat "$work/refused"; fail "$what: something changed before the refusal"; }
  snapshot >"$work/state-after"
  diff "$work/state-before" "$work/state-after" >/dev/null || fail "$what: the host changed: $(diff "$work/state-before" "$work/state-after" | head -20)"
}
sftp_ok() { printf 'ls /\n' | sftp_as "$1" >/dev/null 2>&1; }
dropin() { in_host sh -c "printf '$2' > /etc/ssh/sshd_config.d/$1"; }

run_on() {
  local debian=$1
  log "Debian $debian"
  rm -f -- "$work/known_hosts"
  podman build -q --build-arg "DEBIAN=$debian" -t "localhost/octopushy-snippets-host:$debian" "$here" >/dev/null
  start "$debian"
  in_host useradd --create-home --shell /bin/bash alice
  in_host sshd -T -C user=root,host=x,addr=127.0.0.1 >"$work/root-before"
  snapshot >"$work/host-before"

  # Refused before anything changes: root, and an account this script did not create.
  refused "cannot be root" "root" podman exec -e OCTOPUSHY_SNIPPETS_KEY="$(cat "$work/id.pub")" -e OCTOPUSHY_SNIPPETS_USER=root "$name" bash /root/snippets.sh
  refused "did not create it" "alice" podman exec -e OCTOPUSHY_SNIPPETS_KEY="$(cat "$work/id.pub")" -e OCTOPUSHY_SNIPPETS_USER=alice "$name" bash /root/snippets.sh
  pass "root and an account it did not create are refused, and the host is as it was"

  # Refused before anything changes: an invalid unrelated drop-in, so sshd -t already fails.
  dropin 50-broken.conf 'NotADirective yes\n'
  refused "refuses the configuration as it is" "a broken drop-in" setup "$work/id.pub"
  in_host rm /etc/ssh/sshd_config.d/50-broken.conf
  pass "with a drop-in sshd -t refuses, the first run changes nothing at all: no storage, account, directory or mount"

  # Refused: a block for the account on other addresses, earlier and later than the script's.
  dropin 00-context.conf 'Match User octopushy-snippets Address *,!127.0.0.1\n ChrootDirectory none\n'
  refused "sort before 00-octopushy-snippets.conf" "an earlier block for the account" setup "$work/id.pub"
  in_host rm /etc/ssh/sshd_config.d/00-context.conf
  dropin 60-context.conf 'Match User octopushy-snippets Address *,!127.0.0.1\n ChrootDirectory none\n'
  refused "other sshd files mention octopushy-snippets" "a later block for the account" setup "$work/id.pub"
  in_host rm /etc/ssh/sshd_config.d/60-context.conf
  pass "a block for the account on other addresses, before or after the script's, is refused"

  # Refused: the drop-in's name taken by a file the script did not write, whatever it holds.
  dropin 00-octopushy-snippets.conf 'Match User root Address 192.0.2.0/24\n PermitRootLogin no\n'
  refused "did not write it" "an unowned drop-in restricting root" setup "$work/id.pub"
  dropin 00-octopushy-snippets.conf 'Match User alice\n PasswordAuthentication no\n'
  refused "did not write it" "an unowned drop-in restricting alice" setup "$work/id.pub"
  in_host rm /etc/ssh/sshd_config.d/00-octopushy-snippets.conf
  pass "a drop-in of the same name that the script did not write is never touched"

  # Refused: an sshd_config that does not read sshd_config.d first.
  in_host sh -c 'cp /etc/ssh/sshd_config /root/sshd_config.orig; { echo "PasswordAuthentication no"; cat /root/sshd_config.orig; } > /etc/ssh/sshd_config'
  refused "must start with 'Include" "a setting before the Include" setup "$work/id.pub"
  in_host cp /root/sshd_config.orig /etc/ssh/sshd_config
  pass "an sshd_config with a setting before its Include is refused"

  # With the reviewer's root restriction in a file of another name, which the script never touches.
  dropin octopushy-snippets.conf 'Match User root Address 192.0.2.0/24\n PermitRootLogin no\n'
  dropin 00-root.conf 'Match User root\n PasswordAuthentication no\nMatch all\n'
  snapshot >"$work/with-restrictions"
  setup "$work/id.pub" >"$work/first" || { cat "$work/first"; fail "the first run failed"; }
  grep -q "added snippets to the content of local" "$work/first" || fail "snippets were not added to local"
  grep -q 'iso,vztmpl,backup,import,snippets' <(in_host cat /etc/pve-stub/storage.json) || fail "the content list lost an entry"
  grep -q "$(in_host cat /etc/ssh/ssh_host_ed25519_key.pub | cut -d' ' -f2)" "$work/first" || fail "the host key was not printed"
  for a in 127.0.0.1 192.0.2.1 198.51.100.7; do for u in root alice; do
    diff <(sed -n "/^== $u $a\$/,/^== /p" "$work/with-restrictions" | sed '1d;$d') \
      <(in_host sshd -T -C user=$u,host=x,addr=$a) >"$work/settings-diff" || { cat "$work/settings-diff"; fail "$u's effective settings from $a changed"; }
  done; done
  [[ $(in_host head -n 1 /etc/ssh/sshd_config.d/00-octopushy-snippets.conf) == '# Octopushy snippet uploads'* ]] || fail "the drop-in has no marker"
  in_host rm /etc/ssh/sshd_config.d/octopushy-snippets.conf /etc/ssh/sshd_config.d/00-root.conf
  pass "first run: snippets, account, chroot and drop-in, and root's and alice's settings unchanged for every address"

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
  sftp_ok "$work/id2" && fail "another key logged in"
  for a in 192.0.2.1 198.51.100.7; do
    effective=$(in_host sshd -T -C user=octopushy-snippets,host=x,addr=$a)
    grep -qx 'chrootdirectory /var/lib/octopushy-snippets' <<<"$effective" || fail "the account is not chrooted from $a"
  done
  pass "no command, no forwarding, no password, no other key, and the chroot from every address"

  in_host sshd -T -C user=root,host=x,addr=127.0.0.1 >"$work/root-after"
  diff "$work/root-before" "$work/root-after" >/dev/null || fail "root's sshd settings changed: $(diff "$work/root-before" "$work/root-after")"
  pass "root's effective sshd settings are unchanged"

  setup "$work/id.pub" >"$work/second" || fail "the second run failed"
  grep -q 'nothing changed' "$work/second" || { cat "$work/second"; fail "the second run changed something"; }
  pass "a second run changes nothing"

  setup "$work/id2.pub" >"$work/third" || fail "the run with a new key failed"
  [[ $(grep -c 'changed:' "$work/third") == 2 ]] && grep -q 'installed the key' "$work/third" && grep -q 'reloaded sshd' "$work/third" \
    || { cat "$work/third"; fail "a new key changed more than the key"; }
  sftp_ok "$work/id" && fail "the old key still logs in"
  sftp_ok "$work/id2" || fail "the new key does not log in"
  pass "a new key replaces the old one, and changes nothing else"

  podman restart -t 10 "$name" >/dev/null
  timeout 60 sh -c "until podman exec $name systemctl is-active ssh >/dev/null 2>&1; do sleep 1; done"
  printf 'ls /var/lib/vz/snippets\n' | sftp_as "$work/id2" | grep -q guest-user-data.yaml || fail "the chroot is empty after a restart"
  pass "the bind mount comes back after a restart"

  # Refused with sshd broken by something else: --remove changes nothing, and the account still works.
  dropin 50-broken.conf 'NotADirective yes\n'
  refused "refuses the configuration as it is" "--remove with a broken drop-in" in_host bash /root/snippets.sh --remove
  in_host rm /etc/ssh/sshd_config.d/50-broken.conf
  sftp_ok "$work/id2" || fail "the account no longer works after the refused --remove"
  pass "with a drop-in sshd -t refuses, --remove changes nothing, and the account keeps working"

  in_host bash /root/snippets.sh --remove >"$work/remove" || { cat "$work/remove"; fail "--remove failed"; }
  sftp_ok "$work/id2" && fail "the account still logs in after --remove"
  snapshot >"$work/host-after"
  # What --remove keeps on purpose: the datastore's snippets content and its snippets directory.
  kept() { grep -v '^/var/lib/vz/snippets ' "$1" | sed 's/,"snippets"\{0,1\}//; s/,snippets"/"/'; }
  diff <(kept "$work/host-before") <(kept "$work/host-after") >/dev/null \
    || fail "--remove left something: $(diff <(kept "$work/host-before") <(kept "$work/host-after") | head -20)"
  [[ $(in_host cat /var/lib/vz/snippets/guest-user-data.yaml) == "hello: secret" ]] || fail "--remove deleted a snippet"
  in_host bash /root/snippets.sh --remove | grep -q '0 change(s)' || fail "a second --remove changed something"
  pass "--remove undoes the account, chroot, mount and drop-in, keeps the snippets, and changes nothing a second time"

  podman rm -f "$name" >/dev/null
}

for debian in "${@:-bookworm trixie}"; do
  for d in $debian; do run_on "$d"; done
done
log "all passed"
