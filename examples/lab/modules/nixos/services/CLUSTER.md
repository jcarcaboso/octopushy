# Two DNS VMs on the same Proxmox host

**Preparation only:** two separate NixOS guests on **umbrel**, not two processes
in one guest. The pair shares a host, power, storage, and network failure domain;
it is **not host failover**. No live deployment, DNS/router change, enrollment,
package patch, controller schema, or orchestration is included.

## Opt-in composition

[`../examples/technitium-cluster.nix`](../examples/technitium-cluster.nix) returns
an ordinary attrset containing `primary` and `secondary` guest modules. Import
one member into each parent's independently maintained NixOS guest:

```nix
let
  dnsPair = import ./examples/technitium-cluster.nix;
in {
  imports = [
    ./proxmox-guest.nix
    dnsPair.primary # Use dnsPair.secondary in the other guest.
    # Your installed guest's hardware/network/administrator module.
  ];
}
```

Paths above are relative to this native NixOS directory. The core flake does not
activate either example. Hostnames are `dns-primary` / `dns-secondary`, with
documentation addresses `192.0.2.53` / `192.0.2.54` and reciprocal exact peer IPs.
`lanAddress` describes an **already configured address**, not a network
assignment. Replace the example `ens18` and addresses with real, separate
guest endpoints; no interface, gateway, filesystem, or resolver is created.
Prefer static addresses or externally reserved DHCP leases for persistent DNS.
Plain DHCP is suitable for an explicitly temporary test: discover each lease
before configuring the role and membership. If a lease changes, the firewall
destination/peer rules and application cluster endpoints must be reconciled;
there is no automatic DHCP-to-cluster reconciler.

The operator must later select two VMIDs, capacity, disks, bridge, hardware,
SSH public keys/verified host keys, original `stateVersion`, and bootstrap
`templateURL` plus checksum. **Placement on umbrel is a provision-time choice,
not enforced by these Nix modules.** Each VM gets its own native state directory
and backups; do not share a state disk or clone initialized node identity.

## Closed package and firewall boundary

The exact nixpkgs pin `f3a73bcf5d1c5b75a66cf35a48957e30d41d647a`
provides real Technitium **15.6.0**. Both `exposeDNS` and `exposeCluster` remain
`false`. Either exposure requires the **actually configured native package**
to declare a numeric version **>= 15.6.2**; no override or bypass is supplied.
A separately reviewed package change is a prerequisite for the later steps.
The minimum is not a claim that a release is otherwise safe.

- `exposeCluster` admits only `clusterPeers`: exact interface, destination guest
  address, and source peer IPv4 address on **UDP/TCP 53 and TCP 53443**.
  Configure reciprocal rules on both guests and check intervening network policy
  permits connections in both directions. These are ingress rules, not an egress
  policy or a membership declaration. `clusterPeers` takes IPs, **not CIDRs**.
- **53443 exposes the entire HTTPS UI/API to the peer**, not just cluster URLs.
  TLS identity, authentication, and trusting the other guest remain necessary.
- `exposeDNS` separately admits client UDP/TCP 53 from `allowedNetworks`;
  peer-only bootstrap does not open client networks.
- **HTTP 5380 stays loopback-only through the firewall**, even for peers.
  Non-peer HTTPS remains denied. These flags neither configure application
  listeners/certificates nor enroll nodes. Keep native `openFirewall = false`.

## Later manual initialize / join / verify / enable procedure

Run only after separate authorization, package review, and isolated guest
packet-path verification. Follow the [single-node bootstrap safeguards](README.md)
for each guest, without its client-exposure step. The
[upstream findings](../../../../../docs/research/technitium-cluster.md) describe
the native semantics and source references.

1. **Prepare each VM separately, closed.** Use protected console or authenticated
   SSH forwarding bound to workstation loopback for each node's local 5380 UI.
   Change first-run administrator credentials immediately. Verify local DNS
   endpoints and recursion ACLs independently. Configure distinct application
   DNS Server Domain Names matching the hostnames and a reserved cluster domain,
   e.g. `dns-cluster.test`; resulting node names are
   `dns-primary.dns-cluster.test` and `dns-secondary.dns-cluster.test`.
   Configure native HTTPS on **53443**, each node's own TLS certificate/private
   material, and reachable listener addresses. Verify certificate identity
   through a trusted channel. HTTP and HTTPS share web bind addresses, so do not
   mistake loopback binding for the eventual peer firewall guarantee. Keep
   secrets outside Nix, the store, repository, shell history, and command URLs.
   Back up both guests' initialized application state separately.
