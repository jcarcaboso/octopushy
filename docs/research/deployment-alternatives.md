# Deployment, packaging, and update alternatives

**Reviewed: October 15, 2026.** Scope: alternatives to the single Compose project,
with the server, agent, PostgreSQL, protocol, and variable-store constraints in
the research brief held fixed. Documentation review only; no deployment,
failure-injection, restore, sandbox, or real-host tests were performed.
Recommendations are engineering proposals, not decisions.

**Superseded in part (October 15, 2026):** the user wants Octopushy generic rather
than tied to NixOS, so NixOS is no longer proposed as the base packaging. Section 6
of the [architecture agreement](../architecture-agreement.md) now proposes OCI
images and a native `octopushy` binary on any systemd Linux host, with Octopushy's
own updater; see the portability follow-up below. Placement, tool isolation, and
the shared update contract still apply.

## Conclusion

Propose **NixOS modules with native systemd services, a dedicated server VM, and
separate agent VMs at the networks that need them**. Use rootless Podman for
attempt-scoped tool containers on agent VMs, not for the control server.
NixOS already fits this lab; retaining generations is simpler than building a
binary updater or an appliance distribution. However, **NixOS generations are
not database rollback, application-health rollback, or an agent drain protocol**.
Those still need a small, explicit update coordinator. [D1], [D2], [L1]

The runner-up is **rootless Podman Quadlet for the server, native agents on
separate VMs, and the same guarded update protocol**. Podman supplies useful
failed-start rollback and image-signature policy, but uncoordinated
`podman auto-update` is insufficient for migrations and busy agents. [D5]–[D8]

The important separation is **packaging versus placement versus update safety**.
Splitting VMs fixes the most serious trust-boundary problem regardless of
packaging. No option makes rollback of mutable PostgreSQL data automatic.
Kubernetes adds another control plane without removing these requirements;
there is no compelling reason to add it here. Talos and Flux remain deferred.

## Existing homelab fit

Read-only inspection found NixOS role templates, a rootless Docker owner named
`containers` with lingering enabled, Compose services, and Traefik with both
Docker discovery and a dynamic-file directory. OpenTofu uses `bpg/proxmox`,
HTTP state backends, and GitLab backend credentials supplied by the wrapper;
the wrapper also calls Proxmox directly. The krb, mrb, and umb names suggest
multiple sites, but do not prove network reachability. [L1]

Keep those infrastructure/state owners. Add Octopushy as a new role rather
than converting the existing service fleet to Podman. Route Traefik to a native
server's private address using its file provider; keep the outbound agent mTLS
listener reachable, either directly or through a verified gRPC route. The
existing Renovate configuration enables **only `docker-compose`**, so Nix
updates are not already covered: enable the Nix manager, lock maintenance, and
an explicit automation policy. Renovate documents its Nix manager as opt-in and
beta. [L1], [D3], [D18]

**Correction (parent review, October 15, 2026):** the fleet is flake-based.
`homelab/flake.nix` pins nixos-26.05, defines each node as a
`nixosConfigurations` entry, and deploys with deploy-rs. deploy-rs re-activates
the previous profile when activation fails (auto rollback) and when it cannot
reconnect within `confirmTimeout`, 30 seconds by default (magic rollback). Both
default to on. That covers failed activation and lost connectivity, not
application readiness or data. [L1], [D24]

## Comparison

All rows require the shared migration, drain, and version rules below.

