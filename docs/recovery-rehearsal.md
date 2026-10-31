# Matched database/key recovery rehearsal

Scope: **synthetic, isolated recovery**, October 22, 2026. This is test coverage
and an operator checklist, not a backup service, a retained-environment restore,
or permission to upgrade a running controller.

## What is exercised

`RecoveryTests` uses the existing PostgreSQL Testcontainers fixture. Native
`pg_dump --format=custom` produces a binary archive; native `pg_restore
--exit-on-error` restores it into a newly created database. The helper accepts
only databases it created and never accepts an existing restore destination.
Temporary archive files have mode `0600` and are removed in `finally`.

Both databases are on the same disposable PostgreSQL 18 instance. These tests
exercise logical backup/restore, not a PostgreSQL host rebuild, major-version
upgrade, or physical/WAL recovery. The source fixture has no running background
dispatch loops and uses no real infrastructure credentials.

The tests restore the escrowed fixture KEK into a separate location outside the
restored data directory, copy the artifact/log subset, and instantiate fresh
store/CA services against the restored database. They do not reuse the source
services' in-memory encryption objects.

| Recovery boundary | Evidence |
| --- | --- |
| Versioned credentials | Active, candidate and revoked versions, ciphertext/wrapped keys, promotion/revocation metadata, decryption, and allocation survive the native restore |
| Historical credential restrictions | Revoked and unpromoted versions still cannot be released through `ResolveForAttemptAsync` |
| Backup cutoff | Writes and revocation after the snapshot are absent; independent allocation resumes from the restored counter |
| Run evidence | Resource/run/attempt identity, uncertain mutation status, pinned version, approval digest/actor, artifact metadata and release audit survive |
| Built-in OpenTofu state | The encrypted versions survive the native restore and decrypt to the same bytes with the original KEK; a correctly sized wrong key fails authentication, and state alone is enough for a missing key to be refused rather than regenerated |
| Files outside PostgreSQL | Copied plan decrypts and matches its recorded digest; the restored log reader returns the saved line |
| CA identity | Same CA fingerprint, validity of the pre-backup certificate, and preserved registered serial; a post-backup issued serial is absent |
| Missing/wrong KEK | Missing key is not regenerated; a correctly sized wrong key fails authenticated store, artifact and CA reads without rewriting stored ciphertext |
| Incomplete CA | Missing CA key, missing certificate, and mismatched pair are rejected when enrolled-agent records exist |
| Incomplete artifact files | Missing file or tampered ciphertext fails explicit artifact verification |
| Invalid input archive | `pg_restore` error fails the rehearsal rather than reporting successful recovery |
| Forward-only migration | A real pre-upgrade archive retains the initial schema and decryptable legacy data; downgrade is refused without losing the migrated candidate; restoring the old archive into another database and upgrading it works |

The migration case extends `CredentialMigrationTests`; it does not boot an old
application binary. The CA case checks signature trust and database serial
evidence, **not an actual remote worker reconnect or renewal over TLS**.
No scheduler runs against the restored uncertain work, so preservation is
demonstrated without claiming that resumed dispatch is safe.

The rehearsal does **not** validate user/session restoration, filesystem
ownership for a deployed container, full Git snapshots, a plan against a restored
OpenTofu state, remote outboxes, external-provider state, automatic drain, or live service health.
The fake plan is not executable infrastructure and is never applied.

## Reproduce locally

Use the repository's pinned .NET SDK and a disposable Docker-compatible engine.
The fixture creates its own container/databases; it never reads deployment
connection strings or the retained `.dev/` directory.

```sh
dotnet test backend/tests/Octopushy.Tests \
  --filter 'FullyQualifiedName~RecoveryTests|FullyQualifiedName~CredentialMigrationTests'
```

For extra local isolation, point `DOCKER_HOST` at a separately rooted disposable
Podman API service, as used for the recorded rehearsal. Leave the retained
development engine, containers and volumes alone. If rootless Podman requires
`TESTCONTAINERS_RYUK_DISABLED=true`, cleanup remains the operator's responsibility
after an interrupted test: dispose only that dedicated test engine/storage.
Normal runs dispose their fixture container.

The tests run in the ordinary backend CI job; they require no deployment
credentials. Do not provide real keys, database dumps, or Proxmox access to them.

## Checklist for a later retained-environment rehearsal

This checklist needs explicit environment selection and authorization. Completing
the synthetic tests is not permission to perform these actions.

### 1. Establish a recovery point, not just a database dump

- Record exact controller/worker versions, image digests, schema migration IDs,
  database major version, backup time and checksums. Preserve the matching
  deployment configuration without exposing secrets in a manifest.
- Plan an actual maintenance window. There is no implemented atomic
  drain-and-backup operation. Stop new work through an operator-controlled
  boundary, let known work finish, and inspect unresolved mutations before
  quiescing the controller. Merely stopping the controller does not stop commands
  already executing on a worker or in a guest.
