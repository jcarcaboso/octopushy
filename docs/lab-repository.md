# Lab repository: layout and definitions

**Proposed October 16, 2026; updated October 20, 2026.** Status: a working
proposal, not an implemented parser or an approved public format. It applies the
[architecture agreement](architecture-agreement.md) to the Git repository that
defines labs, their Proxmox installations and hosts, their guests, and the services
on each guest. Examples borrow names from `nodes` for familiarity; this is not a
migration plan for that repository.

**Decided (October 16, 2026):**

- Folders are nested: lab → Proxmox installation → guest → service instance. The
  user expects this to be easier to manage with several Proxmox installations,
  many machines, and several services per machine.
- One repository can hold several labs, each with its own children.
- A VM's definition holds all of its configuration.
- The dashboard shows the resources and their references as a searchable graph, for
  overview and navigation.
- Each service instance is standalone: it belongs to one VM and holds its own files.
  Running the same application on two VMs means two independent folders, with no
  shared definition between them.
- VMs and services each carry an approval setting, automatic by default. When it is
  manual, every proposed change to that VM or service waits for approval. Settings
  flow down the tree, and the nearest one wins.
- Each VM has its own OpenTofu state, so VMs can be planned and applied in parallel
  and fail independently.
- Definitions are YAML.
- Destructive changes (deletes) have their own setting, which defaults to the
  resource's approval setting: manual if approval is manual, automatic if it is
  automatic. It can be set to manual on its own to hold back deletes only.
- Recreating a resource counts as a deployment, so it follows the approval setting.
- A service declares its own DNS records, because the service does not work if they
  fail. The zones they live in, and the systems serving them, are shared across the
  lab and are defined once on the lab.

**Decided (October 19, 2026):**

- Network exposure is declared generically in the service: DNS records, proxy
  routes, firewall ports, and certificates. The lab connects these to whatever
  systems it uses, such as Pi-hole or Technitium for DNS, through integrations.
  Each integration type has an adapter for that product.
- Application clusters are supported, starting with Technitium, on a basis other
  kinds can reuse. Each member is its own service on its own VM, usually on a
  different Proxmox host. Forming the cluster is a separate step: set up one member,
  then join the others to it. The cluster relates its members.
- Maintenance windows are optional, and a resource can have several. They are not a
  priority.
- Technitium is the only DNS integration for now, and no other adapters are planned
  yet.
- Firewall ports are opened in each guest's operating system, probably by a step of
  their own; how is still to be worked out.
- Each apps machine runs its own reverse proxy. There is no central proxy and none
  is planned, so a zone belongs to one lab. Sharing a zone between labs may come
  later.

**Decided (October 20, 2026):**

- Ports are as permissive as possible: a port is open on every interface and to
  every source unless its declaration limits it. A port can be limited to
  interfaces, to source networks, or both.
- How the ports step is implemented still needs checking.

## What the repository holds

