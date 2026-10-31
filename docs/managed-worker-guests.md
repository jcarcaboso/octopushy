# Managed Octopushy worker guests on Proxmox

**Original design/implementation plan — October 24, 2026.**

The October 26 implementation is described in
[the worker deployment wizard](worker-deployment-wizard.md). It implements the
bounded creation/bootstrap path, not the full drain/retire lifecycle proposed
below. This historical design is not a live-acceptance record.

Inspected source baseline: `cf9cb74`, the PR6 credential-routing foundation.
This document does not imply that PR6 was merged or authorize its deployment to
retained infrastructure. The parent's visible controller preview is separate
work; displaying a button or running the controller does not implement this
lifecycle. Inspection was source-only: no builds, containers, infrastructure
operations, private configuration, credentials, or retained databases.

## 1. Decision and bounded vocabulary

Deploy an **ordinary isolated QEMU VM** containing NixOS and the Octopushy agent
service. Do not install the worker, Nix, Podman, or an Octopushy helper on a
Proxmox hypervisor. Use the installation's existing authenticated Proxmox API
and the VM's QEMU guest agent (QGA), not hypervisor SSH.

Terms below refine this proposal without changing the repository glossary:

- **Worker guest:** a Guest resource whose declared role includes running an
  Agent. Its VM, installation, placement and lifecycle remain guest concerns.
- **Worker identity:** the registered Agent ID, locally generated private key,
  issued certificate and issuing controller CA. It is not the VMID, name,
  address, label, or NixOS machine identity.
- **Control worker:** an already enrolled agent outside the target guest,
  explicitly authorized to provision, bootstrap and retire that guest. This is
  a deployment role, not a new scheduler or a controller process.
- **Worker generation:** one controller-recorded incarnation of a worker guest,
  binding its infrastructure ownership to one fresh worker identity.
- **Draining:** no new assignments, while the existing channel remains usable
  for heartbeats, results and acknowledgements. **Disabled** instead denies
  channel authentication and disconnects the agent.

Use the existing Git guest resource, OpenTofu execution, controller runs,
attempts, approvals, credential bindings and inventory. Add only a typed worker
role, its guest/identity binding, a dispatch drain/exclusion guard, and fixed
native guest-bootstrap actions. No autoscaler, generic workflow language,
arbitrary command field, SSH executor, new secret store, or hypervisor agent.

“Any configured Proxmox installation” means any installation meeting the
explicit prerequisites below, independently of other installations. It does not
mean every PVE version, storage backend, network, ACL arrangement or disconnected
site works without qualification.

## 2. What exists, and the exact integration points

Paths are relative to the source repository. “Extend” denotes proposed work,
not functionality already supplied.

### Definitions, provisioning and ownership

- `CONTEXT.md` distinguishes a Proxmox installation, its API connection, host
  placement, a persistent Guest, and an execution Agent. A lab is not a token
  scope. Keep those distinctions in the UI and binding.
- `contracts/schemas/guest.schema.json`,
  `backend/src/Octopushy.Definitions/Model.cs` (`GuestDefinition`) and
  `LabRepositoryLoader.cs` already describe VM placement, explicit VMID,
  `lifecycle: active|retired`, template, hardware, network, recipe, state backend,
  execution labels and policy. `setup` is accepted but not executed;
  `profiles` does not install an agent. There is no worker role today.
- `ProvisionInputs.Build` constructs public tool variables and an input hash;
  `GuestDefinition.Stack` is the guest address with `/` replaced by `.`.
  `RunFactory.Guest` substitutes that stack into an external OpenTofu backend
  when the guest names one; otherwise the controller keeps the
  [built-in state](opentofu-state.md). Preserve one infrastructure state owner per guest.
- `InventoryUpdater.ApplyAsync` reconciles definitions into persistent
  resources and creates revision-triggered runs; `Planner.PlanGuestAsync`
  creates an explicit run. A disappeared definition sets `MissingSince` and
  blocks work: removing a YAML file is not a destroy instruction.
- `RunFactory.Guest` currently emits provision
  `plan → approval → apply → verify`, optionally adding
  `grow-root → verify-root`; retirement is
  `plan → approval → unprotect → apply → verify-absent`.
  `RunEngine.ApplyEffectsAsync` records checked/applied revisions and
  installation/VMID bindings; `verify-absent` sets `RetiredAt` and clears that
  current infrastructure binding. Worker evidence must remain separately
  available after those fields clear.
