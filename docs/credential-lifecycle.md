# Credential lifecycle and run pinning

Implemented October 21, 2026. This extends the built-in encrypted variable store;
it is not an external secret manager, automatic SSH rotation, or proof that a
replacement credential works.

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
for approval, legacy-run handling and read-only provider compatibility limits.

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

Promotion compares the expected active version; stale or concurrent changes
return `409`, requiring a refresh and review. Candidate allocation also uses
optimistic concurrency so simultaneous writes cannot overwrite a version.
All `system/` keys remain unavailable to these user lifecycle endpoints.
Keys with pinned run history or resolution records cannot be deleted; revoke
instead. Plain values remain API-readable, so do not put credentials in them.

## Upgrade and encryption-key custody

The `CredentialLifecycle` database migration preserves existing ciphertext and
authenticated encryption contexts. Existing versions are marked as previously
active, with an explicit legacy/unverified note; their active pointers and
allocation counters are retained. No live credential needs to be replaced merely
to upgrade the schema.

Old runs without version snapshots recover bindings from unambiguous resolution
records. Previously dispatched runs with missing or mixed-version evidence
block for reconciliation; they do not adopt the current credential silently.
The migration deliberately refuses downgrade: older binaries do not understand
candidate allocation or revocation. Recovery requires a matched pre-upgrade
database/key backup, not dropping lifecycle columns.

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
- No guest SSH private-key delivery, host-trust registry, staged guest key
  rotation, or application-password update is implemented here.
- No automatic provider-token creation/revocation, secret-manager integration,
  KEK keyring/rewrap, or complete immutable promotion-event ledger.
- This change does not fix custom-CA transport into OpenTofu or turn insecure
  Proxmox test fixtures into a production TLS configuration.
- Worker results retain their existing durability guarantees. Offline log
  completeness and worker-outbox encryption are not added by this slice.
- API/store/migration/run integration tests use isolated PostgreSQL databases
  and a fake worker. They do not prove live credential acceptance or remote VM
  operations. See [remote worker acceptance](../tests/remote-worker/README.md).
