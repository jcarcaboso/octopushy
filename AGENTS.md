# Working on Octopushy

## Code map

- `backend/src/Octopushy.Server/` is the ASP.NET API, agent channel, database and
  orchestration engine. `Orchestration/RunEngine*.cs` owns run transitions.
  `Git/` reads repository revisions. `Changes/` writes and reconciles changes.
  `Notifications/` records what operators are told; see `docs/notifications.md`.
- `backend/src/Octopushy.Definitions/` parses and validates lab and workflow files.
- `backend/src/Octopushy.Validate/` is the offline repository validator.
- `backend/tests/Octopushy.Tests/` holds definition and server tests.
- `agent/crates/octopushy/` is the Rust agent and its executors. `proto/` generates
  the gRPC types, `proxmox/` is the API client, and `mock-proxmox/` is its test server.
- `contracts/` holds `agent/v1/agent.proto`, `agent/v1/executors.md`, the OpenAPI
  document and the JSON schemas. `frontend/src/api/schema.d.ts` is generated.
- `frontend/` is the React UI. `tests/` holds shell end-to-end suites and fixtures.
- `deploy/` holds Compose, Caddy and the Proxmox installer.
- `CONTEXT.md` defines domain terms. `docs/adr/` records decisions.
  `docs/workflows.md` and `docs/safety-boundary.md` describe the current system.
  `docs/architecture-agreement.md` and `docs/workflows-migration.md` are history.

## Commands

Run from the repository root. Use `pnpm`, not npm. Prefer `rg` to grep and `fzf`
to find when available.

- `just --list` lists the development tasks.
- `just dev-db` starts a development PostgreSQL with Podman.
- `just backend` runs the API on port 8080 and the agent channel on 8443.
- `just frontend` runs Vite on port 5173, proxying `/api` to the backend.
- `just mock` runs the UI with the mock API and no backend or agent.
- `just agent-enroll <token>` enrolls a development agent; `just agent` runs it.
- `just validate examples/lab` validates a lab offline.
- `just test` runs the backend, agent, frontend and offline shell checks.
- `dotnet test backend/Octopushy.slnx --filter FullyQualifiedName~ProvisioningTests`
  runs one backend test class. Server tests use Testcontainers and need a working
  Docker daemon or Podman API socket. They create disposable PostgreSQL containers.
- `cd agent && cargo test --workspace --locked` runs the Rust tests.
- `cd frontend && pnpm test` runs the frontend tests.
- `just check` runs static checks, builds and generated-contract drift checks.
- `just openapi` regenerates the OpenAPI document and frontend client together.
- `just e2e`, `just e2e-ansible` and `just e2e-pipelines` run disposable integration
  suites. Read the corresponding `tests/` README before running them.
- `just proxmox-e2e` targets real infrastructure. Do not run it without explicit
  authorization and the credentials its README requires.

## CI gates

`.github/workflows/checks.yml` runs backend tests, offline repository validation,
C# formatting (`dotnet format`), OpenAPI drift, Rust tests, rustfmt and clippy
with warnings as errors, frontend tests, typecheck, lint, build and
generated-client drift, and dependency audits.
Backend schema tests check the EF model against the migrations. Regenerate both
the OpenAPI document and client when an API changes. Do not hand-edit the client.

## Rules that refactors must keep

- A change is never retried and normal cancellation never interrupts it. Only an
  explicit operator Force stop may interrupt one, on an agent advertising
  `forced-mutation-stop-v1`. A lost lease means uncertain, and one late result may
  still settle it. An assignment the server never began to write to the stream was
  never sent, so putting it back in the queue is not a retry. That is decided by the
  server's own writer alone (`AgentSession.WriteAllAsync`), never by anything an agent
  says: once a write began, a lost assignment is uncertain.
- One writer for runs. Writes to runs, steps and attempts go through the engine
  lock in `RunEngine`. Do not replace it with row versions. Atomic, conditional
  heartbeat lease renewals are the narrow exception.
- Check the approved plan's hash before applying it.
- Keep variable-store `system/` keys away from operator endpoints and definitions.
- Statuses, decisions and policy values are constants. Never infer state by
  comparing message text.
- Contracts live in `contracts/`. Changes to the proto, executor inputs or API
  ship with their contract changes in the same PR.
- Repository content and tool output are hostile input.
- Migrations are append-only. Never squash them. `SchemaHistory` refuses a
  database containing a migration id the assembly does not know.
- Reuse existing code and dependencies. Keep fixes focused and add regression
  tests for the behavior being changed.
