# Proxmox credential roles

**October 23, 2026 — routing foundation, not live rollout approval.**
This separates credential delivery; it does not create upstream tokens, verify
their ACLs, rotate credentials, or certify a complete read-only planning lifecycle.

## Compatibility blocker

The example locks bpg/proxmox **0.117.0**. When an existing VM's ordinary/EFI
disk configuration omits its format, provider refresh requests volume metadata
through a Proxmox GET requiring **`Datastore.Audit` plus `VM.Config.Disk`** on
the owning VM, or broad **`Datastore.Allocate`**. Strictly read-only update and
destroy planning therefore cannot be assumed to work. Fresh-create planning
does not exercise the same refresh. See the
[pinned primary-source analysis](research/proxmox-credential-split.md) for the
exact conditional paths, candidate scopes, and version limits.

**Do not add those mutation permissions to the read-only identity, retry with
the privileged token, or disable refresh to conceal this failure.** A denied
preview must fail without writer release. Resolve and test provider/module
compatibility separately before a live full-lifecycle rehearsal. No ACL recipe
in this milestone is a tested, universally sufficient role.

## Explicit configuration

```yaml
connection:
  endpoint: https://pve.example.test:8006/
  tls: { verify: true }
  readOnlyToken: { store: proxmox/test/read-only-token, version: 1 }
  privilegedToken: { store: proxmox/test/privileged-token, version: 1 }
```

Both references are required and must name **different store keys**. Versions
are optional; an omitted version pins the active version before first execution.
The former `connection.token` is rejected rather than treated as either role.
Do not duplicate the old credential into two entries: different key names cannot
prove different upstream identities or privileges. Tokens should be
privilege-separated and independently scoped per installation; review effective
user **and** token ACLs. Pool membership alone does not cover initial allocation,
host/storage access, or proving absence after deletion.

Store values remain write-only secrets. Existing candidate/promotion/revocation
and audit behavior is unchanged; see [credential lifecycle](credential-lifecycle.md).

## Routing and approval

| Controller step | Credential delivered |
| --- | --- |
| `plan` / OpenTofu `preview`, including retire preview | Read-only |
| `observe`, `verify`, `verify-absent` | Read-only |
| `apply`, `unprotect` | Privileged, after approval |
| `grow-root`, `verify-root` | Privileged, after approval |

Root verification runs a QEMU guest-agent command as guest root. Its
non-mutating retry classification is **not** a read-only credential grant.
Older Proxmox `VM.Monitor` is also execution-capable, not a safe substitute for
granular guest-agent audit permission.

The worker protocol retains the single `proxmox.token` alias. The controller
selects exactly one pinned role for each assignment; the worker does not choose
a role or retry with another. Guest runs pin both roles before their first
attempt; installation observations need only the read-only role. Promotion does
not rewrite an existing run's bindings. Revocation blocks subsequent relevant
releases without replacing the pinned version.

Exact-revision trust remains necessary before either role is released.
Privileged assignment construction additionally checks the approval and saved
plan's identity before decrypting credentials, including native guest actions.
The existing manual/automatic policy still applies: **this is not T3's proposed
controller-owned approval floor**, and automatic policy is not human approval.

For root-filesystem-enabled provisioning, a no-change infrastructure plan still
retains a saved artifact for the approval gate. Manual approval is not skipped
just because `hasChanges` is false when privileged guest work remains. OpenTofu
apply remains skipped for that no-op plan. An older worker that omits the required
artifact cannot advance the privileged guest work.

Backend environment entries cannot override reserved `PROXMOX_VE_*` variables
or alias the configured privileged token into the preview. This bounded check
is not a general secret-purpose or destination policy. Repository-chosen code
still executes with the selected credentials; state-backend initialization and
workspace creation may write even during preview. Provider sandboxing, runtime
restrictions, and state-backend least privilege remain separate work.

## Upgrade and rehearsal checklist

1. **Do not deploy this slice to retained infrastructure yet.** Resolve the
   provider refresh blocker and remaining trust/transport prerequisites first.
2. Inventory existing runs and reconcile already-running or uncertain mutations.
   No automatic drain or safe live upgrade is supplied here.
3. Prepare independent least-privilege identities outside Octopushy and verify
   their scopes without attempting destructive writes. Do not paste secrets into
   Git, logs, or chat.
4. Replace the legacy installation reference with both explicit references;
   stage/import secret versions through the existing store and review/trust the
   resulting exact repository revision.
5. Old serialized runs containing only `proxmox.token` are intentionally blocked
   for future dispatch. Do not rewrite historical evidence or infer a role from
   a formerly shared key. Already-delivered outcomes still reconcile, and
   uncertainty still requires inspection. Create fresh work only after that
   reconciliation permits it.
6. For a separately approved disposable rehearsal, supply distinct private files
   using `PVE_READ_ONLY_TOKEN_FILE` and `PVE_PRIVILEGED_TOKEN_FILE`; the
   [local](../tests/proxmox/README.md) and
   [remote-worker](../tests/remote-worker/README.md) harness documentation records
   preflight and retention rules. No legacy single-token fallback is supported.
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