**Credential boundary:** each Proxmox installation's credentials go only to the
endpoints an operator [trusted](installation-trust.md) for it. A new installation,
or a new endpoint for one, waits for that trust; other changes run as soon as they
are synced. This is separate from the per-resource apply policy described below,
and cannot be enabled or weakened by a setting in the lab repository. An application
cluster's admin password has the same boundary, over its members' addresses, certificates,
and code. A [protected](installation-trust.md#protected-secrets) store entry goes only to what an
operator granted it, and no definition can change that.

The lab repository is the desired configuration Octopushy reads. It is separate from
Octopushy's own source code and holds two kinds of files:

- **Definitions:** Octopushy's own YAML files. They name resources, connect them to
  each other and to operations, and set policy. Octopushy's forms edit only these.
- **Tool-native files:** OpenTofu, Ansible, NixOS, Compose, and scripts. Octopushy
  references and runs them but does not own them (section 5 of the agreement).

Everything else lives elsewhere:

| Where | What |
|---|---|
| Lab repository | Definitions, tool-native files, policies, variable-store key references |
| Octopushy backend (PostgreSQL) | Variable-store values, provider routing, agent enrollments and labels, drafts, runs, attempts, evidence, approvals, the definition snapshot, the entries each integration holds for Octopushy, who has really joined each cluster, what is trusted and granted |
| Octopushy backend, or an external OpenTofu backend for guests that name one | Infrastructure state, which for a cluster records who should be a member |

## Layout

```text
lab-repo/
├── octopushy.yaml                          # repository defaults, optional state backends
├── labs/
│   ├── home/
│   │   ├── lab.yaml                        # integrations, DNS zones, windows
│   │   ├── clusters/
│   │   │   └── technitium/cluster.yaml     # relates guests of this lab
│   │   └── proxmox/
│   │       ├── korriban/                   # a standalone host or a cluster
│   │       │   ├── installation.yaml
│   │       │   └── guests/
│   │       │       └── krb-crt-01/
│   │       │           ├── guest.yaml      # all of the VM's configuration
│   │       │           ├── nixos/          # optional tool-native files for this VM
│   │       │           └── services/
│   │       │               ├── vaultwarden/
│   │       │               │   ├── service.yaml
│   │       │               │   └── compose.yaml
│   │       │               └── arcane/
│   │       │                   ├── service.yaml
│   │       │                   └── compose.yaml
│   │       └── moraband/
│   │           ├── installation.yaml
│   │           └── guests/...
│   └── office/                             # another lab, same shape
├── templates/nixos-base/template.yaml      # VM templates (base images)
├── profiles/service-host/profile.yaml      # machine profiles (roles setup applies)
├── recipes/proxmox-vm/...                  # operation recipes (contract still open)
└── modules/                                # shared tool-native code: OpenTofu, Ansible, Nix
```

## Rules

1. **The folders are the tree.** A lab holds its Proxmox installations and
   application clusters, an installation its guests, a guest its service
   instances. Each resource has one folder; the folder name is its id, unique among
   its siblings. Its address is the path of ids, such as
   `home/korriban/krb-crt-01/vaultwarden`.
2. **Guests sit under the installation, not the host.** In a cluster, VMIDs are
   shared and a VM can migrate between hosts, so the host is a field in
   `guest.yaml`. Moving a VM within a cluster changes that field, not its folder.
   For a standalone installation, the installation is the host.
3. **A guest's folder holds all of its configuration:** `guest.yaml` with placement,
   hardware, network, profiles, access, and policy, plus the tool-native files for
   that VM and its services. Templates and profiles can prefill the form for a new
   guest, but the values are written into `guest.yaml`, so reading that one file
   tells everything about the VM.
4. **Moving a folder creates a new resource.** Moving a guest to another
   installation means a new VMID; moving a service to another guest means a new
   instance whose data has to move too. Octopushy treats the new folder as a new
   resource and the old address as missing, and proposes retiring the old one once
   the new one is verified. Nothing is destroyed implicitly. A rename in place is
   declared with `renamedFrom: <old id>`.
5. **Everything outside the tree is a reference:** templates, profiles,
   dependencies between services on different guests, the service or cluster an
   integration runs on, and the members of an application cluster, which are guests
   until services exist. Inside a lab,
   an address may leave out the lab, as in `korriban/krb-crt-01/vaultwarden`. The
   dashboard's graph shows the tree and these references together, and search finds
   any resource by name, address, or reference.
6. **Services are standalone.** A service folder belongs to one guest and holds
   everything that instance needs: its definition, Compose file, and other files.
   The same application on two guests is two independent folders.
7. **Settings flow down the tree.** The approval and destructive settings,
   maintenance windows, and agent labels set on a lab apply to everything below
   it; an installation, guest, service, or cluster can override them, and the
   nearest setting wins. A cluster sits directly under its lab, and what it sets does
   not reach its member guests. Approval is per
   resource: a guest's setting covers changes to that guest, and each service has
   its own, so approving a VM change never approves its services. VM configuration
   does not inherit (rule 3).
8. **Tool-native files** sit beside the resource that uses them, or under `modules/`
   when shared. Paths in a definition are relative to its file. Octopushy checks
   that they exist and parse; their content stays the tool's business.
9. **Secrets are references.** A definition writes non-secret values directly and
   refers to variable-store entries as `{ store: <key> }`, optionally with an
   exact version as `{ store: <key>, version: 2 }`. It never contains a secret
   value or names a secret provider.
10. **Removal is reviewed.** There are two ways to remove a guest:
    - Set `lifecycle: retired`. This keeps the definition in Git while the guest is removed.
    - **Delete the guest's folder.** Octopushy reports the guest as missing and
      proposes a retire run. That run is built from the revision
      the guest was last applied from, so it destroys what that definition created.
      It follows that definition's approval and `destructive` policy. **Plan removal**
      on the guest page proposes it again after it was cancelled. Restoring the
      folder cancels a removal that has not started changing anything. Managed
      workers are drained first and lose their enrollment once their VM is verified
      absent; their removal always needs approval.

    Either way, the history is kept.
    *Decided October 28, 2026: deleting a folder removes the guest. Before, it only
    reported the guest as missing.*
11. **Deletes have their own setting,** `policy.destructive`: `manual` or
    `automatic`. When it is not set, it takes the resource's approval setting, so a
    manual resource never deletes without approval. Setting `destructive: manual` on
    an automatic resource holds back only its deletes. Destructive means deleting a
    guest, a service, data such as disks or volumes, or a DNS record; removing a
    member from an application cluster also counts. Recreating a resource (a plan
    that destroys and recreates it) counts as a deployment and follows the approval
    setting; the preview still shows it as a recreate. Octopushy decides what a
    change does from the preview, such as an OpenTofu plan's delete and replace
    actions, not from the edit. Retiring a guest retires its services first, each
    under its own setting.

    **A VM or a container is the exception, because recreating one wipes its disks.** A
    provision plan that replaces or deletes a Proxmox VM (`proxmox_virtual_environment_vm`) or
    container (`proxmox_virtual_environment_container`) waits for approval unless
    `destructive: automatic` is written down somewhere: inheriting it from an
    automatic approval is not enough. The table is the same for both:

    | The plan replaces or deletes the guest's own VM or container, and | What happens |
    | --- | --- |
    | `destructive: automatic` is set explicitly | It goes ahead, as a retire would. |
    | OpenTofu replaces it because it is tainted, it is the only VM or container the plan removes, and Octopushy never applied or verified this guest | It recovers on its own, even with `destructive: manual`. |
    | anything else | It waits for a person, and the `approval` setting still applies. |

    The "guest's own" one is found by the plan's state naming the guest's VMID on its host,
    for the kind of resource the guest is: a VM resource never lifts a container's protection,
    nor the other way round.

    Once approved, Octopushy lifts the protection of the guest's own VM or container right before
    applying that plan, as a retire does, and the apply recreates it protected.
    A plan that keeps it never touches protection, and a preview never does. If the
    apply does not finish, for instance a failure, a cancelled run, or a closed
    uncertain outcome, the run puts protection back (a `reprotect` step, through the
    guest's `guestKind`, `qemu` or `lxc`) before it ends and says so, and
    planning again retries. The messages say *container* where the guest is one.
    *Decided November 1, 2026: before this, a replaced VM stopped at Proxmox's
    protection flag, whatever was approved, and someone had to lift it by hand.
    Extended to containers November 2, 2026, so a container that fails its first apply
    no longer gets stuck.*

    A [stateful guest](lxc-recipes.md#stateful-guests) goes further than the rule: once deployed, a plan that
    would replace, destroy or recreate it fails before approval, so no approval can pass it.
12. **Identity is stable.** A guest's address maps to its installation and VMID.
    Changing the VMID of an existing guest is refused as a possible new
    incarnation, and a new guest cannot claim a VMID that is already bound.
13. **Validation:** each kind has one JSON Schema, shared by the backend and the
    editor. The backend also checks that every reference resolves, VMIDs are unique
    per installation, dependencies have no cycles, every DNS record has one owner,
    and every store key enters the reference index.
14. **YAML, with a version in each file.** Every file starts with `schemaVersion`.
    YAML allows comments, and OpenTofu can read it with `yamldecode`. Form edits
    must keep comments and ordering. Deserializing to objects and serializing back
    drops comments, whereas the TypeScript `yaml` library's document API keeps
    them, so form edits could be applied in the frontend and saved into the draft
    the way editor edits are.

## Definitions

### Variable-store references

Anywhere a store reference is accepted, use `{ store: <key> }` or
`{ store: <key>, version: <positive integer> }`. For example:

```yaml
token: { store: proxmox/korriban/api-token, version: 2 }
tls:
  caCertificate: { store: proxmox/korriban/ca, version: 1 }
```

The same syntax applies to backend environment values, SSH keys, and references
in integration or setup definitions. A reference contains only `store` and the
optional `version`; neither a quoted version nor zero, a negative number, or a
fraction is valid.

**Renaming a key.** A key is the entry's identity: nothing renames or moves one, and
moving a key to another folder is the same as renaming it. The reference is in Git,
the stored value is encrypted under the key's name, and run history records it, so
the change is a replacement made in this order:

1. Set the new key. A secret's value cannot be read back, so enter it from its
   source.
2. Commit the changed references. A new key starts at version 1, so change any
   `version` too.
3. Review the plans this causes. A reference in a guest, in an installation's
   `connection`, or in a state backend is part of what a guest's plan is built from,
   so renaming a Proxmox token plans every guest of that installation again, and a
   run waiting on the old definition is no longer current.
4. Wait until **Used by** no longer lists the old key, which includes runs in
   progress.
5. Delete the old key. If it has execution history, deleting is refused: revoke its
   versions instead. The key then stays listed.

A reference to a key that is not set is listed under **Referenced but not set** and
blocks its run until the key is set, so a step taken out of order stops the work
rather than delivering nothing.

For a new run, an unversioned reference pins the active store version before its
first attempt; later attempts keep those pinned versions. An explicit version
expresses the desired binding, not proof that the credential has been verified,
and does not promote a candidate. Explicit candidate versions are blocked until
promoted.
The `system/` scope remains reserved and cannot be referenced by definitions.

### Repository settings: `octopushy.yaml`

```yaml
schemaVersion: 1
defaults:
  policy:
    approval: automatic           # automatic | manual
    # destructive: unset, so it follows approval
stateBackends:                    # optional: only to keep some state outside Octopushy
  gitlab:
    type: http                    # any OpenTofu backend type
    config:                       # passed to tofu init; {stack} is the guest's address with dots
      address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}
      lock_address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}/lock
      unlock_address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}/lock
      lock_method: POST
      unlock_method: DELETE
    env:                          # credentials go here, so they never enter a saved plan
      TF_HTTP_USERNAME: octopushy
      TF_HTTP_PASSWORD: { store: opentofu/gitlab-token }
```

Holds repository-wide defaults and any external OpenTofu state backends. Most labs need
none: a guest that names no backend has its state kept by Octopushy, with nothing to
configure ([how it works](opentofu-state.md)). Server settings (provider routing, agents, sign-in) stay out of Git. The
state backend format, the provisioning recipe file, and template images are the shapes
[milestone 1](milestone-1.md#definitions) implements.

### Lab

```yaml
# labs/home/lab.yaml
schemaVersion: 1
name: Home
timezone: UTC                     # for maintenance windows
execution:
  agentLabels: [home]             # agents that may act on this lab's resources
integrations:                     # systems this lab uses; type selects the adapter
  technitium:
    type: technitium              # the only adapter for now
    endpoint: https://10.1.0.177:53443
    tls: { verify: false }
    token: { store: dns/technitium/api-token }
    cluster: technitium           # runs on this lab's Technitium cluster
dns:
  zones:
    hlab.example.com: { integration: technitium }
    home.arpa: { managed: false } # owned by the router
```

A lab groups installations and application clusters. It carries no state or lock
of its own. Agents get their labels on the server when they enroll, so an agent
cannot grant them to itself. The lab also defines the DNS integration and zones
its services' records go into (see Network exposure below).

### Proxmox installation and its hosts

```yaml
# labs/home/proxmox/moraband/installation.yaml
schemaVersion: 1
type: standalone                  # standalone | cluster
connection:
  endpoint: https://10.1.0.50:8006/
  tls: { verify: false }          # or caCertificate: { store: <key> }
  token: { store: proxmox/moraband/api-token }
hosts:
  moraband:
    datastores: { images: local, disks: local-lvm }
    bridges: [vmbr0]
    limits: { concurrentOperations: 2 }
```

An installation is the management domain: one standalone host, or a cluster whose
hosts share a connection and a VMID space. Hosts are listed in it rather than given
folders, because guests are not nested under hosts (rule 2). Each host keeps its own
datastores, bridges, and capacity limits, and is its own failure domain. A cluster
lists several hosts and may give each its own endpoint. Defining an installation
does not mean Octopushy manages the cluster, its membership, or its storage.

`reservedVmids` lists VMIDs here that Octopushy does not manage, such as a guest made by hand: no guest may
use one, and a plan that would change one fails before approval. What keeps a plan off a guest nobody
declared has to come from here, [not from the token](lxc-recipes.md#what-keeps-a-plan-away-from-other-guests).

One `token` reference is sufficient. Alternatively, omit it and configure both
`readOnlyToken` and `privilegedToken` with distinct store keys; mixed modes are
invalid. Shared mode deliberately gives planning the same token as apply, so
approval does not make planning read-only. See [credentials](proxmox-credentials.md)
for routing, approval, legacy-run handling, and the optional split mode's provider
refresh blocker; these examples do not certify live ACL sufficiency.

### Guest

```yaml
# labs/home/proxmox/korriban/guests/krb-crt-01/guest.yaml
schemaVersion: 1
type: vm                          # vm | container
lifecycle: active                 # active | retired
host: korriban
vmid: 210
template: nixos-base
profiles: [base, service-host]
hardware:
  cores: 4
  memory: 8192
  disks:
    - { size: 100 }               # system disk; datastore from the host
    - { size: 200, datastore: data }
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.21/24
  gateway: 10.1.0.1
provision:
  recipe: proxmox-vm
  # state: { backend: gitlab }    # optional: an external backend from octopushy.yaml
setup:
  recipe: machine-tools           # a recipe with operation: configure
  inputs: { machine_tools_channel: stable }
  secrets:
    machine_tools_token: { store: machines/krb-crt-01/tools-token }
access:
  address: krb-crt-01.home.arpa   # by default the static or reserved address
  user: ops
  sshKey: { store: access/deploy/ssh-key }
  hostKey: { store: access/krb-crt-01/host-key }  # read through Proxmox, never scanned
policy:
  approval: automatic             # changes, including recreating the VM, run on their own
  destructive: manual             # deleting this VM or its disks waits for approval
```

Octopushy itself reads the placement and identity. `hardware` and `network` are
passed to the provisioning recipe as inputs and validated against the recipe's
schema, so Octopushy does not need a VM model of its own. So is `provision.inputs`,
which holds settings of the recipe itself and reaches OpenTofu as `guest.inputs`.
`provision.secrets` names OpenTofu variables of the recipe's root and the store
entries that fill them (see Containers below). The recipe also receives
the host's settings and the installation's connection, so one shared OpenTofu module
provisions every guest. Each guest has its own state, so guests are planned, locked,
and applied independently. Octopushy keeps it unless `provision.state` names an external
backend, where it is named by the guest's address. `nodes` does the same today by
having each guest's OpenTofu root read `node.json`; here the executor passes the
values instead.

`setup.recipe` names a recipe with `operation: configure`, which runs Ansible on the
guest over SSH once it is provisioned; see [configuring a guest](guest-configuration.md).

#### Containers

```yaml
# labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml
schemaVersion: 1
type: container
host: korriban
vmid: 320
template: debian-13-lxc           # a container template, not a disk image
hardware:
  cores: 2
  memory: 2048
  swap: 512
  disks:
    - { size: 8 }                 # the root disk, the only one
  mountPoints:
    - { path: /etc/dns, size: 4 } # further storage, each on a volume of its own
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.32/24
  gateway: 10.1.0.1
  dns: [10.1.0.1]
provision:
  recipe: technitium-lxc
  inputs:
    startup: { order: 1 }         # validated by the recipe, passed as guest.inputs
  secrets:
    admin_password: { store: dns/technitium/admin-password }
```

A container is a guest like a VM: the same runs, approval, protection, and retire flow,
with its own recipes, which declare `guestType: container`. The same rule as a VM's lifts
and restores its protection ([item 11](#rules)). A guest and its recipe must
agree on the type. Verification reads a container's addresses from Proxmox, since it has no
guest agent. Two things need that agent, so a container has neither:

- **Disk reporter:** no step is scheduled, and `setup.diskReporter` on a container is only
  a warning. Proxmox has no guest exec for LXC.
- **`guestFilesystem`** is refused on a container recipe.

A `worker` section is invalid on a container: a managed worker is a NixOS VM.

`recipes/proxmox-lxc` creates an unprivileged container with `nesting` on and `keyctl` off,
started at boot and protected from deletion. [`recipes/technitium-lxc`](technitium-lxc.md)
adds a DNS server to it. Proxmox reads a container's SSH keys only when it creates it, and
changing a mount point replaces the container, which Proxmox refuses while it is protected.
[Containers](lxc-recipes.md) covers what else one gets, such as `stateful: true`, which keeps a plan
from replacing or deleting one that holds data.

**Store references for a recipe.** A key under `provision.secrets` is the name of an
OpenTofu variable, and `TF_VAR_<name>` delivers the entry's value. Only the apply receives
it, after approval, so a saved plan cannot hold it and an unapproved commit cannot read it.
Declare the variable in the root with a null default, because it is unset while planning,
and with `ephemeral = true`, so that the state does not hold it either. A variable that
Octopushy already sets, such as `guest`, cannot be named. A retire run gets none of these.
Changing the value in the store starts no run. A recipe lists the ones it cannot do without
as `secrets: [admin_password]`, and a guest that leaves one out is invalid.

A root that uses another module names it in `modules` in `recipe.yaml`, so that a change to
that module plans the recipe's guests again. A protected entry that a guest binds this way goes only
to a grant: a cluster's members get theirs from Trust cluster, and any other guest from **Trust guest**
on its page ([protected secrets](installation-trust.md#protected-secrets)).

### Service instance

```yaml
# labs/home/proxmox/korriban/guests/krb-crt-01/services/vaultwarden/service.yaml
schemaVersion: 1
deploy:
  recipe: compose
  compose: compose.yaml
dependsOn: []                     # addresses of services this one needs
secrets:
  - key: services/vaultwarden/database-url
    file: { name: database-url, mode: "0400" }
  - key: services/vaultwarden/postgres-password
    file: { name: postgres-password, mode: "0400" }
dns:
  - { name: vaultwarden.hlab.example.com, type: CNAME, value: hlab.example.com }
verify:
  http: { url: https://vaultwarden.hlab.example.com/alive, timeoutSeconds: 180 }
policy:
  approval: manual                # this service's changes wait for approval
```

Each service folder is one deployment instance, such as
`home/korriban/krb-crt-01/vaultwarden`, with its own desired and applied release,
outcome, approval, and failure domain. In `nodes`, Arcane runs on two guests as
two separate directories; here it is likewise two standalone folders, one under
each guest. Secrets are prerequisites of the deployment (section 5 of the
agreement), and the binding here chooses how each one is delivered. It reaches
the reverse proxy on its own guest through Traefik labels in `compose.yaml`. Its
DNS records and ports are described under Network exposure below.

### Network exposure: DNS, ports, and routes

Services declare what they need from the network in generic terms and never name a
product (case C7 in the [nodes comparison](nodes-comparison.md)):

- **DNS records** go through the lab's integrations. Each entry under
  `integrations` has a `type` that selects an adapter built into Octopushy, and
  each zone names the integration that serves it. Technitium is the only adapter
  for now; moving to another DNS server later would change `lab.yaml`, not the
  services.
- **Ports** are opened in the guest's operating system (see Ports below).
- **Routes and certificates** stay in each service's tool-native files for now.
  Each apps machine runs its own reverse proxy as a service on that guest; services
  reach it through Traefik labels in their Compose files, and Traefik obtains
  certificates with ACME. Octopushy neither previews nor owns those routes. A
  generic `routes` declaration with a proxy adapter could come later.

#### DNS records

- **A service declares its own records** under `dns`. A guest can declare its own
  host record the same way, and `lab.yaml` can hold the few records no resource
  owns.
- **An integration can run on the lab's own resources.** The Technitium
  integration names its cluster with `cluster`, so the graph shows the dependency
  and records wait until the cluster is verified.
- **The adapter does four things:** read what the server holds, so the preview
  shows exact changes and drift; apply; remove; and verify. It runs on an agent
  with the lab's labels, which receives the integration's credentials for each
  attempt.
- **Each record has one owner.** Two resources declaring the same name and type
  fail validation. Octopushy records each entry it creates in the backend and
  changes or removes only those, so records made by hand or by other tools are
  never touched. An existing record it did not create shows in the preview as a
  conflict, and adopting it needs approval. A zone with `managed: false` is left
  to its other owner.
- **Creating the records is a step of the owner's deployment,** after the workload
  starts and before verification. Records are verified by resolving them at the
  lab's resolvers. A failure marks only that service instance, at that step.
  Changes to one zone run one at a time, but each record has its own outcome,
  unlike the batch-wide DNS gate in `nodes`.
- **Removing a record,** or retiring its owner, deletes it, so it follows the
  owner's destructive setting. Switching a zone to another integration creates its
  records in the new one and verifies them before removing them from the old one;
  those removals follow the lab's destructive setting.
- **A zone belongs to one lab.** Sharing a zone between labs may come later; record
  ownership would then be checked across labs.

#### Ports

```yaml
# .../guests/krb-crt-01/services/traefik/service.yaml (excerpt)
ports:
  - { port: 80, protocol: tcp }
  - { port: 443, protocol: tcp }
```

- **A service declares the ports it needs,** and the guest's operating system
  opens them. A guest can declare its own the same way. There is no lab-wide
  firewall.
- **A port is open everywhere unless it is limited:** on every interface and to
  every source. `interfaces` limits it to the named interfaces, and `sources` to
  the listed addresses or networks; with both, a connection must match both.

```yaml
# .../guests/krb-dns-01/services/technitium/service.yaml (excerpt)
ports:
  - { port: 53, protocol: udp }                  # open everywhere
  - { port: 53, protocol: tcp }
  - { port: 53443, protocol: tcp, interfaces: [ens18, tailscale0] }
  - { port: 5380, protocol: tcp, sources: [10.1.0.0/24] }
```

- **Applying ports is a step of its own on the guest** (proposed). It gathers the
  ports of the guest and of all its active services and applies them through the
  guest's setup recipe: before a service that needs a new port starts, and after a
  retired service stops, to close its ports. The step carries only that service's
  change, so it follows the service's approval. Steps on one guest run one at a
  time.
- **On NixOS,** the recipe passes the ports into the guest's configuration as an
  input, as `nodes` does with `nodeServices` today, and rebuilds from the guest's
  applied revision. A ports step therefore never applies guest changes that are
  still waiting for approval. A port below 1024 also lowers the unprivileged port
  start, as `nodes` does for rootless containers. An Ansible-managed guest would do
  the same through its own firewall, such as firewalld.
- **Limits map onto the guest's firewall.** The NixOS firewall opens ports per
  interface itself, as the Technitium template in `nodes` does for its LAN
  interface and `tailscale0`; a source limit needs extra rules (for example
  nftables input rules). Firewalld covers both with zones and rich rules.

### Application clusters

```yaml
# labs/home/clusters/technitium/cluster.yaml
schemaVersion: 1
type: technitium                  # selects recipes/technitium-cluster
members:                          # guests on different hosts, in this lab
  - { guest: korriban/krb-dns-01, role: primary }   # formed here
  - { guest: moraband/mrb-dns-01, role: secondary } # joined to it
settings:                         # shared by all members
  clusterDomain: cluster.home.arpa
  forwardZones:
    - { zone: home.arpa, forwarder: 10.1.0.1 }
credentials: { store: dns/technitium/admin-password }
policy:
  approval: manual
```

An application cluster relates guests of one lab, usually on different Proxmox hosts. It is not a
Proxmox cluster (case C8). [Technitium in Proxmox containers](technitium-lxc.md#a-cluster) is the
operator's guide; the implementation differs from the original proposal as noted here.

- **Members are guests,** because services are not implemented yet. Each stays an ordinary guest,
  with its own files, deployment, and approval; the cluster file lists them and holds what they
  share. A guest belongs to at most one cluster. References are checked: they resolve, there is
  one primary and at least one secondary, all in this lab, each with a static address. Members on
  one Proxmox host are a warning.
- **Forming comes after the members.** A cluster's run is blocked, naming the guests it waits for,
  until every member is applied and verified. It forms the cluster on the primary and joins the
  others in one step, in which each member has its own outcome, so a member that fails to join does
  not hold back the others. A member added later joins the same way.
- **The contract is a root and an executor.** The recipe's OpenTofu root applies settings, and
  the `technitium` executor joins, removes, and verifies, because OpenTofu cannot join without
  putting the password in a saved plan, and cannot see a rebuilt member at all. Other kinds of
  cluster would bring a root and an executor of their own.
- **OpenTofu state records who should be a member,** and Octopushy records who is. Each secondary is
  a `terraform_data` resource, so adding one is a create in the preview and removing one a delete. The
  agent checks members before every plan, so a member that is out of the cluster is a replacement
  there, and follows approval. A binding in the database keeps what identifies each joined member, so
  a removal touches that exact peer, and a guest cannot be retired until it has left.
- **Role changes and removals are explicit.** Changing the primary of a formed cluster is a promotion,
  which this version refuses, and so is changing `clusterDomain`. Removing a member makes it leave
  the cluster before the apply, which counts as a delete under the cluster's destructive setting, as
  does removing entries nobody declared. Its guest is retired separately. A guest still listed in a
  cluster cannot be retired, and one that is out of the file waits until it has left. Deleting
  `cluster.yaml` destroys nothing: the cluster is reported as missing, and its guests stay bound.
- **The cluster has its own policy,** flowing down from the lab. Approving a cluster change never
  approves its members' changes. Its `execution.agentLabels` flow down the same way.
- **Its runs and its members' runs exclude each other.** A cluster run waits for its members, then
  holds them until it ends, also when its outcome is uncertain. A guest that applies a change gets
  the cluster checked afterwards.
- **A cluster belongs to one lab,** and so do its members.

### Maintenance windows

```yaml
# in lab.yaml, or the policy of any resource below it
policy:
  windows:
    - { days: [sat, sun], start: "02:00", hours: 6 }
    - { days: [wed], start: "22:00", hours: 2 }
```

- **Windows are optional.** Without one, an automatic change runs as soon as it is
  ready. A resource can have several windows; they flow down like the other
  settings, and the nearest list wins. Times use the lab's `timezone`.
- **A window only limits when an automatic change may start.** An attempt that has
  started finishes even after the window closes. A change waiting for a window
  shows when it will run.
- **Approving a manual change runs it at once,** unless the approver chooses to
  wait for the next window.

### VM template and machine profile

```yaml
# templates/nixos-base/template.yaml
schemaVersion: 1
os: nixos
source:
  build: { recipe: nixos-image }  # or url and checksum for a downloaded image
availableOn: [home/korriban, home/moraband]
```

```yaml
# profiles/service-host/profile.yaml
schemaVersion: 1
requires: [base]
setup:
  nixos: { module: ../../modules/nixos/service-host.nix }  # or an Ansible role
```

A template is a base image; a profile is a role that setup applies, and a guest can
have several. Both are shared across labs and kept minimal: the image pipeline
(case C2) and how profiles compose are still open.

## The first journey through the files

New VM → configured guest → working service:

1. Add `guests/<id>/guest.yaml` under an installation. Octopushy proposes
   provisioning: the `proxmox-vm` recipe's OpenTofu plan for that guest.
2. Once the guest is provisioned and verified, Octopushy runs its `setup.recipe`, if
   it has one. Only Ansible does this today; profiles are not applied yet.
3. Add `services/<name>/` under the guest. When the secrets resolve and are
   delivered, Octopushy proposes deploying that instance: it opens the instance's
   ports on the guest, deploys it, creates its DNS records, then verifies it.
4. For a cluster, add `clusters/<id>/cluster.yaml` listing the member guests, with its password
   as a protected store entry. Trust the code, let the guests install, and trust their
   certificates: one run then forms it on the primary, joins the others, and verifies each.

Each step is a separate proposal. A failure in one guest or instance blocks only
what depends on it.

## Compared with `nodes`

| `nodes` today | Proposed |
|---|---|
| `homelab/nodes/<name>/node.json`, with an OpenTofu root per node | `labs/<lab>/proxmox/<installation>/guests/<id>/guest.yaml` and one shared provisioning recipe |
| `homelab/infrastructure/proxmox/hosts/<host>.tfvars` and `credentials/<host>.env` | `labs/<lab>/proxmox/<installation>/installation.yaml`, with a store reference for the token |
| `homelab/templates/<role>/` (VM defaults and NixOS modules) | `templates/` for base images and `profiles/` for roles; sizes written into each `guest.yaml` |
| `homelab/services/<node>_<service>/service.json`, one per instance | `guests/<id>/services/<service>/`, one standalone folder per instance |
| Infisical paths in `service.json` | `{ store: <key> }` references; the provider is server configuration |
| `dns.records` in `service.json` and `config/dns/providers.json` (Pi-hole or Technitium) | `dns` in each `service.yaml`; integrations and zones in `lab.yaml` |
| A reverse proxy on each apps machine, with Traefik labels in each Compose file | The same, not managed by Octopushy for now |
| Service firewall declarations merged into the node's NixOS configuration | `ports` in each `service.yaml`, applied in the guest's operating system by a step of its own |
| `homelab/clusters/technitium/<cluster>/cluster.json` with `primary`, `secondaries`, `removeSecondaries` | `labs/<lab>/clusters/<id>/cluster.yaml` with member guests and roles; removing a member is a delete |
| `homelab/archived/` | `lifecycle: retired`, then deletion after a verified removal |

Adopting existing `nodes` guests, including state handover and VMID bindings, is a
separate decision (case C1).

## Open questions

- How is the ports step implemented: what does the NixOS recipe receive, how does
  it rebuild from the applied revision, and how are source limits written? This
  still needs checking.
