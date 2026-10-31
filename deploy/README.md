# Running the control host

The control host runs PostgreSQL, the backend, and the frontend as one Compose project.
Agents run separately (see [`agent/`](../agent)). Any Linux host with Podman (through its
Docker-compatible API, or `podman compose`) or Docker Engine with the Compose plugin
works.

To run it in a Proxmox LXC container with Docker, created and updated by script, see
[`proxmox/`](proxmox/README.md).

For repository-backed NixOS worker VMs, see the
[managed worker wizard and image setup](../docs/worker-deployment-wizard.md).
It reuses an independent control worker; publishing a definition does not grant
revision trust or infrastructure approval.

## First start

```sh
cd deploy
cp .env.example .env                      # set POSTGRES_PASSWORD and OCTOPUSHY_AGENT_HOST
install -d -m 0700 secrets
head -c 32 /dev/urandom > secrets/kek     # the variable store's key-encryption key
chmod 0444 secrets/kek                    # readable inside the container; the directory stays private

just images dev                           # from the repository root: builds both images by hand
podman compose up -d                      # or: docker compose up -d
```

- `migrate` applies the database schema and exits; the backend starts after it.
- `COMPOSE_PROFILES=agent` in `.env` adds an internal agent (image `just images` builds
  too). Enroll it once with a token from the web interface:
  `OCTOPUSHY_ENROLLMENT_TOKEN='opy1.…' podman compose run --rm --no-deps -e OCTOPUSHY_ENROLLMENT_TOKEN agent agent enroll --server https://backend:8443 --name internal`.
- The web interface listens on `127.0.0.1:8081` by default. Put TLS in front of it
  before exposing it to the network (`OCTOPUSHY_UI_BIND`).
- Agents connect to port 8443 on `OCTOPUSHY_AGENT_HOST`. That name goes into the agent
  endpoint's certificate, so agents must use it.
- The backend writes a one-time setup code to its data volume; read it with
  `podman compose exec backend cat /var/lib/octopushy/setup-code` and enter it in the
  web interface to create the account.

## Automatic guest disk reporting

Build and publish `octopushy-disk-reporter` for your guests' architecture/libc,
then set these values in `.env` and recreate the backend:

```dotenv
OCTOPUSHY_DISK_REPORTER_ENABLED=true
OCTOPUSHY_DISK_REPORTER_PUBLIC_URL=https://octopushy.example
OCTOPUSHY_DISK_REPORTER_BINARY_URL=https://releases.example/disk-reporter-v1
OCTOPUSHY_DISK_REPORTER_BINARY_SHA256=<sha256sum of the published binary>
OCTOPUSHY_DISK_REPORTER_INTERVAL_SECONDS=3600
```

The web/API origin and direct binary URL must be reachable over HTTPS **from the
VM**. No redirect-following or insecure TLS fallback is allowed. For a private CA,
set `OCTOPUSHY_DISK_REPORTER_CA_PEM` to its public PEM (never the private key);
it becomes the trust bundle for **both** endpoints.

Upgrade the execution agents too: they must advertise the new `guest` executor.
The VM needs Linux/systemd and a running QEMU guest agent with guest execution
enabled. The Proxmox token needs guest-exec privileges on each managed VM.
The [reporter guide](../agent/packaging/disk-reporter/README.md) lists prerequisites,
per-guest overrides, and recovery steps.

Once enabled, provision runs append **install-disk-reporter** after verification,
enable the hourly timer, and attempt the first report immediately. Existing VMs
can be installed or repaired with **Plan now**, even if OpenTofu has no changes.
Manual-approval policy still applies. No SSH key or inbound VM port is required.
Installation is disabled until deployment URLs and the checksum are configured;
the server refuses invalid enabled configuration.

## What to keep safe

- `secrets/kek`: without it, the values in the variable store, including the agent CA's
  key, and the guests' OpenTofu state cannot be read. Keep a copy apart from any database backup **and** from any copy
  of the `data` volume. Anyone holding the key plus the database can decrypt every stored
  secret and the agent CA key, and sign certificates that agents accept.
- The `data` volume holds the setup code, the Git mirror, artifacts, logs, and the
  session-signing key ring (`data-protection/`, unencrypted: whoever has it can forge a
  web session). Treat a copy of it as sensitive. The compose file keeps the key
  outside it with `Octopushy__Store__KeyFile`. Without that setting the server puts the key
  at `keys/kek` inside the data directory and logs a warning at every start; do not run a
  retained environment that way.
- If the agent CA entries (`system/agent-ca/key`, `system/agent-ca/certificate`) are missing
  or do not belong together in a database that already has agents or enrollment tokens, the
  server refuses to start rather than create a new CA, which would leave every enrolled
  agent untrusted. Restore the entries from a database backup.

## Updating

Before updating a retained installation, follow the
[matched database/key recovery checklist](../docs/recovery-rehearsal.md).
Quiesce writers and resolve the disposition of worker mutations; preserve a
matched database/data snapshot and separately escrowed original KEK. Rehearse
recovery in an isolated destination. A database dump alone does not include
artifact files, session keys, worker journals, or external OpenTofu state.

The credential-lifecycle migration is forward-only. An old binary cannot safely
interpret the new candidate/revocation state; rollback requires a matched
pre-upgrade backup and binary, not a schema downgrade or regenerated KEK.

After those gates and explicit upgrade approval, build/pin the new images, set
`OCTOPUSHY_BACKEND_IMAGE` and `OCTOPUSHY_FRONTEND_IMAGE` in `.env`, and start the
selected Compose project. Its `migrate` service runs before the backend. Keep
automatic work/worker ingress isolated until post-upgrade checks and any
post-backup reconciliation are complete.

The synthetic recovery tests validate storage/service boundaries only. A
retained-installation restore, remote-worker reconnect, automated drain and the
supervisor with an upgrade journal remain future work.
