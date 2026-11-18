#!/usr/bin/env bash
# The product version: one version for the backend, the frontend and the agent.
#
#   scripts/release.sh <version>          set it in every file that carries it, then check
#   scripts/release.sh --check <version>  fail unless every file carries <version>
#   scripts/release.sh --check            fail unless every file carries the same version
#
# <version> is MAJOR.MINOR.PATCH, without the tag's leading v. Setting a version twice
# changes nothing the second time. The release notes in README.md are written by hand.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

die() { echo "release.sh: $*" >&2; exit 1; }

# Each site: a file, a sed address that limits it to the right lines, and a sed -E pattern
# whose second group is the version. Cargo.lock holds one entry per workspace crate.
files=() addresses=() patterns=() labels=()
site() { files+=("$1"); addresses+=("$2"); patterns+=("$3"); labels+=("${4:-$1}"); }
site backend/Directory.Build.props '1,$' '^(    <Version>)([^<]*)(</Version>)$'
site agent/Cargo.toml '/^\[workspace\.package\]$/,/^\[/' '^(version = ")([^"]*)(")$'
for crate in octopushy octopushy-proto octopushy-proxmox mock-proxmox; do
  site agent/Cargo.lock "/^name = \"$crate\"\$/,+1" '^(version = ")([^"]*)(")$' "agent/Cargo.lock($crate)"
done
site frontend/package.json '1,$' '^(  "version": ")([^"]*)(",)$'
site deploy/proxmox/install.sh '1,$' '^(ask OCTOPUSHY_VERSION "[^"]*" )([^ ]*)()$'
# shellcheck disable=SC2016 # literal backticks in a Markdown table
site deploy/proxmox/README.md '1,$' '^(\| `OCTOPUSHY_VERSION` \| `)([^`]*)(` \|.*)$'

valid() { [[ $1 =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; }

# Prints "<site> <version>" for every site; a site that matches no line is an error.
current() {
  local i found
  for i in "${!files[@]}"; do
    found=$(sed -nE "${addresses[i]}{s#${patterns[i]}#\\2#p}" "${files[i]}")
    [[ $(wc -l <<<"$found") -eq 1 && -n $found ]] \
      || die "${labels[i]}: expected one version line, found ${found:-none}"
    echo "${labels[i]} $found"
  done
}

check() {
  local want=$1 file version bad=0 lines
  lines=$(current)
  while read -r file version; do
    want=${want:-$version}
    if [[ $version != "$want" ]]; then
      echo "$file carries $version, not $want" >&2
      bad=1
    fi
  done <<<"$lines"
  ((bad == 0)) || die "the version files disagree (set them with: scripts/release.sh <version>)"
  echo "Every version file carries $want."
}

set_version() {
  local version=$1 i
  for i in "${!files[@]}"; do
    sed -i -E "${addresses[i]}{s#${patterns[i]}#\\1${version}\\3#}" "${files[i]}"
  done
  check "$version"
}

case "${1:-}" in
  --check)
    [[ $# -le 2 ]] || die "usage: scripts/release.sh --check [<version>]"
    if [[ $# -eq 2 ]]; then
      valid "$2" || die "not a version: '$2' (expected MAJOR.MINOR.PATCH, without a leading v)"
    fi
    check "${2:-}"
    ;;
  -* | '')
    die "usage: scripts/release.sh <version> | --check [<version>]"
    ;;
  *)
    [[ $# -eq 1 ]] || die "usage: scripts/release.sh <version>"
    valid "$1" || die "not a version: '$1' (expected MAJOR.MINOR.PATCH, without a leading v)"
    current >/dev/null
    set_version "$1"
    echo "Next: write the release notes in README.md, commit, and tag v$1."
    ;;
esac
