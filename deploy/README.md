# Running the control host

The control host runs PostgreSQL, the backend, and the frontend as one Compose project.
Agents run separately (see [`agent/`](../agent)). Any Linux host with Podman (through its
Docker-compatible API, or `podman compose`) or Docker Engine with the Compose plugin
works.

To run it in a Proxmox LXC container with Docker, created and updated by script, see
[`proxmox/`](proxmox/README.md).

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

## Changes from the UI

Forms commit to the lab repository only with `Octopushy__RepositoryEditor__PublishEnabled: "true"` in the backend's
environment, and a repository credential that can push: for GitHub, a deploy key with write access. Without either, the forms
open read-only. See [changes from the UI](../docs/changes-from-the-ui.md).

## Service icons

The Services page can show logos from a folder of yours: uncomment the `icons` line under the backend's `volumes` in
`compose.yaml` and point it at the folder. It is mounted read-only. See [service icons](../docs/service-icons.md).

## Upgrading from 0.5.0

The releases after 0.5.0 add migrations after its `Initial`, so a 0.5.0 database is upgraded in place: `migrate` applies them.

## Starting again with a fresh database

Release 0.5.0 starts its database schema again from one migration. It cannot upgrade a
database created by an earlier release: `migrate` and the backend find the older migration
history, log that this release requires a fresh database, change nothing, and exit with a
non-zero status. Nothing in the old database is carried over, so before you start, write
down the store keys your lab repository references (the Proxmox tokens, any `shared/*`
values, and the workflows repository's credential when `octopushy.yaml` names one) and retire
the guests you want removed while the old release still runs.

Then, from `deploy/`:

1. Stop everything: `podman compose down` (or `docker compose down`).
2. Drop the database and the data that belongs to it, by removing the volumes:
   `podman volume rm octopushy_postgres octopushy_data`, and `octopushy_agent` too if the
   `agent` profile is in use. With your own PostgreSQL instead, run
   `DROP DATABASE octopushy;` and `CREATE DATABASE octopushy OWNER octopushy;`, and empty
   the backend's data directory. `secrets/kek` can stay: an empty database holds nothing
   it encrypted.
3. Set the new image tags in `.env` and start: `podman compose up -d`. `migrate` creates
   the schema on the empty database.
4. Run setup again: read the new setup code
   (`podman compose exec backend cat /var/lib/octopushy/setup-code`) and create the account.
5. Connect the lab repository again and re-enter its variables under **Variables**.
6. Re-enroll every agent. The agent CA is new, so an agent enrolled before cannot connect:
   remove its state directory (the `octopushy_agent` volume for the internal agent) and
   enroll it with a new token from **Agents**.
7. Trust each installation again when Octopushy asks for it.

## Updating

Before updating a retained installation, follow the
[matched database/key recovery checklist](../docs/recovery-rehearsal.md).
Quiesce writers and resolve the disposition of worker mutations; preserve a
matched database/data snapshot and separately escrowed original KEK. Rehearse
recovery in an isolated destination. A database dump alone does not include
artifact files, session keys, or worker journals.

Migrations are forward-only. An old binary cannot safely interpret a newer schema;
rollback requires a matched pre-upgrade backup and binary, not a schema downgrade or
regenerated KEK.

After those gates and explicit upgrade approval, build/pin the new images, set
`OCTOPUSHY_BACKEND_IMAGE` and `OCTOPUSHY_FRONTEND_IMAGE` in `.env`, and start the
selected Compose project. Its `migrate` service runs before the backend. Keep
automatic work/worker ingress isolated until post-upgrade checks and any
post-backup reconciliation are complete.

The synthetic recovery tests validate storage/service boundaries only. A
retained-installation restore, remote-worker reconnect, automated drain and the
supervisor with an upgrade journal remain future work.
