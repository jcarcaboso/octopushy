# Safety boundary

Octopushy holds Proxmox tokens with wide privileges, often root. It must never plan or
apply against a machine it does not manage. Three guards hold that line, in addition to
[installation trust](installation-trust.md) and VM protection. No approval and no policy
lifts any of them, `destructive: automatic` included.

The guards stop **mistakes** by the one operator who writes the lab repository: a typo'd
VMID, a definition in the wrong installation, trust that no longer fits. They are not a
boundary against hostile repository code; see [threat model](#threat-model-and-accepted-limits).

**A Proxmox container is a machine to every guard here, as a VM is.** VMIDs are one
namespace across VMs and containers, and everything below reads them that way: an
observation lists both (`/cluster/resources?type=vm`), and the proof that it saw every
VM covers containers too, since `VM.Audit` on `/vms` is what Proxmox checks for either. So
a container is an observed unmanaged VMID, a VMID offered to the wizard is free of both, a
plan's changes to `proxmox_virtual_environment_container` are checked as a VM's are, and a
container is owned, unprotected and protected again under the same rules. A guest's
`type` also fixes the kind of machine its VMID must be: verify and protection are told
`qemu` or `lxc`, and a machine of the other kind at that VMID fails them, with nothing
changed.

## Stopping an in-progress change

Normal cancellation never interrupts a running change. Losing a lease, draining
an agent or reconnecting does not authorize an interruption or a retry.

An assignment the controller never began to write to the agent's stream was never
sent: it was still queued, or taken off the queue after the connection had closed.
The controller puts that attempt back in the queue and takes back the secrets it
recorded as released and the reservation it made, as when the hand-off itself fails.
That is not a retry. Once the write of an assignment began, a lost assignment is a
change that may have run, uncertain when its lease runs out, even if the agent says
it never got it: an agent restored from a snapshot cannot know what it received, so
the controller never asks.

There are two narrow exceptions to letting a mutating tool finish. Its configured
OpenTofu timeout bounds a hung attempt. Separately, only an explicit operator
**Force stop** may request interruption of a running change. The controller sends
`CancelAttempt.force` only from Force stop and only when the connected agent
advertises `forced-mutation-stop-v1`. An older agent receives ordinary cancel,
refuses to interrupt its mutation and keeps the tool running. An offline agent
cannot receive either request. The confirmation warns about both cases.

OpenTofu receives SIGINT first, allowing it to finish in-flight work and save
state. After the grace period the agent escalates to SIGTERM, then a hard kill.
Ansible receives SIGTERM, then a hard kill if needed. A forced mutation remains
uncertain even if the tool exits zero, and an OpenTofu apply reports its complete
saved state. A partial or missing state is not proof that nothing happened.
Stopping a local tool cannot stop a task already accepted by Proxmox.

A killed agent is the third way a change stops. The agent reports a change that was
running when its process ended as uncertain and never retries it. With `podman` or `docker`,
a tool container the killed process left may still be running that change. At startup,
before it reports, the agent removes every container labelled with its own agent ID, which
stops such a tool; the attempt is uncertain either way. Removal also deletes the container's config, which holds its
environment, secrets included. Containers of another agent on the same runtime are kept.

Neither exception waives the approved-plan hash check, machine ownership guards
or state locking. There is no rollback or automatic retry. The operator inspects
the target and saved state before closing an uncertain run or planning again. A
late result can still settle an uncertain attempt under the existing rules.

## Installations Octopushy only observes: `manage: false`

```yaml
# labs/home/proxmox/ziost/installation.yaml
schemaVersion: 1
manage: false                     # observed only; the default is true
type: standalone
connection:
  endpoint: https://10.1.0.20:8006/
  token: { store: proxmox/ziost/api-token }
hosts:
  ziost: {}
```

Use this for an installation whose VMs Octopushy should show but never touch, such as
one that runs Home Assistant. Octopushy observes its hosts and VMs, and does nothing else:

- **At sync,** a guest or service defined under it is invalid, with an issue that says
  why. The installation itself stays valid, so it is still observed.
- **When a run is made,** the run factory refuses it, and the engine fails any run for
  it that reaches it anyway, as it is added and before each step starts. Such a run
  fails with the reason; it never waits.
- **At dispatch,** the engine and the assignment builder refuse any work other than an
  observation that touches the installation: by the run's resource, the address and host
  in its context, the store keys of the installation's tokens, or its endpoints.
  So its token is released only to an observation, even for a run whose context was
  altered after it was made. Work of **another** installation that names one of those
  keys or endpoints is refused too, its observations included.

### The denylist is the server's record

The sync records the observed-only state on the installation itself, with every token
key and endpoint the installation has declared while observed only. The record does not
depend on the definition being readable:

- A definition that says `manage: false`, even one with other errors, puts the
  installation on the denylist and adds its keys and endpoints.
- A definition that is **invalid or missing** changes nothing: the installation stays
  observed only. So breaking ziost's `installation.yaml` in the same commit that points
  another installation's token at ziost's store key does not release ziost's token.
- Only a **valid** definition that asks to be managed takes the installation off the
  denylist.

### Trust is bound to the management mode

A trust grant is for the endpoints it was given for **and** for the management mode the
installation was in. Entering observed-only turns a grant given while managed into one
for observations alone, so observing goes on without a click, but managing again does
not: however briefly the installation was observed only, going back to managed waits
with *Waiting for trust: … was trusted while it was observed only*, observations
included, and its page asks for trust again, as for a new endpoint. Until an operator
trusts it as managed, its keys and endpoints stay refused to every other installation's
work.

The installation page and the inventory show it as **Observed only**, and the API's
installation summary has `managed: false`; its trust has `managementChanged: true`
while it waits for that trust.

## Plans that act on a machine that is not the run's own

Before an approval is offered, the effects of every OpenTofu plan step are checked
against the agent's plan summary (see
[the executor contract](../contracts/agent/v1/executors.md#preview)), which reports for
every change whether it imports something and, for a resource of a Proxmox provider,
every VM and node it names before and after. The plan fails the run, naming each change,
when:

- **any change imports an existing resource.** What OpenTofu would adopt is something
  Octopushy did not create, so an `import` block is refused whatever it imports.
- **a Proxmox resource with a create, update, delete or replace action names a VM other
  than the run's own,** or names its own VMID without its node. That covers more than
  deleting a VM: firewall options on VM 100, an HA resource, an update in place.
- **a Proxmox resource names no VM, or one not known until apply,** unless its type is on
  the short allowlist of types that never act on a VM. That includes resources that only
  name a node, such as a network bridge.

A **provision** or **retire** run acts on one machine: the guest's VMID on the guest's
host, a VM or a container as its `type` says. Changing or removing that one keeps its usual rules: approval unless destructive is
explicitly automatic, then unprotect, apply, and reprotect if the apply does not finish.
**Unprotect** is sent only when the plan deletes or replaces that VM *and* the guest owns
it (see [ownership](#ownership-reservation-and-confirmed-creation)), for a provision and a
retire alike. Otherwise the VM keeps its protection, and Proxmox refuses the removal.
One more plan lifts it: a provision plan that updates the guest's own **container** in place,
since Proxmox refuses any change to a protected container's disks, growing the root disk
included. That run reprotects the container whether the apply finishes or not. The apply is
still exactly the approved plan, which removes no machine.
A **pipeline stage** owns no machine, so it may change no Proxmox resource that names a
VM at all, and nothing of a Proxmox provider outside the allowlist. A plan that counts
changes without listing them is refused too.

The allowlist (`GuestVmPlan.MachineFreeTypes`) holds files on a node's datastore:

| Type | Why it never acts on a VM |
|---|---|
| `proxmox_virtual_environment_file` | Uploads a snippet, ISO or template to a datastore |
| `proxmox_virtual_environment_download_file` | Downloads an image to a datastore |
| `proxmox_download_file` | The provider's newer name for the same |

A resource counts as Proxmox when its type starts with `proxmox_`, or the agent says its
provider is a Proxmox provider. The approval step and the dispatch of the apply ask
again, so no path offers such a plan for approval or applies it.

## A VMID that is an observed VM Octopushy does not manage

A guest whose `vmid` is a VM in its installation's latest observation, and that VM is
not the guest's own, is blocked, for a provision and a retire alike:

- **At sync,** the guest is invalid, with an issue such as *VMID 100 on pve01 is an
  existing VM Octopushy does not manage (c-fedora-01)*, and no run is proposed.
- **A manual plan** is refused with the same reason, since an observation may be newer
  than the sync.
- **At dispatch of the plan step,** the check is made again, and the run fails with it.

A VMID bound to another guest is reported as that guest's.

Observations are runtime data, so this check is the server's, not the repository
loader's. **Without an observation of the installation yet, nothing is blocked:** there
is nothing to compare with. Creating a VM at a VMID that exists fails at the provider
anyway, and acting on a machine that is not the guest's own is refused by the plan check
above whatever has been observed.

## Ownership: reservation and confirmed creation

A guest **owns** a VM only after a confirmed creation, recorded as its binding:

1. When the server sends the apply of an approved provision plan that creates the
   guest's VMID on its node, it records a **reservation** for that installation, node and
   VMID, with the time it was first sent. A reservation grants nothing by itself.
2. When that apply succeeds, the reservation is marked as applied.
3. When verify-guest then sees the VM on that node with the tag `octopushy` and a
   description containing `Managed by Octopushy as <guest address>.`, the guest is
   bound to it. The example module sets both; a provision module has to.

A successful apply whose plan did not create the guest's machine never binds: verify
says so and the guest stays unbound, so nothing that waits for provisioning starts. A
verified VM without the marks fails the verify step. What an OpenTofu state holds is
never read as ownership: a state can hold a data source or an imported resource for any
VM. A workflow whose machine stage has `verify: none` cannot confirm a creation, so its guest is never bound.

A reservation keeps one thing open: retrying the same guest's create after an apply that
did not finish. While it is **recoverable**, the VM at that VMID and node counts as the
guest's for the VMID check and for lifting protection, so a half-made VM can be replaced.
It stops being recoverable as soon as any observation from **before** the reservation
shows a VM at that VMID: then the create failed on a VM that was there first. A
reservation is cleared when the guest's VMID or node changes while it is unbound, and
when the guest is retired.

## Attaching a machine Octopushy did not create

A guest declared `machine: attached` names a VM that exists. Its VMID being an observed VM it
does not own is expected, and is not refused at sync. It is bound only by an **attach run**, which
a person starts on the guest's page, and whose approval is that person's decision:

1. **Verify** the machine at the guest's VMID is on its host and of its kind, running or not.
2. **Protect** it, with the installation's privileged token. That token is released to an attach
   run only after the person's decision and the verify, and only while the VMID is not another
   guest's, bound or reserved, and the latest observation shows a machine there.
3. Bind the guest to it, recording who attached it.

An attached guest never gets a provision or retire run, so no plan of it can create, replace or
delete its machine. Retiring it, or deleting its folder, detaches it.

## A machine that is gone, or on another host

A binding is to an installation and a VMID, not to a host, so a VM that moves within a
cluster stays the guest's.

**A plan that creates a bound guest's machine again waits for a person,** whatever the
policy says, `destructive: automatic` included, and a deploy's same-click approval does
not cover it. Such a plan means OpenTofu's refresh found the machine gone: deleted outside
Octopushy, or waiting to be restored from a backup. An empty machine at that VMID would
stand in the way of the restore. Restore it at the same VMID and plan again: the binding
still holds, the restored configuration still carries the tag and description, and the plan
finds it. Approve only to start over with an empty machine.

**The guest shows what the installation's latest observation says of its machine,** as
`drift` in the guest's summary, when the guest is bound and the observation was made after
it was bound, proves it saw every VM, and, for a cluster, was quorate:

- `missing`: no host lists a machine at its VMID.
- `moved`: the machine is on another host than `host:` in `guest.yaml`. A plan of the guest
  would migrate it back, and waits for a person, as every migration does (below).
  **Accept placement** on the guest opens an edit of its host, set to the one it is on; its
  plan then changes nothing.

This is shown, not acted on: nothing is planned, retired or written because of it.

**A plan may move a bound guest's machine to its host.** A change of `host:` makes the
provider migrate the machine when its module sets `migrate = true`. The plan's update names
the guest's VMID on two nodes, the one it is on and its own, and is allowed only when it is
an update of the machine alone, names nothing else, and the guest is bound to that VMID in
its installation. **A migration always waits for a person,** whatever the policy says: the plan
cannot tell a `host:` changed on purpose from a machine moved in Proxmox, which it would move
back. A plan that would **replace** the machine to move it is refused, whatever
the policy says, with how to migrate it instead. An edit in the form refuses a host the
installation does not list, and one without snippets for a guest whose host key is
Octopushy's, which would otherwise be replaced.

## Tools apart from the agent and from each other

The agent image runs OpenTofu, Ansible and Nix directly (`--tool-runtime local`). Code from the
repository runs inside them with the attempt's credentials, and that is accepted below. What it
must not reach is the agent's own identity and files, or another attempt's credentials: a
preview must not read what an apply holds.

So the agent starts as root and runs each attempt's tools as an unprivileged user of its own,
leased from a fixed pool in the image (`octopushy-tool-0` to `-15`). Before it runs a tool:

- **The drop is complete.** Real, effective and saved user and group IDs are the user's, there
  are no supplementary groups, no capabilities, and `no_new_privs` is set, so no set-user-ID
  program leads back. The child checks all of it, and that `setuid(0)` fails, before it runs the
  tool. Files the tool creates are its own alone (umask 077). A set-user-ID or set-group-ID
  bit on a program a tool runs is ignored; none of OpenTofu, Ansible, ssh or Nix needs one.
- **It reaches its workspace and nothing else.** The state and work directories are root's and
  can be passed through but not listed (0711); the identity key, certificate, outbox (stored
  results, plan artifacts) and everything else in them is root's and private. A workspace is
  `root:<its user's group>` 0750: the user cannot add, rename or remove its entries, so the
  marker and the `.change-started` record beside the workspace stay the agent's, as does
  `ansible/`, which the agent writes into between runs. Another attempt's workspace belongs to
  another user. The agent's process cannot be read through `/proc` or signalled by a tool.
- **No shared cache.** Each attempt has its own OpenTofu provider cache in its workspace, as a
  shared one would let one attempt plant a provider another runs. Providers are downloaded for
  each attempt.
- **What the agent reads back is hostile input.** It opens a file a tool wrote without following
  a link in any path component, refuses a file with another link (a link to a file of the
  agent's), and caps its size. Nothing of the user's runs while it does: every process of the
  user is killed when the tool exits.

When the attempt ends, every process of its user is killed: `kill(-1)` as that user, and each
of its thread groups by ID, until no thread in `/proc/<pid>/task/*` runs as it, so a group whose
leader has exited while its other threads run is found and killed too. The workspace is taken
back (root, 0700), and whatever the user owns in `/tmp`, `/var/tmp`, `/dev/shm`, `/dev/mqueue`
and `/run/lock`, and its System V IPC objects, are removed. Only when every one of those steps
is done and verified may another attempt get the user. Otherwise, a process that survives
SIGKILL, a `/proc` that cannot be read, a workspace not taken back, an entry not removed, IPC
objects left, or a sweep that runs out of its 200 000-entry budget, the user is quarantined:
nothing runs as it until a later cleanup of it succeeds, which a lease tries when no user is
free. Another user's entries count toward the budget: the sweep cannot skip a folder of another
user's, which may hold this one's files, so a hostile attempt that fills the shared folders can
quarantine users, costing capacity, but not hand a user's files to its next attempt.

Cancelling or timing out a tool still signals its process group first, and the hard kill takes
every process of its user. At start the agent cleans every tool user up the same way, after
whatever a previous process left; a user that cannot be cleaned is quarantined, and with none
left the agent refuses to start. It also takes over the state directory of an agent that ran as
an unprivileged user before.

**Nix is kept apart from the agent and from other tools, not from other builds.** Its
single-user store belongs to one user, the image's `octopushy-agent` (10001), which every build
must write to. Builds run as that user, never root, one at a time, and the agent uploads only a
regular file of that user's inside the store. Its cleanup after each build is the one above; when
it fails, no build runs, and the agent does not offer `nix`, until a later cleanup succeeds. A
build can still change what is in the store for the next one, as before.

**An agent that is not root cannot drop.** A development agent, or the image run with
`--user`, runs every tool as its own user, which can read the agent's files, and runs one
attempt at a time whatever the server allows. It says which mode it is in when it starts. As
root without the pool's users it refuses to start rather than run tools as root.

The container needs only `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `KILL`, `SETUID` and `SETGID`, and
runs with `no-new-privileges`; Compose drops every other capability. The container runtimes
(`podman`, `docker`) are unchanged: each tool runs in a rootless container of its own.

## Threat model and accepted limits

The guards assume one operator who writes all the Git code, and they stop that
operator's mistakes. They do not stop code from the repository that sets out to reach
another machine: a plan and an apply run the repository's OpenTofu with the
installation's token, and that code can do whatever the token allows. Out of scope,
with the examples a review found:

- **Apply-time executable resources and data sources.** `terraform_data` with a
  `local-exec` provisioner, or an `external` data source, can call the Proxmox API with
  the token that is in the environment. No plan summary shows what they will do.
- **Nested references the summary does not parse.** It reads only top-level attributes,
  so `disk.path_in_datastore` pointing at another VM's disk, a `clone` source, or
  `hostpci` and `usb` passthrough reach other machines without naming them.
- **Allowlisted file resources** can overwrite a shared ISO or snippet that other VMs
  use, since files on a datastore are on the allowlist.
- **Providers whose name matches neither** the `proxmox_` type prefix nor a
  `provider_name` ending in `/proxmox` are not read as Proxmox resources at all.
- **A plan runs provider code with credentials before any guard sees it.** The plan step
  gets the installation's read-only (or shared) token, and the provider reads Proxmox
  while it plans. The guards judge the plan's result, not what planning does.
- **The agent's plan summary is trusted.** The controller never sees the JSON plan,
  which can hold configuration values; it relies on the agent to report every change,
  import and target. A missing target fails closed, but an agent that lies is outside
  this boundary, as an agent is trusted with the credentials it is sent.

The boundary against hostile code is on the Proxmox side, and it is the hardening we
recommend next: give Octopushy a token scoped to a Proxmox pool that holds only the VMs it
manages, so that whatever runs with the token cannot reach any other VM. The guards here
do not depend on it.
