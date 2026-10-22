# Optional native Technitium role — dedicated NixOS VM

This small role is **off unless imported and enabled**. It uses
`services.technitium-dns-server`, not Docker. Enabling it starts Technitium but
does **not** admit remote DNS: `exposeDNS` defaults to `false`. Standard web UI
ports are denied on non-loopback interfaces by default, including after DNS
client opt-in. An independent `exposeCluster` opt-in admits only declared peers
to DNS and HTTPS 53443; HTTP 5380 remains loopback-only.
There is no automatic API/bootstrap/record reconciler.

For two separate DNS VMs on the same Proxmox host, see the
[closed cluster example and enrollment procedure](CLUSTER.md). Both guests share
one host failure domain; this is not host-level high availability.

## Compatibility and integration

Reviewed October 20, 2026 against nixpkgs `nixos-26.05` commit
`f3a73bcf5d1c5b75a66cf35a48957e30d41d647a`, Technitium **15.6.0**, with the
package's .NET 10 SDK/ASP.NET runtime dependencies. The parent owns the flake
lock, image, baseline, static network, SSH policy, and deployment workflow.
Do not substitute a different package/module version without rerunning the
checks and reviewing its defaults. The native module has a package option but
**no declarative application-settings option**. [N1], [N2]

**Before any real LAN exposure:** the source review found upstream **15.6.2**
(October 17, 2026) includes UDP resource-exhaustion and zone authorization fixes
after the pinned package. The 15.6.0 pin is an evaluated **closed preparation
baseline**, not a security recommendation for deployment. Keep `exposeDNS =
false` and `exposeCluster = false` until the operator has reviewed those fixes, selected a suitably patched
package/pin, and rerun compatibility checks. No package override is silently
introduced here. The role **asserts that the actual configured native package**
declares a stable numeric `X.Y.Z` version **at least 15.6.2** before allowing
either exposure flag; older, missing, unknown, and prerelease version metadata
fails closed. There is no bypass boolean. This minimum covers **known fixes
only**, not a guarantee that every later version is secure or reviewed. [T4],
[R1]

1. Use a **dedicated guest**, not the controller, router, or a shared DNS host.
   Include the parent's guest/hardware/image baseline.
2. That baseline must explicitly provide:

   ```nix
   networking.firewall.enable = true;
   networking.nftables.enable = true;
   networking.nftables.flushRuleset = false;
   ```

   The role asserts these settings; it does not switch backends, disable the
   firewall, or change unrelated host rules.
3. Import [`../examples/technitium.nix`](../examples/technitium.nix), or import
   `./technitium.nix` and set:

   ```nix
   octopushy.services.technitium = {
     enable = true;
     lanInterface = "ens18";              # exact existing interface
     lanAddress = "192.0.2.53";           # replace documentation address
     allowedNetworks = [ "192.0.2.0/24" ]; # replace documentation source LAN
     exposeDNS = false;
   };
   ```

   All three boundaries are required even during closed bootstrap. The role
   does not assign the address or configure the application's bind endpoints.
   The first version intentionally supports **IPv4 RFC1918 LANs and RFC5737
   documentation networks only**; no public source ranges, `/0`, IPv6
   exposure, wildcard interface, or loopback interface. Documentation addresses
   are examples, not deployable production network advice. [A1]
4. Evaluate the composed host and these tests against the **same pinned
   nixpkgs**, then have the parent perform its explicit guest-generation
   deployment. Importing the module by itself is not an application deployment
   command.

## Closed first-run bootstrap

The upstream application can initially bind beyond loopback and uses default
administrator credentials. Do not open its web UI in a security group, trusted
interface, reverse proxy, or port forward. The safety boundary here is the
guest's early nftables guard, **not an asserted loopback-only app setting**.
There is no password, token, generated credential, or secret path in Nix.
This preparation does not rely on guessed environment variables or copy the
upstream Docker entrypoint. See the source-level bootstrap investigation. [T1],
[T2], [R1]

After the parent activates the closed configuration:

1. Verify that the native service is ordered after and bound to
   `nftables.service`, the `inet octopushy_technitium` table is present, and
   **remote UDP/TCP 53 plus TCP 5380/53443 are denied**. Parent-controlled VM
   validation must verify actual packets; evaluation alone is not that proof.
