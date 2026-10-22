#!/usr/bin/env bash
# Called only for the fresh VM created by tests/proxmox/run.sh.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(realpath "${1:?Pass the disposable Proxmox test work directory}")
workflow=$root/examples/lab/modules/ansible/fedora
ansible=${ANSIBLE_PLAYBOOK:-$root/.dev/ansible/bin/ansible-playbook}
[[ -x $ansible ]] || { echo "Install $workflow/requirements.txt into .dev/ansible first." >&2; exit 1; }
umask 077
python3 "$root/tests/fedora/prepare.py" "$work"
cd "$workflow"
export ANSIBLE_CONFIG=$workflow/ansible.cfg
run() {
  local operation=$1 configuration=$2
  shift 2
  printf '\n=== Fedora %s (%s)\n' "$operation" "$configuration"
  "$ansible" -i "$work/fedora/inventory.ini" workflow.yaml --limit test-vm \
    -e "operation=$operation" -e "@$work/fedora/$configuration.json" "$@"
}
run setup app-a -e allow_bootstrap=true | tee "$work/fedora/setup.log"
run setup app-a -e allow_bootstrap=true | tee "$work/fedora/setup-again.log"
for service in app-a app-b; do
  run expose "$service"
  run deploy "$service"
  run verify "$service"
done
printf '\n=== Reboot the disposable guest to verify runtime, service, and firewall persistence\n'
"$(dirname "$ansible")/ansible" -i "$work/fedora/inventory.ini" test-vm \
  --become -m ansible.builtin.reboot -a reboot_timeout=180
run verify app-a
run verify app-b
run expose app-a | tee "$work/fedora/expose-again.log"
run deploy app-a | tee "$work/fedora/deploy-again.log"
run expose app-a-denied
if curl --noproxy '*' -fsS --max-time 5 "http://$(<"$work/fedora/address"):8080/" >/dev/null 2>&1; then
  echo "The source restriction did not block a new connection from the execution host." >&2
  exit 1
else
  denied_result=$?
  [[ $denied_result == 28 ]] || { echo "Expected a firewall timeout, got curl exit $denied_result." >&2; exit 1; }
fi
run verify app-b
run expose app-a
run verify app-a
if run verify app-a-bad-check -e verify_retries=1 -e verify_delay=1 >"$work/fedora/expected-failure.log" 2>&1; then
  echo "The deliberately incorrect service verification unexpectedly passed." >&2
  exit 1
fi
rg -q '"status": *200' "$work/fedora/expected-failure.log" ||
  { echo "The expected failure was not an HTTP content verification failure." >&2; exit 1; }
run verify app-b
run deploy app-a-v2
run verify app-a-v2
run verify app-b
run remove app-a-v2
run remove app-a-v2
run verify app-b
if curl --noproxy '*' -fsS --max-time 5 "http://$(<"$work/fedora/address"):8080/" >/dev/null 2>&1; then
  echo "app-a is still reachable after removal." >&2
  exit 1
fi
run remove app-b
if curl --noproxy '*' -fsS --max-time 5 "http://$(<"$work/fedora/address"):8081/" >/dev/null 2>&1; then
  echo "app-b is still reachable after removal." >&2
  exit 1
fi
for receipt in setup-again expose-again deploy-again; do
  if ! rg -q 'changed=0[[:space:]]' "$work/fedora/$receipt.log"; then
    echo "$receipt was not idempotent; inspect $work/fedora/$receipt.log." >&2
    exit 1
  fi
done
echo "Passed: reusable setup, two services, owned ports, reboot persistence, no-op reruns, source restrictions, isolated verification failure, update, and independent removal."
