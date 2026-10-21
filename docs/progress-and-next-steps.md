# Progress and next steps

Snapshot: **October 21, 2026**. The plan below is a proposed implementation sequence,
not authorization to deploy, rotate credentials, or change existing machines.

## Where we are

Octopushy has a working VM control plane, reusable native guest/service examples,
and a tested credential-lifecycle implementation. The next gap is **having a
remote worker perform the complete guest and service lifecycle**, rather than
running the guest setup externally after controller-driven provisioning.

### Publication boundary

This is a documentation-only checkpoint on `proxmox-e2e`, based on committed
implementation `ccacdb0`. It does **not** commit or deploy the outstanding code.
At this snapshot, networking extensions, Fedora/NixOS examples, filesystem
growth, worker-readiness fixes, and credential-lifecycle changes are present in
the local working tree but remain uncommitted. A checkout of this documentation
commit therefore does not contain all the capabilities described below.

The most recent worker/credential slice has not been installed into the retained
running environment. Its migration has only been exercised against isolated test
databases. No live credential rotation or remote-topology acceptance is claimed.
Earlier disposable-VM experiments are separate evidence, not proof that this new
slice is deployed.

The [architecture agreement](architecture-agreement.md) describes the broader
direction; [milestone 1](milestone-1.md) describes the original bounded scope.
Neither is a checklist of everything implemented today. This snapshot records
progress without replacing those documents or changing the
[domain vocabulary](../CONTEXT.md).

## Progress by area

| Area | What exists in the reviewed working tree | Boundary |
| --- | --- | --- |
| Control plane | .NET 10 backend, PostgreSQL, React/TypeScript UI, Git definition loading, run/step/attempt history and logs | One trusted administrator and one controller process; not controller HA or tenant isolation |
| Workers | Rust worker, outbound mTLS channel, enrollment/renewal, labels/capabilities, bounded concurrency, durable result outbox | An Octopushy worker is not the QEMU guest agent and need not run inside every managed VM |
| VM lifecycle | Saved OpenTofu plan, policy/manual approval, exact-plan apply, Proxmox verification, explicit retirement and absence verification | No automatic adoption of Jenkins-owned state; provision success is not service health |
| Scheduling/recovery | Per-guest serialization, host/worker limits, visible wait/block reasons, conservative handling of uncertain mutations | Existing controls are not a general dependency scheduler or adaptive load balancer; cancellation does not prove external work stopped |
| Networking | Plain DHCP, DHCP with an expected externally reserved address and explicit MAC, or static IPv4 | Octopushy neither creates reservations nor supplies IPAM; verification checks observed addresses |
| Native guest preparation | Fedora Ansible workflow; pinned NixOS guest/bootstrap-image modules and explicit closure build/copy/activate/verify procedure | Examples/manual execution, not worker-dispatched setup executors |
| Services and DNS preparation | NixOS Docker service-node/Portainer role; optional guarded Technitium role and two-guest same-host cluster example | No controller-owned service/DNS reconciliation; a same-host pair does not survive loss of that host |
| Guest disk growth | Opt-in `nixos-rootfs` executor, separate growth/capacity verification, targeted retry preserving successful infrastructure work | Only the documented NixOS GPT/ext4 layout via QEMU guest-agent execution; not generic disks, filesystems, or scripts |
| Remote-worker readiness | Canonical paths, truthful OpenTofu capability advertisement, clearer queue reasons, opt-in remote acceptance harness | Harness configuration is tested; an actual remote run remains pending |
| Credentials | Encrypted candidate versions, explicit promotion/revocation, pinned run bindings, API/UI controls and migration tests | Not automatic credential verification, guest SSH management, upstream revocation, or master-key rotation |

### Credential milestone in practical terms

- Initial secrets become active for bootstrap compatibility, without a claim
  that they have been authenticated. Replacements become candidates.
- **Variables → Versions** shows metadata/usage and offers explicit promotion or
  revocation. Promotion needs an operator verification note and an expected
  current version to reject conflicting updates.
- Runs pin exact versions before their first attempt. Subsequent steps and
  targeted retries retain those versions; only each step's required values are
  released. Missing or revoked dependencies block rather than silently falling
  back to another credential.
- Revocation blocks later handoffs, but cannot retract assignments already
  handed to the outbound channel or revoke the actual Proxmox token.
- Legacy runs without unambiguous credential evidence block for reconciliation.
  Existing encrypted data with a missing encryption key stops startup instead
  of silently creating a replacement key.
- The store extends the existing subsystem. It does not add a new secret
  manager, full immutable promotion-event ledger, or environment authorization.

## What has been tested

### Latest implementation validation

The completed worker/credential implementation run reported:

