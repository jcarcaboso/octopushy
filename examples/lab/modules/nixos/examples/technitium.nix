# Dedicated VM only. Import alongside the parent's pinned guest/image baseline.
# Documentation addresses: replace these with the VM's real static LAN details.
{
  imports = [ ../services/technitium.nix ];

  octopushy.services.technitium = {
    enable = true;
    lanInterface = "ens18";
    lanAddress = "192.0.2.53";
    allowedNetworks = [ "192.0.2.0/24" ];
    # Keep false through password change, listener/recursion review, and backup.
    # The pinned real 15.6.0 package cannot pass the >=15.6.2 exposure gate;
    # parent must separately select/review a patched compatible package first.
    # A later system-generation activation may explicitly set this to true.
    exposeDNS = false;
  };
}
