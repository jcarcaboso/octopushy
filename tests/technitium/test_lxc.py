"""Offline checks of the technitium-lxc recipe; the install itself runs in tests/technitium-lxc."""
import json
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
LAB = ROOT / "examples/lab"
MODULE = LAB / "modules/opentofu/technitium-lxc"
BASE = LAB / "modules/opentofu/proxmox-lxc"
INSTALL = (MODULE / "install.sh").read_text()
GUESTS = [
    LAB / "labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml",
    LAB / "labs/home/proxmox/moraband/guests/mrb-dns-01/guest.yaml",
]


class PasswordTests(unittest.TestCase):
    def test_the_variable_is_ephemeral_and_unset_while_planning(self):
        variables = (MODULE / "variables.tf").read_text()
        block = re.search(r'variable "admin_password" \{.*?^\}', variables, re.S | re.M).group()
        self.assertIn("ephemeral   = true", block)
        self.assertIn("default     = null", block)

    def test_it_only_reaches_the_guest_sealed(self):
        # OpenTofu cannot check who answers on SSH, so what it uploads must be useless to anyone
        # but the container Proxmox created.
        sources = (MODULE / "main.tf").read_text() + (MODULE / "outputs.tf").read_text()
        self.assertEqual(sources.count("var.admin_password"), 1)
        self.assertNotRegex(sources, r"content\s*=\s*var\.admin_password")
        self.assertIn("admin-password.sealed", sources)
        self.assertNotRegex(sources, r'destination\s*=\s*"\$\{local\.work\}/admin-password"')

    def test_the_sealing_key_goes_only_through_the_proxmox_api(self):
        main = (MODULE / "main.tf").read_text()
        self.assertIn("octopushy-bootstrap=${local.bootstrap}", main)
        block = re.search(r'module "container" \{.*?^\}', main, re.S | re.M).group()
        self.assertIn("extra_ssh_keys", block)
        self.assertIn("local.bootstrap_key", block)
        install = re.search(r'resource "terraform_data" "install" \{.*?^\}', main, re.S | re.M).group()
        self.assertNotRegex(install, r"(?m)^[^#]*BOOTSTRAP")
        self.assertIn("SEAL_NONCE", install)

    def test_the_guest_unseals_it_with_what_proxmox_gave_it(self):
        self.assertIn("/root/.ssh/authorized_keys", INSTALL)
        self.assertIn("octopushy-bootstrap=", INSTALL)
        self.assertRegex(INSTALL, r"(?m)^unseal\(\)")
        self.assertIn("fail \"the bootstrap key is missing", INSTALL)

    def test_the_seal_opens(self):
        """The bash half of the seal against the same construction as the HCL half."""
        import base64, hashlib, subprocess, tempfile
        key, nonce = "ab" * 32, "2026-11-01T00:00:00Z"
        alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
        for secret in ("A test password, with & = + % in it", "x" * 12, "é-unicode-ü-secret", "y" * 200):
            plain = base64.b64encode(secret.encode()).decode().rstrip("=")
            sealed = ""
            for i, c in enumerate(plain):
                block = base64.b64encode(hashlib.sha256(f"{key}:{nonce}:{i // 40}".encode()).digest()).decode()
                sealed += alphabet[(alphabet.index(c) + alphabet.index(block[i % 40])) % 64]
            self.assertNotIn(plain[:12], sealed)
            function = re.search(r"(?ms)^unseal\(\) \{.*?^\}", INSTALL).group()
            with tempfile.TemporaryDirectory() as work:
                Path(work, "sealed").write_text(sealed)
                script = f"set -euo pipefail\n{function}\nunseal {work}/sealed {key} {nonce} {work}/plain\n"
                subprocess.run(["bash", "-c", script], check=True)
                self.assertEqual(Path(work, "plain").read_bytes(), secret.encode())

    def test_the_file_goes_to_curl_as_a_file(self):
        self.assertIn('--data-urlencode "pass@$password"', INSTALL)
        self.assertNotRegex(INSTALL, r"\$\(cat \"?\$password")
        self.assertNotIn("DNS_SERVER_ADMIN_PASSWORD=", INSTALL)

    def test_a_failed_guard_keeps_dns_from_starting(self):
        # Wants= would start DNS anyway, with plain HTTP open to the network.
        unit = re.search(r"unit_text=.*?^UNIT", INSTALL, re.S | re.M).group()
        self.assertRegex(unit, r"(?m)^Requires=dns-http-guard\.service$")
        self.assertRegex(unit, r"(?m)^After=.*dns-http-guard\.service")
        self.assertNotRegex(unit, r"(?m)^Wants=.*dns-http-guard")

    def test_installing_again_does_not_restart_dns_through_its_guard(self):
        # Requires= passes a restart of the guard on to DNS; a reload stays where it is.
        self.assertIn("ExecReload=/usr/sbin/nft -f /etc/dns-http-guard.nft", INSTALL)
        self.assertIn("systemctl reload dns-http-guard.service", INSTALL)
        self.assertNotRegex(INSTALL, r"systemctl (try-)?restart dns-http-guard")

    def test_the_certificate_names_the_address_so_it_can_be_pinned(self):
        # A certificate with only a common name cannot anchor the provider's connection.
        self.assertIn("subjectAltName=IP:$address", INSTALL)
        self.assertIn("DNS_SERVER_WEB_SERVICE_TLS_CERTIFICATE_PATH", INSTALL)
        self.assertNotIn("DNS_SERVER_WEB_SERVICE_USE_SELF_SIGNED_CERT=true", INSTALL)

    def test_a_second_first_start_keeps_the_certificate_dns_config_can_open(self):
        # dns.config keeps the password, and the variables are read only when it is absent.
        self.assertIn('if [[ ! -e $config/dns.config || ! -e $certificate ]]; then', INSTALL)

    def test_both_logins_are_checked_and_the_file_is_removed_on_exit(self):
        self.assertIn("trap cleanup EXIT", INSTALL)
        self.assertIn('rm -rf "$work"', INSTALL)
        self.assertIn("user=admin&pass=admin", INSTALL)
        self.assertIn("the intended admin password does not work", INSTALL)


