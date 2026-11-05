# Workflows migration

**Draft, November 3, 2026, after review by two independent agents.** This plan takes Octopushy from "an engine that
knows every kind of run" to "a generic runner for workflows kept in their own repository". Decisions are marked
**D1**–**D6**.

**Fresh start.** The owner decided on November 3 that `pve01-ai` and `umbrel-worker-01` are test guests, to be removed and
redone. Nothing in the live lab has to survive the migration:

- No guest, no OpenTofu state, and no run history is carried over.
- The database starts empty, and the 19 EF migrations are squashed into one.
- Code that exists only to upgrade earlier releases goes.

What still has to be protected is **what Octopushy does not manage**: Home Assistant (VM 100 on ziost) and `c-fedora-01`
(VM 100 on pve01), both reachable with full-privilege tokens.

## Why

The design rule of November 2 is that Octopushy holds nothing service-specific. The code does not follow it yet:

- **Each run has a fixed shape, chosen by name.** `RunFactory.cs` builds a fixed step list per run kind, and about 30
  `StepNames` each carry their own meaning in about ten places: `ActionFor`, `TokenFor`, `SecretsFor`,
  `ApplyEffectsAsync`, the approval deciders, four server-side worker branches, and the readiness lists.
- **Features are built into the engine.** Managed workers (about 1,100 server lines and 500 agent lines, plus the wizard),
  the disk reporter (about 320 server lines and 1,160 agent lines including its crate) and NixOS root growth (about 300).
  The frontend holds about 700 lines for these.
- **Every new capability costs 2–5k lines of backend.** The pipelines change added 5,058 lines to the backend, schedules
  2,780 and key rotation 3,103. Each time, the lab repository gained nothing.
- **Three copies of the modules have diverged.** `examples/lab` here, `homelab-config` and `octopushy-lab` each hold
  their own `proxmox-vm`. The Technitium, Portainer, NixOS and Fedora tests live in this repository.
- **The docs have drifted.** `homelab-config`'s README describes 0.2.0 and says setup is not implemented. Almost 7k
  lines of docs here mix decisions, dated evidence and superseded designs.

## Final form

Three repositories, each with one job.

| Repository | Holds | Never holds |
|---|---|---|
| `octopushy` | The engine: API, run engine, agent, UI, its own contract tests with minimal fixtures | Workflows, modules, anything that names a service |
| `octopushy-lab` (**workflows**) | `workflows/<id>/`, each versioned `M.m`, plus shared `modules/` (OpenTofu, Ansible, NixOS), tests and CI | Installations, guests, endpoints, secrets |
| `homelab-config` (**config**) | `octopushy.yaml`, `labs/**` (installations, guests, services, policies, schedules), `templates/` (image URL and checksum are data) | Code: no workflows, modules or playbooks |

### Workflows and their versions

**Decided by the owner on November 3: a version belongs to a whole workflow, and is `M.m`.** A workflow is everything needed
to manage one kind of thing from start to end, for example a Fedora VM: create it, set it up, update it, run its actions
and retire it. It is not a single stage.

- **A major version (`M`) is a breaking change.** Examples are an input that is removed, renamed, newly required or
  narrowed, or a change that would replace existing machines.
- **A minor version (`m`) does not break the flow.**

`octopushy-lab` layout:

```text
workflows/fedora-vm/workflow.yaml      version, stages, actions, form
workflows/fedora-vm/inputs.schema.json the inputs, which are also the wizard's form
workflows/technitium-dns/…
workflows/octopushy-worker/…
modules/opentofu/proxmox-vm/           shared code, versioned through the workflows that use it
modules/ansible/machine/
```

```yaml
# workflows/fedora-vm/workflow.yaml
schemaVersion: 1
version: 2.3
manages: vm                       # vm | container | service
description: A Fedora VM on Proxmox, set up with the parts it selects.
inputs: inputs.schema.json        # validates the guest's inputs; renders the form
generated: [vmid]                 # filled by Octopushy in the wizard
stages:
  - name: machine                 # provision: approved on its own, before setup
    executor: opentofu
    root: ../../modules/opentofu/proxmox-vm
    verify: proxmox
  - name: setup
    executor: ansible
    root: ../../modules/ansible/machine
    playbook: configure.yml
actions:
  update-system: { stage: setup, playbook: update-system.yml, destructive: false }
```

A guest in `homelab-config` names one workflow and its major version. `@2` takes the newest `2.x`, and `@2.3` stays on
exactly `2.3`. A reference without a version is invalid, so a major version is never adopted by accident:

```yaml
# labs/home/proxmox/pve01/guests/pve01-ai/guest.yaml
schemaVersion: 1
workflow: fedora-vm@2
host: pve01
vmid: 200
inputs: { cores: 2, memory: 1024, features: [ai-tools, cli-tools, developer, t3, tailscale] }
secrets: { machine_tailscale_auth_key: { store: pve01-ai/tailscale-auth-key } }
policy: { approval: automatic, destructive: manual }
```

`homelab-config/octopushy.yaml` names only the repository: `workflows: { repository: git@github.com:jcarcaboso/octopushy-lab.git }`.

**Releases and the rules around them:**

- **Tags.** CI in `octopushy-lab` tags each workflow as `fedora-vm/v2.3` when it is merged to `main` with a new
  `version`.
- **Version bumps are enforced by CI.** A change to a workflow's folder, or to a module it uses, needs a version bump.
  A change to its inputs schema that rejects inputs the previous version accepted needs a **major** bump.
- **A tag never moves.** Octopushy records the commit each tag pointed to when it first saw it, and refuses a tag that
  later points somewhere else.
- **A new minor release reaches every guest on that major.** Each affected guest's inputs hash changes, and its runs are
  proposed under its policy. That is the point of `@2`. Guests that pin `@2.3` do not move.
- **A new major release is offered, not applied.** The guest page shows "fedora-vm 3.0 available, breaking". Upgrading
  is a UI change, so it becomes a commit from `@2` to `@3` and then follows the normal process. The form asks for any
  new inputs.
- **Each run records what it ran:** the workflow, its version and its commit.

### Changing things from the UI

**Decided by the owner on November 3:** any change can be made in the UI. Behind the scenes it follows the same process
as a change made in Git:

1. **The UI is a way of writing the commit.** A form edit, such as a VM's memory, disks or setup inputs, or a wizard,
   becomes a commit that Octopushy writes to `homelab-config` itself. The commit is compare-and-swap against the commit
   the form was loaded from, and the person who made it is its author.
2. **Octopushy syncs that revision at once.** It does not wait for the poll interval.
3. **The normal process runs:** validation, plan, approval as the policy says, apply, verify. A form edit that does not
   validate is refused before anything is committed.
4. **The UI follows it through.** The page shows the commit, then the run, step by step, until the change is applied or
   fails. A change that waits for approval can be approved from the same page.

Git stays the one source of truth. Nothing the UI changes exists only in the database, and there is no drift to
reconcile.

**Forms come from schemas, not from code.** A guest's form is rendered from its workflow's `inputs` schema, plus
optional UI hints. A new field in a workflow appears in the UI without an engine change.

**Operations that are not desired state** do not go through Git:

- start, stop, shutdown and reboot;
- snapshot, rollback and delete snapshot;
- run a workflow action;
- retry;
- open the console.

They still go through the run engine: each is a run with steps, an agent attempt and a log, so it is recorded like any
other run. They are refused on an installation with `manage: false`.

### One-click deployment: the workflow is the form

The wizard is generic:

1. Choose a workflow, such as Fedora VM, Octopushy worker or Technitium DNS.
2. Choose an installation and host.
3. Fill in the form, which is the workflow's `inputs` schema with UI hints.
4. Review the `guest.yaml` it will commit.
5. **Deploy.** Deploy commits, syncs and runs, and can include the approval in the same click.

**Generated values** are filled in by Octopushy: the next free VMID on the host, an agent enrollment token put in the
store, and keys. An Octopushy worker is just the `octopushy-worker` workflow, whose setup installs the agent with a
generated enrollment token. One click gives a worker, with no worker-specific code in the engine.

### What the engine keeps

- Agents, enrollment, mTLS and the CA.
- The variable store and its versions.
- Built-in OpenTofu state.
- Git sync and the repository editor. The editor stays config-only.
- UI changes as commits: forms rendered from schemas, the compare-and-swap commit, immediate sync, and following the run
  to the end.
- The workflow wizard and generated values.
- VM operations (power, snapshots, actions), as recorded runs.
- Approvals and policy.
- Proxmox observation, inventory, `verify-guest`/`verify-absent` and VM protection.
- Guest access keys and rotation, with their embedded playbooks.
- Schedules and actions.
- Auth and operator tokens.
- Live events and notifications.

### What the engine loses

- Managed workers: their steps, the worker-only wizard, the `worker` field, the `managed-worker` executor, and
  `deploy/worker-template-v1`. One-click workers come back as the `octopushy-worker` workflow.
