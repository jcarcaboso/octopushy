# Coverage check against the `nodes` repository

Reviewed October 14, 2026 against the clean local checkout
`/home/ops/projects/infra/nodes`, branch `main`, commit
`f3d28ccfcd260bb2a080ed1444c7a1fb72ffa505`.

**Purpose: discover cases to account for, not prescribe a one-to-one mapping.**
Jenkins jobs are evidence of operational needs, not a proposed Octopushy API.
State layouts, lock names, Git branches, credentials, and mechanisms below are
properties of the inspected repository—not decisions for the new product.

This was a read-only source inspection. No Jenkins jobs, repository scripts,
infrastructure commands, network probes, or tests were executed. References to
implemented behavior mean executable code exists, not that live deployment or
correctness was verified. Historical rollout reports are not current live evidence.
Source references below are relative to the inspected `nodes` repository.

## Snapshot and important status distinctions

The checkout declares four Proxmox host catalogs, eight active fleet guests,
sixteen service instances on two services-node guests, and one active Technitium
cluster declaration. It also contains an isolated Fedora AI PoC outside the active
fleet and archived node/Swarm declarations. Counts describe Git inventory, not
currently running machines.

- The main fleet path is **NixOS plus deploy-rs**, not generic Ansible guest setup.
- Fedora setup and maintenance have executable PoC paths; they are not the
  general fleet implementation.
- The Proxmox **environment/member** split is explicitly deferred. Current node
  schema still uses `proxmoxHost`; vocabulary in `CONTEXT.md` is ahead of that code.
- Swarm's framework is retained, but its PoC is archived and its jobs are on the
  controller's retired-job list. It is not an active cluster requirement.
- VM retirement has an executable path, but independently managed workload
  removal stages in that pipeline are placeholders.

Evidence: `homelab/nodes/*/node.json`, `homelab/services/*/service.json`,
`homelab/infrastructure/proxmox/hosts/*.tfvars`,
`homelab/clusters/technitium/home/cluster.json`,
`homelab/schema/node.schema.json`,
`docs/proxmox-environment-member-follow-up-plan.md`,
`services/jenkins/casc/jobs.groovy`,
`jenkins/retire-node.Jenkinsfile`.

## What the discussion already covers

The [architecture agreement](architecture-agreement.md) already addresses the
direction of independent management domains, native configuration, central
coordination with remote execution, granular operations, Git-derived intent,
verification, explicit recovery, and separation of tools from OS maintenance.
The gaps below refine these ideas; they do not replace them with Jenkins concepts.

## Cases to account for

### C1. Infrastructure state is a separate ownership boundary

**Repository evidence:** the fleet uses a root module per guest with HTTP backend
declarations. The wrapper targets GitLab state; local-to-remote migration is
explicit. A preflight compares live VM identity with the bindings in state and
refuses to treat an occupied VMID with missing bindings as a new VM.

**Implication:** a useful guest hierarchy does not decide state granularity.
Git-forge independence also does not automatically remove an existing GitLab
backend dependency. Choose backend continuity, state boundaries, and any handover
separately; do not reproduce per-VM state by assumption or provision from empty
state over existing machines.

Sources: `homelab/nodes/ai-auto-001/infra/main.tf`,
`homelab/docs/opentofu-state.md`, `homelab/scripts/tofu`,
`jenkins/scripts/verify-infrastructure-state`.

### C2. Creating a machine is more than running its final configuration

**Repository evidence:** a generic bootstrap image and a node-specific NixOS
system output have different lifecycles. The node pipeline builds/publishes
artifacts, provisions, waits for access, reconciles the root filesystem, activates
the final configuration, verifies it, and releases a temporary bootstrap import.
The VM module also handles separate data disks and optional hardware mappings.

**Implication:** retain a distinction between image preparation, provisioning,
guest readiness, final setup, and artifact cleanup. Disk resizing, data retention,
and hardware availability can be prerequisites or irreversible effects. We have
not chosen an image pipeline, shared cache, filesystem policy, or storage manager.

Sources: `jenkins/reconcile-node.Jenkinsfile`,
`jenkins/scripts/build-bootstrap-image`, `jenkins/scripts/reconcile-root-filesystem`,
`jenkins/scripts/release-bootstrap-import`,
`homelab/infrastructure/modules/proxmox-nixos-vm/main.tf`.

