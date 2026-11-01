#!/usr/bin/env bash
# Runs in the container as root, over SSH from this root's provisioners. Installs the pinned
# ASP.NET Core runtime and Technitium DNS Server next to any older versions, switches to them,
# and checks the result. Running it again changes nothing that is already right.
#
# This is not upstream's install.sh: that one always fetches the latest release and points
# /etc/resolv.conf at 127.0.0.1. The container keeps resolving through the network.dns that
# Proxmox wrote there, so nothing here depends on the server it installs.
#
# $work/admin-password holds the admin password. Technitium reads it only while /etc/dns has
# no auth.config, and takes admin/admin if it cannot, so the login is checked afterwards. It
# reaches curl as a file and never as an argument, and it is removed however this ends.
set -euo pipefail

version=${TECHNITIUM_VERSION:?set TECHNITIUM_VERSION}
sha256=${TECHNITIUM_SHA256:?set TECHNITIUM_SHA256}
runtime_version=${ASPNETCORE_VERSION:?set ASPNETCORE_VERSION}
runtime_sha512=${ASPNETCORE_SHA512:?set ASPNETCORE_SHA512}
address=${GUEST_ADDRESS:?set GUEST_ADDRESS}
nameservers=${NAMESERVERS:?set NAMESERVERS}

work=/run/octopushy-technitium
password=$work/admin-password
config=/etc/dns
base=/opt/technitium
service=dns.service
first_run_drop_in=/run/systemd/system/$service.d
api=http://127.0.0.1:5380/api
download=
first_run=false

fail() {
  echo "technitium-lxc: $*" >&2
  exit 1
}

cleanup() {
  local status=$?
  rm -rf "$work"
  [[ -z $download ]] || rm -rf "$download"
  if [[ -e $first_run_drop_in ]]; then
    rm -rf "$first_run_drop_in"
    systemctl daemon-reload
  fi
  # A first start that failed may have saved the default password: without this, the next
  # attempt would find an auth.config and never set the intended one.
  if [[ $status -ne 0 && $first_run == true ]]; then
    systemctl stop "$service" || true
    rm -f "$config/auth.config"
  fi
}
trap cleanup EXIT

[[ -s $password ]] || fail "$password is missing or empty"
# Identity written to the root disk would be lost with it.
mountpoint -q "$config" || fail "$config is not a mount point; declare it under hardware.mountPoints"

export DEBIAN_FRONTEND=noninteractive
packages=(ca-certificates curl jq libicu76 libgssapi-krb5-2 nftables unattended-upgrades)
if ! dpkg-query -W "${packages[@]}" >/dev/null 2>&1; then
  apt-get -o DPkg::Lock::Timeout=120 update -q
  # unattended-upgrades applies Debian security updates.
  apt-get -o DPkg::Lock::Timeout=120 install -yq --no-install-recommends "${packages[@]}"
fi

# Downloads and checks an archive, then unpacks it into a directory that appears only when
# it is complete. Nothing that runs is touched until both downloads are good.
unpack() {
  local url=$1 tool=$2 digest=$3 target=$4
  [[ ! -e $target ]] || return 0
  if [[ -z $download ]]; then
    download=$(mktemp -d /var/tmp/octopushy-technitium.XXXXXX)
  fi
  curl -fsSL --retry 3 -o "$download/archive.tar.gz" "$url"
  "$tool" -c --quiet - <<<"$digest  $download/archive.tar.gz"
  rm -rf "$target.partial"
  install -d -m 0755 "$target.partial"
  tar -xzf "$download/archive.tar.gz" -C "$target.partial" --no-same-owner
  chmod -R u=rwX,go=rX "$target.partial"
  mv "$target.partial" "$target"
  rm -f "$download/archive.tar.gz"
}

install -d -m 0755 "$base"
runtime_dir=$base/aspnetcore-$runtime_version
release_dir=$base/dns-$version
unpack "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$runtime_version/aspnetcore-runtime-$runtime_version-linux-x64.tar.gz" \
  sha512sum "$runtime_sha512" "$runtime_dir"
unpack "https://download.technitium.com/dns/archive/$version/DnsServerPortable.tar.gz" \
  sha256sum "$sha256" "$release_dir"

id dns-server >/dev/null 2>&1 || useradd --system -M --shell /usr/sbin/nologin --user-group dns-server
install -d -m 0755 /var/log/technitium
install -d -m 0750 -o dns-server -g dns-server /var/log/technitium/dns
chown -R dns-server:dns-server "$config" /var/log/technitium/dns

# Technitium serves its plain HTTP port on the same addresses as HTTPS, and cannot turn it
# off. This is the guest's own rule, so it holds whatever the Proxmox firewall is set to.
cat >/etc/dns-http-guard.nft <<'NFT'
table inet octopushy_technitium
delete table inet octopushy_technitium
table inet octopushy_technitium {
  chain input {
    type filter hook input priority -10; policy accept;
    iif != "lo" tcp dport 5380 drop
  }
}
NFT
cat >/etc/systemd/system/dns-http-guard.service <<UNIT
[Unit]
Description=Keep Technitium's plain HTTP port on loopback
Before=$service

[Service]
Type=oneshot
RemainAfterExit=true
ExecStart=/usr/sbin/nft -f /etc/dns-http-guard.nft

[Install]
WantedBy=multi-user.target
UNIT

