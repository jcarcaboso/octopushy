# Proxmox read-only / privileged credential split

Research date: **2026-10-23**. Bounded source inspection for T2; **not a live
credential-sufficiency result**. No lab infrastructure requests, SSH, lab DNS
queries, private state/secret reads, installs, containers, or repository payload
execution were performed. Only this note was written.

## Decision-relevant findings

1. **GET-only does not imply read-only privileges.** The locked provider's VM
   refresh reads disk/EFI volume metadata when the VM config omits disk format.
   Proxmox gates that GET with **`Datastore.Audit` plus `VM.Config.Disk` on the
   owning VM**, or the broader **`Datastore.Allocate`** on storage. This can
   block ordinary update and destroy previews with a genuinely read-only
   token. Both branches are verified in source; whether a particular host's
   returned configuration takes the branch is **not live-verified**.
   [B-disk], [B-vm], [B-storage], [P-content], [P-volume]
2. **Do not solve that blocker by silently adding mutation privileges to the
   planner.** No general write-privilege precheck was found in the configured
   provider's configure/create-plan paths. A fresh create preview and a refresh
   of an existing VM have materially different permission requirements.
   [B-config], [B-fwconfig], [B-vm], [B-download]
3. **Native `nixos-rootfs.verify` must use an execution-capable privileged
   credential.** It POSTs guest-agent `exec`, running `flock` plus Bash as guest
   root, and GETs `exec-status`, just like `grow`. Its non-mutating retry classification
   describes intended effects, not the API capability it needs. See
   `agent/crates/octopushy/src/executors/nixos_rootfs.rs:30-100`,
   `nixos-rootfs.sh`, and `executors/mod.rs:26-30`. [P-agent], [P-agent-old]
4. **Some refresh failures only degrade the plan.** URL-metadata rejection and
   agent network-lookup rejection become warnings in this provider pin; missing
   disk-format permission becomes an error. A successfully generated plan is
   therefore not proof that every observation succeeded. [B-download],
   [B-network], [B-disk], [B-vm]
5. **“Read-only Proxmox preview” is not “read-only backend access.”** The current
   preview initializes its backend, selects/creates a workspace, and uses normal
   plan locking. PostgreSQL backend initialization/workspace creation can write
   independently of Proxmox. [O-backend], [O-state], [O-client]

## Evidence and exact pins

- Local baseline: **`13b953897bf20207eb61aa8dee7f4f89b45543cd`**. The inspected
  `examples/lab/modules/opentofu/proxmox-vm/.terraform.lock.hcl` selects
  **bpg/proxmox `0.117.0`**, with constraint `~> 0.117.0`.
- Upstream tag **`v0.117.0`** resolves to commit
  **`fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5`**. All B references below use that
  commit, not the provider's moving documentation site. [B-tag]
  The short `proxmox_download_file` resource embeds the traced download-resource
  implementation. [B-short]
- Proxmox API definitions are pinned official source snapshots: pve-manager
  **`f643ce4bceb99e8b23094cd04075405281eb3f27`**, qemu-server
  **`71ea0ac7192bb935f63cb64beb59e87d6cecd3bf`**, pve-storage
  **`75b36a49f57fa7119a45bb69118c8c6eb58102d7`**, pve-access-control
  **`dff26b458651b4e1141ff0a41a296d2904b16228`**, pve-docs
  **`c673ee6c62ec2bf4e33a05fcd1a0f0a29b18d2f7`**, and pve-guest-common
  **`a9e15736e4a4d3831d02e8e6ecef57a76fdcf133`**. These are **not claims about
  installed lab package versions**. Legacy source references below establish
  that older permission models differ; they are not an exhaustive release
  compatibility matrix.
- The server default OpenTofu image is `ghcr.io/opentofu/opentofu:1.13.1`
  (`backend/src/Octopushy.Server/Options/OctopushyOptions.cs`). Upstream
  **`v1.13.1`** resolves through an annotated tag to commit
  **`233700b795b6d2372f1c56ed57a4abaec9d36475`**; O references use that commit.
  A configurable runtime/image can change this assumption. [O-tag]

