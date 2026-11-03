# Workflows migration

**Draft, November 3, 2026, after review by two independent agents.** This plan takes Octopushy from "an engine that
knows every kind of run" to "a generic runner for workflows kept in their own repository". Open decisions are marked
**D1**–**D5**.

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
| `octopushy` | The engine: API, run engine, agent, UI, its own contract tests with minimal fixtures | Recipes, modules, anything that names a service |
| `octopushy-lab` (**workflows**) | `recipes/`, `modules/` (OpenTofu, Ansible, NixOS), `templates/`, their tests and CI | Installations, guests, endpoints, secrets |
| `homelab-config` (**config**) | `octopushy.yaml`, `labs/**`: installations, guests, services, policies, schedules | Code: no `recipes/`, `modules/`, `templates/` |

`homelab-config/octopushy.yaml` pins the workflows **by commit**. Only a config PR can change what runs:

```yaml
schemaVersion: 1
workflows:
  repository: git@github.com:jcarcaboso/octopushy-lab.git
  commit: 3f9c2a1e…      # full SHA, immutable
  release: v0.3.0         # display only; a tag that does not point at `commit` is a warning
defaults:
  policy: { approval: manual }
```

### What the engine keeps

- Agents, enrollment, mTLS and the CA.
- The variable store and its versions.
- Built-in OpenTofu state.
- Git sync and the repository editor. The editor stays config-only.
- Approvals and policy.
- Proxmox observation, inventory, `verify-guest`/`verify-absent` and VM protection.
- Guest access keys and rotation, with their embedded playbooks.
- Schedules and actions.
- Auth and operator tokens.
- Live events and notifications.

### What the engine loses

- Managed workers: their steps, the wizard, the `worker` field, the `managed-worker` executor, and
  `deploy/worker-template-v1`.
- The disk reporter: the step, its tokens and ingest endpoint, the `guest` executor, and the `disk-reporter` crate.
- NixOS root growth: `guestFilesystem`, the `nixos-rootfs` executor, and the `grow-root`/`verify-root` steps.
- External OpenTofu state backends (`stateBackends`, `provision.state`).
- Code that handles runs from earlier releases (legacy credential contexts, revisions without a source).

### How runs work in the end

- **The generic unit is the stage:** one recipe, one executor, one fixed pattern.
  - `opentofu`: `plan`, approval, then `apply` of exactly that plan.
  - `ansible`: `preview`, approval, then `run`, plus the outcome approvals a recipe declares.
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
- Replace its contents with `homelab-config`'s `recipes/`, `modules/`, `templates/` and `tests/`, which are the newest.
- Merge `tests/technitium` from both sources on purpose, rather than overwriting one with the other.
- Add CI: `tofu validate`, `ansible-lint`, the container tests and the snippet test.

**`homelab-config`:**
- Rewrite the README, `docs/repository.md` and `.claude/skills` for the real state.
- Delete `docs/umbrel-onboarding.md`, `docs/fedora-smoke.md`, `ops/fedora-smoke/`, and the worker and update guides.

### Phase 1: shrink the engine

- **Delete managed workers, the disk reporter and NixOS root growth,** across the backend, agent, contracts, frontend and
  docs. Workers become ordinary guests with an `octopushy-agent` setup recipe and a one-time enrollment token from the
  store (**D1**).
- **Delete external state backends and the code for earlier releases.**
- **Squash migrations into a new `Initial`.**
- **Add the safety boundary:** `manage: false` for installations, the refusal of a plan that removes a foreign VM, and
  the VMID check against observed VMs, each with tests.
- **Result:** the run kinds are provision, retire, configure, pipeline and rotate-access, with no feature steps.

### Phase 2: workflows from their own repository

- **Separate mirrors per source,** each fetching by commit. Workflows authentication is a store key.
- **Composed snapshot.** An immutable, separate snapshot overlays the workflows repository's `recipes/`, `modules/` and
  `templates/` onto the config tree. Paths in the bundle stay what they are today, so the loader, the agents and the
  recorded OpenTofu roots do not change. If the config repository holds any of those folders as well, the revision is
  invalid.
- **Revision identity becomes `(config commit, workflows commit)`.** It is used in sync, head selection, `RevisionCache`,
  bundles, pruning, `Run`, approval manifests and `PipelineDocument`. The commit diff in `Planner.cs:334-344` diffs the
  composed trees.
- **The editor writes only config.** It validates drafts against the composed snapshot, including a draft that moves the
  pin, and refuses writes into `recipes/`, `modules/` and `templates/`.
- **Close the hash gaps:** an `inputs` schema outside the recipe folder is refused, and so is an OpenTofu `source` outside
  `root`.
- **Offline validator:** `octopushy validate <config> --workflows <path>`, reusing `Octopushy.Definitions`. It runs in CI
  for `homelab-config` (against the pinned commit) and for `octopushy-lab` (against a fixture config).
- **Cut over:** pin `octopushy-lab` in `homelab-config` and delete `recipes/`, `modules/` and `templates/` there.

### Phase 3: rebuild the lab on the new shape

- Redefine `pve01-ai` in `homelab-config`, using recipes from the pinned `octopushy-lab`.
- Redefine a worker as an ordinary guest, enrolled by the `octopushy-agent` recipe.
- This is the acceptance test of Phases 1 and 2 on real Proxmox.

### Phase 4: live panel and notifications (independent)

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

- **LXC is engine work plus a recipe.** It needs guest `type: container`, client types, `verify-guest`/`verify-absent` and
  protection for containers, the access behaviour (keys are `ForceNew`), and a `proxmox-lxc` recipe in `octopushy-lab`.
- **DNS starts as an explicit recipe stage,** such as Technitium records, before any `interfaces` abstraction. A Proxmox
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

- **D1: managed workers.** **Recommended:** delete them. A worker is an ordinary guest with an `octopushy-agent` setup
  recipe and manual enrollment. Keep the drain safeguard when a worker is retired.
- **D2: disk usage.** **Recommended:** read it from the QEMU guest agent during observation (Phase 4). Until then, no
  disk meter.
- **D3: pinning.** **Recommended:** by commit, with the release tag for display only. Tracking a branch is not offered.
- **D4: old docs.** **Recommended:** delete what is superseded, since Git keeps it.
  - **Keep:** `CONTEXT.md`, the ADRs, and the docs for credentials, state, installation trust, operator access and
    recovery.
  - **Rewrite** `lab-repository.md` and `pipelines.md` into one `workflows.md`.
- **D5: trusting a workflows bump.** **Recommended:** no extra gate, since the pin is a commit in a reviewed config PR.
  Apply approval and the destructive policy still apply to every change, and approval does not sandbox planning.
