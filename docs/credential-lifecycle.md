# Credential lifecycle and run pinning

Implemented October 21, 2026. This extends the built-in encrypted variable store;
it is not an external secret manager or proof that a replacement credential works.
Rotating a guest's SSH key is built on these versions and adds the guest stages they
lack: see [rotating the key](guest-configuration.md#rotating-the-key).

## Operator workflow

1. Create an initial secret under **Variables**. The first value is active
   immediately for bootstrap compatibility; Octopushy has not tested its access.
2. **Update** an existing secret to save a **candidate**. This allocates a new
   encrypted version without changing the active pointer. Description-only
   edits and plain variable edits retain their existing behavior.
3. Verify the replacement's intended permissions externally. Never put the
   credential itself in a confirmation note, Git, a Nix expression, or logs.
4. Open **Versions**, review usage, and **Promote** the candidate with an operator
   confirmation note. This records the signed-in identity and time. It does
   **not** run an authentication probe or verify an SSH login.
5. Allow old-version work to finish, then explicitly **Revoke** that version.
   Revocation blocks subsequent assignments using it. The final outbound-channel
   hand-off is serialized with revocation; assignments already handed to that
   channel cannot be retracted, even if the worker has not acknowledged receipt.
   Revoke the actual provider token separately.

The UI shows active/latest version numbers, candidate/active/previous/revoked
states, latest promotion metadata, revocation metadata, and run/definition
references. History never returns values, ciphertext, or wrapped keys.
Previously promoted versions can be explicitly promoted again if not revoked,
with the same confirmation and concurrency checks. Revocation is irreversible.
Emergency revocation of the active version is allowed; there is no fallback.

## Exact execution bindings

Git can use either form:

```yaml
readOnlyToken: { store: proxmox/home/read-only-token }
# Or deliberately select a previously promoted, non-revoked version:
readOnlyToken: { store: proxmox/home/read-only-token, version: 2 }
```

The same version syntax applies to `privilegedToken`; installations require both
roles with distinct keys. See [Proxmox credential roles](proxmox-credentials.md)
for approval and read-only provider compatibility limits.

Before the first attempt is queued, a run resolves all of its bindings as one
metadata snapshot. An unversioned reference selects the active version; an
explicit version must already have been promoted. Missing, plain-credential,
candidate-only, or revoked bindings block the run. Partial snapshots are not
saved. Proxmox tokens and backend secret-environment references require secret
entries; public Proxmox CA certificates may be plain.

The selected version numbers live in the run context, not their values.
Each dispatch checks its step's selected versions again and releases only that
step's required values. Revoking an already-finished apply's state-backend
credential does not prevent independent guest verification that does not use it.
The existing resolution audit records the attempt, worker,
key and version; it is evidence of resolution for assignment, not proof of
delivery or successful authentication.

Promotion affects runs that have not pinned yet. It does not rewrite already
queued, awaiting-approval, running, or retryable runs. Targeted retries retain
their original bindings and successful infrastructure work. A pinned revoked
version remains blocked whenever a step needs that version, even if another
version is promoted: inspect/cancel
the old work and create a new run where safe, rather than silently changing
credentials under an approved plan.

Updating a backing credential does not change Git's provisioning-input hash
or automatically reprovision machines. A new run selects newly active versions.
Promoting a new version of a key a guest's `secrets` or `secretFiles`
name does configure that guest once, with its `setup` action and under its
own approval policy: see [shared store values](guest-configuration.md#shared-store-values).
Changing an explicit version in Git changes the declared binding and can request
a new plan; it does not by itself require VM replacement.

## API

All endpoints require the existing authenticated administrator session:

| Endpoint | Behavior |
| --- | --- |
| `PUT /api/variables` | Create initial value, edit a plain value, or change description. Replacing an existing secret value is refused. |
| `POST /api/variables/candidates` | `{key, value, description?}` → `{version}`; existing secret only. |
| `GET /api/variables/versions?key=…` | Metadata and usage only. |
| `POST /api/variables/promote` | `{key, version, expectedCurrentVersion, verificationNote}`. |
| `POST /api/variables/revoke` | `{key, version, reason}`. |
| `DELETE /api/variables?key=…` | Deletes the key once nothing needs it; see below. |

Promotion compares the expected active version; stale or concurrent changes
return `409`, requiring a refresh and review. Candidate allocation also uses
optimistic concurrency so simultaneous writes cannot overwrite a version.
All `system/` keys remain unavailable to these user lifecycle endpoints.
Plain values remain API-readable, so do not put credentials in them.

**Deleting a key.** A key is deleted once nothing needs its value. Deleting is
refused, naming what holds it, while any of these uses the key:

- a definition at the branch head: the revision the last sync loaded as the head,
  which after a rollback is the earlier revision again, not the newest one on
  record. Every definition file at that commit, and at the branch as last fetched,
  is also read as plain YAML whether it validates or not, so a definition with a
  schema error still keeps the keys it names. If one of those files is not YAML,
  or the commit cannot be read, nothing is deleted;
- the observations the scheduler sends. While the head is invalid it keeps
  observing each installation from the latest valid revision, with that
  revision's credentials, so those are kept too;
- a change that is not final and may still bring it to the branch, by the same
  rule as [generated-value cleanup](changes-from-the-ui.md#the-workflow-wizard).
  If a commit of such a change cannot be read, deleting is refused until it settles;
- a run that has not finished, pinned or not: one waiting for approval, or not
  dispatched yet, takes its value later;
- an attempt that has not finished, such as a queued observation, which may still
  be given it, or one that was given it and still reports, whose logs and outputs
  are masked with it.

**Used by** on the Variables page reads the same head revision.

Otherwise every version's encrypted value goes, and the key leaves the list. What
stays is the evidence: the resolution records of which attempt and agent received
which version, and the runs that pinned it. A finished attempt needs nothing more:
what it reported was masked when it was kept. A late result from an uncertain
attempt is masked with the values the controller still holds in memory for it,
if it holds them, as when the result arrives before a restart. Otherwise the
controller reads them back from the store, and if one of them was deleted, the
attempt's log lines, error and outputs are withheld rather than kept unmasked.
Masking never passes text through for an attempt whose values it does not hold:
an attempt with no secrets is registered with none, and one closed or unknown has
what it reports withheld.

A key that was ever released to Proxmox or OpenTofu work stays refused to
playbooks, set again or not. The key's last version number is kept, in
`deleted_store_keys` (migration `DeletedStoreKeys`), so a key set again continues
after it and an old version number never names a new value. `system/` keys
cannot be deleted here.

## Encryption-key custody

A run pins every binding's version before its first attempt is dispatched, so a
later promotion never changes what it holds. Migrations are forward-only: older
binaries do not understand candidate allocation or revocation, so recovery
requires a matched database/key backup, not dropping lifecycle columns.

On an empty installation the server creates its encryption-key file. On an
existing installation, a missing key now stops startup if store records or
artifacts exist. Restore the original key; generating another cannot recover
the ciphertext. Preserve the key separately from the database backup. This is
loss protection, **not master-key rotation** or automated backup/restore.

See the [matched database/key recovery rehearsal](recovery-rehearsal.md) for
native backup/restore regression coverage and an operator checklist. Restoring
an old snapshot also loses subsequent revocations and certificate-registration
changes; reconcile those before dispatch resumes.

## Limits and next steps

- One trusted administrator and one controller process. Namespace names are not
  environment authorization boundaries; repository code and workers receiving
  credentials remain trusted. No tenant/RBAC boundary is claimed.
- Worker mTLS renewal is separate and unchanged.
- A guest's own SSH key uses this store under `system/`, so it has versions and pinning, and
  stays out of these operator endpoints. Promoting a version here changes a stored value and tests
  no login. It configures guests whose setup names the key, but never touches a guest's access,
  so a guest's key changes only through [its own rotation](guest-configuration.md#rotating-the-key).
  No host-trust registry or application-password update is implemented here.
- No automatic provider-token creation/revocation, secret-manager integration,
  KEK keyring/rewrap, or complete immutable promotion-event ledger.
- This change does not fix custom-CA transport into OpenTofu or turn insecure
  Proxmox test fixtures into a production TLS configuration.
- Worker results retain their existing durability guarantees. Offline log
  completeness and worker-outbox encryption are not added by this slice.
- API/store/migration/run integration tests use isolated PostgreSQL databases
  and a fake worker. They do not prove live credential acceptance or remote VM
  operations. See [remote worker acceptance](../tests/remote-worker/README.md).
