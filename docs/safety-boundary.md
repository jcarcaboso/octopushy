# Safety boundary

Octopushy holds Proxmox tokens with wide privileges, often root. It must never plan or
apply against a machine it does not manage. Three guards hold that line, in addition to
[installation trust](installation-trust.md) and VM protection. No approval and no policy
lifts any of them, `destructive: automatic` included.

The guards stop **mistakes** by the one operator who writes the lab repository: a typo'd
VMID, a definition in the wrong installation, trust that no longer fits. They are not a
boundary against hostile repository code; see [threat model](#threat-model-and-accepted-limits).

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
host. Changing or removing that one keeps its usual rules: approval unless destructive is
explicitly automatic, then unprotect, apply, and reprotect if the apply does not finish.
**Unprotect** is sent only when the plan deletes or replaces that VM *and* the guest owns
it (see [ownership](#ownership-reservation-and-confirmed-creation)), for a provision and a
retire alike. Otherwise the VM keeps its protection, and Proxmox refuses the removal.
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