- `examples/lab/modules/opentofu/proxmox-vm/main.tf` already imports a
  checksum-pinned image, creates a protected EFI/Q35 VM, configures public
  cloud-init inputs, and attaches extra disks. Downloads are deliberately
  guest-owned, not multiply owned shared resources. Its
  `ignore_changes = [disk[0].import_from]` means a new template does **not**
  upgrade/reimage an existing guest.

### Identity and worker execution

- `Api/Endpoints.cs:MapAgents` exposes `/api/agents`, agent label/concurrency/
  disabled updates, deletion, and one-time enrollment-token creation/deletion.
  Tokens are `opy1.<secret>.<CA fingerprint>`, saved as a secret hash, with
  server-selected labels and concurrency.
- `Agents/AgentServices.cs:EnrollmentService.Enroll` atomically claims a
  single-use, expiring token, signs a guest-generated CSR using
  `AgentCertificateAuthority.IssueAgentCertificate`, and creates `AgentRecord`.
  Tokens have no Guest/resource binding today; names and labels cannot provide
  that missing correlation.
- `agent/.../enrollment.rs` pins the CA fingerprint before submitting a token,
  generates the key/CSR locally, and writes identity through `identity.rs`.
  `AgentChannelService.Connect` and `RenewCertificate` authenticate the
  enabled Agent record and its registered current/previous certificate serial.
  Renewal uses the existing mTLS identity; the previous serial remains valid
  until connection with the new certificate. Reuse this renewal path.
- `RunEngine.Agents.cs:DispatchAsync` selects connected, enabled agents by
  advertised executor, required labels and `MaxConcurrency`, and separately
  enforces target-host concurrent-operation limits. Apply prefers the planning
  agent, but preference is **not** hard affinity. `ToolRuntime` is recorded but
  is not an enforced isolation policy in that candidate filter.
- `agent/.../paths.rs`, `identity.rs`, `outbox.rs` and `attempts/` own the
  worker's persistent identity, paired renewal files, workspace journals,
  unacknowledged results and provider cache. `connection.rs` reconnects,
  advertises running attempts, replays results and removes them on `ResultAck`.
  Its process shutdown drains locally; there is no persisted controller-side
  `Draining` admission state or managed guest relationship today.
- Disabling through `MapAgents` disconnects the channel; it does not prove
  child processes stopped. Deleting an Agent record is not VM removal and is
  guarded only against assigned/running attempts, not this proposed lifecycle.

Here `agent/...` means `agent/crates/octopushy/src`; other backend short paths
above are under `backend/src/Octopushy.Server`.

### NixOS and native guest operations

- `examples/lab/modules/nixos/flake.nix` and `flake.lock` pin nixpkgs and export
  the preparation-only `bootstrap-image`. `image.nix` is an 8 GiB EFI/Q35,
  NoCloud bootstrap; `proxmox-guest.nix` supplies VirtIO/QGA, firewall and
  key-only administrator access. The README explicitly says there is no
  integrated Nix executor. `service-node.nix` is a Docker service-host role,
  not the right worker base.
- `agent/packaging/octopushy-agent.service` is an example service, explicitly
  not real-host tested. Reuse its dedicated user, private `StateDirectory`,
  rootless Podman prerequisites and stop semantics when expressing the NixOS
  module; do not claim the example proves they work on NixOS.
- `agent/crates/proxmox/src/client.rs:guest_exec` already sends an argument
  array through PVE and polls `guest_exec_status`; POST is not retried.
  `executors/nixos_rootfs.rs` demonstrates a bounded native action, guest
  ownership checks and uncertainty on a lost submission/status response.
  It is not a general bootstrap executor. Add a separate fixed worker action
  following that pattern; do not overload filesystem growth.
- `executors/proxmox.rs:verify_absent` checks audit authority before accepting
  an unlisted VM as absent. It proves VM absence, not secure disk erasure,
  disappearance of every volume/import, or absence of backups.

## 3. Minimal Git representation and controller binding

Extend the guest schema/loader with one optional, strictly validated `worker`
object. Presence declares the role; it is not inferred from a profile string.
The following is **proposed syntax, rejected by today's schema**:

