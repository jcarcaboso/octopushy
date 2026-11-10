# Octopushy

A proposed homelab operations control plane for provisioning, maintaining, and
deploying infrastructure from Git-versioned configuration. This is domain
vocabulary, not a list of implemented capabilities or a configuration schema;
see the [architecture agreement](docs/architecture-agreement.md) for decision status.

## Language

**Resource**:
A persistent homelab entity with an identity, declared intent, and observed
condition, such as a managed machine, service, or infrastructure stack.
_Avoid_: Job when referring to something that exists between executions

**Lab**:
A group of related infrastructure, usually associated with a place, which may
contain standalone Proxmox hosts, clusters, or both without sharing credentials.
_Avoid_: Cluster, credential scope, or execution boundary when referring only to the group

**Proxmox host (hypervisor)**:
An individual Proxmox server that provides compute and placement for guests,
either standalone or as a cluster member.
_Avoid_: Unqualified node or instance when the distinction from a guest or cluster matters

**Proxmox installation**:
A standalone Proxmox host or a cluster of hosts sharing a VM identity space and
administrative boundary.
_Avoid_: Lab when referring to this management boundary

**Proxmox cluster**:
A group of Proxmox hosts sharing a management domain and VMID namespace.
_Avoid_: Lab or application cluster when referring to hypervisor membership

**Proxmox connection**:
An API endpoint and credential reference used to access a Proxmox installation.
It is an access route, not the identity or observed health of a hypervisor.

**Managed machine**:
A machine whose declared configuration or workloads are managed through Octopushy.
_Avoid_: Agent, runner, or unqualified node

**Guest**:
A VM or Proxmox container hosted by a Proxmox installation; its placement is
distinct from its identity and management history.
_Avoid_: Deployment when referring to the persistent machine

**Virtual machine (VM)**:
A virtualized guest machine hosted by a hypervisor.

**Proxmox container**:
A guest container managed by Proxmox, distinct from a Docker workload container.

**VM template**:
A reusable, versioned base from which VMs are created, distinct from the
configuration applied to a running VM.
_Avoid_: Machine profile when referring to the base image

**Machine profile**:
A declared role and its required configuration or software, such as a base
machine, service host, or AI node; a VM can have more than one profile.

**AI node**:
A managed machine assigned an AI-workstation or automation role, hosting harnesses
and tools with their own maintenance requirements.
_Avoid_: Service when referring to the whole machine

**Managed tool**:
A tracked software installation on a managed machine, such as an AI harness or CLI,
with a desired version policy; it need not be a running service.

**Setup**:
An operation establishing or reconciling a machine's declared configuration and
prerequisites, rather than an action that is necessarily performed only once.

**Configure run**:
A run of one action of the setup stage of a guest's workflow, `setup` or one the
workflow declares, which reaches the guest over SSH and holds no Proxmox credentials.
_Avoid_: Provisioning when referring to what happens inside an existing machine

**Preview**:
What a configure run shows before it runs: its tasks, inputs, secret names and host
key. It says what will run, not what will change.
_Avoid_: Plan, or dry run, when referring to a configure run

**Prepared snapshot**:
Values an action's prepare playbook resolved for a run, such as release versions. An
approval covers them, and they are kept for later actions only after a run succeeds.

**Host key pin**:
A guest's public SSH host key, which a configure run connects to and to nothing else: the
key Octopushy generated and delivered to a guest it created, or, for any other machine, one
read through a trusted channel and set in the variable store by an operator.
_Avoid_: Host key scan, which authenticates nothing

**Declared outcome**:
A result a playbook reports by name, as its workflow declares, that is neither success
nor failure, such as work being deferred.
_Avoid_: Failure when the playbook deliberately stopped

**Machine maintenance**:
An operation maintaining the guest operating system and its readiness, including
any explicitly authorized restart.

**Tool update**:
An operation changing selected managed software according to its version policy,
distinct from general operating-system maintenance.

**Access profile**:
The approved identity and privileges used to perform a class of operations
against a defined group of homelab resources.

**Variable store**:
Octopushy's key/value dictionary of values kept outside Git, each either plain
or secret, referenced from definitions by key.
_Avoid_: Credential store when plain values are included

**Store folder**:
A `/`-separated prefix that variable-store keys share, shown as a folder in the
interface. It is not stored and has no identity of its own, so it exists exactly
while a key below it does; a key is always its full path.
_Avoid_: Using a folder as a reference target; references name keys

**Secret**:
A variable-store entry whose value is concealed once saved: it is delivered to
operations that reference it, and shown only to an operator who reveals it.
_Avoid_: Variable when the value must stay concealed

**Secret reference**:
A key in a definition naming a variable-store entry; it holds neither the value
nor the provider that resolves it.

**Secret provider**:
A source that resolves the keys in a namespace, such as the built-in store, an
environment file, or an external secret manager.

