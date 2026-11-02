# Trusted Proxmox installations

Octopushy sends Proxmox credentials only to API endpoints an operator trusted, and
[a cluster's admin password](#application-clusters) only to the members trusted for it.
Trust belongs to a Proxmox installation, not to a commit: once an installation
is trusted, every later revision of the repository runs without another click,
unless it points that installation at a new endpoint. Trust is separate from
**apply approval**, which each guest's policy still decides.

## Operator workflow

1. Configure and sync the lab repository. Revisions are loaded, validated and
   turned into runs as soon as they are fetched.
2. A new installation shows **Needs trust** in the inventory. Its runs and
   observations wait with the reason *Waiting for trust* and receive no
   credentials.
3. On the installation's page, check the listed endpoints and choose **Trust
   installation**. Octopushy records the endpoints, the operator and the time,
   and the waiting work continues.
4. If a later commit changes the installation's endpoint, or gives a host its own
   endpoint, work for that installation waits again until you trust the new
   endpoints. Other installations are unaffected.

## What is bound

Trust is keyed by the installation's address (`lab/installation`) and holds every
endpoint it declared when trusted: the connection's endpoint and any host's own.
Trusting again replaces that list with the endpoints declared now. The request
carries the endpoints the operator saw, and the server refuses it when the
current definition declares others.

The controller checks trust before releasing credentials and building assignments,
including after retries, requeues and restarts. Work built from a previous
repository source or an invalid revision still waits and must be planned again;
changing the repository URL or branch does not require trusting installations
again. Trust does not override missing or revoked credential versions, capacity
limits, apply approval or an uncertain outcome. Already delivered credentials and
running mutations cannot be recalled.

## API

- `GET /api/inventory` and `GET /api/installations/{id}`: each installation has
  `trust` with its declared `endpoints`, the `untrusted` ones, `trustedAt` and
  `trustedBy`.
- `POST /api/installations/{id}/trust` with `{ "endpoints": [...] }`: trusts the
  declared endpoints when they match the request. The actor comes from the session.

## Application clusters

A [cluster](technitium-lxc.md#a-cluster)'s admin password is a credential too, and the lab repository
decides where it would go: a member's address comes from its `guest.yaml`. So it gets the same
guarantee, and more, because code the repository controls holds it. Trust is keyed by the cluster's
address (`lab/clusters/id`), and covers:

- **Endpoints:** every member's `https://<address>:53443`.
- **Certificates:** each member's, by fingerprint, as the SHA-256 of the DER of the PEM stored with it,
  which is checked when it is read and again when it is sent to the provider and the agent. The agent talks to that exact certificate and
  nothing else, and the provider takes it as its only root, and there is no setting that turns this
  off, because a commit could use it. A member presenting another one is sent nothing.
- **Code and how it runs:** a digest of the cluster's recipe and root, of each member's, with what
  they use, and of how each member runs: its state backend, which variable gets the password, its
  agents, its template, inputs and network, the guest as it is created (VMID, host, hardware and the
  volumes it mounts, and the SSH keys that can log in to it), the host's datastores and bridges, and
  the Proxmox connection with the store entries it uses as token and as CA. Only a guest's
  `description`, `policy`, `execution`, `setup` and `renamedFrom` are left out. A cluster's plan holds the
  password before anyone approves it, and a member's apply can run with no person approving it, so
  a commit must not be able to run code of its own in either place, or send unchanged code somewhere
  new. The cluster's page shows this for each member before you trust it.

A cluster's root may hold only its own `.tf` files, and a root that receives the password has to pin
its providers with a lock file and call only modules its recipe lists. This holds for a cluster's
recipe and for every recipe that declares `secrets`, such as `technitium-lxc`. The files are read as
HCL, not by matching lines, and a file that cannot be read with certainty is refused: that includes
HCL's escapes `$${` and `%%{`, comments or heredocs inside an interpolation, and a heredoc that
interpolates, because a reader that guessed wrong about one would take code for text.

A hand-written reader is not what the password depends on. A run that holds a protected secret gets
the folders its digest covers, with the module folders its recipe lists, and the agent then keeps
OpenTofu to them: it needs a `.terraform.lock.hcl` and no `.terraform`, runs `tofu init` with
`-lockfile=readonly` so a provider cannot be unpinned, and with `-get=false` when the root calls no module
so that nothing is fetched. With listed modules it reads `.terraform/modules/modules.json` after the
init and before the plan, and fails if a module is not local or resolves outside the listed folders.
A listed module is installed by `init`, which runs with the run's environment, the password included;
a remote one in a file the reader missed is therefore fetched before the check fails, and never
planned. A fetch is not running the module's code, but what a hostile source can do during one was
not tested here. Runs that hold no protected secret are not changed.

1. A new cluster shows **Needs trust**. Its runs and checks wait, and receive no password.
2. On the cluster's page, review the code and choose **Trust cluster**. This also records the grants
   below, so the guests can install before there are certificates to read.
3. Trust again once certificates can be read; [the first deployment](technitium-lxc.md#the-order-of-a-first-deployment)
   gives the order.
4. If a commit changes a member's address, adds a member, or changes any of that code, or a member
   presents another certificate, work waits until you trust what is declared now. Removing a
   member does not ask for this. Other clusters, and every installation, are unaffected.

Reading certificates needs no trust: that connection carries no credentials.

- `GET /api/inventory` and `GET /api/clusters/{id}`: each cluster has `trust` with its `endpoints`,
  the `untrusted` ones, `codeDigest`, `codeChanged`, `certificates`, `passwordProtected`, `trustedAt`
  and `trustedBy`; each member has its `codeDigest`.
- `POST /api/clusters/{id}/certificates`: reads what the members present now.
- `POST /api/clusters/{id}/trust` with `{ "endpoints": [...], "codeDigest": "...", "certificates":
  { "<endpoint>": "<sha256>" } }`: trusts what is declared when it matches the request, and leaves
  any certificate it does not name as it was. The actor comes from the session.

## Protected secrets

Everything above checks what a commit declares, and a commit can change what it declares: it could
delete the cluster and bind the password somewhere else. So protection is not in the repository.

An operator can mark a store entry **Protected**, when creating it or later, under **Variables** or
with `POST /api/variables/protection`. The flag is in the database. No definition can set it, and
changing or deleting a definition does not remove it. A protected entry is released only for a
**grant**: this resource, running code with this digest, which also covers how a guest runs. The
check is where the store releases keys, so it holds for every way a key can be bound, on every
attempt, including retries. A run waiting for one shows *Waiting for trust*, whatever its `policy`.
A state backend never receives a protected entry, even with a grant: its environment reaches
OpenTofu during init, before there is a plan, so give a backend an entry of its own.

**Trust cluster** records grants for the cluster and for each member using its password. A guest
that belongs to no cluster has **Trust guest** on its own page. It waits with *Waiting for trust*, and
the page shows why, with what Trust cluster shows for a member: the protected entries it binds, the
digest of its code and of how it runs (its state backend, variables, agents, Proxmox connection and the
tokens it uses, template, inputs, network, VMID, host, hardware, volumes), and every key that can log in
to it as root, shown by its end. Trust guest records a grant for each protected key the guest's run
binds, the Proxmox token among them, for that digest. A change to the recipe, the root, a module it
lists, or any of that blocks the guest again until the next trust, whatever the repository's `policy`.
Other resources use `POST /api/variables/grants` with `{ "key": "...", "resourceId": "...", "codeDigest":
"..." }`; a null digest is for work that runs no repository code, such as observing an installation.
A run holding a protected entry receives only the folders its digest covers, not the repository.

- `GET /api/guests/{id}`: `trust` has the `keys` the guest binds that are protected, its `codeDigest`
  and `execution`, whether it is `trusted`, the `cluster` it belongs to, if any, a `refusal` when no
  grant can help, and `trustedAt` and `trustedBy`.
- `POST /api/guests/{id}/trust` with `{ "codeDigest": "..." }`: records the grants when the digest is the one
  declared now. Refused (409) for a cluster member, whose trust is its cluster's; for a guest with no
  protected key; for a protected key given to the state backend, which is never released; and when the
  digest the operator saw is not the current one. The actor comes from the session.

An entry that is not protected behaves as it always did.

## Upgrading from revision trust

Earlier versions required trusting each revision. The migration keeps an
installation trusted when its current definition came from a trusted revision,
with that revision's operator and time, and lets runs that were waiting for a
revision's trust be checked again. Any other installation waits for trust once.

## Limits

A trust decision is operator authorization, **not a sandbox or a code review**.
Every commit's tool-native code, such as OpenTofu modules, runs with the
installation's credentials without being reviewed in Octopushy; review changes
before merging them into the lab repository's branch. Deleting a guest's folder
proposes its removal immediately; set `destructive: manual` to approve each
removal. Signed commits, purpose and destination restrictions and least-privilege
Proxmox credentials remain separate work.

See the [decision](adr/0002-installation-trust.md).
