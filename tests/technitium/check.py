#!/usr/bin/env python3
"""Offline static checks; optionally check packet paths in actual Nix eval output."""

import argparse
import ipaddress
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
ROLE = ROOT / "examples/lab/modules/nixos/services/technitium.nix"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--eval-json", type=Path, help="JSON emitted by eval.nix")
args, unittest_args = parser.parse_known_args()
evaluation = json.loads(args.eval_json.read_text()) if args.eval_json else None


def rules(text):
    """Parse only our small emitted nft subset; refuse unrecognized syntax."""
    result = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line in {
            "chain input {",
            "}",
            "type filter hook input priority -10; policy accept;",
        }:
            continue
        if line == 'iifname "lo" accept':
            result.append(("loopback",))
        elif line == "tcp dport { 5380, 53443 } drop":
            result.append(("ui",))
        elif match := re.fullmatch(
            r'iifname "([^"]+)" ip daddr ([0-9.]+) ip saddr \{ ([^}]+) \} '
            r"(udp|tcp) dport (53|53443) accept",
            line,
        ):
            interface, destination, sources, protocol, port = match.groups()
            result.append(
                ("allow", interface, ipaddress.ip_address(destination),
                 tuple(ipaddress.ip_network(x.strip()) for x in sources.split(",")),
                 protocol, int(port))
            )
        elif match := re.fullmatch(r"(udp|tcp) dport 53 drop", line):
            result.append(("drop", match[1]))
        else:
            raise AssertionError(f"Unrecognized emitted rule: {line!r}")
    return result


def verdict(parsed, interface, source, destination, protocol, port, default="accept"):
    """A guard accept CONTINUES to the native host firewall, not final acceptance."""
    source = ipaddress.ip_address(source)
    destination = ipaddress.ip_address(destination)
    for rule in parsed:
        if rule[0] == "loopback" and interface == "lo":
            return "accept"
        if rule[0] == "ui" and protocol == "tcp" and port in {5380, 53443}:
            return "drop"
        if rule[0] == "allow":
            _, allowed_interface, address, networks, allowed_protocol, allowed_port = rule
            if (interface == allowed_interface and destination == address
                    and protocol == allowed_protocol and port == allowed_port
                    and any(source in network for network in networks)):
                return "accept"
        if rule[0] == "drop" and protocol == rule[1] and port == 53:
            return "drop"
    return default


class StaticSafety(unittest.TestCase):
    def test_opt_in_and_firewall_ownership(self):
        module = ROLE.read_text()
        self.assertIn('enable = mkEnableOption', module)
        self.assertRegex(module, r"exposeDNS = mkOption \{\s+type = types.bool;\s+default = false;")
        self.assertRegex(module, r"exposeCluster = mkOption \{\s+type = types.bool;\s+default = false;")
        self.assertIn("config.networking.firewall.enable && config.networking.nftables.enable", module)
        self.assertIn("!config.networking.nftables.flushRuleset", module)
        self.assertIn("networking.nftables.tables.octopushy_technitium", module)
        self.assertIn("networking.firewall.extraInputRules = allowCluster + allowDNS;", module)
        self.assertIn("config.services.technitium-dns-server.package.version or null", module)
        self.assertIn('lib.versionAtLeast packageVersion "15.6.2"', module)
        self.assertIn("assertion = !(cfg.exposeDNS || cfg.exposeCluster) || eligiblePackage;", module)
        self.assertNotRegex(
            module,
            r"allowed(?:TCP|UDP)Ports\s*=|extra(?:Stop)?Commands\s*=|flush ruleset|"
            r"networking\.(?:firewall|nftables)\.enable\s*=|mkForce|"
            r"(?:resolved|resolvconf)\s*=|ExecStart(?:Pre|Post)?\s*=|"
            r"environment\s*=|(?:password|token)\s*=",
        )

    def test_guard_order_is_fail_closed(self):
        module = ROLE.read_text()
        expected = [
            "hook input priority -10; policy accept;",
            'iifname "lo" accept',
            "${allowCluster}",
            "tcp dport { 5380, 53443 } drop",
            "${allowDNS}",
            "udp dport 53 drop",
            "tcp dport 53 drop",
        ]
        positions = [module.index(text, module.index("chain input {")) for text in expected]
        self.assertEqual(positions, sorted(positions))
        self.assertIn('bindsTo = [ "nftables.service" ];', module)

    def test_no_custom_data_lifecycle(self):
        module = ROLE.read_text()
        self.assertNotRegex(module, r"StateDirectory\s*=|WorkingDirectory\s*=|"
                            r"tmpfiles|activationScripts|preStop|postStop|remove|purge")
        self.assertIn('StateDirectoryMode = "0700";', module)
        self.assertIn('UMask = "0077";', module)
        example = (ROLE.parent.parent / "examples/technitium.nix").read_text()
        self.assertIn("exposeDNS = false;", example)
        self.assertIn('lanAddress = "192.0.2.53";', example)


