# LXC recipes: any container, installed from the repository

**November 2, 2026.** A recipe can put any software in a Proxmox container the same way a guest of
type `vm` goes through Octopushy: define it in `guest.yaml`, **plan**, **approve**, **apply**,
**verify**. [`ha-db`](#worked-recipe-ha-db) is the first, and
[`technitium-lxc`](technitium-lxc.md) was moved onto the same module.

## What Octopushy does, and what it does not

Octopushy's side is generic and already existed: `type: container`, `provision.inputs`,
`provision.secrets`, a recipe's `secrets` and `modules`, `guestType: container`, and `inputs.startup`.
It does the same for every container:

- reads the guest and the recipe, checks `provision.inputs` against the recipe's schema, and hands
  OpenTofu the guest as `var.guest`, the host's settings as `var.host`, and the template as `var.template`;
- plans without any secret, shows the plan, waits for approval where the policy says so, and applies
  that saved plan with the secrets `provision.secrets` names, which arrive as `TF_VAR_<name>` for the
  apply only;
- lifts Proxmox's protection flag, only for a plan its policy lets through, and puts it back if the
  apply does not finish ([the rule](lab-repository.md#rules), item 11);
- checks the container through the Proxmox API afterwards (`verify: proxmox`).

It does not know what is installed. **There is no executor and no apply operation for any
application:** Proxmox has no user-data or exec for containers, so the install is OpenTofu's, over
SSH, and it lives in the repository. Nothing in Octopushy mentions PostgreSQL, VictoriaMetrics, or
Technitium. Changing what a recipe installs is a commit, and the next plan shows it.

## A recipe, file by file

```text
recipes/ha-db/
  recipe.yaml            root, modules, secrets, guestType: container, verify: proxmox
  inputs.schema.json     what guest.yaml may say about this recipe
modules/opentofu/ha-db/  the root: thin
  versions.tf            providers, and the proxmox provider block
  variables.tf           octopushy, guest, host, template, one ephemeral variable per secret
  main.tf                one module "app" call, and nothing that installs
  outputs.tf
  .terraform.lock.hcl    pins the providers; a recipe that takes a protected secret needs it
  files/                 uploaded as one directory
    install.sh           the script that is run
    ...                  its configuration
modules/opentofu/lxc-app/      shared: the container, SSH access, upload, seal, run
modules/opentofu/proxmox-lxc/  shared: the container itself
```

`recipe.yaml` lists the modules the root uses, so that a change to one plans the recipe's guests again:

```yaml
root: ../../modules/opentofu/ha-db
modules: [../../modules/opentofu/lxc-app, ../../modules/opentofu/proxmox-lxc]
secrets: [postgres_password, victoriametrics_password]
```

### The `lxc-app` module

A root calls it once. What it takes:

| Variable | What |
| --- | --- |
| `octopushy`, `host`, `template`, `guest` | Passed through from the root's variables. The guest must have a static address: OpenTofu installs over SSH. |
| `firewall` | Optional. Proxmox firewall rules for the container, with everything else dropped. |
| `app.files` | A directory, uploaded as it is with the `file` provisioner. Everything in it is part of what an operator reviews. |
| `app.script` | The file in that directory that is run, as root, in the container. |
| `app.environment` | Not secret. Names match `^[A-Z][A-Z0-9_]*$` and are not `OCTOPUSHY_*`, `LD_*`, `BASH*`, `PATH`, `IFS`, `HOME` and the like. Values are one line without control characters. They reach the script as a file of `NAME=value` lines that is read, never evaluated: nothing from a variable is an unquoted part of a shell command. |
| `app.reinstall_on` | Values whose change must install again, such as a revision that stands for a secret the plan cannot see. The files and the environment already do. |
| `app.nonce_record` | Where the container remembers the seals it opened. It has to survive an install again; the default is on the root disk. |
| `secrets` | An ephemeral `map(string)`: one entry for each secret. |

The module's outputs are `vm_id`, `ipv4_addresses`, `address`, `incarnation` (changes whenever the
container is replaced) and `installed`. A root adds what is its own: the settings its script needs,
its input checks, its outputs, and, for a stateful recipe, [the guard](#stateful-guests).

### What the script gets

`run.sh`, which the module uploads, reads the environment file, opens the seal, and runs the recipe's
script from `$OCTOPUSHY_APP` with:

| | |
| --- | --- |
| `$OCTOPUSHY_ADDRESS` | The container's address, without its prefix. |
| `$OCTOPUSHY_APP`, `$OCTOPUSHY_WORK` | The uploaded directory, and the folder holding everything under `/run/octopushy-install`. |
| `$OCTOPUSHY_SECRETS` | A folder of mode 0700 with a file of mode 0600 for each secret, named as the map's key. |
| `$OCTOPUSHY_NONCES` | The record of used seals; scripts do not touch it. |
| `app.environment` | Each entry, as an environment variable. |

Everything under `/run/octopushy-install` is removed however the script ends, the secrets included:
`run.sh` traps its exit, a hang-up, an interrupt and a termination. A `SIGKILL`, such as the kernel
killing it out of memory, cannot be trapped: the secrets then stay in that tmpfs, mode 0600, until the
next install or reboot.

**A script must be run again without harm.** The install runs whenever the container is created, a
file or a value it depends on changes, or the install is tainted by a failure. So it must converge:
it installs what is missing, rewrites a file only when it differs, restarts a service only when
something it uses changed, never deletes data, and ends by checking itself, failing the apply if the
result is not what it declared. [`ha-db`'s](../examples/lab/modules/opentofu/ha-db/files/install.sh) is
the pattern.

### Review rules

A recipe that takes a secret is reviewed as a whole ([installation trust](installation-trust.md#protected-secrets)):

- `modules:` lists every module the root calls, and every module those call. A listed folder
  that calls an unlisted module is an error.
- The root has a `.terraform.lock.hcl`, and no `.terraform`.
- The files are read as HCL, not by matching lines. A file that cannot be read with certainty is
  refused: `$${` and `%%{` (HCL's escapes), a comment or a heredoc inside an interpolation, a heredoc
  that interpolates, and `.tofu`, `.tofu.json` and `.tf.json` files.
- The digest covers the recipe's folder, the root with its `files/`, and the listed modules. A run
  that holds a protected secret is given those folders and no others, and runs `tofu init` with
  `-lockfile=readonly`, and with `-get=false` when the root calls no module.

## Secrets

A recipe names its secrets (`secrets: [postgres_password, victoriametrics_password]`). Each guest binds
each to a store entry:

```yaml
provision:
  secrets:
    postgres_password: { store: ha-db/postgres-password }
```

The root declares one ephemeral variable for each, null while planning, and passes them to the module as
one map:

```hcl
variable "postgres_password" {
  type      = string
  ephemeral = true
  nullable  = true
  default   = null
}

module "app" {
  # ...
  secrets = { postgres_password = var.postgres_password }
}
```

**Real OpenTofu 1.13.1 lets an ephemeral map reach a module's provisioner**, and one map is what is
needed: a provisioner cannot be repeated for each secret. `tests/ha-db` and `tests/technitium-lxc` run it,
and scan the saved plan, `tofu show -json`, the state and the apply log for the secrets: they are in none.
A secret is in the plan only if it is not ephemeral.

### The seal

OpenTofu cannot tell who answers on SSH: it checks a host key only against a public key, and nothing can give
it the container's before it connects. So the secrets cross SSH sealed, all in **one** file, to a key that
reaches the container only as the comment of an SSH key in the Proxmox API. Its properties are exactly
those [`technitium-lxc`](technitium-lxc.md#the-admin-password) always documented, now for several secrets:

- the sealing key is its own, not derived from the SSH key, and travels only through Proxmox's API;
- a random nonce for each upload (`uuid()`), so a retry has a new one;
- encrypt then MAC: the cipher adds, mod 64, the base64 of the `name:base64(value)` lines to a keystream of
  SHA-256 blocks, and the tag is a double SHA-256 over the authentication key, the guest's id and address, the
  nonce, and the cipher;
- the tag is checked before anything is written or opened, and a nonce already in the record is refused;
- the container opens it with `base64`, `sha256sum`, `basenc` and the shell: **no `jq`**, which the Debian
  template does not have.

**It is a hand-built seal, not a vetted construction**, and has had no review beyond this project's. What it
does and does not do is in [the Technitium page](technitium-lxc.md#the-admin-password); it is the same.
The record of used nonces is `/var/lib/octopushy-install/used-nonces` unless a recipe says otherwise, on the
root disk, so it survives an install again but not a new container. `tests/lxc-app` and the two integration
tests run a changed seal, one for another guest, and one that comes round again.

### What the store does with a secret

An operator sets a secret under **Variables** or with `PUT /api/variables`. After that, **nothing shows it again**:

- `GET /api/variables` returns `value` only for an entry whose visibility is `plain`; for a secret it is null.
- `VariableStore.GetAsync`, which decrypts, is used by the server for its own two credentials (the
  repository's and the disk reporter's), not by an API.
- A run's secrets are decrypted by `ResolveForAttemptAsync`, for an attempt, to hand to an agent, and only for an
  apply of a recipe's secrets, only after approval and the checks of
  [protected secrets](installation-trust.md#protected-secrets).

So **the operator makes both passwords, and keeps a copy**: Home Assistant's `secrets.yaml` needs them, and
Octopushy will not hand them back. They must not be in Git, in a state, or in a saved plan. Rolling a password
means setting a new one, a new store version, and raising `credentialsRevision` in `guest.yaml` (below).

### Trusting a guest

A protected store entry is released only to a resource with a grant, for code with a digest. A cluster's
**Trust cluster** records the grants for its members. A guest that belongs to no cluster has the same on its
own page:

1. Mark the entries **Protected** under **Variables**.
2. The guest waits with *Waiting for trust*. Its page shows why, and the same details Trust cluster shows
   for a member: the protected entries it binds, the digest of its code and of how it runs, its state backend,
   variables, agents, Proxmox connection and the tokens it uses, template, volumes, and every key that can log
   in to it as root, shown by its end.
3. **Trust guest** records a grant for each protected key the guest's run binds. It sends the digest it
   showed, and the server refuses it if the guest is now declared otherwise.

`POST /api/guests/{id}/trust` with `{ "codeDigest": "..." }`. It is refused for a guest that is a member of a cluster
(Trust cluster covers it), for one that binds no protected key, and for one that gives a protected key to its state
backend, which is never released. Whatever the repository's `policy` says, a run waits for the grant, and a
changed root, a changed binding, or a different key blocks it again until the next trust.

#### What an attacker can do to a single guest with protected secrets

- **With commit access.** The grant covers the digest of the recipe, the root, its `files/`, the listed
  modules, and the whole of how the guest runs. A commit that changes any of it stops the guest, and it
  receives nothing until an operator trusts the new digest, so a commit cannot start sending a secret
  somewhere by itself. What it can do is get an operator to trust something they did not read: **trust is a
  review, and it is only as good as that review.** It cannot grant, protect or unprotect: those are in the
  database. It cannot move a protected entry to a state backend (never released), or add a login key (it is in
  the digest). A commit that is trusted runs OpenTofu with the secret.
- **On the LAN.** OpenTofu cannot authenticate who answers on SSH at the container's address, so an
  impostor there receives the upload: ciphertext, the scripts, and the environment file, and no key. It can accept the
  session and report success, so **an apply that succeeds does not prove the real container was installed.** The
  install checks itself, but an impostor controls what it reports. An attacker on the path to the Proxmox API
  could read the sealing key from the create call; installation trust is by endpoint, and only `tls: { verify: true }` (or a
  CA of your own) makes the certificate prove it is Proxmox answering.
- **Holding state.** The state has the generated SSH key and the sealing key. With them one can sign in to the container
  as root and open a seal captured earlier. The secrets are not in the state, and neither is the
  PostgreSQL password nor the VictoriaMetrics password in the saved plan. The container itself holds the
  VictoriaMetrics password in one file of mode 0600 for root, and a SCRAM hash of the PostgreSQL one.
  An external state backend keeps its own copy of the state, which is one more reason a protected secret
  cannot be given to one.

## Stateful guests

Proxmox deletes a container's volumes with the container, and that includes a mount point of its own: it is
deleted too, so a mount point alone does not save data, and one made by `size` is lost when the container is
replaced. `ha-db` keeps its data on the root disk. For a stateful recipe, **a plan that would replace or
destroy the container must stop**, before anyone is asked to approve it.

The root carries this resource, with `prevent_destroy` as a literal (OpenTofu does not accept a variable
there, so this cannot be a setting):

```hcl
resource "terraform_data" "keep_data" {
  triggers_replace = module.app.incarnation
  depends_on       = [module.app]

  lifecycle {
    prevent_destroy = true
  }
}
```

It follows the container's incarnation, which changes whenever the container is replaced, so a replacement
replaces this resource and OpenTofu refuses. `depends_on` creates it only after the install has finished.

`tests/ha-db` proves each of these with real OpenTofu 1.13.1 and the real bpg/proxmox provider, against a
stand-in for Proxmox's API that refuses to delete a protected container as Proxmox does:

1. **A change that forces the container's replacement fails at the plan, with an error naming the guard, before any
   approval.** A forced replacement, and an edit that Proxmox cannot make in place, both end like this in the run's log,
   verbatim from OpenTofu 1.13.1. Octopushy refuses a changed VMID earlier, so the edit you can reach is another datastore
   for the root disk; the test changes the VMID directly in OpenTofu:

   ```text
   Plan: 4 to add, 0 to change, 4 to destroy.

   Error: Resource instance cannot be destroyed

     on main.tf line 47:
     47: resource "terraform_data" "keep_data" {

   Resource instance terraform_data.keep_data has prevent_destroy set, but the
   plan calls for it to be destroyed.

   To proceed, either disable prevent_destroy for this resource or exclude
   instances of this resource from this round using:
       -exclude="terraform_data.keep_data"
   ```

   `tofu plan` exits 1, so the agent's plan step fails and the run ends *failed* at the plan: nothing is
   approved and Proxmox's protection is not touched. The run's detail does not repeat OpenTofu's advice, because
   `-exclude` really does let the replacement through. It says the plan would destroy the guard, that nothing changed, and
   that the way to do this on purpose is to remove the guard in a commit of its own ([how](#retiring-a-stateful-guest)).
2. **A change in place passes.** Cores 2 to 4 and memory 1024 to 1536 plan as `Plan: 0 to add, 1 to change, 0 to
   destroy.`, the container only, and apply (Proxmox received the new `cores`); PostgreSQL did not restart.
3. **A version bump that installs again passes.** A new `retention` (2y to 3y) plans as the install replaced and nothing else,
   applies for real, and VictoriaMetrics runs with `-retentionPeriod=3y`, the rows kept. A new VictoriaMetrics version and
   checksum plan the same way. The guard does not appear in either plan: it follows the incarnation, not the install.
4. **A failed first install can be retried.** After an install that fails, the container exists, the install is tainted,
   and **the guard does not exist**. The next plan, with nothing else changed, replaces the tainted install
   (`module.app.terraform_data.install is tainted, so it must be replaced`), leaves the container alone, creates the
   guard (`Plan: 2 to add, 0 to change, 1 to destroy.`), and the apply succeeds.
5. **A tainted container that never finished can still recover.** With the guard absent, `tofu taint` on the container plans
   its replacement (`replace_because_tainted`, which is what [Octopushy's rule](lab-repository.md#rules) looks for) and the guard's
   creation, with no error. The stand-in refuses to delete it while protected, as Proxmox does (*can't remove CT 102 - protection
   mode enabled*); once protection is lifted, as Octopushy's unprotect step does, the apply replaces the container, and from then
   on the guard stops a replacement as in 1.

### Retiring a stateful guest

A retire run is a `tofu plan -destroy` of the guest's root. Which root depends on how it was asked for. Deleting the
guest's folder builds it from **the revision the guest was last applied from**, which has the guard; `lifecycle: retired` builds
it from the current revision. Tested with real OpenTofu:

- **With the guard in the root, the destroy plan fails**, with the error shown above, at the plan step. A retire run built from the
  last applied revision therefore *fails* and changes nothing, and Proxmox's protection is untouched.
- **Removed on its own, applied first, the way works.** Commit a change that removes the `terraform_data.keep_data` resource and
  nothing else. Its plan destroys exactly the guard (`terraform_data.keep_data will be destroyed`, `(because
  terraform_data.keep_data is not in configuration)`, `Plan: 0 to add, 0 to change, 1 to destroy.`), which `policy.destructive:
  manual` holds for approval, as a delete. Approve it. The container is not touched. **Then** delete the folder or set `lifecycle:
  retired`: the retire run is built from the revision that was just applied, plans the destruction of the rest (`Plan: 0 to
  add, 0 to change, 6 to destroy.`), and goes the usual way: approval, unprotect, apply, verify absent. The stand-in refused the
  destroy while the container was protected, and allowed it after protection was lifted.
- **In one commit.** `lifecycle: retired` in the same commit that removes the guard also makes a destroy plan (`Plan: 0 to add, 0 to
  change, 7 to destroy.`, the guard included), because that run is built from the current revision, which has no guard. A
  *deleted folder* cannot do this: its run is built from the revision before, which has the guard. Two commits are the safe way, and
  the only way for a deleted folder. If the folder was deleted first, its removal run fails at the plan and has changed
  nothing, so restore the folder (that cancels a removal that has not started changing anything), commit the guard's removal
  and apply it, then delete the folder again.

The guard belongs to the recipe's root, not to one guest. Removing it unguards every guest of that recipe until it is back and
applied to each.

**A container deleted outside Octopushy** is stopped by the guard too: the plan wants to create it again, which replaces the
guard, so it fails the same way although the data is already gone. Remove the guard in its own commit, apply, which creates the
container and installs, restore the data from a [backup](#backups), then put the guard back.

**A lost install or sealing key wedges the guest.** The keys are in the guest's state and reach the container only when it is
created. If the state is lost or a key is replaced, every later install fails, refused on SSH or with *the seal does not verify*.
Do not replace the container for this. As root on the host, use `pct exec` to put the new key from the plan, with its
`octopushy-bootstrap=` comment, into `/root/.ssh/authorized_keys`, then plan again.

**The guard watches the container, not what an install does to its data.** Lowering `victoriametrics.retention` is an ordinary
in-place install, and VictoriaMetrics then discards what is older. Treat such a change as you would a delete.

To replace the container on purpose (a template, or a layout change) is the same: remove the guard first, and expect the data to
go with the old container, so restore it from a [backup](#backups). Put the guard back in a commit after the replacement has
applied; it is created once the install has finished.

### How this relates to `policy.destructive`

With the guard in place, **no approval can pass** a plan that replaces or destroys the container: the plan
fails, and the run ends *failed* at its plan step. Once the guard is removed and that has been applied, the
[rule of item 11](lab-repository.md#rules) applies to the container as it does to a VM: a provision plan
that replaces or deletes the guest's own container waits for approval unless `destructive: automatic` is
written down, a tainted container that was never verified recovers on its own, and a failed apply puts
protection back. A container replace also wipes its disks, so it never counts as a plain deployment.
`policy.destructive: manual` on `ha-db` also holds the removal of the guard itself, which is a delete.

### Backups

**Dumps inside the container die with it, so they are not a backup.** `ha-db`'s seven nightly dumps in
`/var/backups/postgresql` cover a mistake, such as a dropped table. A disk failure or a destroyed container
takes them. What is a backup is a Proxmox backup job (vzdump) of container 102 to storage other than `vm-ssd`:
the root volume is always part of one, and a mount point only if it is a volume with `backup=1`
(`mountpoint_backup_enabled` in pve-container; the module defaults `backup` to true, and a bind mount is never included).
For a recipe with a mount point, check that it is. VictoriaMetrics has no backup of its own; a vzdump of the container is what holds
its data. This repository does not create the job.

## Worked recipe: `ha-db`

`ha-db` is container 102 on `ziost`, with PostgreSQL 17 from Debian for Home Assistant's recorder, and
VictoriaMetrics for its sensors. Its content is from homelab-config's `ops/ha-db`, reworked to run again
without failing, losing data, or drifting.

| Part | Value |
| --- | --- |
| Guest | [`examples/lab/labs/home/proxmox/ziost/guests/ha-db/guest.yaml`](../examples/lab/labs/home/proxmox/ziost/guests/ha-db/guest.yaml), under [`ziost/installation.yaml`](../examples/lab/labs/home/proxmox/ziost/installation.yaml) |
| Recipe | [`recipes/ha-db`](../examples/lab/recipes/ha-db/recipe.yaml), root [`modules/opentofu/ha-db`](../examples/lab/modules/opentofu/ha-db) |
| PostgreSQL | 17 from Debian. Role and database `homeassistant`. `pg_hba.conf` is written whole: `postgres` by socket, the role by socket with its password, and `host` lines only for `inputs.postgresql.clients` (10.15.40.10). Everything else is refused. The settings are `postgresql-ha.conf` from the original. |
| VictoriaMetrics | 1.155.0, the SHA-256 pinned (`7180e659...b176`, as in the release's own checksums file). Retention `2y`. User `homeassistant` and the password, in a 0600 environment file. A relabel rule keeps only `ha_value`. `/health` is open, as VictoriaMetrics leaves it; everything that reads or writes samples asks for the password. |
| Dumps | `/usr/local/sbin/ha-db-dump` at 05:15 by cron, one file for each weekday so seven are kept, written aside and moved over the old one only when whole. |
| Checks | The install ends by checking PostgreSQL answers, the role logs in with its password and not without, `/health` answers, and queries and writes get 401 without the password and with a wrong one. A failed check fails the apply. |

`inputs.credentialsRevision` stands for the passwords, which the plan cannot see. **Changing a password in
the store starts no run.** To roll them: set the new values in the store (a new version of each entry), raise
`credentialsRevision` and, if you pin versions, `version:`, and commit. The plan shows the install being
replaced; the apply gives PostgreSQL the new password (without a restart) and rewrites VictoriaMetrics's file
and restarts it. The data stays. Update Home Assistant's `secrets.yaml` and restart it.

### The first deployment, in order

1. **Make the passwords yourself**, for example `openssl rand -base64 24 | tr -d '\n'`, 16 to 128
   characters from letters, digits and `!#%&()*+,./:;<=>?@^_{|}~-`. Keep a copy for Home Assistant's
   `secrets.yaml` (`recorder_db_url`, and the InfluxDB integration's password), in your password manager:
   Octopushy will not show them again.
2. **Variables:** add `ha-db/postgres-password` and `ha-db/victoriametrics-password` as secrets, and tick
   **Protected** on both.
3. **Proxmox:** add `proxmox/ziost/read-only-token` and `proxmox/ziost/privileged-token`, with the
   [permissions below](#proxmox-token-permissions); **Trust installation** `home/ziost`.
4. **The route:** the agent needs a route to 10.15.30.40 on port 22, and the container needs outbound access
   (Debian's mirrors, `github.com` for VictoriaMetrics, `download.proxmox.com` for the template on the host). Home
   Assistant, 10.15.40.10, needs a route to 5432 and 8428 of 10.15.30.40: a firewall rule, outside this repository.
5. **Plan the guest.** It waits, with *Waiting for trust*. On its page, read the digest and the login keys,
   review the recipe, the root and its `files/`, `lxc-app` and `proxmox-lxc`, and choose **Trust guest**.
6. **Approve** the plan if the policy asks for it, and let it apply and verify. `ha-db` then holds the guard.
7. **Connect Home Assistant** as in the original's README: add the InfluxDB integration first (version 1,
   `http://10.15.30.40:8428`, user `homeassistant`, database `ha`), then the recorder's `db_url` from `secrets.yaml`
   and the `influxdb:` block.
8. **Add a Proxmox backup job** for container 102 to storage other than `vm-ssd`.

### Startup order and memory

`inputs.startup: { order: 1, upDelay: 30 }` reaches Proxmox as `startup: order=1,up=30`: the stand-in for the API
in `tests/ha-db` receives exactly that in the create request. VM 100, `ha`, must start after the databases, and its own
`startup order=2` is a setting on the host that **is outside this repository**; set it by hand (`qm set 100
--startup order=2`) or `ha` starts with no order.

`ziost` has 7.6 GiB of RAM. The original's table: the host about 1.6 GiB, `ha` 3.0, `octopushy` 3.0 (0.3 used),
`ha-db` 1.0 (0.6 expected). Container limits are ceilings, so the sum can pass the host's RAM, but keep at least
1 GiB available when adding anything.

## Proxmox token permissions

What a token needs to manage container 102 and its template, with VM 100 (`ha`) untouched. **Everything
here is read from the source of Proxmox VE at git.proxmox.com and is unproven on a live host**: nothing was run against Proxmox,
and the end-to-end proof, a real create, update, stop and destroy with this token, is still to do. The source read:

| Repository | Commit |
| --- | --- |
| pve-container | `761aea86f1ef3b4213590fbcfdf1c0c4b57fa2c4` (6.1.16) |
| pve-manager | `c79d601b09c00f1b7a7d0ef7ec0fc9326038298e` |
| pve-access-control | `dff26b458651b4e1141ff0a41a296d2904b16228` (9.1.2) |
| pve-firewall | `09225d8fc90c3b24b248e29b832e7ff08c42eb88` |
| pve-storage | `4250dfd533c7d98c632cd4f033610b5cb1719800` |
| pve-guest-common | `38f27bab64a27533fb87ef3d7f8d2f81a22d0dab` |
| pve-common | `b9b41d526f1ee1c0424765e72af6181fff69f611` |

That is each repository's head at the time, not the versions on `ziost`: **check what `ziost` runs**.

### What it needs, and where

An API token has its own ACL, and with privilege separation (`--privsep 1`) its rights are the intersection of its
and its user's, on each path, so both need the entries below. Granting on `/vms/102` leaves VM 100 with no
privileges, as long as the user has none on `/`, `/vms` or `/vms/100`.

| Privilege | Path | For |
| --- | --- | --- |
| `VM.Allocate` | `/vms/102` | Create and destroy the container (`create_vm`, `destroy_vm`). Not `/vms`, and no pool is needed. It is also what setting `nesting` asks. |
| `VM.Config.CPU` | `/vms/102` | `cores`. |
| `VM.Config.Memory` | `/vms/102` | `memory`, `swap`. |
| `VM.Config.Disk` | `/vms/102` | `rootfs` and `mpN`, and resizing the root disk. |
| `VM.Config.Network` | `/vms/102` | `net0`, and `hostname` and `nameserver`, which are network options here. **Also writes the container's firewall options and rules** (there is no `Firewall.*` privilege). |
| `VM.Config.Options` | `/vms/102` | `startup`, `onboot`, `protection`, `tags`, `description`, `ostype`. **So the same token can lift `protection`**, which Octopushy's unprotect step does. |
| `VM.PowerMgmt` | `/vms/102` | Start, stop, shutdown, reboot. |
| `VM.Audit` | `/vms/102` | Read the config, `status/current` and the interfaces; reading the firewall. |
| `Datastore.AllocateSpace`, `Datastore.Audit` | `/storage/vm-ssd` | A new root volume or mount point. |
| `Datastore.AllocateTemplate`, `Datastore.Audit` | `/storage/local` (the template storage) | `download-url` of the template, and using it to create a container. |
| `Datastore.Allocate` | `/storage/local` | **Only to delete the template**, which `proxmox_download_file` does when the guest is destroyed or the template changes. See below. |
| `Sys.AccessNetwork` | `/nodes/ziost` | `download-url` and `query-url-metadata`. `Sys.Modify` is not needed. |
| `SDN.Use` | `/sdn/zones/localnetwork/apps` | The bridge `apps`. A plain Linux bridge needs it too, since pve-container 5.0.1. If `apps` is an SDN vnet, use its zone instead of `localnetwork`, and propagate if the guest has a VLAN tag. |

Not needed, from the source: `VM.Config.HWType`, `.Cloudinit` and `.CDROM` (no use for LXC), `Sys.Modify` for
`startup` (that rule is QEMU's, for a VM) or for free-form `tags` (only for a registered tag, or if the
datacenter restricts them), and `Pool.Audit`, `Sys.Audit` or `VM.Audit` on `/` for the provider's flow, by
inference from the stand-in the tests use, which answers no such request.

### Commands

Text only: nothing was run. Role names cannot start with `PVE`.

```text
pveum role add OctoLxcGuest --privs "VM.Audit,VM.Allocate,VM.PowerMgmt,VM.Config.CPU,VM.Config.Memory,VM.Config.Disk,VM.Config.Network,VM.Config.Options"
pveum role add OctoLxcDisk --privs "Datastore.AllocateSpace,Datastore.Audit"
pveum role add OctoLxcTemplate --privs "Datastore.AllocateTemplate,Datastore.Audit,Datastore.Allocate"
pveum role add OctoLxcNetFetch --privs "Sys.AccessNetwork"
pveum role add OctoLxcBridge --privs "SDN.Use"

pveum user add octopushy-lxc@pve --comment "OpenTofu, container 102"
pveum user token add octopushy-lxc@pve tofu --privsep 1

pveum acl modify /vms/102 --roles OctoLxcGuest --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /storage/vm-ssd --roles OctoLxcDisk --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /storage/local --roles OctoLxcTemplate --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /nodes/ziost --roles OctoLxcNetFetch --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 0
pveum acl modify /sdn/zones/localnetwork/apps --roles OctoLxcBridge --users octopushy-lxc@pve --tokens 'octopushy-lxc@pve!tofu' --propagate 1
```

`pveum acl modify` needs the user and the token both, as above. To check that VM 100 is untouched:

```text
pveum user token permissions octopushy-lxc@pve tofu --path /vms/100
pveum user token permissions octopushy-lxc@pve tofu --path /vms/102
```

The first should show no privileges. `--propagate 0` on the guest and the storages keeps the grants from reaching any other
guest or volume. Octopushy's own split tokens (`readOnlyToken`, `privilegedToken`, see [Proxmox credentials](proxmox-credentials.md))
are separate: the read-only one needs `VM.Audit`, `Datastore.Audit` and `Sys.Audit` on `/`. Its `/` ACL shows VM 100 to a
read-only token, which is the point of that token, and it cannot fetch a template's metadata.

### Surprises in the source

- **Destroying the container deletes the ACL on `/vms/102`**, and with it every user and token entry on that path
  (`remove_vm_access`). The next create fails with 403 until an administrator grants it again, and the token cannot grant it
  to itself. **After any destroy or replace of the container, run the `/vms/102` command again.** The alternatives are both
  weaker: a pool survives, but the create's tag check does not see pool membership; and a grant on `/vms` with `NoAccess` on `/vms/100`
  covers every future guest.
- **`Datastore.Allocate` bypasses the ownership check on a volume.** Without it, using a volume that VM 100 owns needs
  `VM.Config.Disk` on `/vms/100`, which the token does not have. With it, the token could delete that volume, or mount it into
  container 102. So it belongs on a storage VM 100 does not use, which `local` is only if VM 100 has no disks or backups there.
  Drop it if it does, and the token then cannot delete the template, so `proxmox_download_file` fails on destroy with a 403.
- **`nesting` does not need `root@pam`; every other feature does.** `features: nesting=1` needs `VM.Allocate` on the guest.
  Any other key, `keyctl=0` included, is refused to anything but `root@pam`, **and no API token can be `root@pam`**: the token's
  user is `root@pam!name`, which the check does not accept. The module sets `keyctl = false`, but the create request the provider sends
  carries exactly `"features": "nesting=1"` (checked against the stand-in with the real provider), so creating is expected to work.
  **What the provider sends when it updates `features` is unproven**; an update that carries more than `nesting` would fail with a 403
  that no token can fix.
- **`protection=1` blocks more than deletion**, for everyone: removing or changing a mount point, `mp0` or `rootfs`, through the
  config. Deleting a protected container is a plain error (HTTP 500, *can't remove CT 102 - protection mode enabled*), not a 403.
- A version-dependent detail: `start=1` at create is checked up front only since pve-container 6.1.12, and `Sys.AccessNetwork` for
  `download-url` since libpve-access-control 8.1.2, libpve-storage-perl 8.1.0 and pve-manager 8.1.5.

### Not proven without a live host

Which package versions `ziost` runs; whether `apps` is a bridge or a vnet, and its zone and VLAN tag; the datacenter's
`registered-tags` and `user-tag-access`; what VM 100 keeps on `vm-ssd` and `local`, and that `local` is file-based with `vztmpl` content and
`vm-ssd` allows `rootdir`; the user's existing ACLs on `/` and `/vms`; whether the provider's updates resend `mp0`, `rootfs` or extra feature
keys, which `protection=1` or the `root@pam` rule would refuse; whether it sends tags at create or in a later `PUT`; and, above all, that this
set is sufficient from a real create to a real destroy.

### Reaching and being reached

- **The agent needs a route to 10.15.30.40, port 22**: OpenTofu installs over SSH.
- **The container needs outbound access** while it installs and afterwards (`unattended-upgrades`): Debian's mirrors,
  `github.com` and its release downloads for VictoriaMetrics.
- `ziost` has 7.6 GiB of RAM; see [the memory table](#startup-order-and-memory).
- VM 100's own `startup order=2` is a setting on the host, **outside this repository**.

## Running the tests

| Command | What it proves |
| --- | --- |
| `python3 -m unittest discover -s tests/lxc-app` | The seal's bash half against its HCL half in Python, the environment file, the runner. Offline. |
| `just technitium-lxc` | The `technitium-lxc` root, now on `lxc-app`, against disposable containers. Needs outbound access. |
| `just ha-db` | The `ha-db` root: real PostgreSQL and VictoriaMetrics in a systemd container, the first run, a second that converges, a password roll, the guard. Needs outbound access: Debian's mirrors, `github.com`, and the OpenTofu registry. |

Both are against a stand-in for Proxmox's API, so they prove nothing about Proxmox itself, its firewall, or an unprivileged
LXC container.
