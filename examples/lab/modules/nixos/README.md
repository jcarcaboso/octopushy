# Native NixOS guest workflow

**Preparation, not controller/agent integration.** Octopushy does not discover,
approve, schedule, or record these files as guest operations. There is no Nix
executor or supported `service.yaml` schema here. Run the native commands
explicitly; VM provisioning remains a separate operation.

This slice supports one authorized, fresh, dedicated **x86_64 NixOS guest**.
It provides a Proxmox-compatible base module and a separate bootstrap image.
It is not a fleet importer, Fedora conversion, remote deployment engine, or
generic deployment framework. A NixOS activation changes the **whole guest**:
adding a service is not an independent per-service activation.

## Inputs and ownership

- `flake.nix` / `flake.lock`: one nixpkgs input, pinned to
  `f3a73bcf5d1c5b75a66cf35a48957e30d41d647a` (NixOS 26.05).
  The real lock metadata was fetched with Nix, not invented. Ordinary commands
  refuse lockfile updates. Updating the pin is a separate reviewed change.
- `nixosModules.proxmox-guest`: QEMU/VirtIO support, guest agent, key-only SSH,
  enabled firewall and nftables (`flushRuleset = false`), and one passwordless-sudo
  administrator. Only SSH is opened by the base. No service is imported.
- `lab.proxmoxGuest.adminUser`: defaults to `ops`; must match the provisioning
  recipe's `admin_user` and the actual SSH account. Configure public keys using
  the ordinary `users.users.<name>.openssh.authorizedKeys` options.
- `nixosModules.proxmox-image`, `nixosConfigurations.bootstrap`, and
  `packages.x86_64-linux.bootstrap-image`: an 8 GiB initial EFI/cloud-init image.
  **Do not import `image.nix` into an installed guest's configuration.**
- `examples/guest.nix`: opt-in installed-guest example. It intentionally refuses
  a build until the administrator supplies public SSH keys. The flake does not
  import it or any optional service example automatically.
- `nixosModules.technitium`: the optional [dedicated DNS role](services/README.md).
  Its closed-bootstrap example is `examples/technitium.nix`; import it explicitly
  alongside a guest's hardware, network, and administrator configuration.
  The selected package is too old for LAN exposure: the role rejects
  `exposeDNS = true` until a separately reviewed package meets the known-fix
  minimum. No patched package is supplied or runtime-tested in this preparation.
- `examples/technitium-cluster.nix`: two closed, independently composed guest
  modules for the [same-host DNS cluster preparation](services/CLUSTER.md).
  This is not an active fleet declaration or a membership/enrollment controller.

The base does **not** own disk layout, hardware detection, addresses, DNS,
`system.stateVersion`, host keys, or runtime service credentials. Keep the
installed guest's hardware configuration, filesystem identities, networking,
and original `system.stateVersion` stable. `26.05` in the example is appropriate
only for a fresh installation with that original baseline, not an upgrade knob.
Cloud-init bootstrap settings do not replace the maintained guest configuration.

## Safety prerequisites

1. Identify exactly one guest, its authorized administrator, and its management
   address. Confirm independent console access and a recovery path before
   activating. This first slice does not change network topology or reimage a
   running guest.
2. Independently verify the guest's SSH public host key over a trusted channel
   and pin it in a dedicated `known_hosts` file. Neither `ssh-keyscan` nor the
   first network connection authenticates a host. An address reported by the
   QEMU agent is discovery, not SSH enrollment.
3. Use your own SSH identity and explicit target alias. The account needs
   passwordless sudo; that is full guest-administrator authority, not sandboxed
   service access. The module does not add it to Nix's trusted-users.
4. Serialize **all full-system changes per guest**, including other operators,
   provisioning, guest maintenance, and optional service changes. The native
   `flock` below serializes cooperating activations, not arbitrary root commands.
   Do not run guest garbage collection between copying and activating a closure.
