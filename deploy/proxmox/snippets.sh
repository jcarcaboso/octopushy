#!/usr/bin/env bash
# Lets Octopushy upload cloud-init snippets to this Proxmox host, once. Run it as root on each
# host, for example from the host's shell in the Proxmox web interface:
#
#   OCTOPUSHY_SNIPPETS_KEY='ssh-ed25519 AAAA… octopushy-snippets@home/pve01' bash snippets.sh
#   bash snippets.sh --remove
#
# The key is the public half Octopushy shows on the installation's page. Running it again
# with the same settings changes nothing, and with a new key it replaces the old one.
#
# What it does, once every check below has passed and before anything else:
#   1. Adds `snippets` to the content of a directory datastore (OCTOPUSHY_SNIPPETS_DATASTORE,
#      default `local`).
#   2. Creates a system account (OCTOPUSHY_SNIPPETS_USER, default `octopushy-snippets`) with
#      no password and no shell.
#   3. Builds a chroot holding only that datastore's snippets directory, at the same path,
#      and bind mounts the real directory into it with a systemd mount unit.
#   4. Adds an sshd drop-in that sorts first, with one block, `Match User <account>` and no other
#      condition: that account may only log in with the key, only use SFTP, only inside the
#      chroot, and new files are created mode 0600. sshd takes the first value it finds for these
#      settings, so the block holds for every connection of that account, and it names no other.
#   5. Checks the whole sshd configuration with `sshd -t`, and only then reloads sshd. If that or
#      anything after it fails, sshd's files are put back as they were.
#   6. Prints what it changed, and this host's SSH host key for Octopushy to pin.
#
# It changes nothing, and says why, if: the account is root or exists without this script's
# marker; sshd_config does not include sshd_config.d at its top; sshd -t already fails; the
# drop-in's name is taken by a file this script did not write, or another drop-in sorts before
# it; or any other sshd file mentions the account.
#
# --remove undoes 2 to 5 with the same checks and the same restore, and leaves the datastore's
# content and any snippets as they are.
set -euo pipefail
# Byte order for comparing file names, as sshd's Include sorts them, and plain messages.
export LC_ALL=C

user=${OCTOPUSHY_SNIPPETS_USER:-octopushy-snippets}
datastore=${OCTOPUSHY_SNIPPETS_DATASTORE:-local}
key=${OCTOPUSHY_SNIPPETS_KEY:-}
chroot=/var/lib/$user
main_conf=/etc/ssh/sshd_config
conf_dir=/etc/ssh/sshd_config.d
conf=$conf_dir/00-$user.conf
keys_dir=/etc/ssh/$user
keys=$keys_dir/authorized_keys
# What marks the account and the drop-in as this script's own.
marker="Octopushy snippet uploads"
conf_marker="# Octopushy snippet uploads, for the account $user. Written by snippets.sh; remove with --remove."

die() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }
say() { printf '\033[1m==> %s\033[0m\n' "$*"; }
changed=()
note() { changed+=("$*"); printf '    changed: %s\n' "$*"; }
usage() { sed -n '/^set -euo/q; 2,$s/^# \{0,1\}//p' "$0"; exit 2; }

remove=0
while (($#)); do
  case $1 in
    --remove) remove=1 ;;
    -h | --help) usage ;;
    *) die "unknown argument: $1" ;;
  esac
  shift
done

# ---- Checks. Nothing on the host changes until all of them have passed. ----