| Alternative | Update and rollback mechanism | Isolation and placement | Bootstrap and day-2 cost |
|---|---|---|---|
| **A. NixOS modules — proposed first choice** | Pinned release/flake input, guarded `nixos-rebuild`; retained generations restore executable/service configuration, not data. No built-in application-health reversal in `system.autoUpgrade`. [D1], [D2] | Native server and PostgreSQL on control VM; native agents and rootless attempt containers on separate VMs | Add modules to existing NixOS templates. New packaging and update guard, but reuse OS management. Lowest fit cost for this lab |
| **B. Podman Quadlet — runner-up** | Opt-in auto-update; previous image restored if systemd restart fails. Readiness must participate in startup; no DB rollback. [D5], [D6] | Separate rootless users and preferably separate VMs; no runtime socket in server or tools | Install Podman, Quadlets, user lingering, trust policy, and timers. Familiar OCI artifacts, but a second runtime beside existing Docker |
| **C. Native binaries + systemd updater** | Versioned directories and atomic active-link swap, or `systemd-sysupdate` transfers; updater must supply readiness checks, rollback, and drain. [D9], [D10] | Same split VMs; systemd sandbox or rootless Podman tools | Install signed bundles, units, trusted key, and updater. Portable across Linux distributions, but Octopushy owns another privileged lifecycle component |
| **D. Immutable appliance VM** | Nix-built or mkosi image; A/B OS slots plus boot assessment. Separate application/schema readiness and mutable-data recovery. [D9], [D11], [D12] | Separate server and agent appliance images; writable DB, drafts, identity, and outbox outside OS slots | Import VM image, provision identity/key, configure partitions and recovery. Consistent fleet, highest release/boot/storage maintenance cost |
| **E. Split layout, applied to A–D** | Does not supply an updater; inherits packaging's rules | Separate VMs are the meaningful boundary. Native agent beside a container server on one host is only a weaker compromise | One control VM plus agent VM(s) where reachable targets require them. More guests, much smaller execution blast radius |

### Coverage of the eight Compose issues

“Guard” means the proposed shared update coordinator, **not an existing
automatic guarantee**. E changes placement, not the other mechanisms.

| Original issue | A: NixOS | B: Quadlet | C: native updater | D: appliance | E: split layout |
|---|---|---|---|---|---|
| **1. Archived upstream Watchtower** | Not used | Not used | Not used | Not used | Only fixed if chosen updater replaces it |
| **2. Watches every container** | Explicit configuration target; OS activation can still affect other units | Only opted-in units; scope update invocation too | Explicit service allowlist | Explicit image/component target | Separate guests reduce collateral scope |
| **3. Agent replaced mid-apply** | Guard before activation/reboot | Exclude agents from blind auto-update; guard restart | Guard active-link swap/restart | Drain before reboot/slot activation | Still needs guard |
| **4. No migration-aware rollback** | Generation + shared DB protocol | Image rollback + shared DB protocol | Old binary + shared DB protocol | Old OS slot + shared DB protocol | Does not solve data recovery |
| **5. Socket exposes server and KEK** | No runtime on control VM; tools on agent VM | Rootless, separate users/VMs, no shared API socket | Split VMs; narrow privileged updater | Separate appliance VMs | Separate VMs address this directly; host agent alone does not |
| **6. Other sites unreachable** | Enroll agents per reachable network | Same | Same | Same, using agent image | Its purpose; no inbound agent listener |
| **7. No signature verification** | Pinned source + authenticated release + configured cache trust | Enforced containers/image signature policy | Verified signed manifest/bundle | Verified signed image manifest; optional verified boot | Inherits selected mechanism |
| **8. Silent updates/approval conflict** | Audited automatic policy and guarded deployment | Guarded trigger, not an invisible timer | Recorded updater operation | Recorded image rollout | Inherits policy; no implicit approval |

Mechanism claims: [D1], [D2], [D4]–[D12]. Coverage and ranking are engineering
assessments, not tested guarantees.

## Shared update contract

### Server, migrations, and PostgreSQL

Use an OS-owned oneshot coordinator that survives the server process stopping.
The server runs its own trusted application/schema maintenance only, **never
executor or recipe code**. A migration bundle or reviewed SQL artifact can be
built in CI; the production VM need not contain the .NET SDK. EF explicitly
warns against application-startup migrations as a production deployment
strategy. [D13]

Proposed sequence:

1. Verify the exact release, stage its closure/image/binary, and retain the
   previous one. Serialize updates and record a durable update journal outside
   the changing executable.
