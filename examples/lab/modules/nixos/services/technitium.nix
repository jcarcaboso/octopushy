{ config, lib, ... }:
let
  cfg = config.octopushy.services.technitium;
  inherit (lib) mkEnableOption mkIf mkOption types;
  packageVersion = config.services.technitium-dns-server.package.version or null;
  # Minimum for known upstream fixes, NOT an assertion that all newer versions
  # are secure/reviewed. Unknown/prerelease metadata fails closed; no bypass.
  eligiblePackage = builtins.isString packageVersion
    && builtins.match "(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" packageVersion != null
    && lib.versionAtLeast packageVersion "15.6.2";

  # Strict numeric input: these values are interpolated into nft syntax.
  ipv4 = value:
    builtins.match "(0|[1-9][0-9]{0,2})(\\.(0|[1-9][0-9]{0,2})){3}" value != null
    && lib.all (part: builtins.fromJSON part <= 255) (lib.splitString "." value);
  # ponytail: IPv4 private/documentation LANs only; add IPv6/public LAN support
  # only with corresponding validation and packet-path tests.
  lanPrefix = value:
    let
      octets = map builtins.fromJSON (lib.splitString "." value);
      a = builtins.elemAt octets 0;
      b = builtins.elemAt octets 1;
      c = builtins.elemAt octets 2;
    in
      if a == 10 then 8
      else if a == 172 && b >= 16 && b <= 31 then 12
      else if a == 192 && b == 168 then 16
      else if (a == 192 && b == 0 && c == 2)
        || (a == 198 && b == 51 && c == 100)
        || (a == 203 && b == 0 && c == 113) then 24
      else null;
  lanAddress = types.addCheck types.str (value: ipv4 value && lanPrefix value != null);
  lanNetwork = types.addCheck types.str (value:
    let
      parts = lib.splitString "/" value;
      address = builtins.head parts;
      prefix = lib.last parts;
    in
      builtins.length parts == 2 && ipv4 address && lanPrefix address != null
      && builtins.match "([1-9]|[12][0-9]|3[0-2])" prefix != null
      && builtins.fromJSON prefix >= lanPrefix address
  );
  interface = types.addCheck (types.strMatching "[a-zA-Z0-9][a-zA-Z0-9_.-]{0,14}")
    (value: value != "lo");
  # Invalid/missing values fail assertions/types; placeholders keep the missing
  # option diagnostic readable instead of failing string interpolation first.
  matchSources = sources: ''iifname "${if cfg.lanInterface == null then "UNSET" else cfg.lanInterface}" ip daddr ${if cfg.lanAddress == null then "0.0.0.0" else cfg.lanAddress} ip saddr { ${lib.concatStringsSep ", " sources} }'';
  allowDNS = lib.optionalString cfg.exposeDNS ''
    ${matchSources cfg.allowedNetworks} udp dport 53 accept
    ${matchSources cfg.allowedNetworks} tcp dport 53 accept
  '';
  # Peer traffic is independent from admitting LAN clients. It grants access
  # to the HTTPS UI/API as well as cluster endpoints: TLS and auth still matter.
  allowCluster = lib.optionalString cfg.exposeCluster ''
    ${matchSources cfg.clusterPeers} udp dport 53 accept
    ${matchSources cfg.clusterPeers} tcp dport 53 accept
    ${matchSources cfg.clusterPeers} tcp dport 53443 accept
  '';
