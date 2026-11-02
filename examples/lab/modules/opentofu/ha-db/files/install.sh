#!/usr/bin/env bash
# Runs in the container as root, started by lxc-app's run.sh, which OpenTofu runs over SSH. Sets up
# PostgreSQL 17 from Debian for Home Assistant's recorder, and VictoriaMetrics for its sensors,
# and checks the result. Running it again changes nothing that is already right, loses no data,
# and brings what differs back to what is declared: the passwords included, which is how a
# password is rolled.
#
# The passwords are the files $OCTOPUSHY_SECRETS/postgres_password and victoriametrics_password.
# run.sh opened them from a seal made with a key only this container has, and removes them however
# this ends. They never appear in a command line or a log: psql reads its statements from stdin,
# curl its credentials from a config on stdin, and the rest go into files of mode 0600.
set -euo pipefail

pg_clients=${PG_CLIENTS:?set PG_CLIENTS}
vm_version=${VM_VERSION:?set VM_VERSION}
vm_sha256=${VM_SHA256:?set VM_SHA256}
vm_retention=${VM_RETENTION:?set VM_RETENTION}
address=${OCTOPUSHY_ADDRESS:?run this through lxc-app}
secrets=${OCTOPUSHY_SECRETS:?run this through lxc-app}
app=${OCTOPUSHY_APP:?run this through lxc-app}

pg_version=17
pg_conf=/etc/postgresql/$pg_version/main
pg_service=postgresql@$pg_version-main
pg_socket=/var/run/postgresql
db_name=homeassistant
db_role=homeassistant
dumps=/var/backups/postgresql
vm_base=/opt/victoria-metrics
vm_config=/etc/victoria-metrics
vm_data=/var/lib/victoria-metrics
vm_port=8428
work=/run/octopushy-ha-db
download=

fail() {
  echo "ha-db: $*" >&2
  exit 1
}

cleanup() {
  local status=$?
  rm -rf "$work"
  [[ -z $download ]] || rm -rf "$download"
  exit "$status"
}
trap cleanup EXIT
install -d -m 0700 "$work"

for name in postgres_password victoriametrics_password; do
  [[ -s $secrets/$name ]] || fail "the $name secret is missing or empty"
done
[[ $(dpkg --print-architecture) == amd64 ]] || fail "VictoriaMetrics is installed from its amd64 build, and this is $(dpkg --print-architecture)"

export DEBIAN_FRONTEND=noninteractive
packages=(ca-certificates cron curl "postgresql-$pg_version" unattended-upgrades)
if ! dpkg-query -W "${packages[@]}" >/dev/null 2>&1; then
  apt-get -o DPkg::Lock::Timeout=120 update -q
  # unattended-upgrades applies Debian security updates.
  apt-get -o DPkg::Lock::Timeout=120 install -yq --no-install-recommends "${packages[@]}"
fi

# Whether a file already holds this text; an install again only touches what differs.
same() { [[ -f $1 ]] && [[ $(<"$1") == "$2" ]]; }
# Writes a file of the given mode from text, through a file of its own that nobody else can read.
put() { # path mode owner text
  local temp
  temp=$(mktemp "$1.XXXXXX")
  chmod "$2" "$temp"
  chown "$3" "$temp"
  printf '%s\n' "$4" >"$temp"
  mv -f "$temp" "$1"
}

eventually() {
  for _ in $(seq 60); do
    "$@" && return 0
    sleep 1
  done
  return 1
}

# ---- PostgreSQL ----

restart_pg=false
reload_pg=false
if ! cmp -s "$app/postgresql-ha.conf" "$pg_conf/conf.d/ha.conf"; then
  install -m 0644 "$app/postgresql-ha.conf" "$pg_conf/conf.d/ha.conf"
  restart_pg=true
fi

# Only Home Assistant may log in over the network, and only to its own database. The unix socket
# is for the postgres user, and for the role that checks itself with its password. Everything
# that is not listed is refused.
hba="# Managed by Octopushy (recipe ha-db). What is not listed here is refused.
local   all             postgres                                peer
local   $db_name   $db_role   scram-sha-256"
for client in $pg_clients; do
  hba+=$'\n'"host    $db_name   $db_role   $client/32   scram-sha-256"