```yaml
# labs/<lab>/proxmox/<installation>/guests/<worker>/guest.yaml
schemaVersion: 1
type: vm
lifecycle: active
host: pve-a
vmid: 901                    # explicitly reserved within this installation
template: octopushy-worker-nixos-v1
hardware:
  cores: 2
  memory: 2048               # MiB; initial assumption, not a measured minimum
  disks:
    - { size: 8 }           # GiB, boot/OS disk
    - { size: 16 }          # fresh per-instance private state/runtime disk
network:
  bridge: vmbr0
  mode: dhcp
provision:
  recipe: managed-worker-vm
  state: { backend: lab-state }
execution:
  agentLabels: [control-worker, site-a]
worker:
  labels: [site-a, pve-a]
  maxConcurrency: 1
policy:
  approval: manual
  destructive: manual
```

`execution.agentLabels` selects the **existing control worker** for operations
on this VM. `worker.labels` describes the **new worker's** eligible workload
scope. Never bootstrap by requiring a label that only the not-yet-created
worker can supply. Labels are routing hints, not proof of authorization or
network reachability. The current API's label/concurrency editors must not
silently become a second desired-state owner for managed workers: make them
read-only for those records and direct changes to the definition.

Keep the existing `template.yaml` URL/checksum/file-name representation for a
published worker image; do not implement `source.build` orchestration as part
of this slice. Add a small worker-specific OpenTofu recipe/module based on the
current VM module, with the explicit two-disk layout and a public generation
ownership marker. Do not change the meaning of every ordinary guest.

Add one durable managed-worker binding table in the controller database:

- Resource GUID, generation GUID, repository-source/revision/run provenance;
- installation identity/address, expected host and VMID, exact image digest;
- bootstrap reservation/challenge ID, CSR public-key hash, issued public
  certificate/Agent ID, phase and latest evidence/error timestamps;
- retirement run/evidence references and terminal tombstone.

Enforce at most one current generation per resource and one binding per Agent
ID; preserve completed generations. Allocate the generation once before its
first plan, not on every retry. Inject its public marker into the worker
recipe's tool variables and PVE description/tags, alongside the existing
address marker. Bindings use Resource/Agent GUIDs, never hostname matching,
an IP address, labels, or bare VMID across installations.

Git owns **intent**. OpenTofu state, built in or in an external backend, owns
resource bindings and locking. The controller DB owns identity correlation, run/approval/evidence and
observed phase. The worker state disk owns its private identity and execution
journals. No one owns the same VM in two OpenTofu states.

Keep worker lifecycle inputs in an explicit run-context/hash contract.
`ProvisionInputs` currently hashes unknown guest fields unless excluded, but
does not forward them as tool variables. Do not accidentally send worker
identity/configuration through Terraform variables or trigger reenrollment on
a label-only change. Role removal, generation replacement, host/VMID changes
and disk replacement must be explicit guarded operations; reject them in the
initial slice. Restore a missing definition to retire it; retain the retired
manifest until verified cleanup.

## 4. Bootstrap authority without secret-bearing images

Choose a **fixed, public-CSR exchange over authenticated PVE/QGA**, not a token
embedded in cloud-init. This is a small new managed-enrollment path, not
something the present CLI/API already supports:

1. After the VM plan is approved, applied and verified, the controller reserves
   a managed enrollment for the exact resource/generation, image, installation,
   VMID and approved bootstrap manifest. The reservation has a public challenge
   ID and short expiry; possession of that ID alone grants no enrollment right.
2. A different, trusted control worker receives a typed native
   `managed-worker/prepare-identity` assignment. Immediately before QGA, it
   verifies the bound VM's name, address and generation markers, placement,
   guest type and expected image/bootstrap protocol. It invokes only the
   image's fixed helper, with bounded public arguments, not repository shell.
3. The helper refuses an existing different generation/enrollment, generates
   an ECDSA key **inside the guest under the agent user**, persists it privately
   on the state disk, and returns only its CSR, generation/challenge and public
   status. A repeat for the same reservation returns the same CSR, not a new key.
4. The controller accepts that result only from the assigned control-worker
   attempt, against the pending reservation, approved destination and expected
   generation. A server-internal issuance operation reuses the certificate
   authority and Agent-record creation rules of `EnrollmentService`; it
   atomically records the one-time claim, Agent ID, CSR hash and issued public
   certificate with the guest binding. Persist issuance before delivery so a
   lost result cannot create a second Agent identity.
5. A typed `managed-worker/install-identity` assignment supplies only the
   issued public certificate, public CA, expected fingerprint, controller agent
   URL and public correlation fields. The fixed helper checks the certificate
   chain/identity/validity and local public-key match, writes identity atomically
   using the existing identity layout, and starts the agent service. It never
   exports or replaces the private key on a retry.