class InstallTests(unittest.TestCase):
    def test_upstreams_installer_and_resolv_conf_are_left_alone(self):
        self.assertNotRegex(INSTALL, r"(curl|wget)[^\n]*dns/install\.sh")
        self.assertNotRegex(INSTALL, r">>?\s*/etc/resolv\.conf")
        self.assertNotRegex(INSTALL, r"(rm|mv|cp)\b[^\n]*/etc/resolv\.conf")

    def test_etc_dns_must_be_a_mount_point_and_is_never_removed(self):
        self.assertIn('mountpoint -q "$config"', INSTALL)
        self.assertEqual(re.findall(r"rm -\w+ \"\$config[^\n]*", INSTALL), ['rm -f "$config/auth.config"'])

    def test_every_download_is_pinned(self):
        self.assertIn('"$tool" -c --quiet -', INSTALL)
        urls = re.findall(r"https://[^\"\s]+", INSTALL)
        self.assertEqual(
            [url for url in urls if "127.0.0.1" not in url],
            [
                "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$runtime_version/aspnetcore-runtime-$runtime_version-linux-x64.tar.gz",
                "https://download.technitium.com/dns/archive/$version/DnsServerPortable.tar.gz",
            ],
        )

    def test_the_unit_depends_on_nothing_it_provides(self):
        unit = re.search(r"\[Unit\]\nDescription=Technitium DNS Server.*?\[Service\]", INSTALL, re.S).group()
        # Only its own guard, which is the point of that guard.
        self.assertEqual(re.findall(r"(?m)^Requires=.*", unit), ["Requires=dns-http-guard.service"])
        self.assertNotIn("BindsTo=", unit)
        self.assertNotIn("nss-lookup", unit)


class ContainerTests(unittest.TestCase):
    def test_the_module_sets_what_a_guest_must_not_change(self):
        main = (BASE / "main.tf").read_text()
        for setting in (r"unprivileged\s+= true", r"nesting = true", r"keyctl\s+= false", r"protection = true",
                        r"start_on_boot = true", r'content_type\s+= "vztmpl"'):
            self.assertRegex(main, setting)

    def test_the_technitium_root_reuses_the_module(self):
        self.assertIn('source = "../proxmox-lxc"', (MODULE / "main.tf").read_text())
        recipe = (LAB / "recipes/technitium-lxc/recipe.yaml").read_text()
        self.assertIn("modules: [../../modules/opentofu/proxmox-lxc]", recipe)
        self.assertFalse(list(MODULE.glob("*container*")))

    def test_port_5380_is_never_opened(self):
        rules = re.findall(r'dport = "(\d+)"', (MODULE / "main.tf").read_text())
        self.assertEqual(rules, ["53", "53", "53443", "22"])

    def test_lock_files_hold_the_pinned_providers(self):
        for root, providers in ((BASE, ["bpg/proxmox"]), (MODULE, ["bpg/proxmox", "hashicorp/tls"])):
            lock = (root / ".terraform.lock.hcl").read_text()
            for provider in providers:
                self.assertIn(f'registry.opentofu.org/{provider}"', lock)


class PinTests(unittest.TestCase):
    def test_the_defaults_match_the_example_and_the_docs(self):
        variables = (MODULE / "variables.tf").read_text()
        defaults = dict(re.findall(r'(\w+)\s+= optional\(string, "([^"]+)"\)', variables))
        guest = GUESTS[0].read_text()
        docs = (ROOT / "docs/technitium-lxc.md").read_text()
        for name, value in (("version", defaults["version"]), ("sha256", defaults["sha256"]),
                            ("aspnetcoreVersion", defaults["aspnetcoreVersion"]),
                            ("aspnetcoreSha512", defaults["aspnetcoreSha512"])):
            self.assertIn(f"{name}: {value}", guest)
            self.assertIn(f"{name}: {value}", docs)
        self.assertEqual(defaults["version"], "15.6.2")

    def test_the_schema_requires_static_addresses_and_etc_dns(self):
        schema = json.loads((LAB / "recipes/technitium-lxc/inputs.schema.json").read_text())
        self.assertEqual(schema["properties"]["network"]["properties"]["mode"]["const"], "static")
        self.assertEqual(
            schema["properties"]["hardware"]["properties"]["mountPoints"]["contains"]["properties"]["path"]["const"],
            "/etc/dns",
        )

    def test_guests_use_unused_vmids_and_documentation_addresses(self):
        vmids = [re.search(r"^vmid: (\d+)", path.read_text(), re.M).group(1) for path in GUESTS]
        others = [re.search(r"^vmid: (\d+)", path.read_text(), re.M).group(1)
                  for path in (LAB / "labs").rglob("guest.yaml") if path not in GUESTS]
        self.assertEqual(len(set(vmids)), 2)
        self.assertFalse(set(vmids) & set(others))
        for path in GUESTS:
            self.assertRegex(path.read_text(), r"address: 10\.1\.0\.\d+/24")


if __name__ == "__main__":
    unittest.main()
