# Octopushy: architecture and proof of concept

> **Architecture alignment update — October 14, 2026:** this is a historical
> exploratory proposal. The [current agreement](architecture-agreement.md)
> supersedes it where they differ; the [nodes comparison](nodes-comparison.md)
> records additional cases, not an implementation mapping. Git is now required
> for desired configuration, the existing PoC is not the required foundation,
> and Talos/Flux are deferred. The optional-Git path, technology choices, detailed
> protocols, and broad PoC scope below are not current commitments.

> **Implementation scope update — October 12, 2026:** this document describes the
> broader product direction. The implemented first slice is the narrower
> Git-linked, serial OpenTofu VM/state manager in [the README](../README.md) and
> [PoC operating guide](../old/docs/poc.md). Parallel execution, agents, Ansible,
> notifications, and service health below remain future work. The Go/React
> discussion is a proposal; the runnable PoC uses Python's standard library,
> SQLite, and a static dashboard.

Status: **historical proposal, not the current specification, an implemented
platform, or an approved stack choice**.
Prepared on October 12, 2026. Repository inspection found no existing application
or configuration to preserve.
Revised after clarification: resources-as-code, optional Git, parallel isolated
deployments, notifications, live service status, and future Talos integration.
“Git free” is treated as independence from a Git forge, with a no-Git input path
as well; “tailos” is assumed to mean Talos Linux. Both interpretations remain
assumptions until confirmed.

## 1. Product boundary

Build a **homelab operations control plane**, not a new general-purpose CI system.
Its differentiator is answering:

1. What needs attention, and which machines/services are affected?
2. Which revision and operation caused the problem?
3. What happened before the failure?
4. What is safe to do next?

Keep OpenTofu, Ansible, and existing scripts as the machinery. Octopushy owns
resource identity, execution coordination, approvals, observations, notifications,
and the operator experience. Resources persist between runs: this is not just a
list of pipeline executions.
Creating infrastructure, patching guests, patching hypervisors, and deploying a
service are distinct workflows; service deployment must not always reprovision
or upgrade its host.

Assume initially: one trusted administrator, trusted configuration, Linux
execution agents, and network access from agents to the relevant Proxmox APIs
and SSH targets. These assumptions must be confirmed before implementation.
Git is optional: an immutable source bundle is sufficient. A Git commit is
provenance for one type of bundle, not a required primary key. “Any Git” means
normal clone/fetch, not support for every forge's Actions format or runner protocol.

**Explicit scope:** resource inventory and desired/observed status; independent
parallel deployments; redeployment; maintenance jobs; release orchestration; one
notification integration; basic live health. Full telemetry storage, a build farm,
and autonomous destructive reconciliation are not the product.

If the intended reference is Blacksmith at blacksmith.sh, borrow its observability
and runner UX, not its product boundary: its documented offering is built around
GitHub Actions runners and CI observability. [B1]

## 2. Proposed shape

```text
Operator browser
    | HTTPS, authenticated UI/API, server-sent run events
    v
Octopushy server (one deployable process)
    UI + API + resource inventory + durable dispatcher + approval/recovery rules
    observation poller + notification outbox
    |
    +-- SQLite: configuration references, runs, attempts, events, audit records
    +-- private artifact volume: plans, inventories, restricted logs

Site A agent ---- outbound authenticated HTTPS ----> server
    +-- fresh execution container: OpenTofu / Ansible Runner / scripts
    +-- scoped API access ---> Proxmox host(s)
    +-- scoped SSH access --> managed nodes --> services

Site B agent ---- outbound authenticated HTTPS ----> server
    +-- same execution model, separately scoped credentials and targets

Git importer OR source bundle ---> immutable definition snapshot
CI build/test/publish -----------> release request (optional integration)
Agents / monitoring adapters ---> timestamped observations
OpenTofu ------- state + locks ---> existing independent remote backend
```

An agent per site/reachability boundary is a starting point, not a requirement to
install an agent on every VM or Proxmox host. A single agent can address several
Proxmox machines if its permissions and connectivity allow it. Prefer a dedicated
small agent VM over running automation directly on a hypervisor.

### Technology proposal

| Part | Initial choice | Reason / limit |
| --- | --- | --- |
| Server and agent | Go; one executable with `server` and `agent` modes | One implementation language for process supervision and the protocol; a proposal, not an existing dependency |
| Frontend | Small React + TypeScript application, packaged with the server; pnpm | Structured run detail and target navigation without a separate frontend service |
| Persistence | SQLite on the server's local persistent disk | One server and small concurrency; use transactions/constraints rather than introduce a queue service |
| Artifacts | Private persistent server volume; restrictive access and encrypted sensitive artifacts | No object-storage service required for the application PoC; independent of the existing OpenTofu backend |
| Agent protocol | HTTPS long polling, heartbeats, event batches | No inbound agent listener and no broker |
| Browser updates | Server-sent events, reconnect using event IDs | Polling fallback; no bidirectional browser transport required |
| Execution | Rootless Podman containers with pinned images | One supported runtime first; private workspace and explicit mounts [P1] |
| Ansible integration | Invoke Ansible Runner inside the execution image | Consume its structured task/host events, not regex-parsed terminal output [A1] |

Keep the implementation modular internally, but do not create microservices,
Kubernetes, Redis, Temporal, a plugin marketplace, or an independent scheduling
cluster for this proof of concept. PostgreSQL becomes worth considering if
multiple control servers or actual write contention become requirements.

### Resource-oriented model and code ownership