2. Use a protected local console with a local browser, or authenticated SSH
   with an explicit loopback-only forwarding endpoint:

   ```sh
   ssh -N -L 127.0.0.1:15380:127.0.0.1:5380 operator@192.0.2.53
   ```

   Open `http://127.0.0.1:15380` on that trusted workstation, log in using
   upstream first-run credentials, and **immediately change the administrator
   password**. Other local users of the VM/workstation are inside this trust
   boundary; do not bootstrap on a multi-user/untrusted machine. Do not put
   credentials in command lines, URLs, Nix strings, the store, or this repo.
3. In Technitium, configure and verify the intended DNS server local endpoints
   (the guest LAN address on port 53, and loopback if needed), keep the web
   service on loopback at its standard port, and choose the intended recursion
   policy. Prefer disabling recursion when not needed; otherwise use the
   application's explicit recursion network ACL matching the intended clients.
   The packet allow-list is **not** application recursion configuration. Keep
   DHCP, DoH/DoT/DoQ and nonstandard listeners disabled. Keep clustering disabled
   for this standalone procedure; use [CLUSTER.md](CLUSTER.md) for explicit peer
   enrollment, HTTPS identities, and separate network opt-in. Application
   settings remain mutable, operator-managed data. [T2], [T3]
4. Verify credentials, listeners, both DNS protocols, recursion restrictions,
   IPv6 rejection, and state ownership; create a consistent backup of the
   initialized configuration. Recheck remote UI denial.
5. Only with a separately reviewed compatible package meeting the minimum
   version gate, explicitly set
   `octopushy.services.technitium.exposeDNS = true` in the parent-owned guest
   configuration and activate that generation. **The current real 15.6.0 pin
   fails evaluation if exposure is requested.** This flag is **operator
   attestation**, not a runtime password/configuration validator. The version
   gate checks package metadata, not a running binary or the completeness of
   future security fixes. No unattended bootstrap is claimed.

Do not restore unknown state or change listener ports under an already exposed
role. Return to closed mode, audit/bootstrap through protected access, and
verify again. If the firewall is stopped or restarted, `BindsTo` stops the
service; after restoring the firewall, an operator may need to start the
service again. Normal NixOS nftables reloads use an atomic transaction. [N3],
[S2]

## Firewall ownership and limitations

- A private `inet octopushy_technitium` input chain at priority `-10` exempts
  loopback, admits explicitly enabled cluster peers before the UI drop,
  denies other non-loopback TCP UI ports 5380/53443, and drops UDP **and**
  TCP 53 unless explicit exposure plus interface, destination address, and
  source CIDR all match. IPv6 remote DNS has no allow rule. A drop cannot be
  rescued by a later host accept, trusted interface, or established-connection
  rule. [N4], [F1]
- `clusterPeers` accepts only distinct exact private/documentation IPv4
  addresses of **other** members, never subnets or this guest's own address.
  Declaring them alone opens nothing. With `exposeCluster = true`, the exact
  ingress interface, destination address, and peer source must match for
  UDP/TCP 53 or TCP 53443. IPv6 and other sources remain denied, even if the
  native host policy trusts their interface. Client `allowedNetworks` never
  grants access to HTTPS management.
- HTTPS 53443 serves the web UI/API **as well as** clustering. A peer allowance
  is not URL-level filtering or peer authentication; both members need protected
  credentials and verified TLS identities. No app settings or membership are
  inferred from firewall inputs.
- The role merges only the same restricted client/peer accepts into
  `networking.firewall.extraInputRules`: passing the private guard must still
  pass the native host firewall. No global `allowedTCPPorts`/`allowedUDPPorts`,
  firewall disabling, table-wide host policy replacement, or `flush ruleset`.
- It owns **only UDP/TCP 53 and TCP 5380/53443**. SSH and all unrelated port
  decisions remain the baseline's responsibility. Changing UI/DNS ports,
  enabling extra protocols, a proxy/port-forward bypass, or running another
  service on these ports violates this first role's contract. No blanket
  per-process network sandbox or forward-chain filtering is claimed.
