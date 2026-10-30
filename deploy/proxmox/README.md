# Octopushy on a Proxmox LXC container

`install.sh` runs on a Proxmox VE host. It creates a Debian 13 LXC container, installs
Docker in it, and starts the [Compose project](../compose.yaml) (PostgreSQL, the
backend, the frontend, and an internal agent) from the images that
[`.github/workflows/images.yml`](../../.github/workflows/images.yml) publishes to
[Docker Hub](https://hub.docker.com/r/skorcius/octopushy). `octopushy-update` moves it to another version later, with a container
snapshot to roll back to.

An LXC container shares the Proxmox host's kernel but has its own file system, network
address, and processes. It is lighter than a VM, and Proxmox can snapshot and back it up
like one. The container is *unprivileged* (root inside is an ordinary user on the host)
and has `nesting` and `keyctl` turned on, which Docker needs.

## Before you start

- **Published images.** The workflow publishes the three images to the public repository
  `docker.io/skorcius/octopushy`, told apart by tag: `backend-…`, `frontend-…`, and
  `agent-…`, followed by `main` or `sha-<commit>` on every push to `main`, and by
  `<version>` or `latest` for every `v<version>` tag. It needs the GitHub repository
  secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`. The container pulls without
  credentials. Docker Hub limits anonymous pulls per address; if a pull stops with
  `toomanyrequests`, run `docker login` in the container.
- **Snapshot-capable storage** for the container: ZFS (`local-zfs`), LVM-thin
  (`local-lvm`), Ceph RBD, or Btrfs. A plain directory works, but then updates cannot
  roll back by themselves.
- **An address plan.** Agents connect to the container on port 8443 by the name or
  address you give as the agent host; it goes into the agent endpoint's certificate, so
  pick one that will not change: a static address, or a DNS name for it.

## Install

As root on the Proxmox host:

```sh
bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/main/deploy/proxmox/install.sh)"
```

It asks for each setting, shows a summary, and asks again before it creates anything.
Each setting can be preset in the environment, which also lets it run unattended:

| Variable | Default | |
|---|---|---|
| `OCTOPUSHY_VERSION` | `0.1.0` | A release, `main` for the newest build of the main branch, or `sha-<commit>` |
| `CTID` | next free ID | |
| `CT_HOSTNAME` | `octopushy` | |
| `CT_STORAGE` | first storage for containers | Holds the root disk and the key volume |
| `CT_TEMPLATE_STORAGE` | first storage for templates | Where the Debian 13 template is downloaded |
| `CT_BRIDGE` / `CT_VLAN` | `vmbr0` / `none` | |
| `CT_IP` / `CT_GATEWAY` | `dhcp` / host's gateway | `10.1.0.50/24` for a static address |
| `OCTOPUSHY_AGENT_HOST` | the static address, else the host name | |
| `CT_CORES` / `CT_MEMORY` / `CT_DISK` | `2` / `4096` MiB / `32` GiB | |
| `CT_SSH_KEY` | `none` | A public key file, to SSH in as root |
| `OCTOPUSHY_YES` | | `1` takes the defaults without asking |

```sh
CT_IP=10.1.0.50/24 CT_GATEWAY=10.1.0.1 OCTOPUSHY_AGENT_HOST=octopushy.home.arpa OCTOPUSHY_YES=1 \
  bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/main/deploy/proxmox/install.sh)"
```

From a checkout on the host, `bash deploy/proxmox/install.sh` uses the checkout's files
instead of downloading them.

### What it creates

- Container `CTID`, started at boot, tagged `octopushy`.
- `/opt/octopushy/compose.yaml` and `.env` (a random PostgreSQL password, the agent host,
  the image tags) in the container.
- The variable store's key at `/etc/octopushy/secrets/kek`, on its own 1 GiB volume
  (`mp0`) marked `backup=0`, so a vzdump backup never holds the key next to the database
  it decrypts.
- The internal agent (Compose profile `agent`), waiting for its enrollment. See below.
- `octopushy-compose` in the container: `docker compose` for the project from any
  directory, and `octopushy-agent-enroll` to enroll the internal agent.
- `/usr/local/sbin/octopushy-update` on the Proxmox host.

## First steps

1. Read the setup code and create the account at `http://<container address>:8081`:

   ```sh
   pct exec <ctid> -- octopushy-compose exec backend cat /var/lib/octopushy/setup-code
   ```

2. Copy the key somewhere safe, away from any backup. Without it the stored secrets and
   the agent CA cannot be read; with it and a database copy, anyone can read them.

   ```sh
   pct exec <ctid> -- base64 /etc/octopushy/secrets/kek
   ```