- The disk reporter: the step, its tokens and ingest endpoint, the `guest` executor, and the `disk-reporter` crate.
- NixOS root growth: `guestFilesystem`, the `nixos-rootfs` executor, and the `grow-root`/`verify-root` steps.
- External OpenTofu state backends (`stateBackends`, `provision.state`).
- Code that handles runs from earlier releases (legacy credential contexts, revisions without a source).

### How runs work in the end

- **The generic unit is the stage** of a workflow: one executor, one fixed pattern.
  - `opentofu`: `plan`, approval, then `apply` of exactly that plan.
  - `ansible`: `preview`, approval, then `run`, plus the outcome approvals a workflow declares.
- **Step meaning comes from a step-type registry**, not from name switches. Each type is one class that says how to
  build its assignment, which credentials it may hold, how it is approved, and what effects it has. The types are:
  `opentofu.plan`, `opentofu.apply`, `ansible.preview`, `ansible.run`, `approval`, `proxmox.verify`,
  `proxmox.verify-absent`, `proxmox.protection`, and the access-rotation types.
- **Resources keep separate runs where approval needs them:**
  - A **guest** has a provision run, then a setup run. A setup preview cannot describe a machine that does not exist
    yet, so the two are not under one approval.
  - A **service** keeps its single pipeline approval.
  - A **cluster** (later) gets stages per member.
- **Scheduling keeps three hashes per stage:** what is desired, what was last proposed, and what last succeeded.
  - A failure is not proposed again by itself. Manual, scheduled and store-value triggers can run unchanged inputs.
  - **Machine generation:** a provision apply that creates or replaces the VM bumps the guest's generation. That
    invalidates its setup and services across runs, not only within one run.

## Safety boundary

Nothing in the plan may let Octopushy plan or apply against a VM it does not manage:

- **`manage: false` on an installation,** for ziost, is enforced by the engine. Observation only: no provision,
  configure or pipeline assignment is ever created for it, and its token is released only to `observe`. It is checked
  at sync and again at dispatch.
- **Two new refusals, in Phase 1.** Today a plan that deletes any VM only waits for approval (`GuestVmPlan.RemovesVm`), and
  nothing compares a declared VMID with the VMs Octopushy observes but does not manage. Instead:
  - a plan that deletes or replaces a VM other than the guest's own is **refused**, not held;
  - a guest whose `vmid` is an observed unmanaged VM is **invalid** at sync.

  Together with VM protection, these guard `c-fedora-01` behind pve01's root token.
- Narrowing the tokens remains the hardening planned for later. It is not a prerequisite.

## Migration

The phases ship in this order. Phase 4 can run beside any of them.

### Phase 0: clean slate

**Lab:**
- Retire `pve01-ai` and `umbrel-worker-01` through Octopushy, using today's removal path. Check that VMs 200 and 900
  are gone.
- Drop the Octopushy database. Keep a list of the store keys to enter again: three Proxmox tokens and the `shared/*`
  values.
- Narrow nothing, and touch nothing on ziost.

**`octopushy`:**
- Fix the README: status, the `proxmox-e2e` default note, and links.
- Delete superseded docs (**D4**).
- Move service-specific content to `octopushy-lab`: `examples/lab/modules/{nixos,ansible/*}` and
  `tests/{technitium,portainer,nixos,fedora}`. Keep minimal fixtures for the engine's own end-to-end, ansible and
  pipeline tests.
- Update `checks.yml`, `scripts/checks/offline.sh`, the flake checks, and `deploy/worker-image/image.nix`. That last one
  goes in Phase 1 along with the worker.

**`octopushy-lab`:**
- Replace its contents with `homelab-config`'s `recipes/`, `modules/` and `tests/`, which are the newest. They keep the
  recipe format until Phase 2 converts them into workflows.
- Merge `tests/technitium` from both sources on purpose, rather than overwriting one with the other.
- Add CI: `tofu validate`, `ansible-lint`, the container tests and the snippet test.

**`homelab-config`:**
- Rewrite the README, `docs/repository.md` and `.claude/skills` for the real state.
- Delete `docs/umbrel-onboarding.md`, `docs/fedora-smoke.md`, `ops/fedora-smoke/`, and the worker and update guides.

### Phase 1: shrink the engine

- **Delete managed workers, the disk reporter and NixOS root growth,** across the backend, agent, contracts, frontend and
  docs. Workers become ordinary guests with an agent-install setup stage and a one-time enrollment token from the
  store (**D1**).
