# Octopushy

A self-hosted operations control plane for a homelab. Definitions live in Git; Octopushy
plans each change, waits for approval where a resource's policy asks for it, applies
exactly what was approved through agents, and keeps a visible record of every step.
The vocabulary is in [CONTEXT.md](CONTEXT.md).

## Status

Milestone 1 has shipped, up to release 0.9.2:

- Definitions as [versioned workflows](docs/workflows.md): a guest or service names `<workflow>@<major>`
  or `@<major>.<minor>`, resolved from the release tags of a
  [workflows repository](docs/workflows.md#workflows-from-their-own-repository), or from the lab repository's own
  `workflows/`. A tag never moves, and a new minor release reaches every guest on its major under that guest's policy.
- Proxmox VMs and LXC containers provisioned, verified and retired with OpenTofu, with state kept by Octopushy. A VM
  Octopushy creates gets an SSH host key Octopushy generated, and a container's keys are pinned from the Proxmox task
  that created it, so neither needs a person to pin it ([host key trust](docs/guest-configuration.md#host-key-trust)).
- [Guest setup with Ansible](docs/guest-configuration.md) once a machine exists, and
  [services](docs/workflows.md#services-and-their-runs) of OpenTofu and Ansible stages.
- Actions on a schedule declared in Git, and SSH access keys that Octopushy manages and rotates.
- A [repository editor](docs/repository-editor.md) that publishes drafts as commits.
- [Worker routing](docs/worker-routing.md): work goes to an agent that was just shown to reach what the step
  connects to, from where the step connects, without location labels.
- [Changes from the UI](docs/changes-from-the-ui.md): a guest or service is edited from its workflow's inputs schema, upgraded
  to a newer major, or a guest removed, and each change is one commit to the lab repository, compare-and-swap against the
  commit its form was loaded from and authored by the operator. Octopushy syncs it at once, and its change page follows it
  through plan and approval to applied. This needs `Octopushy__RepositoryEditor__PublishEnabled=true` and a repository
  credential that can push, such as a deploy key with write access.
- A [workflow wizard](docs/changes-from-the-ui.md#the-workflow-wizard), **Deploy**: pick a workflow, an installation, host
  and name, fill in its form, and deploy a new VM guest or service as one commit. It does not offer container workflows
  yet. A workflow can ask for generated values, such as the next free VMID or a one-time agent enrollment token put in the
  store, and a deploy can approve its first plan in the same click when that plan only creates.
- Proxmox installations observed through their API, next to what Git declares. An
  installation can be [observed only](docs/safety-boundary.md), and no plan may change,
  remove or import a machine that is not its run's own.

New in 0.9.2:

- A rebuilt Overview page, from #78: five tiles for guests (a ring with a legend that opens the inventory on those
  statuses), what needs you (failed runs and repository problems), recent runs, uptime over 30 days by kind, and
  agents that are missing or have an update. `GET /api/overview` gains `failed`, the last run of each guest and
  service the inventory shows as failed, and `week`, the runs of the last 7 days counted by status.
- The Runs page, from #79: "Approve all N" in the Waiting for you header approves each shown run with its own plan
  fingerprint; a refusal does not stop the rest, and the dialog names the runs that were not approved. Earlier shows
  the 50 newest finished runs and says when more were cut. Selects draw their own chevron in both themes.

No agent contract or database change: upgrade the backend; agents need no update.

New in 0.9.1:

- [Uptime](docs/uptime.md#who-checks) checks go to the agents that reach their target, from #76. A check went to one
  agent, the first by name, and whatever it answered was recorded, so an agent with no route to a guest marked it
  down for good. Each check now keeps a pool of the agents that reached its target in the last day and asks them
  first; an agent that cannot connect, does not answer or cannot check passes the check to the next one. Down is
  recorded only when every connected agent failed and one that used to reach the target is among them, or when no
  agent has reached it at all. The pool is kept in memory and learned again after the server restarts.

No agent contract or database change: upgrade the backend; agents need no update.

New in 0.9.0:

- [Interfaces bound per installation](docs/workflows.md#interfaces), from #74. A binding is a file,
  `labs/<lab>/interfaces/<slot>/<name>.yaml`, with `inputs` and `secrets`; an installation names one per slot
  (`interfaces: { dns: moraband }`), and a service stage of an Ansible workflow declares the slot it fills
  (`interface: dns`). That stage gets the binding's inputs as an extra var named after the slot and its secrets as extra
  vars, and is not run on an installation that binds no such slot. The workflows repository's
  `interfaces/<slot>.schema.json` validates a binding. See [ADR 0006](docs/adr/0006-interfaces-bound-per-installation.md).
- [Uptime](docs/uptime.md), from #72. Every minute an agent checks each trusted installation's Proxmox API, each active
  guest's SSH and each service link, and the server keeps the current state and quarter hours for 30 days:
  `GET /api/uptime` returns 24h, 7d and 30d percentages. A service lists its `links` in `service.yaml`; they are shown and
  checked, and are not part of its hash, so changing them runs nothing. Nothing is checked while no connected agent
  announces the new `uptime-v1` capability.
- **Update an agent from Agents**, from #73. An agent on a guest that sets `inputs.worker_agent_version` gets
  **Update to `<version>`**, or **Update all**: the server commits the guest's new version, approves the change's setup,
  and pauses the agent until it is back on that version. One at a time, the rest queued, and a failure empties the queue.
  See [Updating an agent from Agents](docs/worker-routing.md#updating-an-agent-from-agents). `POST /api/agents/{id}/update`,
  `POST /api/agents/update` and `DELETE /api/agents/{id}/update`. Enrollment tokens no longer need labels.
- A new web interface, from #69 to #73: the sidebar in named groups with the logo blue as the accent and no top bar,
  **Runs** as an inbox (waiting for you, in progress, earlier) with each run's steps and Approve, Reject and Cancel from
  the list, **Repository** as tabs beside a settings column, **Inventory** as columns with uptime and service links, and
  **Agents** as a list whose rows open in place.

Upgrade the backend first, then the agents. The agent contract gained an `http` probe protocol and the `uptime-v1`
capability, which only uptime checks use, so 0.8 agents keep working but are not asked to check anything. The database
gains the `uptime_checks` and `uptime_buckets` tables and the update columns of `agents`. Validate a lab that uses
interfaces or service links with the 0.9.0 validator; older ones refuse them.

New in 0.8.1:

- An agent never configures its own guest, from #67. A worker's agent could take its own guest's setup, which then
  waited on itself: a new agent version restarts the agent, and the stop waits for that setup, until the agent is
  killed after 30 minutes and the run is uncertain. The server now keeps the address each agent connects from, and
  never gives an agent a step whose guest SSH address is that address. IP addresses only: behind NAT, or for a guest
  reached by name, give the guest `execution.agentLabels` that only another agent has. See
  [worker routing](docs/worker-routing.md#an-agent-never-configures-its-own-guest).
- **Force stop** on a run whose change is in progress, `POST /api/runs/{id}/force-stop`, for an agent that will never
  report. The agent is asked to cancel, without waiting for it, and the change becomes uncertain, as when its lease
  runs out: a later result still settles it, and otherwise the run is closed after checking the guest.

No agent contract or database change: upgrade the backend; agents need no update.

New in 0.8.0:

- [Built templates](docs/built-templates.md), from #65. A template may name a Nix flake output of the workflows
  repository, `source.build: { workflow, output, lock }`, instead of an image URL. Any connected agent that advertises
  the new `nix` executor builds it, with no labels: a build touches no installation and carries no secrets. The agent
  streams the image to the server, which keeps it by SHA-256 under `Octopushy:Images` (`KeepUnreferenced` 1,
  `MaxBytes` 10 GiB) and never deletes one the newest revision or an unfinished run needs. A guest on a built template
  waits for its image; its plan and apply then fetch it into `.octopushy-images/` of the OpenTofu root, and the
  workflow's module uploads it to Proxmox inside the approved apply. `GET /api/templates` shows each build, and
  `POST /api/templates/{id}/build` retries a failed one.
- The agent image carries Nix. An agent advertises `nix` only with the local tool runtime, a writable `/nix` and
  20 GiB free (`OCTOPUSHY_NIX_MIN_FREE_GB`); its store is a cache, trimmed to `OCTOPUSHY_NIX_STORE_MAX_GB` (40).

Upgrade the backend first, then the agents: the agent contract gained `UploadImage` and `DownloadImage`, which only
builds use, so 0.7 agents keep working and never build. A worker whose container root is read-only needs a volume at
`/nix` to build: octopushy-lab's octopushy-worker 1.5 and octopushy-worker-lxc 1.2 mount one. The database gains the
`image_builds` table. Validate a lab with a built template with the 0.8.0 validator; older ones refuse its guests.

New in 0.7.3:

- [Secret values can be revealed](docs/credential-lifecycle.md) under Variables, from #63. Each secret row has a
  Reveal button, with Hide and Copy once shown, and Reveal all / Hide all reveal or conceal every secret listed: the
  folder, the search results or all keys. A revealed value stays on the page until you leave it or the key gets a new
  version. `POST /api/variables/reveal` returns each key's active value, or none for a revoked or never-promoted
  version, refuses `system/` keys, and the server logs who revealed which keys.
- A declared outcome can say [`promote: false`](docs/workflows.md), from #62, only with `result: succeeded`. The run
  succeeds and leaves what was applied as it was, so a pending recipe change stays pending and `setup` still runs it.
- `just mock` serves the web app against a [mock API](frontend/mock/README.md), from #60, with no backend, database or
  agent.

Upgrade the backend and frontend to 0.7.3. The agent did not change, so 0.7.2 agents keep working. The workflow schema
gained `promote`, so validate a lab that uses it with the 0.7.3 validator; the 0.7.2 validator refuses it.

New in 0.7.2:

- An approval shows what it changes, from #59. The approval step opens with a row of tiles, one per kind of change
  (Disk scsi0 32 → 64 GiB, Memory 4 → 8 GiB, Tags +agents, Resources +1 ~1, Prepared 2 changed), each opening its
  old → new values under the tiles. For a Proxmox VM, the agent reports the old and new values of a short
  [allowlist](contracts/agent/v1/executors.md) of plan attributes (CPU cores and sockets, memory, disk sizes, tags,
  network bridges). A value the plan marks sensitive is shown only as "sensitive", and nothing else from the plan
  leaves the agent. A configure run compares its prepared values with what the guest's last successful run
  prepared, taken when the run is created.

Upgrade the backend, frontend and agents to 0.7.2. A 0.7.1 agent keeps working with a 0.7.2 backend, but its plans
carry no VM values, so the approval shows only the Resources tile for them. The offline validator at 0.7.1 keeps
working, and no workflow or guest schema changed.

New in 0.7.1:

- [A container's protection is lifted to change it in place](docs/safety-boundary.md), from #57. Proxmox refuses any
  change to a protected container's disks, even growing its root disk, so an approved provision plan that updated a
  container in place failed at apply with `protection mode enabled`. Such a plan now lifts the protection of the guest's
  own container right before the apply and puts it back once the run is done, whether the apply finished or not. VMs
  and plans that replace or delete a machine are unchanged.
- [Run again](docs/workflows.md), from #56. A failed run's page offers **Run again**, which starts a new run of the
  same work at the latest revision, under its own approval.
- Release images are published only as `<component>-<version>`, from #55, without `-latest` or `-sha-<commit>` aliases.
  Builds of `main` keep their `-main` and `-sha-<commit>` tags.

Only the backend and frontend changed: upgrade them to 0.7.1. Agents and the offline validator at 0.7.0 keep working, and
no workflow or guest schema changed.

New in 0.7.0:

- [Per-action approval](docs/workflows.md#guest), from #52. A workflow action can set `approval`, and a
  guest can override it with `policy.actions.<action>.approval`. The guest override wins, then the workflow action,
  then the guest's usual approval. This applies to actions started by a commit, a schedule or a person. Destructive
  approval still follows the guest unless separately set. These additions change the workflow and guest schemas:
  upgrade the server and offline validator to 0.7.0 before using them. Older servers and validators reject the new
  fields, so workflows that use `approval` need this release.
- [Worker routing by reachability](docs/worker-routing.md), from #53. The controller probes agents for access to a
  step's Proxmox API, snippet host or guest SSH endpoint, from the agent process or the step's tool runtime as needed.
  It assigns network-dependent work only with fresh positive evidence. **Upgrade the server first, then agents to
  0.7.0 at each site.** Network-dependent work waits on older agents with `update the agent to report reachability`.
  Check **Agents** and the step's waiting reason after upgrading. Container tool runtimes need a network that reaches
  their targets, plus `/bin/sh` and `python3` or `nc` in the tool image. Probes do not support proxies; proxy variables
  in the step's environment or tool container make that context unsupported and its work waits. Location-only labels
  can be removed after agents report reachability; keep labels that restrict access to credentials. See
  [upgrading](docs/worker-routing.md#upgrading) and [how an agent probes](docs/worker-routing.md#how-a-probe-connects).

New in 0.6.2:

- [Store keys](docs/workflows.md#store-keys) follow the lab repository's hierarchy. The deploy wizard generates a guest's
  secrets at `proxmox/<installation>/<guest>/<secret>` and a service's at `proxmox/<installation>/<guest>/<service>/<secret>`,
  in place of `guests/<lab>/…` and `services/<lab>/…`, and suggests `proxmox/<installation>/<guest>/host-key` for a host key.
  Keys generated before keep working, since definitions name them.
- A key under **Variables** can be deleted once nothing needs it, even after runs used it. Its values go, the record of
  which attempt received which version stays, and a key set again continues its version numbers. What attempts report
  is checked for secrets as it is stored. It adds the `DeletedStoreKeys` and `AttemptResultReceipts` migrations
  ([deleting a key](docs/credential-lifecycle.md)).

## Direction

Octopushy is becoming an engine only. Workflows live in their own repository,
`jcarcaboso/octopushy-lab`, where each whole workflow is versioned `M.m` and released by tags; `homelab-config` holds only
data, and names that repository in its `octopushy.yaml`; and every change made in the UI becomes a commit. The plan and its
phases are in [workflows migration](docs/workflows-migration.md).

Service workflows, the NixOS modules and their tests live in `jcarcaboso/octopushy-lab`.

## Layout

| Path | What |
|---|---|
| [`backend/`](backend) | ASP.NET Core (.NET 10): REST API, agent channel (gRPC over mutual TLS), orchestrator, Git, variable store |
| [`frontend/`](frontend) | Vite + React + TypeScript, served by Caddy, which forwards `/api` to the backend |
| [`agent/`](agent) | Rust: the `octopushy` binary (agent mode) and Octopushy's own Proxmox API library |
| [`contracts/`](contracts) | What the parts share: the agent protocol, executor inputs and outputs, JSON Schemas for definitions, the OpenAPI document |
| [`examples/lab/`](examples/lab) | An example lab repository, with two workflows: `proxmox-vm` (the machine only) and `machine-tools` (the machine, set up by `modules/ansible/machine-tools`) |
| [`tests/`](tests) | End-to-end harnesses and offline checks |
| [`deploy/`](deploy) | Compose project for the control host, and the Proxmox LXC installer |
| [`docs/`](docs) | Design notes, ADRs and operating guides |

## Development

Requirements: .NET SDK **10.0.112** (strictly selected by `global.json`),
Rust **1.98.1** (tested toolchain, including cargo, rustfmt, and clippy), Node 24,
pnpm (version pinned in `frontend/package.json`), Python 3, Bash, podman, Git,
and [just](https://just.systems). The Rust workspace's declared MSRV remains 1.88.
Install frontend dependencies with `pnpm --dir frontend install --frozen-lockfile`.

```sh
just dev-db        # PostgreSQL in rootless podman on 127.0.0.1:55432
just backend       # API on 127.0.0.1:8080, agent endpoint on :8443
just frontend      # http://127.0.0.1:5173
```

On first start the backend writes a one-time setup code to `.dev/server/setup-code`;
the web interface asks for it to create the account. Then:

1. Under **Repository**, point Octopushy at a lab repository (a local path works; copy
   `examples/lab` into a Git repository to start).
2. Under **Variables**, set the store keys the definitions reference, such as the
   distinct read-only and privileged Proxmox API tokens
   ([credential roles](docs/proxmox-credentials.md), [credential lifecycle](docs/credential-lifecycle.md)).
3. Under **Agents**, create an enrollment token, then
   `just agent-enroll <token>` and `just agent`.

`just test` runs the disposable backend, Rust, frontend and offline Python suites.
Backend integration tests need a Docker-compatible API (Docker, or a Podman API
socket via `DOCKER_HOST`); Testcontainers creates its own PostgreSQL container and
each test creates a fresh database. They do **not** use `just dev-db`.
`just e2e` runs the separate end-to-end test: the backend and a real agent provision
and retire a test guest with OpenTofu, against a mock Proxmox API. `just e2e-ansible`
and `just e2e-pipelines` do the same for guest setup and service pipelines.
`just demo` leaves that setup running at http://127.0.0.1:5174 (user `demo`, password
`octopushy-demo`) with manual approval, to try the web interface without a Proxmox
installation. `just openapi` regenerates the OpenAPI document and the frontend's client
after an API change. `just validate <dir>` checks a lab repository offline, the way the server reads it, with the
workflows its `octopushy.yaml` names or `--workflows <git url|path>`, and fails on any error; `--workflows-only <dir>`
checks a workflows repository alone. The same validator is published as the `validate-<version>` image, for the CI of
both repositories ([validating offline](docs/workflows.md#validating-offline)). `just proxmox-e2e` runs the [real Proxmox end-to-end test](tests/proxmox/README.md).

### CI

[`.github/workflows/checks.yml`](.github/workflows/checks.yml) runs on pushes to `main` and
on every pull request. It runs the backend tests; locked Rust tests, formatting and clippy;
frozen pnpm tests, typecheck, lint and build; the offline Python checks and shell syntax;
and drift checks for the OpenAPI document and the typed client. It also runs `cargo audit`,
`pnpm audit` and an audited NuGet restore that fails on findings. `just test` and `just check`
run the same checks locally.

CI uses disposable hosted runners, read-only repository permissions, and no secrets.
It never runs `dev-db`, `demo`, the mock end-to-end harness, or the opt-in real Proxmox
harnesses.

## Deployment

`just images <version>` builds the backend, frontend, agent and validator images by hand, and
[`deploy/`](deploy/README.md) runs them with PostgreSQL on one host. GitHub Actions
publishes them to Docker Hub for `main` and every `v<version>` tag;
[`deploy/proxmox/`](deploy/proxmox/README.md) installs those in a Proxmox LXC container
with one command, with an internal agent to start with, and updates it with a snapshot
to roll back to.
