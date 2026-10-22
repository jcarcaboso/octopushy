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
  key, cannot be read. Keep a copy apart from any database backup **and** from any copy
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

Build the images for the new version, set `OCTOPUSHY_BACKEND_IMAGE` and
`OCTOPUSHY_FRONTEND_IMAGE` in `.env`, then `podman compose up -d`. The `migrate` service
runs first. Dump the database before an update that changes the schema:

```sh
podman compose exec postgres pg_dump -U octopushy octopushy > octopushy-$(date +%F).sql
```

The supervisor that will run these steps for you, with a journal and agent updates,
comes in a later milestone.