6. The guest connects outbound through the existing mTLS channel and thereafter
   uses existing renewal/reconnect behavior. Enrollment completion is confirmed
   by the bound Agent ID, not the helper's exit status alone.

The new server-internal managed issuance is **not** an unauthenticated
“sign this CSR” endpoint. Extract only the shared validation/issuance portion
of `EnrollmentService` as needed; retain existing one-time-token enrollment for
operator-installed control workers. Managed issuance must have its own atomic
reservation/replay/concurrency tests; the current token claim does not confer
these new guarantees. Public bootstrap context comes from controller-owned
endpoint/CA configuration, not an arbitrary Git-supplied enrollment destination.

This choice avoids needing an unverified PVE stdin/secret-file API: the current
client supports command arguments, and everything sent to QGA here is public.
The guest's OS/helper and authenticated PVE administration are nevertheless
trusted; a malicious hypervisor or guest root can compromise its VM. This is
not hardware attestation.

### Trust and secret rules

- The controller CA private key and store KEK remain controller-only. No CA
  private key, mTLS private key, enrollment bearer, upstream PVE token,
  state-backend password, private SSH key or registry password enters Git,
  Nix expressions/derivations/store, image contents, cloud-init, public
  bootstrap payloads, plan inputs, outputs or logs.
- Never generate an mTLS private key in a Nix build or clone an enrolled image.
  Public CA certificates, fingerprints, CSRs, certificates and ownership IDs
  may cross the bootstrap path; validate them as untrusted structured input.
- PVE RO/writer and backend secrets continue through the existing version-pinned
  `CredentialBindings`/`VariableStore` and per-assignment secret map. They go
  only to the authorized control worker/tool, not into the worker image or its
  bootstrap environment. The new worker receives workload credentials later,
  only for assignments it is allowed to execute.
- OpenTofu state and saved artifacts remain sensitive operational data outside
  Git/Nix; using environment credentials does not prove all provider state is
  secret-free. Keep the existing storage/encryption/recovery boundaries.
- Do not introduce private SSH bootstrap access. Optional console/key-only
  break-glass access is separately authorized; its public key may be configured,
  but discovery of an IP or SSH key is not authentication.

## 5. Reproducible NixOS artifact, mutable instance state

Extend the existing NixOS preparation with `worker.nix`, `worker-image.nix` and
an explicit worker-image flake output. Reuse the pinned nixpkgs lock, QEMU
support, firewall and image layout. Package the exact reviewed agent source and
Cargo lock into a Nix derivation, then supply that package to the module; do
not download “latest” on first boot. Image build/publish is an explicit release
step, not performed by a target worker or by the controller web process.
There is no worker package/image publication pipeline today; implementing and
checking that package expression is part of this work, not a prerequisite
already satisfied by `bootstrap-image`.

The role contains only the agent, fixed bootstrap helper, QGA, systemd, rootless
Podman and its required subordinate-ID/storage/network setup. Do not import the
Docker service-host role, unrelated services, development tools or a Nix remote
builder. Pin the approved tool OCI image by digest and review provider/module
inputs separately. A reproducible OS image alone does not pin runtime downloads
or establish safe execution.

Assume initially x86_64, EFI/Q35, 2 vCPU, 2 GiB RAM, 8 GiB boot and 16 GiB state,
one concurrent attempt. These are conservative starting configuration choices,
**not benchmarked limits or guarantees**; provider caches/image pulls can fill
16 GiB, and larger plans may need more memory. Bound log/cache retention, expose
disk pressure, and refuse new work rather than removing unacknowledged evidence.
No automatic resizing or placement solver in v1.

The artifact is immutable/versioned; a VM's boot disk is not necessarily mounted
read-only. Keep mutable `/etc`/machine identity behavior instance-specific.
Use a separately attached, fresh state disk for
`/var/lib/octopushy-agent`: `agent.json`, `key.pem`, `cert.pem`, `ca.pem`,
`*.next`, work journals, outbox and cache, including rootless runtime storage.
Mount it before enrollment/service start, under the dedicated user, mode 0700;
private keys are 0600. Never fall back to writing identity onto the boot disk
when the state mount is absent.

