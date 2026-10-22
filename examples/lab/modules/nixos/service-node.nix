{ config, lib, pkgs, ... }:
{
  options.octopushy.serviceNode.enable =
    lib.mkEnableOption "the common Docker service-node tools";

  config = lib.mkIf config.octopushy.serviceNode.enable {
    assertions = [
      {
        assertion = config.networking.firewall.enable && config.networking.nftables.enable;
        message = "The service-node base requires the existing firewall/nftables baseline.";
      }
    ];
    virtualisation.docker = {
      enable = true;
      # Never garbage-collect a service's persistent data automatically.
      autoPrune.enable = false;
      daemon.settings = {
        log-driver = "json-file";
        log-opts = { max-size = "10m"; max-file = "3"; };
      };
    };
    environment.systemPackages = with pkgs; [
      curl
      jq
      gitMinimal
      docker-compose
      openssl
    ];
    # No TCP Docker API, Docker-group membership, or application ports.
    # Access to the Docker socket (including through Portainer) is root-equivalent.
  };
}
