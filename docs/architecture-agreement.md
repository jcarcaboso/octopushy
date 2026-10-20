# Octopushy: agreed direction and open design decisions

Recorded October 14, 2026.

**Status: architecture alignment, not an implementation specification.** This
document distinguishes user-confirmed direction from working proposals. It does
not authorize infrastructure changes or imply that the described product exists.

**Working drawings:** [Architecture draft 01](diagrams/architecture-draft-01.excalidraw)
is an editable Excalidraw board with four views: system responsibilities, resource
relationships, independent progress, and a proposed operation lifecycle.
[Architecture draft 02](diagrams/architecture-draft-02.excalidraw) adds the server
building blocks from section 5: system shape, variable store and secrets, secrets
as service prerequisites, and changes, drafts, and Git write-back. Solid borders
mark confirmed decisions; dashed borders mark working proposals. These are rough
discussion sketches, not additional commitments or a fixed design.

The Python PoC was an experiment, **not the required foundation**; it is archived in
[`old/`](../old/README.md) (October 20, 2026). Designing Octopushy from scratch does not
mean recreating existing VMs, abandoning their state, or replacing their native
automation without a separate decision.

**Implementation started October 20, 2026.** At the user's request, the backend,
frontend, and agent projects exist, and the first milestone is Proxmox and virtual
machine management before anything else. [Milestone 1](milestone-1.md) records its scope
and the contracts between the projects.

The [nodes comparison](nodes-comparison.md) checks this discussion against real
automation. It is a source of cases and constraints, **not a one-to-one Jenkins
migration plan**. Finding something there does not add it to Octopushy's scope.
The older [architecture proposal](architecture-and-poc.md) is historical reference;
it must not override the decisions here.

## 1. Confirmed product direction

- Build a self-hosted homelab operations control plane, not another general-purpose
  CI system. Prioritize understanding affected resources, failures, and safe recovery.
- Git is the source of truth for desired configuration. Independence means
  independence from a particular forge, not an initial Git-free configuration path.
- Keep configuration tool-native: OpenTofu definitions, Ansible playbooks, NixOS
  configuration where used, service definitions, and scripts. Add only the
  declarative metadata needed to connect targets, operations, and policy.
- A central management service coordinates work. Agents execute actions in the
  appropriate networks and report results; the manager does not directly administer
  guests or provision their infrastructure.
- The first useful journey is **new VM → configured guest → working service**.
  This is a product outcome, not approval for all adjacent features at once.
- Every Git change first becomes a proposal with a preview. **Changed (October 16,
  2026):** whether the proposal then waits for approval is decided by each
  resource's approval setting, and the default is automatic. VMs, services, or a
  whole lab can be set to manual (section 7). This replaces the earlier default of
  propose, then approve.
- Independent work should have independent outcomes. A failure must be easy to
  locate and should not unnecessarily derail unrelated targets. Host, guest, and
  service failure boundaries must preserve sibling independence.
- Independent resource workflows may progress in parallel, with configurable
  concurrency limits to avoid overloading execution machines and targets.
  Fleet-wide stage barriers are not the desired operating model.

PostgreSQL is the selected operational database (see section 5). Section 6 holds
the user's stack: a .NET backend, a separate React frontend, and a Rust agent, with
generic packaging. The parts decided so far are marked there, including gRPC for
agents and the Git CLI; the orchestration implementation is still a proposal.
Section 7 describes the lab repository.

## 2. Resource relationships

### Lab

A **lab** is a group of related infrastructure, usually associated with a place.
It may contain multiple standalone Proxmox hosts, clusters, or a mixture.

Lab membership does not imply common credentials. Nor should the organizational
group be mistaken for one state backend, one execution lock, or guaranteed network
reachability. The grouping and the authority to execute are different concerns.

### Proxmox installations, clusters, and hosts

Use **Proxmox host** for the individual server the user also called an
“instance.” Use **Proxmox installation** for the management domain: either a
standalone host or a cluster.

- Each host has its relevant configuration: endpoint, storage/datastore
  references, networking, and other host-specific variables.
- A cluster can share an API credential reference across its member hosts.
  Independent installations need not share credentials.
- Cluster membership and cluster-wide VMID scope are distinct from the lab group.
- Host settings remain available even when management credentials are shared.
  Exact endpoint selection and configuration precedence remain open.

There is no decision to manage Proxmox cluster creation, membership, HA, or storage
pool creation. Defining an existing installation is not the same as owning all
of its administration.

### Guests and their contents

VMs and Proxmox containers are persistent managed resources. Provisioning them is
an operation, not their identity. Their placement, configuration, software, and
service relationships must be understandable separately.

The discussion uses “Proxmox container” for the guest layer and “Docker container”
for the workload layer. Exact initial guest/container support still needs selection.

“Apps” is not retained as one undifferentiated bucket. The user accepted a more
granular split between setup, machine maintenance, tool updates, and service
deployment. Frequently updated software is still software; update frequency is
policy, not another level of the resource hierarchy.

