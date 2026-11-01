# Fixed executor program, not repository-supplied shell. Runs as root via QGA.
# Intentionally supports only our GPT EFI + ext4 base image, on a SCSI disk. Disks are named
# in probe order, so a VM with a data disk can boot from sda or sdb: find the disk from the
# mounted root, and never assume its name or look for it among the other disks.
set -euo pipefail
export PATH=/run/current-system/sw/bin
export LC_ALL=C
fail() { printf '%s\n' "$*" >&2; exit 1; }
mode=$1 expected=$2
[[ $mode == grow || $mode == verify ]] || fail "Unsupported action"
[[ $expected =~ ^[0-9]+$ ]] || fail "Invalid disk capacity"
. /etc/os-release
[[ $ID == nixos ]] || fail "Only NixOS is supported"
[[ $(id -u) == 0 ]] || fail "Guest-agent root access is required"
[[ $(findmnt -n -o FSTYPE /) == ext4 ]] || fail "Root is not ext4"
root=$(readlink -f "$(findmnt -n -o SOURCE /)") || fail "Unsupported root device"
[[ $root =~ ^/dev/(sd[a-z]+)2$ ]] || fail "Unsupported root device"
name=${BASH_REMATCH[1]}
root_disk=/dev/$name
sys=/sys/class/block
[[ $(lsblk -dn -o TYPE "$root") == part ]] || fail "Unsupported root device"
[[ $(lsblk -dn -o PKNAME "$root") == "$name" ]] || fail "Unsupported root device"
[[ $(lsblk -dn -o TYPE "$root_disk") == disk ]] || fail "Unsupported root device"
# growpart.service finds its target through this label, so it must name the mounted root.
[[ $(readlink -f /dev/disk/by-label/nixos) == "$root" ]] || fail "Unexpected root label"
[[ $(lsblk -nrp -o NAME,LABEL | awk '$2 == "nixos" {print $1}') == "$root" ]] || fail "Unexpected root label"
[[ $(readlink -f /dev/disk/by-id/opy-worker-state-v1) != "$root_disk" ]] || fail "The root disk is the worker state disk"
[[ $(lsblk -dn -o PTTYPE "$root_disk") == gpt ]] || fail "Only GPT is supported"
[[ $(lsblk -nr -o NAME "$root_disk") == "$name"$'\n'"${name}1"$'\n'"${name}2" ]] || fail "Unexpected disk layout"
[[ $(lsblk -dn -o FSTYPE "/dev/${name}1") == vfat ]] || fail "Unexpected EFI partition"
[[ $(blockdev --getss "$root_disk") == 512 ]] || fail "Unsupported sector size"
disk=$(blockdev --getsize64 "$root_disk")
[[ $disk == "$expected" ]] || fail "Guest disk capacity does not match the approved desired disk"
start=$(cat "$sys/${name}2/start")
[[ $start =~ ^[0-9]+$ ]] && (( start > 0 && start * 512 <= 1073741824 )) || fail "Unexpected partition offset"
efi_start=$(cat "$sys/${name}1/start")
efi_sectors=$(cat "$sys/${name}1/size")
[[ $efi_start =~ ^[0-9]+$ && $efi_sectors =~ ^[0-9]+$ ]] && (( efi_start + efi_sectors <= start )) || fail "Unexpected partition offset"
uuid=$(findmnt -n -o UUID /)
[[ $uuid =~ ^[a-fA-F0-9-]{36}$ ]] || fail "Missing root filesystem identity"
measure() {
    partition=$(blockdev --getsize64 "$root")
    # Read the ext4 superblock's real block count, not df's metadata-adjusted capacity.
    header=$(dumpe2fs -h "$root" 2>/dev/null)
    [[ $(awk '/^Filesystem UUID:/ {print $3}' <<<"$header") == "$uuid" ]] || fail "Root partition is not the mounted filesystem"
    [[ $(awk '/^Filesystem volume name:/ {print $4}' <<<"$header") == nixos ]] || fail "Unexpected root label"
    blocks=$(awk '/^Block count:/ {print $3}' <<<"$header")
    blocksize=$(awk '/^Block size:/ {print $3}' <<<"$header")
    [[ $blocks =~ ^[0-9]+$ && $blocksize =~ ^[0-9]+$ ]] || fail "Cannot read ext4 capacity"
    filesystem=$((blocks * blocksize))
    gap=$((disk - start * 512 - partition))
    (( gap >= 0 && filesystem <= partition )) || fail "Inconsistent disk geometry"
}
measure
changed=false
if [[ $mode == grow ]] && (( gap > 1048576 || partition - filesystem >= blocksize )); then
    for unit in growpart.service systemd-growfs-root.service; do
        [[ $(systemctl show "$unit" -p LoadState --value) == loaded ]] || fail "$unit is not loaded"
        state=$(systemctl show "$unit" -p ActiveState --value)
        [[ $state != activating && $state != deactivating ]] || fail "$unit is already changing state; inspect it"
    done
    systemctl restart growpart.service >&2
    systemctl restart systemd-growfs-root.service >&2
    changed=true
    measure
fi
(( gap <= 1048576 )) || fail "Root partition has not expanded to the disk capacity"
(( partition - filesystem < blocksize )) || fail "Root filesystem has not expanded to the partition capacity"
[[ $(findmnt -n -o UUID /) == "$uuid" ]] || fail "Root filesystem identity changed"
read -r usable_blocks usable_blocksize < <(stat -f -c '%b %S' /)
printf '{"diskBytes":%s,"partitionBytes":%s,"filesystemBytes":%s,"usableBytes":%s,"uuid":"%s","changed":%s}\n' \
    "$disk" "$partition" "$filesystem" "$((usable_blocks * usable_blocksize))" "$uuid" "$changed"
