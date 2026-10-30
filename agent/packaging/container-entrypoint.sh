#!/bin/sh
# The agent image's entrypoint. With arguments, it runs `octopushy` with them (such as
# `agent enroll`). Without, it waits until the agent is enrolled, then runs it with
# OpenTofu as a local binary.
set -eu

if [ "$#" -gt 0 ]; then
    exec /usr/local/bin/octopushy "$@"
fi

state=${STATE_DIRECTORY:-/var/lib/octopushy-agent}
if [ ! -f "$state/agent.json" ]; then
    echo "The agent is not enrolled yet; waiting for an enrollment in $state." >&2
    while [ ! -f "$state/agent.json" ]; do
        sleep 5
    done
fi
exec /usr/local/bin/octopushy agent run --tool-runtime local
