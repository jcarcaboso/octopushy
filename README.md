# Octopushy

A self-hosted operations control plane for a homelab: Proxmox guests provisioned with
OpenTofu, from definitions kept in Git, with previews, approvals where a resource asks
for them, and a visible record of every step. The design is in the
[architecture agreement](docs/architecture-agreement.md) and the
[lab repository proposal](docs/lab-repository.md); the vocabulary is in
[CONTEXT.md](CONTEXT.md).

For the dated progress snapshot, validation boundaries, and proposed implementation
sequence, see [Progress and next steps](docs/progress-and-next-steps.md).

**Status:** milestone 1, Proxmox and virtual machines
([scope and contracts](docs/milestone-1.md)). Guests are planned, approved, applied,
verified, and retired; installations are observed through the Proxmox API. Guest setup,
services, DNS, and the editor come later. The first Python proof of concept is archived
in [`old/`](old/README.md).

An opt-in [NixOS root-filesystem growth workflow](docs/guest-filesystem-growth.md)
adds separate native growth and capacity-verification steps, with targeted retry.
This is limited to the documented ext4 base-image layout, not general guest setup
or completed service orchestration.

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
   validates them (one JSON Schema per kind, plus reference checks). The operator
   [reviews and trusts the exact revision](docs/trusted-revisions.md) before
   credential-bearing work can run; discovery alone does not grant execution trust.
2. A guest whose inputs changed gets a provision run: **plan** (OpenTofu, on an agent),
   **approval** (automatic or manual, per the guest's policy; deletes follow the
   destructive setting), **apply** of exactly the approved plan, and **verify** through
   the Proxmox API. A retired guest gets a destroy plan, protection lifted, apply, and a
   check that the VM is gone.
3. Agents connect outbound, enroll with a one-time token, and run each attempt with only
   the secrets that attempt needs. OpenTofu runs in a rootless container on the agent.
4. Each installation is observed every few minutes, so the inventory shows what Proxmox
   reports next to what Git declares, including VMs Octopushy does not manage.
   Scheduled observations use the latest eligible trusted revision, which may be
   behind the discovered head. Execution trust is separate from apply approval.

The Proxmox VM recipe supports [plain DHCP, externally reserved DHCP, and static
IPv4](docs/vm-networking.md). A reserved DHCP address is checked during guest
verification; Octopushy does not configure the DHCP server or create its reservation.

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
   distinct read-only and privileged Proxmox API tokens.
3. Under **Agents**, create an enrollment token, then
   `just agent-enroll <token>` and `just agent`.

Existing secret replacements now stage a candidate instead of immediately
changing active credentials. **Variables → Versions** provides explicit
promotion/revocation and usage history; runs pin exact versions before execution.
See [credential lifecycle](docs/credential-lifecycle.md) for verification limits,
upgrade behavior, and encryption-key recovery.

The [Proxmox credential-role guide](docs/proxmox-credentials.md) describes explicit
role routing, approval gates, legacy-run handling, and the pinned provider's
read-only refresh blocker. This routing foundation is not live rollout approval.

The [remote-worker acceptance mode](tests/remote-worker/README.md) uses a
separately enrolled worker and dedicated existing controller/state backend.
It does not install infrastructure or prove remote connectivity by itself.

`just test` runs the disposable backend/Rust/frontend and offline Python suites.
Backend integration tests need a Docker-compatible API (Docker, or a Podman API
socket via `DOCKER_HOST`); Testcontainers creates its own PostgreSQL container and
each test creates a fresh database. They do **not** use `just dev-db`.
`just e2e` runs the separate end-to-end test: the backend and
a real agent provision and retire a test guest with OpenTofu, against a mock Proxmox API.
`just demo` leaves that setup running at http://127.0.0.1:5174 (user `demo`, password
`octopushy-demo`) with manual approval, to try the web interface without a Proxmox
installation. `just openapi` regenerates the OpenAPI document and the frontend's client
after an API change.

A reusable native [Fedora setup/service workflow](examples/lab/modules/ansible/fedora/README.md)
is also available for explicit Ansible execution. It uses rootless Docker,
independent Compose projects, owned port rules, and external HTTP verification.
It is not yet a service executor or a scheduled operation in Octopushy.

The [native NixOS workflow](examples/lab/modules/nixos/README.md) prepares a pinned
Proxmox guest baseline, a separate bootstrap image, and explicit instructions to
build, copy, activate, and verify an exact system closure. An optional
[Technitium role](examples/lab/modules/nixos/services/README.md) targets a dedicated
DNS VM: it is disabled by default, keeps the web console private, and requires a
separate post-bootstrap opt-in before admitting LAN DNS traffic. NixOS activation
is a whole-guest operation, not an independent service deployment.
The pinned Technitium package does not meet the role's known-fix minimum, so LAN
exposure is blocked until a patched package is separately selected and tested.
The [two-VM DNS cluster preparation](examples/lab/modules/nixos/services/CLUSTER.md)
places primary and secondary guests on the same Proxmox host as an explicit
provisioning choice, with separate peer/client exposure switches. It does not
enroll members or provide availability across a host failure.
These are native preparation examples, not controller executors or active lab
definitions. Their local checks do not prove image boot, Proxmox provisioning,
or a live DNS deployment; no existing VMs or DNS settings are adopted.

A reusable [NixOS Docker service-node and Portainer role](examples/lab/modules/nixos/services/PORTAINER.md)
is available with a [live end-to-end test](tests/portainer/README.md). The test uses
Octopushy for VM provisioning, then native NixOS/Docker for service setup and
verification. Service operations are **not yet controller executors**.

### Baseline checks / CI

`.github/workflows/checks.yml` checks pushes and pull requests **to `main`**,
explicitly rather than relying on the repository's current `proxmox-e2e` default.
It runs backend tests; locked Rust tests, formatting and clippy; frozen pnpm
tests/typecheck/lint/build; offline Python mocks and shell syntax; regeneration
drift checks for both the OpenAPI document and typed client; and locked Nix flake
evaluation without building an image or activating a host.

After installing dependencies, `just test`, `just check`, and `just check-nix`
(requires Nix) run those checks locally. CI also runs `cargo audit` (cargo-audit
0.22.2), `pnpm --dir frontend audit`, and NuGet vulnerability reporting plus an
audited restore that treats audit warnings/findings as errors. Audits include
transitive and development dependencies, use current public advisory feeds, and
do not silently update package versions or suppress findings.

CI uses disposable hosted runners, read-only repository permissions, and no
production secrets. It never runs `dev-db`, `demo`, the shared-development-DB
mock e2e harness, or the opt-in real Proxmox/Portainer/remote-worker harnesses.
Ignored Podman/OpenTofu Rust smoke tests and optional real-CLI archived Python
tests stay opt-in. Nix/static firewall checks do not prove guest boot, runtime
packet paths, remote access, or deployment success.

## Deployment

Releases are built by hand for now: `just images <version>` builds the backend and
frontend images, and [`deploy/`](deploy/README.md) runs them with PostgreSQL on one
host. Agents run where they can reach the labs; one agent can serve every lab.
