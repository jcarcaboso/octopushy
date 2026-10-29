#!/usr/bin/env bash
# Updates Octopushy in its LXC container to another image version. Runs on the Proxmox
# host; install.sh installs it as /usr/local/sbin/octopushy-update.
#
#   octopushy-update <container-id> <version> [--no-snapshot] [--keep N] [--yes]
#
# <version> is an image tag: a release such as 0.2.0, `main` for the newest build of the
# main branch, or sha-<commit>. compose.yaml comes from the matching Git ref.
#
# Steps: pull the new images (nothing stops if that fails), stop everything but
# PostgreSQL (the internal agent lets running applies finish first), dump the database
# to this host, snapshot the container, switch compose.yaml
# and the image tags, start (migrations run first), and check /api/health. If the check
# fails, the container is rolled back to the snapshot: database, data, and configuration
# together. The newest N update snapshots are kept (3 by default).
set -euo pipefail

repo=${OCTOPUSHY_REPO:-jcarcaboso/octopushy}
dumps=/var/backups/octopushy
dir=/opt/octopushy

die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }
say() { printf '\033[1m==> %s\033[0m\n' "$*"; }
usage() { sed -n '/^set -euo/q; 2,$s/^# \{0,1\}//p' "$0"; exit 2; }

snapshot=1 keep=3 yes=0 args=()
while (($#)); do
  case $1 in
    --no-snapshot) snapshot=0 ;;
    --keep) keep=${2:?}; shift ;;
    --yes | -y) yes=1 ;;
    -h | --help) usage ;;
    *) args+=("$1") ;;
  esac
  shift
done
((${#args[@]} == 2)) || usage
ct=${args[0]} version=${args[1]}
[[ $version =~ ^[A-Za-z0-9._-]+$ ]] || die "not an image tag: $version"

case $version in
  main) ref=main ;;
  sha-*) ref=${version#sha-} ;;
  *) ref=v$version ;;
esac
ref=${OCTOPUSHY_REF:-$ref}

# in_ct "script": runs a bash script in the container's Octopushy directory.
in_ct() { pct exec "$ct" -- bash -c "set -euo pipefail; cd $dir; $1"; }

[[ $(id -u) == 0 ]] || die "run this as root on the Proxmox VE host"
pct status "$ct" | grep 'status: running' >/dev/null || die "container $ct is not running"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsSL "https://raw.githubusercontent.com/$repo/$ref/deploy/compose.yaml" -o "$work/compose.yaml" ||
  die "no deploy/compose.yaml at $ref in $repo"

env_file=$(in_ct "cat .env")
current=$(sed -n 's/^OCTOPUSHY_BACKEND_IMAGE=//p' <<<"$env_file")
[[ $current == *-backend:* ]] || die "cannot read OCTOPUSHY_BACKEND_IMAGE from $dir/.env"
prefix=${current%-backend:*}
backend=$prefix-backend:$version
# Every image named in .env moves to the new version: backend, frontend, and agent.
targets=() set_images=()
for component in backend frontend agent; do
  var=OCTOPUSHY_${component^^}_IMAGE
  grep "^$var=" <<<"$env_file" >/dev/null || continue
  targets+=("$prefix-$component:$version")
  set_images+=(-e "'s|^$var=.*|$var=$prefix-$component:$version|'")
done

say "Pulling ${targets[*]}"
in_ct "for image in ${targets[*]}; do docker pull -q \$image; done"

cat <<EOF

  container  $ct
  from       $current
  to         $backend (compose.yaml from $ref)
  rollback   $( ((snapshot)) && echo "container snapshot" || echo "none: database dump only")

Octopushy stops for a minute or two. Wait for running provision runs to finish first.
EOF
if ((!yes)); then
  read -r -p "Update? [y/N] " answer
  [[ $answer == [yY]* ]] || die "cancelled"
fi

say "Stopping Octopushy; the internal agent finishes running applies first"
in_ct 'docker compose stop $(docker compose config --services | grep -vx postgres)'

stamp=$(date +%Y%m%d-%H%M%S)
say "Dumping the database to $dumps/ct$ct-$stamp.sql.gz"
install -d -m 0700 "$dumps"
if ! in_ct "docker compose exec -T postgres pg_dump -U octopushy octopushy" | gzip >"$dumps/ct$ct-$stamp.sql.gz"; then
  rm -f "$dumps/ct$ct-$stamp.sql.gz"
  in_ct "docker compose up -d"
  die "the database dump failed, so nothing changed"
fi
# Keep the five newest dumps of this container.
ls -1t "$dumps"/ct"$ct"-*.sql.gz | tail -n +6 | xargs -r rm --

name=octopushy-$stamp
if ((snapshot)); then
  say "Snapshotting container $ct as $name"
  if ! pct snapshot "$ct" "$name" --description "Before the Octopushy update from $current to $backend"; then
    in_ct "docker compose up -d"
    die "the snapshot failed, so nothing changed; use --no-snapshot to update with the database dump alone"
  fi
fi

# Switches compose.yaml and the image tags, starts the project (migrate runs before the
# backend), and waits for /api/health through the frontend.
apply() {
  pct push "$ct" "$work/compose.yaml" "$dir/compose.yaml.new" || return 1
  in_ct "cp compose.yaml compose.yaml.prev && cp .env .env.prev && mv compose.yaml.new compose.yaml
    sed -i ${set_images[*]} .env" || return 1
  in_ct "docker compose up -d --remove-orphans" || return 1
  local port
  port=$(in_ct "sed -n 's/^OCTOPUSHY_UI_PORT=//p' .env")
  for _ in $(seq 90); do
    in_ct "curl -fsS http://127.0.0.1:${port:-8081}/api/health" >/dev/null 2>&1 && return 0
    sleep 2
  done
  return 1
}

say "Switching to $version"
if ! apply; then
  in_ct "docker compose ps; docker compose logs --tail 80 migrate backend" || true
  if ((snapshot)); then
    say "The update failed; rolling container $ct back to $name"
    pct rollback "$ct" "$name" --start 1
    # The snapshot was taken with Octopushy stopped; start it again.
    for _ in $(seq 30); do in_ct "docker info" >/dev/null 2>&1 && break; sleep 2; done
    in_ct "docker compose up -d" || true
    die "rolled back to $current; the logs above show why $version failed"
  fi
  die "the update failed and there is no snapshot. The previous files are $dir/.env.prev and $dir/compose.yaml.prev; the database dump is $dumps/ct$ct-$stamp.sql.gz"
fi

if ((snapshot)); then
  # Snapshot names sort by time; delete all but the newest $keep.
  pct listsnapshot "$ct" | grep -o 'octopushy-[0-9]\{8\}-[0-9]\{6\}' | sort -u | head -n -"$keep" |
    while read -r old; do pct delsnapshot "$ct" "$old"; done || true
fi
in_ct "rm -f compose.yaml.prev .env.prev"
say "Octopushy in container $ct now runs $version"
