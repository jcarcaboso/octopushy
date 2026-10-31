# Managed NixOS worker wizard (template v1)

The wizard adds **one normal VM guest** to the configured homelab repository.
It does not install anything on a Proxmox hypervisor. The existing Fedora guest
and other definitions are not modified.

Use **Review deployment** to review the executable guest definition and versioned
recipe, then **Add worker to repository** to publish them. **Generate review
draft** is an optional documentation-only export; it never creates a VM.
Publication still requires a trusted installation and the saved infrastructure
plan's approval before the VM is created and its worker is bootstrapped.

Generated NixOS worker definitions explicitly disable `setup.diskReporter`: the
separate FHS/systemd installer supports mutable guests such as Fedora, not
immutable NixOS. Enabling server-wide disk reporting must not send that installer
to a default worker. This does not yet provide disk-usage telemetry for the
NixOS worker itself.

## Operator setup

1. Build the image from the exact reviewed application revision:

   ```sh
   bash deploy/worker-image/build.sh /absolute/path/to/new-artifact-directory
   ```

   This uses the locked root Nix flake and a digest-pinned Nix build container,
   produces `octopushy-worker-v1.qcow2` and `SHA256SUMS`, checks the QCOW2,
   and boots a disposable overlay with **no network interface**. The
   The disposable disks reproduce Proxmox's separate SCSI controllers and
   explicit `device_id`, independently of the state disk's serial.
   `boot-smoke.log` records state-disk, rootless Podman and public identity
   helper checks; a failed check makes the build command fail.
   It does **not** upload or deploy it. The image contains the built Rust agent,
   fixed bootstrap helper, QGA and rootless Podman—not controller credentials,
   an agent identity, enrollment tokens, SSH private keys or a lab-specific URL.
   Keep the image and checksum together. Never replace bytes at a published
   version's URL.

2. Publish that artifact at an HTTP(S) URL reachable by the selected Proxmox
   node. Configure the controller (not the wizard request):

   ```text
   Octopushy__WorkerDeployment__ImageUrl=https://images.example/worker-v1.qcow2
   Octopushy__WorkerDeployment__ImageSha256=<64-character SHA-256 from SHA256SUMS>
   Octopushy__WorkerDeployment__RepositoryWritesEnabled=true
   Octopushy__AgentChannel__PublicUrl=https://controller.example:8443
   ```

   The image URL must not contain credentials, query tokens or a fragment.
   The configured repository credential needs permission to add a commit to
   the configured branch. Branch protection is respected: a rejected push
   fails visibly; the wizard does not change branch protection or use another
   credential. There is no GitHub-specific PR bypass.

3. Expose the agent listener to the guest network and include the chosen name/IP
   in `AgentChannel.Hostnames`. The public URL must be explicit and HTTPS, not
   localhost or a wildcard listen address. Worker connections retain the
   application's pinned CA and mTLS, even when Proxmox's separate homelab
   connection has `tls.verify: false`. Local control-worker connectivity does
   not demonstrate remote ingress.

4. Update an **independent control worker** to this revision. It needs
   `opentofu`, `proxmox`, `nixos-rootfs` and `managed-worker` capabilities and
   the target's existing execution labels. Do not replace the control worker
   with the guest it is about to create. Apply migrations only after the normal
   idle/uncertainty checks and matched database/key backup.

## Wizard

Open **Agents → Prepare worker deployment** (`/agents/prepare`).

1. Select an already-configured installation and host. Choose the name/VMID,
   sockets and cores, RAM, datastore, bridge and optional VLAN. OpenTofu state needs no
   choice: Octopushy keeps it, and the field appears only if `octopushy.yaml` defines
   external backends to choose from instead.
2. Defaults are 1 socket × 2 cores, 2048 MiB RAM, 8 GiB boot disk and a
   **separate 16 GiB agent-state disk**, with DHCP networking.
3. **Review deployment** shows the exact files and base commit. Confirm them,
   then **Add worker to repository**. This is an authenticated Git write, not
   a deployment approval.
4. After the next sync, approve the saved VM plan (trust the installation first
   if it is new). A managed worker always requires manual
   approval even when surrounding policy is automatic.
5. Follow the run through VM verification, root capacity checks, guest-local
   CSR preparation, public certificate installation and authenticated readiness.
   The UI does not call a running VM an enrolled worker.

Files added:

```text
labs/<lab>/proxmox/<installation>/guests/<name>/guest.yaml
templates/octopushy-worker-v1/template.yaml
recipes/octopushy-worker-v1/{recipe.yaml,inputs.schema.json}
modules/opentofu/octopushy-worker-v1/{main.tf,variables.tf,versions.tf,outputs.tf,.terraform.lock.hcl}
```

The guest includes a typed `worker` block with template version, generation,
labels and concurrency. Its generation is stable for the repository-source
identity and guest address. The wizard creates a dedicated
`worker-<generation-without-dashes>` routing label. Existing workloads are not
silently routed onto a remote worker: unlabeled work is excluded from managed
workers in both dispatch and final assignment construction. First qualify each
workload's endpoints, then deliberately configure matching execution labels.
Labels are operator opt-in, **not proof of network reachability or authorization
at the destination**. Independent control workers retain their existing routing.

Publication uses literal Git blobs and an explicit expected-head push. No
working-tree filters, hooks or repository scripts run. Existing differing
files are not overwritten. A lost push response can be reconciled by retrying
the same reviewed addition; after sync an existing generation offers status
instead of another VM. Stale or changed input requires another review.

## Identity and safety

- The fixed helper generates the private key **inside the VM**. Only public
  CSR/certificate material crosses QGA. There are no bearer enrollment tokens
  in Git, cloud-init or the image.
- The second disk is identified by its fixed SCSI unit serial through a
  dedicated udev alias; Proxmox's separate VPD device ID must not determine
  which disk is selected. First boot formats only
  an entirely blank disk; existing foreign data is refused. The durable
  identity, outbox and runtime storage live on that state disk. The mount uses
  the already-present serial device and waits for initialization, rather than
  racing a filesystem-label timeout. Scanning a large or slow disk can take a
  long time; it is intentionally not bypassed. Automatic pre-mount fsck is
  disabled so it cannot run before first formatting; ordinary ext4 journal
  recovery still occurs at mount. Damaged filesystems require offline inspection.
- A durable controller binding correlates resource, generation, placement,
  issuing attempt and Agent ID. A matching label or VMID alone is not identity.
- Workload admission waits for the issued agent's authenticated Hello and
  reported runtime capabilities. Self-target execution is refused in dispatch
  and final assignment construction, including preview.
- Native bootstrap uses the privileged action approval path even with a shared
  Proxmox token. Saved-plan integrity is checked before credential release.
  Shared-token preview still has that token's full upstream permissions;
  approval and installation trust are not execution sandboxing.
- Generation replacement, movement, role removal, `lifecycle: retired` and
  destructive plans in ordinary runs are refused. **Deleting an agent revokes its
  enrollment; it does not delete or retire its VM.** The worker view distinguishes
  a removed enrollment from an enrolled but offline worker.
- **To remove a worker, delete its folder.** Octopushy proposes a removal run built from the worker's last applied
  definition: plan the destroy, wait for manual approval (always, for workers),
  drain the worker, remove its VM protection, apply the saved destroy plan, verify
  the VM is absent, then remove its enrollment. Another worker does all of this;
  a worker never operates on its own VM. A failed absence check keeps the
  enrollment. Restoring the folder before the destroy starts cancels the removal,
  but the worker stays drained until it is resumed. See
  [lab-repository.md](lab-repository.md), rule 10.
- To finalize a worker already removed by hand, remove its Git definition, revoke its
  enrollment, and explicitly choose **Verify retirement** on the worker page. A
  deleted worker whose enrollment was already revoked is not destroyed
  automatically; **Plan removal** on its guest page does that explicitly.
  An independent control worker uses the existing read-only `verify-absent`
  executor against the original installation. Only explicit absence evidence
  marks the guest retired and removes it from the active worker list. A present
  VM, changed source, unfinished work or unresolved mutation blocks
  finalization; no plan, unprotect, destroy or guest command is executed.
  Identity and run evidence remain available. Retired generation names are not
  silently reused: choose a fresh name and unused VMID for a new deployment.
  Invalid worker
  edits cannot rename their durable resource, and are recorded as per-resource
  issues without dropping unrelated guest discovery. Reverting a rejected
  managed rename retains the original resource/generation.
- Known failed guest/bootstrap steps can resume without replaying a successful
  VM apply. Uncertain QGA/apply outcomes are never automatically retried:
  inspect and explicitly reconcile them through the existing run workflow.

## Limits and first live acceptance

An image evaluation/build, fixture lifecycle or mock worker is not proof that
the real VM can reach the controller, use the selected storage, obtain DHCP,
run rootless tools or access workload backends.

