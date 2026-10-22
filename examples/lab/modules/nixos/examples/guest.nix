{ config, ... }:
let
  adminUser = config.lab.proxmoxGuest.adminUser;
  keys = config.users.users.${adminUser}.openssh.authorizedKeys;
in
{
  imports = [ ../proxmox-guest.nix ];

  # Edit only in a dedicated, reviewed configuration checkout for ONE guest.
  lab.proxmoxGuest.adminUser = "ops";
  networking.hostName = "nixos-guest";
  # For a fresh installation at 26.05 only. Preserve an existing guest's value.
  system.stateVersion = "26.05";

  users.users.${adminUser}.openssh.authorizedKeys.keys = [
    # Supply your own independently approved public administrator key(s).
    # Private keys, passwords, and runtime credentials never belong here.
  ];

  assertions = [
    {
      assertion = keys.keys != [ ] || keys.keyFiles != [ ];
      message = "Set the guest administrator's public SSH keys before building.";
    }
  ];
  # The composing flake MUST also import this guest's reviewed hardware and
  # network configuration. Do not import image.nix into an installed guest.
}