Keep existing `.tf`/`.tofu`, playbooks, Compose files, or Kubernetes manifests.
Add only the metadata that those tools do not share: stable resource identities,
bindings to execution recipes and outputs, dependencies, health checks, rollout
policy, and notification routes. Do not duplicate VM sizes/disks in a second
unrelated YAML schema or invent a universal replacement for the tools' languages.

Suggested layout, **not a committed configuration format or working parser**:

```text
homelab.yaml          # resource IDs, references, deployment/maintenance policy
infra/               # existing OpenTofu roots and modules
ansible/             # existing configuration, update, and deployment playbooks
services/            # service-native configuration, e.g. Compose files
```

A service may have several deployment instances. Each instance stores desired
release, last verified applied release, current operation, latest health
observation, and observation freshness separately. “Desired v2, applied v1,
healthy, last deployment failed” is a valid state, not a contradictory badge.
Unknown applied state after a partial mutation is also valid.

Node/stack/service relationships are references, not a strict tree: one service
can span several nodes, and several services can share a node. Validate missing
references and cycles before dispatching work. Start with explicit prerequisite
edges and fixed workflow recipes, not a general visual DAG language.

Importing a new definition computes a proposed change; it does not immediately
mutate machines. Changing or removing a resource definition is not authorization
to destroy it. Manual run, approved release request, and maintenance schedule are
distinct triggers using the same execution rules. Automatic reconciliation can
be an explicit later policy for selected operations.

### Concrete hierarchy: Proxmox, VMs, and workloads

The default navigation follows the user's hierarchy, with profiles rather than
different VM types for service and AI roles:

```text
Proxmox installation (standalone host or cluster)
  Hypervisor: pve-a
    VM: services-01   [profile: service-host]
      Service deployment instances
    VM: ai-01         [profile: ai-node]
      Managed AI tools / harnesses
    VM: general-01    [profile: base]
  Versioned VM templates (with availability/placement recorded)
```

This is a navigation projection, not an immutable ownership tree. Proxmox VMIDs
are cluster-wide and VMs can migrate between hosts. Give each VM a stable
Octopushy identity, map it to `{installation, VMID}`, and record current and
desired placement separately. For independent standalone hosts, each host is a
separate installation. Never key a VM only by its IP or current hypervisor. [V1]
Detect deleted/recreated VMID reuse as a possible new incarnation; do not silently
attach old approvals, host keys, or operation history to the replacement.

**Proxmox connection:** API endpoint(s), TLS trust configuration, token ID and
secret reference, installation identity, and permitted operations. Hypervisors
reference a connection; a cluster need not duplicate the same credential for
every host. Explicit hypervisor SSH access is separate from API access.

**VM:** logical identity, installation/VMID, current and desired host, template
revision, actual guest OS, profile(s), OpenTofu stack/resource binding, observed
addresses, and SSH access-profile reference. CPU/disk/network configuration stays
in the existing OpenTofu definitions instead of being independently rewritten in
the catalog.

**Template:** build-recipe revision, image/template identity, OS family, verification
result, and availability on intended hosts/storage. Preserve the existing
Nix-based/Fedora build flow and OpenTofu ownership. Building a template, creating
a VM from it, updating a running guest, and upgrading an AI tool are different
operations. Publishing a template must not silently replace running VMs.

**Profiles:** `base` supplies the explicitly required access/bootstrap
configuration; `service-host` adds Docker and deployment prerequisites where
needed; `ai-node` adds the selected harnesses/toolchains. Profiles are composable.
Do not install Docker, GPU tooling, or service secrets on every VM by default.

**Services and tools:** service deployments track runtime/configuration/secrets,
health, and release identity. Managed CLI tools track install owner, desired and
observed version, update recipe, validation command, and maintenance policy.
A harness running as a daemon may also have a service instance; a CLI version
check is not a service-uptime check.

### Per-VM status is not a second OpenTofu state system

OpenTofu state binds configuration resources to remote objects. Keep that
authority in its backend; a status row per VM does not require one state file
per VM. State boundaries follow independent lifecycle/concurrency needs. [T7]

Project a VM record from its explicit stack/resource binding and allowlisted
outputs; observe live power/placement through the Proxmox API and guest readiness
through authenticated SSH/probes. Do not treat cached OpenTofu state as a live
monitor, edit it to set a UI status, or automatically import/delete an unknown VM.

Display these independently, each with source and freshness:

- Provisioning: absent / planned / applying / provisioned / failed / unknown.
- Runtime: running / stopped / paused / unknown, as observed from Proxmox.
- Access: reachable / authentication failed / host identity changed / stale.
- Configuration: desired revision and last verified applied revision.
- Maintenance: pending / running / failed / last verified success.
- Workloads: service health and managed-tool update status.

“VM running, SSH authentication failed, tool update blocked” is a valid state.
An observation outage cannot imply the VM was deleted or should be recreated.

### Modular boundaries, not a plugin platform

- **Inputs:** source bundle; optional Git import and CI release request.
- **Executors:** OpenTofu, Ansible Runner, and scripts first.
- **Observers:** basic HTTP/TCP checks first; monitoring/Kubernetes/Talos adapters
  later, using existing APIs instead of replacing their controllers.
- **Outputs:** generic authenticated webhook notification first.

Adapters normalize outcomes, progress, artifacts, health evidence, and resource
bindings. Tool-specific plan/retry/upgrade semantics remain tool-specific. A new
ordinary service is a definition plus an existing recipe and health check, not a
new plugin. Add a backend adapter only when a genuinely new management API is
needed; unsupported actions must be explicit rather than disguised as generic SSH.