**Verified** below means inspected local/upstream code or an official permission
definition. **Inference/candidate** means an engineering conclusion from that
evidence, not a tested token, ACL, host response, or provider invocation.

## Actual module and workflow phases

The module configures `provider "proxmox" {}` via environment, a per-guest
`proxmox_download_file` with `content_type = "import"`, checksum and explicit
filename, and the SDK `proxmox_virtual_environment_vm`. The VM has an EFI disk,
imported system disk, optional data disks, enabled guest agent, cloud-init,
bridge/VLAN, explicit VMID, protection, and start/stop lifecycle settings. It
does **not** configure `pool_id` or the newer `vm2` resource. Sources:
`examples/lab/modules/opentofu/proxmox-vm/{main.tf,versions.tf}`.

| Phase | Verified source behavior | Credential implication |
| --- | --- | --- |
| Provider configuration | SDK/framework configure construct API/SSH client objects; token client construction selects an authenticator. The traced token path has no cluster-wide `VM.Allocate`/other write-privilege audit or eager API permission precheck. Creating an SSH client object is not evidence that this module needs an SSH login during preview. [B-config], [B-fwconfig], [B-client] | Configure succeeding does not prove token validity or later API access. No reason found here to give preview an apply credential. |
| Fresh create plan, with neither resource in state | VM diff hooks validate/compare configuration; download size plan modifier explicitly returns for new state. URL metadata/download occur in download **Create**, not fresh plan validation. Explicit VMID avoids needing to allocate an ID during creation, and allocation itself is not a plan operation. [B-vm], [B-network-schema], [B-download] | No provider-side mutating API/privilege prerequisite found for this fresh-create case. This does not cover preexisting state, imports, other modules/data sources, or PG initialization. |
| Existing resource refresh / update preview | VM read discovers node via cluster resources, reads VM config/current status, reads disks/EFI metadata if format omitted, discovers pool if needed, and performs enabled/running QGA network lookup. Download read lists `import` content and, with default `overwrite=true`, queries URL metadata. [B-vm], [B-disk], [B-storage], [B-network], [B-download] | Core VM/storage audit reads are insufficient on the disk-format branch. QGA and URL metadata have additional capability checks and warning degradation described below. |
| Destroy preview | Executor adds `-destroy`, but does **not** disable refresh or locking. Download size modifier skips destroy, but resource refresh still has the VM/storage/agent checks above. Sources: `agent/crates/octopushy/src/executors/opentofu/mod.rs:377-437`; [B-download], [B-vm]. | Not equivalent to apply/destroy permissions, but not guaranteed possible with audit-only privileges either. It does not itself lift VM protection. |
| Apply / retire | Download create calls URL metadata then POST `download-url`; delete calls DELETE storage content. VM create/update/power/delete callbacks are separate from diff/read. Native retire protection toggle PUTs VM config. [B-download], [B-vm], [B-storage], [P-qemu], [P-download], [P-content] | These belong to privileged assignment. Saved-plan execution still configures the provider using that attempt's credential. |

### Hard conditional blocker: disk and EFI format reads

The exact chain is:

1. `disk.Read`, `disk/disk.go:426-442`, calls `GetDatastoreFile` when
   `dd.Format == nil`; `vmReadCustom`, `vm.go:4759-4784`, does the same for an
   EFI disk whose `Format` is absent. Non-not-found errors become diagnostics
   errors. The configured module has both kinds of disks. [B-disk], [B-vm]
2. `GetDatastoreFile`, `content.go:118-145`, sends
   `GET /nodes/{node}/storage/{storage}/content/{volume}`. [B-storage]
3. Proxmox `Content.pm`'s `info` method calls
   `check_volume_access(..., undef, $volid)`: it does **not** pass the owning VMID.
   `Storage.pm:630-646` permits `Datastore.Allocate` as an override; otherwise an
   `images`/`rootdir` volume requires `Datastore.Audit` on storage **and
   `VM.Config.Disk` on its owner VM**. `VM.Audit` does not substitute.
   The same behavior exists in the examined 2024 storage snapshot.
   [P-content], [P-volume], [P-volume-old], [P-content-old]

