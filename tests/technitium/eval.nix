# Pure configuration evaluation; no fetch, build, VM, or service activation.
# nix-instantiate --eval --strict --json tests/technitium/eval.nix \
#   --arg nixpkgs /absolute/path/to/the/pinned/nixpkgs
{ nixpkgs, system ? "x86_64-linux", buildFirewall ? false }:
let
  pkgs = import nixpkgs { inherit system; };
  inherit (pkgs) lib;
  role = ../../examples/lab/modules/nixos/services/technitium.nix;
  settings = {
    enable = true;
    lanInterface = "ens18";
    lanAddress = "192.0.2.53";
    allowedNetworks = [ "192.0.2.0/24" "198.51.100.0/24" ];
  };
  evaluate = modules: (import (nixpkgs + "/nixos/lib/eval-config.nix") {
    inherit system;
    modules = [
      role
      {
        system.stateVersion = "26.05";
        boot.isContainer = true;
        networking.firewall.enable = true;
        networking.nftables.enable = true;
        networking.nftables.flushRuleset = false;
        # Deliberately broad host accepts must not bypass the early guard.
        networking.firewall.allowedTCPPorts = [ 22 53 5380 8443 ];
        networking.firewall.allowedUDPPorts = [ 53 123 ];
        networking.firewall.trustedInterfaces = [ "trusted0" ];
      }
    ] ++ modules;
  }).config;
  withRole = extra: evaluate [
    { octopushy.services.technitium = settings; }
    extra
  ];
  disabled = evaluate [ ];
  bootstrap = withRole { };
  unpatchedExposure = withRole { octopushy.services.technitium.exposeDNS = true; };
  # TEST ONLY: change metadata to exercise the eligibility/allow-rule branch.
  # This is still the old package's outPath, NOT a patched executable. Never
  # build/ship/activate this fixture or use it outside these evaluation checks.
  testOnlyPackageMetadata = pkgs.technitium-dns-server // { version = "15.6.2"; };
  exposed = withRole {
    octopushy.services.technitium.exposeDNS = true;
    services.technitium-dns-server.package = testOnlyPackageMetadata;
  };
  clusterSettings = {
    clusterPeers = [ "192.0.2.54" ];
    exposeCluster = true;
  };
  clusterOnly = withRole {
    octopushy.services.technitium = clusterSettings;
    services.technitium-dns-server.package = testOnlyPackageMetadata;
  };
  clusterAndClients = withRole {
    octopushy.services.technitium = clusterSettings // { exposeDNS = true; };
    services.technitium-dns-server.package = testOnlyPackageMetadata;
  };
  clusterClosed = withRole {
    octopushy.services.technitium.clusterPeers = clusterSettings.clusterPeers;
  };
  clusterPeerRemoved = withRole {
    octopushy.services.technitium = clusterSettings // { clusterPeers = [ "192.0.2.55" ]; };
    services.technitium-dns-server.package = testOnlyPackageMetadata;
  };
  example = evaluate [ ../../examples/lab/modules/nixos/examples/technitium.nix ];
  removed = withRole { octopushy.services.technitium.enable = lib.mkForce false; };
  guard = cfg: cfg.networking.nftables.tables.octopushy_technitium.content;
  passes = cfg: lib.all (entry: entry.assertion) cfg.assertions;
  invalid = name: value:
    !(builtins.tryEval (builtins.deepSeq
      ((withRole { octopushy.services.technitium.${name} = lib.mkForce value; })
        .octopushy.services.technitium.${name}) true)).success;
  tests = {
    disabledByDefault = !disabled.services.technitium-dns-server.enable
      && !(disabled.networking.nftables.tables ? octopushy_technitium);
    enabledNativeService = bootstrap.services.technitium-dns-server.enable;
    defaultsRemainClosed = !bootstrap.octopushy.services.technitium.exposeDNS
      && !bootstrap.octopushy.services.technitium.exposeCluster
      && bootstrap.octopushy.services.technitium.clusterPeers == [ ]
      && bootstrap.networking.firewall.extraInputRules == "";
    clusterPeersAloneStayClosed = passes clusterClosed
      && guard clusterClosed == guard bootstrap
      && clusterClosed.networking.firewall.extraInputRules == "";
    clusterIndependentOfClientExposure = passes clusterOnly
      && !clusterOnly.octopushy.services.technitium.exposeDNS
      && lib.hasInfix "tcp dport 53443 accept" (guard clusterOnly)
      && !(lib.hasInfix "192.0.2.0/24" (guard clusterOnly));
    clusterAndClientsCompose = passes clusterAndClients
      && lib.hasInfix "192.0.2.54" clusterAndClients.networking.firewall.extraInputRules
      && lib.hasInfix "192.0.2.0/24" clusterAndClients.networking.firewall.extraInputRules;
    clusterDoesNotOpenHTTP = !(lib.hasInfix "tcp dport 5380 accept" (guard clusterOnly));
    clusterAllowsMatchNative = lib.hasInfix
      ''iifname "ens18" ip daddr 192.0.2.53 ip saddr { 192.0.2.54 } tcp dport 53443 accept''
      clusterOnly.networking.firewall.extraInputRules;
    clusterPeerRemoval = !(lib.hasInfix "192.0.2.54" (guard clusterPeerRemoved))
      && lib.hasInfix "192.0.2.55" (guard clusterPeerRemoved);
    clusterUnpatchedExposureRejected = !passes (withRole {
      octopushy.services.technitium = clusterSettings;
    });
    clusterUnknownVersionRejected = !passes (withRole {
      octopushy.services.technitium = clusterSettings;
      services.technitium-dns-server.package = builtins.removeAttrs pkgs.technitium-dns-server [ "version" ];
    });
    clusterPrereleaseVersionRejected = !passes (withRole {
      octopushy.services.technitium = clusterSettings;
      services.technitium-dns-server.package = pkgs.technitium-dns-server // { version = "15.6.2-rc1"; };
    });
    clusterEmptyPeersRejected = !passes (withRole {
      octopushy.services.technitium.exposeCluster = true;
      services.technitium-dns-server.package = testOnlyPackageMetadata;
    });
    clusterSelfPeerRejected = !passes (withRole {
      octopushy.services.technitium.clusterPeers = [ settings.lanAddress ];
    });
    clusterDuplicatePeerRejected = !passes (withRole {
      octopushy.services.technitium.clusterPeers = [ "192.0.2.54" "192.0.2.54" ];
    });
    clusterSubnetRejected = invalid "clusterPeers" [ "192.0.2.0/24" ];
    clusterPublicPeerRejected = invalid "clusterPeers" [ "8.8.8.8" ];
    clusterIPv6PeerRejected = invalid "clusterPeers" [ "2001:db8::54" ];
    clusterPeerInjectionRejected = invalid "clusterPeers" [ "192.0.2.54 } accept;" ];
    clusterHostPolicyPreserved =
      clusterOnly.networking.firewall.allowedTCPPorts == disabled.networking.firewall.allowedTCPPorts
      && clusterOnly.networking.firewall.allowedUDPPorts == disabled.networking.firewall.allowedUDPPorts
      && clusterOnly.networking.firewall.trustedInterfaces == disabled.networking.firewall.trustedInterfaces;
    nativeFirewallClosed = !bootstrap.services.technitium-dns-server.openFirewall
      && !exposed.services.technitium-dns-server.openFirewall;
    nativeDataOwnership = bootstrap.systemd.services.technitium-dns-server.serviceConfig.DynamicUser
      && bootstrap.systemd.services.technitium-dns-server.serviceConfig.StateDirectory == "technitium-dns-server"
      && bootstrap.systemd.services.technitium-dns-server.serviceConfig.WorkingDirectory == "%S/technitium-dns-server"
      && bootstrap.systemd.services.technitium-dns-server.serviceConfig.StateDirectoryMode == "0700"
      && !(bootstrap.systemd.services.technitium-dns-server.serviceConfig ? StateDirectoryPreserve);
    firewallDependency = lib.elem "nftables.service" bootstrap.systemd.services.technitium-dns-server.after
      && lib.elem "nftables.service" bootstrap.systemd.services.technitium-dns-server.bindsTo;
    earlyGuard = lib.hasInfix "hook input priority -10; policy accept;" (guard bootstrap);
    bothDNSProtocols = lib.hasInfix "udp dport 53 accept" (guard exposed)
      && lib.hasInfix "tcp dport 53 accept" (guard exposed)
      && lib.hasInfix "udp dport 53 drop" (guard exposed)
      && lib.hasInfix "tcp dport 53 drop" (guard exposed);
    scopedAllowInNativeFirewall = lib.hasInfix
      ''iifname "ens18" ip daddr 192.0.2.53 ip saddr { 192.0.2.0/24, 198.51.100.0/24 }''
      exposed.networking.firewall.extraInputRules;
    unrelatedHostPolicyPreserved =
      exposed.networking.firewall.allowedTCPPorts == disabled.networking.firewall.allowedTCPPorts
      && exposed.networking.firewall.allowedUDPPorts == disabled.networking.firewall.allowedUDPPorts
      && exposed.networking.firewall.trustedInterfaces == disabled.networking.firewall.trustedInterfaces
      && lib.elem 22 exposed.networking.firewall.allowedTCPPorts
      && lib.elem 8443 exposed.networking.firewall.allowedTCPPorts
      && lib.elem 123 exposed.networking.firewall.allowedUDPPorts;
    exampleClosed = example.services.technitium-dns-server.enable
      && !example.octopushy.services.technitium.exposeDNS && passes example;
    ordinaryRemoval = !removed.services.technitium-dns-server.enable
      && !(removed.networking.nftables.tables ? octopushy_technitium);
    validAssertions = passes bootstrap && passes exposed;
    realUnpatchedExposureRejected = !passes unpatchedExposure
      && unpatchedExposure.services.technitium-dns-server.package.version == "15.6.0";
    downgradedPackageOverrideRejected = !passes (withRole {
      octopushy.services.technitium.exposeDNS = true;
      services.technitium-dns-server.package =
        pkgs.technitium-dns-server // { version = "15.6.1"; };
    });
    missingPackageVersionRejected = !passes (withRole {
      octopushy.services.technitium.exposeDNS = true;
      services.technitium-dns-server.package =
        builtins.removeAttrs pkgs.technitium-dns-server [ "version" ];
    });
    unknownPackageVersionRejected = !passes (withRole {
      octopushy.services.technitium.exposeDNS = true;
      services.technitium-dns-server.package =
        pkgs.technitium-dns-server // { version = "unstable"; };
    });
    prereleasePackageVersionRejected = !passes (withRole {
      octopushy.services.technitium.exposeDNS = true;
      services.technitium-dns-server.package =
        pkgs.technitium-dns-server // { version = "15.6.2-rc1"; };
    });
    missingBoundaryRejected = !passes (evaluate [
      { octopushy.services.technitium.enable = true; }
    ]);
    noFirewallRejected = !passes (withRole {
      networking.firewall.enable = lib.mkForce false;
    });
    noNftablesRejected = !passes (withRole {
      networking.nftables.enable = lib.mkForce false;
    });
    globalFlushRejected = !passes (withRole {
      networking.nftables.flushRuleset = lib.mkForce true;
    });
    broadNativeFirewallRejected = !passes (withRole {
      services.technitium-dns-server.openFirewall = true;
    });
    wildcardInterfaceRejected = invalid "lanInterface" "ens*";
    loopbackInterfaceRejected = invalid "lanInterface" "lo";
    interfaceInjectionRejected = invalid "lanInterface" "ens18\"; accept";
    unspecifiedAddressRejected = invalid "lanAddress" "0.0.0.0";
    malformedAddressRejected = invalid "lanAddress" "192.0.2.999";
    ipv6AddressRejected = invalid "lanAddress" "2001:db8::53";
    defaultRouteRejected = invalid "allowedNetworks" [ "0.0.0.0/0" ];
    publicSourceRejected = invalid "allowedNetworks" [ "8.8.8.0/24" ];
    networkEscapesLANRejected = invalid "allowedNetworks" [ "192.168.0.0/8" ];
    networkInjectionRejected = invalid "allowedNetworks" [ "192.0.2.0/24 } accept;" ];
    leadingZeroRejected = invalid "allowedNetworks" [ "192.000.2.0/24" ];
    privateLANAccepted = passes (withRole {
      octopushy.services.technitium = {
        lanAddress = lib.mkForce "10.20.0.53";
        allowedNetworks = lib.mkForce [ "10.20.0.0/16" "172.16.0.0/12" "192.168.1.0/24" ];
      };
    });
    pinnedVersion = pkgs.technitium-dns-server.version == "15.6.0";
  };
  failed = lib.attrNames (lib.filterAttrs (_: passed: !passed) tests);
