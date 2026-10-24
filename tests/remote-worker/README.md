# Remote-worker acceptance readiness

This acceptance harness exercises optional **split-token mode**. A normal
installation can instead use one [shared token](../../docs/proxmox-credentials.md);
the harness does not silently switch modes when its reader lacks permissions.

This is an **opt-in mode of `tests/proxmox/run.sh`**, not a new deployment script.
It reuses a running dedicated controller and an already enrolled worker on another
machine. Its intended lifecycle creates, resizes and retires a disposable Proxmox VM, with the same
plan-count approval checks as the local harness. It checks that executed attempts
belong to the selected worker.

**Readiness only: live/full-lifecycle acceptance is currently blocked.**
The bpg/proxmox v0.117.0 refresh path can query disk/EFI volume metadata through
`storage/content/{volume}`. PVE requires `Datastore.Audit` plus `VM.Config.Disk`
on the owning VM, or broad `Datastore.Allocate` on storage. Strictly read-only
refresh/update/destroy planning is therefore not universally possible. Fresh
create planning does not exercise that refresh. Do not add mutation permissions
to read-only credentials, retry with privileged credentials or use
`-refresh=false`. See the [credential roles guide](../../docs/proxmox-credentials.md)
and [pinned research](../../docs/research/proxmox-credential-split.md).
This mode has not been validated against a live remote topology. Resolving the
provider blocker and a separately authorized topology acceptance run remain required.
Do not run it against a production controller, retained service fixture, or VM.

## Prerequisites (operator-managed)

1. A **dedicated, already running controller** with its own database and persisted
   data directory/CA, an initialized administrator account, and **no configured
   repository or previous repository revision**. Do not repurpose a retained
   controller by clearing its repository. Remote mode never builds, starts, stops,
   enrolls or resets a controller, worker, database, network listener or firewall.
2. Run the harness **on the controller host**. The controller service account must
   be able to read the fixture repository at the exact absolute `REMOTE_WORK_DIR/lab`
   path. A containerized controller needs an operator-managed matching bind mount.
   The worker needs **no checkout, Git access or shared filesystem**: assignments
   bundle the repository files and plan artifacts.
3. The desired worker is separately installed, enrolled and running against this
   controller's persisted CA and routable HTTPS agent-channel URL. Its certificate
   must cover that channel hostname. Set an **exclusive label** on this worker
   through the controller UI; no other registered worker may have that label.
   Copy its worker UUID from the controller. It must be connected, enabled, idle,
   and advertise `opentofu` and `proxmox`.
4. The worker's actual execution environment must reach:
   - the controller's HTTPS/mTLS agent channel;
   - Proxmox HTTPS using both independently reviewed token identities and TLS policy;
   - a **dedicated remote PostgreSQL OpenTofu state database**, with permissions
     to create/use the `octopushy_state` schema and workspaces;
   - the configured OpenTofu image/provider registries (or existing caches).
   Check these routes from the worker/container environment independently.
   **`127.0.0.1` inside a worker or its container is not the controller.**
   This harness cannot prove these routes from the controller machine.
5. A configured tool runtime on the worker (normally rootless Podman), persistent
   state/work/cache directories owned by its service user, and sufficient Proxmox
   storage/memory/permissions for the disposable VM and cloud image.
6. On the controller host: Bash, Python 3, curl, jq, rg, Git and ssh-keygen.
   Neither Cargo/.NET builds nor local Podman/PostgreSQL are used by remote mode.
7. A private Netscape-format **authenticated controller cookie file** for the
   dedicated account; two distinct private Proxmox API-token files; and a private file holding
   the OpenTofu PostgreSQL URI. Use mode `0600` (or `0400`). Obtain these through
   normal administrative workflows, not by copying another fixture's credentials.
   The cookie file is read, not overwritten.

The existing Proxmox fixture uses `tls.verify: false` for its disposable trusted-LAN
target. That behavior is unchanged; it does not demonstrate production Proxmox TLS
trust. Controller API TLS uses curl's normal verification. Agent trust stays pinned
to its enrolled CA. PostgreSQL TLS/trust must work in the worker's tool environment;
this mode does not add custom OpenTofu CA-file transport or disable TLS to make it work.
Do not use shell tracing or put credentials in endpoint URLs or environment command
arguments. The PostgreSQL URI goes from its file to the variable store via stdin,
never into the command line or the harness's normal output.

## Configuration and explicit run

Set these variables in your shell, with paths to your own private files:

```sh
export REMOTE_API_URL=https://controller.example.test
export REMOTE_AGENT_URL=https://controller.example.test:8443
export REMOTE_AGENT_ID=11111111-2222-4333-8444-555555555555
export REMOTE_AGENT_LABEL=remote-acceptance
export REMOTE_WORK_DIR=/absolute/private/remote-worker-acceptance
export REMOTE_COOKIE_FILE=/private/controller.cookies
export REMOTE_TOFU_PG_FILE=/private/remote-state-uri
export PVE_ENDPOINT=https://pve.example.test:8006/
export PVE_READ_ONLY_TOKEN_FILE=/private/disposable-proxmox-read-only-token
export PVE_PRIVILEGED_TOKEN_FILE=/private/disposable-proxmox-privileged-token
export PVE_NODE=disposable-node
export TEST_NAME=opy-remote-01
# Set PVE_IMAGES, PVE_DISKS, PVE_BRIDGE as appropriate; TEST_VMID is optional.
```

`REMOTE_AGENT_URL` is the **controller's agent-channel URL**, not an inbound
listener on the worker. It must match the controller URL used during that worker's
enrollment (`AgentChannel.PublicUrl`); supplying it here does not reconfigure either
process. `REMOTE_API_URL` is the controller API origin (or proxy base path, without
`/api`). Neither may contain credentials, query parameters or fragments.
The state file must contain one `postgres://…/dedicated_database` or
`postgresql://…/dedicated_database` URI, optionally with its trailing newline.
Backend query parameters may configure TLS but may not override host/port/database.

Both Proxmox files are required, must contain independent `USER@REALM!ID=SECRET`
credentials, and must be nonempty regular files with mode `0600` or `0400`.
The validator rejects the same file (including hardlink/symlink aliases) and
equal credential contents after whitespace trimming before any DNS lookup.
`PVE_TOKEN_FILE` is not a fallback and no retained local token path is read by
default. A fresh fixture imports both values without printing them into distinct
`proxmox/<PVE_NODE>/read-only-token` and `proxmox/<PVE_NODE>/privileged-token`
store keys. Observe/inventory and preview select read-only credentials; approved
mutation steps select privileged credentials. The worker wire alias remains
`proxmox.token`; there is no legacy `connection.token` alias.

Offline validation (reads only the supplied files; **no DNS or network calls**):

```sh
python3 tests/remote-worker/config.py
python3 -m unittest discover -s tests/remote-worker -p 'test_*.py'
bash -n tests/proxmox/run.sh
```

Only after resolving the provider blocker **and** separately authorizing the real
disposable-VM lifecycle (not passing acceptance with the current provider):

```sh
REMOTE_WORKER=1 tests/proxmox/run.sh
```

The explicit run resolves endpoint DNS and rejects loopback, wildcard, link-local
and multicast endpoints, checks controller health/authentication, and verifies the
exclusive worker label/capabilities before writing the fixture. DNS resolution is
not a remote-worker connectivity test. It does not probe or provision anything when
the config module is imported or its tests run. The tests exercise only the
offline harness preflight prefix with temporary synthetic files, never the full
script. Mock credential wiring does not prove upstream PVE privileges.

Remote mode never launches/enrolls a local worker or builds/starts a local backend.
It does not modify listener bindings or public URLs. The local harness defaults
remain loopback-only. UI, service workflows and `KEEP`/`KEEP_GUEST` modes are rejected
in this deliberately narrow VM-lifecycle mode.

## Retention and interrupted runs

Remote mode **never deletes a fixture directory or drops/creates databases**.
A new run refuses an existing `REMOTE_WORK_DIR`; it also refuses to replace an
existing controller repository/revision. Fixture files/SSH keys/metadata remain
under that directory; execution logs and run/backend state remain in the dedicated
controller/state databases, even after a successful retirement.
The controller and worker remain running because the harness does not own them.

If interrupted after a VMID is recorded, preserve everything. Inspect the current
run/VM first. Only when retirement is authorized, use **the same exported inputs**
and cookie/state/both token files:

```sh
REMOTE_WORKER=1 CLEANUP=1 tests/proxmox/run.sh
```

Cleanup requires the recorded split-credential fixture metadata
(`credentialSchema: split-v1`), VMID and matching controller
repository; changed controller/worker/channel/label/Proxmox/name inputs are refused.
It reuses the existing backend state rather than re-importing or resetting it.
Legacy `connection.token` fixtures or old metadata fail visibly; the harness
never silently migrates definitions, duplicates an old credential, or rebinds
historical run contexts. Resolve migration separately without rewriting old
run bindings. Cleanup's refresh/destroy plan is subject to the same provider
blocker; this mode is not a guaranteed recovery path.
It does not cancel an in-flight apply; wait for and inspect that outcome first.
Do not remove fixtures/state to get past a guard. A new acceptance run needs a new
dedicated controller/state database and fixture directory provisioned separately.
