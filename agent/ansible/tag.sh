#!/bin/sh
# Prints the image tag: the ansible-core version and a hash of everything in this folder. A
# change here is a new tag, so a published image never changes under an agent.
set -eu
cd "$(dirname "$0")"
version=$(sed -n 's/^ansible-core==\([^ ]*\).*/\1/p' requirements.lock)
hash=$(find . -type f ! -name tag.sh ! -name '*.pyc' -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -c1-12)
printf 'ansible-%s-%s\n' "$version" "$hash"