in
assert lib.assertMsg (failed == [ ]) "Technitium evaluation failures: ${lib.concatStringsSep ", " failed}";
if buildFirewall then
  # These native writeTextFile derivations run nixpkgs' nft --check with its
  # userspace-kernel shim. The exposed rules use TEST-ONLY metadata eligibility.
  # No synthetic Technitium package is built or selected as a build input.
  # Build rules only, never invoke ExecStart or activate them.
  pkgs.runCommand "technitium-firewall-check" { } ''
    test -s ${builtins.elemAt bootstrap.systemd.services.nftables.serviceConfig.ExecStart 1}
    test -s ${builtins.elemAt exposed.systemd.services.nftables.serviceConfig.ExecStart 1}
    test -s ${builtins.elemAt clusterOnly.systemd.services.nftables.serviceConfig.ExecStart 1}
    test -s ${builtins.elemAt clusterAndClients.systemd.services.nftables.serviceConfig.ExecStart 1}
    touch "$out"
  ''
else
{
  inherit tests;
  packageVersion = pkgs.technitium-dns-server.version;
  exposureFixture = "TEST-ONLY synthetic package version metadata; NOT a patched runtime";
  # The host-side check interprets only this deliberately tiny nft rule subset.
  # It is not a replacement for the NixOS nftables build/kernel checker.
  packetFixtures = {
    bootstrap = guard bootstrap;
    exposed = guard exposed;
    nativeAllows = exposed.networking.firewall.extraInputRules;
    clusterOnly = guard clusterOnly;
    clusterAndClients = guard clusterAndClients;
    clusterClosed = guard clusterClosed;
    clusterPeerRemoved = guard clusterPeerRemoved;
    clusterNativeAllows = clusterOnly.networking.firewall.extraInputRules;
  };
}