done
if ! same "$pg_conf/pg_hba.conf" "$hba"; then
  put "$pg_conf/pg_hba.conf" 0640 postgres:postgres "$hba"
  reload_pg=true
fi

systemctl enable "$pg_service" >/dev/null 2>&1 || true
if [[ $restart_pg == true ]]; then
  systemctl restart "$pg_service"
else
  systemctl start "$pg_service"
fi
eventually pg_isready -q -h "$pg_socket" || fail "PostgreSQL does not answer on $pg_socket"
if [[ $reload_pg == true && $restart_pg == false ]]; then
  systemctl reload "$pg_service"
fi

# psql as the postgres user reads its statements from stdin. This session is told not to log them:
# the server would otherwise write a failing statement, password and all, to its log.
admin() { runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -d postgres "$@"; }

# A pgpass file for the check below. It is the one place a password has to be written down for libpq
# to read it, so it is in /run, mode 0600, and removed with the rest. ':' and '\' are escaped.
pgpass=$work/pgpass
escaped=$(<"$secrets/postgres_password")
escaped=${escaped//\\/\\\\}
escaped=${escaped//:/\\:}
(umask 077 && printf '*:*:%s:%s:%s\n' "$db_name" "$db_role" "$escaped" >"$pgpass")
unset escaped
logs_in() { PGPASSFILE=$pgpass psql -X -q -w -At -h "$pg_socket" -U "$db_role" -d "$db_name" -c 'select 1' 2>/dev/null | grep -qx 1; }

admin <<SQL
SELECT 'CREATE ROLE $db_role LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$db_role') \gexec
SELECT 'CREATE DATABASE $db_name OWNER $db_role ENCODING ''UTF8'' LOCALE ''C.UTF-8'' TEMPLATE template0'
  WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$db_name') \gexec
ALTER DATABASE $db_name OWNER TO $db_role;
SQL
# Set the password only when the one in the store does not already work.
if ! logs_in; then
  quoted=$(<"$secrets/postgres_password")
  quoted=${quoted//\'/\'\'}
  {
    echo "SET log_statement = 'none';"
    echo "SET log_min_error_statement = 'panic';"
    printf "ALTER ROLE %s PASSWORD '%s';\n" "$db_role" "$quoted"
  } | admin
  unset quoted
fi

# One dump a night for each weekday, so seven are kept.
install -d -m 0700 -o postgres -g postgres "$dumps"
install -m 0755 "$app/ha-db-dump" /usr/local/sbin/ha-db-dump
install -m 0644 "$app/ha-db-dump.cron" /etc/cron.d/ha-db-dump
systemctl enable --now cron >/dev/null 2>&1 || true

# ---- VictoriaMetrics ----

id victoriametrics >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin --user-group victoriametrics
install -d -m 0700 -o victoriametrics -g victoriametrics "$vm_data"
install -d -m 0755 "$vm_config" "$vm_base"

# Downloaded and checked, then unpacked into a directory that appears only when it is complete.
release_dir=$vm_base/v$vm_version
if [[ ! -x $release_dir/victoria-metrics-prod ]]; then
  download=$(mktemp -d /var/tmp/octopushy-ha-db.XXXXXX)
  archive=victoria-metrics-linux-amd64-v$vm_version.tar.gz
  curl -fsSL --retry 3 -o "$download/$archive" "https://github.com/VictoriaMetrics/VictoriaMetrics/releases/download/v$vm_version/$archive"
  sha256sum -c --quiet - <<<"$vm_sha256  $download/$archive"
  rm -rf "$release_dir.partial"
  install -d -m 0755 "$release_dir.partial"
  tar -xzf "$download/$archive" -C "$release_dir.partial" --no-same-owner victoria-metrics-prod
  chmod -R u=rwX,go=rX "$release_dir.partial"
  rm -rf "$release_dir"
  mv "$release_dir.partial" "$release_dir"
fi

restart_vm=false
if [[ $(readlink "$vm_base/current" || true) != "$release_dir" ]]; then
  ln -sfn "$release_dir" "$vm_base/current"
  restart_vm=true
fi
if ! cmp -s "$app/victoria-metrics-relabel.yaml" "$vm_config/relabel.yaml"; then
  install -m 0644 "$app/victoria-metrics-relabel.yaml" "$vm_config/relabel.yaml"
  restart_vm=true
fi

# The password is in a file of its own that only root reads. systemd reads it as root and shows only
# its name, and VictoriaMetrics takes httpAuth.username and httpAuth.password from the environment.
# Single quotes, because systemd then takes the value as it is; the password has none.
env_text="httpAuth_username=$db_role
httpAuth_password='$(<"$secrets/victoriametrics_password")'"
if ! same "$vm_config/auth.env" "$env_text"; then
  put "$vm_config/auth.env" 0600 root:root "$env_text"
  restart_vm=true
fi
unset env_text

unit_text="[Unit]
Description=VictoriaMetrics
After=network-online.target

[Service]
User=victoriametrics
EnvironmentFile=$vm_config/auth.env
ExecStart=$vm_base/current/victoria-metrics-prod -envflag.enable -storageDataPath=$vm_data -retentionPeriod=$vm_retention -relabelConfig=$vm_config/relabel.yaml -memory.allowedBytes=256MiB -httpListenAddr=:$vm_port
Restart=always

NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$vm_data

[Install]
WantedBy=multi-user.target"
if ! same /etc/systemd/system/victoria-metrics.service "$unit_text"; then
  put /etc/systemd/system/victoria-metrics.service 0644 root:root "$unit_text"
  restart_vm=true
fi
systemctl daemon-reload
systemctl enable victoria-metrics.service >/dev/null 2>&1
if [[ $restart_vm == true ]]; then
  systemctl restart victoria-metrics.service
else
  systemctl start victoria-metrics.service
fi

# ---- It checks itself ----

vm_call() { # extra curl arguments: asks with the credentials, which curl reads from stdin
  printf 'user = "%s:%s"\n' "$db_role" "$(<"$secrets/victoriametrics_password")" | curl -sS -K - "$@"
}
vm_health() { [[ $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$vm_port/health") == 200 ]]; }

eventually logs_in || fail "PostgreSQL does not accept the homeassistant role with the password it was given"
PGPASSFILE=/dev/null psql -X -q -w -At -h "$pg_socket" -U "$db_role" -d "$db_name" -c 'select 1' >/dev/null 2>&1 \
  && fail "PostgreSQL let the homeassistant role in without a password"
# /health is the one path VictoriaMetrics leaves open, as a liveness check needs to be. Everything that
# reads or writes samples asks for the password.
eventually vm_health || fail "VictoriaMetrics does not answer /health"
for path in "api/v1/query?query=1" "api/v1/labels" "write?db=ha"; do
  [[ $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$vm_port/$path") == 401 ]] \
    || fail "VictoriaMetrics answers /$path without a password"
done
[[ $(vm_call -o /dev/null -w '%{http_code}' "http://127.0.0.1:$vm_port/api/v1/query?query=1") == 200 ]] \
  || fail "VictoriaMetrics refuses its own password for queries"
[[ $(printf 'user = "%s:%s"\n' "$db_role" "not-the-password-0123456789" | curl -s -K - -o /dev/null -w '%{http_code}' "http://127.0.0.1:$vm_port/api/v1/query?query=1") == 401 ]] \
  || fail "VictoriaMetrics accepts a password that is not the one it was given"

# The old release is no longer needed, now that this one answers.
find "$vm_base" -mindepth 1 -maxdepth 1 -type d -name 'v*' ! -path "$release_dir" -exec rm -rf {} +

echo "ha-db: PostgreSQL $pg_version answers and VictoriaMetrics $vm_version is healthy; both ask for their passwords"
