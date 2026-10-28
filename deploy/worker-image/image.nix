{ lib, pkgs, utils, octopushyPackage, ... }:
let
  stateDevice = "/dev/disk/by-id/opy-worker-state-v1";
  bootstrap = pkgs.writeShellApplication {
    name = "octopushy-worker-bootstrap";
    runtimeInputs = [ pkgs.util-linux ];
    text = ''
      # Only the fixed native executor calls this root-owned entrypoint via QGA.
      mountpoint -q /var/lib/octopushy || { echo "Worker state disk is not mounted" >&2; exit 1; }
      exec runuser -u octopushy -- ${octopushyPackage}/bin/octopushy \
        agent managed-identity "$@" --state-dir /var/lib/octopushy/identity
    '';
  };
  reporting = pkgs.writeShellApplication {
    name = "octopushy-worker-reporting";
    text = ''
      # No caller-selected arguments, paths or programs. Secret JSON is stdin.
      [ "$#" -eq 0 ] || exit 1
      ulimit -c 0
      exec ${pkgs.coreutils}/bin/timeout --kill-after=5s 60s \
        ${octopushyPackage}/bin/octopushy agent managed-reporting
    '';
  };
in {
  imports = [ ../../examples/lab/modules/nixos/image.nix ];
  image.baseName = lib.mkForce "octopushy-worker-v1";
  networking.hostName = lib.mkDefault "octopushy-worker";

  users.groups.octopushy.gid = 1001;
  users.users.octopushy = {
    isNormalUser = true;
    uid = 1001;
    group = "octopushy";
    home = "/var/lib/octopushy/home";
    createHome = false;
    hashedPassword = "!";
    subUidRanges = [{ startUid = 100000; count = 65536; }];
    subGidRanges = [{ startGid = 100000; count = 65536; }];
    linger = true;
  };
  virtualisation.podman = {
    enable = true;
    dockerCompat = false;
    defaultNetwork.settings.dns_enabled = true;
  };
  environment.systemPackages = [ octopushyPackage bootstrap reporting pkgs.fuse-overlayfs pkgs.slirp4netns pkgs.xz ];

  # Runtime material is never a derivation input or part of the image. Both
  # units are inert on an unconfigured image; the fixed helper publishes private
  # credentials atomically, then waits for the first real HTTPS/204 delivery.
  systemd.services.octopushy-disk-reporter = {
    description = "Report root filesystem usage to Octopushy";
    wants = [ "network-online.target" ];
    after = [ "network-online.target" ];
    unitConfig.ConditionPathExists = "/var/lib/octopushy-disk-reporter/current/token";
    serviceConfig = {
      Type = "oneshot";
      ExecStart = "${octopushyPackage}/bin/octopushy-disk-reporter report %d/config.json";
      LoadCredential = [
        "config.json:/var/lib/octopushy-disk-reporter/current/config.json"
        "token:/var/lib/octopushy-disk-reporter/current/token"
        "ca.pem:/var/lib/octopushy-disk-reporter/current/ca.pem"
      ];
      DynamicUser = true;
      NoNewPrivileges = true;
      ProtectSystem = "strict";
      ProtectHome = true;
      PrivateTmp = true;
      PrivateDevices = true;
      ProtectKernelTunables = true;
      ProtectKernelModules = true;
      ProtectControlGroups = true;
      RestrictSUIDSGID = true;
      LockPersonality = true;
      MemoryDenyWriteExecute = true;
      CapabilityBoundingSet = "";
      RestrictAddressFamilies = [ "AF_UNIX" "AF_INET" "AF_INET6" ];
      UMask = "0077";
      LimitCORE = 0;
      TimeoutStartSec = "30s";
      # Never journal remote or parser diagnostics which could include credentials.
      StandardOutput = "null";
      StandardError = "null";
    };
  };
  systemd.timers.octopushy-disk-reporter = {
    wantedBy = [ "timers.target" ];
    unitConfig.ConditionPathExists = "/var/lib/octopushy-disk-reporter/current/token";
    # Native-v1 explicitly supports only hourly cadence. The controller and
    # helper reject other recorded intervals; this remains correct after reboot.
    timerConfig = {
      OnBootSec = "60s";
      OnUnitActiveSec = "3600s";
      AccuracySec = "1s";
      RandomizedDelaySec = 0;
    };
  };

  # Proxmox supplies device_id=drive-scsi1 independently of serial=. The stock
  # SCSI by-id name follows device_id (VPD 0x83), not the disk's serial (0x80).
  # Match the recipe's exact serial instead; never select an arbitrary disk.
  services.udev.extraRules = ''
    SUBSYSTEM=="block", ENV{DEVTYPE}=="disk", ENV{ID_BUS}=="scsi", ENV{ID_SCSI_SERIAL}=="opy-worker-state-v1", SYMLINK+="disk/by-id/opy-worker-state-v1"
  '';

  # A dedicated serial from the fixed v1 recipe identifies scsi1. Never discover
  # an arbitrary spare disk or format an existing filesystem.
  systemd.services.octopushy-worker-state = {
    description = "Initialize only a completely blank worker state disk";
    wantedBy = [ "local-fs.target" ];
    before = [ "var-lib-octopushy.mount" ];
    after = [ "${utils.escapeSystemdPath stateDevice}.device" ];
    requires = [ "${utils.escapeSystemdPath stateDevice}.device" ];
    unitConfig.DefaultDependencies = false;
    serviceConfig = { Type = "oneshot"; RemainAfterExit = true; TimeoutStartSec = "infinity"; };
    path = [ pkgs.coreutils pkgs.diffutils pkgs.util-linux pkgs.e2fsprogs ];
    script = ''
      set -eu
      device=${stateDevice}
      type="$(blkid -p -s TYPE -o value "$device" || true)"
      label="$(blkid -p -s LABEL -o value "$device" || true)"
      if [ "$type" = ext4 ] && [ "$label" = opy-worker-state ]; then exit 0; fi
      [ -z "$(wipefs --no-act --noheadings --output TYPE "$device")" ] || {
        echo "Refusing a foreign/nonempty worker state disk" >&2; exit 1;
      }
      # Deliberately scan ALL bytes, not just filesystem signatures, before the
      # one-time format. A reused or partially initialized disk fails closed.
      bytes="$(blockdev --getsize64 "$device")"
      [ "$bytes" -ge 17179869184 ]
      cmp --bytes="$bytes" "$device" /dev/zero || {
        echo "Refusing a nonblank worker state disk" >&2; exit 1;
      }
      mkfs.ext4 -q -L opy-worker-state "$device"
    '';
  };
  fileSystems."/var/lib/octopushy" = {
    # This device already exists before initialization. Waiting for the label
    # races the device-job timeout against the full blank-disk scan.
    device = stateDevice;
    fsType = "ext4";
    # Do not run generated fsck before the initializer has created ext4.
    # Existing ext4 journal recovery remains part of the mount.
    noCheck = true;
    options = [ "nodev" "nosuid" "x-systemd.requires=octopushy-worker-state.service" ];
  };
  systemd.services.octopushy-worker-directories = {
    wantedBy = [ "multi-user.target" ];
    before = [ "octopushy-worker.path" ];
    requires = [ "var-lib-octopushy.mount" ];
    after = [ "var-lib-octopushy.mount" ];
    # The path unit precedes basic.target. This tiny filesystem initializer
    # must not gain the default service After=basic.target dependency.
    unitConfig.DefaultDependencies = false;
    serviceConfig = { Type = "oneshot"; RemainAfterExit = true; };
    script = ''
      ${pkgs.coreutils}/bin/install -d -m 0700 -o octopushy -g octopushy \
        /var/lib/octopushy/identity /var/lib/octopushy/home
    '';
  };
  systemd.paths.octopushy-worker = {
    wantedBy = [ "multi-user.target" ];
    requires = [ "octopushy-worker-directories.service" ];
    after = [ "octopushy-worker-directories.service" ];
    pathConfig.PathExists = "/var/lib/octopushy/identity/agent.json";
  };
  systemd.services.octopushy-worker = {
    description = "Octopushy managed worker (enroll before starting)";
    wants = [ "network-online.target" ];
    after = [ "network-online.target" "octopushy-worker-directories.service" "user@1001.service" ];
    requires = [ "octopushy-worker-directories.service" "user@1001.service" ];
    unitConfig.ConditionPathExists = "/var/lib/octopushy/identity/agent.json";
    path = [ pkgs.podman pkgs.shadow pkgs.fuse-overlayfs pkgs.slirp4netns ];
    environment = {
      HOME = "/var/lib/octopushy/home";
      XDG_RUNTIME_DIR = "/run/user/1001";
      # Rootless Podman needs NixOS's setuid newuidmap/newgidmap wrappers,
      # not the unprivileged copies from the shadow package in service.path.
      PATH = lib.mkForce "/run/wrappers/bin:${lib.makeBinPath [ pkgs.podman pkgs.shadow pkgs.fuse-overlayfs pkgs.slirp4netns pkgs.coreutils pkgs.util-linux ]}";
    };
    serviceConfig = {
      Type = "simple";
      User = "octopushy";
      Group = "octopushy";
      ExecStart = "${octopushyPackage}/bin/octopushy agent run --state-dir /var/lib/octopushy/identity --tool-runtime podman --container-network host";
      Restart = "on-failure";
      RestartSec = 5;
      Delegate = true;
      UMask = "0077";
      TimeoutStopSec = 300;
      KillMode = "mixed";
    };
  };
}
