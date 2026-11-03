# Octopushy

A self-hosted operations control plane for a homelab. Definitions live in Git; Octopushy
plans each change, waits for approval where a resource's policy asks for it, applies
exactly what was approved through agents, and keeps a visible record of every step.
The vocabulary is in [CONTEXT.md](CONTEXT.md).

## Status

Milestone 1 has shipped, up to release 0.4.0:

- Proxmox VMs provisioned, verified and retired with OpenTofu, with state kept by Octopushy.
- [Guest setup with Ansible](docs/guest-configuration.md) once a VM exists, and
  [service pipelines](docs/pipelines.md) of OpenTofu and Ansible stages.
- Actions on a schedule declared in Git, and SSH access keys that Octopushy manages and rotates.
- A [repository editor](docs/repository-editor.md) that publishes drafts as commits.
- Proxmox installations observed through their API, next to what Git declares. An
  installation can be [observed only](docs/safety-boundary.md), and no plan may remove
  a machine that is not its run's own.

## Direction

Octopushy is becoming an engine only. Workflows move to their own repository,
`jcarcaboso/octopushy-lab`, where each whole workflow is versioned `M.m`; `homelab-config` holds only data; and every change made in the UI
becomes a commit. The plan and its phases are in [workflows migration](docs/workflows-migration.md).

Service workflows, the NixOS modules and their tests live in `jcarcaboso/octopushy-lab`.

## Layout

| Path | What |
|---|---|
| [`backend/`](backend) | ASP.NET Core (.NET 10): REST API, agent channel (gRPC over mutual TLS), orchestrator, Git, variable store |
| [`frontend/`](frontend) | Vite + React + TypeScript, served by Caddy, which forwards `/api` to the backend |
| [`agent/`](agent) | Rust: the `octopushy` binary (agent mode) and Octopushy's own Proxmox API library |
| [`contracts/`](contracts) | What the parts share: the agent protocol, executor inputs and outputs, JSON Schemas for definitions, the OpenAPI document |
| [`examples/lab/`](examples/lab) | An example lab repository: the `proxmox-vm` and `machine-tools` recipes |
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
after an API change. `just proxmox-e2e` runs the [real Proxmox end-to-end test](tests/proxmox/README.md).

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

`just images <version>` builds the backend, frontend, and agent images by hand, and
[`deploy/`](deploy/README.md) runs them with PostgreSQL on one host. GitHub Actions
publishes them to Docker Hub for `main` and every `v<version>` tag;
[`deploy/proxmox/`](deploy/proxmox/README.md) installs those in a Proxmox LXC container
with one command, with an internal agent to start with, and updates it with a snapshot
to roll back to.
