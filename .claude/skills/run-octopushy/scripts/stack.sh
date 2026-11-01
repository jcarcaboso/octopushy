#!/usr/bin/env bash
# Octopushy from this checkout, without an agent: a throwaway PostgreSQL, the server, and the
# Vite dev server, seeded with an administrator, a few store keys and a copy of the e2e lab.
# Unlike `just demo` it needs no Rust build, keeps nothing, and returns once the app is up.
#
#   stack.sh up        Build, start and seed; refuses if a port is already taken.
#   stack.sh down      Stop everything this checkout started.
#   stack.sh status    Show what is running.
#   stack.sh api METHOD PATH [JSON]   Call the API as the seeded administrator.
#   stack.sh browser   Install Playwright and headless Chromium into the shared cache.
#   stack.sh shim      Print a directory holding a `pnpm` shim, for tools that call bare pnpm.
set -euo pipefail

root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
work=$root/.dev/run
# Shared by every worktree, and not removed by `down`: these are slow to fetch and hold no state.
cache=${XDG_CACHE_HOME:-$HOME/.cache}/octopushy-run
playwright_version=1.64.1

# Apart from `just demo` (18080, 18443, 5174) and the dev database (55432), so the two can coexist.
pg_port=55433
api_port=18180
channel_port=18543
ui_port=5175
pg_container=octopushy-run-pg
api=http://127.0.0.1:$api_port
ui=http://127.0.0.1:$ui_port
user=run
password=octopushy-run
jar=$work/cookies

die() {
  echo "stack.sh: $*" >&2
  exit 1
}

port_taken() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

wait_for() {
  local what=$1 tries=$2
  shift 2
  for _ in $(seq 1 "$tries"); do
    "$@" >/dev/null 2>&1 && return 0
    sleep 1
  done
  die "$what did not come up; see the logs in $work"
}

# pnpm is on PATH on some machines; on others only the version pinned by the frontend is in the store.
pnpm_shim() {
  local shim=$cache/bin version entry
  if command -v pnpm >/dev/null; then
    dirname "$(command -v pnpm)"
    return
  fi
  version=$(jq -r '.packageManager | ltrimstr("pnpm@")' "$root/frontend/package.json")
  entry=$(compgen -G "$HOME/.local/share/pnpm/package-manager-store/*/links/@/pnpm/$version/*/node_modules/pnpm/bin/pnpm.mjs" | head -1) \
    || die "pnpm $version is neither on PATH nor in the local store; install it, not npm"
  mkdir -p "$shim"
  printf '#!/bin/sh\nexec node "%s" "$@"\n' "$entry" >"$shim/pnpm"
  chmod +x "$shim/pnpm"
  echo "$shim"
}

# Its own process group, so `down` also reaches what the command starts.
start() {
  local name=$1
  shift
  setsid "$@" >"$work/$name.log" 2>&1 </dev/null &
  echo $! >"$work/$name.pid"
}

call() {
  local method=$1 path=$2 body=${3:-}
  curl -sS --fail-with-body -b "$jar" -c "$jar" -H 'Content-Type: application/json' -X "$method" "$api$path" ${body:+-d "$body"}
}

put_variable() { call PUT /api/variables "$1" >/dev/null; }

