#!/usr/bin/env bash
# Harness-only authorization of an exact locally generated fixture commit.
# Caller provides authenticated call() and api. Never trust an arbitrary remote head.
trust_fixture_revision() {
  local fixture=$1 expected=$2 repository id="" review payload
  [[ $expected =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] || return 1
  for _ in {1..60}; do
    repository=$(call "$api/api/repository") || return 1
    jq -e --arg fixture "$fixture" '.url == $fixture and .branch == "main"' \
      <<<"$repository" >/dev/null || {
      echo "Fixture trust refused: repository source changed." >&2
      return 1
    }
    id=$(jq -r --arg commit "$expected" '
      select(.headCommit == $commit and .revision.commit == $commit and
        .revision.isCurrentSource == true) | .revision.id // empty' <<<"$repository")
    [[ -n $id ]] && break
    sleep 1
  done
  [[ -n $id ]] || {
    echo "Fixture trust refused: expected commit was not loaded." >&2
    return 1
  }
  review=$(call "$api/api/repository/revisions/$id/review") || return 1
  jq -e --arg commit "$expected" --arg id "$id" '
    .revision.id == $id and .revision.commit == $commit and
    .revision.isCurrentSource == true' <<<"$review" >/dev/null || return 1
  if jq -e '.revision.trustedAt != null' <<<"$review" >/dev/null; then
    return 0
  fi
  jq -e '.canTrust == true and .truncated == false' <<<"$review" >/dev/null || {
    echo "Fixture trust refused: review is unavailable, incomplete or ineligible." >&2
    return 1
  }
  payload=$(jq -c '{commit: .revision.commit, baseCommit}' <<<"$review") || return 1
  # A stale review/source is rejected by the server; never retry with a new basis.
  call -X POST "$api/api/repository/revisions/$id/trust" --data-binary "$payload" >/dev/null
}