((EUID == 0)) || die "run this as root on the Proxmox host"
[[ $user =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "not a user name: $user"
[[ $datastore =~ ^[A-Za-z][A-Za-z0-9_.-]*$ ]] || die "not a datastore name: $datastore"
for tool in pvesh pvesm sshd ssh-keygen systemctl systemd-escape python3; do
  command -v "$tool" >/dev/null || die "$tool is missing: is this a Proxmox VE host?"
done

# The account is this script's own. It never takes over one that exists, root least of all.
[[ $user != root ]] || die "the account cannot be root"
if entry=$(getent passwd "$user"); then
  IFS=: read -r _ _ uid _ gecos _ <<<"$entry"
  ((uid != 0)) || die "$user is UID 0; choose another OCTOPUSHY_SNIPPETS_USER"
  [[ $gecos == "$marker" ]] || die "$user exists and this script did not create it; choose another OCTOPUSHY_SNIPPETS_USER"
elif getent group "$user" >/dev/null; then
  die "a group $user exists without this script's account; choose another OCTOPUSHY_SNIPPETS_USER"
fi

if ((!remove)); then
  [[ -n $key ]] || die "set OCTOPUSHY_SNIPPETS_KEY to the public key Octopushy shows for this installation"
  [[ $key != *$'\n'* ]] || die "OCTOPUSHY_SNIPPETS_KEY must be one line"
  read -r key_type key_blob key_comment <<<"$key"
  [[ $key_type == ssh-ed25519 ]] || die "expected an ssh-ed25519 public key"
  fingerprint=$(ssh-keygen -l -f /dev/stdin <<<"$key_type $key_blob" 2>/dev/null) || die "that is not a valid public key"
  key_line="restrict $key_type $key_blob ${key_comment:-$user}"
fi

# sshd's service is `ssh` on Debian, and so on Proxmox.
service=ssh
systemctl list-unit-files ssh.service >/dev/null 2>&1 || service=sshd

# The datastore's directory, from Proxmox itself.
storage_json=$(pvesh get "/storage/$datastore" --output-format json) || die "there is no datastore $datastore"
path=$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("path", ""))' <<<"$storage_json")
content=$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("content", ""))' <<<"$storage_json")
[[ $path == /* ]] || die "$datastore has no directory path: snippets need a directory datastore"
snippets=$path/snippets
inner=$chroot$snippets
unit=$(systemd-escape -p --suffix=mount "$inner")

# The drop-in is read before anything else in sshd's configuration: Debian's sshd_config includes
# sshd_config.d at its top, and the drop-in sorts first there.
first=$(grep -Ev '^[[:space:]]*(#|$)' "$main_conf" | head -n 1)
[[ $first =~ ^[[:space:]]*[Ii][Nn][Cc][Ll][Uu][Dd][Ee][[:space:]]+/etc/ssh/sshd_config\.d/\*\.conf[[:space:]]*$ ]] \
  || die "$main_conf must start with 'Include $conf_dir/*.conf', as Debian's does; its first setting is: $first"
sshd -t || die "sshd -t refuses the configuration as it is; fix that first"
if [[ -e $conf ]]; then
  [[ -f $conf && $(head -n 1 "$conf") == "$conf_marker" ]] || die "$conf exists and this script did not write it; nothing was changed"
fi
# sshd expands the Include in byte order, as the C locale sorts.
earlier=()
shopt -s nullglob
for file in "$conf_dir"/*.conf; do
  name=${file##*/}
  if [[ $name < ${conf##*/} ]]; then earlier+=("$name"); fi
done
shopt -u nullglob
((${#earlier[@]} == 0)) || die "these drop-ins sort before ${conf##*/} and would be read first: ${earlier[*]}"
# Any other mention of the account, such as a Match block for it on some addresses, could set its
# settings before this script's block does, or after it for what sshd reads last.
mentions=$(grep -lE -- "(^|[^A-Za-z0-9_.-])$user([^A-Za-z0-9_.-]|\$)" "$main_conf" "$conf_dir"/* 2>/dev/null | grep -vxF -- "$conf" || true)
[[ -z $mentions ]] || die "other sshd files mention $user: $(tr '\n' ' ' <<<"$mentions")nothing was changed"

# ---- sshd's files: written, checked and reloaded, or put back. ----

had_conf=0 had_keys=0 old_conf= old_keys=
if [[ -f $conf ]]; then had_conf=1; old_conf=$(cat "$conf"); fi
if [[ -f $keys ]]; then had_keys=1; old_keys=$(cat "$keys"); fi
root_before=$(sshd -T -C "user=root,host=octopushy,addr=127.0.0.1")
staged=0 reloaded=0
restore_sshd() {
  ((staged)) || return 0
  if ((had_conf)); then printf '%s\n' "$old_conf" >"$conf"; else rm -f -- "$conf"; fi
  if ((had_keys)); then mkdir -p -- "$keys_dir"; printf '%s\n' "$old_keys" >"$keys"; else rm -f -- "$keys"; fi
  if ((reloaded)); then systemctl reload "$service" || true; fi
  printf '\033[31m    sshd: put back as it was\033[0m\n' >&2
}
trap restore_sshd EXIT

# Checks what sshd would read, then reloads it. Any failure here leaves restore_sshd to run.
apply_sshd() {
  sshd -t || die "sshd -t refused the configuration"
  [[ $(sshd -T -C "user=root,host=octopushy,addr=127.0.0.1") == "$root_before" ]] || die "the configuration would change root's login"
  reloaded=1
  systemctl reload "$service"
  note "reloaded sshd"
}

if ((remove)); then
  say "Removing the $user account and its SFTP access"
  if ((had_conf || had_keys)); then
    staged=1
    rm -f -- "$conf" "$keys"
    ((had_conf)) && note "removed $conf"
    ((had_keys)) && note "removed $keys"
    apply_sshd
    staged=0
  fi
  [[ -d $keys_dir ]] && rmdir -- "$keys_dir" && note "removed $keys_dir"
  if [[ -f /etc/systemd/system/$unit ]]; then
    systemctl disable --now "$unit" >/dev/null 2>&1 || true
    rm -f -- "/etc/systemd/system/$unit"
    systemctl daemon-reload
    note "removed the mount unit $unit"
  fi
  if mountpoint -q -- "$inner" 2>/dev/null; then umount -- "$inner"; note "unmounted $inner"; fi
  if [[ -d $chroot ]]; then
    # Only empty directories: the snippets themselves live in $snippets, not here.
    find "$chroot" -depth -type d -empty -delete
    [[ -e $chroot ]] && die "$chroot still holds files; look before removing it"
    note "removed $chroot"
  fi
  if [[ -d $snippets && $(stat -c '%G %a' "$snippets") == "$user 2770" ]]; then
    chgrp root "$snippets"
    chmod 755 "$snippets"
    note "gave $snippets back to root, mode 755"
  fi
  if id -u "$user" >/dev/null 2>&1; then userdel "$user"; note "removed the user $user"; fi
  if getent group "$user" >/dev/null; then groupdel "$user"; note "removed the group $user"; fi
  say "Done. ${#changed[@]} change(s)."
  echo "The datastore $datastore keeps snippets in its content, and $snippets keeps its files."
  echo "To stop offering snippets: pvesm set $datastore --content <the list without snippets>"
  exit 0
fi

# ---- Changes. ----

say "Datastore $datastore ($path)"
if [[ ,$content, == *,snippets,* ]]; then
  echo "    already offers snippets"
else
  pvesm set "$datastore" --content "${content:+$content,}snippets"
  note "added snippets to the content of $datastore (was: ${content:-none})"
fi
if [[ ! -d $snippets ]]; then
  mkdir -p -- "$snippets"
  note "created $snippets"
fi

say "Account $user"
if ! getent group "$user" >/dev/null; then groupadd --system "$user"; note "created the group $user"; fi
if ! id -u "$user" >/dev/null 2>&1; then
  useradd --system --gid "$user" --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin --comment "$marker" "$user"
  note "created the user $user"
fi
# No password: '*' matches nothing, and unlike '!' sshd does not read it as a locked account.
if [[ $(getent shadow "$user" | cut -d: -f2) != '*' ]]; then usermod -p '*' "$user"; note "set no password for $user"; fi
# The account writes snippets and nothing else. Proxmox reads them as root.
if [[ $(stat -c '%G %a' "$snippets") != "$user 2770" ]]; then
  chgrp "$user" "$snippets"
  chmod 2770 "$snippets"
  note "made $snippets writable by the group $user (mode 2770)"
fi

say "Chroot $chroot"
# sshd requires every directory down to the chroot to be root's and writable by nobody else.
dir=$chroot
for part in ${snippets//\// }; do
  if [[ ! -d $dir ]]; then mkdir -p -- "$dir"; note "created $dir"; fi
  if [[ $(stat -c '%U %a' "$dir") != "root 755" ]]; then chown root:root "$dir"; chmod 755 "$dir"; note "set $dir to root, mode 755"; fi
  dir=$dir/$part
done
[[ -d $inner ]] || { mkdir -p -- "$inner"; note "created $inner"; }
unit_text="[Unit]
Description=$snippets for $user's SFTP chroot (Octopushy)
Before=$service.service

[Mount]
What=$snippets
Where=$inner
Type=none
Options=bind

[Install]
WantedBy=multi-user.target"
if [[ ! -f /etc/systemd/system/$unit ]] || [[ $(cat "/etc/systemd/system/$unit") != "$unit_text" ]]; then
  printf '%s\n' "$unit_text" >"/etc/systemd/system/$unit"
  systemctl daemon-reload
  note "wrote the mount unit $unit"
fi
if ! systemctl is-enabled --quiet "$unit" 2>/dev/null; then systemctl enable --quiet "$unit"; note "enabled $unit"; fi
if ! mountpoint -q -- "$inner"; then systemctl start "$unit"; note "mounted $snippets at $inner"; fi
mountpoint -q -- "$inner" || die "$inner is not mounted"

say "sshd"
conf_text="$conf_marker
Match User $user
    AuthorizedKeysFile $keys
    AuthenticationMethods publickey
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    ChrootDirectory $chroot
    ForceCommand internal-sftp -u 0077
    AllowTcpForwarding no
    AllowAgentForwarding no
    AllowStreamLocalForwarding no
    PermitTunnel no
    PermitTTY no
    PermitUserRC no
    X11Forwarding no"
sshd_changed=0
staged=1
mkdir -p -- "$keys_dir"
chmod 755 "$keys_dir"
if ((!had_keys)) || [[ $old_keys != "$key_line" ]]; then
  printf '%s\n' "$key_line" >"$keys"
  chmod 644 "$keys"
  note "installed the key $fingerprint in $keys"
  sshd_changed=1
fi
if ((!had_conf)) || [[ $old_conf != "$conf_text" ]]; then
  printf '%s\n' "$conf_text" >"$conf"
  chmod 644 "$conf"
  note "wrote $conf"
  sshd_changed=1
fi
if ((sshd_changed)); then
  # What sshd will do for this account: the block above, whatever the connection.
  effective=$(sshd -T -C "user=$user,host=octopushy,addr=127.0.0.1")
  grep -qx "chrootdirectory $chroot" <<<"$effective" || die "sshd would not chroot $user"
  grep -qx "forcecommand internal-sftp -u 0077" <<<"$effective" || die "sshd would not force SFTP for $user"
  apply_sshd
fi
staged=0

say "Done. ${#changed[@]} change(s)."
if ((${#changed[@]} == 0)); then echo "    nothing changed: this host was already set up"; fi
echo
echo "Store this host's SSH host key in Octopushy as a plain value, under the key the"
echo "installation's snippets.hosts.$(hostname -s).hostKey names:"
echo
cat /etc/ssh/ssh_host_ed25519_key.pub
ssh-keygen -l -f /etc/ssh/ssh_host_ed25519_key.pub
