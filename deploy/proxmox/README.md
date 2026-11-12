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
  only `<version>` for every `v<version>` tag, without SHA or `latest` aliases.
  It needs the GitHub repository secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`. The container pulls without
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
| `OCTOPUSHY_VERSION` | `0.9.1` | A release, `main` for the newest build of the main branch, or `sha-<commit>` |
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
machine. It needs network access to your Proxmox hosts' API (port 8006) and to the
OpenTofu registry for providers. OpenTofu state needs no setup: the backend keeps it in
its database and hands it to the agent over their private channel, so `octopushy-update`
keeps it and its database dump covers it. Restoring that dump needs the key above.

It runs OpenTofu and Ansible directly (`--tool-runtime local`) inside its own Docker
container, as an unprivileged user. Ansible needs a route to the guests it configures,
and the `tmpfs` on `/tmp` that `compose.yaml` gives it. It shares only a private network with the backend, so it cannot
reach PostgreSQL, and it gets neither the key nor the database password. The state it
works on is decrypted for one attempt of the guest it was assigned. It still runs
on the same machine as the server. Once you manage more than this lab, or want a
stronger boundary, add agents on their own VMs, enrolled by hand with a token from
**Agents**, and give the internal agent's labels only to what should run here.

To enroll it again, for example under another name, remove it under **Agents** and run
`pct exec <ctid> -- sh -c 'octopushy-compose rm -sf agent && docker volume rm octopushy_agent'`
before enrolling with a new token (`OCTOPUSHY_AGENT_NAME=<name>` picks the name).

## HTTPS

The web interface is plain HTTP on port 8081. Put a TLS proxy in front of it; the
frontend trusts `X-Forwarded-Proto` from private addresses, so session cookies become
secure once the proxy sets it. Without a proxy the web interface and the
[operator CLI](../../docs/operator-access.md#plain-http) still work
over plain HTTP, but passwords, session cookies and tokens are then readable by anyone on
the network path. With Traefik's file provider:

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

## Snippet uploads, for guests' host keys

A guest Octopushy creates can get an SSH host key of Octopushy's, delivered in a cloud-init
snippet, so nobody reads a key off its console. The Proxmox API cannot upload a snippet, so each
host needs a one-time setup, once Octopushy is on a release that ships
[`snippets.sh`](snippets.sh). On the installation's page, **Create upload key**, then run the
command it shows as root on each host, for example in the host's shell in the Proxmox web
interface. It has this form:

```sh
OCTOPUSHY_SNIPPETS_KEY='ssh-ed25519 AAAA… octopushy-snippets@home/pve01' \
  OCTOPUSHY_SNIPPETS_USER='octopushy-snippets' OCTOPUSHY_SNIPPETS_DATASTORE='local' \
  bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/v<version>/deploy/proxmox/snippets.sh)"
```

It changes, and prints:

| | What |
|---|---|
| Datastore | `snippets` added to the content of `local` (`OCTOPUSHY_SNIPPETS_DATASTORE` picks another directory datastore), and its `snippets` directory made writable by the account's group, mode 2770 |
| Account | `octopushy-snippets` (`OCTOPUSHY_SNIPPETS_USER`), a system user with no password and no shell |
| Chroot | `/var/lib/octopushy-snippets`, holding only the snippets directory at its own path, through a bind mount kept by a systemd mount unit |
| sshd | `/etc/ssh/sshd_config.d/00-octopushy-snippets.conf`: one `Match User` block allowing only that key, only SFTP, only in the chroot, files created mode 0600, no forwarding or TTY. The key is in `/etc/ssh/octopushy-snippets/authorized_keys`, marked `restrict` |

Before it changes anything, it refuses:
- root, or an account or group that exists and that it did not create;
- an `sshd_config` that does not start with `Include /etc/ssh/sshd_config.d/*.conf`;
- a configuration `sshd -t` already rejects;
- a drop-in of its name that it did not write, or one that sorts before it;
- any other sshd file that mentions the account.

Its block sorts first and names only that account, so sshd applies it to every connection of that
account and to no one else, and it lets the account in with its key alone: no CA certificate,
key or principals command, password, host-based, GSSAPI or Kerberos login, even where the host
allows them. sshd's files are copied first, with their modes, checked with `sshd -t` and reloaded,
or put back byte for byte if anything fails, for setup and `--remove` alike. It refuses a link
at any path it writes or backs up. `--remove` also refuses while the account has a process, such
as an open SFTP session, while any process uses the chroot, or while the chroot holds something
it did not make. It locks and expires the account and deletes its key first, and removes the
block last, so if any step fails part way, the account still cannot log in. It gives root the
snippets it keeps before freeing the account's UID. Then it prints the host's own SSH host key. Store that line in **Variables** as a **plain** value,
under the key the installation's `snippets.hosts.<host>.hostKey` names; uploads connect to that
key and nothing else. Running the script again changes nothing.

Then list the host in the lab repository's `installation.yaml`:

```yaml
snippets:
  hosts:
    pve01:
      hostKey: { store: proxmox/pve01/ssh-host-key }   # the plain value the script printed
```

From then on a guest on that host whose workflow's machine stage has `hostKey: managed`, and that
names no `access.hostKey`, gets Octopushy's host key, generated for it and pinned by Octopushy. Its
private half reaches only the guest's provision plans, and a new machine for the guest gets a new
key. Dropping `access.hostKey` from a guest that already exists moves its user data to a snippet,
which replaces the VM, under its `policy.destructive`.

To undo it on a host:

```sh
bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/v<version>/deploy/proxmox/snippets.sh)" -- --remove
```

That removes the account, chroot, mount unit, sshd block and key, gives the snippets directory
back to root, and leaves the datastore's content and any snippets as they are. To stop offering
snippets as well, run `pvesm set local --content` with the list it had before.

Agents that provision those guests then need to reach each host's sshd, port 22 unless
`snippets.hosts.<host>.port` says otherwise, as well as its API.
[Host key trust](../../docs/guest-configuration.md#host-key-trust) has the rest, including who can
read a guest's private host key.

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
yet.

**Updating from a release before 0.5.0.** Release 0.5.0 and later cannot use a
database created by an earlier release: its `migrate` refuses it, logs that a fresh database is
required, and `octopushy-update` rolls the container back. Start again with an empty
database instead ([what that means](../README.md#starting-again-with-a-fresh-database)):

```sh
pct exec <ctid> -- octopushy-compose down
pct exec <ctid> -- docker volume rm octopushy_postgres octopushy_data octopushy_agent
pct exec <ctid> -- octopushy-compose up -d postgres   # an empty database for the update to dump
octopushy-update <ctid> <version>
```

Then run setup with the new setup code, connect the lab repository, re-enter its variables,
re-enroll the internal agent with a new token (`pct exec <ctid> -- octopushy-agent-enroll 'opy1.…'`,
as on the first start) and any other agent, and trust each installation again. The key on
`mp0` stays. Migrations are forward-only: to go back after a successful update, roll back to its
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