**Secret readiness**:
Evidence that a secret a service requires is present on its target in the
expected form and version, distinct from the success of the service deployment.
_Avoid_: Failed deployment when only a required secret is missing

**Git write-back**:
Recording a change made in Octopushy's interface as a Git commit, which then
follows the normal change-detection flow.
_Avoid_: Editing the snapshot directly when Git has not changed

**Change**:
A structured edit of one guest or service made in a form, such as its memory, its inputs, an upgrade to a newer major of
its workflow, or the removal of a guest's folder. Octopushy writes it as one commit to the lab repository, compare-and-swap
against the commit the form was loaded from, syncs it at once, and follows it through the revision and the runs it
proposes. It touches only the resource's own folder.
_Avoid_: Editing the definition in the database, which never happens; draft, which is the repository editor's unpublished edits

**Draft**:
Unpublished edits to repository files on top of a base revision. When explicitly
published, the draft is validated, merged with any newer commits, and committed.
_Avoid_: Commit when the edits have not been published

**Service**:
What runs on one guest, read from `services/<id>/service.yaml` in its folder, which
names a workflow with `manages: service`. Octopushy knows it by its workflow's stages,
not by what it is.
_Avoid_: Job when referring to the workload rather than its deployment

**Pipeline**:
A service's ordered stages: all planned and previewed, approved once, then applied in
order, as one run. There is no rollback, and a later stage cannot use an earlier one's
results.
_Avoid_: Rollout, which spans independent targets

**Stage**:
One part of a workflow: one executor, OpenTofu or Ansible, run from one root. A VM
workflow has the machine stage and at most one setup stage; a service workflow has
one or more.
_Avoid_: Step, which is one thing a run does; a stage has a plan or preview step and an apply step

**Action**:
One playbook that can be run from a workflow's Ansible stage, as a whole. A guest's
setup stage runs `setup` when anything it is built from changes; the workflow's
`actions` are run by hand or on the guest's schedules.
_Avoid_: Task, which is a part of a playbook

**Interface**:
A named slot, such as `dns`, bound to a workflow in the lab, with a schema in the
repository. Swapping the workflow is one line. Not built yet.

**Deployment instance**:
A service's installation on a particular target, with its own desired release,
last applied release, and observed health.

**Release**:
An immutable combination of service artifacts and deployment configuration that
can be promoted to one or more targets.
_Avoid_: Build when referring to the deployable result rather than its creation

**Rollout**:
A set of independently progressing target-specific runs for a change, with
explicit dependencies and failure-containment rules. Grouped reporting does not
make it a single execution or recovery unit.
_Avoid_: One giant pipeline when independent targets have independent outcomes

**Failure domain**:
The resources and dependent operations affected by a particular failure, such as
one host, one guest, or one service deployment instance.
_Avoid_: Lab-wide failure merely because one member's operation failed

**Blocked work**:
An operation that cannot proceed because a required prerequisite is not satisfied.
_Avoid_: Failed when the operation has not run, or blocked when it only awaits capacity

**Concurrency limit**:
A configured maximum of simultaneous operations within a specified scope.
_Avoid_: Failure isolation when referring only to limiting simultaneous work

**Definition revision**:
An immutable snapshot of the homelab's declared configuration: a commit of the lab
repository together with what each of its workflow references resolved to. A new
release that changes a resolution is a new revision of the same commit.
_Avoid_: Commit alone, when workflows come from a workflows repository

**Observed-only installation**:
A Proxmox installation set to `manage: false`: Octopushy observes its hosts and VMs
and changes nothing there, so it holds no guests and gets no runs. The server keeps
that state while its definition is invalid or missing, and it takes trust again to
become managed. See the
[safety boundary](docs/safety-boundary.md).
_Avoid_: Unmanaged installation when meaning one Octopushy simply has no guests in

**Foreign machine**:
A Proxmox VM or container that is not the run's own: for a provision or retire run,
anything but the guest's VMID on its host; for a pipeline stage, any machine. A plan
that changes, deletes or replaces one, through any Proxmox resource, or that imports
anything, is refused, whatever the policy says.
_Avoid_: Unmanaged VM when meaning only that no guest declares it

**Installation trust**:
An operator's decision to let a Proxmox installation's credentials be sent to the
API endpoints it declares, given once when the installation is added or an
endpoint changes.
_Avoid_: Apply approval, a guarantee that code is safe, or trust of a single revision

**Repository source**:
The configured origin and branch from which definition revisions are discovered.
Work built from a previous source is not sent; it must be planned again.
_Avoid_: Commit hash alone when identifying where configuration came from

**Lab repository**:
The Git repository holding Octopushy's definitions and the tool-native files for
one or more labs, nested by lab, Proxmox installation, guest, and service instance.
_Avoid_: Octopushy's source repository when referring to the desired configuration

**Application cluster**:
A set of guests with stages applied to them, member by member, under one approval.
Not built yet.
_Avoid_: Proxmox cluster when referring to application membership