Existing service managers remain possible adapters, not products ruled out by
this proposal. The [service-layer comparison](research/service-management-fit.md)
identifies documented partial fits. If one owns a service's container lifecycle,
Octopushy requests changes through its supported interface rather than competing
with direct Compose/Ansible changes. Agree ownership of Docker/host preparation
as well; verify compatibility with the actual guest images before adoption.

### Ownership of information

- **Definition snapshots:** infrastructure, playbooks, service definitions,
  workflow descriptions, and nonsensitive metadata. Import from Git or a local
  source bundle. The UI selects an immutable snapshot, not a mutable working tree.
  It does not silently become a second configuration editor.
- **Server database:** run/attempt history, approvals, credential references,
  agent enrollment and authorization, runtime evidence, audit records, and
  operator-managed safety policy, resource projections, observations, notification
  delivery records, and schedule cursors.
- **OpenTofu backend:** authoritative infrastructure state and backend locks.
  Do not build a replacement state backend.
- **Agents:** bounded temporary workspace and durable local execution journal;
  neither the authoritative configuration store nor the only copy of evidence.
- **Secrets:** references in definition snapshots; protected files on the selected agent
  for the PoC. Server records allowed references, not their values. An external
  secret manager can replace this after the execution model works.

## 3. Dashboard contract

The default view is **Attention**, not an alphabetical folder tree of jobs.
`docs/dashboard-concept.html` is a mock-data interaction sketch, not a real client.

### Required first screens

1. **Overview / attention:** failed operations, uncertain outcomes, waiting
   approvals, and unavailable agents, ordered by operational impact.
2. **Run detail:** revision, target, actor, agent, step timeline, failing task or
   resource, error excerpt, affected hosts, artifacts, and contextual recovery.
3. **Resources:** views by site, hypervisor, managed node, service, and deployment
   instance, using declared relationships initially. Cross-links support services
   spanning several nodes; desired release, applied release, current operation,
   and observed health are separate fields.
4. **Agents:** heartbeat freshness, site/environment authorization, versions,
   current attempt, and reason for being unavailable.
5. **Rollout:** independent target rows and an honest aggregate outcome such as
   “2 succeeded, 1 failed, 1 dependency-blocked,” with scoped recovery actions.

Keep the happy path visually quiet. Use text/icons in addition to status colors.
Log panels should deep-link to the failed task, support copying an error, and
preserve surrounding output; raw logs remain available but are not the homepage.
Use the same fault summary and recovery actions in the overview and run detail.
Preserve filters and selected target when navigating back.

Show **last observed at** and **unknown/stale** explicitly. A successful deployment
does not prove a service is healthy now. The clarified PoC includes post-deploy
checks plus periodic lightweight HTTP/TCP probes, with configurable timeout,
interval, freshness deadline, and failure/recovery thresholds. A probe timeout,
stale observer, and confirmed endpoint failure are distinct evidence.

The UI must explain both what a recovery will repeat and what it will skip.
Disabled actions need a reason. No generic green “retry everything” button.
Execution history remains immutable even when a later attempt succeeds.

### Acceptance targets for the UX

- Find the failed target and step from the overview in at most two interactions.
- Within 30 seconds, identify the evidence, impact, and permitted next action in
  a seeded failure exercise; this is a test target, not a measured result.
- Before confirming a retry, see revision, affected targets, repeated steps,
  skipped steps, and any required reapproval.
- A disconnected agent never makes an uncertain deployment look “failed and
  therefore safe to retry.”

### Live status and notifications

The dashboard combines declared intent, deployment history, and independently
observed condition. Observation does not automatically authorize repair. Keep
only bounded recent health history initially; integrate existing monitoring for
metrics, logs, and long-term graphs instead of building another telemetry backend.
Link to the underlying monitoring view when present.

Record probe scope/vantage point, observed time, controller receipt time, and
expiration. Agent disconnection makes its observations stale; it does not prove
every service in that site is down. Validate configured endpoints and protect
probe credentials; probes must not become an unauthenticated network proxy.

Start with one authenticated webhook destination. Emit operation failures,
approval requests, sustained unhealthy/recovered transitions, and lost-agent
events. Group rollout summaries without hiding individual failures; use
deduplication keys and thresholding to avoid one alert per poll.

Write the notification event to an outbox in the same transaction as the state
change. Retry delivery with bounded backoff and record delivery failures. A
broken notification endpoint must not fail a deployment or block dispatch.
Delivery is at-least-once, so duplicate external notifications remain possible.
Payloads contain resource IDs, severity, a short sanitized summary, and a run
link, not raw secret-bearing logs. Maintenance suppression has an expiry and
suppresses notifications, not the underlying evidence.

## 4. Execution and durability

### Minimum records

`DefinitionRevision`, `Environment`, `Resource`, `DeploymentInstance`, `Release`,
`Rollout`, `WorkflowRevision`, `Agent`, `Run`, `StepAttempt`, `Approval`, `Artifact`,
`Observation`, notification delivery records, and an append-only run/audit stream.
These are logical records, not a demand for one service or table per noun.
Do not turn this into an event-sourced system: ordinary current-state records
plus immutable execution evidence suffice.

A run fixes the definition snapshot digest, source provenance, workflow digest,
resolved targets, input fingerprint, execution image digest, and dependency
versions. For Git input, verify the full commit and use a detached checkout.
For bundle input, verify the digest and unpack only allowed paths into a fresh
workspace. Neither input may change halfway through a run. External modules,
collections, and deployment images also need pinned versions/revisions.

