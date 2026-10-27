# Fixed program compiled into the control worker. Arguments are public release
# metadata, never shell source, credentials, certificates with private keys or
# repository commands. All command output other than the final evidence is
# suppressed by the calling executor.
set -euo pipefail
umask 077
action=$1 url=$2 digest=$3 target=$4 generation=$5 agent=$6 ca64=$7
export PATH=/run/current-system/sw/bin
export LC_ALL=C
test "$(id -u)" = 0
test "$action" = update-os || test "$action" = verify-os
for tool in nix-instantiate nix-store nix-env nix-prefetch-url xz sha256sum findmnt systemctl readlink; do
  command -v "$tool" >/dev/null
done
identity=/var/lib/octopushy/identity
test "$(findmnt -n -o TARGET --target /var/lib/octopushy)" = /var/lib/octopushy
test "$(findmnt -n -o FSTYPE --target /var/lib/octopushy)" = ext4
test "$(nix-instantiate --eval --strict --json --expr \
  "let p = builtins.fromJSON (builtins.readFile $identity/managed-identity.json);
       a = builtins.fromJSON (builtins.readFile $identity/agent.json);
   in p.generation == \"$generation\" && a.agentId == \"$agent\"")" = true
base=/var/lib/octopushy-os-update
test ! -L "$base"
install -d -m 0700 -o root -g root "$base"
work=$base/$digest
test ! -L "$work"
if test "$action" = update-os; then
  install -d -m 0700 -o root -g root "$work"
fi
test -d "$work"
state_uuid() { findmnt -n -o UUID --target /var/lib/octopushy; }
check_identity() {
  test "$(state_uuid)" = "$(cat "$work/state-uuid")" || return 1
  for f in agent.json managed-identity.json key.pem cert.pem ca.pem; do
    test -f "$identity/$f" && test ! -L "$identity/$f" || return 1
  done
  test "$(cd "$identity"; stat -c '%n %u %g %a %h' agent.json managed-identity.json key.pem cert.pem ca.pem)" \
    = "$(cat "$work/identity.metadata")" || return 1
  (cd "$identity"; sha256sum --status --check "$work/identity.sha256")
}
check_running() {
  test "$(readlink -f /run/current-system)" = "$target" || return 1
  test -x "$target/sw/bin/octopushy-worker-reporting" || return 1
  systemctl is-active --quiet octopushy-worker.service || return 1
  pid="$(systemctl show --property MainPID --value octopushy-worker.service)" || return 1
  test "$pid" -gt 1 || return 1
  test "$(readlink -f "/proc/$pid/exe")" = "$(readlink -f "$target/sw/bin/octopushy")" || return 1
  check_identity
}
if test "$action" = update-os; then
  # A previous interrupted activation is never silently replayed. Even an
  # exact retry requires the original byte-for-byte preservation evidence.
  if test -e "$work/in-progress" && ! test -e "$work/complete"; then exit 76; fi
  if ! test -e "$work/identity.sha256"; then
    test ! -e "$work/in-progress"
    state_uuid > "$work/state-uuid"
    test -s "$work/state-uuid"
    (cd "$identity"
      for f in agent.json managed-identity.json key.pem cert.pem ca.pem; do
        test -f "$f" && test ! -L "$f"
      done
      sha256sum agent.json managed-identity.json key.pem cert.pem ca.pem
    ) > "$work/identity.sha256.tmp"
    (cd "$identity"; stat -c '%n %u %g %a %h' agent.json managed-identity.json key.pem cert.pem ca.pem) > "$work/identity.metadata"
    mv "$work/identity.sha256.tmp" "$work/identity.sha256"
    readlink -f /nix/var/nix/profiles/system > "$work/previous-system"
  fi
  test -s "$work/previous-system"
  check_identity
  if ! test -e "$work/complete"; then
    # This exact closure archive is approved separately from the OpenTofu plan.
    # No daemon config, signatures policy or substituter settings are changed.
    if test -n "$ca64"; then
      printf '%s' "$ca64" | base64 --decode > "$work/download-ca.pem"
      export NIX_SSL_CERT_FILE="$work/download-ca.pem"
    fi
    archive="$(nix-prefetch-url --print-path --type sha256 "$url" "$digest" | tail -n 1)"
    printf '%s  %s\n' "$digest" "$archive" | sha256sum --status --check
    xz -dc "$archive" | nix-store --option require-sigs false --import >/dev/null
    nix-store --verify-path "$target" >/dev/null
    test -x "$target/bin/switch-to-configuration"
    test -x "$target/sw/bin/octopushy-worker-reporting"
    touch "$work/in-progress"
    sync -f "$work"
    rollback() {
      if test -e "$work/complete"; then return; fi
      previous="$(cat "$work/previous-system")"
      nix-env --profile /nix/var/nix/profiles/system --set "$previous" >/dev/null 2>&1 || true
      "$previous/bin/switch-to-configuration" switch >/dev/null 2>&1 || true
    }
    trap rollback EXIT
    nix-env --profile /nix/var/nix/profiles/system --set "$target" >/dev/null
    "$target/bin/switch-to-configuration" switch >/dev/null
  fi
  # Also restart on exact completed retries. The target derivation can be
  # unchanged, but each update run requires its own fresh authenticated Hello.
  systemctl restart octopushy-worker.service
  for i in $(seq 1 30); do
    if check_running; then break; fi
    sleep 1
  done
  check_running
  touch "$work/complete"
  sync -f "$work"
  trap - EXIT
fi
check_running
printf '{"protocolVersion":1,"generation":"%s","systemPath":"%s","archiveSha256":"%s","identityPreserved":true,"stateFilesystemPreserved":true}\n' \
  "$generation" "$target" "$digest"
