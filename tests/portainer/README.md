# NixOS + Docker + Portainer live end-to-end test

This test provisions a **new** VM through the existing Octopushy controller and
agent, then uses native NixOS closures to configure its Docker service role.
It deliberately does not claim that service orchestration is in the controller.
No existing VM, DNS configuration, or router is adopted or modified.

## Boundaries tested

- NixOS module evaluation: disabled defaults, baseline requirements, scoped
  firewall behavior, immutable Docker image, runtime credentials, and limits.
- Octopushy API: a saved two-resource create plan, approval, apply, verification.
- Portainer HTTPS API: initialized admin login, local Docker environment, and
  creation/start/deletion of a bounded, disposable Docker workload.
- Guest/network behavior: closed bootstrap; rejected non-allowed source; direct
  LAN HTTPS; no HTTP/Edge exposure; sibling workload surviving a Portainer stop;
  data surviving role removal/restoration and a real VM reboot; same-generation
  activation not recreating the container.
- A final controller-owned OpenTofu plan must report no VM changes after the
  native service workflow. Unexpected changes are never approved by that step.

## Prerequisites and preparation

The existing trusted-LAN Proxmox installation policy disables Proxmox TLS
verification. SSH keys are fetched through the owned VM's guest-agent management
channel, then pinned strictly. This does not turn that Proxmox connection into
verified TLS. Select proper Proxmox CA trust before production.

Use a locked deployment flake exporting:

- `packages.x86_64-linux.image`, based on the reusable bootstrap image plus
  service-node base, basename `octopushy-nixos-service-node`.
- `packages.x86_64-linux.qemu`.
- `nixosConfigurations.service`, consuming a non-secret `guest.json` with
  `name`, `vmid`, `mac`, observed `ip`, `sshKey`, `enable`, `expose`, and
  `allowedNetworks`. Use the supplied MAC to keep `ens18` stable after reboot.

The private test configuration is under the configuration repository's
`deployments/portainer/nixos`. No production definitions are activated by merely
having this directory.

Build in a resource-bounded rootless Nix runner:

```sh
bash tests/portainer/build.sh /absolute/path/to/locked-flake .dev/portainer-e2e/artifacts
```

The image build needs accessible `/dev/kvm`; it never changes device permissions.
Its explicit capabilities are inside the rootless namespace, for the image
builder's virtiofsd, not host root privileges.

Publish the single checked artifact temporarily, allowing only Proxmox:

```sh
python3 tests/portainer/serve-image.py \
  .dev/portainer-e2e/artifacts/octopushy-nixos-service-node.qcow2 \
  --bind 10.1.0.230 --allow 10.1.0.85
```

Prepare a `nixos-service-node/template.yaml` with that exact URL and SHA-256.
The source is HTTP only on the trusted test LAN; the provider checks the reviewed
checksum. Stop the server after import. It is not a permanent artifact service.

## Run and retain for inspection

```sh
PORTAINER_WORKFLOW=1 KEEP_GUEST=1 \
  PORTAINER_TEMPLATE_DIR=/absolute/path/to/nixos-service-node \
  PORTAINER_CONFIG_DIR=/absolute/path/to/locked-flake \
  bash tests/proxmox/run.sh
```

Defaults: `opy-service-01` on `umbrel`, free test VMID, **1 vCPU, 1 GiB RAM,
16 GiB disk**, ordinary DHCP. Unlike the VM lifecycle test, this profile skips
the resize. `KEEP_GUEST=1` leaves the VM and its data running, but stops the
temporary controller/agent processes. No existing preview is restarted.

This profile has separate databases (`portainer_e2e`, `portainer_e2e_state`),
API/agent ports (`18081`, `18444`), and state/evidence directory
`.dev/portainer-vm-e2e`. Existing databases are never silently reset on a new run.
Retain the PostgreSQL state **and** controller state directory; do not adopt
this VM into another controller or a native OpenTofu state.

Access information is written into `.dev/portainer-vm-e2e/portainer/`:

- `url`: direct HTTPS address.
- `admin-password`: generated private `0600` file; username `admin`.
- `portainer-cert.pem`: public server certificate obtained over pinned SSH.
- `verification.json`, per-stage build/activation logs.

Bootstrap verification temporarily uses a loopback SSH tunnel, closed on exit.
The final access path is direct LAN TCP 9443. Test workloads are removed.

On failure, preserve the VM/state and inspect logs and actual outcomes before
retrying. Do not remove `.dev`, reset databases, reinitialize Portainer, or
blindly apply again. The script does not destroy a guest automatically on failure.

After inspecting and resolving a service-stage failure, rerun the test against
the **same** VM and controller/state databases, without provisioning or resizing:

```sh
PORTAINER_WORKFLOW=1 RESUME_SERVICE=1 KEEP_GUEST=1 \
  PORTAINER_CONFIG_DIR=/absolute/path/to/locked-flake \
  bash tests/proxmox/run.sh
```

The test waits for cloud-init to finish before establishing SSH trust. An older
bootstrap image may leave sshd serving an old in-memory key after cloud-init
regenerates the on-disk key: on the verified new test VM only, the test restarts
sshd through the management channel, then pins the management-retrieved key.
The reusable image now orders sshd after cloud-final to prevent that race.
The first installed-generation reboot happens with Portainer closed, so stable
MAC-matched interface naming is established **before** testing firewall exposure.
Do not bypass host-key checking or relax the firewall to work around either issue.

Retire only when explicitly authorized:

```sh
PORTAINER_WORKFLOW=1 CLEANUP=1 bash tests/proxmox/run.sh
```

This uses the same controller/state owner and reviewed destroy plan. It deletes
the test VM, its disks (including Portainer data), and its owned image import.

## Local evaluation

With Nix and the selected nixpkgs source already available:

```sh
nix-instantiate --eval --strict --json tests/portainer/eval.nix \
  --arg nixpkgs /absolute/path/to/nixpkgs
```

Evaluation is not a substitute for the live packet paths, runtime API, reboot,
or data-persistence checks above. Neither is a production backup/restore test.