Services may need network exposure and DNS. DNS also has operations independent
of services. **Decided (October 19, 2026):** a service declares its exposure
generically, and the lab connects it to the systems it uses through integrations.
For now that means DNS records through Technitium and ports opened in the guest's
operating system; routes stay with the reverse proxy on each apps machine.
Application clusters such as Technitium relate service instances on
several guests; forming one is its own step after its members (section 7).

Reusable machine/setup profiles are a working organizing concept. Their contents,
composition rules, and representation have not been chosen.

## 3. Operations, reconciliation, and evidence

### Declarative execution

Operations must be declared explicitly; Octopushy cannot infer the intended
execution or removal of arbitrary scripts. The supported set should be able to
grow gradually, rather than assume every action is known initially.

Initial operation categories discussed:

- **Provision:** establish or change guest infrastructure.
- **Setup:** establish the desired guest configuration and prerequisites.
- **Maintain OS:** perform system maintenance and any required controlled restart.
- **Update tools:** update selected software separately from general OS maintenance.
- **Deploy service:** establish or update a service installation.

These are working categories, not an exhaustive fixed API. An onboarding journey
may coordinate several of them; a later service deployment should not implicitly
reprovision its VM.

Changes to setup, including additions and removals, must be reconciled
deliberately. Operations over the same software should share an authoritative
definition so that rerunning setup does not unintentionally reverse a tool update.
How removals are expressed depends on the native configuration and still requires
an explicit lifecycle contract.

### Failure visibility and recovery

Distinguish an operation, a run at a fixed configuration/target set, its visible
steps, and attempts made during recovery. Preserve previous outcomes.

A useful example is:

- VM provisioning succeeded.
- Guest access and baseline configuration succeeded.
- Runtime setup failed.
- Service deployment is blocked by that prerequisite.

That must not collapse into an unexplained “deployment failed.” The recovery
interface should identify what would repeat, what would be skipped, and which
prerequisites need checking again.

Detailed failure reporting does not imply that every underlying task can safely
resume independently. Supported restart boundaries and uncertain-outcome handling
must be explicit, rather than a generic retry of arbitrary code.

### Failure isolation and independent progress

The user confirmed these failure boundaries:

- A Proxmox-host-level failure blocks dependent work for that host and its guests,
  not unrelated hosts.
- A guest-level failure blocks that guest's dependent setup, maintenance, tool
  updates, and services, not other guests on the same host.
- A service-specific failure affects that service's deployment instance, not
  unrelated services on the same guest.

Do not turn a child's failed operation into an ancestor-wide execution block
merely because the ancestor's dashboard summarizes a failure. Work blocked by a
prerequisite must be distinguishable from work that ran and failed.

Each resource's workflow can advance when its own prerequisites are satisfied.
For example, guest B may proceed to service deployment while guest A is still
provisioning or recovering. There is no implicit "all guests provisioned" or
"all guests configured" barrier before another resource can progress.

Aggregation is for reporting, not execution. A request affecting many targets
must retain independently progressing operations, outcomes, and recovery
boundaries, rather than become one fleet-wide operation. A grouped progress
view can be added later; its existence or completion must not gate the work.
Native-tool invocation and state ownership must support this independence,
not conceal a coupled fleet-wide job behind separate dashboard entries.

The classification of host-level versus guest-level failures still needs
confirmation. The proposed rule is that a failure creating one VM does not
automatically make its Proxmox host unusable; the evidence and affected
prerequisites determine the scope. Genuine shared prerequisites, such as a
cluster-wide dependency, can affect several otherwise independent workflows.
Their declaration and verification remain design work.

Here, blocking means withholding dependent operations, not shutting down existing
workloads. Diagnostic and recovery actions must remain possible for the blocked
resource. Handling already-running operations when a prerequisite becomes
unavailable, and the checks required to lift a block, remain open decisions.

### Bounded parallelism

Parallel independent progress is a requirement, but unlimited concurrency is not.
Work awaiting capacity should be distinguishable from work blocked by a failure.
The exact limits, their scope, and scheduling fairness are not yet selected.

The user accepted configured capacity limits as the starting approach rather
than metrics-driven scheduling. Execution-worker capacity and target-host capacity
are different constraints: moving execution elsewhere does not necessarily
reduce load on the target. Conflicting mutations also need exclusion regardless
of spare capacity, including operations sharing infrastructure state or guest
configuration. The lock model remains open.

The user raised load distribution as a possibility, not a settled mechanism.
Distributing operations among eligible execution workers is separate from
placing new guests on Proxmox hosts or migrating existing guests. Eligibility
must account for access, capabilities, and required state/artifacts, not just
available capacity. No automatic placement, migration, or live load-balancing
system is committed.

### Preserve prepared work rather than chase spare capacity

The user prefers a short wait over moving work repeatedly and paying for a large
recomputation. Avoiding unnecessary recomputation and execution churn takes
priority over continuously selecting whichever machine currently appears least
busy.

