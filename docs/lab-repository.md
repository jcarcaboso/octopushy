# Lab repository: layout and definitions

**Proposed October 16, 2026; updated October 20, 2026.** Status: a working
proposal, not an implemented parser or an approved public format. It applies the
[architecture agreement](architecture-agreement.md) to the Git repository that
defines labs, their Proxmox installations and hosts, their guests, and the services
on each guest. Examples borrow names from `nodes` for familiarity; this is not a
migration plan for that repository.

**Superseded in part (November 2, 2026).** Octopushy holds nothing service-specific, so a service is now an
ordered list of stages, and [pipelines.md](pipelines.md) is the design: its ports, DNS, clusters and approvals
replace what this page used to say, and the decisions below that it overrides are marked. The rest of the layout stands.

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
- ~~A service declares its own DNS records, because the service does not work if they
  fail. The zones they live in, and the systems serving them, are shared across the
  lab and are defined once on the lab.~~ *Replaced November 2, 2026: DNS is a stage
  and an interface; see [pipelines.md](pipelines.md).*

**Decided (October 19, 2026):**

- ~~Network exposure is declared generically in the service: DNS records, proxy
  routes, firewall ports, and certificates. The lab connects these to whatever
  systems it uses, such as Pi-hole or Technitium for DNS, through integrations.
  Each integration type has an adapter for that product.~~ *Replaced November 2, 2026:
  Octopushy has no adapters. Exposure is whatever a service's stages do.*
- ~~Application clusters are supported, starting with Technitium, on a basis other
  kinds can reuse. Each member is its own service on its own VM, usually on a
  different Proxmox host. Forming the cluster is a separate step: set up one member,
  then join the others to it. The cluster relates its members.~~ *Replaced November 2,
  2026: a cluster is a set of guests with stages applied to them, under one approval.*
- Maintenance windows are optional, and a resource can have several. They are not a
  priority.
- ~~Technitium is the only DNS integration for now, and no other adapters are planned
  yet.~~ *Replaced November 2, 2026: there are no integrations in Octopushy.*
- ~~Firewall ports are opened in each guest's operating system, probably by a step of
  their own; how is still to be worked out.~~ *Replaced November 2, 2026: opening a
  port is a stage of the service that needs it, and a recipe decides how.*
- Each apps machine runs its own reverse proxy. There is no central proxy and none
  is planned, so a zone belongs to one lab. Sharing a zone between labs may come
  later.

**Decided (October 20, 2026):**

- ~~Ports are as permissive as possible: a port is open on every interface and to
  every source unless its declaration limits it. A port can be limited to
  interfaces, to source networks, or both.~~
- ~~How the ports step is implemented still needs checking.~~ *Replaced November 2,
  2026: Octopushy has no ports. A port is an input to a recipe.*

## What the repository holds

**Credential boundary:** each Proxmox installation's credentials go only to the
endpoints an operator [trusted](installation-trust.md) for it. A new installation,
or a new endpoint for one, waits for that trust; other changes run as soon as they
are synced. This is separate from the per-resource apply policy described below,
and cannot be enabled or weakened by a setting in the lab repository.

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
| Octopushy backend (PostgreSQL) | Variable-store values, provider routing, agent enrollments and labels, drafts, runs, attempts, evidence, approvals, the definition snapshot |
| Octopushy backend (PostgreSQL), its built-in state | Infrastructure state |

## Layout