**Inference:** storage-default formats omitted by Proxmox, particularly raw
volumes on a storage such as the module's fallback `local-lvm`, are a realistic
trigger. The provider explicitly comments on this omission, but the actual lab
response was not inspected. Setting a desired `file_format` is **not established
as a fix**: this branch tests the **API response's** format, not simply desired
configuration. Fresh-create success therefore cannot certify subsequent refresh.
[B-disk], [B-vm]

Policy consequence: keep the planner fail-closed on these errors; classify this
as a documented compatibility limitation. If full preview is mandatory, it needs
an explicitly approved upstream/provider workaround or an explicitly privileged
preview mode—not an RO token with hidden `VM.Config.Disk`/`Datastore.Allocate`,
automatic credential fallback, or `-refresh=false` disguised as equivalent
verification.

### Read-side checks that do not require automatic escalation

- **Downloaded image refresh:** listing import content uses storage audit
  permission; the endpoint accepts `Datastore.Audit` or `Datastore.AllocateSpace`.
  Existing image refresh additionally requests
  `GET /nodes/{node}/query-url-metadata` by default. Its examined API permission
  expression is **both `Sys.Audit` and `Sys.Modify` on `/`, OR
  `Sys.AccessNetwork` on `/nodes/{node}`**. It is a GET that lets the host probe a
  supplied URL, not a pure cached observation. [B-download], [B-query],
  [P-content], [P-query]
- In this provider pin, failed URL metadata refresh sets private `url_size=-1`;
  the plan modifier emits a warning, rather than requiring a successful
  download/`Datastore.AllocateTemplate` precheck during planning. New-resource
  metadata lookup is in **Create**. The current module does not override the
  default `overwrite=true`. Do not grant global `Sys.Modify` just to eliminate
  that warning. `overwrite=false` would alter upstream-change detection and is
  not an unqualified credential-only fix. [B-download]
- **Important failure mode:** download read turns listing errors other than its
  special `"failed to authenticate"` case into a warning plus resource removal
  from refreshed state. Insufficient storage access can thus resemble a missing
  file and produce a misleading replacement/create preview. Permission preflight
  and warning review matter even if the command returns success. [B-download]
- **QGA network lookup:** enabled/running VM refresh calls
  `GET .../agent/network-get-interfaces`. The examined new API model accepts
  `VM.GuestAgent.Audit` (or broader `VM.GuestAgent.Unrestricted`) at
  `/vms/{vmid}`; the examined legacy model uses **`VM.Monitor`**, which also
  authorizes agent execution and file writes. Do not call that legacy grant
  read-only. The pin stops retrying HTTP 403 and converts the lookup error to a
  **warning**, leaving address outputs unavailable. Native `verify-guest` is a
  different path and does not inherit this provider warning behavior.
  [B-network], [B-agentwait], [P-agent], [P-agent-old]

## Native action / API / permission map

Verified local call sites are
`agent/crates/proxmox/src/client.rs:203-364`,
`agent/crates/octopushy/src/executors/proxmox.rs`, and
`agent/crates/octopushy/src/executors/nixos_rootfs.rs`.
All paths below are relative to `/api2/json`.

