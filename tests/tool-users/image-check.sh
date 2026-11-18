#!/bin/sh
# Runs in the agent image as root, with the capabilities compose.yaml grants, from run.sh. With the
# folders locked as the agent locks them at start, checks that a tool user can write to none of
# them and that the real tools still work as tool users: a playbook on this machine, which uses
# Python's multiprocessing and so /dev/shm; OpenTofu's init and plan; a Nix build, as the store's
# owner, offline.
set -eu

fail() { echo "FAIL: $*" >&2; exit 1; }

# What the agent does at start (UserPool::lock_down).
for d in /tmp /var/tmp /run/lock /dev/mqueue; do
  if [ -d "$d" ]; then chown 0:0 "$d" && chmod 0755 "$d"; fi
done
chown 0:0 /dev/shm && chmod 1777 /dev/shm

tool=10100
nix=10001
state=/var/lib/octopushy-agent
install -d -m 0711 "$state" "$state/work"
# A workspace as the agent hands it over: root's, the user's group; what is in it, the user's.
workspace() {
  ws=$state/work/$1
  install -d -m 0750 -o 0 -g "$2" "$ws"
  install -d -m 0700 -o "$2" -g "$2" "$ws/tmp" "$ws/home" "$ws/repo"
}
as_user() {
  uid=$1
  shift
  setpriv --reuid="$uid" --regid="$uid" --clear-groups --no-new-privs \
    env -i PATH="$PATH" HOME="$ws/home" TMPDIR="$ws/tmp" TMP="$ws/tmp" TEMP="$ws/tmp" "$@"
}

workspace ansible "$tool"
for d in /tmp /var/tmp /run/lock /dev/mqueue; do
  [ -d "$d" ] || continue
  if as_user "$tool" sh -c "touch $d/planted || mkdir $d/planted-dir" 2>/dev/null; then
    fail "a tool user wrote to $d"
  fi
done
echo "ok   no tool user writes to /tmp, /var/tmp, /run/lock or /dev/mqueue"

# The agent's private folder for the attempt's secrets, in memory.
install -d -m 0711 /tmp/octopushy-secrets
install -d -m 0700 -o "$tool" -g "$tool" /tmp/octopushy-secrets/check
cat >"$ws/repo/play.yml" <<'EOF'
- hosts: localhost
  gather_facts: true
  tasks:
    - ansible.builtin.command: echo hi
    - ansible.builtin.copy:
        content: x
        dest: "{{ lookup('env', 'TMPDIR') }}/copied"
EOF
chown "$tool:$tool" "$ws/repo/play.yml"
cd "$ws/repo"
out=$(as_user "$tool" env OCTOPUSHY_SECRET_DIR=/tmp/octopushy-secrets/check OCTOPUSHY_RECAP=1 \
  ANSIBLE_REMOTE_TMP="$ws/tmp" octopushy-ansible -i localhost, -c local play.yml 2>&1) \
  || { echo "$out"; fail "the playbook failed"; }
printf '%s\n' "$out" | grep -q '"unreachable": 0' || { echo "$out"; fail "the playbook did not reach"; }
printf '%s\n' "$out" | grep -q '"failures": 0' || { echo "$out"; fail "a task failed"; }
echo "ok   a playbook on this machine, with its multiprocessing queue, as a tool user"

workspace tofu "$tool"
printf 'output "x" {\n  value = 1\n}\n' >"$ws/repo/main.tf"
chown "$tool:$tool" "$ws/repo/main.tf"
cd "$ws/repo"
as_user "$tool" env TF_IN_AUTOMATION=1 TF_INPUT=0 tofu init -input=false -no-color >/dev/null \
  || fail "tofu init failed"
as_user "$tool" env TF_IN_AUTOMATION=1 TF_INPUT=0 tofu plan -input=false -no-color >/dev/null \
  || fail "tofu plan failed"
echo "ok   tofu init and plan as a tool user"

workspace nix "$nix"
cd "$ws/repo"
# shellcheck disable=SC2016 # $out is the builder's, in the Nix expression
built=$(as_user "$nix" nix build --impure --no-link --print-out-paths --expr \
  'derivation { name = "check"; system = builtins.currentSystem; builder = "/bin/sh"; args = [ "-c" "echo hi > $out" ]; }') \
  || fail "nix build failed"
[ "$(cat "$built")" = hi ] || fail "nix built something else"
echo "ok   a Nix build as the store's owner"