| Suite/check | Result |
| --- | --- |
| Backend, including isolated PostgreSQL integration and migration tests | 146 passed |
| Rust worker/library tests, including doctest | 86 passed; 2 optional Podman smoke tests ignored |
| Frontend | 51 passed |
| Offline remote-harness configuration | 6 passed |
| Frontend build, TypeScript, lint; shell syntax; whitespace checks | Passed |

These are recorded implementation results, not new test executions performed
for this documentation checkpoint. `cargo fmt` was unavailable and was not run.
The database tests used isolated databases and fake workers, not retained
deployment state.

An independent review found and then verified fixes for a plain-to-secret read
race, revocation racing assignment handoff, and ambiguous legacy credential
recovery after requeue. Regression tests cover those paths. The follow-up review
reported no remaining concrete findings in its reviewed scope; it was not a
production security audit.

### Earlier disposable-VM evidence

- VM creation, resize, and retirement were exercised through the controller and
  a worker running on the controller host. That topology is **not** remote-worker
  acceptance.
- The Portainer experiment combined controller-driven VM provisioning with
  native NixOS/Docker setup outside the controller's service execution model.
  This demonstrates a usable service-node building block, not a completed
  Octopushy service executor.
- The recorded October 21 filesystem test grew the Portainer VM from 26 to
  27 GiB. A deliberately held guest lock failed the growth step; targeted retry
  succeeded without repeating infrastructure work. Independent capacity checks
  and a subsequent no-op passed. Portainer/data identity and filesystem/boot
  identity were preserved; seven unrelated guests were unchanged.
- That VM, its disks/data, and its owned image import were then retired through
  the same controller, with independent absence checks. It was not left running.
- Technitium role/cluster evaluation and native firewall syntax checks are
  separate from runtime DNS, kernel packet-path, or failover verification.
  Passing those checks alone is not evidence of a remotely managed DNS service.

Local working-tree detail lives in `docs/credential-lifecycle.md`,
`docs/vm-networking.md`, `docs/guest-filesystem-growth.md`,
`tests/remote-worker/README.md`, `tests/portainer/README.md`, and
`tests/technitium/README.md`. Those files are **not included in this
documentation-only checkpoint**. Private runtime evidence stays outside Git;
no credentials or deployment-state files should be added when publishing code.

## Rules the next work must preserve

1. **Independent progress.** A genuine host-wide failure blocks that host's
   dependent work, not other hosts. A guest failure blocks its dependants, not
   sibling guests. A service failure blocks only that service and its actual
   dependants. Do not turn every operation error into a host-wide failure.
2. **No fleet-wide phase barriers.** Aggregate results for display, not execution.
   These are requirements for extending the existing VM workflow, not a claim
   that all service dependency behavior already exists.
3. **Bounded, stable execution.** Retain configurable host/worker limits and
   prepared work. Prefer waiting or worker affinity to rebuilding plans and
   artifacts merely to chase spare capacity.
4. **Native ownership.** Keep OpenTofu for infrastructure, Ansible/NixOS for their
   respective guest configuration, and explicit ownership for ports and data.
   A NixOS generation changes the whole guest: shared activation must serialize
   even when service verification and status remain independent.
5. **Honest evidence.** Desired configuration, approved/applied infrastructure,
   successful guest configuration, and observed health are different facts.
6. **Conservative recovery.** Retry only known failed, supported steps. Unknown
   mutation outcomes require reconciliation, not automatic replay. Compensating
   removal is not a promise of transaction rollback or restored application data.

## Proposed next steps

### 0. Publish the implementation and prepare a safe upgrade

Before installing the new worker/credential slice:

- Review and commit the outstanding implementation in coherent scopes, including
  its tests, contracts, examples, and feature documentation. Preserve unrelated
  edits and exclude private files/state.
- Record the tested source revision and repeat the relevant suites if code has
  changed since the recorded runs.
- Back up the retained database, encryption key, and required runtime state;
  verify a matched restore route before applying the credential migration.
  The new schema deliberately does not support downgrade to the old binary.
- Upgrade only an explicitly selected test environment first. Check existing
  credentials, pending runs, UI/API behavior, and worker enrollment.

**Done when:** the tested code is reproducible from Git and the chosen test
environment runs that revision without losing credential/run history. This
documentation push is not that upgrade.

### 1. Establish managed guest access

Start with access to one disposable NixOS guest, not a generic SSH job runner.

- Make automation login user, private-key store reference/version, and approved
  host-key binding explicit for the guest's stable identity.
- Keep automation credentials separate from human/break-glass keys and worker
  mTLS identity. Require an independent host-key verification route and a
  recovery route before changing access.
- Deliver only the selected secret version to the selected worker in a protected
  temporary file. Never put private keys into Git, Nix derivations, outputs,
  artifacts, or the durable result outbox.
- Verify a fresh connection using only the intended identity, strict host-key
  checking, no multiplexing, and the privileges the operation actually requires.
