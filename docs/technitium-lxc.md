# Technitium DNS in a Proxmox container

The `technitium-lxc` recipe runs [Technitium DNS Server](https://technitium.com/dns/)
natively, without Docker, in an unprivileged Debian 13 LXC container. Each server is one
guest, on the Proxmox host it should keep working through. This page covers one server, and
then [a cluster of them](#a-cluster), which is what a lab should run.

## Declare a server

```yaml
# labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml
schemaVersion: 1
type: container
host: korriban
vmid: 320
template: debian-13-lxc
hardware:
  cores: 2
  memory: 2048
  swap: 512
  disks:
    - { size: 8 }
  mountPoints:
    - { path: /etc/dns, size: 4 }
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.32/24
  gateway: 10.1.0.1
  dns: [10.1.0.1]
provision:
  recipe: technitium-lxc
  inputs:
    startup: { order: 1 }
    # firewall: { enabled: true, adminSources: [10.1.0.0/24] }
  secrets:
    admin_password: { store: dns/technitium/admin-password }
```

- **The address must be static,** and `network.dns` must name other resolvers. The container
  keeps resolving through them, never through itself, so it can boot and install with its
  own DNS server down.
- **Set the password first:** add `dns/technitium/admin-password` as a secret under
  **Variables**, at least 12 characters on one line, and mark it **Protected** if the server
  will join a [cluster](#a-cluster). A run is blocked until it exists.
- **Agents need a route to the container's port 22.** OpenTofu installs over SSH.
- **The install needs outbound access:** see [what it downloads](#what-it-downloads).

Sign in at `https://<address>:53443` as `admin`. The certificate is self-signed, made by the
install for this address.

## What gets installed

Proxmox has no user-data or exec for containers, so the root generates an SSH key pair and
creates the container with its public key. OpenTofu then connects as root and runs
[`install.sh`](../examples/lab/modules/opentofu/technitium-lxc/install.sh), which:

- installs the pinned ASP.NET Core runtime and Technitium, each checked against its digest,
  under `/opt/technitium`, next to any older version until the new one passes its checks;
- writes `dns.service`, upstream's unit with the runtime's path, enabled at boot. It depends
  on neither Octopushy nor name resolution;
- keeps Technitium's plain HTTP port 5380 on loopback with an nftables rule of its own, and
  serves the API over HTTPS on 53443, with a certificate of its own that names the address, since
  Technitium's has only a common name, which nothing can verify;
- makes `dns.service` require that rule: **if the rule cannot be loaded, DNS does not start.** That
  is deliberate. A member that is down is an outage you will see, and an admin API in plain HTTP on
  the network is one you will not;
- checks the running version, that the password works and `admin`/`admin` does not, that
  ports 53 and 53443 answer, and that `/etc/resolv.conf` still names `network.dns`. If any
  check fails, the apply fails.

It does not use upstream's `install.sh`, which always fetches the latest release and points
`/etc/resolv.conf` at the server itself. It runs as the `dns-server` user, as upstream does.

### What it downloads

| From | What | Needs reaching |
| --- | --- | --- |
| The Proxmox host | The container template | `download.proxmox.com`, port 80 |
| The agent | OpenTofu's image, if the agent's runtime pulls it | `ghcr.io` |
| The agent | Providers | `registry.opentofu.org`, and `github.com` with its release downloads, where the registry points |
| The container | Packages | The mirrors in the template's sources, `deb.debian.org` by default, port 80 |
| The container | ASP.NET Core | `builds.dotnet.microsoft.com` |
| The container | Technitium | `download.technitium.com` |
| Each member, in a cluster | Block lists | Whatever `settings.blocking.lists` names |

The container keeps needing its mirrors, because it updates itself with `unattended-upgrades`.
`just technitium-lxc` installs for real, so it needs this too, and says so if it cannot.

## Versions

The recipe's defaults are Technitium 15.6.2 and ASP.NET Core 10.0.14. To pin others, set all
four values in `guest.yaml`:

```yaml
provision:
  inputs:
    technitium:
      version: 15.6.2             # at least 15.6.2, which fixes cluster configuration sync
      sha256: 51f2d4ebaf156a85035e820afde5e1d428f1920a37d7f0c65203fb1766917e08
      aspnetcoreVersion: 10.0.14
      aspnetcoreSha512: dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031
```

`sha256` is the digest of
`https://download.technitium.com/dns/archive/<version>/DnsServerPortable.tar.gz`. Commit a
change and the next plan shows `terraform_data.install` being replaced: that resource is the
install, and its apply stops the service for the switch. It follows the guest's approval
setting, and `/etc/dns` stays as it is. The container's Debian packages update themselves
through `unattended-upgrades`.

## The admin password

Technitium takes the password on its first start only, when `/etc/dns/auth.config` does not
exist. If it cannot read it, it silently uses `admin`/`admin`, which is why the install logs
in to check.

The value reaches the apply through `TF_VAR_admin_password`. The plan never receives it, and the
variable is ephemeral, so it is in neither the saved plan nor the state.

OpenTofu installs over SSH, and it **cannot authenticate who answers**: it checks a host key only
against a public key, and nothing can give it this container's before it connects. So the password
does not cross SSH in a form anyone else can read. The root generates a sealing key of its own and
sends it to the container through the Proxmox API, as the comment of an SSH key, which Proxmox
writes into `authorized_keys`. The apply seals the password to that key, and `install.sh` opens it
in the container. Someone impersonating the container on SSH receives ciphertext, the install
script, and no key.

**This is a hand-built seal, not a vetted construction.** OpenTofu has no encryption function, so
it is made from what OpenTofu has, and it has had no review beyond this project's. It is small
enough to read in [`main.tf`](../examples/lab/modules/opentofu/technitium-lxc/main.tf):

- Two keys are hashed from the sealing key, one to encrypt and one to authenticate.
- Each upload gets a random nonce from `uuid()`, made in that upload, so a retry has a new one.
- The cipher adds, mod 64, the password's base64 to a keystream of SHA-256 blocks over the key, the
  nonce, and a counter.
- The tag is a double SHA-256 over the authentication key, the guest's id and address, the nonce,
  and the cipher. It is not HMAC, which OpenTofu cannot compute.
- `install.sh` checks the tag first and writes nothing unless it matches. It then records the nonce
  in `/etc/dns/octopushy-seal-nonces`, and refuses one it has seen.

What this does: keeps the password from whoever answers on SSH, and from whoever records that
session, as long as they do not have the sealing key. It refuses an upload that was changed, one
made for another guest, and one that comes round again.

What it does not do: it does not keep the password from anyone with the sealing key, which is in the
guest's OpenTofu state, in saved plans, in the Proxmox API call and in the container. It does not
say who is on the other end of SSH, and it is no defence against the Proxmox API itself: see
[Proxmox's certificate](#proxmoxs-certificate). A nonce is 122 random bits, so repeating one is
unlikely rather than impossible; the record in the container catches that only on that container.

An impersonator can still accept the session and report success, so **an apply that succeeds does
not prove the real container was installed.** What proves that for a cluster is its certificate,
below. In the container the password reaches curl as a file, not as an argument, and it is deleted
however the install ends. `auth.config` holds only a hash of it.

The store is not kept in sync afterwards. **Changing the value does not change the password.**
Change it in Technitium first, then in the store: a later install checks the value in the store
and fails if it does not work. In a cluster, [rotate it](#rotating-the-password) on the primary.

The generated SSH private key and the sealing key are in the guest's OpenTofu state, and therefore
in saved plans. Octopushy keeps both encrypted, but an external state backend keeps its own copy,
which is one more reason a protected password cannot depend on one. Whoever holds that state can
open a seal they captured, and can sign in to the container. The sealing key also stays in the
container's `authorized_keys`, for the next install.

## `/etc/dns`

`/etc/dns` holds the zones, the settings, and the server's identity, so the install refuses to
run unless it is a mount point of its own. A vzdump backup includes it unless the mount point
sets `backup: false`.

Proxmox deletes a volume together with the container that owns it, so a volume with a `size`
lasts as long as the container, and neither retiring the guest nor replacing the container
keeps it. To keep the data through those too, create the volume under another VMID that no
guest uses, and format it, on the Proxmox host. Then name it:

```yaml
hardware:
  mountPoints:
    - { path: /etc/dns, volume: "local-lvm:vm-9320-disk-0" }
```

Proxmox skips a volume another VMID owns when it deletes a container. The next install finds
`auth.config` and keeps it, so the password in the store must still be the one in that file.

## Firewall

`firewall.enabled` turns on the Proxmox firewall for the container, with incoming traffic
dropped except UDP and TCP 53 from `dnsSources`, and TCP 53443 and 22 from `adminSources`.
Both default to everyone. `adminSources` must include the agents, or the next install cannot
connect. Turning this on for an existing server restarts its container.

**These rules are inert unless the firewall is also enabled for the datacenter** (Datacenter,
Firewall, Options), which Octopushy does not do. Port 5380 does not rely on them: the container's
own rule keeps it closed, and DNS does not start without that rule, so a guard that fails is an
outage of that member, not an open port. Stopping or restarting `dns-http-guard.service` does the
same to DNS; to reload the rule, use `systemctl reload`, as the install does.

In a cluster, members talk to each other on 53443 and 53 in both directions, and Technitium's
cluster traffic does not work through a proxy. Limit the sources to your LAN if you like, but keep
in `adminSources` every other member and each agent, and in `dnsSources` every other member, each
agent, and your clients. Verification asks each member over DNS from the agent.

## Retiring

Retiring works as for a VM: an approved run lifts the protection, destroys the container, and
verifies that it is gone. It does not need the password.

## A cluster

```yaml
# labs/home/clusters/technitium/cluster.yaml
schemaVersion: 1
type: technitium
members:
  - { guest: korriban/krb-dns-01, role: primary }
  - { guest: moraband/mrb-dns-01, role: secondary }
settings:
  clusterDomain: cluster.home.arpa   # permanent once formed
  recursion: { policy: UseSpecifiedNetworkACL, networks: [10.1.0.0/24] }
  forwardZones:
    - { zone: home.arpa, forwarder: 10.1.0.1 }
  blocking:
    enabled: true
    type: NxDomain
    updateIntervalHours: 24
    lists: [https://example.invalid/list.txt]
    listProbes: [listed.example.com] # on that list, so that verification can ask for it
    blocked: [bad.example.com]
    allowed: [false-positive.example.com]
    # control: example.org           # a name every member must resolve
credentials: { store: dns/technitium/admin-password }
policy: { approval: manual }
```

Octopushy forms the cluster on the primary, joins each secondary, and owns these settings and
lists from then on. The [executor contract](../contracts/agent/v1/executors.md#technitium) has
the detail, and [the lab repository](lab-repository.md#application-clusters) the rules.

### Prerequisites

- **Each member is a guest of this recipe** on a static address, with `network.dns` naming other
  resolvers. Put members on different Proxmox hosts: members on one host are a warning, because
  that host is one failure domain for both.
- **Create the password as a protected entry, before the first commit.** Under **Variables**, add
  `dns/technitium/admin-password` and tick **Protected**. Only an operator can set that, and no commit
  can undo it: the entry then goes only to what you trust, however a definition asks for it.
- **Every member's `provision.secrets.admin_password` names the cluster's `credentials`.** Joining
  replaces a secondary's users with the primary's, and Octopushy signs in to every member with one
  password, so a guest that names another entry makes the cluster invalid.
- **At least Technitium 15.6.2**, which fixes cluster configuration sync.
- **The agent needs a route** to every member's ports 53443 and UDP 53. Port 5380 is not used.
- **With the Proxmox firewall on,** see [Firewall](#firewall): members need each other on 53443
  and 53, and the agent's address has to be in both source lists.

### The order of a first deployment

1. **Create the protected store entry,** as above.
2. **Commit the guests and the cluster together.** Trust each new Proxmox installation on its page.
3. **Trust the code.** The guests' runs are blocked with *Waiting for trust*, because they need the
   password and nothing has been granted it. Open the cluster's page, review the recipes and roots in
   the repository, and choose **Trust cluster**. The certificates cannot be read yet, and do not
   need to be: this lets the guests install. It also grants the password to the cluster and to each
   member, for exactly the code shown.
4. **The guests install.** The cluster's run stays blocked until every member is applied and verified,
   naming the ones it waits for. A member's failure shows there as that member's.
5. **Trust the certificates.** Once a member answers, an agent reads its certificate, within a minute
   or at once with **Read certificates**; this sends no password. Compare each fingerprint with the
   member's own, from the Proxmox host, where nobody can stand in the middle:

   ```sh
   pct exec 320 -- sh -c 'openssl s_client -connect 127.0.0.1:53443 </dev/null 2>/dev/null \
     | openssl x509 -outform der | sha256sum'
   ```

   Then choose **Trust cluster** again.
6. **The cluster's run goes.** **Inspect** asks every member what it reports. **Plan** shows forming the
   cluster and a `terraform_data.secondary` for each secondary. After approval, **apply** forms it
   and sets everything; **Join members** joins the secondaries; **Download block lists** forces the
   lists; **Verify every member** checks each one directly.

From then on a commit that changes the cluster's recipe, a member's recipe, or how a member runs
waits for **Trust cluster** again, and that guest's run is blocked, with the password withheld,
whatever its `policy` says. **How a member runs** is what the page lists under each member, and what
trust covers beyond code: its state backend, which variable gets the password, the agents it runs
on, its template, its inputs and its network, and the guest as it is created: its VMID and host, its
hardware including the volumes its mount points name, and **the SSH keys that can log in to it as
root** (`access.sshAuthorizedKeys`), which the page lists first. Also covered are the host's
datastores and bridges, and the Proxmox connection with the store entries it uses as the API token
and as the CA. The cluster's own agents count too. Each of these decides who ends up with the
password, without a line of code changing: a key added to a member lands at the next rebuild, on a
machine that is handed the password, and a token entry swapped for the protected one would send the
password to Proxmox. Only what cannot change the guest is left out: its `description`, `policy`,
`execution`, `setup` and `renamedFrom`.

### Cluster runs and member guests

A cluster run and its members' own runs do not overlap. A cluster run that has not started waits
for its members. Once started it holds them until it ends, including while its outcome is uncertain,
and a guest's run shows *Waiting for cluster run*. Approving a cluster's plan promptly therefore
matters more than it looks: its members cannot change until it is done.

Verification runs on every cluster run, even when the plan has no changes, and **Plan now** on the
cluster's page starts one at any time. A failed check is reported as *Verification failed*, naming
the member and the way out. It says nothing about the deployment of the others, and their guests
stay as they were.

Between runs, a member guest that applied a change gets its cluster checked by a new run within a
minute, and every formed cluster is checked in the background as often as installations are
observed, but not while a run of it is under way, and a check that finishes after a run of the
cluster has started or ended is dropped, so that it cannot replace what the run found. A failed
background check shows on the overview. It proposes nothing and changes nothing.

### Changing a blocklist

Edit `settings.blocking` and commit. Technitium does not enforce a new `lists` URL until it has
downloaded it, so a run downloads whatever it does not know to be downloaded, on the primary, which
covers every member. Octopushy remembers what was downloaded apart from OpenTofu, so if a download
fails, the next run tries again, even if its plan has no changes. Entries in `blocked` and `allowed`
reach secondaries through Technitium's own sync, usually within 20 seconds, and verification waits
for up to 90.

**A list needs `listProbes`:** names that are on it and in neither `blocked` nor `allowed`, one for
each list. A URL says nothing about what a server enforces, so verification asks every member for
these over DNS, and a cluster with lists and no probes is invalid. If a list drops a probe,
verification fails until you pick another. With nothing declared to block, verification asks
nothing and reports `blockingVerified: false` rather than claim anything.

Taking the last entry out of `blocked` or `allowed` deletes a resource in the plan, so it follows
`policy.destructive`.

### Ownership

Octopushy owns the cluster's settings, lists and forward zones. **A hand edit in Technitium's
interface is reverted by the next run,** on either node, since a secondary hands its changes to
the primary. A changed setting is a change in the next plan. An entry someone added to the blocked
or allowed list is one OpenTofu cannot see, so inspect finds it, and the plan replaces
`terraform_data.undeclared_entries`, which removes it. The approval names what goes, and because it
deletes something from a live server it follows `policy.destructive`. A first run creates that
resource with nothing to remove, and is not held for it: only a plan that deletes or replaces it, or an
inspection that named entries, counts. Only those names go: an entry
that appears after that inspection stays, verification reports it, and the next plan names it. To
keep an entry, declare it.

Zones and records you add yourself are left alone, and so is everything the cluster does not
declare. Per-node settings such as `logQueries` keep Technitium's defaults, because a change would
reach the primary only.

### When the primary is down

Secondaries keep answering and keep blocking, also through a restart, with what they last received.
What stops is change: a cluster run fails at **Inspect** saying so, and the guests are not touched.
Nothing is lost when the primary returns, and the next plan is clean. If it is gone for good, see
[promotion](#changing-the-primary).

### A changed certificate

The agent talks only to the exact certificate you trusted for a member. If a member presents another
one it receives nothing, not even a login: the connection ends during its handshake, before any
request, and work stops with *Waiting for trust*. A background check reads what members present
before it fetches the password, so it finds this with no password released at all; a run finds it in
inspection, where the agent holds the password and still sends nothing. Verification fails naming
the member, and the cluster's page shows what it presents.

Either something else is answering at that address, or the member was rebuilt and its `/etc/dns`
went with its certificate. Tell them apart from the Proxmox host, with the command in
[the first deployment](#the-order-of-a-first-deployment). If it is the member, choose **Trust cluster**.
Changing a member's address needs a new certificate too, since the old one names the old address:
that is a rebuild, not an edit.

### A rebuilt member

Replacing a container discards a mount point with a `size`, so a member can come back empty, with
a new certificate. It then serves DNS **with no blocking**, and OpenTofu reports "No changes",
because the primary still lists it. Verification is what catches this: the guest's run gets its
cluster checked, and the check fails.

First [trust its new certificate](#a-changed-certificate), or nothing can sign in to it. The next
verification then fails with the exact call to make. Removing a member is a destructive decision,
so Octopushy does not do this by itself. Sign in on the primary, then delete its stale record with
the id from the failure:

```sh
token=$(curl -sk https://10.1.0.32:53443/api/user/login \
  --data-urlencode user=admin --data-urlencode pass@admin-password | jq -r .token)
curl -sk -H "Authorization: Bearer $token" \
  "https://10.1.0.32:53443/api/admin/cluster/primary/deleteSecondary?secondaryNodeId=ID"
```

`removeSecondary` is no use here: it asks the member to leave, and a rebuilt member fails its TLS
check. Then choose **Plan now** on the cluster. Inspect finds the member out of the cluster, the
plan replaces its `terraform_data.secondary`, and after approval it joins again and receives the
configuration. To avoid all this, give `/etc/dns` a volume of its own: see [`/etc/dns`](#etcdns).

### A rebuilt primary

A primary that reports no cluster, in a cluster that was formed, stops the run at **Inspect**:
forming again would make a second cluster and leave the secondaries in the first. Restore `/etc/dns`
from a vzdump backup, or promote a secondary by hand and take the primary out as below.

### Changing the primary

A change of primary is a promotion, and **this version refuses it** with a clear message on the
cluster. To promote by hand, use `POST /api/admin/cluster/secondary/promote` on the secondary; that
is outside what Octopushy has tested, and `cluster.yaml` can then only be brought back in line by
forming a new cluster. Changing `clusterDomain` is refused for the same reason: it is permanent
once formed, and OpenTofu's `prevent_destroy` stops it a second time.

### Removing things

Take a member out of `members` to remove it. The member list is part of what was trusted, so the
run first waits for **Trust cluster** again. The plan then shows a delete, so `policy.destructive`
applies. After approval the member leaves the cluster **before** the apply, identified by what
Octopushy recorded when it joined, and never by looking undeclared or by its address alone: a peer
nobody approved removing stays, and verification reports it. If it fails, nothing was applied, so
the next plan shows the same delete. A cluster has to keep one secondary.

Three things can happen to a member that is leaving, and only the first lets its guest be retired:

- **It was told to leave and the primary stopped listing it.** The run records that it left.
- **It did not answer, or the primary no longer listed it anyway.** A peer that cannot answer has
  its record deleted from the primary. That is all that happened: the member still believes it
  belongs, and comes back if it answers later. The run goes on, and the cluster's page shows it with
  *nothing saw it leave*, until you have checked it and said so.
- **Only its address was ever known,** because its join never reported back. Another node can be at
  that address by now, so nothing is removed and the step fails. Compare the primary's peers by
  hand, take it out if it is there, and acknowledge it on the cluster's page. The next plan goes on.

A guest stays bound to its cluster until a run has seen it leave, whatever `cluster.yaml` says, so
its retirement waits with *Waiting for the guest to leave*, including when one commit replaces a
secondary and deletes the old guest's folder. If a guest left another way, or the cluster's
definition is gone, say so on the cluster's page; that is your statement, and nothing checks it.

Deleting `cluster.yaml` destroys nothing. The cluster is reported as missing and keeps running,
and Octopushy stops managing it until the file is back; its members stay bound. The root protects
the cluster from destruction and never sets `force_delete`. Taking a cluster apart is done in
Technitium.

### Rotating the password

Change the password on the primary, which syncs it to every secondary, then add it to the store as
a new version of the same entry; protection and grants stay with the entry. Until the store is updated, a cluster run fails at **Inspect** with
the primary refusing the password, and changes nothing. Do not change it on a secondary alone, and
keep it the same store entry on every member: a rebuilt member starts with the value in the store,
so a stale store entry is a member the cluster cannot sign in to.

### The password and what remains

The admin password reaches two kinds of code. The `technitium` executor is Octopushy's own, and
receives it for every step. OpenTofu's provider needs a login too, and **it needs one while
planning, before anyone has approved anything**, because it logs in when it starts, and the plan
reads the live settings. So the plan of any commit holds the admin password in its environment.

It stays out of the saved plan and the state, because it is only ever in `TECHNITIUM_PASSWORD`.
`tests/technitium-cluster` scans both, the plan's JSON, and the logs. That is also why the root does
not use `technitium_cluster_secondary`: with the password in its `primary_node_password`, a saved
plan held it in clear, in `tofu show -json` and in `tfplan`, and it cannot be `ephemeral`, because
those are ordinary sensitive attributes. A typed agent action does the join instead.

A read-only login for planning is what we would have preferred, and it does not work. A user with
View on every section, which needs Administration and Settings granted by hand, is still refused
`/api/zones/options/get`, which needs Modify, so the plan fails as soon as a `technitium_zone` is in
the state. Such a user also reads the catalog's TSIG key from the settings, and would have to exist
before the first plan.

**What protects it** is that the entry is protected, and a protected entry is released only to a
resource granted it, for the digest of the code that resource runs and how it runs it. The check is
at the release, so it covers every way a definition can bind a key, and no commit can change it: not
by editing a definition, and not by deleting one. A run holding such a secret receives only the files
that digest covers, so code elsewhere in the repository is not there to run, and the agent keeps
OpenTofu to them: no module is fetched that no one listed, and no provider the lock file does not pin
([how](installation-trust.md#application-clusters)). A state backend never
gets a protected entry, whatever was granted: its environment reaches OpenTofu during init, before
there is a plan to review, and the backend alone decides where that value goes.

**This is a review, not a sandbox.** The digest says the code is what you looked at. It does not say
the code is safe, and code you trusted runs with the password.

What someone with commit access can still do:

- Anything that is data, not code: change settings, blocklists and forward zones, which then apply
  under `policy`, and `policy` is in the repository too. A forward zone sends a domain's queries to a
  server of their choosing. `policy.approval: manual` is as good as the repository is.
- Destroy or replace unprotected guests, and retire any guest not bound to a cluster.
- Run code at plan time with anything that is not protected: other store entries bound to their
  recipes, backend environment, and Proxmox tokens. Shared-mode tokens reach every plan, as
  [before](proxmox-credentials.md). The check that a backend cannot name a Proxmox token reads the
  commit's own definitions, so it is defence in depth at most; mark such a token protected if it
  matters, and the release check then applies to it as well.
- Hold up work: a commit that changes trusted code stops those runs until someone trusts it.
- Change what a guest does that trust does not list, such as its hardware, under `policy`.

What remains exposed at plan time: the password is in the environment of the trusted root's OpenTofu
and of the provider it pins, on the agent. Anyone who controls that agent, or who gets you to trust
code that sends it away, has it.

And on the network:

- **The join is not pinned.** See [Joining](#joining). During a join, whoever answers for the
  primary's address gets the join credential, an administrator's password for the primary, and a
  fake primary can feed the secondary configuration and apps of its choosing. **Join members on a
  network segment you trust.**
- **Trusting a certificate is only as good as the comparison.** The first read is whatever answered.
  Compare it from the Proxmox host, or you are trusting that nobody was there at that moment.
- **The provider trusts a certificate as a CA, not as one exact leaf.** The agent compares the exact
  certificate, in inspection, which is first in every run, and a run stops there if it differs. The
  provider, which receives the password too, is given the trusted certificate as its only root,
  because that is all v1.3.0 can do, and that is a weaker check in two ways. Something that
  certificate signed would pass: the install makes the certificate with `CA:FALSE`, but Go's
  verifier does not enforce that on a certificate it was given as a trust anchor, so `CA:FALSE`
  is no promise that it cannot sign (we have not tested this ourselves; it is a statement about
  Go, not about this install), and trust does not refuse a member that presents a CA. And an apply can come long after
  inspection, while it waits for approval, with no exact check since. Either way it takes the
  private key of the certificate you trusted, which passes the exact check anyway.
- **Proxmox's connection,** next.

### Proxmox's certificate

`tls: { verify: false }` sets `PROXMOX_VE_INSECURE=true`. Whoever can intercept that connection gets
the Proxmox API token, which is already everything, and the sealing key that travels in the create
request. They are one risk, and the token is the worse half. **Verify Proxmox's certificate.**

`tls.caCertificate` does not do that for OpenTofu. It reaches the agent's own Proxmox client, and no
further: with it set and `verify` on, the real provider fails with *certificate signed by unknown
authority* (tried against a local stand-in, with bpg/proxmox 0.117.0). The provider uses the system's
trust store. So use a certificate that store accepts, on every agent and, with the container
runtime, in the OpenTofu image.

## Joining

Technitium gives a secondary no way to pin the primary, so `initJoin` has to ignore certificate
errors. **That leg goes to whoever answers for the primary's address, and it is not only a
password.** Over the pinned connection, the agent creates `octopushy-cluster-join` on the primary
with a random password, gives that to the secondary, and deletes the user as soon as the join is
over. Where the secondary joins is what is declared, and nothing a node reports:
`https://<primary guest id>.<clusterDomain>:53443/` for the name Technitium insists on, and the
primary's address to connect to.

What a host answering for the primary during the join gets and can do:

- **The join credential,** an administrator on the real primary until the join ends. Used at once
  against it, that is long enough to make another administrator.
- **If it relays the join to the real primary, the `ClusterApiToken`,** which is
  administrator-equivalent and does not expire, **and the users' password hashes.** Deleting
  `octopushy-cluster-join` afterwards does not take that token back.
- **If it does not relay, the secondary's configuration.** Upstream's joiner restores `authConfig`,
  the DNS settings, the zones, the block lists and the apps from whoever answers, so a fake primary
  can feed a secondary any of them, including an app that runs code.

So **join members on a network segment you trust**, with nothing between the agent, the secondary
and the primary that you do not. Nothing here changes that.

What Octopushy checks afterwards, over the pinned connections, is whether the real primary lists the
member and whether the member names the declared primary's address as its own primary. A fake primary
that does not relay the join leaves the real one without the member, and that fails the step with the
member's name, so a join that the primary never saw is not recorded. It cannot tell a relay apart, and
it cannot compare the certificate the secondary saw with the pinned one, because Technitium does not
say. What it passed on is not checked.

After the join, nodes talk to each other, and validate each other's certificates, through DANE
(upstream's `ClusterNode.cs` and `HttpApiClient.cs`). How that behaves when a node has no TLSA
record was not verified here, so nothing in this page relies on it.

It has to be an administrator, since Technitium refuses anyone else. A cluster does not depend on
it: tried on real 15.6.2 servers, sync survives deleting, disabling, and rotating that user, through
a restart of each node and a later change, and `tests/technitium-cluster` keeps checking deletion.

The user is deleted when the join succeeds, when it is refused, when its outcome is lost, and when
making the user fails part way. If the delete itself fails, the step says so and names the user.
An agent that dies during a join leaves the user behind, with a password nobody has, and so does a
delete that could not be done: verification fails, naming it, until it is gone. The next join of any
member replaces it. To remove it by hand, sign in to the primary and delete it under
**Administration, Users**.
