# Proxmox credentials: one token or separate roles

**October 24, 2026 — explicit shared-token support; split roles remain optional.**
One secret is sufficient for an installation. Octopushy does not create upstream
tokens, verify their ACLs, or rotate them automatically.

## Simple homelab configuration

```yaml
connection:
  endpoint: https://10.1.0.85:8006/
  tls: { verify: false } # Explicit homelab opt-out; not verified server identity.
  token: { store: proxmox/umbrel/api-token }
```

Import the token once as a **secret** at that store key. No second entry, duplicate
value, or placeholder privileged token is required. `token` accepts an optional
exact `version`, just like the split references below. Without it, a run pins the
active version before its first attempt.

**Shared means shared permissions:** observation, planning, apply and verification
receive the same token. Trusted repository/provider code can use all of its
upstream permissions during planning, before infrastructure approval. Revision
trust and approved-plan checks remain, but they are **not an execution sandbox or
a read-only planning guarantee**. Scope the token to the resources you intend to
manage. Prefer verified TLS where available; `verify: false` still uses HTTPS but
does not authenticate the Proxmox server. It does not disable UI HTTPS or worker mTLS.

## Optional split configuration

```yaml
connection:
  endpoint: https://pve.example.test:8006/
  tls: { verify: true }
  readOnlyToken: { store: proxmox/test/read-only-token, version: 1 }
  privilegedToken: { store: proxmox/test/privileged-token, version: 1 }
```

Choose **either `token` or both split references**, never a mixture. Split references
must name different store keys. A shared reference cannot alias another installation's
split writer, nor can a split reader alias a shared token. CA and backend environment
references cannot alias configured shared/writer tokens.

Do not duplicate one credential into two entries to simulate separation: different
key names cannot prove different upstream identities or privileges. For split mode,
use independently scoped identities and review effective user **and** token ACLs.
Pool membership alone does not cover initial allocation, host/storage access, or
proving absence after deletion. Store values remain write-only secrets; existing
candidate/promotion/revocation and audit behavior is unchanged. See
[credential lifecycle](credential-lifecycle.md).

### Split-mode compatibility blocker

The example locks bpg/proxmox **0.117.0**. When an existing VM's ordinary/EFI
disk configuration omits its format, provider refresh requests volume metadata
through a Proxmox GET requiring **`Datastore.Audit` plus `VM.Config.Disk`** on
the owning VM, or broad **`Datastore.Allocate`**. Strictly read-only update and
destroy planning therefore cannot be assumed to work. Fresh-create planning
does not exercise the same refresh. See the
[pinned primary-source analysis](research/proxmox-credential-split.md) for the
exact conditional paths, candidate scopes, and version limits.

**In split mode, do not silently widen the reader, retry with the writer, or disable
refresh to conceal this failure.** A denied preview fails without writer release.
Shared mode is a separate, explicit configuration choice that gives planning the
selected token's full privileges; it is not an automatic fallback. No ACL recipe
in this milestone is a tested, universally sufficient role, including in shared mode.

## Routing and approval

| Controller step | Shared mode | Split mode |
| --- | --- | --- |
| `plan` / OpenTofu `preview`, including retire preview | Shared | Read-only |
| `observe`, `verify`, `verify-absent` | Shared | Read-only |
| `apply`, `unprotect` | Shared, after approval | Privileged, after approval |
| `grow-root`, `verify-root` | Shared, after approval | Privileged, after approval |

Root verification runs a QEMU guest-agent command as guest root. Its
non-mutating retry classification is **not** a read-only credential grant.
Older Proxmox `VM.Monitor` is also execution-capable, not a safe substitute for
granular guest-agent audit permission.

The worker protocol retains the single `proxmox.token` alias. The controller
selects exactly one pinned role for each assignment; the worker does not choose
a role or retry with another. Shared runs persist one `proxmox.shared-token` binding
and version. Split guest runs pin both roles before their first attempt; split
installation observations need only the reader. Promotion does not rewrite a run's
bindings. Revocation blocks subsequent relevant releases without replacing the version.

Exact-revision trust remains necessary before any execution token is released.
Privileged **actions**, in either configuration, additionally check approval and saved
plan's identity before decrypting credentials, including native guest actions.
The existing manual/automatic policy still applies: **this is not T3's proposed
controller-owned approval floor**, and automatic policy is not human approval.

For root-filesystem-enabled provisioning, a no-change infrastructure plan still
retains a saved artifact for the approval gate. Manual approval is not skipped
just because `hasChanges` is false when privileged guest work remains. OpenTofu
apply remains skipped for that no-op plan. An older worker that omits the required
artifact cannot advance the privileged guest work.

Backend environment entries cannot override reserved `PROXMOX_VE_*` variables
or alias a configured shared/privileged token into another role. This bounded check
is not a general secret-purpose or destination policy. Repository-chosen code
still executes with the selected credentials; state-backend initialization and
workspace creation may write even during preview. Provider sandboxing, runtime
restrictions, and state-backend least privilege remain separate work.

## Upgrade and rehearsal checklist

1. A UI/controller update is not authorization to run a live guest lifecycle.
   Resolve remaining trust/transport prerequisites before live infrastructure work;
   split-mode full-lifecycle acceptance also requires resolving the refresh blocker.
2. Inventory existing runs and reconcile already-running or uncertain mutations.
   No automatic drain or safe live upgrade is supplied here.
3. Choose a shared token or independent split identities outside Octopushy and
   verify their intended scopes. Do not paste secrets into Git, logs, or chat.
4. Configure `token` or both split references, import through the store, and
   review/trust the exact repository revision. Existing YAML `connection.token`
   is supported for newly created runs; no secret copying is needed. If an older
   build already recorded that commit as invalid, sync a new reviewed commit
   (a configuration comment is sufficient); do not edit stored revision evidence.
5. Old serialized runs containing only `proxmox.token` are intentionally blocked
   for future dispatch. Do not rewrite historical evidence or infer a role from
   a formerly shared key. New shared-mode runs use a distinct persisted binding,
   not reinterpretation of old `proxmox.token` evidence. Already-delivered outcomes still reconcile, and
   uncertainty still requires inspection. Create fresh work only after that
   reconciliation permits it.
6. The existing CLI acceptance harnesses specifically exercise **split mode**;
   they still require distinct private files
   using `PVE_READ_ONLY_TOKEN_FILE` and `PVE_PRIVILEGED_TOKEN_FILE`; the
   [local](../tests/proxmox/README.md) and
   [remote-worker](../tests/remote-worker/README.md) harness documentation records
   preflight and retention rules. They are not the product's one-token setup path
   and have no automatic shared-token fallback.
7. Verify observed effective permissions, every lifecycle phase and assignment's
   credential role, denied-preview behavior, approval, pinned-version continuity,
   cleanup and absence checks. Synthetic tests prove routing and safety gates,
   **not real provider/PVE ACL sufficiency**.

## Still outside this milestone

- Provider compatibility repair and a live RO-refresh acceptance test.
- Controller-owned approval floor, purpose/destination bindings, trusted runtime
  selection, and end-to-end transport hardening.
- Guest SSH identities/rotation, service setup executors, secret delivery into
  guests, and actual remote-worker lifecycle acceptance.
- Upstream token creation/revocation, KEK rotation, and retained-environment rollout.
