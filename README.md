# Octopushy

A self-hosted operations control plane for a homelab: Proxmox guests provisioned with
OpenTofu, from definitions kept in Git, with previews, approvals where a resource asks
for them, and a visible record of every step. The design is in the
[architecture agreement](docs/architecture-agreement.md) and the
[lab repository proposal](docs/lab-repository.md); the vocabulary is in
[CONTEXT.md](CONTEXT.md).

**Status:** milestone 1, Proxmox and virtual machines
([scope and contracts](docs/milestone-1.md)). Guests are planned, approved, applied,
verified, and retired; installations are observed through the Proxmox API. Guest setup,
services, DNS, and the editor come later. The first Python proof of concept is archived
in [`old/`](old/README.md).

## Layout

| Path | What |
|---|---|
| [`backend/`](backend) | ASP.NET Core (.NET 10): REST API, agent channel (gRPC over mutual TLS), orchestrator, Git, variable store |
| [`frontend/`](frontend) | Vite + React + TypeScript, served by Caddy, which forwards `/api` to the backend |
| [`agent/`](agent) | Rust: the `octopushy` binary (agent mode) and Octopushy's own Proxmox API library |
| [`contracts/`](contracts) | What the parts share: the agent protocol, executor inputs and outputs, JSON Schemas for definitions, the OpenAPI document |
| [`examples/lab/`](examples/lab) | An example lab repository with the `proxmox-vm` recipe |
| [`deploy/`](deploy) | Compose project for the control host |

## How it fits together

1. The backend fetches the lab repository's branch, reads each commit's definitions, and
   validates them (one JSON Schema per kind, plus reference checks).
2. A guest whose inputs changed gets a provision run: **plan** (OpenTofu, on an agent),
   **approval** (automatic or manual, per the guest's policy; deletes follow the
   destructive setting), **apply** of exactly the approved plan, and **verify** through
   the Proxmox API. A retired guest gets a destroy plan, protection lifted, apply, and a
   check that the VM is gone.
3. Agents connect outbound, enroll with a one-time token, and run each attempt with only
   the secrets that attempt needs. OpenTofu runs in a rootless container on the agent.
4. Each installation is observed every few minutes, so the inventory shows what Proxmox
   reports next to what Git declares, including VMs Octopushy does not manage.

## Development

Requirements: .NET 10 SDK, Rust (cargo), Node 24, podman, Git, and [just](https://just.systems).

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
   Proxmox API token.
3. Under **Agents**, create an enrollment token, then
   `just agent-enroll <token>` and `just agent`.

`just test` runs every test suite, and `just e2e` the end-to-end test: the backend and
a real agent provision and retire a test guest with OpenTofu, against a mock Proxmox API.
`just demo` leaves that setup running at http://127.0.0.1:5174 (user `demo`, password
`octopushy-demo`) with manual approval, to try the web interface without a Proxmox
installation. `just openapi` regenerates the OpenAPI document and the frontend's client
after an API change.

## Deployment

Releases are built by hand for now: `just images <version>` builds the backend and
frontend images, and [`deploy/`](deploy/README.md) runs them with PostgreSQL on one
host. Agents run where they can reach the labs; one agent can serve every lab.