5. Nix configurations, derivations, closures, and disk images can expose their
   inputs through the store. Never put runtime secrets, private keys, passwords,
   tokens, or secret-bearing environment files in Nix expressions, `extraEnv`,
   `writeText`, image contents, or files referenced by the flake. Deliver secrets
   separately at runtime under the service's documented permissions.
6. Review configuration as executable, root-equivalent code. Build from a
   specific reviewed commit and activate the resulting **exact store path**,
   never an automatically refreshed branch. A successful evaluation or dry
   activation is not an exact saved plan or an approval.

For example, put a dedicated alias in the execution host's `~/.ssh/config`,
**before wildcard entries**. Replace every documentation value:

```sshconfig
Host nixos-guest-reviewed
    HostName 192.0.2.10
    User ops
    Port 22
    IdentityFile /private/guest-administrator
    IdentitiesOnly yes
    BatchMode yes
    StrictHostKeyChecking yes
    UserKnownHostsFile /private/verified-guest-known-hosts
    GlobalKnownHostsFile /dev/null
    UpdateHostKeys no
    ControlMaster no
    ControlPath none
    ForwardAgent no
    ClearAllForwardings yes
```

Inspect `ssh -G nixos-guest-reviewed` and confirm the effective target, user,
identity and host-key policy. All SSH commands below use this one alias; the
native Nix SSH store uses the same config. Do not supply an `NIX_SSHOPTS` string
from guest/service inputs, use accept-new, or disable host-key verification.

## A. Build/bootstrap an image (separate operation)

On a Linux Nix build host with flakes enabled, from this directory:

```sh
nix build --no-update-lock-file --out-link ./result-image \
  'path:.#bootstrap-image'
ls -l ./result-image
sha256sum ./result-image/nixos-proxmox-bootstrap.img
```

The pinned upstream Proxmox module selects a raw disk image when cloud-init is
enabled, not the `vzdump` backup/restore workflow. The module selects EFI/OVMF,
q35 metadata, VirtIO SCSI initrd support, NoCloud metadata, key-only SSH, and the
QEMU agent. No administrator public keys, private keys, fleet host inventory, or
runtime secrets are embedded here. Access must be explicitly supplied during
authorized bootstrap; the image does not provide a default password.
The cloud image uses `image.baseName` plus `.img`; upstream `image.fileName`
describes the different VMA backup artifact and must not be used to locate this
raw disk. VM configuration metadata is not applied by importing the disk alone.

**Current integration gap:** the existing
`examples/lab/modules/opentofu/proxmox-vm` recipe downloads `template.source.url`
with a checksum, then imports the disk as `scsi0` under OVMF/q35. It cannot
consume a local Nix store result. Publishing this raw disk to an authenticated,
immutable artifact location, recording its actual SHA-256 and filename, and
creating a reviewed matching template are separate, unimplemented steps. Do not
pass a store path as a download URL, use a `latest` image, silently replace a
template, or run the backup-restore instructions for this raw artifact.

Before using that recipe, verify the imported format, minimum 8 GiB disk,
EFI/OVMF/q35 settings, cloud-init NoCloud user/network handling, and QEMU-agent
API verification in a disposable test. The recipe's `admin_user` must match
`lab.proxmoxGuest.adminUser` (default `ops`). Verify guest login and independent
host trust before handing the guest to section B. Existing disks deliberately
ignore changed `import_from`; updating an image is **not** a guest update.
Neither this preparation nor a successful image derivation evaluation proves
that first boot or the current provision recipe works end to end.
In particular, validate the intended OVMF Secure Boot policy separately rather
than assuming an unsigned NixOS bootloader will boot under enrolled-key firmware.

## B. Configure one already provisioned guest

Use a separate, access-controlled configuration Git repository for this guest.
Copy these self-contained module/flake files from a reviewed source revision;
do not import the application's whole checkout or another fleet's configuration.
Use `examples/guest.nix` as a starting point, set the real public administrator
key(s), and preserve the guest's existing hardware and network configuration.

