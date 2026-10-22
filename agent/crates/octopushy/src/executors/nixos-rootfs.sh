# Fixed executor program, not repository-supplied shell. Runs as root via QGA.
# Intentionally supports only our GPT /dev/sda1 EFI + /dev/sda2 ext4 base image.
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
[[ $(readlink -f "$(findmnt -n -o SOURCE /)") == /dev/sda2 ]] || fail "Unsupported root device"
[[ $(readlink -f /dev/disk/by-label/nixos) == /dev/sda2 ]] || fail "Unexpected root label"
[[ $(lsblk -dn -o PTTYPE /dev/sda) == gpt ]] || fail "Only GPT is supported"
[[ $(lsblk -nr -o NAME /dev/sda) == $'sda\nsda1\nsda2' ]] || fail "Unexpected disk layout"
[[ $(lsblk -dn -o FSTYPE /dev/sda1) == vfat ]] || fail "Unexpected EFI partition"
[[ $(blockdev --getss /dev/sda) == 512 ]] || fail "Unsupported sector size"
disk=$(blockdev --getsize64 /dev/sda)
[[ $disk == "$expected" ]] || fail "Guest disk capacity does not match the approved desired disk"
start=$(cat /sys/class/block/sda2/start)
[[ $start =~ ^[0-9]+$ ]] && (( start > 0 && start * 512 <= 1073741824 )) || fail "Unexpected partition offset"
uuid=$(findmnt -n -o UUID /)
[[ $uuid =~ ^[a-fA-F0-9-]{36}$ ]] || fail "Missing root filesystem identity"
measure() {
    partition=$(blockdev --getsize64 /dev/sda2)
    # Read the ext4 superblock's real block count, not df's metadata-adjusted capacity.
    header=$(dumpe2fs -h /dev/sda2 2>/dev/null)
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