**Maintenance window**:
A recurring period in which automatic changes to a resource may start.

**Target**:
The explicitly selected infrastructure or service affected by an operation.
_Avoid_: Agent when referring to the destination of a change

**Stack**:
A group of infrastructure resources provisioned and tracked together.
_Avoid_: Pipeline when referring to infrastructure

**OpenTofu state**:
OpenTofu's resource bindings and recorded attributes for a stack, independent of
its configuration source or execution history.
_Avoid_: Status when referring to persistent infrastructure state

**Built-in state**:
OpenTofu state Octopushy keeps for a guest in its own database, encrypted and versioned.
It is the only state backend.
_Avoid_: Local state, which OpenTofu means as a file that outlives a run

**State-derived inventory**:
A view of resources recorded in the last observed OpenTofu state, not a live
measurement of their health.

**Desired revision**:
The configuration revision currently selected for a stack.

**Checked revision**:
The configuration revision most recently evaluated by a successful plan,
whether or not that plan proposed changes.

**Applied revision**:
The configuration revision most recently recorded after a successful apply.
A no-change plan alone is not an apply.

**Agent**:
A registered execution worker permitted to act on specified targets through the
appropriate network access; it is not required on every managed machine.
_Avoid_: Node, runner when naming the Octopushy worker

**Bootstrap image**:
The immutable base image used to create a guest's boot disk.

**Enrollment revocation**:
Removal of an agent's authority to connect and execute work. It does not remove
the machine hosting that agent or erase its historical identity.
_Avoid_: Guest retirement when only execution authority has been removed

**Guest retirement**:
The intentional end of a guest's managed lifecycle, completed after the guest's
absence has been verified. Revoking its agent enrollment alone is not retirement.

**Operation**:
A supported action on explicitly selected resources, such as provisioning,
setup, maintenance, tool updates, service deployment, or verification.

**Workflow**:
Everything needed to manage one kind of thing from start to end, such as a Fedora VM
or a web service, kept in `workflows/<id>/` with one version for all of it: what it
manages, its inputs schema, its stages, actions and outcomes. Guests and services
name a workflow; it holds no lab data.

**Workflows repository**:
The Git repository that holds `workflows/` and `modules/`, separate from the lab
repository, which names it in `octopushy.yaml` and then holds neither. Its versions
are release tags.

**Release**:
A tag `<id>/v<M>.<m>` of a workflows repository, at the commit the tag pointed to when
Octopushy first saw it. That commit never changes: a moved tag is reported and not
followed, and a deleted tag stays a release while its commit is in the mirror.
_Avoid_: Tag, when meaning the recorded commit
_Avoid_: Recipe, the earlier format of one stage; run, when referring to the reusable procedure

**Workflow version**:
`M.m`, declared in `workflow.yaml` as a quoted string. A new major is a breaking change,
such as an input removed or narrowed, or a change that would replace machines; a new
minor is not.

**Workflow reference**:
What a guest or service writes: `<id>@M` for the newest `M.x`, or `<id>@M.m` for exactly
that version. A reference without a version is invalid.

**Resolution**:
Finding the workflow version a reference names, from a workflow source: the releases of
a workflows repository, where `@M` takes the highest usable minor and `@M.m` exactly that
release, or the lab repository's own `workflows/`. It gives the workflow, its version and
the commit to read it from. What it resolved to is part of the loaded definition, of the
revision's identity and of every inputs hash; a reference that resolves to none makes its
guest or service invalid.

**Run**:
One requested execution of an operation or workflow against a fixed configuration
revision, input set, and target set.

**Step**:
A named, observable unit of a run with its own outcome and recovery rules.

**Attempt**:
One execution of a step; a retry creates a new attempt without erasing the
previous outcome.

**Observation**:
What an installation's Proxmox API reported at one time, gathered by an agent in
the background. It changes nothing, so it is not a run; only its failures surface.
_Avoid_: Observe run

**Approval**:
An operator's authorization of a specific proposed change, not a blanket
authorization of all future changes to the same target.

**Recovery**:
An explicit decision about how to proceed after failure or an uncertain outcome.
_Avoid_: Retry when the previous operation may still be running

**Verification**:
Evidence that specified requirements were checked against a target at a stated
time, distinct from the exit status of the preceding mutation.
_Avoid_: Complete compliance when only a limited set of checks was performed

**Reconciliation**:
Comparing desired configuration with observed evidence and coordinating permitted
actions toward that configuration.
_Avoid_: Automatic repair when only detection or proposal is authorized

**Observed health**:
A target's condition at a stated observation time, separate from whether its last
deployment run succeeded.
_Avoid_: Healthy solely because the most recent run succeeded

**Drift**:
A detected difference between declared intent and observed configuration.
_Avoid_: Unhealthy when the difference does not imply a service outage

**Maintenance job**:
A repeatable operation on selected resources, such as guest updates, subject to
the same permissions, concurrency rules, and execution history as manual work.
