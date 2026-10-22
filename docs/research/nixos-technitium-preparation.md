# Native NixOS Technitium preparation

Research date: **2026-10-20**. Scope: a dedicated VM using the native
`services.technitium-dns-server`, with operator-managed application configuration.
No containers, API reconciliation, record controller, or secret provisioning.

## Evidence, pins, and verification limits

- **Selected nixpkgs:** `f3a73bcf5d1c5b75a66cf35a48957e30d41d647a`
  (parent-selected `nixos-26.05` pin). Its package is **15.6.0**, built with
  `.NET SDK 10` and `ASP.NET Core runtime 10`. This remains **closed preparation
  only**, with no package override; it is ineligible for DNS exposure. [N1]
- **Native application source reviewed:** Technitium **15.6.0**, tag
  `v15.6.0`, commit **`70495af5b3caddb1244c2608514987b1622b1594`**:
  `Program.cs`, web/DNS/auth configuration loaders, and settings documentation.
  The official tag API independently resolved that commit. [T0–T5]
- **Newer upstream release:** the official latest-release API returned
  **15.6.2**, published **2026-10-17**, tag commit
  **`0dd592aad21eced4221a9a46204cb1a299972ca0`**. Its changelog fixes a UDP
  resource-exhaustion issue and a zone authorization-boundary issue. Only its
  release metadata/changelog, not all its startup code, were reviewed. [R1–R3]
  The role's exposure gate now requires metadata at least **15.6.2**; the
  selected older package cannot be exposed.
- Sources were investigated with `web.run`, then the relevant official pinned raw
  files were fetched directly with Python `urllib`, in memory. Direct content
  settled discrepancies in web rendering, including the native module's
  **111 lines**, `package` option, literal working directory, and recursion enum.
- **Source inspection only:** no VM, SSH, DNS query, deployed Technitium request,
  service execution, container, host install, or secret access occurred.
  **Parent-reported historical, pre-version-gate checks:** rootless evaluation
  against the exact fetched nixpkgs pin passed **31 Nix assertions**;
  **5 Python checks** passed (3 static and 2 parsing emitted fixtures).
  Evaluation confirmed package `15.6.0` and literal `%S/technitium-dns-server`.
  The current suite has **36 checks; the parent's rerun is pending**. It covers
  rejecting exposure with the real `15.6.0` package, a configured `15.6.1`
  override, and missing/unstable/RC version metadata. Positive allow-rule fixtures
  use **test-only synthetic package metadata with the same old `outPath`**:
  never built as a package, shipped, placed in an example, or activated. They
  are **not patched-runtime validation**. Parent also reports that
  the optional `buildFirewall` native ruleset derivation **succeeded with exit 0**,
  producing `/nix/store/hfh85c8ph5y198xjzp4mkjs03scp3q4i-technitium-firewall-check`;
  log: `.dev/nix-validation/firewall-build.log`. It builds bootstrap/exposed
  native nft rules using upstream `nft --check`/shim. The rootless runner used
  `--option sandbox false --option build-users-group '' --max-jobs 1 --cores 1`,
  read-only source mounts, and downloads from the official Nix binary cache only.
  These results were reported by the parent, not independently executed by this
  sidecar. **No firewall rules or services were activated, and no actual
  kernel/guest traffic validation occurred.** Evaluating or building a system
  generation is not executing its guest service, applying its guest firewall,
  or validating kernel/BPF enforcement.

## Verified native startup and first-run semantics

### Native, not Docker-only

`DnsServerApp/Program.cs` takes a configuration-directory argument and constructs
`DnsWebService` directly. The native application itself reads the variables below;
no Docker entrypoint is required. Its `--stop-if-bind-fails` flag requests startup
failure rather than the normal web-listener recovery path. The stock NixOS
command does not supply that flag. [T1, N2]

### Web console/API

Verified in `DnsWebService.cs`: [T2]

| Variable | Native meaning |
| --- | --- |
| `DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES` | Comma-separated IP addresses, parsed as addresses, not `IP:port` endpoints; use `127.0.0.1,::1` for loopback bootstrap. |
| `DNS_SERVER_WEB_SERVICE_HTTP_PORT` | Integer HTTP port; default `5380`. |
| `DNS_SERVER_WEB_SERVICE_HTTPS_PORT` | Integer HTTPS port; default `53443`; setting the port does not enable HTTPS. |
| `DNS_SERVER_WEB_SERVICE_ENABLE_HTTPS` | Boolean TLS enablement; initially disabled. |
| `DNS_SERVER_WEB_SERVICE_USE_SELF_SIGNED_CERT` | Boolean self-signed certificate option. TLS certificate path/password variables are also read. |