### Normal path

1. Operator requests a workflow and revision for an environment.
2. Server authorizes the actor, workflow, target set, and eligible agent.
3. Server records a rollout and target-specific child runs; for a single target
   this is one run. It dispatches only dependency-ready, authorized work with
   available resource reservations and execution capacity.
4. Agent claims a unique attempt with a lease, records it locally before launch,
   fetches/verifies the revision, and starts its execution container.
5. Agent uploads sequenced events, bounded log chunks, artifacts, and heartbeat
   evidence. Duplicate submissions are deduplicated by attempt/event identity.
6. Server advances that run only after a durable, authoritative outcome. Other
   independent runs progress separately. A review gate waits without keeping
   an execution process alive.
7. Agent/server retain enough evidence to reconcile restarts. A browser refresh
   or lost HTTP response must not launch another attempt.

UI/API mutation requests use idempotency keys, including run creation, approval,
and retry. Database transactions and compare-and-set transitions prevent a
double-click or repeated request from issuing a second command.

Suggested attempt states:

```text
queued → assigned → running → succeeded | failed
                       |
                       +→ cancel_requested → canceled | outcome_unknown
                       +→ outcome_unknown → reconciled result
```

Approval is a run gate, not a fake running process. Run states also include
`waiting_approval` and `needs_attention`; they summarize, rather than overwrite,
the underlying attempts. A canceled command may still have partial external
effects; show that separately from its process exit status.

### Disconnects, retries, and concurrency

- Delivery can repeat; arbitrary infrastructure side effects cannot be promised
  “exactly once.” A lease protects dispatch ownership, not external reality.
- Expired heartbeat during mutation means **outcome unknown**, not automatic
  requeue. Keep the mutation reservation; do not dispatch a replacement agent.
- On reconnection, use attempt identity, local journal, container identity, and
  stored results to reconcile. Repeated delivery of an already-started attempt
  must return its existing status, not launch another process.
- If evidence is lost, the operator must confirm the old executor is stopped
  and inspect target/state evidence before recovery. Reject obsolete agent
  messages for state transitions but retain them as audit evidence.
- **Superseding the first draft:** no global mutation lock. Parallel independent
  deployment is now a PoC requirement. Begin with two execution slots and
  conservative exclusive reservations per affected node and OpenTofu state
  identity, plus any declared shared maintenance/resource domain.
- Server derives reservations from approved resource bindings, not job-supplied
  labels. Acquire a run's complete required set atomically before dispatch;
  persist ownership through restarts, approval, and uncertain outcomes.
  Two environments addressing the same physical node share its reservation.
- A deployment on node A can run beside one on node B, but node A cannot be
  patched/rebooted while its deployment is running. For simplicity, initially
  serialize even different service mutations sharing one node.
- Infrastructure operations also reserve the nodes/resources they might affect.
  A shared-state OpenTofu plan/apply remains one operation and locks that state;
  do not split it into competing per-VM applies to manufacture parallelism.
  Split states only along genuinely independent lifecycle boundaries.
- OpenTofu's own backend locking stays enabled. It protects its state operation;
  it does not coordinate an Ansible reboot with a deployment. [T1]
- Graceful cancellation requests stop the local command/container, capture
  evidence, and block downstream steps. If remote work may continue, mark that
  uncertainty and retain the reservation. Never imply cancellation is rollback.

### Failure containment and schedules

A rollout coordinates independent child runs, not one shell process targeting
the entire fleet. Default policy: keep unrelated branches running, block work
that requires a failed/unknown prerequisite, and preserve each outcome. Failed
does not mean blocked; “partial success” must never appear as success.

For example, a service on node B can fail without canceling the service on node
A. If a shared database migration fails, its dependent application rollout must
stop. Shared storage, networks, hypervisors, and databases remain shared failure
domains: the scheduler cannot guarantee physical isolation where none exists.

An OpenTofu apply can partially change a shared stack before failing; that stack
needs reconciliation and a fresh plan, not a fabricated per-resource success
story. Design Ansible recipes for target independence before executing separate
host runs; cluster-wide plays remain coordinated operations with their own policy.

Use a rollout-specific parallelism limit and an explicit failure budget for
rolling updates. Safe defaults for quorum-sensitive maintenance can be serial
without serializing the whole homelab. “Retry failed targets” previews exact
children and uses tool-specific recovery, not an unconditional bulk replay.

Maintenance schedules create the same authorized runs as manual actions. For the
first scheduled guest-update job, persist a schedule cursor, deduplicate by
schedule ID and scheduled occurrence, skip missed occurrences after controller
downtime, and disallow overlapping runs. Maintenance windows and permissions are
server policy. Notifications failing must not stall the schedule.

## 5. Tool-specific behavior

### OpenTofu

First-class actions are `init/validate`, `plan`, `review`, and `apply`, not opaque
shell blocks. Save the plan with `tofu plan -out=...` and apply that exact saved
artifact; the CLI explicitly supports this automation workflow. [T2][T3]
Pin and test the CLI version rather than assume every live-documentation option
is present in the user's current installation.

- Treat detailed plan exit codes `0` and `2` as successful outcomes: respectively
  no change and changes proposed; `1` is an error. [T2]
- Bind approval to the saved plan digest, definition revision, input fingerprint, execution
  image/tool/provider versions, backend/workspace identity, and recorded state
  identity. Do not store secret inputs as plaintext fingerprints.
