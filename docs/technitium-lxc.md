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
  **Variables**, at least 12 characters on one line. A run is blocked until it exists.
- **Agents need a route to the container's port 22.** OpenTofu installs over SSH.

Sign in at `https://<address>:53443` as `admin`. The certificate is self-signed.

## What gets installed

Proxmox has no user-data or exec for containers, so the root generates an SSH key pair and
creates the container with its public key. OpenTofu then connects as root and runs
[`install.sh`](../examples/lab/modules/opentofu/technitium-lxc/install.sh), which:

- installs the pinned ASP.NET Core runtime and Technitium, each checked against its digest,
  under `/opt/technitium`, next to any older version until the new one passes its checks;
- writes `dns.service`, upstream's unit with the runtime's path, enabled at boot. It depends
  on neither Octopushy nor name resolution;
- keeps Technitium's plain HTTP port 5380 on loopback with an nftables rule of its own, and
  serves the API over HTTPS on 53443;
- checks the running version, that the password works and `admin`/`admin` does not, that
  ports 53 and 53443 answer, and that `/etc/resolv.conf` still names `network.dns`. If any
  check fails, the apply fails.

It does not use upstream's `install.sh`, which always fetches the latest release and points
`/etc/resolv.conf` at the server itself. It runs as the `dns-server` user, as upstream does.

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

The value reaches the apply through `TF_VAR_admin_password`, and OpenTofu copies it to a file
under `/run` in the container. The plan never receives it, and the variable is ephemeral, so
it is in neither the saved plan nor the state. In the container it reaches curl as a file,
not as an argument, and it is deleted however the install ends. `auth.config` holds only a
hash of it.

The store is not kept in sync afterwards. **Changing the value does not change the password.**
Change it in Technitium first, then in the store: a later install checks the value in the store
and fails if it does not work. In a cluster, [rotate it](#rotating-the-password) on the primary.

The generated SSH private key is in the guest's OpenTofu state, and therefore in saved plans.
Octopushy keeps both encrypted, but an external state backend keeps its own copy. OpenTofu does
not check the container's SSH host key, because nothing reports it before the first
connection: keep agents and the container on a network you trust.

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
Firewall, Options), which Octopushy does not do. Port 5380 does not rely on them.

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
    blocked: [bad.example.com]
    allowed: [false-positive.example.com]
    # control: example.org           # a name every member must resolve
