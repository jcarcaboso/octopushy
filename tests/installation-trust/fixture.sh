#!/usr/bin/env bash
# Harness-only trust of the Proxmox installations in a locally generated fixture.
# Caller provides authenticated call() and api. Never use it for an ordinary lab repository.
trust_fixture_installations() {
  local fixture=$1 expected=$2 repository loaded="" inventory
  [[ $expected =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] || return 1
  for _ in {1..60}; do
    repository=$(call "$api/api/repository") || return 1
    jq -e --arg fixture "$fixture" '.url == $fixture and .branch == "main"' \
      <<<"$repository" >/dev/null || {
      echo "Fixture trust refused: repository source changed." >&2
      return 1
    }
    loaded=$(jq -r --arg commit "$expected" '
      select(.headCommit == $commit and .revision.commit == $commit and
        .revision.isCurrentSource == true) | .revision.id // empty' <<<"$repository")
    [[ -n $loaded ]] && break
    sleep 1
  done
  [[ -n $loaded ]] || {
    echo "Fixture trust refused: expected commit was not loaded." >&2
    return 1
  }
  inventory=$(call "$api/api/inventory") || return 1
  # Trust exactly the endpoints shown; the server refuses them if the definition moved on.
  jq -c '.labs[].installations[] | select(.valid and (.trust.untrusted | length) > 0)
    | {id, body: {endpoints: .trust.endpoints}}' <<<"$inventory" |
    while read -r installation; do
      call -X POST "$api/api/installations/$(jq -r .id <<<"$installation")/trust" \
        --data-binary "$(jq -c .body <<<"$installation")" >/dev/null || return 1
    done
}
