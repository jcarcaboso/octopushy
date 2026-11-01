"""octopushy-ansible with a fake ansible-playbook: no Ansible, ssh or network needed."""
import importlib.machinery
import importlib.util
import json
import os
import signal
import stat
import subprocess
import sys
import tempfile
import textwrap
import time
import types
import unittest
from pathlib import Path
from unittest import mock

RUNNER = Path(__file__).resolve().parents[2] / "agent/ansible/octopushy-ansible"
KEY = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----"


def load():
    loader = importlib.machinery.SourceFileLoader("octopushy_ansible", str(RUNNER))
    spec = importlib.util.spec_from_loader("octopushy_ansible", loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


class Recap(unittest.TestCase):
    def callback(self, path):
        """The callback, with just enough of Ansible for it to import."""
        ansible = types.ModuleType("ansible")
        plugins = types.ModuleType("ansible.plugins")
        callback = types.ModuleType("ansible.plugins.callback")
        callback.CallbackBase = type("CallbackBase", (), {"__init__": lambda self: None})
        modules = {"ansible": ansible, "ansible.plugins": plugins, "ansible.plugins.callback": callback}
        with mock.patch.dict(sys.modules, modules), mock.patch.dict(os.environ, {"OCTOPUSHY_RESULT_FILE": str(path)}):
            spec = importlib.util.spec_from_file_location("octopushy_recap", RUNNER.with_name("octopushy_recap.py"))
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            return module.CallbackModule()

    def test_the_end_of_an_ssh_failure_is_kept_however_long_its_banner(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "recap.json"
            callback = self.callback(path)
            message = "@" * 5000 + "\nHost key verification failed."
            callback.v2_runner_on_unreachable(types.SimpleNamespace(_result={"msg": message}, task_name="Gathering Facts"))
            recap = json.loads(path.read_text())
            self.assertTrue(recap["unreachable"].endswith("Host key verification failed."))
            self.assertLessEqual(len(recap["unreachable"]), 1024)
            self.assertFalse(recap["reached"])

    def test_a_task_result_is_never_recorded(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "recap.json"
            callback = self.callback(path)
            callback.v2_runner_on_failed(types.SimpleNamespace(_result={"msg": "s3cret"}, task_name="Install"))
            text = path.read_text()
            self.assertIn("Install", text)
            self.assertNotIn("s3cret", text)


class Mounts(unittest.TestCase):
    def mountinfo(self, *mounts):
        return "\n".join(f"1 2 0:1 / {mount} rw - {kind} none rw" for mount, kind in mounts)

    def test_the_longest_mount_over_the_path_decides(self):
        runner = load()
        self.assertTrue(runner.is_memory("/tmp", self.mountinfo(("/", "overlay"), ("/tmp", "tmpfs"))))
        self.assertFalse(runner.is_memory("/tmp", self.mountinfo(("/", "overlay"))))
        self.assertFalse(runner.is_memory("/tmp", self.mountinfo(("/", "tmpfs"), ("/tmp", "ext4"))))
        self.assertFalse(runner.is_memory("/tmp", self.mountinfo(("/", "overlay"), ("/tmpfoo", "tmpfs"))))


@unittest.skipUnless(load().tmp_is_memory(), "needs /tmp on tmpfs")
class Runs(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.bin = Path(self.dir.name) / "bin"
        self.bin.mkdir()
        self.record = Path(self.dir.name) / "record"
        self.record.mkdir()
        (self.bin / "python3").symlink_to(sys.executable)
        self.script("ansible-playbook", f"""
            #!/bin/sh
            printf '%s\\n' "$@" > {self.record}/args
            env | sort > {self.record}/env
            for a in "$@"; do case "$a" in @*)
              f="${{a#@}}"; cp "$f" {self.record}/vars; stat -c '%a' "$f" > {self.record}/mode
              stat -c '%a' "$(dirname "$f")" > {self.record}/dirmode
              echo "$f" > {self.record}/path
              sed -n 's/^"secret_src": "\\(.*\\)"$/\\1/p' "$f" > {self.record}/filepath
              [ -s {self.record}/filepath ] && cp "$(cat {self.record}/filepath)" {self.record}/file
            ;; esac; done
            [ -f {self.record}/sleep ] && sleep 30
            exit ${{FAKE_EXIT:-0}}
        """)
        self.script("ssh-agent", f"""
            #!/bin/sh
            while [ "$1" != -a ]; do shift; done
            touch "$2"
            echo running > {self.record}/agent
            exec sleep 60
        """)
        self.script("ssh-add", f"""
            #!/bin/sh
            cat > {self.record}/added
            [ -z "${{FAKE_BAD_KEY:-}}" ]
        """)

    def script(self, name, text):
        path = self.bin / name
        path.write_text(textwrap.dedent(text).lstrip())
        path.chmod(path.stat().st_mode | stat.S_IEXEC)

    def run_it(self, *args, secrets=None, env=None):
        environment = {
            "PATH": f"{self.bin}:/usr/bin:/bin",
            "HOME": self.dir.name,
            **(env or {}),
        }
        if secrets is not None:
            environment["OCTOPUSHY_STDIN"] = "1"
        return subprocess.run(
            [self.bin / "python3", RUNNER, *args],
            input=json.dumps(secrets).encode() if secrets is not None else None,
            env=environment,
            capture_output=True,
            check=False,
        )

    def test_a_preview_takes_nothing_from_stdin(self):
        done = self.run_it("--list-tasks", "site.yml")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual((self.record / "args").read_text(), "--list-tasks\nsite.yml\n")

    def test_what_a_repository_could_weaken_is_fixed(self):
        self.run_it("site.yml")
        env = (self.record / "env").read_text()
        for line in ("ANSIBLE_HOST_KEY_CHECKING=True", "ANSIBLE_PIPELINING=True", "ANSIBLE_NO_TARGET_SYSLOG=True",
                     "ANSIBLE_LOG_PATH=/dev/null", "ANSIBLE_KEEP_REMOTE_FILES=False", "ANSIBLE_VERBOSITY=0"):
            self.assertIn(line, env)

    def test_the_exit_status_is_passed_on(self):
        self.assertEqual(self.run_it("site.yml", env={"FAKE_EXIT": "4"}).returncode, 4)

    def test_secrets_are_private_unsafe_and_gone_afterwards(self):
        done = self.run_it("site.yml", secrets={
            "sshKey": KEY + "\r\n",
            "secretVars": {"app_password": 'pa"ss{{ x }}'},
            "secretFiles": {"secret_src": "file secret\n"},
        })
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual((self.record / "added").read_text(), KEY + "\n")
        self.assertEqual((self.record / "mode").read_text().strip(), "600")
        self.assertEqual((self.record / "dirmode").read_text().strip(), "700")
        self.assertIn('"app_password": !unsafe "pa\\"ss{{ x }}"', (self.record / "vars").read_text())
        self.assertEqual((self.record / "file").read_text(), "file secret\n")
        args = (self.record / "args").read_text()
        self.assertNotIn("ss{{ x }}", args)
        self.assertNotIn("file secret", args)
        env = (self.record / "env").read_text()
        self.assertNotIn("ss{{ x }}", env)
        self.assertNotIn("BEGIN OPENSSH", env)
        self.assertIn("SSH_AUTH_SOCK=", env)
        self.assertFalse(Path((self.record / "path").read_text().strip()).exists())

    def test_a_private_directory_the_agent_chose_is_removed(self):
        private = Path(tempfile.gettempdir()) / f"octopushy-attempt-test-{os.getpid()}"
        done = self.run_it("site.yml", secrets={"sshKey": KEY}, env={"OCTOPUSHY_SECRET_DIR": str(private)})
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertFalse(private.exists())

    def test_a_key_that_cannot_be_loaded_stops_the_run(self):
        done = self.run_it("site.yml", secrets={"sshKey": "nonsense"}, env={"FAKE_BAD_KEY": "1"})
        self.assertEqual(done.returncode, 78)
        self.assertIn(b"could not be loaded", done.stderr)
        self.assertNotIn(b"nonsense", done.stderr)
        self.assertFalse((self.record / "args").exists())

    def test_a_signal_reaches_ansible(self):
        (self.record / "sleep").write_text("")
        process = subprocess.Popen(
            [self.bin / "python3", RUNNER, "site.yml"],
            env={"PATH": f"{self.bin}:/usr/bin:/bin", "HOME": self.dir.name},
            stdin=subprocess.DEVNULL,
        )
        for _ in range(200):
            if (self.record / "args").exists():
                break
            time.sleep(0.02)
        process.send_signal(signal.SIGTERM)
        self.assertEqual(process.wait(timeout=10), 128 + signal.SIGTERM)


if __name__ == "__main__":
    unittest.main()