- Preserve worker identity, journals and outboxes separately. Do not acknowledge
  or delete unresolved results to make the backup appear clean.
- Take a matched database export and complete controller-data copy while writers
  are quiesced. A PostgreSQL-consistent dump does not make an independently copied
  artifact directory consistent with it.
- Escrow the **original KEK bytes** separately from the database and data copy.
  Keep the configured external key path, access permissions and custody explicit.
  A new random key cannot recover old encrypted material.
- Keep artifacts, logs, repository mirror/snapshots and the web-session key ring
  protected. The session key ring is sensitive independently of the KEK.
- Built-in OpenTofu state is in the database, so the dump covers it, but it is
  encrypted: **restoring it needs the same KEK**, like stored secrets. Without that key
  the guests' state is unrecoverable, and Octopushy would have nothing to plan from.
  A state written after the dump is not in it, so a restored guest can be behind its
  VM. Compare with the Proxmox observations and the last runs before planning.
- Preserve external OpenTofu backends under their own backup/ownership procedure;
  the Octopushy database does not hold their state, and it is not a VM/data backup.

### 2. Restore into an isolated destination

- Allocate a **new empty database** and a separate data directory. Verify the
  target identity before restoring; do not use `--clean` against a retained
  database or `--create` to implicitly select the archive's original name.
- Keep worker ingress, Git/automatic triggers, external network egress and UI
  mutation paths isolated. Do not run two controllers against the same targets
  or state. `RunBackgroundServices=false` alone is not an authorization boundary.
- Restore only a trusted archive using native `pg_restore --exit-on-error`.
  This test deliberately omits owners and ACLs; a real restore must separately
  provision/review database roles, ownership and grants.
- Restore the matching controller files and escrowed KEK, retaining private
  modes/ownership and keeping the KEK outside the data directory.
- Start with the binary/schema combination recorded at the recovery point.
  Do not run the latest binary's automatic migrations merely to inspect an
  older rollback snapshot. The credential migration's `Down` operation refuses
  downgrade; rollback requires a matched pre-upgrade set in a separate target.
- Check startup, schema identity, decryptable credentials/history, artifact
  presence/digests, approval bindings, run/attempt history and CA fingerprint
  before considering any reconnect. Missing artifacts need explicit checks;
  successful startup is not a complete backup-validation result.

### 3. Reconcile what happened after the backup

**Restoring history also restores old authorization decisions.** A credential
revoked after the backup can appear active again. A later candidate/version and
its use records can disappear; version numbers can be reused from the restored
counter. Reconcile post-backup rotations, revocations, disabled identities,
pending approvals and external changes before allowing secret release.

The restored CA can still trust a worker certificate cryptographically while
the restored agent row rejects its serial. `AgentChannelService` accepts only
the registered current/previous serial of an enabled agent. A worker that
switched to a certificate issued after the backup may need controlled
re-enrollment; this slice has not tested that live procedure. Do not silently
replace the CA or edit serials to bypass the check.

Database rollback does not roll back Proxmox changes, guest commands or service
data. Preserve uncertain outcomes and compare worker journals/outboxes, native
state and infrastructure observations before resuming. Do not auto-replay an
approved plan simply because the restored database says it was pending.

If the original KEK or CA cannot be recovered, stop normal recovery. A new
identity/store with explicitly re-imported secrets and controlled worker
re-enrollment is a separate disaster-recovery project, not an in-place key fix.

### 4. Admit work deliberately

Only after reconciliation, reconnect one selected worker and verify its identity
and status without dispatching a mutation. Review the remaining trust/secret-
release prerequisites in the [foundations plan](review/foundations-plan.md)
before enabling infrastructure access. Then authorize one bounded test target;
do not release a fleet backlog at once.

Neither the controller drain/supervisor nor that retained-installation procedure
is implemented by this test slice.

## Evidence and references

Local validation on October 22, 2026: **169 backend tests passed**, including
**14 recovery/migration cases**; offline checks passed **48 Python tests** with
five existing optional skips, plus tracked shell syntax. The first native
restore test was observed failing before its fixture helpers were implemented.
No retained controller, worker identity, database or Proxmox resource was used.

- `backend/tests/Octopushy.Tests/Server/RecoveryTests.cs`
- `backend/tests/Octopushy.Tests/Server/CredentialMigrationTests.cs`
- `backend/tests/Octopushy.Tests/Server/ServerHarness.cs`
- [Credential lifecycle](credential-lifecycle.md) and
  [deployment/key custody](../deploy/README.md)
- PostgreSQL 18 native command semantics:
  [pg_dump](https://www.postgresql.org/docs/18/app-pgdump.html) and
  [pg_restore](https://www.postgresql.org/docs/18/app-pgrestore.html).
  A single-database export omits cluster-wide roles; restoring untrusted SQL can
  execute code. These tools do not constitute a complete disaster-recovery policy.