- **Delete external state backends and the code for earlier releases.**
- **Squash migrations into a new `Initial`.**
- **Add the safety boundary:** `manage: false` for installations, the refusal of a plan that removes a foreign VM, and
  the VMID check against observed VMs, each with tests.
- **Result:** the run kinds are provision, retire, configure, pipeline and rotate-access, with no feature steps.

### Phase 2: versioned workflows from their own repository

**Built in Octopushy:** the format, local workflows, the hashes, the workflows source with its tags and per-attempt bundles,
and the offline validator with `--workflows` and `--workflows-only` and its image: [workflows.md](workflows.md). What is left
is in the two other repositories: their CI, and converting the recipes into workflows released as `1.0`.

- **Format.** Write the `workflow.yaml` schema: version, `manages`, stages, actions, inputs, generated values. Guests and
  services reference `workflow: <id>@<M>` or `@<M.m>`, with `inputs` and `secrets`.
  - `provision`/`setup`/`recipe` in `guest.yaml` and `stages` in `pipeline.yaml` go, since this is a fresh start and
    there is nothing to migrate.
  - `templates/` stays in `homelab-config`.
- **Workflows source.** A second mirror fetches the workflows repository's branches and tags, authenticated with a store
  key. Tags are parsed as `<id>/v<M>.<m>`, and tag-to-commit records refuse a tag that moves.
- **Resolution.** Each config revision resolves every reference to `(version, commit)`.
  - A revision's identity becomes `(config commit, digest of the resolutions)`. A new minor tag is a new revision, even
    with no new config commit.
  - Every commit-keyed path uses that identity: sync, head selection, `RevisionCache`, bundle cache, pruning, `Run`,
    approval manifests and `PipelineDocument`.
- **Bundles are per attempt:** the config tree plus the workflows tree at the resolved workflow's commit. Paths inside
  stay relative, so agents need no change.
- **Hashes** cover the resolved version, the workflow's folder and the modules it uses. A workflow that references
  anything outside its folder and `modules/` is refused.
- **The editor writes only config.** It validates drafts against their resolved workflows, including a draft that changes
  a version.
- **`octopushy validate <config> --workflows <path>`,** reusing `Octopushy.Definitions`.
- **CI in `octopushy-lab`:**
  - validate every workflow against a fixture config;
  - enforce version bumps, including breaking schema changes as a major;
  - tag on merge;
  - `tofu validate`, `ansible-lint` and the container tests.
- **CI in `homelab-config`:** validate against the resolved workflows.
- **Convert** `octopushy-lab`'s recipes into workflows (`fedora-vm`, `technitium-dns`, `octopushy-worker`) and release
  each as `1.0`. Delete recipes and modules from `homelab-config`.

### Phase 3: change from the UI, and one-click deployment

**3A, changes from the UI, is built** ([changes-from-the-ui.md](changes-from-the-ui.md)): forms rendered from the inputs
schema with `ui` hints, the compare-and-swap commit through the editor's publish path with immediate sync, upgrades to a newer
major, removing a guest, and the change page.