| Action | Requests | Required boundary in examined Proxmox source |
| --- | --- | --- |
| `proxmox.observe` | GET `/version`, `/cluster/status`, `/nodes`, `/cluster/resources?type=vm` | Version/node indexes are authenticated-user reads; cluster status needs `Sys.Audit` on **`/`**. Node metrics are filtered according to node `Sys.Audit`; VM entries according to per-VM `VM.Audit`. Inventory is not necessarily complete. [P-version], [P-cluster], [P-nodes] |
| `proxmox.verify-guest` | GET `/access/permissions?path=/vms/{vmid}`, cluster VM resources, VM current status, QGA network interfaces | Requires effective `VM.Audit` at the VMID before treating absence as meaningful; status uses `VM.Audit`. Network verification needs new-model QGA audit, or legacy execution-capable `VM.Monitor`. [P-permissions], [P-cluster], [P-qemu], [P-agent], [P-agent-old] |
| `proxmox.verify-absent` | GET own permissions for VMID, GET cluster VM resources | `VM.Audit` must remain effective at that VMID even when it no longer exists. Pool membership alone is not durable evidence of absence. [P-permissions], [P-cluster], [P-acl] |
| `proxmox.set-protection` | Own-permission/VM discovery reads; GET config; PUT config `protection=...`; GET config | Reads require VM audit; `protection` is a general option, hence `VM.Config.Options`. Explicitly privileged even when desired protection already matches and no PUT occurs. [P-qemu] |
| `nixos-rootfs.verify` and `.grow` | GET VM config to validate identity/ownership; POST `.../agent/exec`; GET `.../agent/exec-status` | `VM.Audit` plus new-model `VM.GuestAgent.Unrestricted`, or legacy `VM.Monitor`. `VM.GuestAgent.Audit` and `.FileSystemMgmt` alone cannot execute this shell. [P-qemu], [P-agent], [P-agent-old] |

The rootfs verify shell checks NixOS, root UID, ext4, disk layout/size and
capacity; only `grow` restarts `growpart.service` and
`systemd-growfs-root.service`. That intended difference does not constrain the
authority of its shared QGA exec credential. The current `is_mutating` matcher
must not be used as a read-only credential selector.

## Bounded candidate privileges and scopes

These are **candidates for operator validation**, not ready-to-run ACL commands
or assertions of sufficiency. Keep privilege separation enabled and assess the
**effective intersection of token and owning user's ACLs**; full-privilege tokens
inherit the user's authority. Object ACLs can propagate, deeper ACLs can replace
inherited grants, and `NoAccess` can cancel grants. Use explicit VMID/storage/node
paths rather than a broad administrative role. [P-acl]

### A. Strict audit credential

- `Sys.Audit` on `/` for the **current observe implementation's cluster-status
  request**. Consider a non-propagating root grant plus explicit node audit
  grants if the operator wants to limit node metric/config visibility; do not
  claim node-only/pool-only audit satisfies the root check. [P-cluster], [P-nodes]
- `VM.Audit` on each managed `/vms/{vmid}`, including reserved future/deleted
  VMIDs needed for absence verification. `/vms` with propagation is simpler but
  discloses all VM inventory. [P-cluster], [P-qemu], [P-acl]
- `Datastore.Audit` on every image/disk/EFI datastore used by a guest when used
  for provider preview. This supports import listing, **not** the conditional
  VM-image format read described above. [P-content], [P-volume]
- `VM.GuestAgent.Audit` on managed VMIDs **only where the installed API supports
  the granular model**, for complete native guest verification and provider IP
  discovery. On a legacy API, do not replace it with `VM.Monitor` under the same
  “strict RO” label: native network verification is then unsupported by this
  credential; provider lookup can degrade to warnings. [P-agent], [P-agent-old]
- `Pool.Audit` on the specific relevant pools only if accurate pool discovery is
  needed. Provider read falls back to listing visible pools and reading their
  membership when config has no pool ID. An empty permitted pool list can report
  an empty pool rather than complete membership knowledge. [B-vm], [P-pools]

Exclude `VM.Config.*`, `VM.Allocate`, `VM.PowerMgmt`, `VM.Monitor`,
`VM.GuestAgent.Unrestricted`, `Datastore.Allocate*`, `Sys.Modify`,
`Permissions.Modify` and execution/file-write capabilities. Optional
`Sys.AccessNetwork` on the exact node would permit the metadata GET without
`Sys.Modify`, but is a separate network-probing capability requiring explicit
policy acceptance, **not necessary merely to make this pin emit a plan**.
[P-query], [P-acl]

**Supported claim:** native inventory/absence audit, granular-model guest
network verification, and some provider previews. **Unsupported claim:**
unconditional full provider refresh/update/destroy planning.

### B. Privileged apply / retire / guest-exec credential

Candidate union for this **particular no-clone, no-migration module**, in addition
to relevant audit reads:

- On managed `/vms/{vmid}`: `VM.Allocate`, `VM.PowerMgmt`,
  `VM.Config.CPU`, `.Memory`, `.Disk`, `.Network`, `.HWType`, `.Options`,
  `.Cloudinit`, and conservatively `.CDROM` for cloud-init CD-ROM device
  manipulation. This is a bounded union, not proof each is needed by every
  operation. `VM.Config.Options` covers protection lifting; allocation covers
  create/delete; power covers start/stop. [P-qemu], [P-acl]
- On target disk/EFI/cloud-init storage: `Datastore.AllocateSpace` plus audit.
  On image-download storage: `Datastore.AllocateTemplate` plus audit and
  **`Datastore.Allocate` for deletion of the downloaded import volume**, including
  its destroy/replacement path. The latter is a broad datastore capability:
  it is not a per-file or per-guest deletion ACL. Do not omit it merely because
  allocation/upload worked. [P-download], [P-content], [P-acl]
- On the relevant `/nodes/{node}`: `Sys.AccessNetwork` for server-side URL
  metadata/download where supported, rather than root `Sys.Modify`.
  The alternative examined permission expression requires root `Sys.Audit` and
  `Sys.Modify`; older installed packages must be checked against their own
  definitions. [P-query], [P-download]
- `SDN.Use` for the actual bridge/VLAN path. In the examined source, local
  bridges use `/sdn/zones/localnetwork/{bridge}` or its `/{vlan}` child; SDN vnets
  use their real zone instead. Scope to configured networks, not all `/sdn`.
  [P-qemu], [P-vnet]
- On VMIDs requiring native root verification/growth:
  `VM.GuestAgent.Unrestricted` in the granular model or legacy `VM.Monitor`.
  A rootfs-only privileged token could be limited to these guest-exec rights plus
  `VM.Audit`; it does not inherently need VM allocation/power/storage grants.
  [P-agent], [P-agent-old]

Do **not** add `VM.Clone`, `VM.Migrate`, `Permissions.Modify`, host console or
global `Sys.Modify` by default. New module features can change this calculation;
for example the examined API treats VM startup-order edits specially and requires
root-path `Sys.Modify`, whereas the configured module has `on_boot` but no
`startup` block and the provider returns no startup-order body for an absent
block. Host policy can also restrict tags/CPU models independently; source
inspection is not an end-to-end authorization test. [P-qemu], [B-vm]

### Why pool-only ACLs are not sufficient here

Pools propagate to **member VMs and datastores**, not arbitrary root/node/network
paths. They do not satisfy `/cluster/status`'s `/` audit check or the download
node's network-access check. Datastore grants are available through a pool only
when that datastore is actually a member. [P-acl], [P-cluster], [P-download]

The module does not set `pool_id`; its creation therefore cannot rely on the
create API's alternate `VM.Allocate` check for a supplied pool. A future VM is
not yet a pool member, and a deleted VM is no longer one: existing
`require_audit` deliberately refuses to infer absence without effective audit
permission at `/vms/{vmid}`. If pooling is added later, examine its explicit
membership-management permissions separately. [P-qemu], [P-acl];
local `main.tf` and `proxmox.rs:170-181,446-461`.

## PostgreSQL is a separate trust boundary

Local `opentofu/mod.rs:377-437` runs `init`, then
`workspace select -or-create=true`, then ordinary `plan` with a saved output;
it does not use `-lock=false` or `-refresh=false`. Its
`init -lockfile=readonly` on **apply** protects the provider dependency lockfile,
not the state lock or database against writes.

For upstream OpenTofu 1.13.1's PG backend:

- Configure can create a schema, the shared
  `public.global_states_id_seq`, the state table and an index unless the
  corresponding `skip_*_creation` flags are set. Preprovisioning and the skip
  flags can remove this DDL need. [O-backend]
- `StateMgr` checks workspace existence and, for an absent workspace, locks and
  persists an empty sentinel state; `Put` uses INSERT ... ON CONFLICT UPDATE.
  Therefore fresh preview/workspace selection can need database writes even if
  the Proxmox credential is audit-only. [O-state], [O-client]
