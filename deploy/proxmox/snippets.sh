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
#      chroot, and new files are created mode 0600. No CA certificate, key or principals command,
#      password, host-based, GSSAPI or Kerberos login lets it in either. sshd takes the first value
#      it finds for these settings, so the block holds for every connection of that account, and it
#      names no other.
#   5. Copies sshd's files, checks the whole configuration with `sshd -t` and the account's
#      settings with `sshd -T`, and only then reloads sshd. If that fails, the copies go back,
#      byte for byte, and sshd is reloaded again if it already had the new files.
#   6. Prints what it changed, and this host's SSH host key for Octopushy to pin.
#
# It changes nothing, and says why, if: the account is root or exists without this script's
# marker; sshd_config does not include sshd_config.d at its top; sshd -t already fails; the
# drop-in's name is taken by a file this script did not write, or another drop-in sorts before
# it; or any other sshd file mentions the account.
#
# It also refuses a link, or anything but a regular file or directory, at any path it writes or
# backs up.
#
# --remove undoes 2 to 5 with the same checks, and also refuses while the account has a process,
# a process uses the chroot, or the chroot holds something it did not make. It locks and expires
# the account and deletes its key first, and removes the block last, so if a step fails part way
# the account still has no way in; run --remove again to carry on. It leaves the datastore's
# content and the snippets, which it gives to root first, so a later account with the same UID
# cannot read them.
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

# A whole connection for sshd -T, so a Match on any of its parts can be evaluated. What the
# script relies on does not depend on these values: its block holds for every connection.
tuple() { printf 'user=%s,host=octopushy,addr=127.0.0.1,laddr=127.0.0.1,lport=22,rdomain=default' "$1"; }

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
for tool in pvesh pvesm sshd ssh-keygen systemctl systemd-escape python3 pgrep; do
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

# Every path the script writes, backs up or deletes is what it looks like: a regular file or a
# directory, never a link to somewhere else, so a copy is of what sshd reads and a write lands
# where it is meant to.
for path in "$conf" "$keys" "/etc/systemd/system/$unit"; do
  if [[ -L $path || ( -e $path && ! -f $path ) ]]; then die "$path is not a regular file; nothing was changed"; fi