For example, the existing generated hardware module can be collected read-only:

```sh
ssh nixos-guest-reviewed 'cat /etc/nixos/hardware-configuration.nix' \
  > /private/nixos-config/hardware-configuration.nix
```

Review the result locally. If that file is absent, obtain a reviewed hardware
module through the installation/recovery procedure; do not guess its root/EFI
devices or rerun an installer here. Also preserve any required configuration
outside that hardware module, especially a cloud-init-assigned static address.
The base alone does not turn seed metadata into maintained network configuration.

In the copied flake's output attribute set, explicitly add:

```nix
nixosConfigurations.guest = nixpkgs.lib.nixosSystem {
  system = "x86_64-linux";
  modules = [
    ./hardware-configuration.nix
    ./examples/guest.nix
    # Add this guest's reviewed network module if not in the hardware module.
    # Optional service modules belong in this same whole-system composition.
  ];
};
```

Keep the supplied nixpkgs input and lock unchanged. Hardware, configuration, and
public-key files needed by the flake must be tracked in the reviewed commit.
No generated installer or fleet host files are imported automatically.

## C. Build the reviewed system (execution host, no activation)

Use Nix 2.34.9 or a separately validated compatible version. Stop on any failed
command. The configuration repository must have a clean worktree and a reviewed
commit; the following local Git flake URL fixes that commit even if a branch
moves later. Keep the output GC root outside the configuration worktree:

```sh
set -euo pipefail
cd /private/nixos-config
test -z "$(git status --porcelain=v1 --untracked-files=all)"
revision="$(git rev-parse --verify 'HEAD^{commit}')"
mkdir -p /private/nixos-build
nix flake check --no-update-lock-file --no-build \
  "git+file://$PWD?rev=$revision"
nix build --no-update-lock-file --out-link /private/nixos-build/reviewed-system \
  "git+file://$PWD?rev=$revision#nixosConfigurations.guest.config.system.build.toplevel"
system="$(readlink -e /private/nixos-build/reviewed-system)"
printf 'Reviewed revision: %s\nSystem closure: %s\n' "$revision" "$system"
```

Retain the revision, lockfile, build log, and exact store path with the target
identity. Inspect the evaluated configuration and built artifacts before
activation. Do not rebuild at the activation step or substitute a moving flake
reference. Builds may fetch the pinned source and dependencies; they do not
contact or mutate Proxmox.

## D. Copy, then explicitly activate that exact closure

From the execution host, using the `system` value from C:

```sh
unset NIX_SSHOPTS
nix copy --to \
  'ssh://nixos-guest-reviewed?remote-program=sudo%20-n%20nix-store' \
  "$system"
```

The SSH store's **fixed literal** remote program uses the already authorized
sudo account to import the built closure. It does not change Nix's trust policy,
disable signature checking, or interpolate an SSH option string. Copying is a
store mutation, **not** activation or proof of guest health. If the transport or
privilege check fails, stop; do not add trusted-users or weaken SSH to work around
it. Keep the execution host's output link until the operation is resolved.

Open a guest shell, paste the exact reviewed store path, and explicitly activate:

```sh
ssh nixos-guest-reviewed
system='/nix/store/PASTE-THE-EXACT-REVIEWED-SYSTEM-PATH'
sudo -n flock --nonblock /run/lock/native-nixos-guest \
  "$system/sw/bin/nixos-rebuild" switch \
  --store-path "$system" --no-reexec
exit
```

Use the copied closure's pinned native `nixos-rebuild`, not an arbitrary guest
wrapper. At this nixpkgs revision `--store-path` skips a build and selects the
provided system; `--no-reexec` prevents rebuilding/reexecuting the tool from a
different source. Native `switch` updates the system profile and runs the
whole-system activation, including its boot configuration. It is not a
transaction across every affected service.

The advisory kernel lock is held while the native command runs. A busy lock is
a failure, not an invitation to wait/retry automatically. A remaining lockfile
does not itself mean the lock is held; never remove it to bypass an operation.
All guest writers must observe the same serialization policy.