- Decide and test the minimum SSH key format and required guest permissions;
  avoid an unbounded credential/executor interface.

**Done when:** a worker without the controller checkout can authenticate to the
intended guest; wrong host keys, credentials, guest identity, or privilege fail
closed; cleanup and log/output tests show no retained private-key material.
This first step establishes access, not key rotation.

### 2. Move setup and service execution onto the worker

Use the existing assignment, status, artifact, and result channels.

- Add narrowly typed setup/service actions wrapping the proven native NixOS
  workflow; keep Fedora/Ansible integration as a subsequent adapter.
- Snapshot the reviewed configuration/module identity and exact closure or
  artifact used. Setup-only and service-only edits must request the appropriate
  work rather than being ignored by provisioning change detection.
- Separate setup, service application, and external verification in run history.
  Reuse guest mutation locking and uncertainty rules; mark the lifecycle
  synchronized only after its required final verification.
- Keep per-service status/retry boundaries without pretending that NixOS
  activation is service-local. Define who builds a closure, who activates it,
  and how an unavailable build/activation worker affects prepared artifacts.
- Deploy one Docker/Portainer service with explicitly scoped direct-port access
  and protected runtime-secret delivery. Do not introduce DNS or a reverse proxy
  merely for this test. Initial Portainer bootstrap is not password rotation.

**Done when:** the controller dispatches and displays setup → service → verify
on the selected worker; known failures retry only the supported failed step and
dependants; a repeat is a safe no-op. No manual SSH command substitutes for a
missing executor in the acceptance result.

### 3. Add staged SSH access rotation

Build on managed access and the native configuration owner, not cloud-init alone.

1. Stage a candidate automation key and add its public key alongside the old one.
2. Apply that change through the reviewed guest configuration.
3. Verify a fresh candidate-only connection and required privileges.
4. Explicitly promote the guest binding after evidence is recorded.
5. Remove only the old controller-owned authorization in a separate approved
   stage, then verify again. Preserve human/break-glass access.

**Done when:** failed candidate authentication leaves working access intact;
interruption records uncertainty; reconciliation does not blindly remove keys;
successful access policy/version and verification evidence are recorded
separately from the VM provisioning hash. A global secret promotion must not
implicitly rotate other guests.

### 4. Run the full remote-worker acceptance test

Use one dedicated controller, one separately enrolled worker on another machine,
and one small disposable service VM. Start with an exclusive worker label and
concurrency `1`. The current VM-only remote harness can provide an earlier smoke
test after step 0; the full lifecycle below depends on steps 1–3.

- Verify routable controller/mTLS names, worker-reachable OpenTofu state, trusted
  Proxmox access, tool/artifact access, and guest/service reachability. Do not
  copy another worker's identity/state directory or weaken TLS to make it pass.
- Run provision → setup → Portainer bootstrap/external verification → disk
  expansion/capacity verification → no-op → staged SSH rotation.
- Inject a controlled known failure and show targeted retry preserves completed
  infrastructure work. Exercise worker/controller disconnect and restart,
  durable result acknowledgment, and inspection-gated uncertain outcomes.
- Verify routing/capability/capacity waits, assigned worker identity, no shared
  checkout dependency, service data continuity, and unrelated-resource isolation.
- Explicitly retire the VM through the controller and verify owned resources
  absent. Retain sanitized evidence; do not bypass unresolved mutations to clean up.

**Done when:** every lifecycle attempt is attributable to the intended remote
worker, the user can understand and recover failures from controller status,
and cleanup succeeds without affecting other machines. A second disposable
target can then validate concurrency/isolation under configured limits.

## Later work and open limits

Do not turn the first remote lifecycle into a new general-purpose CI platform.
Keep these separate from the steps above:

- Automatic Proxmox credential verification/rotation and upstream revocation;
  full custom-CA transport into OpenTofu; production TLS policy.
- KEK identifiers, rewrapping and backup/restore automation; encrypted worker
  outbox and complete durable offline logs.
- Service dependency modeling beyond the first bounded lifecycle, independent
  maintenance/tool-update operations, and operation-specific remove/compensate
  contracts with verification. Do not infer safe undo for arbitrary scripts.
- Notifications and live service health, with stale/unknown observations distinct
  from successful execution; richer node metrics and eventual load balancing.
- DNS/Technitium lifecycle, record ownership, DHCP reservation integration, and
  multi-host availability testing. Review the actual runtime package before
  exposure; the prepared default package remains blocked by the role's gate.
- External secret providers, RBAC, multiple controllers, editor/Git write-back,
  release workflows, Proxmox containers, and later Talos/Flux support.

For Jenkins migration, move one explicitly owned disposable/new guest first.
Do not allow two systems to mutate the same state or guest configuration.
Adopting existing workloads needs a separate state/ownership handoff, backup,
and reconciliation plan; it is not an implicit part of the remote-worker test.