```text
lab-repo/
├── octopushy.yaml                          # repository defaults
├── labs/
│   ├── home/
│   │   ├── lab.yaml                        # policy, windows; later, interfaces
│   │   └── proxmox/
│   │       ├── korriban/                   # a standalone host or a cluster
│   │       │   ├── installation.yaml
│   │       │   └── guests/
│   │       │       └── krb-crt-01/
│   │       │           ├── guest.yaml      # all of the VM's configuration
│   │       │           ├── nixos/          # optional tool-native files for this VM
│   │       │           └── services/
│   │       │               ├── vaultwarden/
│   │       │               │   ├── pipeline.yaml
│   │       │               │   └── compose.yaml
│   │       │               └── arcane/
│   │       │                   ├── pipeline.yaml
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

1. **The folders are the tree.** A lab holds its Proxmox installations, an
   installation its guests, a guest its service instances. Each resource has one folder; the folder name is its id, unique among
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
5. **Everything outside the tree is a reference:** templates, profiles, and recipes.
   Inside a lab, an address may leave out the lab, as in
   `korriban/krb-crt-01/vaultwarden`. The
   dashboard's graph shows the tree and these references together, and search finds
   any resource by name, address, or reference.
6. **Services are standalone.** A service folder belongs to one guest and holds
   that instance's `pipeline.yaml` and any files of its own. The same application on
   two guests is two independent folders, each with its own status, approval and
   failures. The code they run is shared, as a recipe: rule 8 allows it, and copying a
   playbook into every folder would mean a fix has to be made many times.
7. **Settings flow down the tree.** The approval and destructive settings,
   maintenance windows, and agent labels set on a lab apply to everything below
   it; an installation, guest, or service can override them, and the nearest
   setting wins. A service with no `policy` takes what its guest resolves to. Approval
   is per resource: approving a VM change never approves its services, and a service
   gets one approval covering all its stages ([pipelines](pipelines.md#one-approval-first)). VM configuration
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
      folder cancels a removal that has not started changing anything.

    Either way, the history is kept.
    *Decided October 28, 2026: deleting a folder removes the guest. Before, it only
    reported the guest as missing.*
11. **Deletes have their own setting,** `policy.destructive`: `manual` or
    `automatic`. When it is not set, it takes the resource's approval setting, so a
    manual resource never deletes without approval. Setting `destructive: manual` on
    an automatic resource holds back only its deletes. Destructive means deleting a
    guest, data such as disks or volumes, or what a recipe declares `destructive: true`.
    A stage's plan counts deletes and replaces. Recreating a resource (a plan
    that destroys and recreates it) counts as a deployment and follows the approval
    setting; the preview still shows it as a recreate. Octopushy decides what a
    change does from the preview, such as an OpenTofu plan's delete and replace
    actions, not from the edit. Retiring a guest removes its services with it: the
    guest's own destroy is what removes them, and nothing else does yet
    ([removal](pipelines.md#removal)).

    **A VM is the exception, because recreating one wipes its disks.** A provision
    plan that replaces or deletes a Proxmox VM waits for approval unless
    `destructive: automatic` is written down somewhere: inheriting it from an
    automatic approval is not enough. Two things happen without a person:
    - With `destructive: automatic` set explicitly, the plan goes ahead, as a retire
      would.
    - A VM that never finished its first apply recovers on its own, even with
      `destructive: manual`. OpenTofu must be replacing it because it is tainted,
      that must be the only VM the plan removes, and Octopushy must never have
      applied or verified this guest. Otherwise it waits like any other VM, and the
      `approval` setting still applies.

    Once approved, Octopushy lifts the protection of the guest's own VM right before
    applying that plan, as a retire does, and the apply recreates the VM protected.
    It does so only when the plan's state names the guest's VMID on its host. A plan
    that keeps the VM never touches protection, and a preview never does. If the
    apply does not finish, for instance a failure, a cancelled run, or a closed
    uncertain outcome, the run puts protection back before it ends and says so, and
    planning again retries.
    *Decided November 1, 2026: before this, a replaced VM stopped at Proxmox's
    protection flag, whatever was approved, and someone had to lift it by hand.*
12. **Identity is stable.** A guest's address maps to its installation and VMID.
    Changing the VMID of an existing guest is refused as a possible new
    incarnation, and a new guest cannot claim a VMID that is already bound.
13. **Validation:** each kind has one JSON Schema, shared by the backend and the
    editor. The backend also checks that every reference resolves, VMIDs are unique
    per installation, and every store key enters the reference index.
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
in setup definitions and pipeline stages. A reference contains only `store` and the
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
   `connection` is part of what a guest's plan is built from,
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
```