- NixOS firewall configuration is still a **system-wide merge**. All modules
  share the native table and `nftables.service`; a reload recreates configured
  tables atomically. Disabling the role removes its guard and narrow allows,
  not arbitrary host rules. Root, a conflicting module, an external ruleset,
  or deliberately overriding the guard can defeat the policy. This is not
  isolation from an administrator or from other guest-wide NixOS changes.
  [N3], [N4], [F1]
- The role does not change the system resolver, router, DHCP advertisements,
  VM routes, TLS, or records. A port-53 conflict (including an existing stub
  resolver) must be resolved by the parent/operator, not by silently disabling
  a host service here.

## State, disable, rollback

The native unit uses `DynamicUser=true`, `StateDirectory=technitium-dns-server`,
`WorkingDirectory=%S/technitium-dns-server`, and passes `$STATE_DIRECTORY` to
the native executable. With systemd's dynamic-user handling, host state is
accessed through `/var/lib/technitium-dns-server`, with private backing below
`/var/lib/private/`; let systemd own it, not a guessed fixed UID. This role
adds state/log mode `0700` and umask `0077`. [N1], [S1]

Set the role's `enable = false` (or remove its import and settings) and activate
the parent guest configuration to remove the service and role-owned firewall
contributions. **Ordinary removal does not purge application data.** No
activation hook, cleanup command, tmpfiles removal, or purge workflow is added.
Use an explicit, separately reviewed backup/restore or destructive deletion
procedure if needed. systemd state directories persist after service stop.
[S1]

A NixOS generation activation is **whole-system guest configuration**, even
when only this role changed; it is not an independent app-only deployment.
Starting/stopping the native systemd unit is independent service execution,
but it does not create/activate a NixOS generation. Rolling back a generation
may restore package/unit/firewall definitions, **not mutable DNS state, users,
zones, or application migrations**. Back up application data consistently
before upgrades and verify restore/version compatibility separately. [N5],
[S1]

## Checks and sources

Run the offline checks and parent-coordinated Nix evaluation described in
[`tests/technitium/README.md`](../../../../../tests/technitium/README.md).
Evaluation separates real pinned-package/native-service checks from positive
allow-rule fixtures using **TEST-ONLY synthetic package-version metadata**.
Those fixtures are not a patched package, are never built or activated, and
are not shipped in the example. The optional native ruleset build checks only
the emitted firewall derivations via upstream `nft --check`/shim; it does not
build the synthetic Technitium fixture. See the test README for recorded
results. No rules activation, service execution, actual kernel packet path, or
deployed guest test is claimed. No record reconciliation or controller
scheduler is included.

- **[N1] Pinned native NixOS module:** https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/technitium-dns-server.nix
- **[N2] Pinned native package:** https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/pkgs/by-name/te/technitium-dns-server/package.nix
- **[N3] Pinned nftables service/table lifecycle:** https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/nftables.nix
- **[N4] Pinned native firewall chains:** https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/firewall-nftables.nix
- **[N5] NixOS configuration lifecycle:** https://nixos.org/manual/nixos/stable/#sec-changing-config
- **[S1] systemd.exec — DynamicUser, StateDirectory, directory modes:** https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html
- **[S2] systemd.unit — BindsTo and ordering:** https://www.freedesktop.org/software/systemd/man/latest/systemd.unit.html
- **[F1] Netfilter hook priorities and accept/drop semantics:** https://wiki.nftables.org/wiki-nftables/index.php/Configuring_chains
- **[A1] IANA IPv4 special-purpose registry:** https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml
- **[T1] Technitium upstream README:** https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.0/README.md
- **[T2] Technitium native web service/configuration source:** https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.0/DnsServerCore/DnsWebService.cs
- **[T3] Technitium documented settings/recursion ACL:** https://github.com/TechnitiumSoftware/DnsServer/blob/v15.6.0/APIDOCS.md
- **[T4] Reviewed upstream 15.6.2 fixes:** https://github.com/TechnitiumSoftware/DnsServer/blob/0dd592aad21eced4221a9a46204cb1a299972ca0/CHANGELOG.md
- **[R1] Local cited preparation research:** [nixos-technitium-preparation.md](../../../../../docs/research/nixos-technitium-preparation.md)
