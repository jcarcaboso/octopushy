#!/usr/bin/env bash
# Creates a Debian 13 LXC container on this Proxmox VE host and runs the Octopushy
# control host in it with Docker Compose: PostgreSQL, the backend, and the frontend.
# Run as root on the Proxmox host:
#
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/main/deploy/proxmox/install.sh)"
#
# It asks for each setting with a default. Every setting can be preset in the environment
# (the names are in brackets in the prompts); OCTOPUSHY_YES=1 takes the defaults without
# asking. It also installs `octopushy-update` on this host. See deploy/proxmox/README.md.
set -euo pipefail

repo=${OCTOPUSHY_REPO:-jcarcaboso/octopushy}
images=${OCTOPUSHY_IMAGES:-docker.io/skorcius/octopushy}

say() { printf '\033[1m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# ask VAR "question" default: keeps VAR when it is already set.
ask() {
  local var=$1 question=$2 default=$3 answer
  if [[ -n ${!var:-} ]]; then return; fi
  if [[ ${OCTOPUSHY_YES:-0} == 1 || ! -t 0 ]]; then
    printf -v "$var" '%s' "$default"
    return
  fi
  read -r -p "$question [$var] ($default): " answer
  printf -v "$var" '%s' "${answer:-$default}"
}

# The Git ref that matches an image tag: v<version> for releases, the commit for sha-<commit>.
ref_for() {
  case $1 in
    main) echo main ;;
    sha-*) echo "${1#sha-}" ;;
    *) echo "v$1" ;;
  esac
}

[[ $(id -u) == 0 ]] || die "run this as root on the Proxmox VE host"
for tool in pct pveam pvesm pvesh curl; do
  command -v "$tool" >/dev/null || die "$tool not found; run this on a Proxmox VE host"
done

say "Octopushy on a Proxmox LXC container"
ask OCTOPUSHY_VERSION "Version to install (a release, main, or sha-<commit>)" 0.1.0
OCTOPUSHY_REF=${OCTOPUSHY_REF:-$(ref_for "$OCTOPUSHY_VERSION")}

ask CTID "Container ID" "$(pvesh get /cluster/nextid)"
[[ $CTID =~ ^[0-9]+$ ]] || die "container ID must be a number"
if compgen -G "/etc/pve/nodes/*/lxc/$CTID.conf" >/dev/null ||
  compgen -G "/etc/pve/nodes/*/qemu-server/$CTID.conf" >/dev/null; then
  die "ID $CTID is already in use"
fi
ask CT_HOSTNAME "Host name" octopushy

default_storage=$(pvesm status --content rootdir --enabled 2>/dev/null | awk 'NR == 2 { print $1 }')
ask CT_STORAGE "Storage for the container's disks" "${default_storage:-local-lvm}"
storage_type=$(pvesm status --storage "$CT_STORAGE" 2>/dev/null | awk 'NR > 1 { print $2 }')
[[ -n $storage_type ]] || die "storage $CT_STORAGE not found"
case $storage_type in
  zfspool | lvmthin | rbd | btrfs) ;;
  *) warn "storage $CT_STORAGE ($storage_type) cannot take container snapshots; octopushy-update will need --no-snapshot and cannot roll back by itself" ;;
esac
default_template_storage=$(pvesm status --content vztmpl --enabled 2>/dev/null | awk 'NR == 2 { print $1 }')
ask CT_TEMPLATE_STORAGE "Storage for the Debian template" "${default_template_storage:-local}"

