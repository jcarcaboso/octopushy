# Safety boundary

Octopushy holds Proxmox tokens with wide privileges, often root. It must never plan or
apply against a machine it does not manage. Three guards hold that line, in addition to
[installation trust](installation-trust.md) and VM protection. No approval and no policy
lifts any of them, `destructive: automatic` included.

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
  altered after it was made.

The installation page and the inventory show it as **Observed only**, and the API's
installation summary has `managed: false`.

## Plans that remove a machine that is not the run's own

Before an approval is offered, the effects of every OpenTofu plan step are checked. A
plan that deletes or replaces a `proxmox_virtual_environment_vm` or
`proxmox_virtual_environment_container` fails the run, with the VMID and node of each
such machine in its detail, unless the machine is the run's own:

- A **provision** or **retire** run owns one machine: the guest's VMID on the guest's
  host, as the state names it. Removing that one keeps its usual rules: approval unless
  destructive is explicitly automatic, then unprotect, apply, and reprotect if the apply
  does not finish.
- A **pipeline stage** owns no machine, so any such removal is refused.
- A removal whose VMID or node the plan does not say is refused: what cannot be shown to
  be the guest's own is treated as someone else's. So is a plan that counts deletes or
  replaces but lists no changes.

The approval step and the dispatch of the apply ask again, so no path offers such a plan
for approval or applies it.

## A VMID that is an observed VM Octopushy does not manage

A guest whose `vmid` is a VM in its installation's latest observation, and that VM is
not the guest's own, is blocked:

- **At sync,** the guest is invalid, with an issue such as *VMID 100 on pve01 is an
  existing VM Octopushy does not manage (c-fedora-01)*, and no provision run is proposed.
- **A manual plan** is refused with the same reason, since an observation may be newer
  than the sync.
- **At dispatch of the plan step,** the check is made again, and the run fails with it.

The VM is the guest's own when the guest is bound to it, after its first apply, or when
the guest's own OpenTofu state records it: a first apply that made the VM and did not
finish can still be recovered. Work of the guest's that may have made the VM and not
reported yet does not count as a collision either. A VMID bound to another guest is
reported as that guest's.

Observations are runtime data, so this check is the server's, not the repository
loader's. **Without an observation of the installation yet, nothing is blocked:** there
is nothing to compare with. Creating a VM at a VMID that exists fails at the provider
anyway, and removing a machine that is not the guest's own is refused by the plan check
above whatever has been observed.

## Not covered

Narrowing the Proxmox tokens to what Octopushy needs remains the hardening planned for
later. These guards do not depend on it.
