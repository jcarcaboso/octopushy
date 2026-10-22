{ config, lib, pkgs, ... }:
let
  cfg = config.octopushy.services.portainer;
  inherit (lib) mkEnableOption mkIf mkOption types;
  ipv4 = value:
    builtins.match "(0|[1-9][0-9]{0,2})(\\.(0|[1-9][0-9]{0,2})){3}" value != null
    && lib.all (part: builtins.fromJSON part <= 255) (lib.splitString "." value);
  privatePrefix = address:
    let
      octets = map builtins.fromJSON (lib.splitString "." address);
      a = builtins.elemAt octets 0;
      b = builtins.elemAt octets 1;
    in
      if a == 10 then 8
      else if a == 172 && b >= 16 && b <= 31 then 12
      else if a == 192 && b == 168 then 16
      else null;
  privateNetwork = types.addCheck types.str (value:
    let
      parts = lib.splitString "/" value;
      address = builtins.head parts;
      prefix = lib.last parts;
    in
      builtins.length parts == 2 && ipv4 address && privatePrefix address != null
      && builtins.match "([1-9]|[12][0-9]|3[0-2])" prefix != null
      && builtins.fromJSON prefix >= privatePrefix address
  );
  lanRule = ''
    iifname "${cfg.lanInterface}" ip saddr { ${lib.concatStringsSep ", " cfg.allowedNetworks} } tcp dport 9443 accept
  '';
in
{
  imports = [ ../service-node.nix ];
  options.octopushy.services.portainer = {
    enable = mkEnableOption "Portainer CE as a Docker container";
    image = mkOption {
      type = types.strMatching "docker.io/portainer/portainer-ce:[0-9]+\\.[0-9]+\\.[0-9]+@sha256:[a-f0-9]{64}";
      default = "docker.io/portainer/portainer-ce:2.39.8@sha256:da7f74b74c7f37c50d2ebd3db4c6bcfdbf2fe9ca1e8b4c6c0203e39fde1fd249";
      description = "Reviewed immutable Portainer CE image; updates require an explicit configuration change.";
    };
    adminPasswordFile = mkOption {
      type = types.strMatching "/var/lib/octopushy-secrets/[a-zA-Z0-9_.-]+";
      default = "/var/lib/octopushy-secrets/portainer-admin-password";
      description = "Root-only runtime file, never a Nix path or a secret in the store. Only bootstraps an empty Portainer database.";
    };
    expose = mkOption {
      type = types.bool;
      default = false;
      description = "Opt in to direct LAN HTTPS after bootstrap and authenticated verification.";
    };
    lanInterface = mkOption {
      type = types.addCheck (types.strMatching "[a-zA-Z0-9][a-zA-Z0-9_.-]{0,14}")
        (value: value != "lo");
      default = "ens18";
    };
    allowedNetworks = mkOption {
      type = types.listOf privateNetwork;
      default = [ ];
      description = "Explicit private IPv4 sources. Remote IPv6 is not admitted by this role.";
    };
  };

  config = mkIf cfg.enable {
    assertions = [
      {
        assertion = config.octopushy.serviceNode.enable;
        message = "Portainer requires the common Docker service-node base.";
      }
      {
        assertion = config.networking.firewall.enable && config.networking.nftables.enable
          && !config.networking.nftables.flushRuleset;
        message = "Portainer requires the baseline firewall/nftables without flushing Docker's rules.";
      }
      {
        assertion = !cfg.expose || cfg.allowedNetworks != [ ];
        message = "Portainer exposure requires explicit private source networks.";
      }
    ];

    virtualisation.oci-containers.backend = "docker";
    virtualisation.oci-containers.containers.portainer = {
      inherit (cfg) image;
      # No Docker-published port / DNAT path that bypasses the host input firewall.
      extraOptions = [
        "--network=host"
        "--cap-drop=ALL"
        "--security-opt=no-new-privileges"
        "--memory=384m"
        "--cpus=0.75"
        "--pids-limit=128"
      ];
      volumes = [
        "/var/run/docker.sock:/var/run/docker.sock"
        "octopushy-portainer-data:/data"
        "/run/credentials/docker-portainer.service/admin-password:/run/secrets/admin-password:ro"
      ];
      cmd = [
        "--http-disabled"
        "--bind-https" ":9443"
        "--admin-password-file" "/run/secrets/admin-password"
        "--host" "unix:///var/run/docker.sock"
      ];
    };
    systemd.services.docker-portainer.serviceConfig = {
      LoadCredential = [ "admin-password:${cfg.adminPasswordFile}" ];
      ExecStartPre = lib.mkBefore [
        "${pkgs.writeShellScript "check-portainer-credential" ''
          test -s "$CREDENTIALS_DIRECTORY/admin-password"
        ''}"
      ];
    };

    networking.nftables.tables.octopushy_portainer = {
      family = "inet";
      content = ''
        chain guard {
          type filter hook input priority -10; policy accept;
          iifname "lo" tcp dport 9443 accept
          ${lib.optionalString cfg.expose lanRule}
          tcp dport 9443 drop
        }
      '';
    };
    networking.firewall.extraInputRules = lib.optionalString cfg.expose lanRule;
    # No HTTP 9000, Edge tunnel 8000, or unrestricted allowedTCPPorts entry.
    # Disabling this role removes its unit/rules, NOT its Docker data volume.
  };
}