up() {
  local tool port
  for tool in podman dotnet node jq curl git; do
    command -v "$tool" >/dev/null || die "$tool is required"
  done
  for port in $pg_port $api_port $channel_port $ui_port; do
    # Starting anyway would let a check pass against another worktree's code.
    port_taken "$port" && die "port $port is taken: run \`stack.sh down\` in the checkout that started it"
  done

  PATH=$(pnpm_shim):$PATH
  rm -rf "$work"
  mkdir -p "$work/shots"

  podman run -d --rm --name "$pg_container" --label "octopushy.checkout=$root" -p "127.0.0.1:$pg_port:5432" \
    -e POSTGRES_USER=octopushy -e POSTGRES_PASSWORD=octopushy -e POSTGRES_DB=octopushy \
    docker.io/library/postgres:18-alpine >/dev/null
  wait_for PostgreSQL 60 podman exec "$pg_container" pg_isready -q -U octopushy -d octopushy

  dotnet build --nologo -v quiet "$root/backend/src/Octopushy.Server" >"$work/build-backend.log"
  CI=true pnpm --dir "$root/frontend" install --frozen-lockfile >"$work/install-frontend.log" 2>&1

  cp -r "$root/tests/e2e/lab" "$work/lab"
  git -C "$work/lab" init -q -b main
  git -C "$work/lab" add -A
  git -C "$work/lab" -c user.name=run -c user.email=run@example.invalid commit -q -m "Initial definitions"

  (
    cd "$root/backend/src/Octopushy.Server/bin/Debug/net10.0"
    export ConnectionStrings__Octopushy="Host=127.0.0.1;Port=$pg_port;Database=octopushy;Username=octopushy;Password=octopushy"
    export Octopushy__DataDirectory=$work/server Octopushy__ApiPort=$api_port
    export Octopushy__AgentChannel__Port=$channel_port Octopushy__AgentChannel__BindAddress=127.0.0.1
    export Octopushy__AgentChannel__PublicUrl=https://127.0.0.1:$channel_port Octopushy__MigrateOnStart=true
    start backend ./octopushy-server
  )
  wait_for "The server" 60 curl -sf "$api/api/health"

  (
    cd "$root/frontend"
    OCTOPUSHY_API=$api start frontend node_modules/.bin/vite --port "$ui_port" --strictPort
  )
  wait_for "The dev server" 30 curl -sf "$ui/"

  seed
  status
}

seed() {
  local code
  code=$(tr -d '\n' <"$work/server/setup-code")
  call POST /api/auth/setup "{\"setupCode\":\"$code\",\"username\":\"$user\",\"password\":\"$password\"}" >/dev/null

  # Secret values carry a marker, so a check can show they never reach the page.
  put_variable '{"key":"proxmox/korriban/read-only-token","value":"MARKER-secret-read-only","visibility":"secret","description":"Read-only identity"}'
  put_variable '{"key":"proxmox/korriban/privileged-token","value":"MARKER-secret-privileged","visibility":"secret","description":"Privileged identity"}'
  put_variable '{"key":"dns/technitium/api-token","value":"MARKER-secret-technitium","visibility":"secret"}'
  put_variable '{"key":"motd","value":"plain value","visibility":"plain"}'

  call PUT /api/repository "{\"url\":\"$work/lab\",\"branch\":\"main\",\"pollSeconds\":10,\"credentialKind\":\"none\"}" >/dev/null
  wait_for "The lab sync" 60 lab_indexed
}

lab_indexed() {
  call GET /api/variables | jq -e '.entries[] | select(.key == "proxmox/korriban/read-only-token") | .usedBy | length > 0'
}

down() {
  local pid_file
  for pid_file in "$work"/*.pid; do
    [ -e "$pid_file" ] || continue
    kill -- "-$(cat "$pid_file")" 2>/dev/null || true
    rm -f "$pid_file"
  done
  # By label: the name alone would let another worktree's `down` take this checkout's database.
  podman ps -q --filter "name=^$pg_container\$" --filter "label=octopushy.checkout=$root" | xargs -r podman stop -t 2 >/dev/null
  echo "Stopped. Logs and screenshots stay in $work until the next up."
}

status() {
  if ! curl -sf "$api/api/health" >/dev/null; then
    echo "Not running."
  elif [ ! -e "$work/backend.pid" ]; then
    echo "Another checkout's stack is on these ports: it is not running this code."
  else
    cat <<INFO
Octopushy is up: $ui  (user $user, password $password)
Lab repository: $work/lab  Logs and screenshots: $work
INFO
  fi
}

browser() {
  PATH=$(pnpm_shim):$PATH
  mkdir -p "$cache/tool"
  cd "$cache/tool"
  [ -e package.json ] || printf '{"name":"octopushy-run-tool","private":true}\n' >package.json
  pnpm add --save-exact "playwright@$playwright_version" >/dev/null
  node node_modules/playwright/cli.js install chromium-headless-shell
}

case ${1:-} in
up | down | status | browser) "$1" ;;
api)
  shift
  [ $# -ge 2 ] || die "usage: stack.sh api METHOD PATH [JSON]"
  call "$@"
  ;;
shim) pnpm_shim ;;
*)
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
  ;;
esac