2. Enter maintenance: pause dispatch and all application DB writers, including
   results ingestion and browser/Git write-back. Agents continue running and
   spool results. Preserve the server's Git clone and unpublished draft branches,
   not just PostgreSQL.
3. Before any migration, make a checked, off-VM backup: database plus required
   roles/configuration, with a recorded restore procedure. `pg_dump` provides a
   consistent database export; globals such as roles need separate treatment.
   A successful dump alone is not a tested restore. [D14]
4. Run one migrator with schema privileges; keep the normal server DB role
   narrower. Require an explicit compatible source-schema range. Most EF
   migrations are transactional, **not all**: EF supports SQL outside a
   transaction, so the earlier blanket “PostgreSQL rolls every migration back”
   claim must not be relied on. Earlier successful migrations can also remain
   applied when a later migration fails. [D13], [D15]
5. Start the candidate in maintenance, verify DB access, schema, Git access and
   agent-channel readiness, then reopen writes/dispatch. If startup fails, select
   the previous executable/configuration **only if it supports the resulting
   schema and data semantics**. Otherwise stop and recover deliberately; do not
   automatically downgrade the database.

Require expand/contract migrations and previous-release compatibility for
automatic updates. Destructive changes are a separately authorized maintenance
operation. Once writes resume, restoring the pre-update DB loses subsequent
approvals/results/edits and can contradict work that actually ran: stop dispatch,
preserve agent outboxes and evidence, and reconcile before resuming. Backups
are disaster recovery, not routine executable rollback.

Pin a PostgreSQL **major** explicitly in every layout. Minor releases can use
the guarded restart path. Major upgrades need a separate plan with extension
checks, tested backup/restore, and `pg_upgrade` or dump/restore; retain an
independent old cluster for recovery. Avoid `pg_upgrade --link` when preserving
an independently restartable old cluster matters. Neither a Nix generation nor
an older OCI tag reverses an upgraded data directory. [D16], [D17]

### Agents, leases, and several sites

Propose one signed release manifest naming server/UI/agent versions, immutable
artifacts, and protocol compatibility. Update the server first, then converge
agents to its desired release. Accept agents one protocol version behind as
required; this is **not necessarily one application release behind**.

Each agent must atomically enter local drain mode before acknowledging update
readiness: refuse new assignments, finish active attempts and prepared/leased
work, durably record results, and flush acknowledgements when connected. Keep
identity, outbox, and execution state outside the installed binary. Stage updates
while busy, but postpone activation indefinitely rather than let a systemd stop
timeout kill an OpenTofu apply. Guard **all** activation/reboot paths, including
OS updates; `ExecStartPre` is too late to protect a process already stopped.

Use a local updater/drain handshake, not a fleet-wide “all idle” barrier.
Independent agents update when ready; canary one before wider promotion.
Disconnected sites keep executing existing assignments and queue results.
An idle offline agent may stage a verified cached target, but must not guess a
new desired version. Show stale agents and retries instead of silently skipping
them. Before a further protocol-breaking server update, catch up or explicitly
retire agents that would fall outside the compatibility window.

Server downtime must not revoke running attempts or trigger replacement
mutations: persist ownership, use a bounded update outage budget and adequate
lease/reconnect grace, then reconcile the outbox. If the budget is exceeded,
retain the existing uncertain/recovery behavior; no finite grace protects an
indefinite outage. A newer agent facing a rolled-back server waits safely;
an idle-only downgrade to a retained signed version may be needed.

These are **new Octopushy requirements**, not capabilities supplied by any
packager. Test restart, network partition, long apply, duplicate-result delivery,
and server rollback with newer agents before enabling automatic activation.

### Isolation, secrets, and release trust

Propose a dedicated non-root server identity, PostgreSQL identity, and root-owned
KEK file delivered only to the server through `LoadCredential=`. Keep the KEK,
Git credentials, and agent CA out of Nix expressions/store outputs, OCI layers,
appliance images, and database backups. Escrow the KEK separately; ensure whole-VM
backups do not reunite the DB and readable KEK in one recovery bundle.
Systemd credentials restrict delivery but do not protect against host root.
[D19]

