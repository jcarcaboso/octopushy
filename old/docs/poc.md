# Operating the Git / OpenTofu PoC

One trusted administrator, one Linux controller, default workspace, trusted
repositories, serial execution. Python standard library, SQLite, static dashboard.
Network access defaults to loopback. `serve --host <private-LAN-IPv4>` explicitly
enables trusted-LAN HTTP inspection, retaining token and Host/Origin checks.
HTTP is unencrypted; never expose it publicly or on an untrusted network.

## Three different kinds of state

| Kind | Owner | Meaning |
|---|---|---|
| Desired configuration | Git | Code at the last manually synced branch revision |
| Resource state | OpenTofu backend | Resource bindings, lineage, serial, provider data |
| Run metadata / inventory | Octopushy SQLite | Results, revisions, approvals, allowlisted state projection |

**Checked** means a plan completed for that commit. **Applied** changes only after
successful saved-plan execution. No-change plans record a checked revision, not a
fabricated apply. Neither means a VM/service is currently healthy.

Every new repository commit conservatively supersedes its pending plans. There is
no file-to-stack dependency analysis; select the stacks to plan manually.

## Storage and backup

Default data directory: `.octopushy/`.

```text
access-token                   private browser/API authentication
fingerprint-key                stable HMAC key; back up with the database
controller.lock                controller/executor advisory lock
controller.sqlite3             connections, repos, stacks, runs
sources/<name>/                repositories initialized by the dashboard
git/<repo-id>/                  bare Git mirrors
states/<stack-id>/
  terraform.tfstate            persistent managed-local state
  terraform.tfstate.backup     OpenTofu backup, when written
runs/<run-id>/
  source/                     detached worktree including .terraform data
  plan.bin                    reviewed saved plan
  log.txt                     completed-command output
```

Repository-backend state remains remote. Never share one backend between stacks
or a managed-local directory between controllers.

Stop the controller cleanly, wait for execution to finish, and back up the
**entire data directory**, including the fingerprint key. Also back up source
repositories and remote state using its backend's facilities. Encrypt backups.
Environment-supplied credentials need a separate recovery procedure. File
permissions are not disk encryption.

To rotate the UI token: stop, remove `access-token`, restart. Do not remove
`fingerprint-key`. To rotate a Proxmox token: change the referenced environment
value, restart, and create a fresh plan. Changing backend configuration may
require manual reconciliation, not merely a credential rotation.

There is no retention/deletion UI. Worktrees, plugins, plans and logs accumulate;
monitor disk space. Do not delete active runs, state or metadata to free space.

## Execution and approval

1. Sync fetches a branch, validates its manifest, and records the commit.
2. Plan creates a detached worktree, initializes its backend, validates the module,
   and runs `tofu plan -detailed-exitcode -out=...`.
3. Exit `2` means changes ready for review; `0` means no changes. JSON is parsed
   privately for resource addresses/actions. The UI shows these and OpenTofu's
   readable plan, not raw state or raw JSON output values.
4. Approval binds the saved-plan digest. Deletes/replacements also require typing
   the stack name. Plans expire after one hour.
5. Before apply, Git is fetched again. Branch revision, saved plan, source
   snapshot, inputs, CLI version information and state are compared with the
   reviewed run. Any mismatch invalidates it; no silent replanning.
6. `tofu apply` receives the saved plan with normal locking/stale-plan checks.
   Success records the applied revision and observes state.

Commands have a 30-minute timeout and OpenTofu lock acquisition waits 30 seconds.
Output is capped at 8 MiB per command; the API displays the last 1 MB of the private
log with a truncation warning. Read the full local log before approving a truncated
plan. Output appears **after each command finishes**, not as a live stream.
Large repositories/plans/states are outside the tested scale.

This does not freeze real infrastructure between plan and apply: external
Proxmox operations can change reality without changing state. Keep reviews short
and use exclusive state ownership. Worktrees are trusted, not tamper-proof.