ask CT_BRIDGE "Network bridge" vmbr0
ask CT_VLAN "VLAN tag (empty for none)" none
ask CT_IP "IPv4 address with prefix, such as 10.1.0.50/24, or dhcp" dhcp
if [[ $CT_IP != dhcp ]]; then
  [[ $CT_IP == */* ]] || die "give the address with its prefix length, such as $CT_IP/24"
  ask CT_GATEWAY "Gateway" "$(ip -4 route show default | awk 'NR == 1 { print $3 }')"
  default_agent_host=${CT_IP%/*}
else
  default_agent_host=$CT_HOSTNAME
fi
ask OCTOPUSHY_AGENT_HOST "Name or address agents use to reach Octopushy (goes into its certificate)" "$default_agent_host"

ask CT_CORES "CPU cores" 2
ask CT_MEMORY "Memory in MiB" 4096
ask CT_DISK "Disk size in GiB" 32
ask CT_SSH_KEY "SSH public key file for root (empty for none)" none

net0="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP"
[[ $CT_IP == dhcp ]] || net0+=",gw=$CT_GATEWAY"
[[ $CT_VLAN == none || -z $CT_VLAN ]] || net0+=",tag=$CT_VLAN"
ssh_args=()
if [[ $CT_SSH_KEY != none && -n $CT_SSH_KEY ]]; then
  [[ -f $CT_SSH_KEY ]] || die "$CT_SSH_KEY not found"
  ssh_args=(--ssh-public-keys "$CT_SSH_KEY")
fi

cat <<EOF

  container   $CTID ($CT_HOSTNAME), Debian 13, unprivileged, nesting + keyctl for Docker
  resources   $CT_CORES cores, $CT_MEMORY MiB, $CT_DISK GiB on $CT_STORAGE
  network     $net0
  agents      connect to $OCTOPUSHY_AGENT_HOST:8443
  images      $images:{backend,frontend,agent}-$OCTOPUSHY_VERSION
  files from  github.com/$repo at $OCTOPUSHY_REF

EOF
if [[ ${OCTOPUSHY_YES:-0} != 1 && -t 0 ]]; then
  read -r -p "Create it? [y/N] " answer
  [[ $answer == [yY]* ]] || die "cancelled"
fi

# The files that go into the container: from this checkout when run from one, else from GitHub.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
here=$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" && pwd)
for file in deploy/compose.yaml deploy/proxmox/setup-container.sh deploy/proxmox/update.sh; do
  local_copy=$here/../../$file
  if [[ -f $here/setup-container.sh && -f $local_copy ]]; then
    cp "$local_copy" "$work/"
  else
    curl -fsSL "https://raw.githubusercontent.com/$repo/$OCTOPUSHY_REF/$file" -o "$work/${file##*/}" ||
      die "could not download $file at $OCTOPUSHY_REF"
  fi
done

say "Downloading the Debian 13 template"
pveam update >/dev/null || warn "pveam update failed; using the cached template list"
template=$(pveam available --section system | awk '{ print $2 }' |
  grep -E '^debian-13-standard_.*_amd64\.tar\.(zst|xz|gz)$' | sort -V | tail -n 1) ||
  die "no Debian 13 template in the template list"
if ! pveam list "$CT_TEMPLATE_STORAGE" | grep "/$template" >/dev/null; then
  pveam download "$CT_TEMPLATE_STORAGE" "$template"
fi

say "Creating container $CTID"
# mp0 holds only the variable store's key. backup=0 keeps it out of vzdump backups, so a
# backup never carries the key next to the database it decrypts.
pct create "$CTID" "$CT_TEMPLATE_STORAGE:vztmpl/$template" \
  --hostname "$CT_HOSTNAME" \
  --ostype debian \
  --unprivileged 1 \
  --features nesting=1,keyctl=1 \
  --cores "$CT_CORES" \
  --memory "$CT_MEMORY" \
  --swap 512 \
  --rootfs "$CT_STORAGE:$CT_DISK" \
  --mp0 "$CT_STORAGE:1,mp=/etc/octopushy/secrets,backup=0" \
  --net0 "$net0" \
  --onboot 1 \
  --tags octopushy \
  --description "Octopushy control host. Update with: octopushy-update $CTID <version>" \
  "${ssh_args[@]}"
pct start "$CTID"

say "Waiting for the network"
for _ in $(seq 60); do
  pct exec "$CTID" -- getent hosts deb.debian.org >/dev/null 2>&1 && break
  sleep 2
done
pct exec "$CTID" -- getent hosts deb.debian.org >/dev/null 2>&1 ||
  die "container $CTID has no working network or DNS; check $net0"

say "Installing Docker and starting Octopushy"
pct exec "$CTID" -- mkdir -p /opt/octopushy
pct push "$CTID" "$work/compose.yaml" /opt/octopushy/compose.yaml
pct push "$CTID" "$work/setup-container.sh" /root/octopushy-setup.sh --perms 0755
pct exec "$CTID" -- env \
  OCTOPUSHY_VERSION="$OCTOPUSHY_VERSION" \
  OCTOPUSHY_IMAGES="$images" \
  OCTOPUSHY_AGENT_HOST="$OCTOPUSHY_AGENT_HOST" \
  bash /root/octopushy-setup.sh

install -m 0755 "$work/update.sh" /usr/local/sbin/octopushy-update

address=$(pct exec "$CTID" -- hostname -I | awk '{ print $1 }')
cat <<EOF

Octopushy is running in container $CTID.

  Web interface   http://$address:8081  (plain HTTP: put HTTPS in front, see the README)
  Setup code      pct exec $CTID -- octopushy-compose exec backend cat /var/lib/octopushy/setup-code
  Internal agent  create a token under Agents, then: pct exec $CTID -- octopushy-agent-enroll <token>
  Other agents    connect to $OCTOPUSHY_AGENT_HOST:8443
  Update          octopushy-update $CTID <version>
  Shell           pct enter $CTID

Keep a copy of the variable store's key somewhere safe, apart from any backup:
  pct exec $CTID -- base64 /etc/octopushy/secrets/kek
EOF