On agent VMs, use the native static Rust agent, built-in executors, and the
dedicated Rust Proxmox client. Give each tool attempt a rootless Podman container
with only its private workspace and attempt-specific secrets, no host/runtime
socket, no agent home/identity/outbox mount, no privileged mode, and constrained
capabilities/resources. Keep OpenTofu state in its existing native HTTP backend.
Rootless execution reduces host privilege, but shares a kernel; compromised
agent/host administrators can still access that agent's concurrent secrets.
Provider credentials also retain their target-side authority. [D7], [L1]

An alternative is a system-manager transient service per attempt, with a fresh
`DynamicUser=`, `ProtectSystem=strict`, explicit writable/bind paths,
`ProtectHome=`, `PrivateTmp=`, `PrivateDevices=`, `NoNewPrivileges=`, and capability
restrictions. `DynamicUser` alone is not a complete sandbox; filesystem
read-only does not mean unreadable. `PrivateNetwork=yes` blocks needed network
access, and rootless/user-manager namespace restrictions depend on host support.
Do not give the agent unrestricted privileged `systemd-run`: use a narrow
launcher if this path is chosen. Bubblewrap likewise needs a deliberately built
mount/network policy, not merely wrapping the command. [D19], [D20], [D21]

Verify release origin **before privileged activation**. A hash pins content,
not its publisher. For Nix, pin the input and authenticate the selected release;
configure the release cache's trusted public key without disabling signature
requirements or marking an arbitrary cache trusted. Nix documents exceptions
for content-addressed objects, so cache signatures are not universal source
authentication. Native/appliance bundles need a pinned verification key and
signed version/digest manifest. Also reject unauthorized downgrade/replay, with
an explicit recovery override. [D4], [D10]

## Alternative-specific details

### A. NixOS modules and generations

Publish proposed `services.octopushy` and `services.octopushy-agent` modules:
server/UI self-contained .NET artifact plus Git CLI and required native runtime
dependencies; PostgreSQL on persistent storage; static agent and pinned tool
environment. Self-contained .NET includes the runtime, **not every OS library**,
so Nix packaging still needs its native dependencies. [D22]

Renovate proposes the release input/lock bump; an expressly authorized policy
may automatically merge authenticated compatible releases. A guarded deployment
unit then builds and switches the pinned configuration. `system.autoUpgrade`
provides a timer and rebuild mechanism, **not Renovate merge, flake-input
selection, database backup, drain, or readiness verification**. A local flake
does not advance its lock simply because a timer fires; fetch the selected
configuration or deliberately update inputs. The inspected NixOS module exposes
`flake`, `flags`, `operation`, and reboot settings. Prefer one guarded entry point
over an additional unguarded upgrade timer. [D1], [D3]

Keep reboot disabled for routine application upgrades. Retain the previous
generation and closure against GC, verify the new service, and explicitly
reactivate the previous generation on failure using the shared DB contract.
`nixos-rebuild switch --rollback` is available, but a whole-generation rollback
also reverts unrelated service configuration. Keep unrelated fleet changes out
of the Octopushy application rollout. [D1], [D2]

Bootstrap: use the existing NixOS VM provisioning path, import the module, pin
the release, provision KEK/CA/Git credentials separately, and start the server.
For a remote agent, provision a small NixOS VM at that site, configure server
address and CA trust, and enroll outbound with a one-time expiring token. Generate
its private key locally, persist its renewable mTLS identity, and remove the
bootstrap token. The enrollment protocol remains to be implemented.

### B. Podman Quadlet and auto-update

Rootless Quadlets belong to the service user's Quadlet search path, not a system
Quadlet with `User=` added. Configure their install targets and user lingering
for unattended boot; enable the update timer only in that intended user context.
Use a separate server service account, never the existing broad `containers`
account for both tool execution and server secrets. [D6], [D23]