A proposed interpretation is stable execution assignment: after work has been
prepared on an eligible worker, prefer keeping its execution context there
through temporary capacity contention. Placement of new work can still consider
available capacity. The exact assignment lifetime and waiting/failover policy
are not settled; this is not a commitment to automatic guest migration.

Preserving work must not mean blindly reusing stale results or skipping safety
checks. Reuse requires appropriate revision, input, dependency, and freshness
validation. Durable records and reusable artifacts should remain distinct from
an in-progress process whose outcome is unknown. If execution is lost, recovery
must establish what happened before starting a replacement mutation elsewhere.

No distributed cache, universal checkpoint/resume mechanism, or automatic
failover implementation has been selected.

### Verification

Verification is part of the agreed direction: check that the relevant declared
requirements are satisfied, rather than trust only command exit status.

Keep these distinct:

- Desired configuration.
- The result of an execution.
- OpenTofu's infrastructure bindings and recorded attributes.
- Verification evidence and independently observed health.

Checks should explain what they establish and when. Exact status names, evidence
storage, freshness rules, and periodic observation schedules remain design work.
Metrics aggregation is a later concern, not a reason to build a monitoring platform
now.

## 4. Working proposals that still need confirmation

These refinements were discussed or recommended, but must not be mistaken for a
fully approved interface:

1. **Migration-inspired lifecycle recipes.** The user proposed explicit `up` and
   `down` behavior for arbitrary procedures. Naming these `apply`, `verify`, and
   `remove`, making removal optional, and defining separate retirement stages are
   proposed refinements—not a finalized YAML schema.
2. **Supported executors versus recipes.** A small set of supported mechanisms
   could execute reusable Git-defined recipes. The exact meaning of “accepted
   operations,” admission/trust rules, and initial executor set remain open.
3. **Safe removal and compensation.** Do not assume `down` is a complete rollback
   or run it automatically after a partial `up`. Explicit data-deletion approval,
   retained retirement information, and dependency checks are recommended
   guardrails whose concrete policies still need agreement. **Decided (October 16,
   2026):** deletes (of a VM, service, data, or DNS record) have their own setting,
   which defaults to the resource's approval setting; recreating a resource counts
   as a deployment (section 7).
4. **Version policy.** Exact Git pins and Git-declared update channels are different
   desired-state models. Selection, publication, verification, and retaining the
   exact resolved version need definition; the `nodes` repository contains examples
   of both approaches.
5. **Policy boundaries.** Automatic versus manual authorization may differ for a
   VM, service, tool update, and destructive operation. **Decided (October 16,
   2026):** VMs and services each have an approval setting, automatic by default;
   manual changes wait for approval, and settings flow down the lab tree with the
   nearest winning (section 7). Destructive operations have their own setting
   (item 3). Maintenance windows are optional, and a resource can have several
   (October 19, 2026). Tool updates are not settled.

No illustrative configuration in the conversation is an implemented parser or
approved public format.

## 5. Server building blocks

Discussed as "draft 02" of the architecture. The first list records the user's
decisions; everything after it is a working proposal awaiting confirmation.

### Confirmed decisions

- **PostgreSQL** is the operational database for runs, steps, attempts, approvals,
  locks, leases, evidence, and the variable store.
- **Executors start concrete.** Build around Proxmox hosts and the VM lifecycle
  first, then generalize into a plugin interface once that works. Prefer
  self-contained choices: executors built into the agent rather than separately
  installed plugin runtimes.
- **Secret sources are provider-agnostic.** A built-in store must work, and so must
  a simple environment file or environment variable. Infisical can remain one
  provider among others rather than a requirement.
- **Values are created manually**, with bulk import and export in both encrypted
  and plain form.
