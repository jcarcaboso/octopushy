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

**Secret**:
A variable-store entry whose value is concealed once saved and is delivered only
to operations that reference it.
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

**Draft**:
Unpublished edits to repository files on top of a base revision. When explicitly
published, the draft is validated, merged with any newer commits, and committed.
_Avoid_: Commit when the edits have not been published

**Service**:
A named workload deployed to one or more managed machines.
_Avoid_: Job when referring to the workload rather than its deployment

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
An immutable snapshot of the homelab's declared configuration at a Git revision.

**Lab repository**:
The Git repository holding Octopushy's definitions and the tool-native files for
one or more labs, nested by lab, Proxmox installation, guest, and service instance.
_Avoid_: Octopushy's source repository when referring to the desired configuration

**Integration**:
A system a lab uses to provide what services declare generically, such as a DNS
server, connected through an adapter for its product.
_Avoid_: Provider, which names where a secret's value comes from

**Application cluster**:
Service instances on several guests that a cluster recipe forms into one
application, such as Technitium DNS with a primary and secondaries.
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

**Operation**:
A supported action on explicitly selected resources, such as provisioning,
setup, maintenance, tool updates, service deployment, or verification.

**Operation recipe**:
A versioned declaration of how to perform an operation and interpret its result.
_Avoid_: Run when referring to the reusable procedure

**Workflow**:
A coordinated sequence of operations or steps with their prerequisites and
recovery boundaries.

**Run**:
One requested execution of an operation or workflow against a fixed configuration
revision, input set, and target set.

**Step**:
A named, observable unit of a workflow with its own outcome and recovery rules.

**Attempt**:
One execution of a step; a retry creates a new attempt without erasing the
previous outcome.

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