credentials: { store: dns/technitium/admin-password }
tls: { verify: false }               # members serve a self-signed certificate
policy: { approval: manual }
```

Octopushy forms the cluster on the primary, joins each secondary, and owns these settings and
lists from then on. The [executor contract](../contracts/agent/v1/executors.md#technitium) has
the detail, and [the lab repository](lab-repository.md#application-clusters) the rules.

### Prerequisites

- **Each member is a guest of this recipe** on a static address, with `network.dns` naming other
  resolvers. Put members on different Proxmox hosts: members on one host are a warning, because
  that host is one failure domain for both.
- **Every member's `provision.secrets.admin_password` names the cluster's `credentials`.** Joining
  replaces a secondary's users with the primary's, and Octopushy signs in to every member with one
  password, so a guest that names another entry makes the cluster invalid.
- **At least Technitium 15.6.2**, which fixes cluster configuration sync.
- **The agent needs a route** to every member's ports 53443 and UDP 53. Port 5380 is not used.
- **With the Proxmox firewall on,** see [Firewall](#firewall): members need each other on 53443
  and 53, and the agent's address has to be in both source lists.

### The order of runs

1. Commit the guests and the cluster together. Each guest's run goes as usual.
2. The cluster's run is **blocked** until every member is applied and verified, and its reason
   names the ones it waits for. A member's failure shows there as that member's, and nothing else
   stops.
3. It then waits for [trust](installation-trust.md#application-clusters). Open the cluster's page,
   check the addresses and the recipe, and choose **Trust cluster**.
4. **Inspect** asks every member what it reports. **Plan** shows forming the cluster and a
   `terraform_data.secondary` for each secondary. After approval, **apply** forms it and sets
   everything; **Join and remove members** joins the secondaries; **Download block lists** forces
   the lists; **Verify every member** checks each one directly.

Verification runs on every cluster run, even when the plan has no changes, and **Plan now** on the
cluster's page starts one at any time. A failed check is reported as *Verification failed*, naming
the member and the way out. It says nothing about the deployment of the others, and their guests
stay as they were.

Between runs, a member guest that applied a change gets its cluster checked by a new run within a
minute, and every formed cluster is checked in the background as often as installations are
observed. A failed background check shows on the overview. It proposes nothing and changes nothing.

### Changing a blocklist

Edit `settings.blocking` and commit. Technitium does not enforce a new `lists` URL until it has
downloaded it, so a plan that changes a URL includes **Download block lists**, on the primary,
which covers every member. Entries in `blocked` and `allowed` reach secondaries through
Technitium's own sync, usually within 20 seconds, and verification waits for up to 90.

Taking the last entry out of `blocked` or `allowed` deletes a resource in the plan, so it follows
`policy.destructive`.

### Ownership

Octopushy owns the cluster's settings, lists and forward zones. **A hand edit in Technitium's
interface is reverted by the next run,** on either node, since a secondary hands its changes to
the primary. A changed setting is a change in the next plan. An entry someone added to the blocked
or allowed list is one OpenTofu cannot see, so inspect finds it, and the plan replaces
`terraform_data.undeclared_entries`, which removes it after approval. To keep an entry, declare it.

Zones and records you add yourself are left alone, and so is everything the cluster does not
declare. Per-node settings such as `logQueries` keep Technitium's defaults, because a change would
reach the primary only.

### When the primary is down

Secondaries keep answering and keep blocking, also through a restart, with what they last received.
What stops is change: a cluster run fails at **Inspect** saying so, and the guests are not touched.
Nothing is lost when the primary returns, and the next plan is clean. If it is gone for good, see
[promotion](#changing-the-primary).

### A rebuilt member

Replacing a container discards a mount point with a `size`, so a member can come back empty. It
then serves DNS **with no blocking**, and OpenTofu reports "No changes", because the primary still
lists it. Verification is what catches this: the guest's run gets its cluster checked, and the
check fails with the exact call to make.

Removing a member is a destructive decision, so Octopushy does not do this by itself. Sign in on
the primary, then delete its stale record with the id from the failure:

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

Take a member out of `members` to remove it. The plan shows a delete, so `policy.destructive`
applies, and after approval it leaves the cluster, by `deleteSecondary` if it cannot answer. Retire
its guest afterwards: a guest listed in a cluster cannot be retired, and its folder cannot be
removed. A cluster has to keep one secondary.

Deleting `cluster.yaml` destroys nothing. The cluster is reported as missing and keeps running,
and Octopushy stops managing it until the file is back. The root protects the cluster from
destruction and never sets `force_delete`. Taking a cluster apart is done in Technitium.

### Rotating the password

Change the password on the primary, which syncs it to every secondary, then add it to the store as
a new version of the same entry. Until the store is updated, a cluster run fails at **Inspect** with
the primary refusing the password, and changes nothing. Do not change it on a secondary alone, and
keep it the same store entry on every member: a rebuilt member starts with the value in the store,
so a stale store entry is a member the cluster cannot sign in to.

### The password and the trade-off

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

**So this is a trade-off, and you should know it:** repository code that runs at plan time could
read the password. What stands between them is that trust covers the recipe's code: a commit that
changes the root, its lock file, or the recipe's image waits for an operator, and a cluster's root
may hold only its own files. This is stronger than a Proxmox token in
[shared mode](proxmox-credentials.md), where any commit's code gets the token at plan time, but it
is not a sandbox: approve changes to that code as you would give someone the password. An agent
holds the password in memory and in that environment while it works.