- Locking uses PostgreSQL advisory-lock functions and table row IDs, **not a
  separate lock-table INSERT**. Existing-workspace read/lock access is distinct
  from state mutation/DDL; a proposed restricted DB role needs those function
  calls and reads assessed separately, with workspace/bootstrap already handled.
  Do not describe ordinary plan locking as proof that all PG paths need DML, or
  disabling locking as a safe solution. [O-client]

Keep PG credentials independent of the Proxmox token split. Retaining a bounded
state-backend writer for normal preview bootstrap is the minimal compatible
candidate; an additional DB read-role redesign is separate work and not proven
by this research.

## Feasible no-live checks and remaining proof boundary

Existing scaffolding, inspected but **not executed in this sidecar**:

- `agent/crates/octopushy/tests/proxmox_executor.rs` tests observe/verify/protection
  against `mock-proxmox`, including rejection when `VM.Audit` is absent.
  `agent/crates/proxmox/tests/{wire,against_mock}.rs` cover client behavior.
- `mock-proxmox/src/api.rs` has the native GET/config-PUT routes and
  `/access/permissions`; its middleware compares one configured token string.
  Its fixture exposes a privilege list, but it does **not** implement the full
  Proxmox ACL engine, per-token privilege separation, provider storage/pool/
  URL-metadata API surface, or QGA exec. A passing native mock test cannot certify
  real provider permissions.
- `agent/crates/octopushy/tests/nixos_rootfs.rs` uses a scripted Axum API.
  `verifies_real_capacity_and_keeps_mutation_and_verification_distinct` already
  asserts **one exec POST for both grow and verify**, despite verify's
  `is_mutating=false`. Extend that pattern to assert the intended credential,
  rather than treating non-mutation as RO proof.
- `opentofu_fake.rs` captures command/environment behavior with a fake executable;
  it can verify per-action credentials, unchanged backend secrets, redaction and
  no privileged-token fallback. It is not an actual bpg-provider or PG permission
  test. `backend/tests/Octopushy.Tests/Server/CredentialLifecycleTests.cs` is
  existing contract-level scaffolding for assignment secret selection.

Minimal useful preflight proposal: reuse `GET /access/permissions?path=...` to
check **effective** privileges for the selected action/VMID/datastores/root/node
and deny an RO assignment whose effective rights include disk mutation or guest
execution. No `Permissions.Modify` is needed to inspect the calling token's own
permissions. This is **proposed**, not an existing all-scope checker.
[P-permissions]

A bounded offline regression could script the absent-format VM config plus
403 volume-info response, assert preview fails, and assert no retry with the
privileged token. Separately script metadata/QGA 403 warnings, failed audit
preflight, and root verify's exec credential. An actual provider-against-mock
contract would need the missing API surface; do not expand the native mock into
a purported complete Proxmox emulator just for this split.

**Still unverified:** installed API privilege vocabulary and token support,
effective user/token ACLs and propagation, returned disk formats, all configured
datastore content/backend combinations, host tag/CPU/network policy, actual
provider refresh results, and PG grants/initialization behavior. Source evidence
establishes the conditional blockers and candidate scopes—not live sufficiency.
Any later rehearsal must be separately approved and must not “test” denied
privileges by attempting real destructive writes.

## Primary sources

All GitHub links below are upstream-owned repositories pinned to exact commits.

