"""Offline checks of the lxc-app module and its guest runner; tests/technitium-lxc and tests/ha-db
install through it for real."""
import base64
import hashlib
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
MODULE = ROOT / "examples/lab/modules/opentofu/lxc-app"
MAIN = (MODULE / "main.tf").read_text()
VARIABLES = (MODULE / "variables.tf").read_text()
RUNNER = (MODULE / "guest/run.sh").read_text()
ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
KEY = "ab" * 32
IDENTITY = "ha-db/10.15.30.40"
NONCE = "0e7f2c9a-6f7e-4f7e-8f7e-0123456789ab"


def seal(secrets, key=KEY, nonce=NONCE, identity=IDENTITY):
    """The HCL half of the seal, in Python, to hold the bash half to."""
    sha = lambda text: hashlib.sha256(text.encode()).hexdigest()
    encrypt, authenticate = sha(f"octopushy-seal-encrypt:{key}"), sha(f"octopushy-seal-authenticate:{key}")
    lines = "\n".join(f"{name}:{base64.b64encode(value.encode()).decode()}" for name, value in sorted(secrets.items()))
    plain = base64.b64encode(lines.encode()).decode().rstrip("=")
    cipher = ""
    for i, c in enumerate(plain):
        block = base64.b64encode(hashlib.sha256(f"{encrypt}:{nonce}:{i // 40}".encode()).digest()).decode()
        cipher += ALPHABET[(ALPHABET.index(c) + ALPHABET.index(block[i % 40])) % 64]
    tag = sha(f"{authenticate}:" + sha(f"{authenticate}:{identity}:{nonce}:{cipher}"))
    return f"{nonce}.{cipher}.{tag}"


def run_function(name, body, work):
    """Runs one function of the runner in a shell of its own, with the runner's globals."""
    function = re.search(rf"(?ms)^{name}\(\) \{{.*?^\}}", RUNNER).group()
    script = (f"set -euo pipefail\nwork={work}\nsecrets={work}/secrets\n"
              "fail() { echo \"$*\" >&2; exit 1; }\n" f"{function}\n{body}\n")
    return subprocess.run(["bash", "-c", script], capture_output=True, text=True)


