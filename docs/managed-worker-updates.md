# In-place managed worker OS updates

This path activates a prebuilt NixOS system on an existing managed worker through
Octopushy. It preserves the VM, generation, enrollment files and state filesystem.
It is separate from template updates, which do not change an imported boot disk.

**Source implementation is not live acceptance.** Do not enable this feature
until an old-image-to-new-system fixture has qualified the archive, QGA
continuity, state/identity preservation, failure handling and reporting after a
reboot. No running homelab VM has been updated by implementing this path.

## Prepare a release

Build the image and system from the same reviewed source:

```sh
nix build .#worker-image-v1 --out-link result-worker-image
nix build .#worker-system-v1 --out-link result-worker-system
```

`deploy/worker-image/build.sh` also produces the matching `worker-system.nar.xz`,
`SYSTEM_PATH` and `SHA256SUMS` after its networkless image boot check. Use a
fresh, dedicated container engine as described in that script. This boot check
alone is not old-to-new update or configured reporting acceptance.

Export the complete system closure as a compressed `nix-store --export` stream,
not a bare NAR or a filesystem tar:

```bash
set -euo pipefail
system="$(readlink -f result-worker-system)"
nix-store --export $(nix-store --query --requisites "$system") |
  xz -T0 -c > worker-system.nar.xz
sha256sum worker-system.nar.xz
printf '%s\n' "$system"
```

Publish the archive and matching QCOW2 over guest-reachable HTTPS. Record their
different SHA-256 values. Do not include credentials in the image, Nix store,
export or URL. Qualify the actual release on disposable guests before offering
it to a real worker.

Configure the controller's `Octopushy:WorkerUpdates` section:

| Setting | Meaning |
| --- | --- |
| `Enabled` | Default false. Enable only after release qualification. |
| `ArchiveUrl` | HTTPS URL of the compressed export, without credentials, query or fragment. |
| `ArchiveSha256` | Exact 64-character SHA-256 of the compressed archive. |
| `SystemPath` | Exact `/nix/store/...-nixos-system-...` path exported above. |
| `ImageSha256` | SHA-256 of the matching qualified worker QCOW2. |
| `CaCertificatePem` | Optional public CA for archive download. Never a private key. |

For native reporting, also configure the existing DiskReporter HTTPS web/API
origin and set `DiskReporter.ManagedNixosImageSha256` to the qualified image's
hash. Native-v1 uses 3600 seconds. An explicit `setup.diskReporter.enabled: false`
in Git is still respected. Change that opt-out through the normal reviewed
repository flow before requesting reporting on an upgraded worker.

Upgrade the controller and the **independent control worker** first. The target
must have its original Ready enrollment and be idle, without unresolved work.
Its original repository source, placement and endpoint must still match. This
path does not repair remote-worker access to a loopback-only state backend.

## Request through Octopushy

Under **Agents → Prepare worker deployment → Existing managed workers**, choose
**Review OS update**. Review the system path and archive hash, then choose
**Drain and plan OS update**.

This freezes the release metadata and creates a normal provision run using
stored, version-pinned credentials. It does not approve the run. The worker is
drained so it cannot receive work while an independent control worker prepares
the saved OpenTofu plan.

Review the run. Approval must name both the saved-plan hash and the exact OS
archive hash, even when infrastructure has no changes. Destructive/replacement
plans remain refused. This first update path requires an **unchanged**
infrastructure plan, including outputs. Reconcile other infrastructure changes
separately. A missing VM cannot be recreated through an OS update, and a direct
assignment cannot bypass these checks.

The native executor checks VM ownership and generation, then runs a fixed
controller-built program under the existing NixOS lock. It verifies local
generation and Agent ID, downloads the hash-pinned export, imports it, and
activates the exact system. Signature checking is relaxed only for this
explicitly hash-approved import process, not the daemon's configuration.
No repository script, VM recreation, disk formatting or identity replacement
is performed.

The program retains the previous system profile and records local hashes of
the enrollment files and the state filesystem UUID. It requires exact
preservation, the selected running system, the native reporter helper, and
the correct worker executable. Activation explicitly restarts the idle worker.
A separate verification and a fresh authenticated Hello from the same Agent ID
must succeed. The controller closes any pre-result worker session so a reconnect
during archive download cannot satisfy post-switch readiness. Bootstrap and
certificate issuance are not repeated.

The update switches user space and selects the system for the next boot. It
does not automatically reboot or claim that a different kernel is already
running. Kernel/boot changes need separate maintenance and boot qualification.

Successful completion **leaves the worker drained**. Review the run and any
reporting result, then use **Resume scheduling** on the Agents page. Draining
is independent of disabling; the worker remains connected while drained.

The controller records the verified release only after activation verification
and fresh authentication. Later reporting setup uses that release, not the old
bootstrap-image pin. The preparation page links to its evidence run. Failed or
uncertain updates do not replace this evidence. Existing Terraform import inputs
can therefore stay unchanged when enabling reporting on an upgraded worker.

The equivalent API calls work through [operator CLI access](operator-access.md):

- `GET /api/managed-worker-preparation/{resourceId}/update` reviews the offer.
- `POST` to the same path accepts `generation` and `archiveSha256`.
- Run approval also requires `workerUpdateSha256`.
- `PATCH /api/agents/{agentId}` with `{"draining":false}` resumes after completion.

## Failure handling and qualification

An unfinished update prevents resuming, disabling or deleting its target's
enrollment. Lost QGA responses, invalid preservation evidence or an interrupted
activation leave a mutating outcome **uncertain**, with no automatic replay.
A local in-progress marker also prevents blind activation retries.

Activation failures attempt to restore the previous profile and runtime.
This is best-effort rollback, not proof that rollback completed. Inspect a failed
or uncertain update before closing it. Automatic recovery, reboot orchestration
and re-enrollment are not part of this slice.

Required image-level acceptance includes:

- Real old-image-to-new-system activation with the original key, certificate,
  Agent ID, generation and state filesystem UUID unchanged.
- Correct running worker executable and fresh mTLS Hello.
- Bad archive hash, unavailable HTTPS/CA, foreign ownership/generation, busy
  lock, interrupted activation and rollback without blind replay.
- Real HTTPS/204 reporting, non-null root usage, and hourly cadence after reboot.
- Explicit drain/resume behavior and independent control-worker execution.

Mocked QGA, API tests and source-policy assertions do not establish these claims.

`deploy/worker-image/upgrade_reporting_smoke.py` exercises the fixed native
program against a disposable old-image overlay and synthetic, isolated HTTPS.
It requires the cached QEMU/firmware/Python/OpenSSL/cdrkit tools, the old public
image and checksum, the new closure and system path, and the fixed program path.
It supplies NoCloud metadata like a real Proxmox guest, verifies preserved
identity/state and exact retry, rejects bad reporting CA/token, and receives a
real report after reboot. Its hourly timer assertion checks the runtime
configuration, not a full hour's elapsed cadence. It never contacts a real
controller or proves live mTLS/ingress.