**3B, the workflow wizard, is built** ([the workflow wizard](changes-from-the-ui.md#the-workflow-wizard)): **Deploy** picks a
workflow, an installation and host, fills in the form and the secrets, reviews the exact file, and commits a new guest's or
service's folder as a `create-guest` or `create-service` change. Generated values are generic and requested by the workflow's
`ui` ([generated values](workflows.md#generated-values)): the next free VMID, a one-time agent enrollment token, and a random
password; the managed SSH key stays the server's. A deploy can approve its first plan in the same click when that plan only
creates. `octopushy-lab` gives its VM workflows these hints at 1.1. What is left of Phase 3 is the acceptance on real
Proxmox.

- **UI changes as commits.**
  - Form editing for guests and services, rendered from the workflow's `inputs` schema.
  - A compare-and-swap commit through the existing `RepositoryMirror.Write` path, with immediate sync.
  - A change page that follows commit → revision → run → applied, with approval inline.
  - Octopushy needs a repository-scoped deploy key with write access to `homelab-config`.
- **The workflow wizard.** Choose a workflow, then installation and host, then form, then review, then Deploy. Deploy can
  include the approval. Generated values are the next free VMID, an enrollment token and keys.
- **Upgrades.** A new major version is shown on guests that use the workflow, with an upgrade that commits `@M+1`.
- **Acceptance on real Proxmox:** rebuild `pve01-ai` and a worker **through the wizard**, then resize one from its form.
  Each change shows as a commit in `homelab-config` and is applied through the normal process.

### Phase 4: live panel, VM operations and notifications (independent)

- **VM operations as recorded runs.** New `proxmox` executor actions: `start`, `shutdown`, `stop`, `reboot`,
  `snapshot`, `rollback` and `delete-snapshot`. They are offered from the guest page, recorded with a log, and refused on
  `manage: false`. A rollback or a stop waits for confirmation in the UI.
- **Live updates.** `GET /api/events` (SSE) streams run, step, attempt and resource changes. The UI invalidates queries on
  events, and polling stays as the fallback.
- **Disk usage from observation** (**D2**). The agent calls the QEMU guest agent's `get-fsinfo` per VM, bounded and with
  partial failure handled. When it is not available the panel shows *unknown*, not zero. Check the privilege with the
  real observation token first, and never widen ziost's token for it.
- **Notifications.** One transport, a webhook, for `run.failed`, `approval.waiting` and `guest.unreachable`, configured in
  `octopushy.yaml` with its URL in the store. `notifyme` receives it.
- **Health checks.** Services declare checks (`http`, `tcp`, `dns`), and agents run them during observation. This
  replaces the uptime prototype on :8090.

### Phase 5: LXC and DNS

**LXC is built** ([a workflow that manages a container](workflows.md#a-workflow-that-manages-a-container)): guest
`type: container` and `manages: container`, the client's `lxc` endpoints, `verify-guest`, `verify-absent` and protection
for containers, every guard of the [safety boundary](safety-boundary.md) for them, keys set once at creation, and a host
key pinned from the create task with no person ([the host key of a container](guest-configuration.md#the-host-key-of-a-container)).
`octopushy-lab` has `proxmox-lxc` and `octopushy-worker-lxc` at `1.0`. The wizard does not offer container workflows
yet. What is left is the acceptance on real Proxmox: a worker container on `umbrel`, committed in Git.

- **LXC is engine work plus a workflow.** It needs guest `type: container`, client types, `verify-guest`/`verify-absent` and
  protection for containers, the access behaviour (keys are `ForceNew`), and a `proxmox-lxc` workflow in `octopushy-lab`.
- **DNS starts as an explicit workflow stage,** such as Technitium records, before any `interfaces` abstraction. A Proxmox
  cluster installation (`type: cluster`) and a Technitium cluster are different things, and they are planned separately.

### Phase 6: step-type registry

- **Extract `IStepType`** from about 4,000 orchestration lines: `ActionFor`, `TokenFor`, `SecretsFor`, the deciders,
  `ApplyEffectsAsync` and the readiness lists. It is a refactor with no change in behaviour, and the tests are the proof.
- **`StepTimeline` labels come from step data.**
- **OpenTofu stages may hold credentials,** through the registry, not by relaxing validation.

### Phase 7: run model

Specified in its own document before any code is written. It covers:

- the three per-stage hashes;
- machine generation;
- provision and setup as separate approvals;
- outcome approvals in stages;
- one scheduler where that removes duplication that has been shown, not as a goal in itself.

## Decisions

- **D1: managed workers.** **Recommended:** delete the engine machinery. A worker is an ordinary guest deployed from
  the `octopushy-worker` workflow, whose generated enrollment token keeps it one click. Keep the drain safeguard when
  a worker is retired. Between Phase 1 and Phase 3 a worker is enrolled by hand.
- **D2: disk usage.** **Recommended:** read it from the QEMU guest agent during observation (Phase 4). Until then, no
  disk meter.
- **D3: versions.** **Decided by the owner, November 3:** each whole workflow has a version `M.m`, and references name
  `@M` (newest minor) or `@M.m` (exact). Commit pins are not used. Tags are immutable, and CI enforces bumps.
- **D4: old docs.** **Recommended:** delete what is superseded, since Git keeps it.
  - **Keep:** `CONTEXT.md`, the ADRs, and the docs for credentials, state, installation trust, operator access and
    recovery.
  - **Rewrite** `lab-repository.md` and `pipelines.md` into one `workflows.md`.
- **D5: adopting a minor release.** **Recommended:** automatic for `@M` references, under each guest's approval and
  destructive policy, with no config commit. A guest that must not move pins `@M.m`. Approval does not sandbox
  planning, so a minor release runs its plan with the guest's credentials before anyone approves.
- **D6: UI changes.** **Decided by the owner, November 3:** the UI may change anything, and every change is a commit to
  `homelab-config` that then goes through the normal process. Operations that are not desired state, such as power and
  snapshots, are recorded runs with no commit.