- Apply must never quietly generate a new plan under an old approval. Rejected,
  expired, changed, missing, or stale plans require a new plan and approval.
- PoC pins related steps to one agent and a consistent container workspace path;
  no transparent migration of saved plans between arbitrary tool installations.
- Hold the application mutation reservation while awaiting approval, with a
  configurable approval deadline. This does not hold an OpenTofu backend lock
  continuously across the review window.
- Use a short plan validity window and surface possible out-of-band changes.
  State identity checks alone cannot prove the real infrastructure has not
  changed outside OpenTofu.
- Retain the user's existing remote backend if it supports locking. If a backend
  is needed, evaluate the S3 backend with `use_lockfile`; an S3-compatible service
  must actually support the required conditional writes, not merely the name
  “S3-compatible.” Test locking and restore before adoption. [T4]
- Treat state, saved plans, and raw JSON rendering as secret-bearing. OpenTofu
  supports plan/state encryption; protect keys separately from backups and
  validate restore. Public dashboard summaries must filter sensitive fields,
  not expose raw `tofu show -json` output. [T5][T6]
- After a partial/uncertain apply, inspect and **re-plan**, then obtain fresh
  approval. No automatic force-unlock, state manipulation, targeted apply,
  destroy, or “rollback infrastructure” shortcut.

### Ansible and maintenance

Consume Ansible Runner's structured events for host/task outcomes and error
locations. Retain the human-readable log as secondary evidence. [A1][A2]

Run a reviewed idempotent playbook from its beginning for the permitted target
set when retrying; do not promise arbitrary task-level resume. Restricting to a
failed host is permitted only for playbooks whose cross-host dependencies and
handlers have been reviewed. Default to the original target set otherwise.

Patch guests in a separate workflow. Model drain → patch → reboot if needed →
wait for connectivity → health check, using Ansible's rolling-batch facilities
where appropriate. [A3] Stop on a failed health gate. Package changes are not
generally reversible, so describe recovery rather than offer a blanket rollback.

Hypervisor maintenance is a **later, separately privileged workflow** needing
cluster/quorum/storage checks, evacuation policy, and a control-plane escape
path. Do not reboot all Proxmox hosts from an ordinary node-patching workflow.

Check mode is useful preview evidence but not a transactional plan equivalent:
support depends on modules, and playbooks can explicitly run tasks normally in
check mode. It is not a security boundary for untrusted playbooks. [A4]

### Scripts

Run a snapshot-pinned executable/argv list with explicit working directory, timeout,
environment allowlist, and bounded output. An explicit shell-script step is fine;
do not concatenate user inputs into a shell command. Scripts are **not safely
retryable by default**. A reviewed workflow can designate a script repeatable or
provide a check/recovery action.

### Frequent AI-tool updates and guest ownership

Treat AI-tool maintenance as a first-class workflow, separate from guest OS
updates, VM replacement, and service releases. Preserve a specific resolved tool
version and recipe revision for each attempt; don't execute an unrecorded moving
`latest` target. The set of tools and their actual package managers is not yet
known and must be provided before implementing recipes.

A frequent schedule may detect updates several times a week without blindly
upgrading every node at once. Proposed flow: detect candidate → select/pin
version → wait for a safe maintenance boundary → update one canary → validate
version/basic behavior → roll out to remaining selected AI nodes.

Do not interrupt active harness work implicitly. Use an explicit idle/drain check
where the tool exposes one; otherwise require an operator-approved window.
Keep update failures local to affected nodes unless the rollout's failure budget
requires pausing related updates. Keep enough previous-version information for
recovery, but do not promise automatic rollback for irreversible changes.

Clarify whether “Nix” means NixOS guests or Nix-based image builds only. NixOS has
its own declarative configuration/rebuild model. If a component is Nix-managed,
update that definition and apply through its owner rather than fight it with
unrelated imperative package updates. Ansible can orchestrate the chosen
operation; it need not own every guest's package configuration. Fedora/native
package and separately managed tool recipes remain distinct. [N1]

### Recovery actions presented in the UI

| Outcome | Primary action | What must not happen |
| --- | --- | --- |
| Plan error, no mutation started | Rerun plan | Reuse an unrelated approval |
| Apply failed or outcome uncertain | Inspect state → create fresh plan → approve | Blindly replay the old apply |
| Reviewed Ansible deployment failed | Preview rerun of the playbook and allowed targets | Jump to an arbitrary task and omit prerequisites/handlers |
| Script failed, replay safety unknown | Inspect output and choose explicit recovery | Automatic retry |
| Agent disconnected during mutation | Reconcile execution | Start a competing attempt |
| Post-deploy probe failed | Rerun read-only probe, inspect service, or deploy a reviewed revision | Pretend a previous image/config reverses database migrations |
| Configuration needs a fix | Start linked run at the new definition revision | Change the revision inside the old run |

### Future Talos/Kubernetes boundary

Assuming “tailos” means Talos Linux: its documented management model is API-based
and does not provide SSH or a shell. Do not model it as an ordinary Linux guest
receiving package-update playbooks. [L1]

Keep VM provisioning in OpenTofu when that is the chosen resource owner. Use
Talos APIs/`talosctl` from an external agent for machine configuration, upgrades,
and observations; use Kubernetes APIs and the selected application deployment
tool/controller for workloads. Ansible may invoke APIs from an execution host,
but ordinary SSH-based guest administration is not the Talos management path.

