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
    cd frontend && npm run dev

# Build the agent and run it against the local backend; enroll first with `just agent-enroll <token>`.
agent:
    cd agent && cargo run --bin octopushy -- agent run --state-dir ../.dev/agent

agent-enroll token:
    cd agent && cargo run --bin octopushy -- agent enroll --server https://127.0.0.1:8443 --token {{token}} --state-dir ../.dev/agent --name dev-agent

# Regenerate the OpenAPI document and the frontend's typed client from it.
openapi:
    dotnet build backend/src/Octopushy.Server
    cd frontend && npm run gen:api

# Run every test suite.
test:
    dotnet test backend/Octopushy.slnx
    cd agent && cargo test --workspace
    cd frontend && npm test

# End to end: backend, a real agent running OpenTofu in rootless podman, and a mock Proxmox API.
e2e: dev-db
    tests/e2e/run.sh

# A local demo with a real agent and a mock Proxmox, at http://127.0.0.1:5174.
demo: dev-db
    tests/e2e/demo.sh

# The VM lifecycle on a real Proxmox host (umbrel by default): create, resize, delete.
# Needs the host's API token in .dev/secrets/umbrel-proxmox-token; see tests/proxmox/README.md.
proxmox-e2e: dev-db
    tests/proxmox/run.sh

# Build the container images by hand, tagged with the given version (releases are manual for now).
images version:
    podman build -f backend/Containerfile -t octopushy-backend:{{version}} .
    podman build -f frontend/Containerfile -t octopushy-frontend:{{version}} .