These initialize missing `webservice.config`, provided legacy configuration was
not migrated. They are **not continuous overrides of existing state**.

Initial configured addresses are **`0.0.0.0` and `::`**, not loopback. The normal
startup recovery path can revert to wildcard addresses and default ports
`5380`/`53443`; further recovery tries IPv4 wildcard HTTP before loopback.
Therefore loopback environment configuration alone is **not an exposure
guarantee**. [T2]

### Administrator password

The native `AuthManager` creates default authentication state when `auth.config`
is absent: [T4]

1. Nonempty `DNS_SERVER_ADMIN_PASSWORD` takes precedence.
2. Otherwise `DNS_SERVER_ADMIN_PASSWORD_FILE` is opened and **only its first
   line** is used as the password.
3. With neither value, the account is **`admin` / `admin`**.
4. Exceptions while reading/using the password file are logged and **fall back
   to `admin` / `admin`**, rather than failing closed.

Existing authentication state bypasses this creation path: changing either
variable does not rotate the saved password. Do not delete `auth.config` as a
routine configuration operation. Do not assume generic `_FILE` support for other
settings.

**Design implication:** a future credential integration must keep the file
readable by the service's dynamic user, never put plaintext in the Nix store,
and detect provisioning failures independently. File support is not a
fail-closed password-delivery mechanism. This role need not implement one.

### DNS listeners and recursion

Verified in `Dns/DnsServer.cs`: [T3]

- Default configured DNS endpoints are **`0.0.0.0:53` and `[::]:53`**.
  Actual socket availability is a runtime question.
- The reviewed first-run loader has **no DNS listen-address/port environment
  variable**. Configure DNS endpoints through the application; do not invent
  `DNS_SERVER_DNS_SERVER_LOCAL_END_POINTS` from a Docker assumption.
- `DNS_SERVER_RECURSION` is a case-insensitive enum. Exact values are `Deny`,
  `Allow`, **`AllowOnlyForPrivateNetworks`**, and `UseSpecifiedNetworkACL`.
  The first-run default is `AllowOnlyForPrivateNetworks`, not deny-all.
- `DNS_SERVER_RECURSION_NETWORK_ACL` accepts comma-separated ACL entries and takes
  precedence over the older denied/allowed-network environment variables.
- These bootstrap settings are read when `dns.config` is missing, not
  continuously reconciled. `DNS_SERVER_RECURSION=Deny` can reduce fresh-state
  risk but cannot establish policy for restored/existing state.

### Application settings and authentication boundaries

The pinned settings documentation names `dnsServerLocalEndPoints`,
`webServiceLocalAddresses`, the HTTP/TLS ports, and `recursion`. These identify
operator settings, **not a proposal to call the API**. The web console and API
share the web service. [T5]

For `recursionNetworkACL`, plain IP/CIDR entries allow, `!` entries deny,
evaluation is ordered, and unmatched clients are denied except loopback. The
ACL is used only with `UseSpecifiedNetworkACL`; merely filling it while retaining
the default recursion mode is insufficient. Recursion policy is not a substitute
for DNS listener/firewall policy or web authentication. [T5]

## Verified native NixOS integration

At the selected pin the module exposes `enable`, **`package`**, `openFirewall`,
`firewallTCPPorts`, and `firewallUDPPorts`; there is **no `settings` option**.
`openFirewall` defaults to false. Enabling it would add global UDP `53` and TCP
`53`, `5380`, `53443` by default. Keep it false. [N2]

Literal unit configuration: [N2]

```nix
ExecStart = "${cfg.package}/bin/technitium-dns-server $STATE_DIRECTORY";
DynamicUser = true;
StateDirectory = "technitium-dns-server";
LogsDirectory = "technitium";
WorkingDirectory = "%S/technitium-dns-server";
```

`%S` resolves to the system state root, conventionally `/var/lib`.
`DynamicUser` changes the backing location to `/var/lib/private/...` and creates
the usual-path symlink; **the module does not literally configure a
`/var/lib/private/...` working directory**. [M1]

The native unit already restricts address families to
`AF_INET AF_INET6 AF_UNIX AF_NETLINK` and capabilities to
`CAP_NET_BIND_SERVICE`. These do not restrict individual listening addresses or
permitted DNS clients. [N2]