Do not install the normal host agent directly into Talos or invent an SSH
backdoor. First integrate read-only machine/cluster/workload observations;
then add explicitly supported lifecycle actions with their own safety checks.
If an existing cluster controller owns VM provisioning or application
reconciliation, Octopushy requests changes through it rather than becoming a
second writer. Pick one authority per resource; an observer is not another owner.

## 6. Security and bootstrap constraints

This is remote code execution with infrastructure credentials. A private network
alone is not authorization.

### Three separate credential boundaries

1. **Proxmox API:** store a token reference, not its value, on the connection.
   Use scoped non-root automation identities and privilege-separated tokens,
   with observation and mutation privileges separated where practical. Proxmox
   documents token-specific permissions and expiration; granting privileges to
   an underlying user alone does not define the whole separated-token scope. [V2]
2. **SSH:** an access profile resolves username, address/port, private-key reference,
   approved host identity/trust, optional jump host, and allowed privilege
   escalation. A profile may cover a scoped group of targets; this does not mean
   sharing one unrestricted root key across the entire homelab.
3. **Service/tool runtime:** database credentials, registry credentials, and AI
   provider tokens belong to the workload that needs them. They are not inherited
   automatically from the host's administrative access.

SSH is the current access capability for Fedora/Nix-based guests and explicitly
authorized hypervisor operations, not a requirement for every future platform.
The Talos exception remains API-based. An agent must reach both the relevant
Proxmox API and the guest SSH network, directly or through an approved jump host.

For the PoC, reuse protected operator-provisioned secret files on the designated
agent, on encrypted storage with restricted permissions and encrypted backups.
Resolve only authorized references for a step into short-lived private files or
an isolated SSH-agent context; clean them up after completion and crash recovery.
This is a trusted-agent-host boundary, not protection against root on that host.
Use an existing secret manager when available; do not implement a homegrown vault.
Record reference/version, use, rotation, and revocation in audit data, not values.

Only public authorized keys belong in VM bootstrap data; never bake controller
private keys, Proxmox tokens, or workload secrets into reusable templates. Guest
SSH host identities must be generated/verified per VM, not blindly shared by
clones. Establish initial trust through a verified bootstrap channel; preserve
host-key checking and require explicit reconciliation after reinstallation.
Ansible supports SSH-key authentication and enables host-key checking by default.
Do not disable that protection just to make unattended provisioning succeed. [A5]

- Validate API TLS certificates, including a configured private CA where needed.
- Secret-entry/update flows must not expose values in browser history, ordinary
  logs, task arguments, or read-back API responses.
- Hypervisor SSH maintenance has its own explicit access/approval scope. A guest
  service deployment must never inherit hypervisor administrative credentials.
- Credential rotation is independent of VM replacement; do not require rebuilding
  the fleet to rotate an automation key or service secret.

- Initial exposure is private LAN/VPN only, with TLS and authenticated UI/API.
  Use an existing authenticating reverse proxy if available; otherwise implement
  a single-admin login before any infrastructure mutation. Cookie-authenticated
  actions need CSRF protection. Do not ship an unauthenticated “temporary” panel.
- Agent enrollment uses a one-time expiring bootstrap token, exchanged for a
  revocable per-agent credential; never use one permanent token for every agent.
  No secret values in logs, URLs, examples, or Git.
- Server-approved agent site/environment membership is authorization; a
  self-reported label is only metadata and cannot grant access.
- Jobs never receive agent control credentials, other jobs' workspaces, the
  container engine socket, or the server's storage. Use minimal mounts, a
  nonprivileged container, and least-privilege Proxmox/SSH credentials.
- Credentials are injected only for the authorized step. In the PoC, the agent
  reads operator-provisioned protected files and mounts ephemeral copies into
  the job; no secret CRUD UI or custom encryption service.
- Redaction is best-effort, not a sandbox. Avoid logging secrets at source;
  apply Ansible `no_log`/restricted diffs where needed; restrict artifact access
  and audit downloads. Treat retained logs as potentially sensitive.
- Scripts, providers, modules, and playbooks are executable code. Initial runs
  accept only trusted immutable configuration. No public/fork PR execution with
  homelab credentials. Rootless containers do not make this a safe hostile
  multi-tenant runner service.
- Server-side policy controls approvals, eligible agents, secret grants, and
  destructive actions. A supplied workflow cannot turn those requirements
  off by editing its YAML or labeling a mutating command “read-only.”
- Validate target selections, repository URLs, checkout/workspace paths, event
  payload sizes, and artifact paths at trust boundaries. Bundle ingestion rejects
  absolute/traversal paths, escaping symlinks, and oversized/unbounded extraction.
  Render logs as escaped text. Upload authorization is bound to its assigned attempt.
- Back up the database, artifacts, and deployment configuration consistently;
  keep secret/encryption-key recovery separate. Document restore from outside
  the infrastructure being repaired.
- Bootstrap the controller, initial agent, remote state storage, and access
  credentials using a documented manual procedure. Do not require a functioning
  Octopushy instance to repair Octopushy. Keep a CLI break-glass runbook.

## 7. Sources, CI integration, and releases

**PoC:** manual submission of a source snapshot, stored by digest. A local
directory is packaged before submission; it is never executed in place. Support
one optional Git importer with read-only credentials and fixed-commit checkout
using the same snapshot representation. Record source provenance and actor
identity separately; a content hash alone does not establish trust.