## Existing state

**Do not move existing VMs to an empty managed-local state.** Their resource
bindings would be missing.

For an existing remote backend:

1. Stop other writers, including Jenkins jobs/schedules.
2. Back up and inspect state, lineage, workspace, backend identity and locking.
3. Keep the same module, addresses, provider versions, lockfile and backend.
   Link a manifest with `backend: "repository"`.
4. Supply supported credentials through environment references.
5. Plan only; investigate unexpected creates/deletes before approval.

Existing **local** state has no automatic import path. Leave that stack on
Jenkins, or first migrate to a supported remote backend using OpenTofu's migration
procedure, outside this app. The app never calls state push/import/force-unlock
or automatic migration. Committed state and `.terraform` data are rejected.

Registered stack bindings are immutable. Removing/renaming a stack or changing
its path/backend/connection/environment-reference mapping needs a deliberately
implemented migration workflow. Editing SQLite is not a supported shortcut.

## Failure and recovery

| Result | Next step |
|---|---|
| `plan_failed` | Fix the issue, sync code if needed, and create a new plan |
| `superseded` / `invalidated` | Sync and plan again |
| `no_changes` | Configuration checked; no apply needed |
| `outcome_unknown` | Stop and inspect; further execution is blocked |
| `applied_unverified` | Apply succeeded; fix observation and plan, never replay apply |

Startup marks interrupted executions `outcome_unknown`. Children inherit the
controller lock, so a new controller cannot start while an inherited executor
still holds it. Apply errors/timeouts are also unknown: partial effects may have
occurred. Failed plans do not permanently block another stack. Uncertain applies
block the **whole controller** in this serial PoC.

To reconcile:

1. Stop the controller and confirm old executors stopped.
2. Inspect provider tasks, state and infrastructure. A dead local process does
   not prove a Proxmox task stopped.
3. Back up state and resolve backend/provider issues.
4. Only then acknowledge locally:

   ```sh
   python3 octopushy.py --data-dir /your/data/path resolve-run RUN_ID \
     --acknowledge-stopped-and-state-inspected
   ```

5. Restart, sync if needed, and create a **fresh plan**.

Acknowledgement is an audit entry permitting another plan. It does not repair
state, verify infrastructure or unlock a backend.

## Trust boundaries and limitations

- Proxmox tokens are environment references, not form values. Use least privilege.
- Raw state, saved plans, plugins, backend metadata and logs remain sensitive.
  Known environment values are redacted best-effort. Transformed secrets and
  values not marked sensitive can leak. Resource names/addresses must not contain
  secrets.
- Providers, modules, host Git hooks and provisioners execute as the controller
  user. A reduced subprocess environment is **not a sandbox**.
- Git SSH authentication is separate from guest-management SSH keys. No guest
  keys or Ansible execution are implemented.
- Only default-workspace conventional JSON state is supported. Encrypted state,
  other workspaces, remote execution backends and untested authentication schemes
  need explicit integration.
- No live Proxmox health, notifications, automatic Git sync, schedules, webhooks,
  remote agents, parallel execution, or service/release workflows yet.

## Verification

Unittests cover temporary real Git repos, state across checkouts/restarts,
approval, stale plans, changed inputs/state, typed deletes, secret redaction,
backend checks, process locking, reconciliation, and API boundaries. Optional
real-CLI tests use **OpenTofu 1.13.1**, `terraform_data`, persistent local state,
and a temporary local HTTP state server with locking.
The generated Proxmox scaffold also passed `init -backend=false` and `validate`
with `bpg/proxmox` **0.116.0**, without a Proxmox connection.

Browser smoke checks cover login, connection registration, local repository
initialization/linking, and saved-plan review/apply of a provider-free resource.
Real Proxmox cloning, provider SSH-dependent features and Jenkins-state migration
have **not** been exercised against a homelab.
