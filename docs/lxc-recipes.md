# Containers: data, trust and the Proxmox token

**November 2, 2026.** What a Proxmox container gets from Octopushy beyond [`type: container`](lab-repository.md#containers):
a guard for the data in it, a way to give it protected secrets without a cluster, and what keeps its
plans away from guests nobody declared. Nothing here installs anything.

## What Octopushy does, and what it does not

Octopushy's side is generic: `type: container`, `provision.inputs`, `provision.secrets`, a recipe's
`secrets` and `modules`, `guestType: container`, and `inputs.startup`. It does the same for every container:

- reads the guest and the recipe, checks `provision.inputs` against the recipe's schema, and hands
  OpenTofu the guest as `var.guest`, the host's settings as `var.host`, and the template as `var.template`;
- plans without any secret, shows the plan, waits for approval where the policy says so, and applies
  that saved plan with the secrets `provision.secrets` names, which arrive as `TF_VAR_<name>` for the
  apply only;
- lifts Proxmox's protection flag, only for a plan its policy lets through, and puts it back if the
  apply does not finish ([the rule](lab-repository.md#rules), item 11);
- checks the container through the Proxmox API afterwards (`verify: proxmox`).

**OpenTofu makes the machine. It does not install what runs on it.** `recipes/proxmox-lxc` creates the
container and stops. Software belongs to Ansible, through a guest's `setup` and its services. Technitium
is the exception for now: [`technitium-lxc`](technitium-lxc.md) keeps installing through its own OpenTofu root.

Why a generic OpenTofu install was built on this branch and then taken out, because the reasons stand:

- **OpenTofu cannot tell who answers on SSH.** It checks a host key only against a public key, and nothing
  can give it the container's before it connects, so an apply that succeeds does not prove the real
  container was installed. An Ansible run connects to a pinned host key and fails closed.
- **Secrets through OpenTofu needed a hand-built seal.** It had no review beyond this project's, and it
  left a sealing key in the state. A playbook gets its secrets over a connection that is already authenticated.
- **It was a second way to declare what runs on a guest,** with no preview of tasks and no declared outcomes.
- **A script must be run again without harm** in either case. That lesson moved with the content:
  it installs what is missing, rewrites a file only when it differs, restarts only what changed, never deletes
  data, and ends by checking itself.

## Secrets

### What the store does with a secret

An operator sets a secret under **Variables** or with `PUT /api/variables`. After that, **nothing shows it again**:

- `GET /api/variables` returns `value` only for an entry whose visibility is `plain`; for a secret it is null.
- `VariableStore.GetAsync`, which decrypts, is used by the server for its own two credentials (the
  repository's and the disk reporter's), not by an API.
- A run's secrets are decrypted by `ResolveForAttemptAsync`, for an attempt, to hand to an agent, and only for an
  apply of a recipe's secrets, only after approval and the checks of
  [protected secrets](installation-trust.md#protected-secrets).

So **an operator who generates a password keeps a copy** if anything else has to read it: Octopushy will not
hand it back. Never put one in Git, a state, or a saved plan.

### Trusting a guest

A protected store entry is released only to a resource with a grant, for code with a digest. A cluster's
**Trust cluster** records the grants for its members. A guest that belongs to no cluster has the same on its
own page:

1. Mark the entries **Protected** under **Variables**.
2. The guest waits with *Waiting for trust*. Its page shows why, and the same details Trust cluster shows
   for a member: the protected entries it binds, the digest of its code and of how it runs, its state backend,
   variables, agents, Proxmox connection and the tokens it uses, template, volumes, and every key that can log
   in to it as root, shown by its end.
3. **Trust guest** records a grant for each protected key the guest's run binds. It sends the digest it
   showed, and the server refuses it if the guest is now declared otherwise.

`POST /api/guests/{id}/trust` with `{ "codeDigest": "..." }`. It is refused for a guest that is a member of a cluster
(Trust cluster covers it), for one that binds no protected key, and for one that gives a protected key to its state
backend, which is never released. Whatever the repository's `policy` says, a run waits for the grant, and a
changed root, a changed binding, or a different key blocks it again until the next trust.

**What a commit can do to a guest with protected secrets.** The grant covers the digest of the recipe, the root,
the listed modules, and the whole of how the guest runs. A commit that changes any of it stops the guest, and it
receives nothing until an operator trusts the new digest, so a commit cannot start sending a secret somewhere by
itself. What it can do is get an operator to trust something they did not read: **trust is a review, and it is
only as good as that review.** It cannot grant, protect or unprotect: those are in the database. It cannot move a
protected entry to a state backend (never released), or add a login key (it is in the digest). A commit that is
trusted runs its code with the secret.

## Stateful guests

Proxmox deletes a container's volumes with the container, and that includes a mount point of its own: moving data
to a mount point does not save it. For a guest that holds data found nowhere else, **a plan that would replace or
destroy it must stop**, before anyone is asked to approve it. Say so in `guest.yaml`:

```yaml
stateful: true
```

Octopushy reads the plan the agent reports, and at the approval step refuses a plan that would:

| The plan | Reason |
| --- | --- |
| delete or replace any Proxmox VM or container | Its disks go with it. |
| create the guest's own VM or container, with nothing to replace | It is gone, deleted outside Octopushy. Creating it again would give whatever connects to it an empty database that looks healthy. |

The run ends *failed* at its approval step, with the reason. `policy` does not enter into it, `destructive:
automatic` included: **no approval can pass it, so nobody is asked.** Proxmox's protection is not touched, and
nothing changes.

**The guard arms when the guest has been deployed:** once a run has verified it. Until then there is nothing to keep,
so a first deployment that failed can be retried, and the [table of rule 11](lab-repository.md#rules) decides as it
does for any guest: a tainted container that never finished recovers on its own, and anything else waits for a person.
What follows verification is a playbook, and a playbook that fails is run again, not replaced. A change in place
is not refused: cores, memory and the like go through the ordinary plan.

### Why this is not `prevent_destroy`

The first version was a `terraform_data` resource with `prevent_destroy` in the recipe's root, created only after the
install finished, so a failed first install could be retried. With the install gone from OpenTofu it had nothing to wait
for, and it had other costs that this version does not have:

- **It belonged to the recipe, not the guest.** Removing it unguarded every guest of that recipe. The marker is in one `guest.yaml`.
- **It could not be a setting,** because `prevent_destroy` has to be a literal, so a stateful recipe had to be a root of its own.
- **`-exclude` went straight through it,** and OpenTofu's own error offered that. Octopushy never excludes anything, and it
  judges the saved plan that would be applied, so there is no such option here.

The guard is not a lock on Proxmox. Someone with the token and a shell can still delete the container; Proxmox's
protection flag is what stands in that way.

### Retiring or replacing on purpose

Take `stateful: true` out, in a commit of its own so that the change is plain to review. That changes the guest's
inputs, so it is planned again, and from there the guest follows [rule 11](lab-repository.md#rules):
a plan that replaces or deletes the container waits for approval unless `destructive: automatic` is written down, and
a failed apply puts protection back. Put the marker back once the replacement has been verified.

- **`lifecycle: retired`:** the marker can go in the same commit, because that run reads the commit it was built from.
- **Deleting the folder:** the marker has to be out first. With it still there, the removal fails at the approval step and has
  changed nothing: restore the folder, take the marker out, commit, and delete the folder again. The removal is built from
  the revision last applied, which may still have the marker, so the guard reads the definition last synced instead.

**A container deleted outside Octopushy** stops the same way, although the data is gone. Decide what to do knowingly: take the
marker out, let it be created, restore from a [backup](#backups), and put the marker back.

**The guard watches the container, not what a run does to its data.** Lowering a retention setting is an ordinary change
in place, and the application then discards what is older. That belongs to whatever applies it, and is declared as
destructive there.

## What keeps a plan away from other guests

A Proxmox token carries no notion of which guests a lab declares. **Keeping a plan off a guest nobody declared comes from the
definitions, not from the token's permissions:**

```yaml
# installation.yaml
reservedVmids: [100]              # made by hand; Octopushy does not manage it
```

- A guest that uses a reserved VMID is invalid.
- A plan that would change a reserved VMID, in any way, fails at the approval step. No policy passes it.
- A guest's own identity is checked too: VMIDs are unique in an installation and bound on first apply, and protection is lifted
  only for the guest's own VMID on its own host.

What this does not do: it reads the plan, so it stops a plan from being applied. It is not a sandbox. **Planning
runs repository code with the token**, and with one shared token that is the whole of it, before any approval
([shared credentials](proxmox-credentials.md)). A provider or an external data source in a reviewed root could call the
API directly. Review before merging is the defence, as with any code that runs at plan time, and a
[narrower token](#later-hardening-a-narrower-proxmox-token) is the way to make that matter less.

## Backups

**A dump inside the container dies with it, so it is not a backup.** It covers a mistake, such as a dropped table. A disk
failure or a destroyed container takes it. What is a backup is a Proxmox backup job (vzdump) to storage other than the
container's own: the root volume is always part of one, and a mount point only if it is a volume with `backup=1`
(`mountpoint_backup_enabled` in pve-container; the module defaults `backup` to true, and a bind mount is never included).
This repository does not create the job.

## Later hardening: a narrower Proxmox token

**Not a prerequisite.** A real installation can run on one token with all privileges, and [`ziost`](../examples/lab/labs/home/proxmox/ziost/installation.yaml)
does for now. This section is for after everything works, when the Proxmox servers and API keys come down to
moderate privileges. Nothing in Octopushy relies on it, and nothing is safer until it is done.

The example's `readOnlyToken` and `privilegedToken` are the shape this would use. Scoped to container 102 on `ziost`, with
VM 100 given no privileges. **Everything here was read from the source of Proxmox VE at git.proxmox.com and has never been
proven on a host**: the end-to-end proof, a real create, update, stop and destroy with such a token, is still to do.

| Repository | Commit |
| --- | --- |
| pve-container | `761aea86f1ef3b4213590fbcfdf1c0c4b57fa2c4` (6.1.16) |
| pve-manager | `c79d601b09c00f1b7a7d0ef7ec0fc9326038298e` |
| pve-access-control | `dff26b458651b4e1141ff0a41a296d2904b16228` (9.1.2) |
| pve-firewall | `09225d8fc90c3b24b248e29b832e7ff08c42eb88` |
| pve-storage | `4250dfd533c7d98c632cd4f033610b5cb1719800` |
| pve-guest-common | `38f27bab64a27533fb87ef3d7f8d2f81a22d0dab` |
| pve-common | `b9b41d526f1ee1c0424765e72af6181fff69f611` |

That is each repository's head at the time, not the versions on `ziost`: **check what `ziost` runs**.

### What it would need, and where

An API token has its own ACL, and with privilege separation (`--privsep 1`) its rights are the intersection of its
and its user's, on each path, so both need the entries below. Granting on `/vms/102` leaves VM 100 with no
privileges, as long as the user has none on `/`, `/vms` or `/vms/100`.

| Privilege | Path | For |
| --- | --- | --- |
| `VM.Allocate` | `/vms/102` | Create and destroy the container (`create_vm`, `destroy_vm`). Not `/vms`, and no pool is needed. It is also what setting `nesting` asks. |
| `VM.Config.CPU` | `/vms/102` | `cores`. |
| `VM.Config.Memory` | `/vms/102` | `memory`, `swap`. |
| `VM.Config.Disk` | `/vms/102` | `rootfs` and `mpN`, and resizing the root disk. |
| `VM.Config.Network` | `/vms/102` | `net0`, and `hostname` and `nameserver`, which are network options here. **Also writes the container's firewall options and rules** (there is no `Firewall.*` privilege). |
| `VM.Config.Options` | `/vms/102` | `startup`, `onboot`, `protection`, `tags`, `description`, `ostype`. **So the same token can lift `protection`**, which Octopushy's unprotect step does. |
| `VM.PowerMgmt` | `/vms/102` | Start, stop, shutdown, reboot. |
| `VM.Audit` | `/vms/102` | Read the config, `status/current` and the interfaces; reading the firewall. |
| `Datastore.AllocateSpace`, `Datastore.Audit` | `/storage/vm-ssd` | A new root volume or mount point. |
| `Datastore.AllocateTemplate`, `Datastore.Audit` | `/storage/local` (the template storage) | `download-url` of the template, and using it to create a container. |
| `Datastore.Allocate` | `/storage/local` | **Only to delete the template**, which `proxmox_download_file` does when the guest is destroyed or the template changes. See below. |
| `Sys.AccessNetwork` | `/nodes/ziost` | `download-url` and `query-url-metadata`. `Sys.Modify` is not needed. |
| `SDN.Use` | `/sdn/zones/localnetwork/apps` | The bridge `apps`. A plain Linux bridge needs it too, since pve-container 5.0.1. If `apps` is an SDN vnet, use its zone instead of `localnetwork`, and propagate if the guest has a VLAN tag. |

Not needed, from the source: `VM.Config.HWType`, `.Cloudinit` and `.CDROM` (no use for LXC), `Sys.Modify` for
`startup` (that rule is QEMU's, for a VM) or for free-form `tags` (only for a registered tag, or if the
datacenter restricts them), and `Pool.Audit`, `Sys.Audit` or `VM.Audit` on `/` for the provider's flow, by
inference from the stand-in the tests use, which answers no such request.

### Commands

Text only: nothing was run, and none of this is needed to deploy. Role names cannot start with `PVE`.

```text
pveum role add OctoLxcGuest --privs "VM.Audit,VM.Allocate,VM.PowerMgmt,VM.Config.CPU,VM.Config.Memory,VM.Config.Disk,VM.Config.Network,VM.Config.Options"
pveum role add OctoLxcDisk --privs "Datastore.AllocateSpace,Datastore.Audit"
pveum role add OctoLxcTemplate --privs "Datastore.AllocateTemplate,Datastore.Audit,Datastore.Allocate"
pveum role add OctoLxcNetFetch --privs "Sys.AccessNetwork"
pveum role add OctoLxcBridge --privs "SDN.Use"

pveum user add octopushy-lxc@pve --comment "OpenTofu, container 102"
pveum user token add octopushy-lxc@pve tofu --privsep 1

pveum acl modify /vms/102 --roles OctoLxcGuest --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /storage/vm-ssd --roles OctoLxcDisk --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /storage/local --roles OctoLxcTemplate --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /nodes/ziost --roles OctoLxcNetFetch --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /sdn/zones/localnetwork/apps --roles OctoLxcBridge --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 1
```

`pveum acl modify` needs the user and the token both, as above. To check that VM 100 is untouched:

```text
pveum user token permissions octopushy-lxc@pve tofu --path /vms/100
pveum user token permissions octopushy-lxc@pve tofu --path /vms/102
```

The first should show no privileges. `--propagate 0` on the guest and the storages keeps the grants from reaching any other
guest or volume. Octopushy's own split tokens (`readOnlyToken`, `privilegedToken`, see [Proxmox credentials](proxmox-credentials.md))
are separate: the read-only one needs `VM.Audit`, `Datastore.Audit` and `Sys.Audit` on `/`. Its `/` ACL shows VM 100 to a
read-only token, which is the point of that token, and it cannot fetch a template's metadata.

### Surprises in the source

- **Destroying the container deletes the ACL on `/vms/102`**, and with it every user and token entry on that path
  (`remove_vm_access`). The next create fails with 403 until an administrator grants it again, and the token cannot grant it
  to itself. **After any destroy or replace of the container, run the `/vms/102` command again.** The alternatives are both
  weaker: a pool survives, but the create's tag check does not see pool membership; and a grant on `/vms` with `NoAccess` on `/vms/100`
  covers every future guest.
- **`Datastore.Allocate` bypasses the ownership check on a volume.** Without it, using a volume that VM 100 owns needs
  `VM.Config.Disk` on `/vms/100`, which the token does not have. With it, the token could delete that volume, or mount it into
  container 102. So it belongs on a storage VM 100 does not use, which `local` is only if VM 100 has no disks or backups there.
  Drop it if it does, and the token then cannot delete the template, so `proxmox_download_file` fails on destroy with a 403.
- **`nesting` does not need `root@pam`; every other feature does.** `features: nesting=1` needs `VM.Allocate` on the guest.
  Any other key, `keyctl=0` included, is refused to anything but `root@pam`, **and no API token can be `root@pam`**: the token's
  user is `root@pam!name`, which the check does not accept. The module sets `keyctl = false`, but the create request the provider sends
  carries exactly `"features": "nesting=1"` (checked against the stand-in with the real provider), so creating is expected to work.
  **What the provider sends when it updates `features` is unproven**; an update that carries more than `nesting` would fail with a 403
  that no token can fix.
- **`protection=1` blocks more than deletion**, for everyone: removing or changing a mount point, `mp0` or `rootfs`, through the
  config. Deleting a protected container is a plain error (HTTP 500, *can't remove CT 102 - protection mode enabled*), not a 403.
- A version-dependent detail: `start=1` at create is checked up front only since pve-container 6.1.12, and `Sys.AccessNetwork` for
  `download-url` since libpve-access-control 8.1.2, libpve-storage-perl 8.1.0 and pve-manager 8.1.5.

### Not proven without a live host

Which package versions `ziost` runs; whether `apps` is a bridge or a vnet, and its zone and VLAN tag; the datacenter's
`registered-tags` and `user-tag-access`; what VM 100 keeps on `vm-ssd` and `local`, and that `local` is file-based with `vztmpl` content and
`vm-ssd` allows `rootdir`; the user's existing ACLs on `/` and `/vms`; whether the provider's updates resend `mp0`, `rootfs` or extra feature
keys, which `protection=1` or the `root@pam` rule would refuse; whether it sends tags at create or in a later `PUT`; and, above all, that this
set is sufficient from a real create to a real destroy.