In the current LAN installation, the existing controller's agent listener and
OpenTofu PostgreSQL endpoint were previously loopback-only. Those must be
deliberately qualified before sending real workload assignments to a new VM.
The built-in state needs no such endpoint: it reaches workers on the agent channel, so
they never connect to the controller's database. Otherwise, do not substitute the
controller's database for an external backend, expose PostgreSQL casually, or
disable TLS on the worker channel to make the smoke test pass.

Before a real creation: retain a matched backup; verify an unused VMID and
capacity; publish the tested image; verify mTLS ingress; update the independent
control worker; inspect the exact generated Git diff and saved plan. Afterwards
verify the bound Agent ID, actual runtime capability probe, reconnect across
guest/controller restart and a harmless assigned task. Leave the existing Fedora
VM unchanged. No live acceptance is implied by the offline tests.

## Validation record — October 26, 2026

All application fixtures used generated identities and a separately rooted
disposable PostgreSQL/container engine, not the retained controller, worker,
state database or Proxmox installation.

| Check | Observed result |
| --- | --- |
| Full backend suite after review fixes | 333 passed |
| Frontend suite | 95 passed; lint, typecheck and production build passed |
| Rust workspace | 100 passed, including doctest; 2 existing optional Podman tests ignored |
| Rust formatting and Clippy | Passed, including `--all-targets -- -D warnings` |
| Offline Python suites | 63 passed; 5 existing optional checks skipped |
| OpenAPI/client regeneration | Regenerated from the backend contract |
| Worker OpenTofu module | Pinned provider initialization without a backend, then native validation: valid, zero errors/warnings |
| Locked Nix evaluation and image build | Passed; QCOW2 integrity check passed |
| QEMU/KVM boot with no VM networking | State disk mounted; rootless Podman info passed; public-only CSR, idempotent same-generation prepare, foreign-generation refusal and unenrolled service inactivity passed |

The complete `build.sh` entrypoint (including its boot gate) also passed. The
locally validated QCOW2's SHA-256 was
`05976fc2a9bf7046867b35a7e1f204f7062482f47d5f5c7bb7a8d2c7a9494b44`.
This identifies that artifact, not a promise that an independently rebuilt image
will have identical bytes. Use the checksum accompanying the image you publish.

**October 27 correction:** that artifact's simplified QEMU disk setup did not
exercise Proxmox's explicit SCSI `device_id`; its serial-derived mount path
could fail on Proxmox. Do not deploy the above artifact. The corrected image
matches the exact SCSI unit serial with udev, and the boot gate now reproduces
Proxmox's separate controllers, LUN and device ID. The replacement locally
validated artifact is
`04fbc180d88277ee3a4397b0e37c925b0ab5dc61db38fac0c3bf8e94cb597e84`.
Its no-network boot, state mount, rootless Podman and identity checks passed.
The verification-retry regression also passed, preserving prior plan,
approval and apply evidence; the full backend suite passed 370 tests and the
frontend suite passed 102, with lint, typecheck and production build passing.
A subsequent fresh lab deployment through wizard publication, exact-revision
trust and saved-plan approval reached authenticated Ready on Proxmox. Every
executed step succeeded on its first attempt, without custom QEMU arguments.
This validates worker provisioning/enrollment, not general remote workloads or
an automated retirement workflow.

Red/green regressions include forbidden inventory renames and revert recovery,
ordinary rename correction, invalid worker edits alongside unrelated guests,
unlabeled workload admission, exact generation/key retries, partial enrollment
replacement refusal, in-flight source changes, and destructive-plan approval
bypass. The independent read-only reviews found concrete inventory and boot
ordering defects; the fixes and executable regressions are included.

The hosted baseline checks are configured for stacked PR targets too. Hosted checks
do not build or boot this image, deploy a VM, or qualify real provider/network
permissions.

### Native disk reporter qualification

New image source includes an image-native reporter, but the old published image
pin is not automatically qualified. Automatic setup requires global reporting
and an explicit `DiskReporter.ManagedNixosImageSha256` matching the wizard's new
qualified `WorkerDeployment.ImageSha256`; otherwise generated definitions retain
the explicit reporter opt-out. Native-v1 supports only the recorded 3600-second
interval. See [managed worker disk reporting](managed-worker-disk-reporting.md)
for exact controller settings, runtime secret delivery, approval gates, validation
levels and the separately authorized OS update/fresh-deployment requirement for
existing Ready workers. A template pin update alone does not change their boot OS.
