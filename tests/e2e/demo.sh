#!/usr/bin/env bash
# A local demo with real work and no real Proxmox: the backend, an agent running OpenTofu in
# rootless podman, the mock Proxmox API, and the frontend dev server. The lab asks for manual
# approval, so the first plan waits in the web interface. Everything keeps running until Ctrl-C.
# Needs the development PostgreSQL (`just dev-db`). Logs go to .dev/demo.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$root/.dev/demo
api=http://127.0.0.1:18080
pg=(podman exec octopushy-dev-pg psql -q -U octopushy -d octopushy -v ON_ERROR_STOP=1)
pids=()
trap 'kill "${pids[@]}" 2>/dev/null || true; wait 2>/dev/null || true' EXIT

rm -rf "$work" && mkdir -p "$work"
dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
(cd "$root/agent" && cargo build --quiet --bin octopushy --bin mock-proxmox)
"${pg[@]}" -c "DROP DATABASE IF EXISTS demo_octopushy" -c "CREATE DATABASE demo_octopushy"

cp -r "$root/tests/e2e/lab" "$work/lab"
printf 'policy:\n  approval: manual\n' >>"$work/lab/labs/e2e/lab.yaml"
git -C "$work/lab" init -q -b main
git -C "$work/lab" add -A
git -C "$work/lab" -c user.name=demo -c user.email=demo@example.invalid commit -q -m "Initial definitions"

"$root/agent/target/debug/mock-proxmox" --listen 127.0.0.1:18006 --fixture "$root/tests/e2e/proxmox.json" \
  --tls-self-signed >"$work/mock.log" 2>&1 &
pids+=($!)
(
  cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
  ConnectionStrings__Octopushy="Host=127.0.0.1;Port=55432;Database=demo_octopushy;Username=octopushy;Password=octopushy" \
  Octopushy__DataDirectory="$work/server" Octopushy__ApiPort=18080 \
  Octopushy__AgentChannel__Port=18443 Octopushy__AgentChannel__BindAddress=127.0.0.1 \
  Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:18443 Octopushy__MigrateOnStart=true \
  Octopushy__Scheduling__VerifyWaitSeconds=30 \
  exec ./octopushy-server
) >"$work/backend.log" 2>&1 &
pids+=($!)
for _ in $(seq 1 60); do curl -sf "$api/api/health" >/dev/null && break; sleep 1; done

jar=$work/cookies
call() { curl -sf -b "$jar" -c "$jar" -H 'Content-Type: application/json' "$@"; }
code=$(tr -d '\n' <"$work/server/setup-code")
call -X POST "$api/api/auth/setup" -d "{\"setupCode\":\"$code\",\"username\":\"demo\",\"password\":\"octopushy-demo\"}" >/dev/null
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/read-only-token","value":"octopushy-reader@pve!e2e-read-only=00000000-0000-4000-8000-0000000000e1","visibility":"secret","description":"Read-only identity for the mock Proxmox"}'
call -X PUT "$api/api/variables" -d '{"key":"proxmox/korriban/privileged-token","value":"octopushy-writer@pve!e2e-privileged=00000000-0000-4000-8000-0000000000e2","visibility":"secret","description":"Privileged identity for the mock Proxmox"}'
call -X PUT "$api/api/repository" -d "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null
token=$(call -X POST "$api/api/agents/tokens" -d '{"name":"demo-agent","labels":["e2e"]}' | jq -r .token)
OCTOPUSHY_ENROLLMENT_TOKEN=$token "$root/agent/target/debug/octopushy" agent enroll \
  --server https://127.0.0.1:18443 --name demo-agent --state-dir "$work/agent" >"$work/enroll.log" 2>&1
"$root/agent/target/debug/octopushy" agent run --state-dir "$work/agent" --tool-runtime podman \
  >"$work/agent.log" 2>&1 &
pids+=($!)
(cd "$root/frontend" && OCTOPUSHY_API=$api exec pnpm exec vite --port 5174 --strictPort) >"$work/frontend.log" 2>&1 &
pids+=($!)

cat <<INFO

Octopushy demo: http://127.0.0.1:5174  (user demo, password octopushy-demo)
First trust the Proxmox installation on its Inventory page; this permits
planning, not apply. The first plan then waits for your separate approval.
The lab repository is $work/lab: edit and commit there to see new proposed work.
Ctrl-C stops everything.
INFO
wait