## Parent-selected role design — not observed runtime behavior

The following records the implementation agreement, not a claim that this
research sidecar implemented or tested it:

- Reject `exposeDNS = true` unless the **actual**
  `config.services.technitium-dns-server.package.version` is known stable numeric
  **`X.Y.Z >= 15.6.2`**. Missing, unknown, prerelease, or older metadata fails
  closed, with **no bypass boolean**. This minimum addresses the known UDP
  exhaustion/zone authorization fixes; it does **not** establish that every
  later version is secure or reviewed. Eligibility is a metadata minimum plus
  human bootstrap attestation, **not runtime verification**.
- Require `networking.firewall.enable`, `networking.nftables.enable`, and
  `networking.nftables.flushRuleset = false`. Use a role-owned
  **`inet octopushy_technitium`** table, input hook priority **`-10`**, with
  no global `flush ruleset`. NixOS supports table-scoped replacement/cleanup;
  `flushRuleset` explicitly controls whole-ruleset flushing. [N3]
- Drop non-loopback TCP management traffic to **5380/53443** regardless of
  bootstrap/exposure state. Drop remote UDP/TCP DNS **53** until the operator
  explicitly opts in with an interface, destination IPv4, and source CIDR.
  Initially accept only **RFC1918/documentation IPv4** configuration; deny
  remote IPv6 DNS. This is intentionally not a public-resolver role.
- On opt-in, add matching narrow normal-firewall `extraInputRules`, not broad
  `allowedTCPPorts`/`allowedUDPPorts`. An nftables drop is final; accept in an
  earlier base chain does not bypass later chains. Thus the guard can veto a
  broad accept, while selected traffic still needs the normal firewall's
  permission. [F1]
- Bind/order Technitium after `nftables.service` using `bindsTo` and `after`.
  This concerns **guest activation**, not evaluating the Nix expressions.
  Global configuration remains mergeable and trusted root can override/delete
  rules. This is not tamper resistance against the guest administrator.
- Retain state when removing the role. Treat restored state as potentially
  different from bootstrap intent; neither role removal nor changed environment
  variables should silently reset credentials or application configuration.
- Keep the minimal scope: **no secret/configuration helper**. Exposure requires
  the operator's protected-console/bootstrap attestation. This is an explicit
  human assertion, **not programmatic verification** that a password changed,
  an ACL is correct, or existing state honors first-run environment variables.

### Optional future systemd hardening — not in the evaluated role

The current role retains the native upstream sandbox, sets directory modes, and
adds `bindsTo`/`after`; it adds **neither `SocketBindDeny`/`SocketBindAllow` nor
`RestrictNetworkInterfaces`**. The following are optional future considerations,
not implemented or evaluated role behavior and not bootstrap requirements.

A future hardening change could evaluate `SocketBindDeny=any` with explicit
positive-port allowances for standard DNS/management sockets. The rule grammar
supports IPv4/IPv6, TCP/UDP,
and ports, **not a local-IP allowlist**. `any` covers positive ports: port-zero
dynamic binding is outside these rules, so this is not a guarantee that arbitrary
ephemeral listeners are impossible. It also does not prohibit outbound resolver
connections. [M2]

If separately adding `RestrictNetworkInterfaces` in future, account for
**loopback and the selected VM interface**, not loopback alone: the allowlist
also affects upstream traffic.
These cgroup/BPF mechanisms require compatible guest support and verification;
they do not replace the input firewall. [M2]

Keep the role's claim **standard-port-only**. Persisted alternate DNS/web ports,
DHCP, DoH/DoT/DoQ, HTTP/3, clustering, additional interfaces, and an IPv6 service
are unsupported until separately reviewed with corresponding sandbox/firewall
rules. A port number does not authenticate the protocol served on it.

## Recommended manual bootstrap

1. Start with remote DNS closed and management always blocked remotely. The
   actual minimal role sets **no application environment variables**: it relies
   on the closed-first firewall, then operator configuration via console/tunnel.
   If separately chosen by the parent, the verified first-run loopback web
   environment setting is optional defense in depth, not current role behavior
   or a bootstrap requirement. Retain the firewall guard: defaults, existing
   state, or recovery can select wildcard addresses.
2. Use the guest's trusted local graphical console/browser to reach
   `http://127.0.0.1:5380/`. A serial console alone is not a JavaScript web
   browser. If no local browser exists, arrange a separately reviewed transport
   to the guest's loopback listener; an already secured SSH tunnel is one
   possible design, **not something this role provisions or this research used**.
   Do not temporarily expose port 5380 to solve this prerequisite.
