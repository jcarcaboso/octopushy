# Guest filesystem growth

VM disk capacity and guest filesystem capacity are different facts. An OpenTofu
apply is not proof that the guest can use the new space.

## Explicit, limited support

Select `recipes/nixos-vm` (alongside the shared `recipes/proxmox-vm` input schema),
or add this to a compatible provisioning recipe:

```yaml
verify: proxmox
guestFilesystem: nixos-ext4-root
```

Declare `hardware.disks[0].size` explicitly in GiB. The opt-in means the recipe
authorizes **online guest root reconciliation**, including after an infrastructure
no-op. Existing VM plan/apply approval policy remains unchanged. Do not enable
this recipe if every guest-side repair requires its own separate approval;
guest-specific approval gates are not implemented.

The first version supports only the common NixOS base image on a SCSI disk: GPT,
512-byte sectors, partition 1 EFI/vfat followed by partition 2 ext4 labelled `nixos`,
with no other partitions on that disk. The root partition must start within the first
GiB.

The disk is found from the mounted root, not by name: Linux can call the boot disk
`sda` on one boot and `sdb` on the next when a VM has a second disk, as a managed
worker does. Partition 2 must be the mounted root, the only device labelled `nixos`,
and hold the mounted filesystem's UUID, because the native `growpart.service` follows
that label. The disk's capacity is then compared with the desired `hardware.disks[0]`
size, so the worker state disk is never measured or resized in its place.

It rejects other layouts, encryption, LVM, other filesystems, unexpected VM
identity, templates, and a virtual disk whose capacity differs from the desired
capacity. It never formats, shrinks, changes mounts, rebuilds the OS, or reboots.
Data-disk expansion is outside this contract.

The guest needs QEMU guest-agent command execution, root privileges, NixOS's
existing `growpart.service` and `systemd-growfs-root.service`, and the native
`dumpe2fs`/util-linux/coreutils tools. The existing Proxmox credential must be
allowed to execute guest-agent commands. This is root-equivalent access, not
merely permission to read guest network addresses. TLS policy remains the
installation's configured policy; no new SSH credentials are involved.

## Steps, verification, and retries

The provision run becomes:

1. `plan` → `approval` → `apply`: the existing saved-plan lifecycle.
2. `verify`: VM presence, placement, running state, and network readiness.
3. `grow-root`: validate ownership/layout/capacity, then use the two native NixOS
   units only when the partition or ext4 filesystem has room to grow.
4. `verify-root`: independently measure the disk, partition, ext4 block count,
   filesystem UUID, and usable capacity.

Both guest actions use a fixed executor program via the Proxmox API. Git cannot
substitute commands. It takes the same `/run/lock/octopushy-nixos.lock` used by
the native activation procedure. Existing agents without the `nixos-rootfs`
capability cannot be assigned these steps.

Growth and verification have separate attempts, logs, and outputs. Applied
revision still records infrastructure apply; the desired inputs are marked
synchronized only after guest capacity verification succeeds. A no-op plan skips
infrastructure apply, **not** guest capacity checks. A fully expanded guest takes
the no-change path without restarting native growth units.

For a known failed `grow-root` or `verify-root`, use **Retry guest step**, or:

```text
POST /api/runs/{id}/retry-filesystem
```

It creates a new attempt only for the failed step and continues the remaining
guest steps. It retains prior attempts, does not replan/reapply infrastructure,
and refuses stale desired inputs, newer runs, retired/missing targets, and
uncertain outcomes (including an uncertain run subsequently closed by an operator).

Submission/status transport loss, deadline expiry, or interruption of a mutating
guest command is **uncertain**, not an ordinary retryable failure. Inspect the
reported guest PID, native unit jobs, lock, and actual capacity before closing
and replanning. There is no automatic command replay. A new run still checks
the lock and native unit state before mutation.

Retirement never runs guest growth/verification: it retains the existing
destroy-plan approval, unprotect, apply, and absence-verification path. VM deletion
also destroys guest-local service data; this feature does not provide backups.

## Checks

- Definitions tests cover opt-in, invalid contracts, and retirement exclusion.
- Server integration tests exercise the public agent/API boundary: isolated
  retry, preserved history, no-op checks, stale and uncertain retry refusal.
- Agent tests exercise the wire/executor boundary, ownership checks, real
  capacity expectations, known failures, and lost execution status.
- `agent/crates/octopushy/tests/nixos_rootfs_guest.rs` runs the guest program itself
  as the executor submits it, with stubbed disk tools: both disk orders, growth and
  no-change paths, and each refusal, always leaving the state disk alone.
- Live testing should verify actual filesystem growth, a deliberately blocked
  guest step followed by targeted retry, Portainer/data continuity, a final no-op,
  and unrelated guest isolation. Local mocked checks alone do not establish
  those properties.

### Live verification — October 21, 2026

The retained Portainer test VM passed a 26 → 27 GiB resize through the existing
controller/state. Holding the native operation lock deliberately failed only
growth; a targeted retry succeeded without repeating infrastructure work.
Independent capacity verification passed, followed by a no-op run that did not
restart growth units. Portainer authentication, container/data identity, and
boot/filesystem identity were preserved. Seven unrelated guests were unchanged.

After these checks, the authorized retirement removed the VM, its disks/data,
and its owned image import through the same controller. Independent Proxmox checks
confirmed their absence. Evidence is retained in
`.dev/portainer-e2e/rootfs-workflow/`; the test VM is no longer running.
