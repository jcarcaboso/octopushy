# Workflows and the lab repository

**Decided November 3, 2026.** This page is the format of the lab repository and of the workflows its guests and services
name. It replaces the earlier lab repository proposal and the pipelines design, which described recipes: a recipe was one
stage, named by a guest's `provision` and `setup` and by a service's `pipeline.yaml`. A **workflow** is everything needed
to manage one kind of thing from start to end, with one version for all of it. The plan behind it is
[workflows-migration.md](workflows-migration.md).

The owner's rule of November 2 still holds: **Octopushy provides the infra to define the lab and run the operations defined.
It holds nothing service-specific.** No code in `backend/src`, `agent/crates`, `contracts` or `frontend/src` names a service or
a kind of service. What a service is, and what is on a VM, lives in the repository, as workflows.

**What exists today:** workflows come either from a separate workflows repository, released by tags, so the lab repository
holds only data ([workflows from their own repository](#workflows-from-their-own-repository)), or, mainly for development and
tests, from the lab repository itself, in `workflows/` ("local workflows"). The offline validator reads both the way the server
does. Anything marked **Later** is the agreed design, not built. Its fields are refused, not ignored.

## What the repository holds

**Credential boundary:** each Proxmox installation's credentials go only to the endpoints an operator
[trusted](installation-trust.md) for it. A new installation, or a new endpoint for one, waits for that trust; other changes
run as soon as they are synced. This is separate from the per-resource apply policy described below, and cannot be enabled
or weakened by a setting in the lab repository.

The lab repository is the desired configuration Octopushy reads. It is separate from Octopushy's own source code and holds
two kinds of files:

- **Definitions:** Octopushy's own YAML files. They name resources, connect them to each other and to workflows, and set
  policy. Octopushy's forms edit only these.
- **Tool-native files:** OpenTofu, Ansible and anything else a workflow runs. Octopushy references and runs them but does
  not own them (section 5 of the [architecture agreement](architecture-agreement.md)).

| Where | What |
|---|---|
| Lab repository | Definitions, workflows, shared modules, policies, variable-store key references |
| Octopushy backend (PostgreSQL) | Variable-store values, provider routing, agent enrollments and labels, drafts, runs, attempts, evidence, approvals, the definition snapshot |
| Octopushy backend (PostgreSQL), its built-in state | Infrastructure state ([OpenTofu state](opentofu-state.md)) |

## Layout

```text
lab-repo/
├── octopushy.yaml                          # repository settings and defaults
├── labs/
│   └── home/
│       ├── lab.yaml                        # policy, windows, time zone, agent labels
│       └── proxmox/
│           └── korriban/                   # a standalone host or a cluster
│               ├── installation.yaml
│               └── guests/
│                   └── krb-app-01/
│                       ├── guest.yaml      # all of the VM's configuration, and the workflow it uses
│                       ├── motd            # optional files for this VM, read through guest_directory
│                       └── services/
│                           └── web/
│                               ├── service.yaml
│                               └── site.conf
├── templates/fedora-44/template.yaml       # base images: data, an image URL and its checksum
├── workflows/                              # local workflows only; otherwise in the workflows repository
│   ├── machine-tools/
│   │   ├── workflow.yaml                   # version, stages, actions, outcomes
│   │   └── inputs.schema.json              # its inputs, which are also its form
│   └── web/…
└── modules/                                # shared tool-native code, used by workflows; likewise
    ├── opentofu/proxmox-vm/
    └── ansible/machine-tools/
```

With a [workflows repository](#workflows-from-their-own-repository), `workflows/` and `modules/` live there, with the same
layout, and the lab repository has neither.

## Rules

1. **The folders are the tree.** A lab holds its Proxmox installations, an installation its guests, a guest its services.
   Each resource has one folder; the folder name is its id, unique among its siblings. Its address is the path of ids, such
   as `home/korriban/krb-app-01/web`. One repository can hold several labs.
2. **Guests sit under the installation, not the host.** In a cluster, VMIDs are shared and a VM can migrate between hosts,
   so the host is a field in `guest.yaml`. Moving a VM within a cluster changes that field, not its folder. For a
   standalone installation, the installation is the host.
3. **A guest's folder holds all of its configuration:** `guest.yaml` with placement, hardware, network, its workflow and
   that workflow's inputs, access, and policy, plus files of its own and its services. Reading that one file tells
   everything about the VM.
4. **Moving a folder creates a new resource.** Moving a guest to another installation means a new VMID; moving a service to
   another guest means a new instance whose data has to move too. Octopushy treats the new folder as a new resource and the
   old address as missing. A rename in place is declared with `renamedFrom: <old id>`.
5. **Everything outside the tree is a reference:** templates and workflows. The dashboard shows the tree and these
   references together, and search finds any resource by name, address, or reference.
6. **Services are standalone.** A service folder belongs to one guest and holds that instance's `service.yaml` and any files
   of its own. The same application on two guests is two independent folders, each with its own status, approval and
   failures. The code they run is shared, as a workflow.
7. **Settings flow down the tree.** The approval and destructive settings, maintenance windows, and agent labels set on a
   lab apply to everything below it; an installation, guest, or service can override them, and the nearest setting wins. A
   service with no `policy` takes what its guest resolves to. Approval is per resource: approving a VM change never approves
   its services, and a service gets [one approval covering all its stages](#one-approval-first). VM configuration does not
   inherit (rule 3).
8. **Tool-native files** sit in a workflow's folder or under `modules/` when shared. A guest's or service's own files sit in
   its folder. Paths in a workflow are relative to its `workflow.yaml`, and [are checked](#paths).
9. **Secrets are references.** A definition writes non-secret values directly and refers to variable-store entries as
   `{ store: <key> }`, optionally with an exact version as `{ store: <key>, version: 2 }`. It never contains a secret value
   or names a secret provider.
10. **Removal is reviewed.** There are two ways to remove a guest:
    - Set `lifecycle: retired`. This keeps the definition in Git while the guest is removed.
    - **Delete the guest's folder.** Octopushy reports the guest as missing and proposes a retire run. That run is built
      from the revision the guest was last applied from, so it destroys what that definition created. It follows that
      definition's approval and `destructive` policy. **Plan removal** on the guest page proposes it again after it was
      cancelled. Restoring the folder cancels a removal that has not started changing anything.

    Either way, the history is kept. *Decided October 28, 2026.*
11. **Deletes have their own setting,** `policy.destructive`: `manual` or `automatic`. When it is not set, it takes the
    resource's approval setting, so a manual resource never deletes without approval. Setting `destructive: manual` on an
    automatic resource holds back only its deletes. Destructive means deleting a guest, data such as disks or volumes, or a
    playbook the workflow declares `destructive: true`. Recreating a resource counts as a deployment and follows the
    approval setting; the preview still shows it as a recreate. Octopushy decides what a change does from the preview, such
    as an OpenTofu plan's delete and replace actions, not from the edit. Retiring a guest removes its services with it.

    **A VM is the exception, because recreating one wipes its disks.** A provision plan that replaces or deletes a Proxmox
    VM waits for approval unless `destructive: automatic` is written down somewhere: inheriting it from an automatic approval
    is not enough. Two things happen without a person:
    - With `destructive: automatic` set explicitly, the plan goes ahead, as a retire would.
    - A VM that never finished its first apply recovers on its own, even with `destructive: manual`. OpenTofu must be
      replacing it because it is tainted, that must be the only VM the plan removes, and Octopushy must never have applied
      or verified this guest. Otherwise it waits like any other VM, and the `approval` setting still applies.

    Once approved, Octopushy lifts the protection of the guest's own VM right before applying that plan, as a retire does,
    and the apply recreates the VM protected. It does so only when the plan's state names the guest's VMID on its host. A
    plan that keeps the VM never touches protection, and a preview never does. If the apply does not finish, the run puts
    protection back before it ends and says so, and planning again retries. A plan that deletes or replaces a VM other than
    the guest's own is refused: see the [safety boundary](safety-boundary.md). *Decided November 1, 2026.*
12. **Identity is stable.** A guest's address maps to its installation and VMID. Changing the VMID of an existing guest is
    refused as a possible new incarnation, and a new guest cannot claim a VMID that is already bound. A guest whose `vmid`
    is an observed VM Octopushy does not manage is invalid.
13. **Validation:** each kind has one JSON Schema in `contracts/schemas`, shared by the backend, the editor and the
    [validator](#validating-offline). The backend also checks that every reference resolves, VMIDs are unique per
    installation, and every store key enters the reference index.
14. **YAML, with a version in each file.** Every file starts with `schemaVersion`. YAML allows comments, and OpenTofu can
    read it with `yamldecode`. Form edits must keep comments and ordering.

## Workflows

A workflow is a folder, `workflows/<id>/`, with a `workflow.yaml` and the files it uses. It names what it manages, its
version, its inputs, and its stages: each stage is one executor run from one root. Its code may sit in its folder or be
shared under `modules/`.

### A workflow that manages a VM

```yaml
# workflows/machine-tools/workflow.yaml
schemaVersion: 1
version: "1.0"                    # M.m, quoted
manages: vm                       # vm | service; containers come later
description: A Fedora VM, set up by Ansible with a set of tools.
inputs: inputs.schema.json        # validates { hardware, network, inputs } from the guest
ui: {}                            # optional hints for the form; see the form: ui hints
secrets: [machine_tools_token]    # what a guest has to provide in secrets or secretFiles
stages:
  - name: machine                 # the OpenTofu stage that makes the VM: always first
    executor: opentofu
    root: ../../modules/opentofu/proxmox-vm
    verify: proxmox               # the default; none skips verification through Proxmox
  - name: setup                   # at most one stage more: the Ansible stage that sets the VM up
    executor: ansible
    root: ../../modules/ansible/machine-tools
    playbook: configure.yml       # the action named setup, run whenever what it is built from changes
    prepare: prepare.yml          # runs on the agent, with no secrets, before approval
    timeoutMinutes: 180
    # destructive: false          # the default
actions:                          # what else can be run from the Ansible stage
  update-tools:
    stage: setup
    playbook: update-tools.yml
    prepare: prepare.yml
    destructive: false            # required: only the workflow can say
  update-system:
    stage: setup
    playbook: update-system.yml
    snapshot: applied             # what the last successful run prepared
    destructive: false
outcomes:
  deferred:                       # reported by a playbook with set_stats
    description: Work is active on the machine. Approving interrupts it.
    approve:
      vars: { machine_tools_allow_busy: true }
```

For `manages: vm`:

- **The first stage is the OpenTofu stage that makes the machine.** Provision and retire runs plan and apply it; `verify`
  belongs on it only.
- **At most one stage follows, and it is an Ansible stage: the guest's setup.** Its `playbook` is the action named
  `setup`, which runs whenever the guest's inputs, secrets, access, the workflow, or the guest's files change. It is
  `destructive: false` unless the stage says otherwise. No declared action may be called `setup`.
- **`actions`** are the other playbooks of that stage, run by hand or on the guest's schedules. Each names its `stage`.
- **`secrets` and `outcomes`** need the Ansible stage. A workflow without one has neither.

### A workflow that manages a service

```yaml
# workflows/web/workflow.yaml
schemaVersion: 1
version: "1.2"
manages: service
inputs: inputs.schema.json        # validates { inputs } from the service
secrets: [web_password]
stages:
  - { name: expose, executor: opentofu, root: ../../modules/opentofu/web-rules }
  - { name: deploy, executor: ansible, root: ../../modules/ansible/web, playbook: deploy.yml }
```

A service workflow has one stage or more, planned and previewed, approved once, then applied in order: see
[services and their runs](#services-and-their-runs). An Ansible stage runs on the guest's connection, and its action is
named after the stage. An OpenTofu stage has a state of its own, its inputs as variables, and no credentials of any kind.
A service offers Deploy only, so a service workflow has no `actions`, and an outcome a person approves is refused: that
would be a second approval, after something has changed.

### Versions and references

**Decided by the owner on November 3, 2026: a version belongs to a whole workflow, and is `M.m`.**

- **A major version (`M`) is a breaking change:** an input that is removed, renamed, newly required or narrowed, or a change
  that would replace existing machines.
- **A minor version (`m`) does not break the flow.**
- **`version` is a quoted string.** Unquoted, YAML reads `2.10` as the number 2.1, so a number is refused with a message
  saying to quote it.

A guest or service names a workflow and a version:

| Reference | Resolves to |
|---|---|
| `fedora-vm@2` | the newest `2.x` |
| `fedora-vm@2.3` | exactly `2.3` |
| `fedora-vm` | invalid: a major version is never adopted by accident |

**In a workflows repository, a version is a release tag,** `fedora-vm/v2.3`: `@2` takes the newest usable `2.x` release and
`@2.3` exactly that one. See [workflows from their own repository](#workflows-from-their-own-repository).

**In this repository's `workflows/`, each workflow has one version,** the one its `workflow.yaml` declares. `@2` matches it
when its major is 2, and `@2.3` only when it is exactly `2.3`. Otherwise the guest or service is invalid, and says what is
there: `fedora-vm@3 not found; available 2.3.` A reference to a workflow whose `workflow.yaml` cannot be read says so, and so
does one to a workflow that manages something else.

What a reference resolved to is part of the loaded definition and of every hash below. Pages show it beside what was
written: a guest on `fedora-vm@2` shows `fedora-vm@2.3`. Runs and approval documents name the workflow at its version and
the commit its files came from. Resolution sits behind one interface, `IWorkflowSource`, in `Octopushy.Definitions`:
`LocalWorkflowSource` for a folder, `GitTagWorkflowSource` for a workflows repository, and nothing that reads definitions
knows which.

## Workflows from their own repository

```yaml
# octopushy.yaml of the lab repository
schemaVersion: 1
workflows:
  repository: git@github.com:jcarcaboso/octopushy-lab.git   # https://, ssh://, user@host:path, file:// or an absolute path
  # credential: { store: git/octopushy-lab }                 # optional
```

The workflows repository holds `workflows/<id>/` and `modules/`, laid out as above. The lab repository then holds data only:
**a lab repository that names a workflows repository and also has `workflows/` or `modules/` is an invalid revision**,
"Workflows come from &lt;repository&gt;; remove workflows/ and modules/ here." What a run executes is never a mix of two
repositories.

**Credential.** `credential` names a store key: an HTTPS token for an `https://` repository, or an SSH private key for an SSH
one. Without it, the lab repository's own credential (`system/git/credential`, set under **Repository**) is used when both
repositories are reached the same way, HTTPS or SSH; otherwise none. A path or `file://` needs none.

### Releases are tags, and a tag never moves

Octopushy keeps a second bare mirror next to the lab repository's, one per workflows repository, and fetches every branch's
objects and **every tag** on the same poll as the lab repository's branch, and on **Repository → Sync now**. A failed fetch is
shown on the Repository page; the releases already recorded keep resolving, from the mirror.

- **A release is a tag `<id>/v<M>.<m>`,** such as `fedora-vm/v2.3`: `M` and `m` are non-negative integers with no leading
  zeros (`0` itself is fine). Any other tag is ignored.
- **Its commit is the one the tag pointed to the first time Octopushy saw it,** recorded with when (`workflow_releases`). That
  commit is the release for good, and Octopushy keeps it in the mirror under a ref of its own.
- **At that commit, `workflows/<id>/workflow.yaml` must exist and declare the tag's `version`.** Otherwise the release is
  invalid: it is never resolved to, and the Repository page says why.
- **A tag that later points to another commit is not followed.** The first commit stays the only one used, and the Repository
  page says "fedora-vm/v2.3 was moved from &lt;a&gt; to &lt;b&gt;; the original commit is used. Moved tags are not followed."
  Release a new version instead.
- **A deleted tag stays a release** while its commit is in the mirror, which Octopushy's own ref sees to. If the commit is
  gone, as after the mirror was lost, a reference that resolves to it is invalid and says so.

### Resolution and revisions

For each reference in a lab repository's commit, `@M` takes the highest minor among the usable releases of major `M`, and
`@M.m` takes exactly that release; an invalid release named exactly says why it cannot be used. Each reference resolves to
`(workflow, version, commit)`, and the workflow is read from that commit's files: its folder and `modules/` there, checked
with the [path rules](#paths) as a local workflow is.

**A revision is the lab repository's commit together with what its references resolved to.** Its identity is the source, the
commit and the digest of those resolutions. So:

- **A new tag that changes a resolution is a new revision,** even with no new commit in the lab repository, and it is applied
  like a new commit: hashes are compared and runs proposed under each resource's policy. That is how a new minor release
  reaches every guest and service on `@M`. One pinned to `@M.m` resolves as before, so nothing is proposed for it.
- **A new major is offered, not applied.** The Repository page shows "fedora-vm 3.0 available, breaking" beside each guest
  and service on an older major. Upgrading is a change to the reference.
- **A revision is always read again with the resolutions it recorded,** never with newer releases: a run waiting for approval
  is built from exactly what it was proposed from.
- Head selection, the revision cache, bundles, pruning, runs, approval documents and the editor all use this identity. The
  editor validates a draft and its base with the releases recorded now, as a sync would read them, including a draft that
  changes a version.

**Hashes** are computed from the resolved commit's files. They cover the workflow's id, version and files, not its commit, so
the same files at the same version from another commit change nothing.

### What a run gets

Each attempt's bundle is the lab repository's files at the run's commit, with the workflows repository's `workflows/` and
`modules/` at the resolved workflow's commit, merged at the bundle root. Paths stay what they are, so a stage's `root` and a
guest's directory read the same, and agents need no change. Bundles are cached per pair of commits. Every run records the
workflow, its version and its commit, and the run page shows them.

### Paths

A workflow can only run what its hashes cover, so every path it names is checked:

- **`inputs`** is a file inside the workflow's own folder.
- **A stage's `root`** is inside the workflow's folder or under `modules/`. A path that leaves both with `..`, an absolute
  path, or one that goes through a symbolic link is refused. A root inside another workflow's folder is refused too.
- **Every path must exist.** A `playbook` or `prepare`, of a stage or an action, must be a file inside its stage's root.
- **An OpenTofu root** holds top-level `.tf` or `.tf.json` files and declares no `backend`: Octopushy keeps the state.

An error in a workflow makes the workflow invalid, and with it every guest and service that resolves to it. Their pages show
the workflow's errors with their own.

`snapshot: applied` is only on actions. A stage's playbook runs whenever something changes, and a new machine has no
snapshot to reuse.

### Inputs, the form and secrets

- **A guest's inputs** are `hardware`, `network` and `inputs` from `guest.yaml`. The workflow's `inputs` schema validates
  the object `{ hardware, network, inputs }`. `hardware` and `network` reach the OpenTofu stage as `guest.hardware` and
  `guest.network`, as before, and `inputs` reaches every stage: as the OpenTofu variable `inputs`, and as extra vars to the
  Ansible stage. OpenTofu warns about a variable a root does not declare, and goes on.
- **A service's inputs** are `inputs` from `service.yaml`, validated as `{ inputs }`. Every stage gets them: an OpenTofu stage
  as its variables, an Ansible stage as extra vars. There are no per-stage inputs.
- **The schema is also the form.** Forms for guests and services are rendered from it, with the optional `ui` hints, so a
  new field in a workflow appears in the UI without an engine change: see [the form](#the-form-ui-hints) and
  [changes from the UI](changes-from-the-ui.md). The server stores and serves `ui`; only the form reads it, except that a
  deploy generates a secret only where `ui.secrets` asks for that generator ([generated values](#generated-values)).
- **Secrets** are extra vars filled from the variable store. A workflow lists the ones a guest or service must provide in
  `secrets`; the guest or service provides each in `secrets` (the value) or `secretFiles` (the path of a private file
  holding it). Only the steps that run a playbook receive them, never a preview or an OpenTofu stage. A Proxmox token is
  never one, and a name may be set only once across `inputs`, `secrets` and `secretFiles`.
- **Extra vars** whose names start with `ansible_` or `octopushy` are refused: they are how Octopushy pins a connection.
  `{{ octopushy.guest_directory }}` is the guest's folder, or for a service stage the service's.

### The form: `ui` hints

A guest's form is its workflow's inputs schema, rendered as fields over `{ hardware, network, inputs }`; a service's is
`{ inputs }`. The form renders:

- objects with `properties`, nested to any depth, and arrays whose `items` is one schema, of values or of objects, with
  add and remove;
- `string`, `integer`, `number` and `boolean`, `enum` as a choice (an array of `enum` items as checkboxes), and `const`;
- local `$ref`s into `$defs` or `definitions`, with the keywords next to a `$ref` applying too;
- `title` and `description` as label and help, `required` as a mark, `default` for a new item, `minimum` and `maximum`,
  and `pattern` and `format` as hints. The server stays authoritative: it validates every change.

Keywords that only constrain values, such as `if`/`then`/`else`, `allOf`, `not`, `dependentSchemas`, `dependentRequired`,
`contains` and `uniqueItems`, are left to the server, and what it finds is shown next to the field. **Anything the form does
not render**, such as `oneOf`, `anyOf`, a list of types, `patternProperties`, or an object with no `properties`, is edited as
YAML for that part of the value, and keys of an object that its schema does not name are kept in a YAML box of their own.
Nothing is silently dropped.

`ui` in `workflow.yaml` is optional and small. Every hint is optional, and unknown ones are ignored:

```yaml
ui:
  order: [hardware, network, inputs]          # the form's sections, top to bottom
  fields:                                     # hints per field, by dotted path; [] is an array's items
    hardware.memory: { label: Memory, help: In MiB. }
    hardware.disks[].size: { label: Size, help: In GiB. }
    inputs: { order: [worker_server_url, worker_agent_version] }
    inputs.worker_server_url: { group: Server }
    inputs.worker_agent_digest: { group: Server }
    inputs.machine_t3_settings: { widget: yaml }
```

| Hint | For | What it does |
|---|---|---|
| `label` | any field | Shown instead of the property's name or the schema's `title` |
| `help` | any field | Shown under it instead of the schema's `description` |
| `order` | an object | Its properties in this order; those not listed follow in the schema's order |
| `group` | any field | Fields of one object with the same group are shown together, under the group's name |
| `widget` | any field | `text`, `textarea`, `select`, `radio`, `checkboxes`, `yaml` (edit it as YAML) or `hidden` (not shown, value kept) |

### Generated values

The [workflow wizard](changes-from-the-ui.md#the-workflow-wizard) deploys a new guest or service from a workflow. A workflow
asks it for generated values with more `ui` hints. Each one is generic: nothing in the engine names a workflow.

```yaml
ui:
  vmid: { generate: next-vmid }                  # propose the next free VMID; from: 200 starts looking there
  template: fedora-44                            # the template a new guest starts from
  access: { user: ops }                          # the login the setup stage uses on a new guest
  secrets:
    worker_enrollment_token:
      generate: agent-enrollment-token           # a one-time enrollment token, made at deploy
      labels: inputs.worker_agent_labels         # the token's labels, read from this field of the form
      name: inputs.worker_agent_name             # the agent's name, read from this field; the guest's name otherwise
      help: Made at deploy and stored for this guest.
    admin_password: { generate: random-password, length: 32 }
```

| Hint | What the wizard does |
|---|---|
| `vmid: { generate: next-vmid, from }` | Proposes the smallest VMID at or above `from` (100 by default; installations have no setting for it) that is free on the installation: not observed anywhere on it, since a cluster shares its VMIDs, not written in any `guest.yaml` under it at the head commit, valid or not, not bound to or reserved by a guest, and not held by a deploy that is not final yet. The person can change it. |
| `template` | Starts the template choice there. |
| `access: { user }` | Starts the setup stage's login there. The host key is a store key the person sets after the first boot. |
| `secrets.<name>.generate` | Offers to generate the secret instead of naming a store key: `agent-enrollment-token` or `random-password`. |
| `secrets.<name>.labels`, `.name` | For `agent-enrollment-token`: dotted paths into the form whose values start the token's labels and name. Without `labels`, the lab's agent labels. |
| `secrets.<name>.length` | For `random-password`: 16 to 128 characters, 32 by default. |
| `secrets.<name>.label`, `.help` | Shown on the secret's choice. |
| `secrets.<name>.requiredWhen: { pointer, value }` | The secret is required when the value at the JSON pointer into `{ hardware, network, inputs }` is `value`, or is an array that holds it, such as `{ pointer: /inputs/machine_features, value: tailscale }`. Otherwise it is optional. The server enforces it. |

**The VMID is checked again when the deploy is committed,** against the branch the push lands on. If it was taken since the
review, nothing is committed and the refusal offers the next free one to review again with: the number is never changed
after the review.

**Free VMIDs are known only from a reliable observation.** Offering a VMID, and committing one, generated or typed, needs the
installation's latest successful observation to be:

- **recent:** no older than `Octopushy__Deploy__ObservationMaxAgeMinutes`, by default three observation intervals
  (`Octopushy__Scheduling__ObserveMinutes`, 5), so 15 minutes;
- **newer than any failed observation;**
- **complete:** every host it lists online, every host `installation.yaml` declares listed, and a cluster quorate.

Otherwise nothing is offered or committed. The reason names the installation and when it was last observed, such as *"home/
pve01 has not been observed successfully since 2026-11-04 08:00 UTC, more than 15 minutes ago. Refresh its observation
first."*, and the wizard offers **Observe now**. A `guest.yaml` under the installation that is not YAML at all refuses too:
the VMID it holds is unknown. A guest that is YAML but fails validation still holds the VMID it names.

**A generated secret** is made when the deploy is committed, never at the review:

- `agent-enrollment-token` creates a token as **Agents** does, with the labels and name shown in the review. It lives for
  `Octopushy__AgentChannel__GeneratedEnrollmentTokenMinutes`, three days by default and a week at most, so the guest can wait
  for an approval, be provisioned and be set up. A token that expired before setup enrolled is made again under **Agents**,
  and its new value set in the same key.
- `random-password` is 32 random URL-safe characters, or `length`.

Each goes into the variable store as a secret, under `guests/<lab>/<installation>/<guest>/<name>`, or
`services/<address>/<name>` for a service: the address keeps two guests of one name on two installations apart. The new
definition references it, `{ store: … }`. The value is never shown, returned by the API, logged, or written to Git. A key that
is already in the store refuses the deploy rather than replacing it. Each value records what it wrote: the store version and
its token. Once its change is final and nothing at the branch head references the key, cleanup removes exactly that version,
while it is still the one present, and revokes the token unless it was used. A removal that fails is retried at every sync
([how generated values are removed](changes-from-the-ui.md#the-workflow-wizard)).

The server generates a value only where the workflow's `ui.secrets` asks for that generator for that secret; any other secret
names an existing store key. The managed SSH key a guest's setup logs in with is not a generated value here: Octopushy makes
it for every guest that needs one ([guest configuration](guest-configuration.md)).

### How a workflow runs

The run model has not changed. A workflow is read into the runs Octopushy already knows:

| Workflow | Run | What it does |
|---|---|---|
| `manages: vm`, the machine stage | provision, or retire for a retired or deleted guest | plan, approval, apply of exactly that plan, verify |
| `manages: vm`, the setup stage | configure: the `setup` action, or a declared action | preview, approval, playbook, plus the outcome approvals declared |
| `manages: service`, all stages | pipeline | a plan or preview of every stage, one approval, then every stage applied in order |

A guest has a provision run, then a setup run: a setup preview cannot describe a machine that does not exist yet, so the two
are not under one approval. [Configuring a guest](guest-configuration.md) has what a configure run does.

### Hashes

Each run has an inputs hash, and Octopushy proposes one automatic run per hash. Every hash covers the workflow the reference
resolved to: **its id and version, every file in its folder, and every file of the roots that run uses.** What was written
is not hashed, only what it resolved to, so `@2` and `@2.3` resolving to the same version are the same.

- **Provision:** the guest's definition without its description, policy, execution, secrets, secret files, schedules,
  `renamedFrom` and the access fields only Ansible uses; its host and installation settings; its template; the workflow; and
  the machine stage's root. Inputs are part of it, since OpenTofu gets them.
- **Configure:** the guest's inputs, secrets, secret files and access; the workflow; the setup stage's root; and the guest's
  own files outside `guest.yaml` and `services/`. Hardware and network are not part of it, and neither is a schedule, which
  says when an action runs, not what it does.
- **Service:** `service.yaml` without its description, policy and execution; the service's own files; how its guest is
  reached; the workflow; and the roots of all its stages.

Because a root must be inside the workflow's folder or under `modules/`, a change to a module a workflow uses changes its
runs' hashes, and a change to a module it does not use changes nothing.

## Definitions

### Variable-store references

Anywhere a store reference is accepted, use `{ store: <key> }` or `{ store: <key>, version: <positive integer> }`:

```yaml
token: { store: proxmox/korriban/api-token, version: 2 }
tls:
  caCertificate: { store: proxmox/korriban/ca, version: 1 }
```

The same syntax applies to SSH keys and to the secrets of guests and services. A reference contains only `store` and the
optional `version`; neither a quoted version nor zero, a negative number, or a fraction is valid.

For a new run, an unversioned reference pins the active store version before its first attempt; later attempts keep those
pinned versions. An explicit version expresses the desired binding, not proof that the credential has been verified, and
does not promote a candidate. Explicit candidate versions are blocked until promoted. The `system/` scope is reserved and
cannot be referenced by definitions.

**Renaming a key.** A key is the entry's identity: nothing renames or moves one, and moving a key to another folder is the
same as renaming it. The reference is in Git, the stored value is encrypted under the key's name, and run history records
it, so the change is a replacement made in this order:

1. Set the new key. A secret's value cannot be read back, so enter it from its source.
2. Commit the changed references. A new key starts at version 1, so change any `version` too.
3. Review the plans this causes. A reference in a guest, or in an installation's `connection`, is part of what a guest's
   plan is built from, so renaming a Proxmox token plans every guest of that installation again, and a run waiting on the
   old definition is no longer current.
4. Wait until **Used by** no longer lists the old key, which includes runs in progress.
5. Delete the old key. If it has execution history, deleting is refused: revoke its versions instead. The key then stays
   listed.

A reference to a key that is not set is listed under **Referenced but not set** and blocks its run until the key is set, so
a step taken out of order stops the work rather than delivering nothing.

### Repository settings: `octopushy.yaml`

```yaml
schemaVersion: 1
# workflows: { source: local }    # the default: this repository's workflows/
# workflows: { repository: <git url or path>, credential: { store: <key> } }
defaults:
  policy:
    approval: automatic           # automatic | manual
    # destructive: unset, so it follows approval
```

Holds where workflows come from and repository-wide defaults. `workflows.source: local`, which is also what an absent
`workflows` means, is this repository's `workflows/`; `workflows.repository` names a
[workflows repository](#workflows-from-their-own-repository). One or the other, not both. OpenTofu state needs nothing here:
Octopushy keeps every guest's state. Server settings (provider routing, agents, sign-in) stay out of Git.

### Lab

```yaml
# labs/home/lab.yaml
schemaVersion: 1
name: Home
timezone: Europe/Madrid           # for guest schedules and maintenance windows
execution:
  agentLabels: [home]             # agents that may act on this lab's resources
```

A lab groups installations. It carries no state or lock of its own. Agents get their labels on the server when they enroll,
so an agent cannot grant them to itself. `integrations` and `dns` are accepted and do nothing. **Later,**
[interfaces](#interfaces) replace them.

### Proxmox installation and its hosts

```yaml
# labs/home/proxmox/moraband/installation.yaml
schemaVersion: 1
type: standalone                  # standalone | cluster
# manage: false                   # observed only: no guests, no runs (default true)
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

An installation is the management domain: one standalone host, or a cluster whose hosts share a connection and a VMID space.
Hosts are listed in it rather than given folders, because guests are not nested under hosts (rule 2). Each host keeps its
own datastores, bridges, and capacity limits, and is its own failure domain. A cluster lists several hosts and may give each
its own endpoint. Defining an installation does not mean Octopushy manages the cluster, its membership, or its storage.

`manage: false` makes an installation observed only: Octopushy reads its hosts and VMs and changes nothing there. No guest or
service may be defined under it, no run is made for it, and its token goes only to observations. See the
[safety boundary](safety-boundary.md).

One `token` reference is sufficient. Alternatively, omit it and configure both `readOnlyToken` and `privilegedToken` with
distinct store keys; mixed modes are invalid. Shared mode deliberately gives planning the same token as apply, so approval
does not make planning read-only. See [credentials](proxmox-credentials.md).

### Guest

```yaml
# labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml
schemaVersion: 1
type: vm                          # Proxmox containers later
lifecycle: active                 # active | retired
host: korriban
vmid: 310
template: fedora-44
workflow: machine-tools@1         # required, with a version: @M or @M.m
hardware:
  cores: 2
  memory: 4096
  disks:
    - { size: 32 }                # system disk; datastore from the host
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.31/24
  gateway: 10.1.0.1
inputs:                           # given to every stage, validated by the workflow's schema
  machine_tools_channel: stable
secrets:                          # extra var -> store key; only the playbook gets these
  machine_tools_token: { store: machines/krb-app-01/tools-token }
schedules:                        # actions of the workflow, in cron, in the lab's timezone
  - { action: update-tools, cron: "0 6 * * *" }
access:
  user: ops
  hostKey: { store: access/krb-app-01/host-key }  # read through Proxmox, never scanned
  # address defaults to the static or reserved address. Octopushy logs in with its own key for
  # this guest; only a machine it did not create names one: sshKey: { store: access/deploy/ssh-key }
policy:
  approval: automatic             # changes, including recreating the VM, run on their own
  destructive: manual             # deleting this VM or its disks waits for approval
```

Octopushy itself reads the placement and identity. `hardware`, `network` and `inputs` are validated against the workflow's
schema, so Octopushy does not need a VM model of its own. The machine stage also receives the host's settings, the
installation's connection and the template, so one shared OpenTofu module provisions every guest. Each guest has its own
state, so guests are planned, locked, and applied independently.

`secrets`, `secretFiles` and `schedules` need a workflow with a setup stage, and so do `access.user` and `access.hostKey`
for it to log in. `schedules` name the workflow's actions, `setup` included. `profiles` is passed to the machine stage and
not applied otherwise yet.

### Service

```yaml
# labs/home/proxmox/korriban/guests/krb-app-01/services/web/service.yaml
schemaVersion: 1
workflow: web@1                   # a workflow with manages: service
inputs: { port: 8000 }            # given to every stage
secrets:
  web_password: { store: services/web/password }
policy: { approval: manual }
```

Each service folder is one deployment instance, such as `home/korriban/krb-app-01/web`, with its own status, approval, and
failure domain. Octopushy holds nothing about what it is: ports, DNS, routes and verification are stages of its workflow, and
what they mean is the workflow's. `secrets` and `secretFiles` reach only the workflow's Ansible stages, so a workflow with
none refuses them.

### Maintenance windows

```yaml
# in lab.yaml, or the policy of any resource below it
policy:
  windows:
    - { days: [sat, sun], start: "02:00", hours: 6 }
    - { days: [wed], start: "22:00", hours: 2 }
```

- **Windows are optional.** Without one, an automatic change runs as soon as it is ready. A resource can have several
  windows; they flow down like the other settings, and the nearest list wins. Times use the lab's `timezone`.
- **A window only limits when an automatic change may start.** An attempt that has started finishes even after the window
  closes. A change waiting for a window shows when it will run.
- **Approving a manual change runs it at once,** unless the approver chooses to wait for the next window.

### VM template

```yaml
# templates/fedora-44/template.yaml
schemaVersion: 1
os: fedora
source:
  url: https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Cloud/x86_64/images/Fedora-Cloud-Base-Generic-44-1.7.x86_64.qcow2
  checksum: sha256:28680fe5b371a5a82ebf43a31926e086a168e59949d03969c5093e7071f90b7f
availableOn: [home/korriban, home/moraband]
```

A template is a base image, and is data: the image URL and its checksum. `source.build` naming a workflow is accepted with a
warning and cannot be used yet.

## Services and their runs

### One approval, first

Plan and preview every stage, approve once, then apply them in order. **Nothing changes before that approval.** A service's
run is one run, and its steps read as one timeline:

```
expose  plan
deploy  preview
        approval          one document, covering every stage
expose  apply
deploy  playbook
```

The approval is bound to **one document covering every stage**, as `preview.json` is for a configure run. Octopushy writes it
when the last preview ends. It lists each stage with the workflow at its version and the SHA-256 of the saved plan or
preview that stage will apply, and what the plan said. An approval names that document's SHA-256, and each apply is refused
unless the document still has that digest and names exactly the artifact it is about to send. The document is on
`GET /api/runs/{id}`, under the step's `approval.document`.

**The limit: an OpenTofu plan runs provider code before approval, with whatever credentials it holds.** "Nothing changes" is
a property of OpenTofu and of the provider, not something Octopushy can enforce on a plan. A provision plan holds the
read-only token for this reason. An OpenTofu stage of a service holds nothing, so its plan has no token to misuse. An
Ansible preview has no such limit: it gets the public host key only, and never contacts the guest. A `prepare` playbook
runs on the agent, as it does for a guest.

A plan with no changes has nothing to apply: its apply step is skipped, and if no stage has anything to change the run needs
no approval. An Ansible stage always counts as a change, since nothing can show what a playbook will do. A declared outcome
with `result: failed` fails the stage, and `result: succeeded` passes it.

### Policy

**The default is automatic deployment.** With no explicit `policy` on a service, it takes what is configured:
`octopushy.yaml` defaults, lab, installation, guest. A service adds only its own override, and the nearest setting wins.
If nothing is set anywhere, approval is `automatic`.

**Destructive plans keep the [destructive rules](#rules)** (rule 11): an OpenTofu plan with a pure delete, or a stage the
workflow declares `destructive: true`, waits under `destructive: manual`, whatever `approval` says. The VM guard applies to a
guest's provision run and not to a stage. A stage has no such guard, so a stage's plan counts deletes and replaces together,
and a stage that replaces something under `destructive: manual` waits.

### No rollback

A failed stage stops the pipeline. The stages before it stay applied, and nothing undoes them: a later stage's failure says
nothing about an earlier one, and a workflow may not be safely reversible.

**Retrying continues from that stage without a new approval, unless the inputs changed.** Retry is on the failed step, or
`POST /api/runs/{id}/retry-stage`. It is refused, and a new run is needed, when:

- **The inputs changed,** or a newer run exists, or the service went missing or invalid. The approval was of something else.
- **A failed OpenTofu apply moved its stage's state.** OpenTofu will not apply a saved plan to a state that changed, and a new
  plan would be a document nobody approved. So this one takes a new run, which plans what is left. If the apply failed
  without changing the state, the same plan is used again.
- **A change may have been lost.** An uncertain attempt is closed by a person, as for any run, and cannot be repeated. A plan
  or preview that failed changed nothing, so Deploy is the way on.

Deploy on the service's page, or `POST /api/services/{id}/deploy`, starts a new run whatever the last one did.

### Later stages cannot use results of earlier ones

Every plan is made before anything applies, so a stage uses repository inputs only. There are no stage outputs. A value one
stage produces and another needs belongs in the repository, or on the machine for a later stage's playbook to read when it
runs.

### Readiness and turns

**Provision, then setup, then services.** A service waits until its guest is bound, its provisioning has caught up with its
definition, and its setup, if its workflow has a setup stage, has been applied at its current inputs. It shows as *waiting*,
not failed, and says what for.

**This is checked again at dispatch.** A run can wait a long time for its turn or for a person, and the guest can change
meanwhile. So every step that changes something re-reads the guest, both when its attempt is created and when it is handed
to an agent. Until it holds the run stays where it is, its approval stays good, and the run says what it waits for. A change
held at dispatch goes back to its run and leaves the queue, so it cannot crowd out the guest's setup it is waiting for.

**One change at a time on a guest, with its services.** Nothing that changes a guest starts while something else is changing
it. That covers only what changes something: a plan or preview can run beside it, and **a run waiting for approval does not
hold its siblings**. An uncertain run does hold them, until a person closes it, because it may still be running. A guest's
own run does not wait for a service's work that is only queued, since that may itself be waiting for the guest.

**A failed service marks neither its guest nor its siblings.** A guest's setup failing does hold back its services, since
they would be deployed on a machine that is not prepared, and they say why.

**One automatic run per input hash** ([hashes](#hashes)). A failure is not repeated by itself: it waits for a new commit or
a person. A guest's setup changing does not run its services again, and a change to a service does not plan or configure its
guest. A provision run that creates or replaces something may have made a new machine, so its setup and then its services
are proposed again.

### Services on a guest

Services are read from `services/<id>/service.yaml` in the guest's folder. A guest's page lists them with their status and
last run, and each has a page with its workflow, stages, policy and history. `GET /api/services/{id}` has the same.

An invalid `service.yaml` marks only that service. An invalid `guest.yaml` marks the guest's services invalid, and they are
not treated as gone. A renamed guest keeps its services and their state.

**The key follows who manages the machine, not what kind of Ansible work runs.** Decided by the owner on November 3, 2026:

- **On a machine Octopushy provisions, any Ansible stage logs in with Octopushy's own key.** A service's Ansible stage counts
  exactly as a guest's setup stage does, so a guest whose only Ansible work is a service gets a key of its own, as described
  in [guest-configuration.md](guest-configuration.md#ssh-identity). The private half reaches only that guest's `playbook`
  steps, never a preview or an OpenTofu stage, and a run binds only its own guest's key.
- **An explicit `access.sshKey` still wins**, and a guest that names one gets no key of Octopushy's.
- **A machine Octopushy does not manage has to name `access.sshKey`.** Octopushy puts its key on a machine only through an
  apply of that guest's provisioning, and never forces it on otherwise. What it checks is a record, not ownership: **a
  successful provisioning apply carried the public key, or a login with it alone worked.** Where neither is recorded,
  nothing that would log in with Octopushy's key starts, for setup and services alike, and Deploy, Configure and a schedule
  are refused with a message saying to name `access.sshKey`.

**Only a valid service gives its guest Octopushy's key, and once issued the key stays.** Once Octopushy has issued a key for
a guest, the guest keeps wanting it until the guest is retired or names `access.sshKey`, whatever happens to its services or
its workflow: a service broken in any way, removed, or a workflow without a setup stage. Taking Octopushy's public key off a
machine is never worth provisioning it again for, and a key left on a machine is harmless: nothing logs in with it unless
Ansible work asks for it, and that work is held to the delivery record above.

**Adding a valid service with an Ansible stage to an existing guest delivers the key the way a setup stage does.** It
changes the guest's provisioning hash, so the guest is provisioned again first, and the service waits for that. For a VM that
is an in-place update and one reboot.

### Removal

**Later.** A deleted folder proposes a removal under the same single approval and policy, and removes only what workflows
declare.

**Today a deleted service folder is shown as *missing*, and removes nothing.** No run starts. Whatever the service deployed
stays on the guest, and its stages' OpenTofu state stays in Octopushy. Restoring the folder picks the service up again.
Removing the guest does remove the service as a resource, with what was inside the guest, and ends any of its runs still
going. Anything a stage made outside the guest stays there, since nothing plans its destroy yet.

## The earlier format is refused

There is nothing to migrate (a fresh start, decided November 3, 2026), so the recipe format is not read alongside:

- **A `recipes/` folder** makes the whole repository invalid, with a message pointing here.
- **A service folder with `pipeline.yaml`** makes that service invalid, with a message pointing here.
- **`provision`, `setup` and `recipe` in `guest.yaml`** are unknown fields, refused by its schema, and a guest without
  `workflow` is invalid.

## Validating offline

`octopushy-validate` reads a repository the way the server does, with the same `Octopushy.Definitions`, and prints every
issue with its file and line, then what each guest and service resolved to:

```sh
octopushy-validate <config-dir> [--workflows <git url|path>]
octopushy-validate --workflows-only <dir>

just validate examples/lab
# or
dotnet run --project backend/src/Octopushy.Validate -- <config-dir>
# or, with no .NET installed
docker run --rm -v "$PWD:/repo:ro" docker.io/skorcius/octopushy:validate-main /repo
```

- **Without `--workflows`,** workflows are the repository's own `workflows/`, or the release tags of the workflows repository
  its `octopushy.yaml` names, fetched with Git's own credentials.
- **`--workflows <url>`,** a Git URL (`https://`, `ssh://`, `user@host:path`, `file://`) or a bare repository: its tags,
  resolved exactly as the server resolves them, with the same code. A validation has no history, so a tag is taken where it
  points now; an invalid release is a warning.
- **`--workflows <dir>`,** a directory with `workflows/`: every workflow at the version its `workflow.yaml` declares, as it is
  on disk, the way local workflows are read. This is for a workflows repository's own CI, with a fixture lab repository.
- **`--workflows-only <dir>`** checks every `workflows/*/workflow.yaml` in a directory and the paths it names, with no lab
  repository.

It exits 1 when there is an error, 2 for a usage error, and 0 otherwise; warnings are printed and do not fail it. The image
`validate-<version>` (and `validate-main`) is published with the others, and runs as `octopushy-validate`.

**CI in the workflows repository** checks its workflows alone, then against a fixture lab repository that names them at the
versions on the branch:

```yaml
# .github/workflows/validate.yml in octopushy-lab
on: [push, pull_request]
jobs:
  validate:
    runs-on: ubuntu-24.04
    container: docker.io/skorcius/octopushy:validate-main
    steps:
      - uses: actions/checkout@v6
      - run: octopushy-validate --workflows-only .
      - run: octopushy-validate tests/fixture-config --workflows .
```

**CI in the lab repository** resolves its references against the workflows repository's tags, as the server will:

```yaml
# .github/workflows/validate.yml in homelab-config
on: [push, pull_request]
jobs:
  validate:
    runs-on: ubuntu-24.04
    container: docker.io/skorcius/octopushy:validate-main
    steps:
      - uses: actions/checkout@v6
      # A private workflows repository needs a credential Git can use, such as a read-only deploy key or token.
      - run: octopushy-validate .
```

## The first journey through the files

New VM → configured guest → working service:

1. Add `guests/<id>/guest.yaml` under an installation, naming a workflow with `manages: vm`. Octopushy proposes provisioning:
   the plan of the workflow's machine stage for that guest.
2. Once the guest is provisioned and verified, Octopushy runs the workflow's setup stage, if it has one.
3. Add `services/<name>/service.yaml` under the guest, naming a workflow with `manages: service`. Once the guest's provision
   and setup are done, Octopushy plans and previews every stage, applies its policy to them once, then applies them in order.

Each step is a separate proposal. A failure in one guest or service blocks only what depends on it.

## Later

### Interfaces

`dns` is a named slot bound to a workflow in the lab. Each workflow of a slot accepts the same inputs, so swapping one is one
line. This replaces `lab.dns`, which is accepted and ignored today. The slot's schema lives in the repository, such as
`interfaces/dns.schema.json`, and is evaluated against the stage's inputs together with the guest's definition, as an invalid
definition at sync, with no knowledge of DNS in Octopushy.

### Clusters

A cluster is a set of guests with stages applied to them, under one approval. A stage runs on each member in turn, starting
with the first listed, because a run connects to one guest with its own key and may bind only that key. A failed member
stops the rest. When a member is replaced, the cluster's run is due again. A `clusters` folder in a lab is not read today,
and the lab shows a warning.

### Build order

1. **Stages with one approval, and services shown on guests.** Built.
2. **Workflows kept in the lab repository, with versions, and the offline validator.** Built.
3. **A workflows repository resolved by tags,** with immutable tags recorded per commit. Built.
4. **Interfaces, and secrets for OpenTofu stages.** `lab.dns` goes. A stage's plan gets its token here, which is the limit
   stated above.
5. **Containers.**
6. **Clusters.**

### Left out for now

- `interface` on a stage, `interfaces` on a lab, `dependsOn` between services, and any other unknown field: refused by the
  schemas.
- Secrets on an OpenTofu stage. A stage's state is always the built-in state.
- A list of what changed since the last success for a service's stage preview, which a configure run's preview has.
- Choosing an action for a service: its page offers Deploy only.