done
dirs=("$keys_dir" "$snippets" "$chroot")
dir=$chroot
for part in ${snippets//\// }; do dir=$dir/$part; dirs+=("$dir"); done
for path in "${dirs[@]}"; do
  if [[ -L $path || ( -e $path && ! -d $path ) ]]; then die "$path is not a directory; nothing was changed"; fi
done

# ---- sshd's files: written, checked and reloaded, or put back. ----

# Copies of sshd's files as they are, bytes, mode and owner, in a directory only root can read.
backup=$(mktemp -d /run/octopushy-snippets.XXXXXX)
chmod 700 "$backup"
had_conf=0 had_keys=0 old_conf= old_keys=
if [[ -f $conf ]]; then had_conf=1; cp -a -- "$conf" "$backup/conf"; old_conf=$(cat "$conf"); fi
if [[ -f $keys ]]; then had_keys=1; cp -a -- "$keys" "$backup/keys"; old_keys=$(cat "$keys"); fi
root_before=$(sshd -T -C "$(tuple root)")
staged=0 reloaded=0
restore_sshd() {
  if ((staged)); then
    if ((had_conf)); then cp -a -- "$backup/conf" "$conf"; else rm -f -- "$conf"; fi
    if ((had_keys)); then mkdir -p -- "$keys_dir"; cp -a -- "$backup/keys" "$keys"; else rm -f -- "$keys"; fi
    if ((reloaded)); then systemctl reload "$service" || true; fi
    printf '\033[31m    sshd: put back as it was\033[0m\n' >&2
  fi
  rm -rf -- "$backup"
}
trap restore_sshd EXIT

# Checks what sshd would read, then reloads it. Any failure here leaves restore_sshd to run.
apply_sshd() {
  sshd -t || die "sshd -t refused the configuration"
  [[ $(sshd -T -C "$(tuple root)") == "$root_before" ]] || die "the configuration would change root's login"
  reloaded=1
  systemctl reload "$service"
  note "reloaded sshd"
}

if ((remove)); then
  say "Removing the $user account and its SFTP access"
  # What would stop the removal part way, checked while it can still be refused: a process of the
  # account, such as an open SFTP session; anything using the chroot, such as a process working in
  # it, which would keep it from being unmounted; another mount in it; and anything in it but its
  # directories and the mounted snippets directory.
  if id -u "$user" >/dev/null 2>&1 && pgrep -u "$user" >/dev/null; then
    die "processes of $user are running, such as an SFTP session: $(pgrep -u "$user" -d ' '); nothing was changed"
  fi
  if [[ -d $chroot ]]; then
    users=()
    for proc in /proc/[0-9]*; do
      for link in "$proc"/cwd "$proc"/root "$proc"/fd/*; do
        target=$(readlink -- "$link" 2>/dev/null) || continue
        if [[ $target == "$chroot" || $target == "$chroot"/* ]]; then users+=("${proc#/proc/}"); break; fi
      done
    done
    ((${#users[@]} == 0)) || die "processes use $chroot, so it cannot be unmounted: ${users[*]}; nothing was changed"
    others=$(findmnt -rn -o TARGET | grep -E "^$chroot(/|\$)" | grep -vxF -- "$inner" || true)
    [[ -z $others ]] || die "other file systems are mounted in $chroot: $others; nothing was changed"
    stray=()
    while IFS= read -r found; do
      # The directories on the way to the snippets directory are the script's; anything else is not.
      [[ -d $found && ! -L $found && $inner == "$found"/* ]] || stray+=("$found")
    done < <(find "$chroot" -mindepth 1 -path "$inner" -prune -o -print)
    ((${#stray[@]} == 0)) || die "$chroot holds more than this script made: ${stray[*]}; nothing was changed"
  fi

  # The restrictive block is the last thing to go. Until then, every way into the account but its
  # key is off, so taking the key away and expiring the account leaves no way in at all. If any
  # step below fails, the block is still there, and --remove can be run again to carry on.
  if id -u "$user" >/dev/null 2>&1; then
    if [[ $(getent shadow "$user" | cut -d: -f8) != 1 ]]; then usermod -L -e 1 "$user"; note "locked and expired $user"; fi
  fi
  if [[ -f $keys ]]; then rm -f -- "$keys"; note "removed $keys, so $user has no key that sshd accepts"; fi
  if [[ -d $keys_dir ]]; then rmdir -- "$keys_dir"; note "removed $keys_dir"; fi
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
  # The snippets stay, and they hold guests' private host keys. Before the account's UID is free
  # for another account to get, everything in the directory that the account owns becomes root's,
  # without following a link anywhere, and keeps its mode.
  if [[ -d $snippets ]] && id -u "$user" >/dev/null 2>&1; then
    owned=$(find "$snippets" -xdev -mindepth 1 \( -user "$user" -o -group "$user" \) -print | wc -l)
    if ((owned > 0)); then
      find "$snippets" -xdev -mindepth 1 \( -user "$user" -o -group "$user" \) -exec chown -h root:root {} +
      note "gave root the $owned file(s) of $user in $snippets"
    fi
  fi
  if [[ -d $snippets && $(stat -c '%G %a' "$snippets") == "$user 2770" ]]; then
    chgrp root "$snippets"
    chmod 00755 "$snippets"
    note "gave $snippets back to root, mode 755"
  fi
  if id -u "$user" >/dev/null 2>&1; then userdel "$user"; note "removed the user $user"; fi
  if getent group "$user" >/dev/null; then groupdel "$user"; note "removed the group $user"; fi

  # Last, the block itself: removed, checked and reloaded, or put back byte for byte.
  if ((had_conf)); then
    had_keys=0
    staged=1
    rm -f -- "$conf"
    note "removed $conf"
    apply_sshd
    staged=0
  fi
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
# An expiry is what a --remove that stopped part way leaves; setting up again clears it.
if [[ -n $(getent shadow "$user" | cut -d: -f8) ]]; then usermod -e '' "$user"; note "cleared the expiry of $user"; fi
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
    AuthorizedKeysCommand none
    AuthorizedPrincipalsFile none
    AuthorizedPrincipalsCommand none
    TrustedUserCAKeys none
    AuthenticationMethods publickey
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    HostbasedAuthentication no
    GSSAPIAuthentication no
    KerberosAuthentication no
    PermitEmptyPasswords no
    DisableForwarding yes
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
  # What sshd will do for this account: the block above, whatever the connection. Only the key in
  # its file lets it in: no certificate, no command, no password and no other method.
  effective=$(sshd -T -C "$(tuple "$user")")
  for expected in "authorizedkeysfile $keys" "authorizedkeyscommand none" "authorizedprincipalsfile none" \
    "authorizedprincipalscommand none" "trustedusercakeys none" "authenticationmethods publickey" \
    "passwordauthentication no" "kbdinteractiveauthentication no" "hostbasedauthentication no" \
    "gssapiauthentication no" "kerberosauthentication no" "permitemptypasswords no" "disableforwarding yes" \
    "chrootdirectory $chroot" "forcecommand internal-sftp -u 0077" "permittty no" "allowtcpforwarding no"; do
    grep -qxF -- "$expected" <<<"$effective" || die "sshd would not apply '$expected' to $user"
  done
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
