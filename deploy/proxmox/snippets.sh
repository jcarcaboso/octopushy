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
# What it does:
#   1. Adds `snippets` to the content of a directory datastore (OCTOPUSHY_SNIPPETS_DATASTORE,
#      default `local`).
#   2. Creates a system account (OCTOPUSHY_SNIPPETS_USER, default `octopushy-snippets`) with
#      no password and no shell.
#   3. Builds a chroot holding only that datastore's snippets directory, at the same path,
#      and bind mounts the real directory into it with a systemd mount unit.
#   4. Adds an sshd `Match User` block: that account may only log in with the key, only use
#      SFTP, only inside the chroot, and new files are created mode 0600.
#   5. Installs the key, checks the whole sshd configuration with `sshd -t`, and only then
#      reloads sshd. A configuration that does not pass is put back as it was.
#   6. Prints what it changed, and this host's SSH host key for Octopushy to pin.
#
# --remove undoes 2 to 5, and leaves the datastore's content and any snippets as they are.
set -euo pipefail

user=${OCTOPUSHY_SNIPPETS_USER:-octopushy-snippets}
datastore=${OCTOPUSHY_SNIPPETS_DATASTORE:-local}
key=${OCTOPUSHY_SNIPPETS_KEY:-}
chroot=/var/lib/$user
conf_dir=/etc/ssh/sshd_config.d
conf=$conf_dir/$user.conf
keys_dir=/etc/ssh/$user
keys=$keys_dir/authorized_keys

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

((EUID == 0)) || die "run this as root on the Proxmox host"
[[ $user =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "not a user name: $user"
# The account is this script's own. It never takes over one that exists, root least of all: the
# sshd block it writes would make that account's logins SFTP-only, in a chroot. Checked before
# anything changes, for --remove as well.
marker="Octopushy snippet uploads"
[[ $user != root ]] || die "the account cannot be root"
if entry=$(getent passwd "$user"); then
  IFS=: read -r _ _ uid _ gecos _ <<<"$entry"
  ((uid != 0)) || die "$user is UID 0; choose another OCTOPUSHY_SNIPPETS_USER"
  [[ $gecos == "$marker" ]] || die "$user exists and this script did not create it; choose another OCTOPUSHY_SNIPPETS_USER"
elif getent group "$user" >/dev/null; then
  die "a group $user exists without this script's account; choose another OCTOPUSHY_SNIPPETS_USER"
fi
[[ $datastore =~ ^[A-Za-z][A-Za-z0-9_.-]*$ ]] || die "not a datastore name: $datastore"
for tool in pvesh pvesm sshd ssh-keygen systemctl systemd-escape; do
  command -v "$tool" >/dev/null || die "$tool is missing: is this a Proxmox VE host?"
done

# sshd's service is `ssh` on Debian, and so on Proxmox.
service=ssh
systemctl list-unit-files ssh.service >/dev/null 2>&1 || service=sshd

reload_sshd() {
  sshd -t || return 1
  systemctl reload "$service"
}

# The datastore's directory, from Proxmox itself.
storage_json=$(pvesh get "/storage/$datastore" --output-format json) || die "there is no datastore $datastore"
path=$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("path", ""))' <<<"$storage_json")
content=$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("content", ""))' <<<"$storage_json")
[[ $path == /* ]] || die "$datastore has no directory path: snippets need a directory datastore"
snippets=$path/snippets
inner=$chroot$snippets
unit=$(systemd-escape -p --suffix=mount "$inner")

if ((remove)); then
  say "Removing the $user account and its SFTP access"
  if [[ -f $conf ]]; then
    rm -f -- "$conf"
    reload_sshd || die "sshd -t failed after removing $conf; fix the configuration, then reload ssh"
    note "removed $conf and reloaded sshd"
  fi
  if [[ -e $keys_dir ]]; then rm -rf -- "$keys_dir"; note "removed $keys_dir"; fi
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

[[ -n $key ]] || die "set OCTOPUSHY_SNIPPETS_KEY to the public key Octopushy shows for this installation"
[[ $key != *$'\n'* ]] || die "OCTOPUSHY_SNIPPETS_KEY must be one line"
read -r key_type key_blob key_comment <<<"$key"
[[ $key_type == ssh-ed25519 ]] || die "expected an ssh-ed25519 public key"
fingerprint=$(ssh-keygen -l -f /dev/stdin <<<"$key_type $key_blob" 2>/dev/null) || die "that is not a valid public key"
key_line="restrict $key_type $key_blob ${key_comment:-$user}"

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
grep -Eqi '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\.d/\*\.conf' /etc/ssh/sshd_config \
  || die "/etc/ssh/sshd_config does not include $conf_dir/*.conf; add that Include line first"
conf_text="# Written by Octopushy's snippets.sh. Remove with: bash snippets.sh --remove
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
# Written, checked as sshd will read it, and reloaded only then. If anything fails, the files are
# put back as they were, and sshd is reloaded again if it already had them, so it keeps running
# what it had. What came before this stage is left for a second run or --remove.
had_conf=0 had_keys=0 old_conf= old_keys=
if [[ -f $conf ]]; then had_conf=1; old_conf=$(cat "$conf"); fi
if [[ -f $keys ]]; then had_keys=1; old_keys=$(cat "$keys"); fi
sshd -t || die "sshd -t refuses the configuration as it is, before any change of this script's; fix that first"
root_before=$(sshd -T -C "user=root,host=octopushy,addr=127.0.0.1")
staged=0 reloaded=0 sshd_changed=0
restore_sshd() {
  ((staged)) || return 0
  if ((had_conf)); then printf '%s\n' "$old_conf" >"$conf"; else rm -f -- "$conf"; fi
  if ((had_keys)); then printf '%s\n' "$old_keys" >"$keys"; else rm -f -- "$keys"; fi
  if ((reloaded)); then systemctl reload "$service"; fi
  printf '\033[31m    sshd: put back as it was\033[0m\n' >&2
}
trap restore_sshd EXIT
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
sshd -t || die "sshd -t refused the configuration"
# What sshd will do for this account, and that root's login is exactly as it was.
effective=$(sshd -T -C "user=$user,host=octopushy,addr=127.0.0.1")
grep -qx "chrootdirectory $chroot" <<<"$effective" || die "sshd would not chroot $user; another Match block may take precedence"
grep -qx "forcecommand internal-sftp -u 0077" <<<"$effective" || die "sshd would not force SFTP for $user"
[[ $(sshd -T -C "user=root,host=octopushy,addr=127.0.0.1") == "$root_before" ]] || die "the configuration would change root's login"
if ((sshd_changed)); then
  reloaded=1
  systemctl reload "$service"
  note "reloaded sshd"
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
