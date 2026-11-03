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
script=${SNIPPETS_SH:-$repo/deploy/proxmox/snippets.sh}
work=$(mktemp -d "${TMPDIR:-/tmp}/octopushy-snippets.XXXXXX")
port=${OCTOPUSHY_SNIPPETS_TEST_PORT:-22987}
name=octopushy-snippets-test-$$
trap 'podman rm -f "$name" >/dev/null 2>&1 || true; rm -rf -- "$work"' EXIT

fail() { printf '\033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }
pass() { printf '\033[32mok\033[0m   %s\n' "$*"; }
log() { printf '\033[1m== %s\033[0m\n' "$*"; }

ssh-keygen -q -t ed25519 -N '' -C 'octopushy-snippets@home/pve01' -f "$work/id" </dev/null
ssh-keygen -q -t ed25519 -N '' -C 'octopushy-snippets@home/pve01' -f "$work/id2" </dev/null
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
    for a in 127.0.0.1 192.0.2.1 198.51.100.7; do for u in root alice octopushy-snippets; do echo "== $u $a"; sshd -T -C user=$u,host=x,addr=$a,laddr=127.0.0.1,lport=22,rdomain=default; done; done; true' 2>&1
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
  in_host sshd -T -C user=root,host=x,addr=127.0.0.1,laddr=127.0.0.1,lport=22,rdomain=default >"$work/root-before"
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
      <(in_host sshd -T -C user=$u,host=x,addr=$a,laddr=127.0.0.1,lport=22,rdomain=default) >"$work/settings-diff" || { cat "$work/settings-diff"; fail "$u's effective settings from $a changed"; }
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
    effective=$(in_host sshd -T -C user=octopushy-snippets,host=x,addr=$a,laddr=127.0.0.1,lport=22,rdomain=default)
    grep -qx 'chrootdirectory /var/lib/octopushy-snippets' <<<"$effective" || fail "the account is not chrooted from $a"
  done
  pass "no command, no forwarding, no password, no other key, and the chroot from every address"

  in_host sshd -T -C user=root,host=x,addr=127.0.0.1,laddr=127.0.0.1,lport=22,rdomain=default >"$work/root-after"
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

  # Only the key in the account's file lets it in: not a certificate from a CA the host trusts.
  rm -f -- "$work/ca" "$work/ca.pub" "$work/certified" "$work/certified.pub" "$work/certified-cert.pub"
  ssh-keygen -q -t ed25519 -N '' -f "$work/ca" </dev/null
  ssh-keygen -q -t ed25519 -N '' -f "$work/certified" </dev/null
  ssh-keygen -q -s "$work/ca" -I test -n octopushy-snippets "$work/certified.pub" </dev/null
  podman cp "$work/ca.pub" "$name:/etc/ssh/user_ca.pub"
  in_host sh -c 'echo "TrustedUserCAKeys /etc/ssh/user_ca.pub" >> /etc/ssh/sshd_config && systemctl reload ssh'
  [[ $(in_host sshd -T -C user=root,host=x,addr=192.0.2.1,laddr=127.0.0.1,lport=22,rdomain=default | grep '^trustedusercakeys') == "trustedusercakeys /etc/ssh/user_ca.pub" ]] \
    || fail "the host does not trust the CA, so this proves nothing"
  setup "$work/id2.pub" >/dev/null || fail "the run with a host-wide CA failed"
  printf 'ls /\n' | sftp -i "$work/certified" -o CertificateFile="$work/certified-cert.pub" "${opts[@]}" -P "$port" -b - octopushy-snippets@127.0.0.1 >/dev/null 2>&1 \
    && fail "a certificate from the host's CA logged in as the account"
  for expected in "trustedusercakeys none" "authorizedkeyscommand none" "authorizedprincipalsfile none" \
    "authorizedprincipalscommand none" "hostbasedauthentication no" "gssapiauthentication no" "authenticationmethods publickey"; do
    in_host sshd -T -C user=octopushy-snippets,host=x,addr=192.0.2.1,laddr=127.0.0.1,lport=22,rdomain=default >"$work/effective"
    grep -qxF "$expected" "$work/effective" || fail "the account's effective settings lack '$expected'"
  done
  in_host sh -c 'sed -i "/^TrustedUserCAKeys/d" /etc/ssh/sshd_config && systemctl reload ssh'
  sftp_ok "$work/id2" || fail "the account's own key no longer logs in"
  pass "a certificate signed by a CA the host trusts does not log in as the account; only its own key does"

  # A valid configuration with a Match on the local port: setup and --remove read it.
  dropin 50-localport.conf 'Match LocalPort 2222\n PasswordAuthentication no\n'
  setup "$work/id2.pub" >"$work/localport" || { cat "$work/localport"; fail "the run with Match LocalPort failed"; }
  pass "a Match LocalPort elsewhere does not stop the script"

  # A failure after sshd's files are written, or after sshd reloaded them: the files come back
  # byte for byte, with their modes, and what sshd does is as it was.
  in_host sh -c 'chmod 600 /etc/ssh/sshd_config.d/00-octopushy-snippets.conf; echo >> /etc/ssh/octopushy-snippets/authorized_keys; mkdir -p /run/fault'
  sshd_files() {
    in_host sh -c 'cd /etc/ssh; stat -c "%n %a %U %G %s" sshd_config.d/00-octopushy-snippets.conf octopushy-snippets/authorized_keys
      sha256sum sshd_config.d/00-octopushy-snippets.conf octopushy-snippets/authorized_keys
      for u in root octopushy-snippets; do sshd -T -C user=$u,host=x,addr=192.0.2.1,laddr=127.0.0.1,lport=22,rdomain=default; done'
  }
  # In --remove the drop-in is the last thing to go, so a failure there puts back only the drop-in:
  # the account is already locked or gone, with no key, which is as safe as it gets.
  sshd_dropin() {
    in_host sh -c 'cd /etc/ssh; stat -c "%n %a %U %G %s" sshd_config.d/00-octopushy-snippets.conf; sha256sum sshd_config.d/00-octopushy-snippets.conf
      sshd -T -C user=root,host=x,addr=192.0.2.1,laddr=127.0.0.1,lport=22,rdomain=default'
  }
  sshd_files >"$work/files-before"
  sshd_dropin >"$work/dropin-before"
  for fault in sshd-t reload; do
    for mode in setup remove; do
      # sshd -t's second call is the one after the write; its first is the check before any change.
      if [[ $fault == reload ]]; then in_host sh -c 'rm -f /run/fault/*; touch /run/fault/reload'; else in_host sh -c 'rm -f /run/fault/*; echo 2 > /run/fault/sshd-t'; fi
      if [[ $mode == setup ]]; then
        setup "$work/id.pub" >"$work/faulted" 2>&1 && { cat "$work/faulted"; fail "setup passed with the fault '$fault'"; }
      else
        in_host bash /root/snippets.sh --remove >"$work/faulted" 2>&1 && { cat "$work/faulted"; fail "--remove passed with the fault '$fault'"; }
      fi
      in_host rm -f /run/fault/sshd-t /run/fault/sshd-t-count /run/fault/reload
      if [[ $mode == setup ]]; then
        sshd_files >"$work/files-after"
        diff "$work/files-before" "$work/files-after" >/dev/null || fail "$mode with the fault '$fault' left sshd's files changed: $(diff "$work/files-before" "$work/files-after" | head -8)"
      else
        sshd_dropin >"$work/dropin-after"
        diff "$work/dropin-before" "$work/dropin-after" >/dev/null || fail "$mode with the fault '$fault' did not put the drop-in back: $(diff "$work/dropin-before" "$work/dropin-after" | head -8)"
      fi
      grep -q 'put back as it was' "$work/faulted" || { cat "$work/faulted"; fail "$mode with the fault '$fault' did not say it put sshd back"; }
      if [[ $mode == setup ]]; then
        sftp_ok "$work/id2" || fail "after $mode with the fault '$fault', the account's key no longer logs in"
        sftp_ok "$work/id" && fail "after $mode with the fault '$fault', the new key logs in"
      else
        sftp_ok "$work/id2" && fail "after $mode with the fault '$fault', the account still logs in"
        # Set up again, as an operator would, and put back the bytes and modes the next case compares.
        setup "$work/id2.pub" >/dev/null || fail "setup after $mode with the fault '$fault' failed"
        in_host sh -c 'echo >> /etc/ssh/octopushy-snippets/authorized_keys'
        sshd_files | diff -q "$work/files-before" - >/dev/null || fail "setup after $mode with the fault '$fault' did not get back to where it was"
      fi
    done
  done
  in_host sh -c 'chmod 644 /etc/ssh/sshd_config.d/00-octopushy-snippets.conf'
  pass "a failure after the write or after the reload, in setup or --remove, puts sshd's files back byte for byte, modes included"

  # Refused before any change: a link at any path the script writes or backs up, so a copy is
  # always of the file itself. The link's target is left as it was.
  in_host sh -c 'cp -a /etc/ssh/octopushy-snippets/authorized_keys /root/keys-target
    ln -sf /root/keys-target /etc/ssh/octopushy-snippets/authorized_keys'
  refused "is not a regular file" "a linked authorized_keys" setup "$work/id.pub"
  refused "is not a regular file" "--remove with a linked authorized_keys" in_host bash /root/snippets.sh --remove
  in_host sh -c 'rm /etc/ssh/octopushy-snippets/authorized_keys; mv /root/keys-target /etc/ssh/octopushy-snippets/authorized_keys'
  in_host sh -c 'mv /etc/ssh/sshd_config.d/00-octopushy-snippets.conf /root/conf-target
    ln -s /root/conf-target /etc/ssh/sshd_config.d/00-octopushy-snippets.conf'
  refused "is not a regular file" "a linked drop-in" setup "$work/id.pub"
  in_host sh -c 'rm /etc/ssh/sshd_config.d/00-octopushy-snippets.conf; mv /root/conf-target /etc/ssh/sshd_config.d/00-octopushy-snippets.conf'
  in_host sh -c 'mv /etc/ssh/octopushy-snippets /root/keys-dir-target; ln -s /root/keys-dir-target /etc/ssh/octopushy-snippets'
  refused "is not a directory" "a linked key directory" setup "$work/id.pub"
  in_host sh -c 'rm /etc/ssh/octopushy-snippets; mv /root/keys-dir-target /etc/ssh/octopushy-snippets'
  sftp_ok "$work/id2" || fail "the account no longer works after the refused runs"
  pass "a link at the drop-in, the key file or its directory is refused before any change, setup and --remove alike"

  # The reviewer's case: the host trusts a CA for everyone and serves internal-sftp, and --remove
  # cannot finish. Whatever stopped it, the account must not get in.
  in_host sh -c 'cp -a /etc/ssh/sshd_config /root/sshd_config.before-ca
    sed -i "s|^Subsystem.*|Subsystem sftp internal-sftp|" /etc/ssh/sshd_config
    echo "TrustedUserCAKeys /etc/ssh/user_ca.pub" >> /etc/ssh/sshd_config; systemctl reload ssh'
  cert_refused() {
    printf 'get /etc/passwd %s/leaked\n' "$work" | sftp -i "$work/certified" -o CertificateFile="$work/certified-cert.pub" "${opts[@]}" -P "$port" \
      -b - octopushy-snippets@127.0.0.1 >/dev/null 2>&1 && fail "$1: a certificate from the host's CA logged in as the account"
    [[ ! -e $work/leaked ]] || fail "$1: the account read /etc/passwd"
  }
  cert_refused "with the block in place"
  # A process working in the chroot, as root: the mount could not be taken away, so --remove refuses.
  podman exec -d "$name" sh -c 'cd /var/lib/octopushy-snippets/var/lib/vz/snippets && exec sleep 300'
  timeout 10 sh -c "until podman exec $name sh -c 'ls -l /proc/*/cwd 2>/dev/null | grep -q octopushy-snippets/var'; do sleep 1; done"
  refused "processes use /var/lib/octopushy-snippets" "--remove with the chroot busy" in_host bash /root/snippets.sh --remove
  in_host pkill -f 'sleep 300'
  cert_refused "after --remove refused a busy chroot"
  # A step that fails part way, after the account is locked and its key gone: the block stays.
  in_host sh -c 'mkdir -p /run/fault; touch /run/fault/userdel'
  if in_host bash /root/snippets.sh --remove >"$work/partial" 2>&1; then cat "$work/partial"; fail "--remove passed with userdel failing"; fi
  in_host rm -f /run/fault/userdel
  cert_refused "after --remove failed part way"
  sftp_ok "$work/id2" && fail "the account's own key still logs in after --remove failed part way"
  effective=$(in_host sshd -T -C user=octopushy-snippets,host=x,addr=192.0.2.1,laddr=127.0.0.1,lport=22,rdomain=default)
  grep -qx 'chrootdirectory /var/lib/octopushy-snippets' <<<"$effective" && grep -qx 'trustedusercakeys none' <<<"$effective" \
    || fail "after --remove failed part way, the block is no longer in effect"
  in_host test -f /etc/ssh/sshd_config.d/00-octopushy-snippets.conf || fail "after --remove failed part way, the drop-in is gone"
  # Setting up again carries on from there: the expiry is cleared and the key is back.
  setup "$work/id2.pub" >"$work/again" || { cat "$work/again"; fail "setup after a partial --remove failed"; }
  grep -q 'cleared the expiry' "$work/again" || { cat "$work/again"; fail "setup did not clear the expiry --remove left"; }
  sftp_ok "$work/id2" || fail "the account does not work again after setup"
  cert_refused "after setting up again"
  in_host sh -c 'cp -a /root/sshd_config.before-ca /etc/ssh/sshd_config; systemctl reload ssh'
  pass "with a host-wide CA, --remove refuses a busy chroot, and one that fails part way leaves the block, so nothing gets in"

  # Refused while something --remove cannot undo past sshd: a stray file in the chroot, a session.
  in_host touch /var/lib/octopushy-snippets/var/stray
  refused "holds more than this script made" "--remove with a stray file in the chroot" in_host bash /root/snippets.sh --remove
  in_host rm /var/lib/octopushy-snippets/var/stray
  # A session that ends by itself: sftp reads its commands from a pipe that closes after 15 seconds.
  { sleep 15 | sftp -i "$work/id2" "${opts[@]}" -P "$port" octopushy-snippets@127.0.0.1 >/dev/null 2>&1; } &
  session=$!
  timeout 10 sh -c "until podman exec $name pgrep -u octopushy-snippets >/dev/null; do sleep 1; done" || fail "the SFTP session did not start"
  refused "processes of octopushy-snippets are running" "--remove with an open SFTP session" in_host bash /root/snippets.sh --remove
  wait "$session" || true
  timeout 30 sh -c "while podman exec $name pgrep -u octopushy-snippets >/dev/null; do sleep 1; done" || fail "the SFTP session did not end"
  sftp_ok "$work/id2" || fail "the account no longer works after the refused --remove"
  pass "--remove is refused, changing nothing, while the chroot holds a stray file or the account has a session"

  # Refused with sshd broken by something else: --remove changes nothing, and the account still works.
  dropin 50-broken.conf 'NotADirective yes\n'
  refused "refuses the configuration as it is" "--remove with a broken drop-in" in_host bash /root/snippets.sh --remove
  in_host rm /etc/ssh/sshd_config.d/50-broken.conf
  sftp_ok "$work/id2" || fail "the account no longer works after the refused --remove"
  pass "with a drop-in sshd -t refuses, --remove changes nothing, and the account keeps working"

  in_host bash /root/snippets.sh --remove >"$work/remove" || { cat "$work/remove"; fail "--remove failed"; }
  sftp_ok "$work/id2" && fail "the account still logs in after --remove"
  # The removal above ran with the Match LocalPort drop-in in place; it and the CA are the test's.
  in_host rm /etc/ssh/sshd_config.d/50-localport.conf /etc/ssh/user_ca.pub
  snapshot >"$work/host-after"
  # What --remove keeps on purpose: the datastore's snippets content and its snippets directory.
  kept() { grep -v '^/var/lib/vz/snippets ' "$1" | sed 's/,"snippets"\{0,1\}//; s/,snippets"/"/'; }
  diff <(kept "$work/host-before") <(kept "$work/host-after") >/dev/null \
    || fail "--remove left something: $(diff <(kept "$work/host-before") <(kept "$work/host-after") | head -20)"
  [[ $(in_host cat /var/lib/vz/snippets/guest-user-data.yaml) == "hello: secret" ]] || fail "--remove deleted a snippet"
  [[ $(in_host stat -c '%U %G %a' /var/lib/vz/snippets/guest-user-data.yaml) == "root root 600" ]] || fail "a kept snippet is not root's, mode 600"
  [[ $(in_host stat -c '%U %G %a' /var/lib/vz/snippets) == "root root 755" ]] || fail "the snippets directory is not root's, mode 755"
  in_host useradd --system --no-create-home --shell /usr/sbin/nologin unrelated
  in_host su -s /bin/sh unrelated -c 'cat /var/lib/vz/snippets/guest-user-data.yaml' >/dev/null 2>&1 && fail "an account made afterwards reads a kept snippet"
  in_host userdel unrelated
  pass "after --remove, the kept snippets are root's, mode 600, and an account made afterwards cannot read them"
  in_host bash /root/snippets.sh --remove | grep -q '0 change(s)' || fail "a second --remove changed something"
  pass "--remove undoes the account, chroot, mount and drop-in, keeps the snippets, and changes nothing a second time"

  podman rm -f "$name" >/dev/null
}

for debian in "${@:-bookworm trixie}"; do
  for d in $debian; do run_on "$d"; done
done
log "all passed"