3. With fresh default authentication, sign in locally and immediately choose a
   strong unique password. Existing state may require existing credentials.
   Do not assume a password-file failure prevented startup.
4. In the application, review DNS listen endpoints, authoritative zones,
   forwarding/upstream settings, and recursion. Prefer explicit trusted-client
   recursion ACLs with `UseSpecifiedNetworkACL`; choose `Deny` if recursion is
   not needed. Keep optional protocols and remote management out of scope.
5. Back up persistent state securely. The agreed `15.6.0` pin remains closed:
   DNS opt-in requires a separately reviewed actual package with stable numeric
   version metadata at least `15.6.2`, plus bootstrap attestation and the precise
   IPv4 scope. No package override is part of this preparation. Follow any
   eligible opt-in with separately authorized guest
   runtime checks of allowed/denied clients, management isolation, restart,
   existing-state behavior, IPv6 denial, and firewall dependency behavior.

Read-only prior art was inspected at
`/home/ops/projects/infra/nodes/homelab/templates/dns-server/modules/technitium.nix`
and located with `rg` at
`/home/ops/projects/infra/nodes/docs/technitium-dns-architecture-research.md`.
It is background, not proof of these guarantees; the former immediately opens
DNS and management ports on selected interfaces and is not the desired bootstrap
policy.

## Primary source URLs

Technitium source links below are immutable commit links. Systemd manuals use
the official `v259` tag, not an assertion that guest/kernel enforcement was tested.

- **[T0]** Official `v15.6.0` tag resolution:
  https://api.github.com/repos/TechnitiumSoftware/DnsServer/git/ref/tags/v15.6.0
- **[T1]** Native entry point:
  https://github.com/TechnitiumSoftware/DnsServer/blob/70495af5b3caddb1244c2608514987b1622b1594/DnsServerApp/Program.cs
- **[T2]** Web defaults, first-run loader, listener recovery:
  https://github.com/TechnitiumSoftware/DnsServer/blob/70495af5b3caddb1244c2608514987b1622b1594/DnsServerCore/DnsWebService.cs
  (notably lines 105–119, 266–345, 1630–1696, 2949–2956).
- **[T3]** Native DNS defaults/environment/recursion:
  https://github.com/TechnitiumSoftware/DnsServer/blob/70495af5b3caddb1244c2608514987b1622b1594/DnsServerCore/Dns/DnsServer.cs
  (enum lines 68–74, first-run loader 463–595, endpoints 6953–6973).
- **[T4]** Native authentication bootstrap:
  https://github.com/TechnitiumSoftware/DnsServer/blob/70495af5b3caddb1244c2608514987b1622b1594/DnsServerCore/Auth/AuthManager.cs
  (missing-config handling around 258; password selection 943–974).
- **[T5]** Settings documentation:
  https://github.com/TechnitiumSoftware/DnsServer/blob/70495af5b3caddb1244c2608514987b1622b1594/APIDOCS.md#set-dns-settings
- **[R1]** Official latest-release API, queried 2026-10-20:
  https://api.github.com/repos/TechnitiumSoftware/DnsServer/releases/latest
- **[R2]** Official `v15.6.2` tag resolution:
  https://api.github.com/repos/TechnitiumSoftware/DnsServer/git/ref/tags/v15.6.2
- **[R3]** Immutable 15.6.2 changelog:
  https://github.com/TechnitiumSoftware/DnsServer/blob/0dd592aad21eced4221a9a46204cb1a299972ca0/CHANGELOG.md
- **[N1]** Selected package:
  https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/pkgs/by-name/te/technitium-dns-server/package.nix
- **[N2]** Selected native module:
  https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/technitium-dns-server.nix
- **[N3]** Selected nftables integration:
  https://github.com/NixOS/nixpkgs/blob/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a/nixos/modules/services/networking/nftables.nix
- **[M1]** Official systemd execution settings, including dynamic-user directories:
  https://github.com/systemd/systemd/blob/v259/man/systemd.exec.xml
- **[M2]** Official systemd bind/interface controls:
  https://github.com/systemd/systemd/blob/v259/man/systemd.resource-control.xml
- **[F1]** Official nftables chain/hook/verdict semantics:
  https://wiki.nftables.org/wiki-nftables/index.php/Configuring_chains
