#!/usr/bin/env bash
# Produces an immutable public artifact only. Does not upload, change Git, or deploy.
set -euo pipefail
umask 077
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
output="$(realpath -m "${1:?Pass a fresh artifact directory}")"
[[ ! -e "$output/octopushy-worker-v1.qcow2" ]] || { echo "Refusing to overwrite an existing image." >&2; exit 1; }
mkdir -p "$output"
source_dir="$(mktemp -d)"
trap 'rm -rf "$source_dir"' EXIT
# No private workspaces, .dev, tool caches, Git config or host identities enter the image build.
python3 - "$root" "$source_dir" <<'PY'
from pathlib import Path
import shutil, sys
root, target = map(Path, sys.argv[1:])
for name in ("flake.nix", "flake.lock"):
    shutil.copy2(root / name, target / name)
for name in ("agent", "contracts/agent", "examples/lab/modules/nixos", "deploy/worker-image"):
    shutil.copytree(root / name, target / name, ignore=shutil.ignore_patterns("target", ".dev", "node_modules", ".git"))
PY
[[ -r /dev/kvm && -w /dev/kvm ]] || { echo "Accessible /dev/kvm is required. No host permissions changed." >&2; exit 1; }
# Use a dedicated engine (CONTAINER_HOST=unix://... with podman --remote) when
# rehearsing locally. Never share this volume with a controller or worker.
podman_args=()
if [[ -n "${CONTAINER_HOST:-}" ]]; then podman_args=(--remote --url "$CONTAINER_HOST"); fi
podman "${podman_args[@]}" run --rm --name octopushy-worker-image-build \
  --cpus=2 --memory=4g --pids-limit=512 --device /dev/kvm \
  --cap-drop=all --cap-add=CHOWN --cap-add=FOWNER --cap-add=DAC_OVERRIDE \
  --cap-add=SETUID --cap-add=SETGID --cap-add=SYS_CHROOT --cap-add=FSETID \
  --cap-add=MKNOD --cap-add=SETFCAP --cap-add=SETPCAP --cap-add=DAC_READ_SEARCH \
  --security-opt=no-new-privileges --security-opt=label=disable \
  -v octopushy-worker-image-nix:/nix -v "$source_dir:/src:ro" -v "$output:/artifacts" \
  docker.io/nixos/nix@sha256:53b5ec518b696373829ea305c3c07a8c870113e44648c88ec484ef078550fd4f \
  bash -euc '
    export NIX_CONFIG="experimental-features = nix-command flakes
    sandbox = false
    build-users-group =
    system-features = kvm
    max-jobs = 1
    cores = 2"
    nix flake check --no-update-lock-file --no-build path:/src
    nix build -L --no-update-lock-file --out-link /nix/var/nix/gcroots/worker-v1 path:/src#worker-image-v1
    nix build --no-update-lock-file --out-link /nix/var/nix/gcroots/worker-qemu path:/src#qemu
    image=/nix/var/nix/gcroots/worker-v1/octopushy-worker-v1.img
    test -f "$image"
    qemu=/nix/var/nix/gcroots/worker-qemu/bin/qemu-img
    "$qemu" convert -f raw -O qcow2 -c "$image" /artifacts/octopushy-worker-v1.qcow2
    "$qemu" check /artifacts/octopushy-worker-v1.qcow2
    cd /artifacts
    sha256sum octopushy-worker-v1.qcow2 > SHA256SUMS
    firmware="$(nix build --no-update-lock-file --no-link --print-out-paths path:/src#firmware)"
    python="$(nix build --no-update-lock-file --no-link --print-out-paths path:/src#smoke-python)"
    "$python/bin/python3" /src/deploy/worker-image/smoke.py \
      --image /artifacts/octopushy-worker-v1.qcow2 \
      --qemu /nix/var/nix/gcroots/worker-qemu --firmware "$firmware" \
      > /artifacts/boot-smoke.log
    cat /artifacts/boot-smoke.log
  '
printf 'Image, SHA256SUMS and passing no-network boot-smoke.log written under %s. Not uploaded or deployed.\n' "$output"