### C3. Native configuration ownership can limit independent mutation

**Repository evidence:** OpenTofu owns VM infrastructure; the main guest path
builds and activates a complete NixOS system through deploy-rs. Its configuration
enables activation/connection rollback. The Fedora PoC instead uses Ansible
configuration and runtime-release verification.

**Implication:** “setup,” “maintenance,” and “update tools” are useful operator
actions, but cannot imply competing authorities over the same installation.
Some updates may require activation of a complete native configuration. Native
activation rollback is not a universal undo of service data or external effects.
Choose the first supported guest path instead of assuming all recipes are SSH
scripts or that both OS paths must ship initially.

Sources: `homelab/flake.nix:174`, `jenkins/scripts/deploy-node`,
`homelab/ansible/playbooks/fedora-ai-poc.yml`,
`homelab/ansible/playbooks/update-tools.yml`.

### C4. A new Git commit is not necessarily a change to every target

**Repository evidence:** the planner compares evaluated infrastructure, bootstrap,
and NixOS identities with per-node accepted records. It selects infrastructure
planning and configuration activation independently. A separate scheduled drift
path forces infrastructure planning. Candidate qualification is revision-bound.

**Implication:** decide how Octopushy identifies affected targets and records the
last verified relevant configuration. Source revision, effective target inputs,
and drift observations are different. A Git-only change detector cannot cover
out-of-band changes. This does not commit us to Nix fingerprints, the existing
qualification format, or a Git branch for operational state.

Sources: `jenkins/plan-changes`, `jenkins/scripts/evaluate-fleet`,
`jenkins/drift-reconciler.Jenkinsfile`,
`jenkins/scripts/accepted-state`,
`jenkins/lib/vars/homelabIssueQualification.groovy`.

### C5. Recovery requires revalidation, not just saved step markers

**Repository evidence:** node runs checkpoint operation metadata and phase results,
support a bound resume, and revalidate machine plans, readiness, deployed
configuration, and health before trusting completed phases. The service path
separately commits a remote release and publishes its accepted record; it retains
evidence for interrupted publication.

**Implication:** preserve the distinction between “not executed,” “executed but
unverified,” and “verified but result publication failed.” An operator must not
repeat a successful mutation merely because the manager lost its result. We need
an explicit durability/reconciliation contract, not a copy of Jenkins artifacts
or a claim that external effects can be atomically committed with app metadata.

Sources: `jenkins/reconcile-node.Jenkinsfile:255`,
`jenkins/reconcile-node.Jenkinsfile:349`,
`jenkins/lib/vars/homelabCheckpointOperation.groovy`,
`jenkins/scripts/lib/reconcile-services-remote:317`,
`jenkins/reconcile-services.Jenkinsfile:114`.

### C6. A service has an instance-specific lifecycle and policy

**Repository evidence:** copies of the same application on different guests are
separate service instances. Each has its own release pointer and accepted record.
Service approval is independent of node approval and defaults to automatic in
this repository. Mutations of services on one node share its lifecycle lock.
Preparation failures can attempt a service-local release rollback.

**Implication:** target identity, release identity, approval scope, and conflict
scope are separate decisions. Preserve independent outcomes without assuming
same-host mutations are safe concurrently. Do not silently inherit the
repository's automatic-service default or promise that switching a release
reverses database migrations.

Sources: `homelab/schema/service.schema.json`,
`jenkins/reconcile-services.Jenkinsfile`,
`jenkins/scripts/service-accepted-state`,
`jenkins/scripts/lib/reconcile-services-remote`,
`docs/adr/0001-independent-service-instances.md`.

### C7. Service exposure creates dependencies beyond the workload

**Repository evidence:** service firewall declarations contribute to the owning
node's NixOS configuration. Service jobs wait for the required accepted node
identity. The service planner attempts independent service work, but cancels its
subsequent DNS apply if the service batch fails. DNS has a global lock, saved-plan
checks, change-count limits, provider gates, and resolver verification. Configuration
also distinguishes zones whose records are managed from zones left to another owner.

**Implication:** model actual prerequisites and ownership of firewall, service
readiness, and DNS publication. The current batch-wide DNS gate is an example of
coupling to evaluate, **not a rule to copy**. We have not decided whether unrelated
DNS changes should be isolated, how provider transitions work, or who owns ports,
proxy routes, certificates, and records.