Holds repository-wide defaults. OpenTofu state needs nothing here: Octopushy keeps every
guest's state ([how it works](opentofu-state.md)). Server settings (provider routing, agents, sign-in) stay out of Git. The
provisioning recipe file, and template images are the shapes
[milestone 1](https://github.com/jcarcaboso/octopushy/blob/1446f1b6e7866992df64306d4b0b23d08edaae0e/docs/milestone-1.md#definitions) implements.

### Lab

```yaml
# labs/home/lab.yaml
schemaVersion: 1
name: Home
timezone: UTC                     # for guest schedules and maintenance windows
execution:
  agentLabels: [home]             # agents that may act on this lab's resources
```

A lab groups installations. It carries no state or lock of its own. Agents get
their labels on the server when they enroll, so an agent cannot grant them to
itself. `integrations` and `dns` are accepted and do nothing. **Later,**
[interfaces](pipelines.md#interfaces) replace them: a named slot bound to a recipe.

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

One `token` reference is sufficient. Alternatively, omit it and configure both
`readOnlyToken` and `privilegedToken` with distinct store keys; mixed modes are
invalid. Shared mode deliberately gives planning the same token as apply, so
approval does not make planning read-only. See [credentials](proxmox-credentials.md)
for routing, approval, and the optional split mode's provider
refresh blocker; these examples do not certify live ACL sufficiency.

### Guest

```yaml
# labs/home/proxmox/korriban/guests/krb-crt-01/guest.yaml
schemaVersion: 1
type: vm                          # Proxmox containers later
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
setup:
  recipe: machine-tools           # a recipe with operation: configure
  inputs: { machine_tools_channel: stable }
  secrets:
    machine_tools_token: { store: machines/krb-crt-01/tools-token }
  schedules:                      # actions of the recipe, in cron, in the lab's timezone
    - { action: update-tools, cron: "0 6 * * *" }
access:
  address: krb-crt-01.home.arpa   # by default the static or reserved address
  user: ops
  hostKey: { store: access/krb-crt-01/host-key }  # read through Proxmox, never scanned
  # sshAuthorizedKeys are for people. Octopushy logs in with its own key for this guest. Only a
  # machine it did not create names one: sshKey: { store: access/deploy/ssh-key }
policy:
  approval: automatic             # changes, including recreating the VM, run on their own
  destructive: manual             # deleting this VM or its disks waits for approval
```

Octopushy itself reads the placement and identity. `hardware` and `network` are
passed to the provisioning recipe as inputs and validated against the recipe's
schema, so Octopushy does not need a VM model of its own. The recipe also receives
the host's settings and the installation's connection, so one shared OpenTofu module
provisions every guest. Each guest has its own state, so guests are planned, locked,
and applied independently. Octopushy keeps it; see [OpenTofu state](opentofu-state.md). `nodes` does the same today by
having each guest's OpenTofu root read `node.json`; here the executor passes the
values instead.

`setup.recipe` names a recipe with `operation: configure`, which runs Ansible on the
guest over SSH once it is provisioned; see [configuring a guest](guest-configuration.md).

### Service instance

```yaml
# labs/home/proxmox/korriban/guests/krb-crt-01/services/vaultwarden/pipeline.yaml
schemaVersion: 1
stages:
  - { name: expose, recipe: guest-firewall }
  - name: deploy
    recipe: compose
    secrets:
      database_url: { store: services/vaultwarden/database-url }
inputs: { port: 8000 }
```

A service is an ordered list of stages, each a recipe. Each service folder is one
deployment instance, such as `home/korriban/krb-crt-01/vaultwarden`, with its own
status, approval, and failure domain. Octopushy holds nothing about what it is: ports,
DNS, routes and verification are stages, and what they mean is the recipes'.
[Pipelines](pipelines.md) is the design: stages, the one approval, readiness, retry,
and what is not built yet.

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
3. Add `services/<name>/pipeline.yaml` under the guest. Once the guest's provision and
   setup are done, Octopushy plans and previews every stage, applies its policy to
   them once, then applies them in order.

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
| `dns.records` in `service.json` and `config/dns/providers.json` (Pi-hole or Technitium) | A stage bound to an interface; the recipe behind it in `lab.yaml` (later) |
| A reverse proxy on each apps machine, with Traefik labels in each Compose file | The same, not managed by Octopushy for now |
| Service firewall declarations merged into the node's NixOS configuration | A stage of the service, with the port as an input |
| `homelab/clusters/technitium/<cluster>/cluster.json` with `primary`, `secondaries`, `removeSecondaries` | `labs/<lab>/clusters/<id>/cluster.yaml`: a set of guests with stages applied to them (later) |
| `homelab/archived/` | `lifecycle: retired`, then deletion after a verified removal |

Adopting existing `nodes` guests, including state handover and VMID bindings, is a
separate decision (case C1).

## Open questions

