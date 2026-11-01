"""Offline checks of the technitium-cluster recipe; real servers are in tests/technitium-cluster."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
LAB = ROOT / "examples/lab"
MODULE = LAB / "modules/opentofu/technitium-cluster"
SOURCES = "".join(path.read_text() for path in sorted(MODULE.glob("*.tf")))
MAIN = (MODULE / "main.tf").read_text()


class PasswordTests(unittest.TestCase):
    def test_no_password_reaches_a_resource_or_a_variable(self):
        # The provider's own attributes would put it in the saved plan and the state.
        self.assertNotIn('resource "technitium_cluster_secondary"', SOURCES)
        self.assertNotIn("primary_node_password", SOURCES.replace("in primary_node_password,", ""))
        self.assertNotRegex(SOURCES, r'variable "[a-z_]*password')
        self.assertNotRegex(SOURCES, r"^\s*(username|password|api_token)\s*=", )

    def test_nothing_in_the_root_can_run_code_of_its_own(self):
        for word in ("provisioner", 'data "external"', "local-exec", 'module "'):
            self.assertNotIn(word, SOURCES)


class ClusterTests(unittest.TestCase):
    def test_the_cluster_is_protected_from_destruction(self):
        block = re.search(r'resource "technitium_cluster" "this" \{.*?^\}', MAIN, re.S | re.M).group()
        self.assertIn("prevent_destroy = true", block)
        self.assertNotRegex(SOURCES, r"^\s*force_delete\s*=", )

    def test_the_provider_is_pinned_exactly_with_a_lock(self):
        versions = (MODULE / "versions.tf").read_text()
        self.assertIn('source  = "darkhonor/technitium"', versions)
        self.assertIn('version = "1.3.0"', versions)
        lock = (MODULE / ".terraform.lock.hcl").read_text()
        self.assertIn('provider "registry.opentofu.org/darkhonor/technitium"', lock)
        self.assertIn('version     = "1.3.0"', lock)
        self.assertRegex(lock, r'"h1:')

    def test_every_setting_with_a_foreign_default_is_set(self):
        block = re.search(r'resource "technitium_server_settings" "this" \{.*?^\}', MAIN, re.S | re.M).group()
        for setting in ("recursion", "randomize_name", "log_queries", "logging_type", "max_log_file_days",
                        "enable_dns_over_tls", "enable_dns_over_https", "allow_txt_blocking_report"):
            self.assertRegex(block, rf"(?m)^\s*{setting}\s*=", setting)

    def test_an_empty_list_is_no_resource(self):
        for resource in ("technitium_blocked_zones", "technitium_allowed_zones"):
            block = re.search(rf'resource "{resource}" "this" \{{.*?^\}}', MAIN, re.S | re.M).group()
            self.assertIn("> 0 ? 1 : 0", block)

    def test_the_addresses_the_controller_reads_exist(self):
        for address in ('resource "terraform_data" "secondary"', 'resource "terraform_data" "block_lists"',
                        'resource "terraform_data" "undeclared_entries"'):
            self.assertIn(address, MAIN)
        plan = (ROOT / "backend/src/Octopushy.Server/Orchestration/ClusterPlan.cs").read_text()
        for address in ("terraform_data.secondary[", "terraform_data.block_lists", "terraform_data.undeclared_entries"):
            self.assertIn(address, plan)

    def test_the_port_stays_the_https_one(self):
        self.assertIn(":53443", SOURCES)
        self.assertNotIn("5380", SOURCES)


class ExampleTests(unittest.TestCase):
    def test_the_example_cluster_names_its_members_password(self):
        cluster = (LAB / "labs/home/clusters/technitium/cluster.yaml").read_text()
        self.assertIn("credentials: { store: dns/technitium/admin-password }", cluster)
        self.assertIn("tls: { verify: false }", cluster)
        for guest in ("korriban/guests/krb-dns-01", "moraband/guests/mrb-dns-01"):
            text = (LAB / f"labs/home/proxmox/{guest}/guest.yaml").read_text()
            self.assertIn("admin_password: { store: dns/technitium/admin-password }", text)


if __name__ == "__main__":
    unittest.main()
