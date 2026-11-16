# Confirm workflows sources before fetching

Extends [0002](0002-installation-trust.md): an operator confirms the workflows
repository URL and credential store reference before the server fetches it. A
commit can propose another pair, including a different pinned credential version,
but cannot authorize a connection or select a secret by itself. Confirmation
requires an interactive operator session and names the discovered lab source,
commit and exact pair; a stale confirmation fails rather than trusting a newer
proposal. Existing installations must also confirm their first external source
after upgrading. We do not infer consent from previous fetch history.

While a proposal waits, the server retains the applied revision and confirmed
source for existing work. It may refresh that confirmed mirror, but does not load
the proposed head until its source is confirmed. Neither confirmation nor pending
state enters revision identity: commit, resolution digest, immutable releases and
pinned workflow commits keep their existing meaning. Trust changes and lab-source
changes discard stale sync results. Changing or disconnecting the lab source
clears its workflows trust.

The initial connection is two explicit steps: connect the lab repository, then
review and confirm the workflows pair discovered in its first fetched commit.
This avoids sending credentials to a repository-declared URL during discovery.
The Repository page shows “Waiting for trust”, the old and proposed pairs, and an
acknowledgement before confirmation. Automation tokens cannot grant this trust.

Repository-declared absolute paths and `file://` URLs are disabled by default,
even after confirmation. `Octopushy:AllowLocalWorkflowSources=true` permits them
for disposable tests or an explicitly configured local deployment; it does not
skip confirmation. The offline validator intentionally accepts local sources:
it runs at the invoking operator's request, not as a credentialed server polling
hostile repository content. Confirmation does not relax the system-key, cleartext
credential, or lab-credential forwarding restrictions.