class SecretsTests(unittest.TestCase):
    def test_the_variable_is_one_ephemeral_map(self):
        block = re.search(r'variable "secrets" \{.*?^\}', VARIABLES, re.S | re.M).group()
        self.assertIn("type        = map(string)", block)
        self.assertIn("ephemeral   = true", block)

    def test_secrets_are_read_only_where_the_seal_is_made(self):
        # Nothing else may use them: not an output, not a trigger, not a command.
        used = [m.start() for m in re.finditer(r"var\.secrets", MAIN)]
        self.assertEqual(len(used), 3)
        content = re.search(r'provisioner "file" \{\n    content = length\(var\.secrets\).*?destination = "\$\{local\.work\}/secrets\.sealed"', MAIN, re.S).group()
        self.assertEqual(content.count("var.secrets"), 3)
        self.assertNotIn("var.secrets", (MODULE / "outputs.tf").read_text())
        trigger = re.search(r"triggers_replace = \{.*?\n  \}", MAIN, re.S).group()
        self.assertNotIn("secrets", trigger)

    def test_a_seal_has_a_random_nonce_of_its_own(self):
        # A clock is no nonce: one key and one nonce twice give the difference of two passwords.
        self.assertNotIn("plantimestamp", MAIN)
        self.assertRegex(MAIN, r"for nonce in \[uuid\(\)\]")

    def test_the_sealing_key_goes_only_through_the_proxmox_api_and_is_not_the_ssh_key(self):
        self.assertIn("octopushy-bootstrap=${local.bootstrap}", MAIN)
        container = re.search(r'module "container" \{.*?^\}', MAIN, re.S | re.M).group()
        self.assertIn("extra_ssh_keys = [local.bootstrap_key]", container)
        install = re.search(r'resource "terraform_data" "install" \{.*?^\}', MAIN, re.S | re.M).group()
        self.assertNotRegex(install, r"(?m)^[^#]*(bootstrap|tls_private_key\.seal)")
        bootstrap = re.search(r"(?m)^\s*bootstrap\s*=.*$", MAIN).group()
        self.assertIn("tls_private_key.seal", bootstrap)
        self.assertNotIn("tls_private_key.install", bootstrap)
        self.assertEqual(MAIN.count('resource "tls_private_key"'), 2)

    def test_the_tag_binds_the_guest_and_its_address(self):
        self.assertIn('seal_identity     = "${var.guest.id}/${local.address}"', MAIN)
        self.assertIn('"$(</proc/sys/kernel/hostname)/$OCTOPUSHY_ADDRESS"', RUNNER)

    def test_the_guest_checks_the_tag_before_it_opens_anything(self):
        unseal = re.search(r"(?ms)^unseal\(\) \{.*?^\}", RUNNER).group()
        self.assertLess(unseal.index("the seal does not verify"), unseal.index("printf '%s\\n' \"$nonce\""))
        self.assertLess(unseal.index("printf '%s\\n' \"$nonce\""), unseal.index("base64 -d"))
        self.assertNotIn("jq", RUNNER)

    def unseal(self, sealed, key=KEY, identity=IDENTITY, used=None):
        with tempfile.TemporaryDirectory() as work:
            Path(work, "sealed").write_text(sealed)
            spent = used or f"{work}/state/spent"
            done = run_function("unseal", f"unseal {work}/sealed {key} '{identity}' {spent}", work)
            files = {p.name: (p.read_bytes(), oct(p.stat().st_mode & 0o777)) for p in Path(work, "secrets").glob("*")}
            return done, files, Path(work, "secrets").is_dir() and oct(Path(work, "secrets").stat().st_mode & 0o777)

    def test_several_secrets_open_into_a_file_each_at_0600(self):
        secrets = {"postgres_password": "A test password, with & = + % in it", "victoriametrics_password": "second\nline: with colon",
                   "empty_looking": " ", "unicode": "é-ü-secret", "long": "y" * 300}
        sealed = seal(secrets)
        for value in secrets.values():
            self.assertNotIn(base64.b64encode(value.encode()).decode()[:12], sealed)
        done, files, mode = self.unseal(sealed)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual({name: contents for name, (contents, _) in files.items()}, {n: v.encode() for n, v in secrets.items()})
        self.assertEqual({m for _, m in files.values()}, {"0o600"})
        self.assertEqual(mode, "0o700")
        self.assertEqual(done.stdout + done.stderr, "")

    def test_a_tampered_seal_gives_nothing_up(self):
        nonce, cipher, tag = seal({"a_secret": "A test password, with & = + % in it"}).split(".")
        flipped = "B" if cipher[3] != "B" else "C"
        for changed in (
            f"{nonce}.{cipher[:3]}{flipped}{cipher[4:]}.{tag}",
            f"{nonce}.{cipher}0.{tag}",
            f"{nonce}.{cipher}.{'0' * 64}",
            f"{'1' * 8}{nonce[8:]}.{cipher}.{tag}",
            f"{nonce}.{cipher}",
        ):
            done, files, _ = self.unseal(changed)
            self.assertNotEqual(done.returncode, 0, changed)
            self.assertEqual(files, {}, "something was written before the tag was checked")
            self.assertIn("seal", done.stderr)

    def test_a_seal_opens_only_on_the_guest_it_was_made_for_and_with_its_key(self):
        sealed = seal({"a_secret": "a test password here"})
        for other in ({"identity": "mrb-dns-01/10.1.0.42"}, {"identity": "ha-db/10.15.30.41"}, {"key": "cd" * 32}):
            done, files, _ = self.unseal(sealed, **other)
            self.assertNotEqual(done.returncode, 0, other)
            self.assertEqual(files, {})

    def test_a_nonce_is_never_used_twice_and_the_record_is_made_where_it_is_told(self):
        with tempfile.TemporaryDirectory() as work:
            used = f"{work}/deep/er/used-nonces"
            self.assertEqual(self.unseal(seal({"a_secret": "the first test password"}), used=used)[0].returncode, 0)
            self.assertEqual(oct(Path(used).stat().st_mode & 0o777), "0o600")
            # The same nonce again, as an old clock could give it, over another password.
            done, files, _ = self.unseal(seal({"a_secret": "another test password"}), used=used)
            self.assertNotEqual(done.returncode, 0)
            self.assertEqual(files, {})
            self.assertIn("nonce", done.stderr)

    def test_a_name_that_is_not_a_plain_name_is_refused_on_the_guest_too(self):
        done, files, _ = self.unseal(seal({"../../etc/passwd": "a test password here"}))
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual(files, {})


