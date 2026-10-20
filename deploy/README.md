# Running the control host

The control host runs PostgreSQL, the backend, and the frontend as one Compose project.
Agents run separately (see [`agent/`](../agent)). Any Linux host with Podman (through its
Docker-compatible API, or `podman compose`) or Docker Engine with the Compose plugin
works.

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
- The web interface listens on `127.0.0.1:8081` by default. Put TLS in front of it
  before exposing it to the network (`OCTOPUSHY_UI_BIND`).
- Agents connect to port 8443 on `OCTOPUSHY_AGENT_HOST`. That name goes into the agent
  endpoint's certificate, so agents must use it.
- The backend writes a one-time setup code to its data volume; read it with
  `podman compose exec backend cat /var/lib/octopushy/setup-code` and enter it in the
  web interface to create the account.

## What to keep safe

- `secrets/kek`: without it, the values in the variable store, including the agent CA's
  key, cannot be read. Keep a copy apart from any database backup.
- There are no backups yet (decided October 20, 2026). Losing the database loses the
  stored secrets, run history, and enrolled agents; the definitions in Git and the
  OpenTofu state in its backend are not affected.

## Updating

Build the images for the new version, set `OCTOPUSHY_BACKEND_IMAGE` and
`OCTOPUSHY_FRONTEND_IMAGE` in `.env`, then `podman compose up -d`. The `migrate` service
runs first. Dump the database before an update that changes the schema:

```sh
podman compose exec postgres pg_dump -U octopushy octopushy > octopushy-$(date +%F).sql
```

The supervisor that will run these steps for you, with a journal and agent updates,
comes in a later milestone.
