# Trusted configuration revisions

This gate separates **discovering configuration**, **trusting its execution** and
**approving an infrastructure change**. It does not introduce a general policy
engine or change who owns OpenTofu state.

## Operator workflow

1. Configure and sync the lab repository as before. Octopushy can fetch, parse,
   validate and show a revision without trusting it for execution.
2. On the Repository page, review the exact revision. The review identifies the
   full target commit and compares it with the latest other trusted revision of
   the current source. The first review compares against an empty repository.
3. Inspect the configuration and tool-native code, not just the VM values. The
   confirmation permits credential-bearing work such as planning and observing.
   A truncated, unavailable, invalid or previous-source review cannot be trusted.
4. Explicitly trust that revision. The controller records the authenticated
   operator and time. A stale review is rejected; reload and review it again
   instead of silently accepting a new baseline.
5. Eligible work can proceed through the ordinary scheduling and credential
   checks. If policy requires apply approval, review and approve that plan
   separately. Existing automatic-apply policies may proceed without another
   operator click after trust is granted; trust does not turn them into manual
   approval policies.

A new commit starts untrusted, even if it changes only documentation, is a
descendant of a trusted commit, or proposes the same infrastructure inputs.
No branch-wide or automatically inherited trust is created.

The demo intentionally leaves trust to the operator. Automated test harnesses
may explicitly trust **their own exact local fixture commits** through the same
API. That is a test authorization, not a production auto-trust setting.

## What is bound

Trust belongs to a loaded revision and its repository-source generation, not
just a commit string. Changing the repository URL or branch, or disconnecting
and reconnecting it, creates a new generation. Returning to the old URL or
encountering the same commit hash does not restore its earlier authority.
Changing polling frequency alone does not create a different source.

Runs retain their fixed revision. A newer untrusted revision does not remove
trust from already prepared work at an older revision of the same source.
Scheduled observations use the latest eligible trusted revision rather than
executing newly discovered untrusted configuration. Discovered desired state
can therefore be ahead of the revision used for execution; that difference is
intentional.

The controller checks trust before releasing execution credentials and building
assignments, including after retries, requeues and restarts. Source changes and
dispatch use the existing controller gate. Work without sufficient provenance
stays blocked with a reason instead of being reported as a failed execution.
Trusting a revision does not override missing/revoked credential versions,
capacity constraints, apply approval or an uncertain outcome.

Already delivered credentials and running mutations cannot be recalled by this
gate. Their results and uncertainty must still be reconciled. Changing repository
settings is not a way to cancel an external operation or authorize its replay.

## API

The authenticated repository API exposes:

- `GET /api/repository/revisions/{id}/review`: exact revision, comparison
  `baseCommit`, text `diff`, `truncated`, `canTrust` and `blockedReason`.
- `POST /api/repository/revisions/{id}/trust`: submit the `commit` and
  `baseCommit` from the displayed review. The server checks the source and
  comparison again and derives the actor from the session.
- Revision metadata: `trustedAt`, `trustedBy` and `isCurrentSource`.

The patch is shown as text, never executed or interpreted as HTML. Review uses
native Git without external diff helpers or text conversion. Binary-change
patches are included in the bounded diff rather than silently replaced by an
omitted-content marker. A textual patch that is not valid UTF-8 is refused rather
than displayed with replacement characters; normalize the source encoding before
requesting trust. Encoded binary patches are not a content audit; review any
opaque payload independently before trusting it.

Execution snapshots preserve literal checked-in blob contents: repository
`export-subst` cannot turn commit messages into different executable bytes, and
`export-ignore` cannot silently remove reviewed files. Checkout conversions
(line endings, `ident`, filters and working-tree encoding) are also disabled.
Controller-owned archive attributes enforce this native Git behavior. Regular
tracked files are included; do not use archive attributes to keep secrets off a
worker. The existing safe extractor still excludes symlinks and special entries,
and does not fetch submodule contents.
The derived cache namespaces are `git/snapshots-literal-v3` and
`git/bundles-literal-v3`. Old snapshot/bundle caches, including the pre-publication
`literal-v2` form, are ignored, not adopted as
reviewed content or automatically deleted. A normal process restart clears the
in-memory parsed-revision cache; no hot-code upgrade is supported or implied.

## Existing installations and recovery

The migration **does not trust existing revisions** or invent their repository
provenance from current settings. Historical plans, successful applies and
previous credential releases are evidence of past work, not a new trust grant.
After an explicitly authorized upgrade:

- Sync the current repository and review/trust a newly loaded, current-source
  revision.
- Inspect old pending work. Runs lacking source provenance remain blocked; do
  not edit their database rows to manufacture authority. Cancel/replan only when
  the existing mutation and uncertainty rules allow it.
- Do not restart a running mutation or accept an old result as permission for
  unrelated new work.

Trust metadata is part of the database recovery boundary. An older backup can
restore older repository settings and authority decisions; reconcile source
changes, credentials, approvals and worker evidence before allowing dispatch,
as described in the [matched-recovery checklist](recovery-rehearsal.md).
This feature is not permission to migrate or restore a retained installation.

## Limits and next work

The current single-admin authentication boundary remains. A trust decision is
operator authorization, **not a sandbox, a code audit or proof of safety**.
The Git commit identifies checked-in content; it does not pin every provider,
remote module, image or artifact downloaded when that content executes.

Repository-fetch credentials are separate and still used to discover
configuration. This gate concerns credentials released for worker execution; it
does not fix repository-credential transport or reuse by itself.

Still separate: signed-commit trust, trust-revocation controls, purpose and
destination restrictions, worker/runtime restrictions, least-privilege Proxmox
credentials, a controller approval floor, complete TLS handling, and managed
guest SSH access. Do not distribute SSH private keys or upgrade the retained
environment on the assumption that this one gate completes those safeguards.

See the [decision](adr/0001-revision-execution-trust.md) and
[foundations plan](review/foundations-plan.md) for the sequencing rationale.
