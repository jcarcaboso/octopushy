{ config, lib, modulesPath, ... }:
let
  adminUser = config.lab.proxmoxGuest.adminUser;
in
{
  imports = [ (modulesPath + "/profiles/qemu-guest.nix") ];

  options.lab.proxmoxGuest.adminUser = lib.mkOption {
    type = lib.types.addCheck (lib.types.strMatching "[a-z_][a-z0-9_-]*") (
      name: name != "root"
    );
    default = "ops";
    description = ''
      Dedicated key-only administrator. Match the provisioner's admin_user;
      configure its public keys through users.users.<name>.openssh.authorizedKeys.
      This user has passwordless sudo and is not added to nix.settings.trusted-users.
    '';
  };

  config = {
    boot.initrd.availableKernelModules = [
      "sd_mod"
      "virtio_scsi"
    ];
    services.qemuGuest.enable = true;

    users.users.${adminUser} = {
      isNormalUser = true;
      extraGroups = [ "wheel" ];
    };
    users.users.root.hashedPassword = lib.mkDefault "!";
    security.sudo.wheelNeedsPassword = false;

    services.openssh = {
      enable = true;
      openFirewall = true;
      settings = {
        AllowUsers = [ adminUser ];
        PermitRootLogin = "no";
        PasswordAuthentication = false;
        KbdInteractiveAuthentication = false;
      };
    };
    networking.firewall.enable = true;
    networking.nftables = {
      enable = true;
      flushRuleset = false;
    };

    nix.settings.experimental-features = [
      "nix-command"
      "flakes"
    ];
    # Deliberately no filesystem, stateVersion, service, secret, or host inventory.
  };
}
