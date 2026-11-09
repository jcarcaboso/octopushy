# Octopushy

A self-hosted operations control plane for a homelab. Definitions live in Git; Octopushy
plans each change, waits for approval where a resource's policy asks for it, applies
exactly what was approved through agents, and keeps a visible record of every step.
The vocabulary is in [CONTEXT.md](CONTEXT.md).

## Status

Milestone 1 has shipped, up to release 0.7.2:

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
