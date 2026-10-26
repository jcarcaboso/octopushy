{
  description = "Octopushy versioned managed-worker image";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/f3a73bcf5d1c5b75a66cf35a48957e30d41d647a";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      agent = pkgs.rustPlatform.buildRustPackage {
        pname = "octopushy";
        version = "0.1.0-worker-v1";
        src = pkgs.lib.cleanSourceWith {
          src = ./.;
          filter = path: type:
            let relative = pkgs.lib.removePrefix (toString ./. + "/") (toString path);
            in relative == "agent" || relative == "contracts"
              || (pkgs.lib.hasPrefix "agent/" relative && !(pkgs.lib.hasPrefix "agent/target" relative))
              || pkgs.lib.hasPrefix "contracts/agent" relative;
        };
        cargoRoot = "agent";
        buildAndTestSubdir = "agent";
        cargoLock.lockFile = ./agent/Cargo.lock;
        cargoBuildFlags = [ "-p" "octopushy" ];
        cargoTestFlags = [ "-p" "octopushy" "--lib" ];
        # Cargo and runtime tests both need a writable private HOME. Do this
        # before any phase: a check-only override leaves cargo's earlier cache
        # in /homeless-shelter and Nix refuses subsequent unsandboxed builders.
        preUnpack = ''
          export HOME="$TMPDIR/octopushy-build-home"
          mkdir -p "$HOME"
        '';
      };
      image = nixpkgs.lib.nixosSystem {
        inherit system;
        specialArgs = { octopushyPackage = agent; };
        modules = [ ./deploy/worker-image/image.nix ];
      };
    in {
      packages.${system} = {
        inherit agent;
        default = agent;
        worker-image-v1 = image.config.system.build.cloudImage;
        qemu = pkgs.qemu;
        firmware = pkgs.OVMF.fd;
        smoke-python = pkgs.python3;
      };
      nixosConfigurations.worker-v1 = image;
      checks.${system}.worker-image-policy =
        assert image.config.services.qemuGuest.enable;
        assert image.config.virtualisation.podman.enable;
        assert !image.config.virtualisation.docker.enable;
        assert image.config.users.users.octopushy.isNormalUser;
        assert image.config.users.users.octopushy.uid == 1001;
        assert image.config.systemd.services.octopushy-worker.serviceConfig.User == "octopushy";
        assert image.config.systemd.services.octopushy-worker.serviceConfig.ExecStart
          == "${agent}/bin/octopushy agent run --state-dir /var/lib/octopushy/identity --tool-runtime podman --container-network host";
        assert image.config.systemd.paths.octopushy-worker.pathConfig.PathExists
          == "/var/lib/octopushy/identity/agent.json";
        assert image.config.fileSystems."/var/lib/octopushy".device
          == "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_opy-worker-state-v1";
        assert image.config.fileSystems."/var/lib/octopushy".noCheck;
        assert image.config.systemd.services.octopushy-worker-state.serviceConfig.TimeoutStartSec == "infinity";
        assert image.config.proxmox.partitionTableType == "efi";
        assert image.config.system.stateVersion == "26.05";
        pkgs.runCommand "worker-image-v1-policy" { } ''touch "$out"'';
    };
}