in
{
  options.octopushy.services.technitium = {
    enable = mkEnableOption "the optional native Technitium role on a dedicated VM";
    lanInterface = mkOption {
      type = types.nullOr interface;
      default = null;
      example = "ens18";
      description = "Exact ingress interface for LAN DNS; never a wildcard or loopback.";
    };
    lanAddress = mkOption {
      type = types.nullOr lanAddress;
      default = null;
      example = "192.0.2.53";
      description = "Existing destination IPv4 address; does not configure networking. DHCP tests must reconcile rules and cluster endpoints if the lease address changes.";
    };
    allowedNetworks = mkOption {
      type = types.listOf lanNetwork;
      default = [ ];
      example = [ "192.0.2.0/24" ];
      description = "Explicit IPv4 source CIDRs within RFC1918 or documentation networks; no default route.";
    };
    exposeDNS = mkOption {
      type = types.bool;
      default = false;
      description = ''
        Admit LAN UDP and TCP 53 only after an operator has changed the default
        administrator password and verified listeners, recursion ACLs, and
        persistent application configuration through protected local access.
        This is an operator attestation, not automated configuration verification.
      '';
    };
    clusterPeers = mkOption {
      type = types.listOf lanAddress;
      default = [ ];
      example = [ "192.0.2.54" ];
      description = "Other members' exact IPv4 addresses, not subnets. Prefer stable addresses; DHCP changes require explicit reconciliation. Declaring peers does not initialize or join a cluster.";
    };
    exposeCluster = mkOption {
      type = types.bool;
      default = false;
      description = ''
        Admit only declared peers on UDP/TCP 53 and HTTPS TCP 53443 after
        credentials, TLS identity, listeners, and a patched package are verified.
        HTTP 5380 remains loopback-only. This does not enable HTTPS, configure
        membership, or verify application readiness; client DNS is a separate opt-in.
      '';
    };
  };

  config = mkIf cfg.enable {
    assertions = [
      {
        assertion = config.networking.firewall.enable && config.networking.nftables.enable;
        message = "Technitium requires the baseline NixOS firewall and nftables enabled; the role will not switch the host backend.";
      }
      {
        assertion = !config.networking.nftables.flushRuleset;
        message = "Technitium requires networking.nftables.flushRuleset = false; do not flush unrelated host tables.";
      }
      {
        assertion = cfg.lanInterface != null && cfg.lanAddress != null && cfg.allowedNetworks != [ ];
        message = "Technitium requires explicit lanInterface, lanAddress, and nonempty allowedNetworks, even during closed bootstrap.";
      }
      {
        assertion = !config.services.technitium-dns-server.openFirewall;
        message = "Use the Technitium role's source/destination/interface rules, not native openFirewall.";
      }
      {
        assertion = !(cfg.exposeDNS || cfg.exposeCluster) || eligiblePackage;
        message = "Technitium DNS or cluster exposure requires the actual services.technitium-dns-server.package to declare a numeric version >= 15.6.2 for known upstream fixes (missing/older/prerelease metadata fails closed). This minimum is not a security guarantee; review the package before exposure.";
      }
      {
        assertion = !cfg.exposeCluster || cfg.clusterPeers != [ ];
        message = "Technitium cluster exposure requires explicit peer IPs; no wildcard management access.";
      }
      {
        assertion = !(lib.elem cfg.lanAddress cfg.clusterPeers)
          && builtins.length cfg.clusterPeers == builtins.length (lib.unique cfg.clusterPeers);
        message = "Technitium cluster peers must be distinct other members, not this guest's own address.";
      }
    ];

    services.technitium-dns-server = {
      enable = true;
      openFirewall = lib.mkDefault false;
    };

    # Drop decisions in an earlier base chain survive accepts in other tables
    # (including trustedInterfaces and established connections in nixos-fw).
    # Only these owned ports are affected. Non-standard listeners are out of scope.
    networking.nftables.tables.octopushy_technitium = {
      family = "inet";
      content = ''
        chain input {
          type filter hook input priority -10; policy accept;
          iifname "lo" accept
          ${allowCluster}
          tcp dport { 5380, 53443 } drop
          ${allowDNS}
          udp dport 53 drop
          tcp dport 53 drop
        }
      '';
    };
    # An accept above does not bypass the later NixOS firewall's default drop.
    # Merge only our narrow allows; preserve all unrelated host policy.
    networking.firewall.extraInputRules = allowCluster + allowDNS;

    systemd.services.technitium-dns-server = {
      after = [ "nftables.service" ];
      bindsTo = [ "nftables.service" ];
      serviceConfig = {
        StateDirectoryMode = "0700";
        LogsDirectoryMode = "0700";
        UMask = "0077";
      };
    };
  };
}