# Upstream's systemd.service, with the runtime's path. A failed guard does not hold DNS back,
# and neither Octopushy nor name resolution has to be up for this to start.
unit=/etc/systemd/system/$service
unit_text=$(cat <<UNIT
[Unit]
Description=Technitium DNS Server
After=network.target dns-http-guard.service
Wants=network-online.target dns-http-guard.service

[Service]
Type=simple
User=dns-server
Group=dns-server
SyslogIdentifier=dns-server

WorkingDirectory=$base/dns
ReadWritePaths=$config /var/log/technitium/dns

Environment="OPENSSL_ENABLE_SHA1_SIGNATURES=1"

ExecStart=$base/aspnetcore/dotnet $base/dns/DnsServerApp.dll $config

Restart=always
RestartSec=10

LockPersonality=true
NoNewPrivileges=true
PrivateDevices=true
PrivateTmp=true
ProtectClock=true
ProtectControlGroups=true
ProtectHome=true
ProtectHostname=true
ProtectKernelLogs=true
ProtectKernelModules=true
ProtectKernelTunables=true
ProtectSystem=strict
RemoveIPC=true
RestrictRealtime=true
RestrictSUIDSGID=true

AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE

UMask=0077

[Install]
WantedBy=multi-user.target
UNIT
)

changed=false
[[ $(cat "$unit" 2>/dev/null || true) == "$unit_text" ]] || changed=true
[[ $(readlink "$base/dns" || true) == "$release_dir" ]] || changed=true
[[ $(readlink "$base/aspnetcore" || true) == "$runtime_dir" ]] || changed=true
if [[ $changed == true ]]; then
  systemctl stop "$service" 2>/dev/null || true
  printf '%s\n' "$unit_text" >"$unit"
  ln -sfn "$release_dir" "$base/dns"
  ln -sfn "$runtime_dir" "$base/aspnetcore"
fi
systemctl daemon-reload
systemctl enable --now dns-http-guard.service \
  || fail "cannot load the nftables rule that keeps port 5380 on loopback; is nf_tables available on the Proxmox host?"
systemctl restart dns-http-guard.service

# The first start, the only one that reads these. They live in /run, so a reboot can never
# start the server with them, and a file that names the password file holds no secret.
if [[ ! -e $config/auth.config ]]; then
  first_run=true
  chown root:dns-server "$work"
  chmod 0750 "$work"
  chown dns-server "$password"
  chmod 0400 "$password"
  install -d -m 0755 "$first_run_drop_in"
  cat >"$first_run_drop_in/first-run.conf" <<CONF
[Service]
Environment="DNS_SERVER_ADMIN_PASSWORD_FILE=$password"
Environment="DNS_SERVER_WEB_SERVICE_ENABLE_HTTPS=true"
Environment="DNS_SERVER_WEB_SERVICE_USE_SELF_SIGNED_CERT=true"
Environment="DNS_SERVER_WEB_SERVICE_HTTPS_PORT=53443"
CONF
  systemctl stop "$service" 2>/dev/null || true
  systemctl daemon-reload
  systemctl start "$service"
  for _ in $(seq 60); do
    [[ -e $config/auth.config ]] && break
    sleep 1
  done
  [[ -e $config/auth.config ]] || fail "Technitium did not create auth.config"
  systemctl stop "$service"
  rm -rf "$first_run_drop_in"
  systemctl daemon-reload
fi

systemctl enable --now "$service"

echo "Waiting for Technitium to answer"
for _ in $(seq 60); do
  [[ $(curl -s -o /dev/null -w '%{http_code}' "$api/user/session/get" || true) != 000 ]] && break
  sleep 1
done

login=$(curl -fsS --data 'user=admin&includeInfo=true' --data-urlencode "pass@$password" "$api/user/login") \
  || fail "the API does not answer on $api"
[[ $(jq -r .status <<<"$login") == ok ]] || fail "the intended admin password does not work"
token=$(jq -r .token <<<"$login")
running=$(jq -r .info.version <<<"$login")
curl -fsS -o /dev/null --data @- "$api/user/logout" <<<"token=$token" || true
unset login token

[[ ${running%.0} == "${version%.0}" ]] || fail "version $running is running, not $version"

default=$(curl -fsS --data 'user=admin&pass=admin' "$api/user/login") || fail "the API stopped answering"
[[ $(jq -r .status <<<"$default") == error ]] || fail "admin/admin still works; the intended password was not set"
unset default

# The web service can answer before the rest is up.
eventually() {
  for _ in $(seq 30); do
    "$@" && return 0
    sleep 1
  done
  return 1
}
listens() { ss -H "$1" 'sport = :53' | grep -q .; }
eventually curl -fsSk -o /dev/null https://127.0.0.1:53443/ || fail "HTTPS does not answer on port 53443"
eventually listens -lun || fail "nothing listens on UDP port 53"
eventually listens -ltn || fail "nothing listens on TCP port 53"

# Proxmox writes resolv.conf from network.dns; this server must never be in it.
for server in $nameservers; do
  grep -Eq "^nameserver[[:space:]]+${server//./\\.}([[:space:]]|$)" /etc/resolv.conf \
    || fail "/etc/resolv.conf does not name $server"
done
if grep -Eq "^nameserver[[:space:]]+(127\.|::1|${address//./\\.}([[:space:]]|$))" /etc/resolv.conf; then
  fail "/etc/resolv.conf points at this server"
fi

# The checks passed, so older versions can go. /etc/dns stays as it is.
find "$base" -mindepth 1 -maxdepth 1 -type d \( -name 'dns-*' -o -name 'aspnetcore-*' \) \
  ! -path "$release_dir" ! -path "$runtime_dir" -exec rm -rf {} +

echo "Technitium $running is running; HTTPS on 53443, DNS on 53"
