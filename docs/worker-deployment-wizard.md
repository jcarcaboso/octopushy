# Managed NixOS worker wizard (template v1)

The wizard adds **one normal VM guest** to the configured homelab repository.
It does not install anything on a Proxmox hypervisor. The existing Fedora guest
and other definitions are not modified.

## Operator setup

1. Build the image from the exact reviewed application revision:

   ```sh
   bash deploy/worker-image/build.sh /absolute/path/to/new-artifact-directory
   ```

   This uses the locked root Nix flake and a digest-pinned Nix build container,
   produces `octopushy-worker-v1.qcow2` and `SHA256SUMS`, checks the QCOW2,
   and boots a disposable overlay with **no network interface**. The
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
   sockets and cores, RAM, datastore, bridge, optional VLAN and existing
   OpenTofu state backend.
2. Defaults are 1 socket × 2 cores, 2048 MiB RAM, 8 GiB boot disk and a
   **separate 16 GiB agent-state disk**, with DHCP networking.
3. **Review deployment** shows the exact files and base commit. Confirm them,
   then **Add worker to repository**. This is an authenticated Git write, not
   a deployment approval.
4. Review and trust the new exact revision using the normal Repository flow.
   Then approve the saved VM plan. A managed worker always requires manual
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
- The second disk is identified by its fixed serial. First boot formats only
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
  approval and exact revision trust are not execution sandboxing.
- Retirement, generation replacement, movement, role removal and destructive
  replacement plans are refused. **There is no worker-delete operation in v1.**
  Persistent drain/retirement remains a separate milestone. Invalid worker
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
Do not substitute the controller's database, expose PostgreSQL casually, or
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

Red/green regressions include forbidden inventory renames and revert recovery,
ordinary rename correction, invalid worker edits alongside unrelated guests,
unlabeled workload admission, exact generation/key retries, partial enrollment
replacement refusal, in-flight source changes, and destructive-plan approval
bypass. The independent read-only reviews found concrete inventory and boot
ordering defects; the fixes and executable regressions are included.

The hosted baseline checks are configured for stacked PR targets too. Hosted checks
do not build or boot this image, deploy a VM, or qualify real provider/network
permissions.
