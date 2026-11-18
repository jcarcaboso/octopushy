#!/bin/sh
# Creates the users the agent runs each attempt's tools as, with --tool-runtime local and the
# agent as root (crates/octopushy/src/tools/users.rs): octopushy-tool-0 to -<count - 1>, each with
# a group of its own, no home and no login. The agent finds them by name; their IDs only have
# to be free and unprivileged.
#   tool-users.sh [count] [first id]
set -eu
count=${1:-16}
first=${2:-10100}
i=0
while [ "$i" -lt "$count" ]; do
    id=$((first + i))
    groupadd --gid "$id" "octopushy-tool-$i"
    useradd --no-log-init --no-create-home --uid "$id" --gid "$id" \
        --home-dir /nonexistent --shell /usr/sbin/nologin "octopushy-tool-$i"
    i=$((i + 1))
done