Verified behavior: rollback defaults to enabled and retries the previous image
when the systemd restart fails. A process that fails **after startup was deemed
successful** can escape that detection. Quadlet `Notify=true` delegates readiness
to the application (`READY=1`); `Notify=healthy` bridges a configured healthcheck
to startup readiness. A healthcheck alone does not do that. Bound startup with
`TimeoutStartSec`; later unhealthy state is not continuous auto-update rollback.
Health failure defaults to no action; separate stop/kill plus systemd restart
can aid recovery but is not image rollback. [D5]–[D7]

For migrations, invoke updates through the shared coordinator rather than let a
timer race them. Keep PostgreSQL major pinned and agent units out of blind
auto-update. Exact digest promotion may be simpler than mutable auto-update
tags; it gives up some of the built-in poller's convenience.

Require `signedBy` or `sigstoreSigned` in the effective service-user trust
policy; TLS verification is not publisher verification. Check the rootless policy
path and Sigstore registry-attachment configuration. Repository signature identity
alone does not bind an intended tag/version, so verify the release's digest
mapping too. [D8]

Bootstrap adds Podman, Quadlets, volumes, trust policy, secrets and user timers
to a fresh VM; remote agents use the same enrollment as A. Rollback still follows
the shared DB rules. This is worthwhile if OCI distribution matters more than
Nix-native deployment, not as a migration of all existing Compose services.

### C. Native binaries with a separate updater

Install immutable version directories and atomically switch an active symlink.
Keep a co-located **OS-managed updater**, not a server overwriting itself: it
verifies/stages artifacts, runs the shared backup/migration protocol, stops and
starts the service, probes readiness, and restores the prior link when safe.
Give it only the required service/update privileges; this is still trusted
host code, never recipe-accessible.

`systemd-sysupdate` already supports versioned file/directory/partition transfers
and A/B-style installation. Remote manifests use `SHA256SUMS` and detached GPG
signatures; verification defaults on. `CurrentSymlink=` can publish a selected
version. **Inference:** this removes transfer/selection plumbing, not the
application's backup, drain, health-based binary reversal, or schema recovery.
It is not a complete agent rollout controller. [D9], [D10]

Use rootless Podman or the transient-unit sandbox above for tools. Bootstrap
requires a verified bundle, units, updater trust key, external secrets, DB and
agent enrollment. All sites follow the shared version protocol. This is a useful
future non-Nix Linux distribution format, but creating a custom updater now
duplicates capabilities the lab already has.

### D. Immutable appliance VM with A/B images

Build server and agent VM images separately using Nix or mkosi; keep DB, Git
drafts, identities, KEK provisioning and outboxes on persistent storage, not the
replaceable OS image. Install the candidate into an inactive slot, verify its
signature, and reboot after server maintenance or agent drain. PostgreSQL major
must remain pinned even when the OS image advances. [D9], [D12]

Boot counting and `systemd-bless-boot` can fall back after unsuccessful boots
**if the image/bootloader layout is configured for automatic boot assessment**.
Wire Octopushy readiness into the boot-success decision; reaching the kernel or
default boot target is insufficient. A machine stuck before that decision also
needs a watchdog/reboot path. NixOS generation menus are not automatically this
A/B mechanism. A blessed image that later fails needs separate recovery;
mutable database state still follows the shared contract. [D11]

Bootstrap imports an image and supplies unique identity, network settings and
secrets; never clone an enrolled agent key. Remote sites need verified image
access or cached artifacts. Day-2 adds partition sizing, bootloader integration,
image signing, persistent-format compatibility and recovery-image testing.
Consider only if supporting many non-Nix users justifies owning an appliance.

### E. Split server and execution layouts

Prefer server + PostgreSQL together on a dedicated control VM, and one or more
dedicated agent VMs according to reachability and trust. Not every managed
machine needs an agent. Bootstrap each half with its selected packaging; all
update, migration and signing rules remain unchanged.