The worker recipe and image must agree on the exact state-device identity.
Any one-time formatter must accept only the declared fresh device, fail closed
on unexpected signatures/layout, and never guess “the next empty disk.” Public
generation metadata can be initialized on it at bootstrap. Reset machine IDs
and host keys in the reusable artifact; don't copy them from a running worker.
Prove two instances produce distinct machine and mTLS identities.

Initially image changes require a separately approved replacement, with the old
generation drained/disabled before a fresh generation gets a fresh state disk
and key. No in-place NixOS deployment engine, automatic boot-disk swap or
software self-updater. Preserving the existing disk is allowed only for recovery
of that **same** generation, never to clone capacity. Never run restored copies
of one identity concurrently.

## 6. State flow and recovery boundaries

Lifecycle phases are persisted controller workflow state, using the existing
Run/Step/Attempt engine; they are not a second polling scheduler. Expose
waiting/failed/uncertain reasons alongside the phase.

```text
Deploy requested (Git intent, validated and exact revision trusted)
  → Deploying: RO plan → explicit approval → privileged apply → RO VM verify
  → Bootstrapping: prepare guest-local identity / retrieve public CSR
  → Enrolling: issue bound identity / install public certificate / mTLS connect
  → Ready
  → Draining
  → Disabled
  → Retiring: unprotect → apply saved destroy plan
  → Verifying absent
  → Retired (VM absence verified; retain identity/run tombstone)
```

**Before Draining**, removal requires trusted `lifecycle: retired` intent, a
successful RO destroy preview and explicit approval of both destruction and
the drain/disable manifest. Do not disrupt a working agent just to discover the
preview is forbidden by its RO credentials. The saved approval covers the exact
generation, VM/state, image/identity actions and plan digest; revalidate those
bindings before each privileged action. If the plan is stale, stop and replan/
reapprove. The existing retire run must gain these guards, not run alongside an
unguarded ordinary-guest destroy.

### Deploy, bootstrap, enroll, ready

- Preflight requires a connected compatible control worker, verified PVE TLS,
  independent RO/writer credentials, reachable image registry and any external state backend,
  approved image digest, explicit host/storage/bridge/VMID, time synchronization
  and guest-to-controller HTTPS reachability. No working prerequisite means
  **Blocked**, not optimistic success.
- Continue recording ordinary infrastructure apply/verify evidence separately
  from worker readiness. QGA network discovery proves neither enrollment nor
  worker-to-backend connectivity.
- A managed Agent starts ineligible for ordinary assignments until its binding
  reaches Ready. Ready requires the correlated current certificate/Agent ID,
  fresh authenticated Hello/heartbeat, expected compatible version, required
  advertised executors, approved non-local tool runtime and a confirmed writable
  private state mount. Do not declare Ready from VM power state or an installer
  exit code. Show per-destination reachability as separate evidence; a fresh
  heartbeat is not proof a future OpenTofu plan will succeed.
- Same-reservation retries reuse the pending key/CSR/certificate. A consumed or
  uncertain enrollment is reconciled by its binding, not solved with CLI
  `--force`, another token or another Agent row. An expired/lost identity needs
  an explicit recovery/replacement decision.

### Drain, disable, retire, verify absent

1. Persist `AgentRecord.Draining` and exclude it from dispatch under the same
   `RunEngine` serialization boundary used to admit assignments. Include
   assignments already admitted but not yet delivered in the outstanding set.
   A reconnect does not clear Draining. Keep its channel enabled so evidence can
   flush.
2. Wait for all assigned/running attempts, durable results/ack cleanup and every
   unresolved mutating uncertainty belonging to that agent, across **all**
   resources it serves. Do not equate a count of zero running processes with a
   drained outbox. Do not cancel an apply or delete evidence to speed this up.
3. Through the independent control worker, use a fixed
   `managed-worker/quiesce` helper to stop the guest service after those attempts
   settle and return structured evidence: service stopped, no executing child
   work or unresolved journal, and empty durable outbox. Reuse process drain/
   stop behavior but do not parse the human-readable `agent status` output as a
   protocol. An outbox that could not flush keeps removal blocked.
4. Mark the bound Agent Disabled, disconnect it and deny future renewal/
   connection. Retain its record and audit history. All certificate serials
   associated with this record must remain unusable; don't re-enable it as a
   side effect of a delayed Hello or bootstrap result.
5. An eligible **different** control worker executes the approved unprotect and
   saved destroy apply, then the existing RO `verify-absent`. Recheck ownership/
   generation before destructive mutation. Reject drift or VMID reuse instead
   of removing whatever now occupies the number.
