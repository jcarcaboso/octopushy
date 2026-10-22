# Two reusable guest modules, NOT nixosConfigurations or a cluster inventory.
# Import each beside proxmox-guest.nix and that VM's own hardware/admin config.
# Intended placement: two distinct VMs on Proxmox host "umbrel"; not enforced here.
# Documentation addresses/interface only: provision each static address separately.
{
  primary = {
    imports = [ ../services/technitium.nix ];
    networking.hostName = "dns-primary";

    octopushy.services.technitium = {
      enable = true;
      lanInterface = "ens18";
      lanAddress = "192.0.2.53";
      allowedNetworks = [ "192.0.2.0/24" ];
      clusterPeers = [ "192.0.2.54" ];
      # The real pinned 15.6.0 package cannot pass either exposure gate.
      exposeCluster = false;
      exposeDNS = false;
    };
  };

  secondary = {
    imports = [ ../services/technitium.nix ];
    networking.hostName = "dns-secondary";

    octopushy.services.technitium = {
      enable = true;
      lanInterface = "ens18";
      lanAddress = "192.0.2.54";
      allowedNetworks = [ "192.0.2.0/24" ];
      clusterPeers = [ "192.0.2.53" ];
      exposeCluster = false;
      exposeDNS = false;
    };
  };
}