- **The store is a key/value dictionary.** Octopushy does not attach meaning to
  entries; each entry carries a setting that conceals it (hidden/encrypted).
  [Research on Jenkins' approach](research/secret-storage.md) informs the
  proposal below.
- **Secrets a service needs are prerequisites of its deployment**, not merely
  variables passed to it.
- **Octopushy manages the lifecycle**, including restarts after a secret change.
- **Octopushy keeps its own snapshot** of the Git definitions (operations, and the
  connections between steps where configured). When it detects a new change, it
  computes the difference and updates according to policy.
- **UI changes can be written back to Git**, so Octopushy needs Git write access
  in addition to read access.
- **Two visibility levels:** each stored entry is plain or secret.
- **UI edits are drafts first.** Octopushy keeps drafts on its side. Publishing is
  an explicit action; Octopushy then validates, merges, and commits automatically,
  handling merge conflicts itself.
- **Editable is not the same as owned.** Octopushy's own forms change only its
  definition files. The integrated editor can change any file in the repository,
  like a normal editor. Scripts and other tool-native files are still not owned
  by Octopushy: it does not generate, rewrite, or manage their lifecycle.
- **Agents do not read environment-file or environment-variable providers**; those
  sources are resolved on the server.
- **The integrated editor is Monaco**, embedded in Octopushy's UI.
- **Single user.** Octopushy has one user. Several drafts can exist at once, and a
  draft's conflicts must be resolved before it can be published.
- **Host information and metrics** gathering is wanted but deferred.

### Proposed system shape

- The control server knows contracts and holds records; it runs no tool code.
  Agents run executors and report results.
- Every executor follows one contract: `validate`, `preview`, `apply`, `verify`,
  `observe`, and optionally `remove`. A result is succeeded, failed, or
  **uncertain**, with typed outputs and evidence. Outputs such as a VMID or IP
  address become inputs to later steps, so executors need not know each other.
- Agents connect outbound. Enrollment uses a one-time, expiring token; the agent
  generates its own key pair and receives a short-lived, renewable credential.
  What an agent may run is server-side policy, not the agent's claim.
- Each assignment carries an attempt-scoped token, and secrets are released only
  to that attempt. A lost lease makes the attempt uncertain, not failed; recovery
  verifies before any replacement mutation.
- Resource status is derived from desired revision, outcomes, verification,
  observations, and blocks. It is not one stored field. Attempt states include
  queued, waiting for capacity, blocked, awaiting approval, running, succeeded,
  failed, uncertain, and cancelled.
- Logs and artifacts live outside database rows. OpenTofu state stays in its native
  backend; Octopushy reads it rather than owning it.

### Proposed variable and secret store

- **Entry:** key, value (multi-line allowed), visibility, description, optional
  usage scope, and version history. *Plain* values can be read back; *secret*
  values are write-only after saving, masked in logs, and cannot be downgraded to
  plain. All values are encrypted at rest regardless of visibility.
- **Keys:** envelope encryption with a key-encryption key held outside the database
  and its backups, such as a protected file or systemd credential. Values use
  authenticated encryption bound to their key name and version, unlike Jenkins'
  unauthenticated AES-CBC under an unencrypted master key.
- **Linking to Git:** definitions reference store keys, never values or providers.
  Server configuration routes each key namespace to exactly one provider (built-in,
  environment file, environment variable, Infisical, …). There are no fallback
  chains, and an unresolved reference blocks the operation instead of becoming an
  empty value.
- **Delivery:** the Git binding chooses the form (environment variable, or file
  with path, owner, and mode); the stored entry does not.
- **Reference index:** the snapshot lists referenced-but-missing keys, unused keys,
  and each key's consumers. Deleting a key still referenced by the current or any
  in-flight revision is refused.
- **Import:** `.env`, JSON/YAML, or an encrypted bundle, previewed as a diff before
  it applies. New keys default to secret.
- **Export:** plain variables export in plain text. Secrets export as an
  [age](https://age-encryption.org)-encrypted bundle (passphrase or recipient key)
  that standard tools can decrypt offline. Plain secret export requires
  re-authentication, explicit confirmation, and an audit record, and can be disabled.
- **Audit:** every resolution records key, version, attempt, and agent, never the
  value. Octopushy's own credentials, such as Git keys and the agent CA, sit in a
  system scope that recipes cannot bind.
- **Changes are change sources:** a new value marks its consumers as drifted and
  produces a proposal, as a Git change does.

### Proposed secret prerequisites for services

For each service instance: the secret exists in its provider → the guest's
delivery path is ready → the secret is materialized on the guest (present, owner
and mode correct, expected version) → deploy → verify. Materialization is an
Octopushy step or only a check, depending on the delivery path.

| What's wrong | Shown as | Affects |
|---|---|---|
| Reference does not resolve | blocked: missing secret | every consumer of that key |
| Guest delivery path unavailable | blocked: secret delivery unavailable | that guest's services needing secrets |
| File missing, wrong owner/mode, or old version | blocked: secret not ready | that service instance |
| Deploy fails with secrets ready | failed: deploy | that service instance |
| Provider has a newer version | drift: secret outdated | proposal for its consumers |

When several services share a secret, rotation may need an order (for example,
the database accepts the new password before the application switches). Declare
an order only for secrets that need one.

### Proposed change detection and Git write-back

- Git commits, store changes, and observed drift all feed the same flow: a
  resource-level diff against Octopushy's snapshot, then a proposal or an allowed
  automatic action.
- Detect commits by polling, which works with any forge, plus optional webhooks.
- A UI edit becomes a Git commit, never a direct snapshot edit; it then flows
  through the same detection. Store values are edited in the store, never in Git.
- **Draft:** a set of edits to any repository files on top of a base revision,
  kept as a local Git branch in Octopushy's own clone so it survives restarts and
  can use Git's merge machinery. It is never pushed until published.
- **Publish** (explicit): validate the draft → fetch → if the tracked branch
  moved, merge the draft onto the new head and validate the merged result again →
  commit → push.
- **Validation depth:** Octopushy definition files get full validation (parsing,
  schemas, reference index, dependency graph). Other files get only format
  checks, such as YAML or JSON parsing. Tool-native mistakes surface in the
  resulting proposal's preview, such as an OpenTofu plan, before anything applies.
- **Conflicts:** a draft that cannot merge cleanly, whether with commits made
  outside Octopushy or with another draft published first, is marked conflicted.
  It cannot be published until its conflicts are resolved in a three-way view.
  If the branch moves again during a push, retry fetch, merge, validate, and push
  a bounded number of times.
- Failed validation or a rejected push leaves the draft intact with the reason
  shown. Committing is not applying: the commit produces a proposal like any other.
- Following the existing `nodes` updater, never force-push.
- Use separate Git read and write credentials, both in the system scope.

### Integrated editor

The editor is Monaco, VS Code's editor component, embedded in Octopushy's UI
([options review](research/integrated-editor.md)). It saves into drafts; only
Octopushy's publish path pushes to Git. A self-built VS Code for the Web and
code-server/OpenVSCode Server are not planned. Monaco provides a two-way diff
editor, so the three-way conflict view is Octopushy's to build.

## 6. Proposed stack, packaging, and updates

**The user's proposal (October 15, 2026):** a .NET 10 server with EF Core, and
SignalR where needed; a Rust agent; a Vite + React + TypeScript UI. Everything
should be self-contained enough to deploy on one node, and both the server and its
agents should update themselves and stay in sync with each other. Octopushy should
not need its own Proxmox client: VMs are managed by running OpenTofu. The
[stack review](research/stack-review.md) checks this proposal; everything below is
a working proposal awaiting confirmation unless marked as decided or accepted.

**Decided (October 20, 2026):**

- Docker images are built by hand for now. Releases are named by versions of
  Octopushy's repository. Publishing, signing, and automatic updates wait until
  releases are published.
- Agents carry out the work on machines, including their updates. One agent that
  can reach every lab is enough; more agents only make work faster. No network
  needs an agent of its own.
- No backups for now; a database backup comes later.
- The server runs on one host, and agents can run on other hosts.
- gRPC for agents and the Git CLI, for now.
- Octopushy's code is one repository with `backend/`, `frontend/`, and `agent/`
  projects, as proposed below.

### Stack

- **Backend and frontend are separate (decided October 16, 2026):** the user splits
  the server into a backend and a frontend, built and deployed separately so each is
  easier to manage. "Server" below means both together.
- **Backend:** ASP.NET Core on .NET 10 (LTS until November 2028), published
  self-contained but not Native AOT, because EF Core's AOT support is still
  experimental. EF Core with Npgsql for the domain model and migrations; plain SQL
  for the work queue and leases (`FOR UPDATE SKIP LOCKED`, `LISTEN`/`NOTIFY`). The
  orchestrator is Octopushy's own code, not a job framework whose retry model
  conflicts with attempts and uncertain outcomes. The backend holds the API, the
  agent channel, the orchestrator, Git, and the variable store.
- **Agent:** a static Rust binary (tokio, tonic, rustls). Its first executor drives
  OpenTofu, which already owns the VM lifecycle in `nodes` through the bpg/proxmox
  provider. **Decided (October 15, 2026):** direct Proxmox API access goes through a
  dedicated Rust library of Octopushy's own. It starts with what `nodes`'
  `homelab/scripts/tofu` wrapper does around OpenTofu today (locate the VM and check
  the token's permission, read the VM's IP address and SSH host key through the
  QEMU guest agent, toggle VM protection around a destroy) and grows as needed.
- **Tool execution:** the executor logic is built into the agent; the tool it drives
  (OpenTofu, Ansible, Nix) runs in a pinned container image that the agent starts
  in a rootless container, with a private workspace per attempt. The agent holds no
  policy and no state beyond its credential, its in-flight attempts, and an outbox
  of unsent results.
- **Agent channel (decided for now):** gRPC bidirectional streaming over mutual TLS,
  on a port of its own. One Protobuf contract generates both the .NET and Rust
  sides.
- **UI channel:** REST with a TypeScript client generated from the backend's OpenAPI
  document; SignalR only to push state changes and log tails to the browser.
- **Frontend:** Vite + React + TypeScript with Monaco, shipped as its own image: the
  static files served by a small web server that also forwards `/api` and the
  SignalR hub to the backend. The browser therefore sees one origin, which keeps
  passkeys and session cookies simple and avoids CORS. One JSON Schema per
  definition kind validates on the backend and in the editor.
- **Frontend and backend versions:** they no longer ship as one program, so the
  frontend checks the backend's API version when it loads and asks for a reload on
  a mismatch. The backend stays compatible with the previous frontend for one
  release, and updates go backend first.
- **Source layout (decided):** one Git repository for Octopushy's code, with
  separate projects (`backend/`, `frontend/`, `agent/`) and the contracts they
  share: the Protobuf agent contract, the OpenAPI document that generates the
  frontend client, and the JSON Schemas for lab definitions. A contract change and
  both sides of it then land in one commit.
- **Git (decided for now):** the Git CLI. `git merge-tree --write-tree` merges a
  draft and reports conflicts without a working tree. LibGit2Sharp's SSH support
  depends on an old community package.
- **Sign-in:** a local account with passkeys, which ASP.NET Core Identity supports
  in .NET 10. OIDC can come later.
- **Releases:** one version number for backend, frontend, and agent, taken from
  Octopushy's repository (proposed: a Git tag such as `v0.1.0`). For now the images
  are built by hand; a pipeline that builds and signs them comes with publishing.
  The artifacts are listed under Packaging and placement.

### Packaging and placement

**Generic packaging (October 15, 2026):** the user wants Octopushy to be generic
rather than tied to NixOS. The single Compose project stays set aside (its analysis
is in the [stack review](research/stack-review.md)), and so does NixOS as the base
packaging proposed by the
[deployment alternatives review](research/deployment-alternatives.md). That
review's other findings still apply: separate placement is what protects the server
and its key, and no packaging makes an update safe by itself. **The user accepted
this generic direction on October 16, 2026.** Release publishing is deferred: for
now the images are built by hand (October 20, 2026).

- **Host requirements:** any Linux host with systemd and a container runtime that
  can run a Compose file: Docker Engine with the Compose plugin, or Podman through
  its Docker-compatible API. Nothing depends on a distribution's own packaging; on
  NixOS the same pieces are declared in the host's configuration.
- **Artifacts:** one release consists of:
  - the backend as one OCI image, containing the Git CLI and the EF Core migration
    bundle;
  - the frontend as a second OCI image;
  - PostgreSQL as the official image, pinned to a major version;
  - one static Rust binary, `octopushy`, for x86_64 and aarch64, which runs either
    as an agent or as a supervisor (below);
  - one release manifest naming all of them by digest, signed once releases are
    published.

  The backend image also carries its release's agent binaries, so agents always
  download the version their server expects.
- **Control host:** the backend, the frontend, and PostgreSQL run as one Compose
  project. No
  container in it mounts the runtime socket, and no tool code runs there.
- **Supervisor:** the `octopushy` binary in supervisor mode, running as a native
  systemd service on the control host. It installs and updates the Compose project
  and is the only Octopushy component with access to the container runtime. It runs
  no executors: it enrolls with the server like an agent, but the server never
  assigns it work.
- **Agents:** the same binary in agent mode, a native systemd service under its own
  unprivileged user, on any host that can reach the labs it serves. One agent can
  serve every lab; more agents run more work at once. It is installed with one
  command and a one-time enrollment token, then updates itself from its server.
- **Tool isolation:** each attempt runs its tool in a rootless container that the
  agent starts under its own user (Podman preferred, rootless Docker accepted),
  with only its private workspace and that attempt's secrets: no runtime socket, no
  privileged mode, no access to the agent's identity or outbox. The agent's user is
  never in the `docker` group, which is equivalent to root.
- **Placement (decided October 20, 2026):** the server runs on one host, and agents
  can run on other hosts. An agent can also share the control host, under another
  user with its own rootless runtime; users on one host share a kernel, so that
  boundary is weaker than a separate VM.
- **Key-encryption key:** generated at install into a file on the control host that
  only Octopushy's service identity can read, mounted into the server container as
  a Compose secret rather than an environment variable or an image layer, and kept
  out of backups. Escrowing it waits for database backups.
- **Optional adapters later:** a NixOS module (which would fit `nodes`) or Podman
  Quadlets can install the same artifacts and supervisor, but none is required.
  With an adapter, the supervisor still owns which release is installed, so two
  systems never compete over versions.

### Updates

- **No Watchtower and no host-specific updater.** The supervisor and agents update
  Octopushy themselves. Until releases are published, an update starts by hand:
  the images for a version are built, and the supervisor installs them with the
  procedure below. Once releases are signed, only signed releases are installed.
- **Release selection (later):** once releases are published, one proposal is that
  the server watches a release channel and verifies each new manifest, and that the
  release in use is pinned in the lab repository (`octopushy.yaml`, section 7), so a
  new release arrives as a proposed change like any other: the server opens it as a
  draft, and Renovate could do the same. Merging it, by approval or under an
  automatic policy, starts the update.
- **Server updates** are run by the supervisor, not the server process, with a
  durable journal:
  1. Verify the manifest and pull the new images by digest; keep the previous ones.
  2. Enter maintenance: pause dispatch and database writes. Agents keep running
     and queue results.
  3. Dump the database (a one-shot `pg_dump` container), the server's Git clone,
     and unpublished drafts on the host. This is not a backup, which comes later,
     but the only way back from a failed migration (proposed).
  4. Run migrations with the release's migration bundle, as a one-shot container
     under a separate schema-owner role. EF Core advises against migrating at
     application startup in production, and not every migration runs in a
     transaction.
  5. Recreate the backend container from the new image and check readiness
     (database, schema, Git, agent channel), then the frontend, before reopening.
- **Failure:** if the new version fails, recreate the previous images only if they
  support the resulting schema; otherwise stop for deliberate recovery.
- **Previous images are not data rollback.** An older image restores the program,
  not the data. Restoring the dump after writes resume loses later results, so it
  is disaster recovery, not routine rollback.
- **Migrations:** expand/contract, staying compatible with the previous release.
  Destructive schema changes are separate, explicitly approved maintenance.
  PostgreSQL is pinned to a major version; a major upgrade is planned work
  (`pg_upgrade` or dump/restore, keeping the old cluster).
- **Agents** update after the server, one first. Each agent downloads its new
  binary from its server, checks it against the release manifest, and stages it
  beside the current one. It drains before switching: it refuses new assignments,
  finishes its attempts, and flushes its outbox. Activation waits while the agent is
  busy; a stop timeout must never kill an apply.
- **Revert:** before switching, the agent arms a systemd timer that runs the
  previous binary to switch back. The new version cancels it only once its server
  confirms it, so a version that crashes, hangs, or cannot reconnect is reverted,
  much as deploy-rs reverts a deployment it cannot confirm. The supervisor updates
  itself the same way, after the server.
- **Host maintenance:** on a generic host, OS updates and reboots belong to the
  host's own tooling, which Octopushy does not control. The agent holds a systemd
  shutdown inhibitor while attempts run, and host maintenance should be scheduled
  outside long attempts. Whether common unattended-upgrade tools respect the
  inhibitor is not verified.
- **Release trust (later):** once releases are published, the release key signs the
  manifest, and its public key is pinned at install. Images are pulled by the
  digests it names, and agent binaries are checked against it before activation.
  Downgrades and replayed manifests are refused without an explicit recovery
  override. Until then, an agent trusts the binary its server sends over mutual
  TLS.
- **Automatic updates (later)** are an explicit, audited policy for compatible
  releases on a chosen channel. Changes to the lab repository follow their own
  approval settings (section 7).
- **A server restart must not make running work uncertain.** Agents keep executing,
  queue their results in the outbox, and reconnect; lease grace periods outlast an
  update.
- **Agent settings:** everything else an agent needs (labels, allowed executors,
  concurrency) comes from the server on connect. The agent stores only its
  credential and the server address.
- **Updates are Octopushy operations from the start.** Each update appears in the
  UI with its journal: target, dump, migrations, readiness, drain, revert, and
  outcome. The supervisor keeps the journal locally and reports it once the server
  is back, so completion never depends on the restarting server.

## 7. Lab repository

The user asked for the architecture of the Git repository that defines labs, their
Proxmox hosts, VMs, and services (October 16, 2026). The
[lab repository proposal](lab-repository.md) has the layout, rules, and example
definitions.

**Decided (October 16, 2026):**

- One repository can hold several labs, each with its children nested in folders:
  lab → Proxmox installation → guest → service instance.
- A VM's definition holds all of its configuration: placement, hardware, network,
  profiles, access, and policy. Templates and profiles may prefill a new VM's form,
  but the values are written into its file.
- The dashboard shows the resources and their references as a searchable graph, for
  overview and navigation.
- Each service instance is standalone: it belongs to one VM and holds its own
  files. The same application on two VMs is two independent folders.
- VMs and services each have an approval setting, automatic by default; when it is
  manual, every proposed change to that VM or service waits for approval. Settings
  flow down the tree and the nearest wins. Approving a VM change never approves its
  services.
- Each VM has its own OpenTofu state, so VMs plan and apply in parallel and fail
  independently.
- Definitions are YAML.
- Deletes have their own setting, `policy.destructive`. When it is not set, it
  takes the resource's approval setting, so a manual resource never deletes without
  approval; it can be set to manual on its own to hold back only deletes.
- Recreating a resource counts as a deployment and follows the approval setting.
- A service declares its own DNS records, because the service does not work if
  they fail. DNS is shared across the lab, so its zones and the systems serving
  them are defined once on the lab.

**Decided (October 19, 2026):**

- Network exposure is declared generically in the service: DNS records, proxy
  routes, firewall ports, and certificates. The lab connects it to whatever systems
  it uses, such as Pi-hole or Technitium for DNS, through integrations, each with an
  adapter for its product.
- Application clusters are supported, starting with Technitium, on a basis other
  kinds can reuse. Each member is its own service on its own VM; forming the
  cluster is a separate step that sets up one member and joins the others to it.
  The cluster relates its members.
- Maintenance windows are optional, a resource can have several, and they are not a
  priority.
- Technitium is the only DNS integration for now; no other adapters are planned
  yet.
- Firewall ports are opened in each guest's operating system, probably by a step
  of their own.
- Each apps machine runs its own reverse proxy, with no central proxy planned, so a
  zone belongs to one lab. Sharing zones between labs may come later.

**Decided (October 20, 2026):**

- Ports are as permissive as possible: open on every interface and to every source
  unless the declaration limits them to interfaces, source networks, or both.
- How the ports step is implemented still needs checking.

**Proposed:**

- Guests sit under the installation, not the host, so migrating a VM within a
  cluster changes a field rather than its folder.
- Moving a folder to another parent creates a new resource; Octopushy proposes
  retiring the old one once the new one is verified. Renames are declared.
- Shared parts sit at the repository root: `templates/`, `profiles/`, `recipes/`,
  and `modules/`.
- Agent labels flow down the tree the same way as the approval setting.
- Destructive means deleting a guest, a service, data such as disks or volumes, or
  a DNS record. Octopushy decides what a change does from the preview (such as an
  OpenTofu plan's delete and replace actions), not from the edit, and shows a
  recreate as a recreate. Retiring a guest retires its services first.
- Each DNS record has one owner, and creating it is a step of that service's
  deployment, verified by resolving it. A failure affects only that service:
  changes to one zone are applied one at a time, but each record has its own
  outcome.
- Applying ports is a step on the guest that follows the service's approval. On
  NixOS it passes the ports into the guest's configuration and rebuilds from the
  guest's applied revision, so it never applies unapproved guest changes.
  Interface limits use the NixOS firewall's per-interface ports; source limits
  need extra rules.
- Routes and certificates stay in each service's tool-native files (Traefik labels
  and ACME) and are not managed for now.
- Every integration adapter reads, applies, removes, and verifies, so previews show
  exact changes. Octopushy records the entries it creates and touches only those;
  adopting an existing entry needs approval. Adapters ship with Octopushy and run
  on an agent.
- An application cluster is `labs/<lab>/clusters/<id>/cluster.yaml`: a recipe type,
  member services with roles, and shared settings. The recipe forms, joins,
  leaves, and verifies; each join has its own outcome. Removing a member is a delete
  under the cluster's own destructive setting.
- A maintenance window only limits when an automatic change may start; started
  attempts finish, and approving a manual change runs it at once unless the
  approver chooses the next window.
- Hardware and network are inputs to the provisioning recipe, validated against
  its schema, so one shared OpenTofu module provisions every guest.
- Secrets are `{ store: <key> }` references; removal is an explicit
  `lifecycle: retired`, never a deleted file.
- **Outside Git:** variable-store values, provider routing, agent enrollments,
  drafts, runs, and evidence live in the backend; infrastructure state stays in the
  OpenTofu backend.

## 8. Decisions to make before implementation

Use the concrete cases in [the comparison](nodes-comparison.md) to answer:

- What native guest-management path proves the first journey, and what is the
  authoritative configuration owner for each component?
- What is the first recipe contract: inputs, execution location, verification,
  retry boundary, removal behavior, and trust?
- Which state backend is appropriate, and how could existing ownership be handed
  over without competing writers or lost infrastructure bindings? (State
  granularity is decided: one state per VM, section 7.)
- How are dependencies and conflicts expressed across guest setup, service
  deployment, networking, DNS, credentials, and active workloads?
- What evidence establishes a host-wide, guest-wide, or service-specific block,
  and which recovery checks clear it? What happens to work already running?
- Where should concurrency limits apply, and does initial load distribution mean
  execution-worker selection, guest placement, or neither?
- How long should prepared work retain its execution assignment, what evidence
  permits reuse, and when should waiting become an explicit recovery decision?
- What constitutes verified completion, and what survives agent/controller
  interruption so recovery can distinguish unfinished work from missing evidence?
- Which existing cases are required for the first milestone, and which are simply
  future compatibility considerations?

Open questions from section 5:

- Should drafts check for upstream changes only when published, or continuously
  so conflicts show up while editing?

Open questions from section 6:

- Is a version a Git tag on Octopushy's repository, and do hand-built images stay
  on the control host or go to a registry?
- Should the Octopushy version in use be pinned in the lab repository, so each
  update arrives as a change to approve?
- May a long-running attempt postpone an update indefinitely?

Deferred until releases are published or backups are added:

- Where are releases published (for example GitHub Releases and a container
  registry, or self-hosted), where is the signing key kept, and which channel may
  update automatically, in what maintenance window?
- What backup retention and restore target are acceptable for the database, and
  where is the key-encryption key escrowed?

Open questions from section 7 are listed in the
[lab repository proposal](lab-repository.md#open-questions): how the ports step is
implemented, including how source limits are written, still needs checking.

Section 5 holds working proposals for the secret store and agent enrollment.
Execution isolation, notifications, rollout policies, live health, backup/restore,
and any integration with existing CI still require concrete decisions. None of
these are commitments to copy the corresponding Jenkins facilities.

**Deferred:** Talos and Flux are out of the current design discussion at the user's
request. Earlier proposals about them are not current scope. Metrics, including
host information gathering, are also later work. Neither the old PoC nor the full
`nodes` feature set defines the next milestone.