6. Retire only after authorized absence evidence. An unreachable API, a 403,
   stale inventory or lack of visibility is not absence. Preserve run,
   certificate/binding and external-state history; a disabled Agent may safely
   remain recorded after its VM is gone. Record the exact cleanup scope:
   current `verify-absent` proves the VM is absent, not volume erasure. Track
   guest-owned boot/EFI/state/cloud-init volumes and imported image, and surface
   orphan/cleanup uncertainty separately. Full “removed” acceptance must check
   those owned artifacts too, with adequate RO authority; never delete shared
   templates or claim backups were erased.

Use a persisted exclusion of the target Agent ID for **all** attempts operating
on its worker guest, including preview, bootstrap, unprotect, destroy and
verification; propagate the exclusion to retries and fallback selection.
Resolve/check the current generation's binding again at dispatch and assignment
construction, including runs created before enrollment assigned that Agent ID.
Otherwise current apply preference can choose the worker being destroyed.
An eligible alternate disappearing blocks teardown, never relaxes exclusion.

### Disconnects and uncertain outcomes

The existing worker continues attempts across a channel drop, and the controller
can expire a mutation lease into Uncertain. Local restart recovery similarly
preserves uncertain mutations and replays its outbox. Preserve those semantics:
network loss is not cancellation, service disable is not process termination,
and an expired lease is not permission to destroy the worker or retry a write.

Classify new identity preparation/install/quiesce mutations in both controller
and worker mutation tables. Lost QGA POST/status responses are uncertain; a
bounded read of the matching reservation/generation may reconcile them, but
never blindly replay an uncorrelated mutation. Make the fixed helper operations
idempotent for their own reservation without promising exactly-once delivery.
An interrupted teardown retains Draining/Disabled and its evidence; controller
restart must not reopen admission or allocate a new generation.
Recovery needs a matched controller DB/CA/KEK/artifact backup, the external
OpenTofu state and that generation's worker disk/journals, following
[the recovery checklist](recovery-rehearsal.md). A CA-valid certificate alone
cannot repair a restored database's missing binding or registered serial.

No “force remove” shortcut in v1. If an offline guest cannot prove it has stopped
work or flushed its outbox, show Recovery required. An operator's exceptional
decision to abandon evidence or fence a machine is outside this happy path and
must not be presented as a successful normal drain.

## 7. The first control worker and placement assumptions

The controller cannot provision its first execution worker by dispatching work
to that nonexistent worker. Initially require one operator-installed,
independently enrolled control worker, with its existing one-time-token process,
under a dedicated identity and rootless runtime. Prefer a separate trusted VM/
machine, outside the guest it manages and outside the controller's secrets/data.

If hosted on the controller's physical machine, it still needs a separate user,
state and rootless runtime; that shares a kernel and is a weaker boundary than
a separate VM. Do not add container-runtime sockets, repository executors or
PVE tooling to the web/backend container. The proposed deployment supervisor is
not a worker: `agent/cli.rs` does not implement supervisor mode, and the
architecture agreement explicitly assigns it no tool work.

One control worker can serve multiple installations if explicitly authorized
and network-reachable, with independently scoped credentials per installation.
An isolated site needs an independently bootstrapped control worker there.
Keep that anchor outside managed-worker removal in v1; removing the last worker
capable of managing a site is blocked. No automatic cross-site placement,
cluster-wide VMID allocation, HA migration handling or autoscaling. Placement
uses explicit host, datastores, bridge, addressing and reserved VMID already in
the repository; worker concurrency and Proxmox host limits remain separate.

## 8. Safety gates: why this is not one-click deployment today

The controlling baseline is [Proxmox credential roles](proxmox-credentials.md),
not the older aspirational deployment descriptions.

1. **Live provider compatibility blocker.** The example pins bpg/proxmox
   **0.117.0**. Refresh of an existing VM's ordinary/EFI disk when format is
   omitted can request metadata requiring `Datastore.Audit` plus
   `VM.Config.Disk`, or broad `Datastore.Allocate`. Fresh-create preview does
   not establish strict-RO update/destroy compatibility. Resolve and separately
   prove that path before any full lifecycle rehearsal. Do not give mutation
   rights to the RO role, retry with writer credentials or turn refresh off.
   A provider/module repair must also be checked for this worker's EFI and
   extra state disk. The pinned-source analysis is in
   [research/proxmox-credential-split.md](research/proxmox-credential-split.md).
