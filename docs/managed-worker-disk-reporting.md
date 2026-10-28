# Managed NixOS worker disk reporting

Managed workers use the existing approved `install-disk-reporter` step and
per-guest HTTPS reporting API. They **do not** use the FHS downloader/installer.
The metric remains allocated usage of `/` (`statvfs` blocks minus free blocks),
not the sum of `/` and the separate `/var/lib/octopushy` state filesystem.

## Image capability and controller configuration

Publish a **new, qualified** `worker-image-v1` built from this source. The image
now builds `octopushy-disk-reporter` alongside `octopushy`, includes the fixed
`octopushy-worker-reporting` helper, and declares the service/timer with Nix-store
executable paths. No endpoint, bearer token, or runtime CA is an image/derivation
input. An unconfigured image's service and timer stay inactive without failing.

Upgrade the controller **and independent control worker** first; an older control
worker safely rejects the new native assignment shape, but cannot perform setup.
After image build, boot, HTTPS-ingress, and reboot-cadence qualification, configure:

```text
Octopushy:WorkerDeployment:RepositoryWritesEnabled = true
Octopushy:WorkerDeployment:ImageUrl = <public URL of the NEW qualified qcow2>
Octopushy:WorkerDeployment:ImageSha256 = <exact SHA-256 of that qcow2>
Octopushy:DiskReporter:Enabled = true
Octopushy:DiskReporter:ManagedNixosImageSha256 = <the SAME qualified qcow2 SHA-256>
Octopushy:DiskReporter:PublicUrl = https://<guest-reachable WEB/API origin>
Octopushy:DiskReporter:IntervalSeconds = 3600
Octopushy:AgentChannel:PublicUrl = https://<guest-reachable agent origin>:8443
```

Retain the existing valid `DiskReporter:BinaryUrl` / `BinarySha256` configuration
for ordinary supported guests: global reporter validation still requires it.
Those download settings are **not used or asserted as provenance** for native
workers. Native runs freeze `installationMode=managed-nixos-v1` and the qualified
**image** SHA-256, plus the existing VM name/placement/address/generation. An
optional public `DiskReporter:CaCertificatePem` travels in runtime configuration,
not the image. Use the actual web/API CA; the agent-mTLS CA/port is not the disk
report endpoint. Use the existing HTTPS ingress with correctly trusted forwarded
scheme headers (`TrustForwardedHeaders` only on an isolated/private API listener),
not the API's direct HTTP port. There is no TLS verification bypass or redirect
following.

If the repository already contains the old versioned template, the wizard still
refuses to overwrite conflicting template files. Publish the new pin/template
through an explicitly reviewed Git template migration first, preserving existing
worker identities and opt-outs; then review/publish new worker definitions. This
migration does not update the old VMs' imported boot OS.

The capability pin is deliberately separate and absent by default. Merely
upgrading the controller does not qualify the currently configured old image.
The wizard emits `setup.diskReporter.enabled: true` only when global reporting
is enabled and its image checksum exactly matches that capability pin. Otherwise
it retains the explicit opt-out. Existing definitions are never rewritten by the
wizard. Explicit per-guest `enabled: false` and global disable suppress setup;
these are **not** an uninstall/revocation mechanism for an already running timer.
A manually enabled managed worker without a qualified image fails run creation;
an implicit default on an unqualified image remains disabled, never FHS.

Native-v1 deliberately supports **only 3600 seconds**. Both run creation and the
helper reject another recorded interval. The image timer has a 60-second boot
activation, hourly `OnUnitActiveSec`, one-second accuracy, and no random delay.
Runtime settings persist across reboot; no volatile timer override can silently
revert the recorded interval.

## Approved runtime delivery

Normal trusted-revision plan/checksum review and action-based privileged approval
still apply, including no-change plans and Ready generations. No reporter token
is created/released until successful guest/root verification and authenticated
worker readiness. Pinned Proxmox credential checks and self-target exclusion
remain ahead of secret resolution. Only the reporter setup assignment receives
the per-guest token; neither `RunContext`, ordinary input JSON, nor managed
identity prepare/install payload/arguments receives it.

The independent control worker verifies VM identity, running state, management
tag, exact resource address and generation description through Proxmox before
calling **only** `/run/current-system/sw/bin/octopushy-worker-reporting` via QGA.
The bounded typed JSON, including the bearer, travels as QGA **stdin**, never
command arguments or shell source. No repository program, arbitrary unit/path,
or FHS fallback can be selected. The helper also checks the guest-local enrolled
generation. Missing helpers and known helper/service failures visibly refuse setup; an API
error which cannot confirm dispatch is conservatively uncertain.

The root-only helper locks, stops the fixed timer/service, atomically publishes a
private credential bundle under `/var/lib/octopushy-disk-reporter` (0700
directories / 0600 files), then starts the fixed oneshot and timer. Systemd loads
config/token/optional CA as runtime credentials; the reporter runs as a hardened
dynamic user. The helper waits for the oneshot's **new authenticated HTTPS 204**
delivery before returning success; only then is `firstReportSent=true` emitted.
Guest stdout/stderr are never forwarded to results/logs, and the service does not
journal arbitrary reporter diagnostics. CA/ingress/token errors are visible as a
failed setup/service, without echoing sensitive diagnostics. The fixed helper
has a 60-second process deadline and the oneshot a 30-second service deadline.
Lost dispatch/status, interruption, or helper timeout is uncertain; inspect the
guest before authorized recovery, never automatically replay a mutation.

## Existing Ready workers and rollout limits

An already Ready generation can run normal approved provisioning again and
configure reporting: prepare/install/wait identity steps remain skipped and its
agent ID, issued certificate and Ready timestamp remain unchanged. A fresh saved
plan and manual approval are still required, including a no-change plan.

**Updating source/template pins does not update an existing imported boot OS.**
The worker recipe intentionally ignores imported boot-disk changes. In particular,
changing a controller/image pin does not retrofit VM902. An old guest without the
helper fails visibly; it is not sent a shell installer. Before enabling its
previous explicit reporter opt-out, obtain separate authorization for an in-place
OS generation update that preserves identity/state, or deploy a fresh qualified
worker with a new identity through the normal workflow. The separate
[managed worker OS update path](managed-worker-updates.md) implements reviewed
in-place activation. It still requires release qualification and explicit
approval; implementing it performs no live retrofit.

## Validation levels

- `python3 deploy/worker-image/reporting_policy_test.py`: source regression
  assertions only (not Nix evaluation or image acceptance).
- `nix build .#checks.x86_64-linux.worker-image-policy`: evaluated image-unit
  policy, when Nix is available.
- `nix build .#worker-image-v1`, then the existing `smoke.py`: real disposable,
  networkless boot/state/rootless/identity checks plus reporter measurement,
  executable/units, runtime credential declaration and unconfigured inactivity.
  It cannot prove ingress delivery or configured reboot cadence.
- Native scripted-QGA tests verify fixed command/stdin, ownership refusal,
  missing helper, redaction and uncertainty; helper tests verify bounded input
  and private/idempotent atomic files. Reporter tests exercise genuine local TLS
  and require 204 (reject redirect/server failures). Backend tests use synthetic
  assignments and the authenticated report endpoint; they are not live VM proof.

Qualification before setting the capability pin must additionally test a real
configured disposable image against HTTPS ingress, invalid CA/token and
unreachable ingress, and reboot with persisted credentials/hourly timer. Source
assertions and fake assignments cannot substitute for this image acceptance.
