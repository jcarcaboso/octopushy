# Services

**Decided November 2, 2026.** Octopushy works in three layers:

1. **Create the machine.** OpenTofu, for a VM or a container.
2. **Prepare the machine.** Ansible, through a guest's `setup`.
3. **Deploy services.** Ansible again, through a `service.yaml` in the guest's folder.

This page is the design of the third. A service is a resource in the existing tree, and a service run is
a [configure run](guest-configuration.md) on that resource: the same executor, preview, approval, outcomes and
secret handling, with no second path. It carries no knowledge of any one application.
[`ha-db`](#worked-example-ha-db) is the worked example: one container, two services.

**What is new compared with [lab-repository.md](lab-repository.md)** is marked **New** below, and
[listed at the end](#what-changed-from-lab-repositorymd).

## Definition

```yaml
# labs/home/proxmox/ziost/guests/ha-db/services/postgresql/service.yaml
schemaVersion: 1
deploy:
  recipe: postgresql              # a configure recipe, as for a guest's setup
  inputs:                         # extra vars, validated by the recipe's schema
    postgresql_database: homeassistant
  secrets:                        # extra var name -> store reference
    postgresql_password: { store: ha-db/postgres-password }
ports:
  - { port: 5432, protocol: tcp, sources: [10.15.40.10] }
policy:
  destructive: manual
```

The folder is the identity: the address is `home/ziost/ha-db/postgresql`, and moving a folder makes a new
resource (rule 4).

- **`deploy.recipe`** names a recipe with `operation: configure` and `executor: ansible`, exactly as `setup.recipe`
  does. That recipe names the Ansible project and its actions, as [guest configuration](guest-configuration.md#definitions)
  describes.
- **`deploy.inputs`, `deploy.secrets` and `deploy.secretFiles`** mean what the same fields of `setup` mean: the
  same schema check, the same reserved names (`ansible_*`, `octopushy*`), and the same delivery.
- **Which action** is the recipe's: the one with `onChange: true` runs on a change, and any other runs when a person
  chooses it. An action is the whole unit, as for a guest.
- **The connection is the guest's.** A service has no `access`: it uses its guest's address, user, SSH key and pin, so a
  guest with services must declare them.

### Instances are standalone; code may be shared

The lab repository says two instances share no definition, while a recipe is shared by design. Both hold, because
they are about different things:

| Shared, under `recipes/` and `modules/` | The instance's own, in its folder |
| --- | --- |
| The Ansible project, its actions, and what an input means | Its inputs, secrets, ports and policy |
| | Its status, history, approval, and failures |
| | Any files of its own, reached as `{{ octopushy.guest_directory }}`, which is the service's folder on a service run |

So rule 6 stands for **definitions and state**: nothing about one instance is inherited from another, and two
instances of one recipe are planned, approved and failed separately. **New:** the rule no longer says a folder holds
the code. Rule 8 already allowed shared tool-native code under `modules/`, and copying a playbook into every
folder would mean a fix has to be made many times, so that is what a recipe is for. A commit to a shared recipe gives each
of its instances a new input hash, and each gets its own run, under its own policy.

## Relation to `setup`

| | Guest `setup` | Service |
| --- | --- | --- |
| What it is for | What the machine needs whatever runs on it | One thing that runs on it, with its own lifecycle |
| Resource | The guest | The service |
| A failure marks | The guest's configuration | That service only |

**The order is provision, then setup, then services.** A service waits for all of these, and is shown as *waiting*, not
failed, until they hold:

1. The guest is bound, and its provisioning has caught up with its definition: the rule a configure run already has.
2. The guest's setup, if it has an `onChange` action, has been applied at its current inputs.

**A service never marks anything else failed.** It has its own runs, so a failed service leaves its guest's status and its
siblings' statuses as they were. A failed `setup` does hold back the guest's services, since they would be deployed on
a machine that is not prepared, and they say why.

**Two services on one guest run one at a time.** So do a guest's own runs and its services: nothing that changes a
guest starts while something else is changing it. This holds only for the work that changes something: a preview or a
plan can run beside it, and **a run waiting for approval does not hold its siblings**, so a service nobody has
approved yet cannot stop another from deploying. An uncertain run does hold them, until a person closes it, because it may
still be running. **New:** the run that waits says which run it waits for.

`dependsOn` is not built: `ha-db` needs no order between its services, and a schema that accepted it would promise
what nothing does. It is refused, like any unknown field, until something needs it.

## Approval and policy

Each service has its own `policy`, resolved through the tree as usual: repository, lab, installation, guest, service, and
the nearest wins. So a guest's `policy` reaches its services unless they say otherwise, while **approval stays per
resource: approving a guest change never approves a service change**, because they are separate runs with separate
approval steps. `execution.agentLabels` flows down the same way.

`policy.approval: manual` waits on every run. `policy.destructive: manual` waits for an action the recipe declares
`destructive: true`. As with a guest, that declaration is the repository's claim: nothing can show what a playbook will
delete. A change that can lose data belongs on an action that says so, and the action that runs on a change should refuse
to do it. `ha-db`'s [retention](#what-happens-to-a-change) is the pattern.

## Triggers

| Trigger | Action |
| --- | --- |
| The first time the guest is [ready](#relation-to-setup) | the `onChange` action |
| A change to `service.yaml`, the recipe, its project, the service's folder, or the guest's `access` | the `onChange` action |
| A provision run that creates or replaces the machine | the `onChange` action, after `setup` |
| **Deploy** on the service's page, or `POST /api/services/{id}/run` | the one chosen |
| A schedule | Not built |

**Each input hash gets one automatic run.** A failure is not retried by itself: it waits for a new commit or a person. A
guest's setup changing does not re-run its services, since nothing about them changed. A person cannot run a service
whose guest is not ready either.

The guest's hash does not include `services/`, so a change to a service does not configure its guest again.

**Schedules are not closed off.** A schedule would call the same `Planner` method with the reason `schedule`, and a
field on an action is a compatible addition, so the `setup.schedules` work needs nothing different here.

## Status

A service has its own status, from its own runs and what it waits for: *in sync*, *pending*, *waiting*,
*running*, *blocked*, *awaiting approval*, *failed*, *uncertain*, *invalid* or *missing*.

- **Guest page:** a card lists each service with its status, ports, last run, and a link to it.
- **Service page:** `/services/{id}`, with its status, why it waits, its actions, what trusting it covers, and its run history.
- **Inventory:** each guest lists its services, and `GET /api/inventory` carries them, with their status.
- **Graph:** **there is no graph view yet**, on any branch. The inventory API has what one would draw: each service,
  its address and its parent guest.

## Removal

**Deleting a service's folder does not delete its data.** It marks the service *missing*, and what happens next is the
recipe's to declare.

```yaml
# recipe.yaml
actions:
  remove:
    playbook: remove.yml
    destructive: true             # removal has to be declared destructive
    removal: true                 # at most one action; it runs when the folder is deleted
```

- **With a removal action,** Octopushy proposes it, once, built from the revision last applied, as a guest's removal is. **Run
  removal** on the service's page proposes it again, and **Trust** there covers what it would run.
  It **always waits for a person unless `destructive: automatic` is written down somewhere**: following an automatic approval
  is not enough, the same rule as replacing a VM (rule 11). When it succeeds the service is retired, and its history stays.
- **With none,** nothing runs. The service keeps running on the guest, shown as *missing*.
- **Forget** stops tracking a missing service and changes nothing on the guest. It is the way out of a removal that cannot succeed.
- **If no run ever reached the guest,** there is nothing to remove, so it is retired at once. A deployment that failed partway did reach
  it, and one still going is waited for.
- **Restoring the folder** cancels a removal that has not started.
- **What the action deletes is the recipe's choice.** `ha-db`'s stop their services and leave the data on disk.
- **Retiring the guest takes its services with it.** **New, and narrower than rule 11:** the guest's own destroy
  removes everything in it, so services get no runs of their own. Their policies cannot hold that back, so a retire run of a
  guest with services always waits for a person, and says which ones. A `stateful` guest stops first, as ever.

## Containers over SSH

A run reaches a container over SSH, with the same pin as a VM. Two things need saying, because cloud-init does not
apply to a container and Proxmox has no guest exec for one.

**`sshd` and a login user.** The Debian 13 template ships `openssh-server` and `python3`, and `root` is the login:

- `access.sshAuthorizedKeys` holds the public half. Proxmox writes it into `/root/.ssh/authorized_keys` when it creates the
  container, and only then, so a change to it later does nothing.
- `access.sshKey` is a store reference to the private half, and `access.user: root`.
- Debian's default `PermitRootLogin prohibit-password` accepts the key and no password.

**The first pin.** The template holds host keys of its own, and every container made from it would share them, so Proxmox
replaces them in `pct create`: `rewrite_ssh_host_keys` in pve-container makes new ones and prints each fingerprint into the
create task's log. So the key exists before the container first starts. Octopushy never scans, so a person reads the key through Proxmox:

```sh
# on the Proxmox host, as root
pct exec 102 -- cat /etc/ssh/ssh_host_ed25519_key.pub
```

Set the line under **Variables** as a **plain** entry, at the key `access.hostKey` names. Until then the guest's setup
run is blocked with *Referenced but not set*, and its services wait. Then it goes on by itself. The chain of
authentication is your login to the host, then `pct`, with no network path to the guest involved. The console in the Proxmox
interface does the same job.

If you would rather not use a shell on the host, the create task's log gives the fingerprint through the Proxmox API,
so a key copied from anywhere can be checked against it with `ssh-keygen -lf`. That is the same anchor, with one more step.
This comes from reading pve-container, and **it has not been seen on a host**. A replaced container has new keys, so its runs fail closed
until the pin is set again.

Nothing here gives a run Proxmox credentials: service and configure runs hold the SSH key, the pin and their own
secrets, and `CredentialBindings` refuses anything else.

## Ports and DNS

**This slice covers `ports`, as a declaration. It covers no `dns`.**

| Declared in the design | Here |
| --- | --- |
| `ports[].port`, `protocol`, `sources` | Accepted, validated, part of the input hash, shown in the preview and on the service's page, and handed to the playbook |
| `ports[].interfaces` | Refused: nothing needs it |
| The ports step, gathering a guest's ports and opening them | **Not built. Octopushy opens and closes nothing** |
| `dns` | Refused: no adapter exists to serve it |
| `verify` | Refused: a playbook ends by checking itself, and its failure is the run's |

A definition that accepted `dns` and did nothing with it would be worse than one that refuses it.

**The service's own project enforces a limit.** `octopushy.ports` holds what `service.yaml` declares, and the playbook applies it,
so there is one place to say it. Octopushy does not check that a playbook did: this is the repository's claim, as `destructive` is.

For `ha-db`, PostgreSQL's limit is **enforced by PostgreSQL**: `pg_hba.conf` is written whole from `sources`, with one `host` line
for each, and what is not listed is refused, even with the right password. Port 5432 itself stays open at the network
level, which is honest about what this is: a connection from elsewhere reaches PostgreSQL and is refused there. A Proxmox
firewall rule would stop it sooner: `recipes/proxmox-lxc` takes `inputs.firewall`, but that is inert until the datacenter firewall is on, and it
would also have to let the agent in on port 22, so `ha-db` does not rely on it.

## Trust

A protected store entry goes to a service or a guest's setup as it does to a guest: only with a grant, for a digest. For a
configure run, that digest covers the recipe, its Ansible project, the resource's own folder, and how it runs: the
address, port, user, the SSH key and pin it uses, its agents, inputs, ports and which variable each secret becomes. A
run holding a protected entry gets only those folders.

**Trust** on the service's page, or `POST /api/resources/{id}/trust-configure` with the digest it showed, records the grants.
A changed playbook or binding blocks the run again until the next trust. A guest's setup has the same thing on its page.

[ADR 0004](adr/0004-guest-configuration-trust.md) is unchanged: a key that any installation declares, or that has gone to
Proxmox or OpenTofu work, never reaches a playbook.

## Worked example: `ha-db`

`ha-db` is container 102 on `ziost`, under [`examples/lab/labs/home/proxmox/ziost/guests/ha-db`](../examples/lab/labs/home/proxmox/ziost/guests/ha-db).

| Layer | Recipe | What it does |
| --- | --- | --- |
| Machine | `proxmox-lxc` | Debian 13, unprivileged, nesting=1, 2 cores, 1024 MiB, 256 MiB swap, 32 GiB on `vm-ssd`, start at boot, order=1 up=30, bridge `apps`, 10.15.30.40/24. `stateful: true`. |
| Setup | `debian-base` | Python for Ansible, `unattended-upgrades`, `cron`. |
| Service | `postgresql` | PostgreSQL 17 from Debian, role and database `homeassistant`, reachable only from 10.15.40.10, a nightly `pg_dump` with seven kept. |
| Service | `victoriametrics` | 1.155.0, checksum-pinned, retention 2y, a password, and a relabel rule keeping only `ha_value`. |

**Why this split.** Setup is what any service on this machine would want. Each database is a service because it owns data, has
a port, has a password, and has a lifecycle of its own: a new VictoriaMetrics must not restart PostgreSQL, and a failed
VictoriaMetrics must not stop Home Assistant's recorder from being deployed. Dumps belong to PostgreSQL's service, because they
are what it backs up.

### What happens to a change

| Change | What runs | Effect |
| --- | --- | --- |
| Cores or memory | Provision, in place | Nothing restarts in the container |
| A base package in `debian-base` | The guest's setup | Services are not touched |
| PostgreSQL settings or `sources` | `postgresql`, `deploy` | Reload or restart of PostgreSQL only |
| VictoriaMetrics version and checksum | `victoriametrics`, `deploy` | Restart of VictoriaMetrics only; the data stays |
| Retention raised | `victoriametrics`, `deploy` | Restart; nothing is lost |
| **Retention lowered** | `victoriametrics`, `deploy` **fails**, and says to run `shrink-retention` | Nothing changes until a person runs that action, which is `destructive: true`, so it waits under `destructive: manual` |
| A password | Update the entry under Variables and **Promote** the candidate, then raise `credentials_revision` in `service.yaml` | That service only. The store alone starts nothing, since a commit is what can be reviewed |
| The template, or anything that replaces the container | Provision **fails before approval**: the guest is `stateful` | See [stateful guests](lxc-recipes.md#stateful-guests) |

### Passwords

There are two, and **Home Assistant has to read both, so the operator makes them and keeps a copy**. Octopushy never shows a
secret again. Generate them with `openssl rand -hex 24`, which needs no escaping in a database URL. Put them in Variables as
secrets, tick **Protected**, and put them in Home Assistant's `secrets.yaml`. They reach the playbooks through
[the usual path](guest-configuration.md#secrets), and are in neither Git, a state, a saved plan, a preview nor a log.

On the container, the VictoriaMetrics password is in `/etc/victoria-metrics/auth.env`, mode 0600, as it must be. PostgreSQL holds a SCRAM hash.

`pg_dump` output lives in the same container, so it is **not** a backup against losing it. See [backups](lxc-recipes.md#backups).

### The first deployment, in order

Nothing here has run on a real Proxmox host. Everything below is what the end-to-end test does, with the host's part
done by hand, and the rest comes from reading Proxmox's source.

1. **Make the login key,** `ssh-keygen -t ed25519 -N '' -C octopushy@ha-db -f ha-db-key`. Put the public line in `access.sshAuthorizedKeys`
   **before the container exists**: Proxmox reads it only then. Put the private key under Variables as the secret `ha-db/ssh-key`.
2. **Make the passwords,** `openssl rand -hex 24`, twice. Add them as the secrets `ha-db/postgres-password` and
   `ha-db/victoriametrics-password`, tick **Protected** on both, and keep a copy for Home Assistant.
3. **Check the definitions.** `sources` has Home Assistant's address, and `reservedVmids` lists VM 100.
4. **Open the network.** The agent needs port 22 of 10.15.30.40. The container needs Debian's mirrors and `github.com`. Home Assistant,
   10.15.40.10, needs ports 5432 and 8428 of 10.15.30.40: a firewall rule, outside this repository.
5. **Commit, and let it provision.** Trust the installation if it is new, and approve if the policy asks.
6. **Set the pin.** On the host, `pct exec 102 -- cat /etc/ssh/ssh_host_ed25519_key.pub`, then set that line as the plain entry
   `ha-db/host-key`. Setup goes on by itself.
7. **Trust each service** on its page, once you have reviewed the recipes. Each then deploys, one at a time.
8. **Connect Home Assistant.** Add the InfluxDB integration first (version 1, `http://10.15.30.40:8428`, user `homeassistant`, database
   `ha`), then the recorder's `db_url` and the `influxdb:` block, from `secrets.yaml`.
9. **Make VM 100 start after it,** `qm set 100 --startup order=2`: that is a setting on the host, outside this repository.
10. **Add a Proxmox backup job** for container 102, to storage other than `vm-ssd`.

## What is not here

- **No `dependsOn`, `dns`, `verify`, `interfaces`, or ports step,** as above.
- **No Compose runtime.** `deploy.compose` is refused.
- **No application clusters of services.** Technitium keeps installing through OpenTofu, and moves here later.
- **No schedule.**
- **No drift detection.** A service's status says what its last run did, not what is true now.
- **No graph view.**

## What changed from `lab-repository.md`

- A service names a recipe in `deploy.recipe`, rather than holding everything in its folder (rule 6, narrowed to definitions and state).
- `deploy.compose`, `dependsOn`, `dns` and `verify` are refused for now.
- `ports` is a declaration handed to the playbook. The ports step stays a proposal.
- Removal needs a declared `removal: true` action, and a person unless `destructive: automatic` is written down.
- Retiring a guest takes its services with it, and always waits for a person when it has any (rule 11 said each service retires first).
- A run that waits for approval does not hold its siblings; the doc's "steps on one guest run one at a time" applies to work that changes something.
- `setup` gates services. The doc only said that setup comes first.
