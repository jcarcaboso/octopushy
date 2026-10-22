# Reusable Fedora workflow, first accepted operations

**Native workflow, not controller integration.** These playbooks can be executed
explicitly with Ansible. Octopushy's server/agent currently schedule only
Proxmox/OpenTofu work: they do not discover these files, approve their execution,
schedule services, or store these operations' outcomes. Do not add a provision
recipe claiming `executor: ansible`; that executor does not exist yet.

The purpose is to verify reusable guest operations before integrating them as
independent runs, rather than embedding service deployment in VM provisioning.

## Accepted scope

- A **new, dedicated Fedora guest**, with working SSH, passwordless sudo, pinned
  SSH host trust, and a network path from the execution host.
- Rootless Docker and Compose under a dedicated `octopushy` user. The rootful
  Docker socket/service stays disabled. No user is added to the `docker` group.
- One stateless container per Compose project, using an immutable image digest.
- One explicit LAN IPv4/TCP binding per service, on a port from 1024 to 65535.
- Explicit allowed IPv4 source networks. No `0.0.0.0/0`, IPv6 publication,
  privileged port changes, router/NAT changes, or DNS/proxy integration.
- The guest's default IPv4 interface and its actual firewalld zone.
- One operation per guest at a time. Other guests can run independently.

No shared volumes, secret delivery, multi-container Compose stacks, generic
script execution, OS upgrades, or NixOS management are supported in this slice.
`setup` installs missing packages; it does not perform system maintenance or
upgrade packages on every service deployment.
Docker RPM versions are not frozen in this slice: initial bootstrap uses the
vendor's signed stable repository. Freeze package versions before a wider rollout;
the immutable workload image and pinned Ansible Core are separate inputs.

## Operations

| Operation | Responsibility |
|---|---|
| `setup` | Explicit opt-in bootstrap: signed Docker packages, dedicated user, lingering user manager, rootless daemon, firewalld, exposure helper |
| `expose` | Reconcile only the service's owned runtime and permanent firewall rules |
| `deploy` | Require exact reconciled exposure, then apply only that service's Compose project |
| `verify` | Require HTTP 200 and expected content, checked from the execution host outside the guest |
| `remove` | Stop only that Compose project, then remove only its owned exposure; never delete data volumes |

The first deployment is `setup → expose → deploy → verify`. A normal workload
update is `deploy → verify`, without rerunning setup or changing sibling services.
An allowed-source change requires `expose` first. A binding address, port, or zone
change currently requires explicit `remove`, then `expose → deploy → verify`.
This intentionally does not promise a zero-downtime update or rollback.

Failure is visible at the invoked operation. There is no automatic rollback or
retry after an unknown remote outcome. Ansible check mode is refused: it is not
an exact saved-plan preview or an approval mechanism for these operations.

## Running

On the execution host, create an isolated Ansible environment:

```sh
python3 -m venv .dev/ansible
.dev/ansible/bin/python -m pip install -r modules/ansible/fedora/requirements.txt
. .dev/ansible/bin/activate
```

The path above assumes this module lives in a lab repository. In the application
checkout its path is `examples/lab/modules/ansible/fedora/`.

Copy `examples/inventory.ini` to your execution host and replace its documentation
address. Keep native service inputs based on `examples/http.yaml` in your private
configuration repository or in a local test file; these are not controller-watched
`service.yaml` definitions yet. Supply your own SSH private-key path and a
`known_hosts` file whose public host key was verified through a trusted channel.
Never disable SSH host-key checking or treat `ssh-keyscan` as authentication.

From this module's directory:

```sh
export ANSIBLE_CONFIG="$PWD/ansible.cfg"
ansible-playbook -i /private/inventory.ini workflow.yaml --limit service-vm \
  -e @/private/http.yaml -e operation=setup -e allow_bootstrap=true
ansible-playbook -i /private/inventory.ini workflow.yaml --limit service-vm \
  -e @/private/http.yaml -e operation=expose
ansible-playbook -i /private/inventory.ini workflow.yaml --limit service-vm \
  -e @/private/http.yaml -e operation=deploy
ansible-playbook -i /private/inventory.ini workflow.yaml --limit service-vm \
  -e @/private/http.yaml -e operation=verify
```

Run `remove` explicitly to retire the service. The `service.id` is a stable
identity, used for the Compose project and exposure ownership, not a display name.
Reuse the same inputs and identity when retrying or removing a partial deployment.

## Ownership, recovery, and trust

- The guest runtime has an ownership marker at `/etc/octopushy-fedora-workflow`.
  Setup refuses to adopt an unrelated existing user or rootful Docker socket.
  This is not an importer or a migration path for Jenkins-managed guests.
- Compose projects are named `opy-<service.id>` and stored below
  `/var/lib/octopushy/runtime/services/`. They are separate deployments.
- The exposure helper records ownership and pending intent before mutation under
  `/var/lib/octopushy/exposure/`. It refuses a binding owned by another service.
  It also refuses to adopt matching firewall rules without an ownership record.
  Partial application can be reconciled explicitly; deployment refuses pending,
  changed, or drifted exposure.
- The helper installs a default deny for the binding and higher-priority accepts
  for the declared sources. It reconciles runtime and permanent rules without a
  global firewall reload, never flushing another service's rules.
  Source restrictions apply to new connections; this does not flush established
  connection tracking to terminate already accepted connections.
- Rootless publication is intentional: this workflow does not implement
  rootful Docker's forwarding-chain firewall policy.
- Every operation holds `/run/lock/octopushy-fedora-workflow`. Normal failures
  release it. A killed controller or unreachable guest can leave a stale lock:
  establish the outcome and make sure no prior operation is still running before
  an administrator removes that directory. Never automatically delete it to retry.
- This is trusted-code execution under one administrator. Separate Compose
  projects and outcome boundaries do not sandbox mutually hostile workloads.

## Checks

From the application checkout:

```sh
python3 -m unittest discover -s tests/fedora -p 'test_*.py' -v
ANSIBLE_CONFIG="$PWD/examples/lab/modules/ansible/fedora/ansible.cfg" \
  .dev/ansible/bin/ansible-playbook \
  -i examples/lab/modules/ansible/fedora/examples/inventory.ini \
  examples/lab/modules/ansible/fedora/workflow.yaml --syntax-check
FEDORA_WORKFLOW=1 TEST_SSH_KNOWN_HOSTS=/private/verified-known-hosts tests/proxmox/run.sh
```

The last command is a **real infrastructure test**: it provisions/resizes one
disposable VM through Octopushy, runs these native operations, then retires the
VM through Octopushy. It does not prove controller/agent service integration.
Fedora's SELinux policy prevents the guest agent from reading the SSH host key;
the test does not disable or weaken SELinux. The default requires an independently
verified `TEST_SSH_KNOWN_HOSTS` file. For an explicitly disposable trusted-LAN test
only, `ALLOW_TEST_SSH_TOFU=1` captures the first key over the network and pins it
for all subsequent connections. **That first key is not authenticated.** This
flag is confined to the test harness, not the reusable workflow. The Proxmox
connection also retains the provisioning test's explicit, unverified-TLS LAN
assumption. Neither shortcut validates production host enrollment.