**CI boundary:** native GitHub/GitLab runners belong to their native controllers;
they are not interchangeable Octopushy agents. Supported integrations are native
CI jobs calling Octopushy's API/CLI, or Octopushy dispatching native workflows/
pipelines and following their identifiers. The research note distinguishes
documented interfaces from design judgments. [CI1]

Preferred release path:

```text
CI (optional): build → test → publish immutable artifact
                                  |
                                  v
Release request: artifact digest + definition revision + authorized targets
                                  |
                                  v
Octopushy: preflight → approval if required → target rollout → verify → notify
```

A release can also be submitted manually using an already published artifact.
CI is not required to redeploy a known release. Keep mutable image tags out of
release identity, and require approvals/secret grants independently of the
requester's parameters. The PoC validates this handoff with a manual request;
native CI adapters come later.

Keep privileged homelab operations and credentials on dedicated agents. Hosted
CI does not automatically have access to a private homelab: choose an explicit
authenticated network path or use CI inside the trusted network. If CI dispatch
is added, correlate native run IDs/attempts and deduplicate requests; a successful
build is not a successful deployment. [CI1]

**Next:** one forge's event/status adapter, not four at once. Normalize events,
verify signatures/authentication, and keep source trust separate from transport.
Calling a provider API is not implementing its runner protocol. Co-hosting agents
shares hardware, not their credentials, scheduler, or protocol. [CI1]

Manual operation continues without a forge webhook/API if the definition
snapshot, artifacts, required dependencies, and credentials are available.

## 8. Proof of concept: one complete operational slice

### Demo boundary

One controller, one outbound agent with two isolated execution slots, one trusted
definition snapshot, one isolated test environment, and two disposable VMs on a
real Proxmox host: a service-host VM and an AI-role VM, using existing verified
templates. Exercise a service deployment and a managed-tool update independently.
Provisioning may share one OpenTofu
stack; its applies remain serialized. Separate states are needed to demonstrate
independent provisioning operations, not merely parallel service deployment.
No changes to existing homelab workloads until the safety tests pass.

### Demonstration

1. Submit a definition snapshot without requiring a forge and request the test VMs.
2. Show the saved OpenTofu plan in the dashboard and require explicit approval.
3. Apply that plan and map allowlisted outputs into an Ansible inventory.
4. Run the appropriate guest baseline and profile preparation. Inject public SSH
   access without baking private/runtime secrets into templates.
5. Start a service deployment on A and a pinned AI-tool update on B concurrently.
   Deliberately fail B; verify A completes and remains manageable.
6. Show desired/applied service release, desired/observed tool version, and VM
   runtime/access separately; distinguish service health, stale evidence, and
   failed maintenance.
7. Send one failure notification; preview and recover only B without repeating A,
   provisioning, or patching. Verify the maintenance-recovery notification and
   separately exercise a service unhealthy/recovered transition.
8. Attempt a concurrent update/deployment on the same node and verify it queues;
   verify an unrelated node is not blocked.
9. Disconnect the agent during a controlled mutation; show an uncertain result,
   retain only affected reservations, reconnect, and reconcile.
10. Run a scheduled guest-update fixture through the same dispatcher and verify
    restart deduplication/non-overlap. Make the notification endpoint fail and
    verify delivery failure does not stop independent operations.

After that works, add a second agent/network boundary and a second Proxmox host
to validate routing. This is the expansion checkpoint, not a prerequisite to the
first useful end-to-end demo.

### Build order and go/no-go gates

| Milestone | Deliverable | Exit condition |
| --- | --- | --- |
| 0. Buy/build challenge | Test resource-as-code, independent failure/recovery, live status, and notifications in the closest candidate | Stop evaluating job dashboards that do not model the desired operations; stop building if adoption meets the need |
| 1. Operator experience | Resource overview, parallel rollout, failed run, and recovery preview | User distinguishes intent, applied release, operation, and health |
| 2. Execution skeleton | Authenticated server/agent, durable run records, one harmless script, live events | Restart/disconnect/double-click exercises cannot cause duplicate launch |
| 3. Infrastructure | Saved plan review and apply for disposable VMs | Approval/artifact identity, resource reservations, and backend locking verified |
| 4. Configuration/deployment | Parallel service/tool operations, Ansible events, periodic probes, webhook notification, one maintenance schedule | AI-tool update B fails without stopping service deployment A; scoped recovery and status/notification checks pass |
| 5. Operational rehearsal | Backup/restore, cancellation, lost-agent reconciliation, independent bootstrap | Recover without Jenkins or a working controller |

Schedule estimates should follow milestone 2, not precede it: durable execution
and trustworthy recovery are the uncertain work, not running three CLIs.

### Required checks before touching real infrastructure

- A duplicate run/approval/retry request creates one authorized action.
- A server restart preserves queue and approvals without launching duplicates.
- An agent restart after launch does not rerun the same attempt.
- An expired lease during mutation retains the reservation and blocks retries.
- A stale or modified plan cannot execute under a previous approval.
- Plan exit code `2` produces “changes proposed,” not failure.
- Independent-node mutation requests overlap within the configured limit.
- Conflicting node/state/shared-resource mutations queue; aliases in different
  environments cannot bypass reservations.
- B failing does not cancel independent A; failed prerequisites block dependents.
- A shared-state apply is never split or replayed as concurrent per-node applies.
- Replayed event batches produce no duplicate visible events/state regressions.
- Failure of deployment does not cause infrastructure and patch steps to rerun.
- An uncertain/canceled operation does not imply remote effects were undone.
- A new definition revision creates a linked run, not a changed old attempt.
- Bundle input works without Git and rejects unsafe extraction paths.
- Wrong agent identity, cross-environment secret access, and path traversal
  requests are rejected; trusted test secrets are absent from displayed output.