A host-native agent plus container server avoids Docker-in-Docker, but still
shares the kernel, and an agent with host root or the server's runtime socket
can reach the server/KEK. Separate Unix users and rootless runtimes help but are
not equivalent to separate VMs. Never place the executor directly on a Proxmox
hypervisor merely to save a guest. This placement judgment is the main reason
to reject the original single-project boundary, not a criticism of Compose as
a service definition format.

## Proposed first implementation and self-management later

1. Package only the two NixOS modules and signed release metadata; native control
   VM, separate agent VMs, rootless attempt containers. Keep existing Traefik,
   OpenTofu state ownership, and other Docker services unchanged. Keep one
   configuration owner: updates select a Git-pinned Nix configuration, rather
   than having Nix and a binary self-updater compete over installed versions.
2. Build one guarded update service and durable journal per VM, with the shared
   server/agent contract. Use policy-authorized automatic compatible releases
   from the beginning; record target/digests, backup, migration, readiness,
   drain, rollback and outcome. Git infrastructure changes remain
   propose-then-approve unless an explicit policy authorizes that exact class.
3. Later expose this same update mechanism as ordinary Octopushy operations.
   A separate maintenance agent can activate the control VM's update; agent
   self-updates use their OS-owned updater. Do not make completion depend on
   the restarting server: replay the local journal after reconnection. Keep
   an out-of-band recovery command and SSH/console access.

This proposes reuse of NixOS, **not** a new general-purpose deployment engine.
Defer custom A/B binary management and appliance builds until portability is a
real requirement.

## Open questions and unverified points

- Accept the extra control/agent VM boundary, and which networks actually need
  agents? Site prefixes alone do not answer reachability.
- Which signed compatible release channel, canary delay and maintenance window
  may update automatically? May a long-running attempt defer updates without
  limit, and how should a persistently stale agent block protocol advancement?
- What outage budget, backup retention, restore target, and independent KEK
  escrow are acceptable? Is a dedicated PostgreSQL VM needed later?
- **Unverified:** exact deployed NixOS/systemd/Podman versions, rootless networking
  and sandbox compatibility with bpg/proxmox and the future Ansible/Nix tools;
  `Notify=healthy` availability must match the pinned Podman version.
- **Unverified:** automatic rollback wiring, migration compatibility, power-loss
  recovery, lease/outbox behavior, Traefik gRPC/mTLS routing, agent enrollment and
  release-signature enforcement in an actual Octopushy deployment. Upstream
  documentation verifies component mechanisms, not this proposed integration.
  Some sources are rolling documentation; match them to packaged versions.

## Follow-up: portability requirement (parent review, October 15, 2026)

The user rejected NixOS as a dependency. That meets this report's own condition for
reconsidering ("until portability is a real requirement") and changes the ranking:

- **C becomes the base, with OCI images for the server.** The case against C was
  that it duplicates what the lab already has, which holds only on NixOS hosts. The
  shared update contract must be built under every option, because no packaging
  supplies backup, migrations, readiness, or drain; what C adds is the binary swap
  and its revert.
- **The updater is the agent binary in a supervisor role**, not a third program, so
  there is one native binary to build, sign, and update.
- **The server stays a container.** Self-contained .NET still needs native
  libraries, and the server needs the Git CLI; an image carries both on any
  distribution. [D22]
- **A and B become optional adapters** over the same artifacts. The supervisor
  stays the single owner of installed versions.
- **systemd-sysupdate is not relied on.** Its availability across distributions was
  not verified, and it would not remove the drain, readiness, or schema work. [D9]
- **New risks Octopushy owns:** the supervisor's runtime access (equivalent to root
  with rootful Docker), the self-update revert path, custody of the release key,
  and host OS updates that Octopushy cannot hold back.

## Sources