## E. Verify independently; stop on uncertain outcomes

Start a **new** SSH connection from the execution host, with the same strict
trust policy, then compare both links with the recorded closure:

```sh
ssh nixos-guest-reviewed
readlink -e /run/current-system
readlink -e /nix/var/nix/profiles/system
systemctl is-active sshd qemu-guest-agent
systemctl --failed --no-pager
sudo -n nft list ruleset
exit
```

Require the expected current system and profile, working new SSH access, active
base units, no unexpected failed units, and the intended firewall. A QEMU-agent
address or successful SSH exit alone does not verify a service. Verify each
enabled service from an independently authorized client using its explicit
endpoint and health criteria. This base does not query DNS or assert Technitium
readiness. A switch is not a reboot test; validate bootability separately during
an authorized maintenance window.

A failed build has not activated the guest. After a copy/activation timeout,
disconnect, nonzero activation, or lost execution-host process, the remote
outcome may be **unknown**, not unchanged. Do not retry, automatically roll back,
delete the lockfile, or assume the old system is still active. Use the trusted
console/SSH recovery path to inspect current, booted, and profile store links,
running activation processes, `systemctl list-jobs`, `systemctl --failed`, and
`journalctl -b`. Establish that the previous operation has ended and reconcile
the actual result before an operator chooses the next explicit action.

There is no broad `down`, automatic rollback, garbage collection, disk deletion,
network teardown, or removal of runtime service data. Removing an optional
service means reviewing another **whole-guest** configuration and activation;
data retention/removal needs its own deliberate procedure.

## Local checks and evidence limits

From the application checkout:

```sh
bash -n tests/nixos/run.sh
bash tests/nixos/run.sh
nix build --no-update-lock-file \
  'path:./examples/lab/modules/nixos#checks.x86_64-linux.guest-policy' \
  'path:./examples/lab/modules/nixos#checks.x86_64-linux.image-policy' \
  'path:./examples/lab/modules/nixos#checks.x86_64-linux.technitium-composition'
```

`run.sh` runs `nix flake check --no-build`: it evaluates real locked modules,
security policy assertions, username validation, image settings, and derivations.
The final command realizes three small policy-check derivations, including
composition of the guest baseline with the closed Technitium example. None
boots a VM or tests real cloud-init/SSH/activation. The
[Technitium tests](../../../../tests/technitium/README.md) separately check
its exposure gate and emitted firewall rules. No custom deployment helper needs
a separate retry/parser test suite.
An image build, image boot, Proxmox provisioning, and full guest/service
activation require separate evidence. Local validation used an isolated
rootless Nix runner; no host Nix installation or live guest activation occurred.

## Official sources reviewed

- [Pinned upstream Proxmox image module](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/virtualisation/proxmox-image.nix):
  raw cloud image versus backup artifact, EFI, cloud-init, guest agent, and image
  output behavior.
- [Pinned cloud-init module](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/system/cloud-init.nix):
  native settings and boot stages.
- [Pinned native rebuild orchestration](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/pkgs/by-name/ni/nixos-rebuild-ng/src/nixos_rebuild/__init__.py)
  and [activation implementation](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/pkgs/by-name/ni/nixos-rebuild-ng/src/nixos_rebuild/services.py):
  `--store-path`, `--no-reexec`, profile update, and whole-system switch.
- [Nix 2.34 SSH store](https://nix.dev/manual/nix/2.34/store/types/ssh-store.html),
  [copy](https://nix.dev/manual/nix/2.34/command-ref/new-cli/nix3-copy),
  and [locked build flags](https://nix.dev/manual/nix/2.34/command-ref/new-cli/nix3-build):
  explicit store transport, fixed remote program, and refusing lockfile updates.
- [NixOS manual](https://nixos.org/manual/nixos/stable/):
  system configuration, upgrades, hardware configuration, and preserving
  `system.stateVersion`.
