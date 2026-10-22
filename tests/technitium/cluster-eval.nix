# Pure evaluation only: no VM, service, enrollment, package override, or build.
# nix-instantiate --eval --strict --json tests/technitium/cluster-eval.nix \
#   --arg nixpkgs /nix/store/kv7b75nbhc93yrs276n0ba9gsiv8cz3b-source
{ nixpkgs, system ? "x86_64-linux" }:
let
  pkgs = import nixpkgs { inherit system; };
  inherit (pkgs) lib;
  examples = import ../../examples/lab/modules/nixos/examples/technitium-cluster.nix;
  modules = [ examples.primary examples.secondary ];
  evaluate = module: extra: (import (nixpkgs + "/nixos/lib/eval-config.nix") {
    inherit system;
    modules = [
      ../../examples/lab/modules/nixos/proxmox-guest.nix
      module
      {
        system.stateVersion = "26.05";
        boot.isContainer = true; # Evaluation fixture, not a deployed container.
      }
      extra
    ];
  }).config;
  guests = map (module: evaluate module { }) modules;
  primary = builtins.elemAt guests 0;
  secondary = builtins.elemAt guests 1;
  role = cfg: cfg.octopushy.services.technitium;
  unit = cfg: cfg.systemd.services.technitium-dns-server;
  passes = cfg: lib.all (entry: entry.assertion) cfg.assertions;
  openingRejected = name: lib.all (module:
    !passes (evaluate module {
      octopushy.services.technitium.${name} = lib.mkForce true;
    })
  ) modules;
  tests = {
    bothGuestModulesValid = lib.all passes guests;
    distinctNames = primary.networking.hostName == "dns-primary"
      && secondary.networking.hostName == "dns-secondary";
    distinctAddresses = (role primary).lanAddress == "192.0.2.53"
      && (role secondary).lanAddress == "192.0.2.54";
    reciprocalExactPeers = (role primary).clusterPeers == [ (role secondary).lanAddress ]
      && (role secondary).clusterPeers == [ (role primary).lanAddress ];
    nativeRoleEnabled = lib.all (cfg:
      (role cfg).enable && cfg.services.technitium-dns-server.enable
    ) guests;
    bothExposureSwitchesClosed = lib.all (cfg:
      !(role cfg).exposeDNS && !(role cfg).exposeCluster
    ) guests;
    noClientOrPeerAllows = lib.all (cfg:
      !cfg.services.technitium-dns-server.openFirewall
      && cfg.networking.firewall.allowedTCPPorts == [ 22 ]
      && cfg.networking.firewall.allowedUDPPorts == [ ]
      && cfg.networking.firewall.extraInputRules == ""
    ) guests;
    closedGuardPresent = lib.all (cfg:
      let guard = cfg.networking.nftables.tables.octopushy_technitium.content;
      in lib.hasInfix ''iifname "lo" accept'' guard
        && lib.hasInfix "tcp dport { 5380, 53443 } drop" guard
        && lib.hasInfix "udp dport 53 drop" guard
        && lib.hasInfix "tcp dport 53 drop" guard
        && !lib.hasInfix "dport 53 accept" guard
        && !lib.hasInfix "dport 53443 accept" guard
    ) guests;
    realPinnedPackageUnchanged = pkgs.technitium-dns-server.version == "15.6.0"
      && lib.all (cfg:
        cfg.services.technitium-dns-server.package.version == "15.6.0"
        && cfg.services.technitium-dns-server.package.outPath
          == pkgs.technitium-dns-server.outPath
      ) guests;
    realPinRejectsClientExposure = openingRejected "exposeDNS";
    realPinRejectsClusterExposure = openingRejected "exposeCluster";
    # This checks declarative hooks, NOT the contents of mutable runtime state.
    noMembershipAutomationOrPeerStartupBarrier = lib.all (cfg:
      (unit cfg).script == ""
      && (unit cfg).preStart == ""
      && (unit cfg).postStart == ""
      && (unit cfg).serviceConfig.ExecStart
        == "${cfg.services.technitium-dns-server.package}/bin/technitium-dns-server $STATE_DIRECTORY"
      && (unit cfg).bindsTo == [ "nftables.service" ]
      && !(lib.any (dependency:
        lib.hasInfix "dns-primary" dependency || lib.hasInfix "dns-secondary" dependency
      ) ((unit cfg).after ++ (unit cfg).requires ++ (unit cfg).wants))
    ) guests;
    nativePrivateStatePerGuest = lib.all (cfg:
      (unit cfg).serviceConfig.DynamicUser
      && (unit cfg).serviceConfig.StateDirectory == "technitium-dns-server"
      && (unit cfg).serviceConfig.StateDirectoryMode == "0700"
    ) guests;
  };
  failed = lib.attrNames (lib.filterAttrs (_: passed: !passed) tests);
in
assert lib.assertMsg (failed == [ ]) "Technitium pair evaluation failures: ${lib.concatStringsSep ", " failed}";
{
  inherit tests;
  packageVersion = pkgs.technitium-dns-server.version;
}