2. **Credential routing is necessary, not sufficient.** Reuse
   `AssignmentBuilder.TokenFor`, `SecretsFor`, `ApprovedPlanAsync` and
   `CredentialBindings`: RO for plan/observe/VM verify/absence; writer only for
   approved apply, unprotect and privileged QGA bootstrap. Rootfs verify already
   needs writer authority despite its non-mutating retry classification.
   Treat helper status/quiesce via QGA exec the same way; don't disguise command
   execution as RO. Extend routing explicitly for new step names, rather than
   letting the current default select RO accidentally.
3. **Controller-owned approval floor remains missing.** Today's automatic Git
   policy may approve privileged work. Initial managed-worker deployment and
   removal require controller-enforced human approval independent of repository
   policy, bound to the saved plan and typed bootstrap/drain manifest. Native
   work after an infrastructure no-op still requires that approval/artifact;
   generalize the existing rootfs no-op handling, do not skip identity changes.
   Revision execution trust and action approval are separate decisions.
4. **Purpose/destination restrictions remain missing.** Distinct store keys
   cannot prove distinct upstream privileges. Bind approved credential purposes
   and versions to this installation, verified endpoint and intended actions;
   do not permit a repository endpoint change, backend variable alias or redirect
   to release a PVE identity to another destination. Keep the managed bootstrap
   action native and controller-owned, not user-authored code with access to
   enrollment signing. Current reserved-environment checks are not this policy.
5. **Transport/runtime safeguards remain incomplete.** Prove controller UI/API
   HTTPS, agent endpoint/SAN/CA pinning and renewal, verified PVE HTTPS/custom
   CA, state-backend TLS and tool-container CA availability without skip-verify
   fallbacks. The example installation uses `tls.verify: false`; it is not an
   acceptable managed-worker deployment default. Enforce approved rootless
   runtime/digest in dispatch/assignment construction, since current dispatch
   does not reject `local`. Revision trust alone does not sandbox code or pin
   all downloaded dependencies.

Exact revision trust for the current repository source, approved plan identity,
least-privilege upstream ACLs and protected state storage all remain mandatory.
The bootstrap writer needs guest-command authority in the chosen PVE version;
do not invent a universally sufficient ACL recipe or equate pool membership
with allocation/storage/absence permissions.

Thus “one click” can eventually mean **request a reviewed workflow from the
server**, not authorize every future operation. The UI must still stop for
missing trust, credentials, readiness, exact-plan approval and uncertainty.
Neither this design nor the PR6 green baseline is permission for retained
installation rollout or proof of remote-worker acceptance.

## 9. UI actions and visible status

Extend the existing `InstallationPage.tsx`, `GuestPage.tsx`, `AgentsPage.tsx`,
`pages/agents/AgentRow.tsx`, run timeline and approval components, rather than
building another console:

- **Deploy worker:** initially generate/download the proposed guest/template
  definition for operator commit. No hidden direct VM creation.
  Git write-back/drafts are vocabulary/planned work, not an implemented editor
  in the current repository page. A later minimal publish action must validate
  a base revision, create a real Git commit through an explicitly configured
  write identity, then follow normal sync/trust; that integration is not a
  prerequisite for proving worker lifecycle.
- **Plan/approve deployment:** use existing run links with separate blocked
  reasons and show the bootstrap manifest alongside the exact plan.
- **Drain worker:** show workload targets affected, pending/uncertain attempts,
  outbox/quiesce evidence and the alternate control worker. Standalone drain
  pauses admission; it does not silently change desired Git lifecycle.
- **Remove worker:** propose/commit `lifecycle: retired`, preview, approve, drain,
  disable and retire. Label it “Request removal,” not “Delete agent.”
- **Recover/inspect:** link the pinned revision, certificate, attempt journal/
  outbox evidence and uncertain native operation. No blind mutation retry.

Show both guest and agent identity, installation/host/VMID, generation, image/
agent versions, Git desired/applied/checked revisions, controller-observed
lifecycle phase, last heartbeat/certificate expiry, effective labels/runtime/
capacity, blocked reason, active run and last verification timestamp/scope.
“Offline” is connectivity; “Disabled” is admission/authentication; “VM absent”
is separately verified infrastructure. Never collapse them into one green icon.

