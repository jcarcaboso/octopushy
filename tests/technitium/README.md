# Technitium role checks (offline / evaluation only)

No running guest, network request to a DNS server, secret, host install,
resolver change, or service activation is needed. Python uses only its standard
library. Use the parent's approved rootless Nix environment when Nix is absent;
these tests do not download a container or install a daemon.

## Static checks without Nix

From the repository root:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 tests/technitium/check.py -v
```

Three static checks run; the two evaluated-output checks explicitly skip.

## Evaluate the actual NixOS module

Use the parent's **same pinned nixpkgs source**, not a floating channel:
`f3a73bcf5d1c5b75a66cf35a48957e30d41d647a` (`nixos-26.05`),
Technitium 15.6.0. `eval.nix` performs no fetch itself.

```sh
nix-instantiate --eval --strict --json tests/technitium/eval.nix \
  --arg nixpkgs /absolute/path/to/pinned-nixpkgs \
  > /tmp/technitium-eval.json
PYTHONDONTWRITEBYTECODE=1 python3 tests/technitium/check.py \
  --eval-json /tmp/technitium-eval.json -v
```

Parent's already-cached source in the approved validation container is
`/nix/store/kv7b75nbhc93yrs276n0ba9gsiv8cz3b-source`. Substitute it for the
placeholder; bind the repository read-only and run offline. Container lifecycle
is the parent's responsibility, not hidden in a test runner.

Evaluation asserts:

- Disabled-by-default import and ordinary removal leave no native service or
  private guard; enabling alone leaves DNS closed.
- Explicit DNS opt-in adds **both** protocols, with exact ingress interface,
  destination IPv4 address, and source networks.
- Cluster peer declaration alone opens nothing. A separate cluster opt-in
  admits only exact peer IPv4 sources to UDP/TCP 53 and HTTPS TCP 53443,
  independently of client exposure. HTTP 5380 stays private. Empty, duplicate,
  self, public, subnet, IPv6, and injected peer addresses are rejected.
- Native dynamic-user/state-directory semantics, private directory modes, and
  firewall startup/stop dependency.
- Unrelated host TCP/UDP lists and trusted-interface policy are **identical**
  with the role off/on, including SSH; native loopback merges are retained.
- Firewall/nftables disabled, global flush, broad native `openFirewall`,
  missing boundaries, wildcard/loopback/injected interfaces, malformed/IPv6
  addresses, public/default-route/escaping/injected CIDRs are rejected.
- Real default 15.6.0 can run closed but **cannot opt into client or cluster exposure**.
  Actual configured package overrides with older, absent, unknown, or
  prerelease version metadata cannot fall back to the default package's
  version for eligibility.
- The documentation-address example evaluates and stays closed.

**Positive exposure fixtures are TEST-ONLY synthetic package metadata:** they
change the old derivation's `version` attribute to test eligibility and emitted
allow rules; its outPath is still the old package. They are **not a patched
executable**, never built, shipped in the example, or activated, and do not
prove runtime compatibility of 15.6.2. Real pinned-package tests stay separate.
The JSON's `exposureFixture` marker makes this limitation explicit.

The Python evaluated-output check parses the **actual emitted** nft subset and
checks closed bootstrap, opt-in success, wrong source/interface/destination,
trusted-interface bypass attempts, IPv6 denial, remote UI denial (also IPv6),
loopback management, peer-only cluster paths, peer removal, independent client
exposure, and unrelated SSH/TCP/UDP continuation. It refuses unknown
rule syntax. This small interpreter is a regression check, **not a kernel
firewall or VM test**; an accept in the guard still continues to the native
firewall.

## Closed two-guest example

The closed two-guest example has an additional configuration-only check:

```sh
nix-instantiate --eval --strict --json tests/technitium/cluster-eval.nix \
  --arg nixpkgs /absolute/path/to/pinned-nixpkgs
```

It checks separate identities, reciprocal exact peers, independently composed
guest baselines, closed client and peer exposure, the unchanged real package,
both exposure gates, and absence of enrollment hooks or peer startup barriers.
It does not prove actual placement on a Proxmox host or create cluster membership.

## Optional native nft syntax/build check

Run this in a validation environment that can obtain the nftables build
dependencies:

```sh
nix-build tests/technitium/eval.nix --no-out-link \
  --arg nixpkgs /absolute/path/to/pinned-nixpkgs \
  --arg buildFirewall true
```

This builds bootstrap, client-only, peer-only, and combined
**native NixOS ruleset derivations**, whose
default `checkRuleset` runs `nft --check` using nixpkgs' userspace-kernel shim.
Positive exposed rules use the explicitly TEST-ONLY metadata fixture above.
It does not execute the unit's `ExecStart`, apply firewall rules, build the
Technitium application, or activate a generation. Even a successful build
would not prove real guest traffic behavior or boot/stop ordering.

### Recorded validation, October 20, 2026

Validation used the exact pinned source in an isolated rootless Nix 2.34.9
runner, with repository mounts read-only:

- **53 role Nix assertions passed**, real default package `15.6.0`;
  exposure of that actual package was rejected. Positive allow-rule cases use
  only the explicitly labeled synthetic metadata fixture.
- **13 closed two-guest configuration checks passed** against that same real
  package; no synthetic package is used by the pair example.
- **6 Python checks passed** against the fresh JSON, including its required
  test-only fixture marker and peer-only cluster packet cases.
- The **native ruleset build passed**, exit 0:
  `/nix/store/n1dqsvfd4xknjgml4rnjkrc0ip1cafg0-technitium-firewall-check`.
  Closed, client-only, peer-only, and combined rulesets used upstream
  `nft --check`/shim.
- The integrated NixOS flake passed evaluation, and all three policy-check
  derivations were realized: guest baseline, bootstrap-image policy, and
  composition with the closed Technitium example. The image itself was not built.
- No rules activation, services, kernel packet-path, or live guest validation.

Local evidence is `.dev/nix-validation/technitium-eval.json`,
`.dev/nix-validation/technitium-cluster-eval.json`,
`.dev/nix-validation/nixos-eval.log`, and
`.dev/nix-validation/cluster-firewall-build.log` (not repository fixtures).
The runner's build sandbox was disabled **inside the rootless validation
container**, not by changing the host; no native Nix daemon was installed.
This validates the prepared baseline, not the security status of its older
application package. Review the newer upstream fixes documented in the role's
README/research before any real exposure.

## Later authorized guest verification

Before production DNS opt-in, verify in an isolated dedicated guest:

1. Closed first-run packets cannot reach UDP/TCP 53 or TCP UI 5380/53443.
2. Initialize credentials and app bindings/recursion ACL using protected
   console/tunnel access; never expose an uninitialized UI.
3. Confirm source/interface/destination allow and deny paths on both DNS
   transports, IPv6 denial, UI tunnel-only access, and unaffected SSH.
4. Confirm firewall failure stops the unit, disabling the role stops it without
   deleting state, and restoring a backup remains a separate operation from
   Nix generation rollback.

These steps are a handoff checklist, not requests to contact a live VM now.
