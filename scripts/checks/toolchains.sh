#!/usr/bin/env bash
# The toolchains the images and CI build with, and the check that the places naming them agree.
#
#   scripts/checks/toolchains.sh        check
#   scripts/checks/toolchains.sh sdk    print the .NET SDK version (CI installs exactly this one)
#   scripts/checks/toolchains.sh rust   print the Rust toolchain (CI installs exactly this one)
#
# .NET: the exact SDK is the tag of the SDK image in backend/Containerfile, the file Dependabot
# bumps. global.json stays the rule for a developer's machine (rollForward latestFeature, from
# #90), so the pinned SDK has to be one that global.json accepts, and the validator image has to
# build with the same SDK image, digest and all.
# Rust: agent/rust-toolchain.toml names the toolchain, and the agent image builds with it.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.."

die() { echo "toolchains.sh: $*" >&2; exit 1; }

# The image reference after FROM on the line naming <image>: one line, tag and digest.
from() {
  local file=$1 image=$2 refs
  refs=$(sed -nE "s#^FROM ${image//./\\.}:([^ ]+).*#\\1#p" "$file")
  [[ -n $refs && $(wc -l <<<"$refs") -eq 1 ]] || die "$file: expected one FROM $image"
  [[ $refs =~ ^[^@]+@sha256:[0-9a-f]{64}$ ]] || die "$file: $image:$refs is not pinned by tag and digest"
  echo "$refs"
}

sdk_image=$(from backend/Containerfile mcr.microsoft.com/dotnet/sdk)
sdk=${sdk_image%@*}
rust_image=$(from agent/Containerfile docker.io/library/rust)
rust=${rust_image%%-*}

case "${1:-}" in
  sdk) echo "$sdk"; exit ;;
  rust) echo "$rust"; exit ;;
  '') ;;
  *) die "usage: scripts/checks/toolchains.sh [sdk|rust]" ;;
esac

bad=0
fail() { echo "$*" >&2; bad=1; }

# .NET SDK
[[ $sdk =~ ^([0-9]+)\.([0-9]+)\.([0-9])([0-9]{2})$ ]] \
  || die "backend/Containerfile: the SDK image tag $sdk is not an exact SDK version such as 10.0.404"
validate_image=$(from backend/src/Octopushy.Validate/Containerfile mcr.microsoft.com/dotnet/sdk)
[[ $validate_image == "$sdk_image" ]] \
  || fail "backend/src/Octopushy.Validate/Containerfile builds with sdk:$validate_image, backend/Containerfile with sdk:$sdk_image"

floor=$(sed -nE 's/^ *"version": *"([^"]+)".*/\1/p' global.json)
roll=$(sed -nE 's/^ *"rollForward": *"([^"]+)".*/\1/p' global.json)
[[ $floor =~ ^([0-9]+)\.([0-9]+)\.([0-9])([0-9]{2})$ ]] || die "global.json: cannot read sdk.version"
# How far the SDK may roll forward from global.json's version and still be accepted.
case "$roll" in
  disable) same=$floor ;;
  latestPatch) same="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}" ;;
  latestFeature) same="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}." ;;
  *) die "global.json: rollForward '$roll' is not one this check knows; teach it here" ;;
esac
if [[ $sdk != "$same"* ]] || [[ $(printf '%s\n' "$floor" "$sdk" | sort -V | head -n1) != "$floor" ]]; then
  fail "global.json (version $floor, rollForward $roll) does not accept the SDK $sdk that backend/Containerfile pins"
fi

# Rust
channel=$(sed -nE 's/^channel = "([^"]+)"$/\1/p' agent/rust-toolchain.toml)
[[ $rust_image =~ ^[0-9]+\.[0-9]+\.[0-9]+-[a-z]+@ ]] \
  || fail "agent/Containerfile: the Rust image tag ${rust_image%@*} is not an exact toolchain such as 1.98.1-trixie"
[[ $channel == "$rust" ]] \
  || fail "agent/rust-toolchain.toml names ${channel:-no channel}, agent/Containerfile builds with rust:$rust"

((bad == 0)) || exit 1
echo "Toolchains agree: .NET SDK $sdk (global.json $floor, $roll), Rust $rust."