API/DTO/OpenAPI and frontend generated types must evolve together. Existing
generic agent deletion/reenable and guest planning endpoints must enforce the
managed binding/drain invariants too; hiding unsafe buttons is insufficient.
No private token/key preview, secret-bearing install command or secret copy
button is needed for the managed CSR bootstrap.

## 10. Ordered implementation slices and test seams

### Next slice: relationship and teardown safety, offline only

Implement the typed `worker` definition, its validation, DB binding/generation,
persisted Draining admission, managed-agent mutation guards and hard
self-target exclusion. Cover explicit/automatic planning, dispatch, retries and
controller restart through existing fake agents. Show correlated
“Managed worker — not deployed / blocked” read models; do not enable real
bootstrap/retirement until subsequent gates exist.

This first slice should demonstrate: the target worker can never receive its
own teardown, Draining survives reconnect, disable is not treated as drained,
unresolved mutations/outbox requirements prevent removal, and ordinary guests
and operator-enrolled control workers keep their current behavior.

### Then, in dependency order

1. Resolve the provider/module strict-RO refresh issue in a separate compatibility
   slice and add the controller approval floor plus purpose/destination/runtime/
   transport gates above. Synthetic coverage alone does not clear the live gate.
2. Add the locked agent package, minimal worker image/module and two-disk recipe.
   Check empty-image identity, private mount/service ordering and rootless tool
   execution before connecting a worker to privileged credentials.
3. Implement reservation-bound CSR prepare/issue/install with fixed native
   actions, helper validation, Ready gating and replay recovery. No generic
   executor input or new shell recipe surface.
4. Extend the existing retire workflow with reviewed drain/quiesce/disable,
   hard exclusion, ownership rechecks and clearly scoped cleanup evidence.
   Persist partial outcomes; do not create a parallel retirement state owner.
5. Wire the visible actions/status, initially using operator Git commits.
   Add Git write-back only as a separately authorized publish slice.
6. Only after explicit approval, rehearse two fresh disposable worker guests on
   one qualified installation using an independent control worker. Demonstrate
   distinct identities, renewal/reconnect, real work/outbox acknowledgement,
   denied RO preview without writer release, drain, disable, external teardown,
   VM/owned-artifact absence and retained audit evidence. Qualify each additional
   installation/version/storage/network independently.

### Reuse these test seams

- Definitions: `backend/tests/Octopushy.Tests/Definitions/`
  (`LabRepositoryLoaderTests`, `PolicyAndInputsTests`, `SplitCredentialTests`):
  worker schema/labels, independent control-worker selectors, role changes,
  generation-input stability and no private bootstrap data in tool variables.
- Server: `ServerHarness`, `ProvisioningTests`, `RecoveryTests`, `ApiTests`,
  `GuestFilesystemRunTests`, `SplitCredentialTests`, `TrustedRevisionTests` and
  `DispatchTrustBacklogTests`: no-op approval, RO/writer release, exact source/
  revision, self-target exclusion including retry construction, last-control-
  worker block, durable drain, late results and restart/uncertain safety.
- Identity: existing `agent/crates/octopushy/tests/grpc_e2e.rs` enrollment,
  wrong-fingerprint, renewal, reconnect and outbox replay tests; extend with
  managed binding, wrong CSR/generation/destination, concurrent claims, lost
  issuance/install replies and disabled-identity replay.
- Native API/helper: `agent/crates/proxmox/tests/{wire,against_mock}.rs`,
  `agent/crates/octopushy/tests/{proxmox_executor,nixos_rootfs}.rs` and
  attempt-recovery tests. Assert fixed command shape, ownership refusal, no
  private values in requests/logs/artifacts, no POST retry, and explicit
  mutation classification for new actions.
- Nix/image: extend the preparation flake's policy checks; later boot two
  disposable image instances to check fresh identities, wrong/missing state
  disk refusal, service stop/outbox evidence and required Podman capabilities.
  Evaluation assertions alone are not a boot or rootless-runtime test.
- Frontend: existing API/query/status/run-page test patterns for blocked,
  draining, disabled, absent and uncertain states; ensure API guards reject
  unsafe actions even when invoked outside the UI.
- Live evidence: `tests/remote-worker/README.md` currently requires an already
  installed/enrolled worker. Reuse its retention and explicit-approval rules,
  but do not cite it as an existing deployment/bootstrap/removal harness.

**Acceptance boundary:** this file specifies one bounded implementation path.
It supplies no executable lifecycle, live ACL/transport validation, image build
result, deployment permission or retained-environment acceptance.
