{
  description = "Native NixOS guest preparation, not an Octopushy executor";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a";

  outputs =
    { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
      bootstrap = nixpkgs.lib.nixosSystem {
        inherit system;
        modules = [ ./image.nix ];
      };
      # Evaluation only: not a deployable host or a hardware inventory.
      testGuest = nixpkgs.lib.nixosSystem {
        inherit system;
        modules = [
          self.nixosModules.proxmox-guest
          {
            lab.proxmoxGuest.adminUser = "deployer";
            system.stateVersion = "26.05";
          }
        ];
      };
      guest = testGuest.config;
      image = bootstrap.config;
      # Configuration-only composition fixture; never exported as a deployable host.
      dnsGuest = (testGuest.extendModules {
        modules = [
          ./examples/technitium.nix
          { boot.isContainer = true; }
        ];
      }).config;
      userType = testGuest.options.lab.proxmoxGuest.adminUser.type;
      exampleAssertions = (import ./examples/guest.nix { config = guest; }).assertions;
    in
    {
      nixosModules.proxmox-guest = import ./proxmox-guest.nix;
      nixosModules.proxmox-image = import ./image.nix;
      nixosModules.service-node = import ./service-node.nix;
      nixosModules.portainer = import ./services/portainer.nix;
      nixosModules.technitium = import ./services/technitium.nix;
      nixosConfigurations.bootstrap = bootstrap;

      # An image build is separate from configuring an installed guest.
      packages.${system}.bootstrap-image = image.system.build.cloudImage;

      checks.${system} = {
        guest-policy =
          assert userType.check "deployer";
          assert !(userType.check "root");
          assert !(userType.check "ops;echo unsafe");
          assert !(builtins.head exampleAssertions).assertion;
          assert guest.users.users.deployer.isNormalUser;
          assert builtins.elem "wheel" guest.users.users.deployer.extraGroups;
          assert !guest.security.sudo.wheelNeedsPassword;
          assert guest.services.openssh.enable;
          assert guest.services.openssh.settings.AllowUsers == [ "deployer" ];
          assert guest.services.openssh.settings.PermitRootLogin == "no";
          assert !guest.services.openssh.settings.PasswordAuthentication;
          assert !guest.services.openssh.settings.KbdInteractiveAuthentication;
          assert guest.users.users.root.hashedPassword == "!";
          assert guest.services.qemuGuest.enable;
          assert guest.networking.firewall.enable;
          assert guest.networking.nftables.enable;
          assert !guest.networking.nftables.flushRuleset;
          assert guest.networking.firewall.allowedTCPPorts == [ 22 ];
          assert guest.networking.firewall.allowedUDPPorts == [ ];
          assert guest.nix.settings.trusted-users == [ "root" ];
          assert guest.system.tools.nixos-rebuild.enable;
          assert !guest.services.cloud-init.enable;
          pkgs.runCommand "native-nixos-guest-policy" { } ''
            touch "$out"
          '';

        image-policy =
          assert image.proxmox.partitionTableType == "efi";
          assert image.proxmox.qemuConf.bios == "ovmf";
          assert image.proxmox.qemuExtraConf.machine == "q35";
          assert image.proxmox.cloudInit.enable;
          assert image.services.cloud-init.enable;
          assert builtins.elem "cloud-final.service" image.systemd.services.sshd.after;
          assert image.services.cloud-init.settings.system_info.default_user.name == "ops";
          assert image.services.cloud-init.settings.datasource_list == [ "NoCloud" ];
          assert !image.services.cloud-init.settings.ssh_pwauth;
          assert image.services.qemuGuest.enable;
          assert image.virtualisation.diskSize == 8192;
          assert image.image.baseName == "nixos-proxmox-bootstrap";
          assert image.system.stateVersion == "26.05";
          assert nixpkgs.lib.isDerivation image.system.build.cloudImage;
          pkgs.runCommand "native-nixos-image-policy" { } ''
            touch "$out"
          '';

        technitium-composition =
          assert nixpkgs.lib.all (entry: entry.assertion) dnsGuest.assertions;
          assert !guest.services.technitium-dns-server.enable;
          assert !image.services.technitium-dns-server.enable;
          assert dnsGuest.services.technitium-dns-server.enable;
          assert !dnsGuest.octopushy.services.technitium.exposeDNS;
          assert dnsGuest.networking.firewall.enable;
          assert dnsGuest.networking.nftables.enable;
          assert dnsGuest.networking.firewall.allowedTCPPorts == [ 22 ];
          assert dnsGuest.networking.firewall.allowedUDPPorts == [ ];
          assert dnsGuest.networking.firewall.extraInputRules == "";
          assert dnsGuest.networking.nftables.tables ? octopushy_technitium;
          pkgs.runCommand "native-nixos-technitium-composition" { } ''
            touch "$out"
          '';
      };
    };
}