- **[L1] Local, read-only design and infrastructure references:** `CONTEXT.md`,
  `docs/architecture-agreement.md` sections 1, 5 and 6,
  `docs/research/stack-review.md`, `docs/research/integrated-editor.md`; under
  `/home/ops/projects/infra/nodes`: `homelab/templates/services-node/modules/`
  (`containers.nix`, `runtime-secrets.nix`, `default.nix`),
  `homelab/templates/jenkins-agent/modules/default.nix`,
  `homelab/templates/docker-swarm-node/modules/docker.nix`,
  `homelab/services/traefik/compose.yml`,
  `homelab/infrastructure/modules/proxmox-nixos-vm/`,
  `homelab/nodes/*/infra/main.tf`, `homelab/scripts/tofu`, `renovate.json`,
  `homelab/flake.nix`.
- **[D1] NixOS — automatic upgrade module, NixOS 26.05:**
  https://github.com/NixOS/nixpkgs/blob/nixos-26.05/nixos/modules/tasks/auto-upgrade.nix
- **[D2] NixOS manual — upgrading and rolling back:**
  https://nixos.org/manual/nixos/stable/#sec-upgrading
- **[D3] Renovate — Nix manager and lock-file maintenance:**
  https://docs.renovatebot.com/modules/manager/nix/
- **[D4] Nix — `require-sigs`, `trusted-public-keys` and trust exceptions:**
  https://nix.dev/manual/nix/2.28/command-ref/conf-file.html
- **[D5] Podman — auto-update, selection and rollback:**
  https://docs.podman.io/en/latest/markdown/podman-auto-update.1.html
- **[D6] Podman — Quadlet, rootless paths, `Notify=` and version caveats:**
  https://docs.podman.io/en/stable/markdown/podman-systemd.unit.5.html
- **[D7] Podman — run, rootless restrictions and health actions:**
  https://docs.podman.io/en/stable/markdown/podman-run.1.html
- **[D8] containers/image — signature trust policy, rootless lookup and Sigstore:**
  https://github.com/containers/image/blob/main/docs/containers-policy.json.5.md
- **[D9] systemd — sysupdate, versioned/A/B transfers:**
  https://github.com/systemd/systemd/blob/main/man/systemd-sysupdate.xml
- **[D10] systemd — transfer configuration, `Verify=` and `CurrentSymlink=`:**
  https://github.com/systemd/systemd/blob/main/man/sysupdate.d.xml
- **[D11] systemd — automatic boot assessment:**
  https://systemd.io/AUTOMATIC_BOOT_ASSESSMENT/
- **[D12] systemd — mkosi image builder:**
  https://github.com/systemd/mkosi
- **[D13] Microsoft — applying EF Core migrations, scripts and bundles:**
  https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
- **[D14] PostgreSQL — pg_dump consistency, scope and restore formats:**
  https://www.postgresql.org/docs/current/app-pgdump.html
- **[D15] Microsoft — migration transactions and `suppressTransaction`:**
  https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing
- **[D16] PostgreSQL — minor and major version upgrades:**
  https://www.postgresql.org/docs/current/upgrading.html
- **[D17] PostgreSQL — pg_upgrade, link mode and reverting:**
  https://www.postgresql.org/docs/current/pgupgrade.html
- **[D18] Traefik — file provider:**
  https://github.com/traefik/traefik/blob/master/docs/content/reference/install-configuration/providers/others/file.md
- **[D19] systemd — service sandboxing, dynamic identities and credentials:**
  https://github.com/systemd/systemd/blob/main/man/systemd.exec.xml
- **[D20] systemd — transient services and execution properties:**
  https://github.com/systemd/systemd/blob/main/man/systemd-run.xml
- **[D21] bubblewrap — security and sandbox construction:**
  https://github.com/containers/bubblewrap
- **[D22] Microsoft — .NET Linux dependencies, including self-contained apps:**
  https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian#dependencies
- **[D23] systemd — loginctl user lingering:**
  https://github.com/systemd/systemd/blob/main/man/loginctl.xml
- **[D24] serokell — deploy-rs (magic and auto rollback):**
  https://github.com/serokell/deploy-rs
