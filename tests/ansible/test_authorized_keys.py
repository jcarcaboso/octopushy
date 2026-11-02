#!/usr/bin/env python3
"""The authorized_keys program, on the kinds of file sshd accepts. Run it with python3."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "backend/src/Octopushy.Server/Orchestration/GuestAccess/authorized_keys.py"
spec = importlib.util.spec_from_file_location("authorized_keys", SCRIPT)
authorized_keys = importlib.util.module_from_spec(spec)
spec.loader.exec_module(authorized_keys)

OLD = "AAAAC3NzaC1lZDI1NTE5AAAAIOLDOLDOLDOLDOLDOLDOLDOLDOLDOLDOLDOLDOLDOLD"
NEW = "AAAAC3NzaC1lZDI1NTE5AAAAINEWNEWNEWNEWNEWNEWNEWNEWNEWNEWNEWNEWNEWNEW"
PERSON = "AAAAC3NzaC1lZDI1NTE5AAAAIPERSONPERSONPERSONPERSONPERSONPERSONPERSON"


class Blobs(unittest.TestCase):
    def test_a_key_is_read_from_its_field(self):
        cases = {
            f"ssh-ed25519 {OLD}": OLD,
            f"ssh-ed25519 {OLD} octopushy@guest\n": OLD,
            f"ssh-ed25519\t{OLD}\tcomment\n": OLD,
            f"  ssh-ed25519   {OLD}\n": OLD,
            f'no-pty ssh-ed25519 {OLD}\n': OLD,
            f'command="echo \\"a b\\"",no-pty ssh-ed25519 {OLD} c\n': OLD,
            f"ecdsa-sha2-nistp256 {OLD}\n": OLD,
            f"sk-ssh-ed25519@openssh.com {OLD}\n": OLD,
        }
        for line, blob in cases.items():
            with self.subTest(line=line):
                self.assertEqual(authorized_keys.blob_of(line), blob)

    def test_what_authorizes_no_key_has_no_blob(self):
        for line in ("\n", f"# ssh-ed25519 {OLD}\n", "not a key at all\n", "ssh-ed25519\n"):
            with self.subTest(line=line):
                self.assertIsNone(authorized_keys.blob_of(line))

    def test_a_key_quoted_elsewhere_on_a_line_is_not_that_lines_key(self):
        self.assertEqual(authorized_keys.blob_of(f"ssh-ed25519 {PERSON} replaces ssh-ed25519 {OLD}\n"), PERSON)
        self.assertEqual(authorized_keys.blob_of(f'environment="OLD=ssh-ed25519 {OLD}" ssh-ed25519 {PERSON}\n'), PERSON)


class Program(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.home = Path(directory.name)
        (self.home / ".ssh").mkdir(mode=0o700)
        self.file = self.home / ".ssh/authorized_keys"

    def run_it(self, *arguments):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *arguments], env={**os.environ, "HOME": str(self.home)},
            capture_output=True, text=True, check=False,
        )

    def test_add_keeps_every_byte_of_a_file_with_no_final_newline(self):
        self.file.write_text(f"ssh-ed25519 {PERSON} person")
        self.assertEqual(self.run_it("add", f"ssh-ed25519 {NEW} octopushy@guest").stdout.strip(), "changed")
        self.assertEqual(self.file.read_text(), f"ssh-ed25519 {PERSON} person\nssh-ed25519 {NEW} octopushy@guest\n")
        self.assertEqual(self.file.stat().st_mode & 0o777, 0o600)

    def test_adding_a_key_already_there_changes_nothing_however_it_is_written(self):
        self.file.write_text(f"ssh-ed25519\t{NEW}\n")
        self.assertEqual(self.run_it("add", f"ssh-ed25519 {NEW} octopushy@guest").stdout.strip(), "")
        self.assertEqual(self.file.read_text(), f"ssh-ed25519\t{NEW}\n")

    def test_removal_takes_the_key_and_no_line_that_only_mentions_it(self):
        keep = (
            f"# ssh-ed25519 {OLD} a note\n"
            f"ssh-ed25519 {PERSON} person, replaces ssh-ed25519 {OLD}\n"
            f'environment="X=ssh-ed25519 {OLD}" ssh-ed25519 {NEW} other\n'
            "\n"
        )
        self.file.write_text(f"ssh-ed25519 {OLD} octopushy@guest\n" + keep)
        self.assertEqual(self.run_it("remove", OLD).stdout.strip(), "changed")
        self.assertEqual(self.file.read_text(), keep)

    def test_removal_finds_a_key_however_its_fields_are_separated(self):
        for line in (f"ssh-ed25519\t{OLD}\n", f"no-pty ssh-ed25519 {OLD}\n", f"ssh-ed25519   {OLD}   x\n"):
            with self.subTest(line=line):
                self.file.write_text(line + f"ssh-ed25519 {PERSON}\n")
                self.assertEqual(self.run_it("absent", OLD).returncode, 1)
                self.run_it("remove", OLD)
                self.assertEqual(self.file.read_text(), f"ssh-ed25519 {PERSON}\n")
                self.assertEqual(self.run_it("absent", OLD).returncode, 0)

    def test_every_copy_of_a_key_goes(self):
        self.file.write_text(f"ssh-ed25519 {OLD} a\nssh-ed25519 {PERSON}\nssh-ed25519 {OLD} b\n")
        self.run_it("remove", OLD)
        self.assertEqual(self.file.read_text(), f"ssh-ed25519 {PERSON}\n")

    def test_removing_a_key_that_is_not_there_writes_nothing(self):
        self.file.write_text(f"ssh-ed25519 {PERSON}\n")
        before = self.file.stat().st_mtime_ns
        self.assertEqual(self.run_it("remove", OLD).stdout.strip(), "")
        self.assertEqual(self.file.stat().st_mtime_ns, before)

    def test_a_mention_does_not_read_as_a_key_still_authorized(self):
        self.file.write_text(f"ssh-ed25519 {PERSON} replaces {OLD}\n")
        self.assertEqual(self.run_it("absent", OLD).returncode, 0)

    def test_a_symlinked_file_stays_a_symlink(self):
        real = self.home / "real_keys"
        real.write_text(f"ssh-ed25519 {OLD}\n")
        self.file.symlink_to(real)
        self.run_it("remove", OLD)
        self.assertTrue(self.file.is_symlink())
        self.assertEqual(real.read_text(), "")


if __name__ == "__main__":
    unittest.main()