[B-tag]: https://api.github.com/repos/bpg/terraform-provider-proxmox/git/ref/tags/v0.117.0
[B-config]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmoxtf/provider/provider.go
[B-fwconfig]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/fwprovider/provider.go#L322-L649
[B-client]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmox/api/client.go#L138-L167
[B-download]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/fwprovider/nodes/resource_download_file.go
[B-short]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/fwprovider/nodes/resource_download_file_short.go
[B-vm]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmoxtf/resource/vm/vm.go
[B-disk]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmoxtf/resource/vm/disk/disk.go#L409-L449
[B-storage]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmox/nodes/storage/content.go
[B-query]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmox/nodes/query_url_metadata.go
[B-network]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmoxtf/resource/vm/network/network.go#L184-L257
[B-network-schema]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmoxtf/resource/vm/network/schema.go#L173-L230
[B-agentwait]: https://github.com/bpg/terraform-provider-proxmox/blob/fdd929f183fd905bc35a8c9a423fb0fa1dcfe4c5/proxmox/nodes/vms/vms.go#L588-L643
[P-version]: https://github.com/proxmox/pve-manager/blob/f643ce4bceb99e8b23094cd04075405281eb3f27/PVE/API2.pm#L85-L114
[P-cluster]: https://github.com/proxmox/pve-manager/blob/f643ce4bceb99e8b23094cd04075405281eb3f27/PVE/API2/Cluster.pm
[P-nodes]: https://github.com/proxmox/pve-manager/blob/f643ce4bceb99e8b23094cd04075405281eb3f27/PVE/API2/Nodes.pm#L2920-L3004
[P-query]: https://github.com/proxmox/pve-manager/blob/f643ce4bceb99e8b23094cd04075405281eb3f27/PVE/API2/Nodes.pm#L1905-L1917
[P-qemu]: https://github.com/proxmox/qemu-server/blob/71ea0ac7192bb935f63cb64beb59e87d6cecd3bf/src/PVE/API2/Qemu.pm
[P-agent]: https://github.com/proxmox/qemu-server/blob/71ea0ac7192bb935f63cb64beb59e87d6cecd3bf/src/PVE/API2/Qemu/Agent.pm
[P-agent-old]: https://github.com/proxmox/qemu-server/blob/7547eb37a96a648a7946c584400295eab23f594e/PVE/API2/Qemu/Agent.pm
[P-download]: https://github.com/proxmox/pve-storage/blob/75b36a49f57fa7119a45bb69118c8c6eb58102d7/src/PVE/API2/Storage/Status.pm#L737-L757
[P-content]: https://github.com/proxmox/pve-storage/blob/75b36a49f57fa7119a45bb69118c8c6eb58102d7/src/PVE/API2/Storage/Content.pm
[P-volume]: https://github.com/proxmox/pve-storage/blob/75b36a49f57fa7119a45bb69118c8c6eb58102d7/src/PVE/Storage.pm#L618-L662
[P-content-old]: https://github.com/proxmox/pve-storage/blob/39fd552d14c075a9760fcdbbecd207b8c8028731/src/PVE/API2/Storage/Content.pm#L285-L327
[P-volume-old]: https://github.com/proxmox/pve-storage/blob/39fd552d14c075a9760fcdbbecd207b8c8028731/src/PVE/Storage.pm#L528-L563
[P-permissions]: https://github.com/proxmox/pve-access-control/blob/dff26b458651b4e1141ff0a41a296d2904b16228/src/PVE/API2/AccessControl.pm#L509-L535
[P-pools]: https://github.com/proxmox/pve-manager/blob/f643ce4bceb99e8b23094cd04075405281eb3f27/PVE/API2/Pool.pm
[P-acl]: https://github.com/proxmox/pve-docs/blob/c673ee6c62ec2bf4e33a05fcd1a0f0a29b18d2f7/pveum.adoc
[P-vnet]: https://github.com/proxmox/pve-guest-common/blob/a9e15736e4a4d3831d02e8e6ecef57a76fdcf133/src/PVE/GuestHelpers.pm#L397-L420
[O-tag]: https://api.github.com/repos/opentofu/opentofu/git/tags/c512d0956f274dfbe02de09ccb7b534f16caf13e
[O-backend]: https://github.com/opentofu/opentofu/blob/233700b795b6d2372f1c56ed57a4abaec9d36475/internal/backend/remote-state/pg/backend.go#L110-L178
[O-state]: https://github.com/opentofu/opentofu/blob/233700b795b6d2372f1c56ed57a4abaec9d36475/internal/backend/remote-state/pg/backend_state.go#L60-L125
[O-client]: https://github.com/opentofu/opentofu/blob/233700b795b6d2372f1c56ed57a4abaec9d36475/internal/backend/remote-state/pg/client.go
