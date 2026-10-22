# Remote-worker acceptance readiness

This is an **opt-in mode of `tests/proxmox/run.sh`**, not a new deployment script.
It reuses a running dedicated controller and an already enrolled worker on another
machine. It creates, resizes and retires a disposable Proxmox VM, with the same
plan-count approval checks as the local harness. It checks that executed attempts
belong to the selected worker.

**Readiness only: this mode has not been validated against a live remote topology.**
The real topology acceptance test remains a separate, explicitly authorized step.
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
   - Proxmox HTTPS using the reviewed token and TLS policy;
   - a **dedicated remote PostgreSQL OpenTofu state database**, with permissions
     to create/use the `octopushy_state` schema and workspaces;
   - the configured OpenTofu image/provider registries (or existing caches).
   Check these routes from the worker/container environment independently.
   **`127.0.0.1` inside a worker or its container is not the controller.**
   This harness cannot prove these routes from the controller machine.
5. A configured tool runtime on the worker (normally rootless Podman), persistent
   state/work/cache directories owned by its service user, and sufficient Proxmox
   storage/memory/permissions for the disposable VM and cloud image.
6. On the controller host: Bash, Python 3, curl, jq, Git and ssh-keygen.
   Neither Cargo/.NET builds nor local Podman/PostgreSQL are used by remote mode.
7. A private Netscape-format **authenticated controller cookie file** for the
   dedicated account; a private Proxmox API-token file; and a private file holding
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
export PVE_TOKEN_FILE=/private/disposable-proxmox-token
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

Offline validation (reads only the supplied files; **no DNS or network calls**):

```sh
python3 tests/remote-worker/config.py
python3 -m unittest discover -s tests/remote-worker -p 'test_*.py'
bash -n tests/proxmox/run.sh
```

Only after authorizing the real disposable-VM lifecycle:

```sh
REMOTE_WORKER=1 tests/proxmox/run.sh
```

The explicit run resolves endpoint DNS and rejects loopback, wildcard, link-local
and multicast endpoints, checks controller health/authentication, and verifies the
exclusive worker label/capabilities before writing the fixture. DNS resolution is
not a remote-worker connectivity test. It does not probe or provision anything when
the config module is imported or its tests run.

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
and cookie/state/token files:

```sh
REMOTE_WORKER=1 CLEANUP=1 tests/proxmox/run.sh
```

Cleanup requires the recorded fixture metadata, VMID and matching controller
repository; changed controller/worker/channel/label/Proxmox/name inputs are refused.
It reuses the existing backend state rather than re-importing or resetting it.
It does not cancel an in-flight apply; wait for and inspect that outcome first.
Do not remove fixtures/state to get past a guard. A new acceptance run needs a new
dedicated controller/state database and fixture directory provisioned separately.