- A guest deployment cannot request the hypervisor's API/SSH credential; secrets
  remain absent from templates, inventory exports, and public status responses.
- SSH host-key changes block operations until reconciled; rotation/revocation of
  an access credential does not require VM replacement.
- VM migration updates placement without changing resource identity; detected
  VMID reuse/recreation cannot silently inherit prior approvals or SSH trust.
- An AI-tool update respects its package/configuration owner and does not upgrade
  active harness sessions without the configured maintenance authorization.
- Failed and stale health observations are distinct from deployment status.
- Notification outages do not alter deployment outcomes; retry/deduplication and
  maintenance suppression expiry work.
- Duplicate schedule occurrences and overlapping maintenance runs are rejected;
  controller downtime does not cause a catch-up patch storm.
- Backup restore and independent manual access both work.

### Explicitly excluded

Multi-user RBAC, multi-tenancy, public PR execution, arbitrary DAG/visual workflow
editors, native GitHub Actions compatibility, auto-scaling agents, agent-per-VM
installation, automatic remediation, blanket rollback, hypervisor upgrades,
a time-series/log-storage platform, Kubernetes/Talos lifecycle controllers, a
custom secret manager, and four complete forge integrations.

Later additions should be driven by a real workflow: a second site; first-forge
triggers; richer maintenance policies; read-only drift plans; finer-grained
service locks; team permissions; additional notification/monitoring integrations;
then Talos/Kubernetes observations and explicitly scoped lifecycle actions.

## 9. Migration and unresolved decisions

Keep Jenkins running while evaluating. Move one low-risk workflow to the new
path, disable the corresponding Jenkins mutation job, and verify history and
recovery before moving another. Never let both systems auto-apply the same stack
or patch/deploy the same targets concurrently.

Confirm before implementation:

1. Whether “Git free” means forge-independent, entirely Git-free, or zero-cost;
   approximate topology: Proxmox hosts/clusters, guests,
   network boundaries, and whether “update machines” includes hypervisors.
2. Current remote state backend and locking, secrets/SSH handling, and service
   deployment style (Compose, systemd, Kubernetes, or other).
3. The three concrete Jenkins interactions that are most painful, and whether a
   paid self-hosted edition is acceptable if it eliminates custom maintenance.
4. Where the controller/agents will live, and the independent recovery path when
   that infrastructure is down.
5. Preferred implementation languages; Go/React is a default proposal rather
   than a requirement.
6. Whether Proxmox hosts are clustered or independent; whether AI nodes are VMs;
   and whether “Nix” means NixOS guests or only the image-build tooling.
7. Existing secret storage/SSH bootstrap approach and the first AI tools to
   maintain. Do not request secret values in chat or commit them to examples.

### A third option worth testing

If an existing product's execution and recovery behavior is sufficient but its
navigation is the problem, test a thin custom dashboard over its supported API
before owning an execution engine. Verify that the API exposes immutable
revisions, attempt history, approvals, scoped reruns, and agent outcomes; a
prettier UI cannot supply missing execution guarantees. Do not commit to this
integration or a second backend abstraction before the evaluation demonstrates
that it saves work.

## Sources

Primary documentation checked October 12, 2026; live documentation can change.
Architecture and UI decisions above are proposals, not vendor guarantees.

- [B1] Blacksmith product and console overview: https://www.blacksmith.sh/
- [T1] OpenTofu state locking: https://opentofu.org/docs/language/state/locking/
- [T2] OpenTofu plan, saved plans, detailed exit codes: https://opentofu.org/docs/cli/commands/plan/
- [T3] OpenTofu apply: https://opentofu.org/docs/cli/commands/apply/
- [T4] OpenTofu S3 backend: https://opentofu.org/docs/language/settings/backends/s3/
- [T5] OpenTofu state/plan encryption: https://opentofu.org/docs/language/state/encryption/
- [T6] OpenTofu show and sensitive JSON output: https://opentofu.org/docs/cli/commands/show/
- [T7] OpenTofu state purpose and resource bindings: https://opentofu.org/docs/language/state/purpose/
- [A1] Ansible Runner inputs and job events: https://docs.ansible.com/projects/runner/en/latest/intro/
- [A2] Ansible Runner event interfaces: https://docs.ansible.com/projects/runner/en/latest/python_interface/
- [A3] Ansible rolling updates: https://docs.ansible.com/projects/ansible/latest/playbook_guide/guide_rolling_upgrade.html
- [A4] Ansible check/diff mode limitations: https://docs.ansible.com/projects/ansible/latest/playbook_guide/playbooks_checkmode.html
- [A5] Ansible SSH keys and host-key checking: https://docs.ansible.com/projects/ansible/latest/inventory_guide/connection_details.html
- [V1] Proxmox VM identity and migration, official source: https://raw.githubusercontent.com/proxmox/pve-docs/master/qm.adoc
- [V2] Proxmox API-token permissions, official source: https://raw.githubusercontent.com/proxmox/pve-docs/master/pveum.adoc
- [N1] NixOS configuration and upgrades: https://nixos.org/manual/nixos/stable/
- [P1] Podman execution options: https://docs.podman.io/en/latest/markdown/podman-run.1.html
- [L1] Talos management model and API-only administration: https://www.siderolabs.com/faq
- [CI1] Primary-source CI integration research: [CI execution boundaries](research/ci-execution-boundaries.md)
