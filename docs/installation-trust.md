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
guarantee. Trust is keyed by the cluster's address (`lab/clusters/id`) and holds the endpoint of
every member, `https://<address>:53443`, when it was trusted.

A cluster also trusts its recipe's code. An installation does not need this, but a cluster's plan
holds the admin password in its environment, because the provider logs in while planning, so a
commit must not be able to run code of its own there. Trust records a digest of `recipe.yaml`, which
names the image, and of the root's folder, which holds the lock file that pins the provider. A
cluster's root may hold only its own `.tf` files.

1. A new cluster shows **Needs trust**. Its runs and checks wait, and receive no password.
2. On the cluster's page, review the endpoints and the code, then choose **Trust cluster**.
3. If a commit changes a member's address, adds a member, or changes the recipe, work waits again
   until you trust what is declared now. Removing a member does not ask for this. Other clusters,
   and every installation, are unaffected.

The controller checks this before it releases the password or builds an assignment, as above. The
password can also not reach an unreviewed plan another way: a backend environment value cannot use
it, a Proxmox token cannot be it, and a guest's `provision.secrets` reach only an apply.

- `GET /api/inventory` and `GET /api/clusters/{id}`: each cluster has `trust` with its declared
  `endpoints`, the `untrusted` ones, `codeDigest`, `codeChanged`, `trustedAt` and `trustedBy`.
- `POST /api/clusters/{id}/trust` with `{ "endpoints": [...], "codeDigest": "..." }`: trusts what is
  declared when it matches the request. The actor comes from the session.

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
