#!/usr/bin/env bash
# Runs inside the Octopushy LXC container; install.sh pushes it there with compose.yaml.
# Installs Docker Engine and the Compose plugin from Docker's repository, writes .env and
# the variable store's key on the first run, and starts the Compose project in
# /opt/octopushy with the internal agent. Running it again keeps the existing .env and
# key.
set -euo pipefail

dir=/opt/octopushy
secrets=/etc/octopushy/secrets
version=${OCTOPUSHY_VERSION:?set OCTOPUSHY_VERSION}
images=${OCTOPUSHY_IMAGES:?set OCTOPUSHY_IMAGES}
agent_host=${OCTOPUSHY_AGENT_HOST:?set OCTOPUSHY_AGENT_HOST}

[[ -f $dir/compose.yaml ]] || { echo "$dir/compose.yaml is missing" >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get upgrade -yq
# unattended-upgrades applies Debian security updates; Docker's packages are left alone.
apt-get install -yq --no-install-recommends ca-certificates curl gzip unattended-upgrades

if ! command -v docker >/dev/null; then
  install -d -m 0755 /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  # shellcheck source=/dev/null
  codename=$(. /etc/os-release && echo "$VERSION_CODENAME")
  cat >/etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/debian
Suites: $codename
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  apt-get update -q
  apt-get install -yq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
systemctl enable --now docker

# `octopushy-compose <command>` runs docker compose on the Octopushy project from anywhere.
cat >/usr/local/bin/octopushy-compose <<EOF
#!/bin/sh
cd $dir && exec docker compose "\$@"
EOF
chmod 0755 /usr/local/bin/octopushy-compose

# `octopushy-agent-enroll [token]` enrolls the internal agent with a one-time token from
# the web interface (Agents); without an argument it asks for the token.
cat >/usr/local/bin/octopushy-agent-enroll <<'SCRIPT'
#!/bin/sh
set -eu
if [ "$#" -gt 1 ]; then
    echo "usage: octopushy-agent-enroll [token]" >&2
    exit 2
fi
token=${1:-}
if [ -z "$token" ]; then
    printf 'Enrollment token: ' >&2
    read -r token
fi
cd /opt/octopushy
OCTOPUSHY_ENROLLMENT_TOKEN=$token docker compose run --rm --no-deps -e OCTOPUSHY_ENROLLMENT_TOKEN \
    agent agent enroll --server https://backend:8443 --name "${OCTOPUSHY_AGENT_NAME:-internal}"
echo "Enrolled. The agent connects within a few seconds and shows up under Agents."
SCRIPT
chmod 0755 /usr/local/bin/octopushy-agent-enroll

# The variable store's key, on its own volume (mp0) that vzdump leaves out.
install -d -m 0700 "$secrets"
if [[ ! -s $secrets/kek ]]; then
  head -c 32 /dev/urandom >"$secrets/kek"
  # Readable inside the backend container, which runs as its own user; the directory stays private.
  chmod 0444 "$secrets/kek"
fi

if [[ ! -f $dir/.env ]]; then
  (
    umask 077
    cat >"$dir/.env" <<EOF
# Written by setup-container.sh. octopushy-update changes the image lines.
POSTGRES_PASSWORD=$(od -An -tx1 -N24 /dev/urandom | tr -d ' \n')
# The host name or address agents use to reach the control host.
OCTOPUSHY_AGENT_HOST=$agent_host
OCTOPUSHY_AGENT_PORT=8443
# This container is the control host, so the web interface listens on every address.
# It is plain HTTP: put HTTPS in front of it (deploy/proxmox/README.md).
OCTOPUSHY_UI_BIND=0.0.0.0
OCTOPUSHY_UI_PORT=8081
OCTOPUSHY_KEK_FILE=$secrets/kek
OCTOPUSHY_BACKEND_IMAGE=$images-backend:$version
OCTOPUSHY_FRONTEND_IMAGE=$images-frontend:$version
# The internal agent: enroll it with octopushy-agent-enroll.
COMPOSE_PROFILES=agent
OCTOPUSHY_AGENT_IMAGE=$images-agent:$version
EOF
  )
fi

cd "$dir"
docker compose pull
docker compose up -d --remove-orphans

echo "Waiting for Octopushy to answer"
port=$(sed -n 's/^OCTOPUSHY_UI_PORT=//p' .env)
for _ in $(seq 90); do
  curl -fsS "http://127.0.0.1:${port:-8081}/api/health" >/dev/null 2>&1 && break
  sleep 2
done
if ! curl -fsS "http://127.0.0.1:${port:-8081}/api/health" >/dev/null 2>&1; then
  docker compose ps
  docker compose logs --tail 50 migrate backend
  echo "Octopushy did not become healthy" >&2
  exit 1
fi

# The setup code exists until the account is created.
if code=$(docker compose exec -T backend cat /var/lib/octopushy/setup-code 2>/dev/null); then
  echo "Setup code: $code"
fi
if ! docker compose run --rm --no-deps agent agent status >/dev/null 2>&1; then
  echo "The internal agent waits for its enrollment: octopushy-agent-enroll <token>"
fi
