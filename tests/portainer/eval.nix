{ nixpkgs }:
let
  pkgs = import nixpkgs { system = "x86_64-linux"; };
  lib = pkgs.lib;
  evaluated = extra: import (nixpkgs + "/nixos/lib/eval-config.nix") {
    system = "x86_64-linux";
    modules = [
      ../../examples/lab/modules/nixos/proxmox-guest.nix
      ../../examples/lab/modules/nixos/services/portainer.nix
      { boot.isContainer = true; system.stateVersion = "26.05"; }
      extra
    ];
  };
  system = extra: (evaluated extra).config;
  opts = (evaluated {}).options.octopushy.services.portainer;
  acceptsSources = sources: (builtins.tryEval (builtins.deepSeq
    (system { octopushy.services.portainer.allowedNetworks = sources; })
      .octopushy.services.portainer.allowedNetworks true)).success;
  disabled = system {};
  closed = system {
    octopushy.serviceNode.enable = true;
    octopushy.services.portainer.enable = true;
  };
  exposed = system {
    octopushy.serviceNode.enable = true;
    octopushy.services.portainer = {
      enable = true;
      expose = true;
      allowedNetworks = [ "10.1.0.0/24" ];
    };
    networking.firewall.allowedTCPPorts = [ 8443 ];
  };
  valid = c: lib.all (a: a.assertion) c.assertions;
  bad = system {
    octopushy.serviceNode.enable = true;
    octopushy.services.portainer = { enable = true; expose = true; };
  };
  checks = {
    disabledByDefault = !disabled.virtualisation.docker.enable
      && !(disabled.systemd.services ? docker-portainer)
      && !(disabled.networking.nftables.tables ? octopushy_portainer);
    validClosedConfiguration = valid closed;
    validExposedConfiguration = valid exposed;
    exposureNeedsSources = !valid bad;
    needsBase = !valid (system { octopushy.services.portainer.enable = true; });
    noAutomaticPrune = !closed.virtualisation.docker.autoPrune.enable;
    noDockerTcpListener = lib.all (host: !(lib.hasPrefix "tcp://" host))
      (closed.virtualisation.docker.daemon.settings.hosts or []);
    dockerNotPodman = closed.virtualisation.oci-containers.backend == "docker";
    noPublishedPortBypass = closed.virtualisation.oci-containers.containers.portainer.ports == []
      && lib.elem "--network=host" closed.virtualisation.oci-containers.containers.portainer.extraOptions;
    bootstrapSecretOutsideStore = closed.systemd.services.docker-portainer.serviceConfig.LoadCredential
      == [ "admin-password:/var/lib/octopushy-secrets/portainer-admin-password" ];
    boundedMemory = lib.elem "--memory=384m" closed.virtualisation.oci-containers.containers.portainer.extraOptions;
    httpDisabled = lib.elem "--http-disabled" closed.virtualisation.oci-containers.containers.portainer.cmd;
    onlyScopedFirewallAllow = !(lib.elem 9443 exposed.networking.firewall.allowedTCPPorts)
      && lib.elem 22 exposed.networking.firewall.allowedTCPPorts
      && lib.elem 8443 exposed.networking.firewall.allowedTCPPorts;
    closedHasNoLanAllow = !lib.hasInfix "10.1." closed.networking.nftables.tables.octopushy_portainer.content;
    ipv6AndOtherSourcesDenied = lib.hasInfix "tcp dport 9443 drop"
      exposed.networking.nftables.tables.octopushy_portainer.content;
    noBootstrapCredentialInArgs = !lib.elem "--admin-password"
      closed.virtualisation.oci-containers.containers.portainer.cmd;
    rejectsUnrestrictedSources = !acceptsSources [ "0.0.0.0/0" ];
    rejectsPublicSources = !acceptsSources [ "8.8.8.8/32" ];
    rejectsOverbroadPrivateSources = !acceptsSources [ "10.1.0.0/7" ];
    rejectsMalformedSources = !acceptsSources [ "10.999.0.0/24" ];
    rejectsNftInjection = !acceptsSources [ "10.1.0.0/24; accept" ];
    rejectsFloatingImage = !opts.image.type.check "docker.io/portainer/portainer-ce:latest";
    rejectsStoreSecret = !opts.adminPasswordFile.type.check "/nix/store/password";
  };
in
assert lib.assertMsg (lib.all (x: x) (builtins.attrValues checks))
  ("Failed Portainer checks: " + lib.concatStringsSep ", " (builtins.attrNames (lib.filterAttrs (_: value: !value) checks)));
{
  inherit checks;
  dockerVersion = closed.virtualisation.docker.package.version;
  image = closed.virtualisation.oci-containers.containers.portainer.image;
  closedGuard = closed.networking.nftables.tables.octopushy_portainer.content;
  exposedGuard = exposed.networking.nftables.tables.octopushy_portainer.content;
}