2. **Enable only peer access**, using `exposeCluster = true` on each guest after
   the package prerequisite. Leave `exposeDNS = false`. Verify reciprocal
   UDP/TCP 53 and HTTPS 53443 reachability with the intended addresses and
   listeners, and denied non-peer HTTPS / all remote HTTP. TLS must terminate
   directly at Technitium: **no reverse TLS termination**. Native cluster
   communication uses TLSA/DANE-EE; a proxy certificate is not the node identity.
3. **Initialize the primary manually.** In Administration > Cluster on
   `dns-primary`, select Initialize New Cluster, domain `dns-cluster.test`, and
   its static IP (`192.0.2.53` only as a placeholder). Check the reported primary
   identity and cluster/catalog zones. Do not reuse a namespace with unrelated
   zones or allow unrelated authorities to own this `.test` example.
4. **Join the secondary manually.** Confirm backups and the overwrite warning:
   joining replaces secondary **Allowed, Blocked, Apps, Settings, and
   Administration** configuration, including administrator settings. Do not
   assume its old local credentials remain valid. On `dns-secondary`, select
   Join Cluster; provide its static `.54` address, primary HTTPS **hostname**
   and port 53443, and the explicit primary bootstrap `.53` IP. Supply
   credentials/2FA only in protected UI fields. Avoid resolving the primary via
   the not-yet-formed cluster: the hostname URL supplies TLS identity while the
   explicit IP supplies transport. Do not use an IP-literal primary URL or
   casually disable certificate validation. Wait for completion.
5. **Verify sync and independent service.** Check both nodes' reported roles,
   membership, versions, and synchronization. Create one disposable `.test`
   zone/record on the primary and explicitly add that zone to the cluster catalog;
   verify the secondary receives it and both answer over UDP **and** TCP 53.
   Run these checks from the allowed peer guests or protected guest-local
   sessions: ordinary LAN clients are still deliberately denied at this stage.
   Check deletion of the test record replicates, then clean up the test zone.
   Only catalog-member zones replicate; unrelated node-local zones are not
   automatically added. Independently inspect each node's listeners, TLS
   certificate, recursion ACLs, and private state after join.
6. **Enable clients explicitly and individually.** Only after the previous
   checks, opt each validated guest into `exposeDNS = true`. Advertising both
   resolver addresses to clients/router is a **separate future authorized
   change**; client failover behavior must be tested. Verify retained data and
   direct DNS answers when either daemon is unavailable. Do not stop an existing
   serving primary merely because the secondary is unavailable. There is no
   fleet-wide activation barrier or automatic write-primary election here;
   promotion/recovery requires a separate deliberate operator decision.

**Unknown outcome is not permission to retry:** initialization/join is a
state-changing operation, not an idempotent deployment hook. After a timeout,
lost UI response, or restart, inspect **both** nodes' membership, cluster/catalog
state, and logs through protected access. Resume only after confirming the
actual outcome. Do not blindly rejoin, delete membership, restore state, or
promote a secondary. Nix generation rollback does not roll back mutable DNS data.

## Evaluation, not runtime evidence

```sh
nix-instantiate --eval --strict --json tests/technitium/cluster-eval.nix \
  --arg nixpkgs /nix/store/kv7b75nbhc93yrs276n0ba9gsiv8cz3b-source
```

Run from the repository root in the prepared Nix validation environment; the store
path is that environment's cached exact pin. This checks closed composition,
reciprocal peers, names/addresses, native startup without enrollment hooks or
peer barriers, and rejection of both exposure flags on real 15.6.0. It does not
initialize a cluster or validate guest traffic, TLS/DANE, synchronization,
restart recovery, client failover, or survival of a host failure.

All 13 pair checks passed against this pin on October 20, 2026. The recorded
JSON is `.dev/nix-validation/technitium-cluster-eval.json`; it is local test
evidence, not a deployed cluster's status.
