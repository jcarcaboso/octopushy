# Credential lifecycle and run pinning

Implemented October 21, 2026. This extends the built-in encrypted variable store;
it is not an external secret manager or proof that a replacement credential works.
Rotating a guest's SSH key is built on these versions and adds the guest stages they
lack: see [rotating the key](guest-configuration.md#rotating-the-key).

## Operator workflow

1. Create an initial secret under **Variables**. The first value is active
   immediately; Octopushy has not tested its access.
2. To replace a value, choose **Modify** to edit the active value, or **Update**
   to enter a whole new one, then **Save and make active**. The new version is
   active at once. The version before it becomes previous and stays available.
   Description-only edits and plain variable edits work as before.
3. To check a replacement before anything uses it, choose **Save as a
   candidate** instead. This adds a new encrypted version and leaves the active
   one as it is. Verify the replacement's permissions externally, then open
   **Versions**, review usage, and choose **Make active**.
4. Each version made active records the signed-in user, the time, and the
   text of **Note, optional** if you gave one. Never put the credential itself in a note, Git, a Nix
   expression, or logs. Making a version active does **not** run an
   authentication probe or verify an SSH login.
5. Allow old-version work to finish, then explicitly **Revoke** that version.
   Revocation blocks subsequent assignments using it. The final outbound-channel
   hand-off is serialized with revocation; assignments already handed to that
   channel cannot be retracted, even if the worker has not acknowledged receipt.
   Revoke the actual provider token separately.

The UI shows the active version number, candidate/active/previous/revoked
states, latest promotion metadata, revocation metadata, and run/definition
references. History never returns values, ciphertext, or wrapped keys. A key is
marked **Newer v*n*** only when a version after the active one is not revoked,
so it could be made active; a revoked one after it is not offered.
A previous version that is not revoked can be made active again, with the same
concurrency checks. Revocation is irreversible.
Emergency revocation of the active version is allowed; there is no fallback.

**Show value** on a version reads that one version's value, whatever its state,
through the logged reveal endpoint, and compares it line by line with another
version, the active one at first. Revealing a value releases it to no run.
**Delete** removes a version that is not the active one: see
[deleting a version](#deleting-a-version).

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

A new active version affects runs that have not pinned yet. It does not rewrite already
queued, awaiting-approval, running, or retryable runs. Targeted retries retain
their original bindings and successful infrastructure work. A pinned revoked
version remains blocked whenever a step needs that version, even if another
version is made active: inspect/cancel
the old work and create a new run where safe, rather than silently changing
credentials under an approved plan.

Updating a backing credential does not change Git's provisioning-input hash
or automatically reprovision machines. A new run selects newly active versions.
A new active version of a key a guest's `secrets` or `secretFiles`
name, saved directly or made active from a candidate, does configure that guest once, with its `setup` action and under its
own approval policy: see [shared store values](guest-configuration.md#shared-store-values).
Changing an explicit version in Git changes the declared binding and can request
a new plan; it does not by itself require VM replacement.

## API

All endpoints require the existing authenticated administrator session:

| Endpoint | Behavior |
| --- | --- |
| `PUT /api/variables` | `{key, value?, visibility?, description?, note?, expectedCurrentVersion?}`. Creates a key, or adds a version of an existing one, plain or secret, and makes it active at once. Records the signed-in user and the note. Without `value`, only the description changes. An empty value for an existing secret is refused, and so is making a secret plain. |
| `POST /api/variables/candidates` | `{key, value, description?}` → `{version}`; existing secret only. |
| `GET /api/variables/versions?key=…` | Metadata and usage only. |
| `POST /api/variables/versions/reveal` | `{key, version}` → `{key, version, value}`: one version's value, whatever its state, for comparing versions. A version that does not exist, and keys under `system/`, are refused. The server logs who revealed which version. |
| `DELETE /api/variables/versions?key=…&version=…` | Deletes one version that is not active, once nothing needs it; see below. |
| `POST /api/variables/reveal` | `{keys}` → `{values: [{key, value}]}`: each key's active value, plain or secret. `null` when the active version is revoked or was never promoted, or the key is missing. Keys under `system/` are refused. The server logs who revealed which keys. |
| `POST /api/variables/promote` | `{key, version, expectedCurrentVersion, verificationNote?}`. The note is optional. |
| `POST /api/variables/revoke` | `{key, version, reason}`. |
| `DELETE /api/variables?key=…` | Deletes the key once nothing needs it; see below. |

Promotion compares the expected active version; stale or concurrent changes
return `409`, requiring a refresh and review. A save with `expectedCurrentVersion`
does the same, and `0` means the key must not exist yet, so a form for a new
entry cannot overwrite one. A save without it is not checked. Notes are at most
2000 characters; a blank note is stored as none. Candidate allocation also uses
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

**Deleting a version.** One version that is not the active one can be deleted:
its encrypted value goes, and its number is never given to another value, as for
a deleted key. It is refused for the active version, so make another one active
first, or delete the key. It is refused too, naming what holds it, while a
definition in the head revision, or in the one the scheduler observes from,
pins that version; or a run or an attempt that has not finished pinned it or
asked for it, or was given it. The resolution records of who received it stay.

Outputs are parsed before they are masked. Strings are masked as they read once
decoded, whatever JSON escaped in them, and stay strings. Nothing else is
rewritten: if a secret is in a property name, is the text of a number, `true`,
`false` or `null`, is a whole JSON object or array in the output, or runs across
tokens in the text the output is kept or shown as, compact or as PostgreSQL's
jsonb prints it, the whole output is withheld. Outputs that are not JSON are
withheld too. Numbers are checked as PostgreSQL stores them. The agent writes
every number in plain decimal form, as jsonb prints it, including those it passes
through from Proxmox. A number written otherwise, by an agent before 0.6.2 or
inside a tool's output, is rewritten first into the exact form PostgreSQL would
store, when that is short: an exponent within ±40 and at most 40 significant
digits, so `4.2e-7` is kept as `0.00000042` and `1.50e1` as `15.0`. Any other
number, in the outputs of an attempt that holds secrets, has its outputs withheld.

What is stored is checked as it is stored, not only as the attempt reported it:
the outputs with what the server adds to them, such as a plan's digest; an
observation's data, with the connection it went through; and a pipeline's
approval document, against the secrets of every attempt it is built from. Each
is checked whole and kept as it is or not at all, so a digest or an identity in
it is never rewritten. Text the server writes from what an attempt reported, such
as "It reported pending-reboot: A reboot is due." as a step's or a run's detail,
or a refused plan's description, is masked whole with that attempt's masks before
it is kept.

The guarantee covers what an attempt reported and the documents and text built
from it. It does not cover names and fields the server generates itself: the
property names of what the API returns, such as `toolVersion`, and its own fixed
words are not checked against secrets, so a secret that is such a word can
appear there.

An uncertain attempt takes one later result: its lease ran out, or the agent
could only say it was uncertain, as when its stored result could not be read for
a moment, and sends the result it stored once it can. The same result sent again
changes nothing. An attempt the server settled itself, because the outputs of
the result it had could not be kept, takes no other. An attempt whose outputs are withheld is settled without them:
failed, or uncertain if its work may have changed something, with the reason as
its error. What reads outputs treats them as unknown, never as empty: a withheld
observation leaves the installation's previous observation as the evidence the
VMID collision check uses, and a VMID an observation shows but that cannot be
read, null included, counts as possibly colliding.

A very short secret, such as `true`, `null` or `name`, matches ordinary output:
every observation of an installation whose read-only token is such a value is
withheld, and its observation error says so. Do not use short or common words as
credential values.

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
