# Native Technitium cluster preparation

Research checked **October 20, 2026** against the official **November 29, 2025**
clustering guide and **v15.6.2** API/source. This narrows the read-only prior art
at `/home/ops/projects/infra/nodes/docs/technitium-dns-architecture-research.md`
to two separate NixOS DNS VMs on **umbrel**. No controller integration, enrollment
automation, package override, or live infrastructure operation is included.

## Verified upstream behavior

- **One write-primary, independent DNS service.** Clustering coordinates shared
  configuration; either node answers clients without routing queries through
  the other. Do not infer quorum or automatic primary election: promotion is a
  separate operation, and loss of primary prevents shared configuration changes.
  Both primary/secondary application roles differ from NixOS hostnames. [T1, T2]
- **Catalog ownership is selective.** Initialization creates a cluster zone and
  catalog. Only explicitly enrolled catalog-member zones replicate; pre-existing
  unrelated zones remain node-local. The primary can retain existing data.
  Forwarder members produce secondary-forwarder zones, not an authority over all
  local DNS. Use a disposable `.test` namespace, not live `home.arpa`. [T1, T3]
- **Join is destructive to secondary configuration.** Initialization/join runs
  on the joining node and replaces Allowed, Blocked, Apps, Settings, and
  Administration, including local administrative configuration. Back up first.
  Synchronization restores authentication/DNS data but explicitly excludes web
  service settings, log settings, DHCP scopes, and the node's cluster config.
  Keep each node's listener/certificate/state lifecycle separate. [T2, T3]
- **Direct TLS is required.** Node communication uses DANE-EE and TLSA records;
  terminating TLS at an HTTPS reverse proxy breaks this model. The permitted
  ports for this preparation are peer UDP/TCP **53** plus native HTTPS
  **53443** in both directions. HTTPS is also the full administrative UI/API,
  not a cluster-only endpoint. Initialization/join can enable self-signed HTTPS,
  but that is not a substitute for verifying bootstrap trust. HTTP **5380** is
  kept loopback-only by the local role's firewall. [T1, T2, T4, L1]
- **Bootstrap using static IPs, not the new DNS service.** The primary URL must
  contain its hostname; the v15.6.2 implementation rejects an IP-literal URL.
  Supplying explicit primary IPs avoids resolving that hostname through the
  incomplete cluster. **Correction, October 21, 2026:** direct retrieval of
  tagged `WebServiceClusterApi.cs`, method `InitializeAndJoinClusterAsync`,
  confirms that the init/join handler reads singular **`primaryNodeIpAddress`**,
  matching APIDOCS. The earlier plural-parameter claim conflated this endpoint
  with other cluster operations. Primary initialization uses plural
  `primaryNodeIpAddresses`; secondary init/join uses singular. [T2–T4]
- **A lost response leaves an unknown outcome.** Source rejects repeat initialize
  or join once initialized and attempts compensating cleanup on join failure;
  cleanup itself may fail. Therefore inspect both nodes before retrying; do not
  treat a timeout as proof of no mutation. This is an operational inference
  from the state-changing implementation, not an upstream idempotency promise.
  [T3, T4]

## Local preparation boundary and validation limits

The native guest example exports two explicit modules, with reciprocal exact
peer IPs and both exposure flags off. Peer allow rules do not enable HTTPS or
create membership. The native nixpkgs service starts the daemon with its own
systemd-managed private state directory; it provides no clustering reconciler.
The selected nixpkgs pin
`f3a73bcf5d1c5b75a66cf35a48957e30d41d647a` supplies **15.6.0**:
both exposure switches remain blocked until the actual configured package has
numeric version **>= 15.6.2**, via a separately reviewed change. [N1, N2, L1]

Same-host placement is a provisioning decision, not a Nix guarantee, and leaves
both resolvers in one host failure domain. No cross-guest startup/activation
barrier is needed: a serving primary must not wait for a missing secondary.
These are design constraints of this preparation, not host-availability claims.
The [manual runbook](../../examples/lab/modules/nixos/services/CLUSTER.md) orders
initialize-primary, join-secondary, verify-sync, and only then client exposure.

Sources were checked with `web.run` and relevant tagged raw files read directly
in memory. No Nix executable is available in this sidecar shell; the parent
runs `tests/technitium/cluster-eval.nix` against its cached exact pin. Source
inspection/evaluation cannot prove patched-runtime compatibility, direct
TLS/DANE trust, successful enrollment, replication, boot behavior, kernel
packet filtering, or client failover. All remain later isolated-guest checks.

## Primary sources

- **[T1]** [Official clustering guide, November 29, 2025](https://blog.technitium.com/2025/11/understanding-clustering-and-how-to.html).
- **[T2]** [v15.6.2 APIDOCS](https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.2/APIDOCS.md): Cluster API Calls, Initialize Cluster, Initialize And Join Cluster, Promote Secondary Node.
- **[T3]** [v15.6.2 ClusterManager.cs](https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.2/DnsServerCore/Cluster/ClusterManager.cs): `InitializeCluster`, `InitializeAndJoinClusterAsync`, `SyncConfigFromAsync`, `PromoteToPrimaryNodeAsync`.
- **[T4]** [v15.6.2 WebServiceClusterApi.cs](https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.2/DnsServerCore/WebServiceClusterApi.cs): TLS bootstrap and `InitializeAndJoinClusterAsync` request parsing.
- **[N1]** [Pinned native NixOS module](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/technitium-dns-server.nix).
- **[N2]** [Pinned native package](https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/pkgs/by-name/te/technitium-dns-server/package.nix).
- **[L1]** [Local optional role](../../examples/lab/modules/nixos/services/technitium.nix) and [pair evaluation](../../tests/technitium/cluster-eval.nix), maintained alongside this note.