class EnvironmentTests(unittest.TestCase):
    def load(self, text, check=""):
        with tempfile.TemporaryDirectory() as work:
            Path(work, "environment").write_text(text)
            body = f"load_environment\n{check}"
            done = run_function("load_environment", body, work)
            return done, Path(work)

    def test_values_are_data_and_never_evaluated(self):
        hostile = ["$(touch {work}/pwned)", "`touch {work}/pwned`", "'; touch {work}/pwned; '", '" $(touch {work}/pwned) "', "a=b=c", "  spaced  ", "${{HOME}}"]
        for value in hostile:
            with tempfile.TemporaryDirectory() as work:
                value = value.format(work=work)
                Path(work, "environment").write_text(f"SOME_VALUE={value}\nOCTOPUSHY_SCRIPT=install.sh\n")
                done = run_function("load_environment", f'load_environment\n[[ $SOME_VALUE == "$(cat {work}/expected)" ]]', work) \
                    if Path(work, "expected").write_text(value) else None
                self.assertEqual(done.returncode, 0, (value, done.stderr))
                self.assertFalse(Path(work, "pwned").exists(), value)

    def test_a_name_the_shell_itself_uses_is_refused(self):
        for name in ("PATH", "IFS", "LD_PRELOAD", "BASH_ENV", "BASHOPTS", "ENV", "SHELLOPTS", "PROMPT_COMMAND", "HOME", "OCTOPUSHY_SECRETS", "OCTOPUSHY_OTHER", "TMPDIR"):
            done, _ = self.load(f"{name}=/tmp/x\n")
            self.assertNotEqual(done.returncode, 0, name)
            self.assertIn("may not", done.stderr)

    def test_a_line_that_is_not_name_equals_value_is_refused(self):
        for text in ("not a line\n", "lower=case\n", "1ST=digit\n", "=novalue\n", "NA ME=x\n"):
            done, _ = self.load(text)
            self.assertNotEqual(done.returncode, 0, text)

    def test_the_modules_own_names_are_accepted(self):
        done, _ = self.load("OCTOPUSHY_SCRIPT=install.sh\nOCTOPUSHY_ADDRESS=10.0.0.1\nOCTOPUSHY_NONCES=/var/lib/x\nA_B1=ok\n",
                            '[[ $OCTOPUSHY_SCRIPT == install.sh && $A_B1 == ok ]]')
        self.assertEqual(done.returncode, 0, done.stderr)


class RunnerTests(unittest.TestCase):
    def test_nothing_survives_however_it_ends(self):
        self.assertIn("trap cleanup EXIT", RUNNER)
        for signal in ("HUP", "INT", "TERM"):
            self.assertRegex(RUNNER, rf"trap 'exit \d+' {signal}")
        cleanup = re.search(r"(?ms)^cleanup\(\) \{.*?^\}", RUNNER).group()
        self.assertIn('rm -rf "$work"', cleanup)
        self.assertIn('exit "$status"', cleanup)

    def test_the_script_runs_last_inside_the_folder_and_its_status_is_the_runners(self):
        self.assertTrue(RUNNER.rstrip().endswith('bash "$app/$OCTOPUSHY_SCRIPT"'))
        self.assertNotRegex(RUNNER, r"(?m)^[^#]*\beval\b")
        self.assertNotRegex(RUNNER, r"(?m)^\s*(source|\.) ")

    def test_the_runner_is_never_given_a_secret_or_a_value_on_a_command_line(self):
        self.assertNotRegex(MAIN, r'inline\s*=\s*\[[^\]]*(var\.|local\.environment|secrets)')
        commands = re.findall(r"inline\s*=\s*\[(.*?)\]", MAIN, re.S)
        self.assertEqual(len(commands), 2)
        for command in commands:
            self.assertEqual(re.findall(r"\$\{(\w+\.\w+)\}", command), ["local.work"] * len(re.findall(r"\$\{", command)))

    def test_every_variable_of_the_app_is_checked(self):
        for check in (r"\^\[A-Z\]\[A-Z0-9_\]\*\$", r"OCTOPUSHY_\|LD_", r"\\\\x00-\\\\x1f", r"fileexists\(", r"\^\[a-z\]\[a-z0-9_\]\*\$"):
            self.assertRegex(VARIABLES, check)

    def test_files_and_environment_change_the_install_without_a_trigger_of_the_recipe(self):
        trigger = re.search(r"triggers_replace = \{.*?\n  \}", MAIN, re.S).group()
        for part in ("module.container.incarnation", "local.file_hashes", "local.environment_file", "var.app.reinstall_on", "guest/run.sh"):
            self.assertIn(part, trigger)

    def test_it_is_a_module_not_a_root(self):
        self.assertNotRegex(MAIN + (MODULE / "versions.tf").read_text(), r'(?m)^provider "')
        self.assertFalse((MODULE / ".terraform.lock.hcl").exists())


if __name__ == "__main__":
    unittest.main()