3. Enroll the internal agent: under **Agents**, create an enrollment token, then

   ```sh
   pct exec <ctid> -- octopushy-agent-enroll 'opy1.…'
   ```

   It shows up under **Agents** a few seconds later.

4. Put HTTPS in front of the web interface (below), then continue with the
   [first-start steps](../../README.md#development): point Octopushy at your lab
   repository and set the variables its definitions reference, such as the Proxmox API
   tokens.

## The internal agent

The agent runs the work: OpenTofu plans and applies, and the Proxmox API checks. The
internal one runs in the same container as the server, so you can start without another
machine. It needs network access to your Proxmox hosts' API (port 8006), to the OpenTofu
registry for providers, and to wherever your OpenTofu state lives.

It runs OpenTofu directly (`--tool-runtime local`) inside its own Docker container, as
an unprivileged user. It shares only a private network with the backend, so it cannot
reach PostgreSQL, and it gets neither the key nor the database password. It still runs
on the same machine as the server. Once you manage more than this lab, or want a
stronger boundary, add agents on their own VMs (the
[managed worker wizard](../../docs/worker-deployment-wizard.md) builds them with this
one) and give the internal agent's labels only to what should run here.

To enroll it again, for example under another name, remove it under **Agents** and run
`pct exec <ctid> -- sh -c 'octopushy-compose rm -sf agent && docker volume rm octopushy_agent'`
before enrolling with a new token (`OCTOPUSHY_AGENT_NAME=<name>` picks the name).

## HTTPS

The web interface is plain HTTP on port 8081. Put a TLS proxy in front of it; the
frontend trusts `X-Forwarded-Proto` from private addresses, so session cookies become
secure once the proxy sets it. With Traefik's file provider:

```yaml
http:
  routers:
    octopushy:
      rule: Host(`octopushy.example.com`)
      entryPoints: [websecure]
      service: octopushy
      tls:
        certResolver: letsencrypt # your resolver
  services:
    octopushy:
      loadBalancer:
        servers:
          - url: http://10.1.0.50:8081
```

**Leave port 8443 out of the proxy.** Agents use gRPC with Octopushy's own certificates
and authenticate with client certificates, so TLS must end at the backend. Let the agent
host name resolve to the container itself. If agents can only reach the proxy, use a TCP
router with `tls.passthrough: true` on its own entry point or host name.

## Updating

On the Proxmox host:

```sh
octopushy-update <ctid> 0.2.0     # a release
octopushy-update <ctid> main      # the newest build of main
```

It pulls the new images first (nothing stops if that fails), then stops everything but
PostgreSQL (the internal agent lets running applies finish first), dumps the database to `/var/backups/octopushy/` on the host, snapshots the
container, takes `compose.yaml` from the matching Git ref (`v0.2.0`, `main`, or the
commit), sets the image tags in `.env`, and starts the project; `migrate` runs before the
backend. If `/api/health` does not answer within three minutes, it rolls the container
back to the snapshot: database, data volume, and configuration together. It keeps the
three newest update snapshots (`--keep N`) and the five newest dumps.

Wait for running provision runs to finish before you update; there is no automatic drain
yet. Migrations are forward-only: to go back after a successful update, roll back to its
snapshot (`pct rollback <ctid> octopushy-<time> --start 1`, then
`pct exec <ctid> -- octopushy-compose up -d`) rather than starting older images.

To make a release: set the new version in `backend/Directory.Build.props`,
`frontend/package.json`, `agent/Cargo.toml`, and the default in `install.sh`, tag that
commit `v<version>`, and push the tag. The workflow publishes `backend-<version>`,
`frontend-<version>`, and `agent-<version>`.

## Day to day

```sh
pct enter <ctid>                                    # a shell in the container
pct exec <ctid> -- octopushy-compose ps
pct exec <ctid> -- octopushy-compose logs -f backend
pct exec <ctid> -- octopushy-compose logs -f agent
```

The container applies Debian security updates by itself (`unattended-upgrades`). Docker
comes from Docker's repository and is updated with `apt upgrade` in the container.

**Docker in an LXC container depends on the host's kernel and AppArmor.** A Proxmox
upgrade has broken Docker in containers before. Snapshot the container before upgrading
Proxmox too, and check `octopushy-compose ps` afterwards. Proxmox recommends a VM for
Docker; the Compose project and `.env` move to a Debian VM unchanged, but
`octopushy-update` only handles containers.

## Removing

```sh
pct stop <ctid> && pct destroy <ctid>    # deletes the container, its volumes, and snapshots
rm /usr/local/sbin/octopushy-update
```
