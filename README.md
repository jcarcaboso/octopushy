# Octopushy

A self-hosted operations control plane for a homelab. Definitions live in Git; Octopushy
plans each change, waits for approval where a resource's policy asks for it, applies
exactly what was approved through agents, and keeps a visible record of every step.
The vocabulary is in [CONTEXT.md](CONTEXT.md).

## Status

Milestone 1 has shipped, up to release 0.6.1:

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