@unittest.skipUnless(evaluation is not None, "pass --eval-json for evaluated packet checks")
class EvaluatedGuard(unittest.TestCase):
    def test_peer_only_cluster_paths(self):
        fixtures = evaluation["packetFixtures"]
        cluster = rules(fixtures["clusterOnly"])
        native = rules(fixtures["clusterNativeAllows"])
        combined = rules(fixtures["clusterAndClients"])
        closed = rules(fixtures["clusterClosed"])
        removed = rules(fixtures["clusterPeerRemoved"])
        peer = "192.0.2.54"
        client = "192.0.2.42"
        target = "192.0.2.53"
        for protocol, port in (("udp", 53), ("tcp", 53), ("tcp", 53443)):
            for table in (cluster, native, combined):
                self.assertEqual(verdict(table, "ens18", peer, target, protocol, port), "accept")
            for source in (client, "198.51.100.42", "203.0.113.42"):
                self.assertEqual(verdict(cluster, "ens18", source, target, protocol, port), "drop")
                self.assertEqual(
                    verdict(native, "ens18", source, target, protocol, port, default="drop"), "drop")
            for interface, source, destination in (
                ("other0", peer, target), ("trusted0", peer, target),
                ("ens18", peer, "192.0.2.99"),
                ("ens18", "2001:db8::54", "2001:db8::53"),
            ):
                self.assertEqual(verdict(cluster, interface, source, destination, protocol, port), "drop")
            self.assertEqual(verdict(closed, "ens18", peer, target, protocol, port), "drop")
            self.assertEqual(verdict(removed, "ens18", peer, target, protocol, port), "drop")
            self.assertEqual(verdict(removed, "ens18", "192.0.2.55", target, protocol, port), "accept")
        for protocol in ("udp", "tcp"):
            self.assertEqual(verdict(combined, "ens18", client, target, protocol, 53), "accept")
        self.assertEqual(verdict(combined, "ens18", client, target, "tcp", 53443), "drop")
        for table in (cluster, combined):
            for source in (peer, client):
                self.assertEqual(verdict(table, "ens18", source, target, "tcp", 5380), "drop")
            for protocol, port in (("tcp", 22), ("tcp", 8443), ("udp", 123)):
                self.assertEqual(verdict(table, "ens18", client, target, protocol, port), "accept")
            self.assertEqual(verdict(table, "lo", "127.0.0.1", "127.0.0.1", "tcp", 5380), "accept")

    def test_all_nix_assertions_passed(self):
        self.assertEqual(evaluation["packageVersion"], "15.6.0")
        self.assertTrue(evaluation["tests"])
        self.assertTrue(all(value is True for value in evaluation["tests"].values()))
        self.assertTrue(evaluation["tests"]["disabledByDefault"])
        self.assertTrue(evaluation["tests"]["ordinaryRemoval"])
        self.assertTrue(evaluation["tests"]["realUnpatchedExposureRejected"])
        self.assertTrue(evaluation["tests"]["downgradedPackageOverrideRejected"])
        self.assertTrue(evaluation["tests"]["missingPackageVersionRejected"])
        self.assertIn("TEST-ONLY", evaluation["exposureFixture"])
        self.assertIn("NOT a patched runtime", evaluation["exposureFixture"])

    def test_packet_paths(self):
        fixtures = evaluation["packetFixtures"]
        closed, exposed = rules(fixtures["bootstrap"]), rules(fixtures["exposed"])
        native = rules(fixtures["nativeAllows"])
        allowed = ("ens18", "192.0.2.10", "192.0.2.53")
        for protocol in ("udp", "tcp"):
            with self.subTest(protocol=protocol):
                self.assertEqual(verdict(closed, *allowed, protocol, 53), "drop")
                self.assertEqual(verdict(exposed, *allowed, protocol, 53), "accept")
                self.assertEqual(verdict(native, *allowed, protocol, 53, default="drop"), "accept")
                for interface, source, destination in [
                    ("ens18", "203.0.113.8", "192.0.2.53"),   # wrong source
                    ("ens19", "192.0.2.10", "192.0.2.53"),   # wrong interface
                    ("trusted0", "192.0.2.10", "192.0.2.53"),# trusted host interface
                    ("ens18", "192.0.2.10", "192.0.2.54"),   # wrong destination
                    ("ens18", "2001:db8::10", "2001:db8::53"),
                    ("ens18", "192.0.2.10", "2001:db8::53"),
                ]:
                    packet = (interface, source, destination, protocol, 53)
                    self.assertEqual(verdict(exposed, *packet), "drop")
                    self.assertEqual(verdict(native, *packet, default="drop"), "drop")
                self.assertEqual(verdict(exposed, "ens18", "198.51.100.10",
                                         "192.0.2.53", protocol, 53), "accept")
        for parsed in (closed, exposed):
            for interface in ("ens18", "trusted0"):
                for port in (5380, 53443):
                    for source, destination in [
                        ("192.0.2.10", "192.0.2.53"),
                        ("2001:db8::10", "2001:db8::53"),
                    ]:
                        self.assertEqual(verdict(parsed, interface, source, destination,
                                                 "tcp", port), "drop")
                # Guard does not alter unrelated host decisions, including SSH.
                for protocol, port in (("tcp", 22), ("tcp", 8443), ("udp", 123)):
                    self.assertEqual(verdict(parsed, interface, "192.0.2.10",
                                             "192.0.2.53", protocol, port), "accept")
            for port in (53, 5380, 53443):
                self.assertEqual(verdict(parsed, "lo", "127.0.0.1",
                                         "127.0.0.1", "tcp", port), "accept")


if __name__ == "__main__":
    unittest.main(argv=[__file__, *unittest_args])
