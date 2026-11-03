#!/usr/bin/env python3
"""The authorized_keys program, with a real ssh-keygen. Run it with python3.

authorized_keys_sshd.py checks the same program against a real sshd.
"""
import base64
import hashlib
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "backend/src/Octopushy.Server/Orchestration/GuestAccess/authorized_keys.py"
spec = importlib.util.spec_from_file_location("authorized_keys", SCRIPT)
authorized_keys = importlib.util.module_from_spec(spec)
spec.loader.exec_module(authorized_keys)


def new_key(directory, name):
    path = os.path.join(directory, name)
    subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path], check=True)
    text = Path(path + ".pub").read_text().strip()
    blob = text.split()[1]
    digest = base64.b64encode(hashlib.sha256(base64.b64decode(blob)).digest()).decode().rstrip("=")
    return text, blob, f"SHA256:{digest}"


@unittest.skipUnless(shutil.which("ssh-keygen"), "needs ssh-keygen")
class Program(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.keys = tempfile.TemporaryDirectory()
        cls.old, cls.old_blob, cls.old_print = new_key(cls.keys.name, "old")
        cls.new, cls.new_blob, cls.new_print = new_key(cls.keys.name, "new")
        cls.person, cls.person_blob, cls.person_print = new_key(cls.keys.name, "person")

    @classmethod
    def tearDownClass(cls):
        cls.keys.cleanup()

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.home = Path(directory.name)
        (self.home / ".ssh").mkdir(mode=0o700)
        self.file = self.home / ".ssh/authorized_keys"

    def run_it(self, *arguments, env=None):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *arguments], env={**os.environ, "HOME": str(self.home), **(env or {})},
            capture_output=True, text=True, check=False,
        )

    def test_add_appends_and_keeps_every_byte_before_it(self):
        self.file.write_bytes(f"{self.person} \xff".encode("latin-1"))
        self.assertEqual(self.run_it("add", self.new).stdout.strip(), "changed")
        self.assertEqual(self.file.read_bytes(), f"{self.person} \xff\n{self.new}\n".encode("latin-1"))

    def test_add_makes_a_file_that_is_not_there(self):
        self.file.parent.rmdir()
        self.run_it("add", self.new)
        self.assertEqual(self.file.read_text(), self.new + "\n")
        self.assertEqual(self.file.stat().st_mode & 0o777, 0o600)

    def test_adding_a_key_already_there_changes_nothing_however_it_is_written(self):
        self.file.write_text(f"ssh-ed25519\t{self.new_blob}\n")
        self.assertEqual(self.run_it("add", self.new).stdout.strip(), "")
        self.assertEqual(self.file.read_text(), f"ssh-ed25519\t{self.new_blob}\n")

    def test_what_is_not_a_key_cannot_be_added(self):
        self.assertEqual(self.run_it("add", "not a key").returncode, 3)
        self.assertFalse(self.file.exists())

    def test_removal_takes_the_key_and_no_line_that_only_mentions_it(self):
        keep = (
            f"# {self.old}\n"
            f"{self.person}, replaces {self.old}\n"
            f'environment="X={self.old}" {self.new}\n'
            "\n"
        )
        self.file.write_text(self.old + "\n" + keep)
        self.assertEqual(self.run_it("remove", self.new_print, self.old_print).stdout.strip(), "changed")
        self.assertEqual(self.file.read_text(), keep)

    def test_only_a_newline_ends_a_line(self):
        for separator in ("\r", "\v", "\f", "\x85", "\u2028"):
            with self.subTest(separator=repr(separator)):
                keep = f"{self.person}{separator}{self.old}\n{self.new}\n".encode()
                self.file.write_bytes(self.old.encode() + b"\n" + keep)
                self.run_it("remove", self.new_print, self.old_print)
                self.assertEqual(self.file.read_bytes(), keep)

    def test_every_copy_goes_however_it_is_written(self):
        blob = self.old_blob
        for line in (
            f"ssh-ed25519\t{blob}", f"no-pty {self.old}", f"  ssh-ed25519   {blob}   x", f"{self.old}\r",
            f"ssh-ed25519 {blob[:20]}\r{blob[20:]} c", f"no-pty  {self.old}", f"no-pty \t {self.old}",
            f"no-pty  ssh-ed25519   {blob}", f"{self.old} PRIVATE KEY", f'environment="A=PRIVATE KEY" {self.old}',
            f'environment="A=\\"b c" {self.old}',
        ):
            with self.subTest(line=line):
                self.file.write_text(f"{line}\n{self.new}\n{self.old}\n")
                self.assertEqual(self.run_it("absent", self.new_print, self.old_print).returncode, 1)
                self.run_it("remove", self.new_print, self.old_print)
                self.assertEqual(self.file.read_text(), self.new + "\n")
                self.assertEqual(self.run_it("absent", self.new_print, self.old_print).returncode, 0)

    def test_removing_a_key_that_is_not_there_writes_nothing(self):
        self.file.write_text(self.new + "\n")
        before = self.file.stat().st_mtime_ns
        self.assertEqual(self.run_it("remove", self.new_print, self.old_print).stdout.strip(), "")
        self.assertEqual(self.file.stat().st_mtime_ns, before)

    def test_a_mention_does_not_read_as_a_key_still_authorized(self):
        self.file.write_text(f"{self.new}\n{self.person} replaces {self.old}\n")
        self.assertEqual(self.run_it("absent", self.new_print, self.old_print).returncode, 0)

    def test_a_person_with_options_keeps_every_byte(self):
        keep = f'no-pty,command="/bin/echo a  b"  {self.person}  spaced\n{self.new}\n'
        self.file.write_text(f"no-pty {self.old}\n{keep}")
        self.run_it("remove", self.new_print, self.old_print)
        self.assertEqual(self.file.read_text(), keep)

    def test_nothing_is_read_as_gone_unless_the_key_in_use_is_found(self):
        path = self.home / "fake"
        path.mkdir()
        tool = path / "ssh-keygen"
        for name, script in (
            ("not there", None), ("fails", "exit 1"), ("killed", "kill -9 $$"), ("says something else", "echo 256 not a fingerprint"),
        ):
            with self.subTest(name):
                tool.unlink(missing_ok=True)
                if script:
                    tool.write_text(f"#!/bin/sh\n{script}\n")
                    tool.chmod(0o755)
                self.file.write_text(f"{self.old}\n{self.new}\n")
                for action in ("absent", "remove"):
                    result = self.run_it(action, self.new_print, self.old_print, env={"PATH": str(path)})
                    self.assertEqual(result.returncode, 3, result.stderr)
                self.assertEqual(self.file.read_text(), f"{self.old}\n{self.new}\n")

    def test_a_file_without_the_key_in_use_is_not_written(self):
        self.file.write_text(f"{self.old}\n{self.person}\n")
        for action in ("absent", "remove"):
            self.assertEqual(self.run_it(action, self.new_print, self.old_print).returncode, 3)
        self.assertEqual(self.file.read_text(), f"{self.old}\n{self.person}\n")

    def test_a_short_write_is_not_a_success(self):
        self.file.write_text(self.person + "\n")
        authorized_keys.PATH = str(self.file)
        original = os.write
        self.addCleanup(setattr, os, "write", original)
        os.write = lambda descriptor, data: original(descriptor, data[:10])
        with self.assertRaises(OSError):
            authorized_keys.add(self.new)

    def test_a_symlinked_file_stays_a_symlink(self):
        real = self.home / "real_keys"
        real.write_text(f"{self.old}\n{self.new}\n")
        self.file.symlink_to(real)
        self.run_it("remove", self.new_print, self.old_print)
        self.assertTrue(self.file.is_symlink())
        self.assertEqual(real.read_text(), self.new + "\n")

    def test_the_mode_of_the_file_is_kept(self):
        self.file.write_text(f"{self.old}\n{self.new}\n")
        self.file.chmod(0o640)
        self.run_it("remove", self.new_print, self.old_print)
        self.assertEqual(self.file.stat().st_mode & 0o777, 0o640)

    def test_a_write_during_a_removal_is_kept(self):
        self.file.write_text(f"{self.old}\n{self.new}\n")
        authorized_keys.PATH = str(self.file)
        written = []
        original = authorized_keys.replace

        def someone_else_writes(before, after):
            if not written:
                written.append(True)
                with open(self.file, "a") as stream:
                    stream.write(self.person + "\n")
            return original(before, after)

        authorized_keys.replace = someone_else_writes
        self.addCleanup(setattr, authorized_keys, "replace", original)
        self.assertEqual(authorized_keys.remove(self.new_print, {self.old_print}), 0)
        self.assertEqual(self.file.read_text(), f"{self.new}\n{self.person}\n")

    def test_a_file_that_keeps_changing_stops_the_removal(self):
        self.file.write_text(f"{self.old}\n{self.new}\n")
        authorized_keys.PATH = str(self.file)
        original = authorized_keys.replace
        authorized_keys.replace = lambda before, after: False
        self.addCleanup(setattr, authorized_keys, "replace", original)
        with self.assertRaises(OSError):
            authorized_keys.remove(self.new_print, {self.old_print})
        self.assertEqual(self.file.read_text(), f"{self.old}\n{self.new}\n")


if __name__ == "__main__":
    unittest.main()