**Update (October 19, 2026):** decided as generic declarations in each service.
DNS records go through the lab's Technitium integration, each owned by one service
with its own outcome; ports are opened in the guest's operating system; routes stay
with the reverse proxy on each apps machine for now
([lab repository](lab-repository.md#network-exposure-dns-ports-and-routes)).

Sources: `homelab/templates/services-node/modules/firewall.nix`,
`jenkins/service-planner.Jenkinsfile:36`,
`jenkins/scripts/reconcile-dns`, `homelab/config/dns/providers.json`.

### C8. Application clusters are not Proxmox clusters

**Repository evidence:** Technitium declares primary/secondary membership across
guests. Its executable reconciliation handles joining, promotion, removal, forward
zones, blocking policy, and query-log configuration, with approval and locks on
shared DNS plus member nodes. The archived Swarm framework is another, separate
cluster lifecycle.

**Implication:** some resources span several guests and cannot be children of only
one VM. Avoid equating every “cluster” with a hypervisor cluster or forcing all
relationships into a strict tree. This is a compatibility consideration, not a
decision to implement Technitium/Swarm lifecycle support in the first milestone.

**Update (October 19, 2026):** application clusters are now required, starting with
Technitium, as a lab-level resource that relates member services and forms the
cluster as a separate step ([lab repository](lab-repository.md#application-clusters)).

Sources: `homelab/clusters/technitium/home/cluster.json`,
`jenkins/reconcile-technitium.Jenkinsfile`,
`jenkins/scripts/reconcile-technitium-cluster`,
`homelab/docs/docker-swarm-deployment.md`.

### C9. Tool updates already use two different desired-version models

**Repository evidence:** the Nix AI updater resolves upstream releases, smoke-tests
them, updates version/lock files, and publishes a constrained Git patch for normal
deployment. The Fedora PoC resolves configured channels to a candidate, verifies
activation, and promotes an installed-release receipt. Both `configure` and
`update-tools` resolve channels there; `update-system` reuses the installed receipt.

**Implication:** decide whether Git declares exact versions or allowed channels,
which operations may select new versions, and how an exact execution resolution
is retained. In particular, decide whether ordinary setup should also upgrade
tools. Reusing one installation recipe avoids conflicting implementations, but
does not by itself settle version-selection policy.

Sources: `jenkins/ai-app-updater.Jenkinsfile`,
`homelab/scripts/update-ai-apps`, `homelab/scripts/update-ai-apps-publish`,
`homelab/pocs/fedora-ai-server/run`,
`homelab/pocs/fedora-ai-server/prepare-tool-update`.

### C10. Maintenance can be deferred or partially complete

**Repository evidence:** Fedora maintenance checks active AI work, distinguishes
idle/busy/unknown, can defer activation, and can require administrator approval
to interrupt work. OS packages may be updated while a necessary reboot remains
pending. The pipeline explicitly acknowledges that its activity checks are not
an atomic barrier preventing new work from starting.

**Implication:** “deferred,” “needs approval,” “reboot pending,” and “failed” are
different operational situations. Define maintenance/admission boundaries and
partial-completion evidence. Do not promise interruption-free upgrades from a
best-effort idle check or copy PoC-specific process detection into a generic rule.

Sources: `jenkins/maintain-fedora-poc.Jenkinsfile`,
`homelab/ansible/playbooks/update-system.yml`,
`homelab/ansible/tasks/t3-activity-poc.yml`,
`homelab/pocs/fedora-ai-server/run`.

### C11. Retirement is a concrete test of the proposed `down` lifecycle

**Repository evidence:** node retirement moves a complete declaration to archived
inventory instead of authorizing destruction through plain deletion. Planning
binds preserved configuration, state, and live VM identity; the pipeline approves
destruction, cleans state, and verifies absence. However, workload preparation/
removal is explicitly deferred in that pipeline; its placeholder stages are not
an implemented dependent-service teardown.

**Implication:** retain enough information to retire an object after it leaves
active desired state. Resolve dependent services, persistent data, credentials,
records, and history explicitly. Archive folders are only one implementation.
Do not infer a safe general-purpose `down` action from the existing VM destroy
path, or assume declaring retirement already handles all dependent workloads.

Sources: `jenkins/scripts/validate-inventory-transition`,
`jenkins/scripts/lib/removed-nodes`,
`jenkins/retire-node.Jenkinsfile:129`,
`jenkins/scripts/revalidate-retirement-operation`,
`jenkins/scripts/destroy-removed-node`.

### C12. Access and secret readiness are prerequisites, not just variables

**Repository evidence:** deployment SSH identity and host trust are handled
separately from Proxmox credentials. Service-secret metadata feeds a guest-local
Infisical Agent; remote service preparation checks that required files are ready
and have the intended ownership/permissions before invoking Compose.

**Implication:** agree separate management credentials, guest access, and workload
secret delivery. Missing secrets and changed host identity must be distinguishable
from failed application deployment. Account for initial enrollment, rotation, and
independent recovery. Neither Infisical nor Jenkins' credential store has been
selected for Octopushy.

Sources: `homelab/access/README.md`, `jenkins/scripts/deployment-identity`,
`jenkins/scripts/operation-known-host`,
`homelab/templates/services-node/modules/runtime-secrets.nix`,
`jenkins/scripts/lib/reconcile-services-remote:122`.

### C13. Setup has ownership boundaries inside the guest

**Repository evidence:** the Fedora PoC distinguishes system configuration,
root-owned tool releases, user-level services, developer workspace/bootstrap,
editor configuration, and independently enrolled agents. Its editor recipe
preserves an existing user-managed configuration rather than replacing it blindly.

**Implication:** being able to administer a VM does not mean every file or tool
installation is owned by Octopushy. Recipes need target identity and privilege
boundaries, and a policy for managed versus user-owned content. “Remove setup”
must not imply deleting workspaces or unrelated user data.

Sources: `homelab/ansible/playbooks/fedora-ai-poc.yml`,
`homelab/ansible/tasks/editor-poc.yml`,
`homelab/ansible/tasks/install-poc-tool.yml`.

### C14. Workers can themselves be managed infrastructure

**Repository evidence:** Jenkins separates build and mutation capabilities,
excludes the target guest from deployment-worker selection, and uses node,
Proxmox, and shared-resource claims. Guest deployment can enroll a Jenkins worker.
The Fedora PoC uses a specific existing controller's state rather than silently
creating a second copy on whichever worker receives a job.

**Implication:** agent eligibility depends on tools, authority, connectivity, and
durable inputs—not just lab membership. Account for self-update/reboot and recovery
when workers depend on infrastructure being changed. Do not confuse a managed
Jenkins-agent guest with the future Octopushy execution-agent model.

Sources: `jenkins/reconcile-node.Jenkinsfile`, `jenkins/scripts/deploy-node`,
`jenkins/lib/vars/homelabWithResourceClaims.groovy`,
`jenkins/scripts/maintain-fedora-poc`,
`homelab/pocs/fedora-ai-server/jenkins-maintain`.

### C15. Operational evidence has its own lifecycle

**Repository evidence:** failure notifications include target context and tolerate
delivery failure. There are controller backup/restore-validation helpers and
retention settings for jobs, artifacts, images, and Nix outputs. The backup guide
explicitly separates archive validation from a complete recovery drill.

**Implication:** decide what evidence must survive retries, retirement, agent loss,
and backup restoration; do not clean it up as if it were a disposable workspace.
Notifications and cleanup must not obscure the actual operation result. No Discord,
restic, cache, retention period, or telemetry platform is implied for Octopushy.

Sources: `jenkins/lib/vars/homelabNotifyFailure.groovy`,
`jenkins/scripts/backup-controller`, `docs/jenkins-backup-recovery.md`,
`homelab/README.md:52`.

## How to use this comparison

Before implementing the first journey, pick which of these cases it must handle.
Useful early design exercises are:

1. Create one guest, configure it, deploy a service, and verify each boundary.
2. Change that service's port and trace only its actual prerequisites/dependents.
3. Fail setup or verification, then recover without repeating unrelated work.
4. Update a tool, then rerun setup and OS maintenance without unintended version
   changes or interruption of active work.
5. Remove a managed component while dependents or user-owned data remain.
6. Lose the result after a remote mutation and determine what may safely run next.

These exercises test the agreed direction. They are not a promise to reproduce
all fifteen cases in v1, and they do not authorize running the existing automation.
