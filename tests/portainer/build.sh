#!/usr/bin/env bash
# Build only; no Proxmox mutation or service exposure.
set -euo pipefail
umask 077
config=$(realpath "${1:?Pass the locked deployment flake directory}")
output=$(realpath -m "${2:?Pass the artifact/evidence directory}")
mkdir -p "$output"
[[ ! -e "$output/octopushy-nixos-service-node.qcow2" ]] || {
  echo "Artifact already exists. Use a fresh output directory; do not overwrite a reviewed image." >&2
  exit 1
}
[[ -r /dev/kvm && -w /dev/kvm ]] || { echo "The image build needs accessible /dev/kvm; do not change host permissions automatically." >&2; exit 1; }
podman run --rm --name octopushy-service-image-build \
  --cpus=2 --memory=2200m --pids-limit=256 --device /dev/kvm \
  --cap-drop=all --cap-add=CHOWN --cap-add=FOWNER --cap-add=DAC_OVERRIDE \
  --cap-add=SETUID --cap-add=SETGID --cap-add=SYS_CHROOT \
  --cap-add=FSETID --cap-add=MKNOD --cap-add=SETFCAP --cap-add=SETPCAP \
  --cap-add=DAC_READ_SEARCH \
  --security-opt=no-new-privileges --security-opt=label=disable \
  -v octopushy-nix-validation-store:/nix -v "$config:/config:ro" -v "$output:/artifacts" \
  docker.io/nixos/nix@sha256:53b5ec518b696373829ea305c3c07a8c870113e44648c88ec484ef078550fd4f \
  bash -euc '
    export NIX_CONFIG="experimental-features = nix-command flakes
    sandbox = false
    build-users-group =
    system-features = kvm
    max-jobs = 1
    cores = 2"
    nix build -L --no-update-lock-file --out-link /nix/var/nix/gcroots/octopushy-service-image path:/config#image
    nix build --no-update-lock-file --out-link /nix/var/nix/gcroots/octopushy-service-qemu path:/config#qemu
    image=/nix/var/nix/gcroots/octopushy-service-image/octopushy-nixos-service-node.img
    test -f "$image"
    qemu=/nix/var/nix/gcroots/octopushy-service-qemu/bin/qemu-img
    "$qemu" convert -f raw -O qcow2 -c "$image" /artifacts/octopushy-nixos-service-node.qcow2
    "$qemu" check /artifacts/octopushy-nixos-service-node.qcow2
    sha256sum /artifacts/octopushy-nixos-service-node.qcow2
  '
