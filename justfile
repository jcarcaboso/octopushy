# Development tasks. `just --list` shows them all.

dev_db := "octopushy-dev-pg"

default:
    @just --list

# Start the development PostgreSQL (rootless podman, port 55432 on localhost).
dev-db:
    podman container exists {{dev_db}} \
      && podman start {{dev_db}} \
      || podman run -d --name {{dev_db}} -p 127.0.0.1:55432:5432 \
           -e POSTGRES_USER=octopushy -e POSTGRES_PASSWORD=octopushy -e POSTGRES_DB=octopushy \
           -v octopushy-dev-pg:/var/lib/postgresql docker.io/library/postgres:18-alpine

# Run the backend against the development database (API on 127.0.0.1:8080, agents on :8443).
backend:
    ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/src/Octopushy.Server

# Run the frontend dev server on http://127.0.0.1:5173, forwarding /api to the backend.
frontend:
    cd frontend && pnpm run dev

# The frontend on http://127.0.0.1:5176 against a mock API with realistic data: no backend, database or agent (frontend/mock).
mock:
    #!/usr/bin/env bash
    set -euo pipefail
    cd frontend
    port="${MOCK_PORT:-18280}"
    MOCK_PORT="$port" node mock/server.ts & trap 'kill $!' EXIT
    OCTOPUSHY_API="http://127.0.0.1:$port" pnpm run dev --port 5176

# Build the agent and run it against the local backend; enroll first with `just agent-enroll <token>`.
agent:
    cd agent && cargo run --bin octopushy -- agent run --state-dir ../.dev/agent

agent-enroll token:
    cd agent && cargo run --bin octopushy -- agent enroll --server https://127.0.0.1:8443 --token {{token}} --state-dir ../.dev/agent --name dev-agent

# Regenerate the OpenAPI document and the frontend's typed client from it.
openapi:
    dotnet build backend/src/Octopushy.Server --no-incremental
    cd frontend && pnpm run gen:api

# Validate a lab repository offline, as Octopushy reads it, with the workflows its octopushy.yaml names
# (extra arguments pass through, such as `--workflows <git url|path>`, or `--workflows-only <dir>` in place of <dir>).
[no-cd]
validate dir *args:
    dotnet run --project "{{justfile_directory()}}/backend/src/Octopushy.Validate" -- "{{dir}}" {{args}}

# Run disposable tests only (Testcontainers needs Docker or a Podman API socket).
test:
    dotnet test backend/Octopushy.slnx
    cd agent && cargo test --workspace --locked
    tests/tool-users/run.sh
    cd frontend && pnpm test
    bash scripts/checks/offline.sh

# Static validation and generated-contract drift, without running a deployment.
check:
    scripts/release.sh --check
    scripts/checks/toolchains.sh
    dotnet format backend/Octopushy.slnx --verify-no-changes
    cd agent && cargo fmt --all -- --check
    cd agent && cargo clippy --workspace --all-targets --locked -- -D warnings
    cd frontend && pnpm run typecheck
    cd frontend && pnpm run lint
    cd frontend && pnpm run build
    just openapi
    git diff --exit-code -- contracts/openapi/octopushy.json frontend/src/api/schema.d.ts

# The agent's root-only tests (tools as users of their own), as root in a container. AGENT_IMAGE=<image> also checks a built agent image.
test-tool-users:
    tests/tool-users/run.sh

# Environment: OCTOPUSHY_EVAL_RUNS=3, OCTOPUSHY_EVAL_ONLY=burst,outbox (agent/evals/README.md).
# The agent's performance evals and their invariants; fails on a failed check or a regression from agent/evals/baseline.json.
agent-evals:
    cd agent && cargo test --release --locked -p octopushy --test evals -- --ignored --nocapture

# Measure the agent five times and save the result as agent/evals/baseline.json.
agent-evals-baseline:
    cd agent && OCTOPUSHY_EVAL_SAVE_BASELINE=1 OCTOPUSHY_EVAL_RUNS="${OCTOPUSHY_EVAL_RUNS:-5}" \
      cargo test --release --locked -p octopushy --test evals -- --ignored --nocapture

# End to end: backend, a real agent running OpenTofu in rootless podman, and a mock Proxmox API.
e2e: dev-db
    tests/e2e/run.sh

# End to end: a real ansible-playbook configuring a throwaway SSH container, on the given runtime.
e2e-ansible runtime="podman":
    E2E_TOOL_RUNTIME={{runtime}} tests/ansible/run.sh

# End to end: a service's pipeline with real OpenTofu and a real ansible-playbook, on the given runtime.
e2e-pipelines runtime="podman":
    E2E_TOOL_RUNTIME={{runtime}} tests/pipelines/run.sh

# A local demo with a real agent and a mock Proxmox, at http://127.0.0.1:5174.
demo: dev-db
    tests/e2e/demo.sh

# The VM lifecycle on a real Proxmox host (umbrel by default): create, resize, delete.
# Needs explicit PVE_READ_ONLY_TOKEN_FILE and PVE_PRIVILEGED_TOKEN_FILE.
# Live rollout is blocked on read-only provider refresh; see tests/proxmox/README.md.
proxmox-e2e: dev-db
    tests/proxmox/run.sh

# Prepare a release: set the version (MAJOR.MINOR.PATCH) in every file that carries it (scripts/release.sh).
release version:
    scripts/release.sh {{version}}

# Build the container images by hand, tagged with the given version (a v<version> tag publishes a release: .github/workflows/images.yml).
images version:
    podman build -f backend/Containerfile -t octopushy-backend:{{version}} .
    podman build -f frontend/Containerfile -t octopushy-frontend:{{version}} .
    podman build -f agent/Containerfile -t octopushy-agent:{{version}} .
    podman build -f backend/src/Octopushy.Validate/Containerfile -t octopushy-validate:{{version}} .
