{ config, modulesPath, ... }:
{
  imports = [
    ./proxmox-guest.nix
    (modulesPath + "/virtualisation/proxmox-image.nix")
  ];

  # Initial installation baseline only; never use this to upgrade an existing
  # guest's stateVersion or replace its hardware configuration.
  system.stateVersion = "26.05";
  image.baseName = "nixos-proxmox-bootstrap";
  virtualisation.diskSize = 8192;
  proxmox = {
    partitionTableType = "efi";
    qemuConf.bios = "ovmf";
    qemuExtraConf.machine = "q35";
    cloudInit.enable = true;
  };
  services.cloud-init.settings = {
    datasource_list = [ "NoCloud" ];
    disable_root = true;
    ssh_pwauth = false;
    system_info.default_user = {
      name = config.lab.proxmoxGuest.adminUser;
      groups = [ "wheel" ];
      sudo = [ "ALL=(ALL) NOPASSWD:ALL" ];
      lock_passwd = true;
    };
  };
  # Cloud-init can replace the host keys after sshd has read them. Do not
  # advertise a stale in-memory key or accept logins before account setup ends.
  systemd.services.sshd = {
    after = [ "cloud-final.service" ];
    wants = [ "cloud-final.service" ];
  };
}
