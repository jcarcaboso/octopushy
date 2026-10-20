import http.client
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from octopushy import App, Handler, Problem, bind_address, scaffold, source_url

FAKE = Path(__file__).with_name("fake_tofu.py").resolve()


class WorkflowTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        FAKE.chmod(0o755)
        self.git("init", "--initial-branch=main", "--template=")
        self.config = {"generation": 1, "trace": str(self.root / "trace")}
        self.manifest = {"version": 1, "stacks": [
            {"id": "vms", "path": ".", "backend": "managed-local"}]}
        self.commit()
        self.app = App(self.root / "data", str(FAKE), timeout=10)
        self.addCleanup(lambda: self.app.close())
        # Most checks use synchronous execution; the concurrency test uses real threads.
        self.app.launch = lambda target, run_id: target(run_id)

    def git(self, *args):
        return subprocess.check_output(
            ["git", "-C", str(self.repo), "-c", "user.name=Test",
             "-c", "user.email=test@localhost", *args], stderr=subprocess.DEVNULL).decode().strip()

    def commit(self):
        (self.repo / ".octopushy.json").write_text(json.dumps(self.manifest))
        (self.repo / "fixture.json").write_text(json.dumps(self.config))
        self.git("add", ".")
        self.git("commit", "--allow-empty", "-m", "Test revision")

    def link(self):
        repo_id = self.app.link_repository("lab", str(self.repo), trusted=True)
        self.repo_id = repo_id
        self.stack = self.app.snapshot()["stacks"][0]
        return repo_id

    def plan(self):
        return self.app.one("runs", self.app.start_plan(self.stack["id"]))

    def apply(self, run, confirmation=""):
        self.app.start_apply(run["id"], run["plan_hash"], confirmation)
        return self.app.one("runs", run["id"])

    def state_path(self):
        return self.app.data / "states" / self.stack["id"] / "terraform.tfstate"

    def test_git_plan_apply_and_state_survive_new_snapshot_and_restart(self):
        self.link()
        first = self.plan()
        self.assertEqual(first["status"], "planned")  # detailed exit code 2 is success
        applied = self.apply(first)
        self.assertEqual(applied["status"], "applied")
        state = json.loads(self.state_path().read_text())
        stack = self.app.snapshot()["stacks"][0]
        self.assertEqual(stack["applied_revision"], first["revision"])
        self.assertEqual(stack["resources"][0]["vm"]["vm_id"], 201)
        self.assertNotIn("api_token", json.dumps(stack["resources"]))
        self.assertEqual(self.plan()["status"], "no_changes")
        with self.assertRaises(Problem):
            self.apply(applied)
        self.config["generation"] = 2
        self.commit()
        self.app.sync(self.repo_id)
        second = self.plan()
        self.assertNotEqual(second["revision"], first["revision"])
        self.assertEqual(self.apply(second)["status"], "applied")
        next_state = json.loads(self.state_path().read_text())
        self.assertEqual(next_state["lineage"], state["lineage"])
        self.assertEqual(next_state["serial"], state["serial"] + 1)
        self.app.close()
        self.app = App(self.root / "data", str(FAKE))
        self.app.launch = lambda target, run_id: target(run_id)
        self.assertEqual(self.plan()["status"], "no_changes")
        trace = [json.loads(line) for line in (self.root / "trace").read_text().splitlines()]
        applies = [args for args in trace if args[0] == "apply"]
        self.assertEqual(len(applies), 2)
        self.assertTrue(all(args[-1].endswith("plan.bin") and "-auto-approve" not in args for args in applies))

    def test_branch_advance_invalidates_approved_plan(self):
        self.link()
        run = self.plan()
        self.commit()
        self.assertEqual(self.apply(run)["status"], "invalidated")
        self.assertFalse(self.state_path().exists())

    def test_sync_supersedes_old_plan_without_applying(self):
        self.link()
        run = self.plan()
        self.commit()
        self.app.sync(self.repo_id)
        self.assertEqual(self.app.one("runs", run["id"])["status"], "superseded")
        self.assertFalse(self.state_path().exists())

    def test_tampered_plan_rejected(self):
        self.link()
        run = self.plan()
        with self.assertRaises(Problem):
            self.app.start_apply(run["id"], "wrong-digest")
        (self.app.run_dir(run["id"]) / "plan.bin").write_text("{}")
        self.assertEqual(self.apply(run)["status"], "invalidated")

    def test_changed_source_rejected(self):
        self.link()
        run = self.plan()
        (self.app.run_dir(run["id"]) / "source" / "new-file").write_text("changed")
        self.assertEqual(self.apply(run)["status"], "invalidated")

    def test_changed_state_rejected(self):
        self.link()
        run = self.plan()
        self.state_path().write_text('{"serial": 4}')
        self.assertEqual(self.apply(run)["status"], "invalidated")
        self.assertEqual(json.loads(self.state_path().read_text())["serial"], 4)

    def test_changed_input_rejected(self):
        self.manifest["stacks"][0]["env"] = {"TF_VAR_sample": "OCTOPUSHY_SECRET_TEST"}
        self.commit()
        with patch.dict(os.environ, {"OCTOPUSHY_SECRET_TEST": "original"}):
            self.link()
            run = self.plan()
            os.environ["OCTOPUSHY_SECRET_TEST"] = "changed"
            self.assertEqual(self.apply(run)["status"], "invalidated")

    def test_expired_plan_requires_replanning(self):
        self.link()
        run = self.plan()
        self.app.update_run(run["id"], created_at=time.time() - 3601)
        with self.assertRaisesRegex(Problem, "expired"):
            self.apply(run)

    def test_delete_requires_exact_confirmation(self):
        self.link()
        self.apply(self.plan())
        self.config["generation"] = 0
        self.commit()
        self.app.sync(self.repo_id)
        run = self.plan()
        with self.assertRaisesRegex(Problem, "typing"):
            self.apply(run)
        self.assertEqual(self.apply(run, "vms")["status"], "applied")
        self.assertEqual(self.app.snapshot()["stacks"][0]["resources"], [])

    def test_partial_apply_is_unknown_and_blocks_until_reconciled(self):
        self.config["fail_apply"] = True
        self.commit()
        self.link()
        run = self.apply(self.plan())
        self.assertEqual(run["status"], "outcome_unknown")
        self.assertTrue(self.state_path().exists())
        self.assertIsNone(self.app.snapshot()["stacks"][0]["applied_revision"])
        with self.assertRaises(Problem):
            self.plan()
        with self.assertRaises(Problem):
            self.app.resolve(run["id"])
        self.app.resolve(run["id"], True)
        self.assertEqual(self.plan()["status"], "no_changes")

    def test_restart_does_not_retry_interrupted_run(self):
        self.link()
        run = self.plan()
        self.app.update_run(run["id"], status="applying")
        self.app.close()
        self.app = App(self.root / "data", str(FAKE))
        self.assertEqual(self.app.one("runs", run["id"])["status"], "outcome_unknown")
        with self.assertRaises(Problem):
            self.plan()
        self.assertFalse(self.state_path().exists())

    def test_access_token_rotation_preserves_backend_identity(self):
        self.link()
        self.apply(self.plan())
        self.app.close()
        (self.root / "data" / "access-token").unlink()
        self.app = App(self.root / "data", str(FAKE))
        self.app.launch = lambda target, run_id: target(run_id)
        run = self.plan()
        self.assertEqual(run["status"], "no_changes", run["error"])

    def test_no_implicit_state_or_binding_migration(self):
        self.link()
        self.apply(self.plan())
        original = self.state_path().read_text()
        self.manifest["stacks"][0]["backend"] = "repository"
        self.commit()
        with self.assertRaisesRegex(Problem, "immutable"):
            self.app.sync(self.repo_id)
        self.assertEqual(self.state_path().read_text(), original)
        self.manifest["stacks"] = []
        self.commit()
        with self.assertRaises(Problem):
            self.app.sync(self.repo_id)

    def test_committed_state_and_symlinks_fail_before_tofu(self):
        self.link()
        (self.repo / "terraform.tfstate").write_text('{"resources": []}')
        self.commit()
        self.app.sync(self.repo_id)
        self.assertEqual(self.plan()["status"], "plan_failed")
        self.assertFalse((self.root / "trace").exists())
        (self.repo / "terraform.tfstate").unlink()
        (self.repo / "escape").symlink_to(self.root)
        self.commit()
        self.app.sync(self.repo_id)
        self.assertEqual(self.plan()["status"], "plan_failed")
        self.assertFalse((self.root / "trace").exists())

    def test_existing_remote_state_is_retained(self):
        remote = self.root / "remote-state.json"
        remote.write_text('{"version": 4, "lineage": "existing", "serial": 12, "resources":[]}')
        self.manifest["stacks"][0]["backend"] = "repository"
        self.config.update(backend="http", state_file=str(remote))
        self.commit()
        self.link()
        run = self.plan()
        self.assertEqual(self.apply(run)["status"], "applied")
        value = json.loads(remote.read_text())
        self.assertEqual((value["lineage"], value["serial"]), ("existing", 13))
        self.assertFalse(self.state_path().exists())
        # Even without changing the manifest, backend configuration cannot redirect state.
        self.config["state_file"] = str(self.root / "different-remote")
        self.commit()
        self.app.sync(self.repo_id)
        self.assertEqual(self.plan()["status"], "plan_failed")

    def test_repository_mode_rejects_local_backend(self):
        self.manifest["stacks"][0]["backend"] = "repository"
        self.config["state_file"] = str(self.root / "unsafe-local")
        self.commit()
        self.link()
        run = self.plan()
        self.assertEqual(run["status"], "plan_failed")
        self.assertIn("explicit remote backend", run["error"])

    def test_secret_reference_only_and_redacted_failure(self):
        value = "user@pve!test=very-private-test-value"
        self.app.register_connection("lab", "https://pve.example:8006/", "OCTOPUSHY_SECRET_PVE")
        self.manifest["stacks"][0]["connection"] = "lab"
        self.config["invalid"] = True
        self.commit()
        with patch.dict(os.environ, {"OCTOPUSHY_SECRET_PVE": value}):
            self.link()
            run = self.plan()
            self.assertEqual(run["status"], "plan_failed")
            exposed = json.dumps(self.app.snapshot()) + (self.app.run_dir(run["id"]) / "log.txt").read_text()
            self.assertNotIn(value, exposed)
            self.assertNotIn("very-private-test-value", exposed)
            self.assertIn("[REDACTED]", exposed)

    def test_sensitive_vm_attributes_are_not_projected(self):
        self.link()
        self.app.observe(self.stack["id"], {"resources": [{
            "type": "proxmox_virtual_environment_vm", "name": "vm", "mode": "managed",
            "instances": [{"attributes": {"name": "secret-name", "vm_id": 202},
                           "sensitive_attributes": [[{"type": "get_attr", "value": "name"}]]}]
        }]})
        self.assertEqual(self.app.snapshot()["stacks"][0]["resources"][0]["vm"], {"vm_id": 202})

    def test_untrusted_repo_and_bad_manifest_rejected(self):
        with self.assertRaises(Problem):
            self.app.link_repository("bad", str(self.repo))
        for stacks in ([{"id":"bad","path":"../escape","backend":"managed-local"}], ["not-an-object"]):
            self.manifest["stacks"] = stacks
            self.commit()
            with self.assertRaises(Problem):
                self.link()
        self.assertEqual(self.app.snapshot()["repositories"], [])
        for value in ("https://user:password@example.com/repo", "ext::arbitrary", "-option"):
            with self.assertRaises(Problem):
                source_url(value)

    def test_scaffold_initializes_safe_empty_local_repo(self):
        path = scaffold(self.root / "new", "lab")
        self.assertTrue((path / ".git").is_dir())
        self.assertIn("default = {}", (path / "infra/vms/main.tf").read_text())
        with self.assertRaises(Problem):
            scaffold(path, "lab")
        self.app.register_connection("lab", "https://pve.example:8006/", "OCTOPUSHY_SECRET_PVE")
        repo_id = self.app.initialize_repository("generated", "lab")
        self.assertTrue(self.app.one("repositories", repo_id)["revision"])

    def test_controller_and_execution_locks(self):
        with self.assertRaises(Problem):
            App(self.root / "data", str(FAKE))
        self.config["sleep"] = 0.4
        self.commit()
        self.link()
        self.app.launch = App.launch.__get__(self.app)
        run_id = self.app.start_plan(self.stack["id"])
        with self.assertRaises(Problem):
            self.plan()
        for thread in self.app.threads:
            thread.join(timeout=10)
        self.assertEqual(self.app.one("runs", run_id)["status"], "planned")

    def test_child_retains_lock_after_controller_closes(self):
        child = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(0.3)"],
                                 pass_fds=(self.app.lock_file.fileno(),))
        try:
            self.app.close()
            with self.assertRaises(Problem):
                App(self.root / "data", str(FAKE))
            child.wait(timeout=5)
            self.app = App(self.root / "data", str(FAKE))
        finally:
            if child.poll() is None:
                child.kill()
                child.wait()

    def test_missing_credentials_do_not_block_another_stacks_plan(self):
        self.manifest["stacks"].append({
            "id": "broken", "path": ".", "backend": "managed-local",
            "env": {"TF_VAR_token": "OCTOPUSHY_SECRET_MISSING_TEST_TOKEN"}
        })
        self.commit()
        self.link()
        broken = next(s for s in self.app.snapshot()["stacks"] if s["name"] == "broken")
        with patch.dict(os.environ, {}, clear=True):
            run_id = self.app.start_plan(broken["id"])
        self.assertEqual(self.app.one("runs", run_id)["status"], "plan_failed")
        self.assertEqual(self.plan()["status"], "planned")

    def test_subprocess_time_and_output_limits(self):
        with patch("octopushy.MAX_OUTPUT", 2048):
            with self.assertRaisesRegex(Problem, "output"):
                self.app.command([sys.executable, "-c", "print('x'*4096)"])
        self.app.timeout = 0.1
        with self.assertRaisesRegex(Problem, "timed out"):
            self.app.command([sys.executable, "-c", "import time;time.sleep(10)"])

    def test_api_auth_origin_and_body_validation(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        server.app = self.app
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        self.addCleanup(lambda: (server.shutdown(), thread.join(), server.server_close()))
        def request(method, path, body=None, **headers):
            connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
            connection.request(method, path, body, headers)
            response = connection.getresponse()
            result = response.status, response.read()
            connection.close()
            return result
        self.assertEqual(request("GET", "/api/status")[0], 401)
        auth = {"Authorization": "Bearer " + self.app.token}
        self.assertEqual(request("GET", "/api/status", **auth)[0], 200)
        self.assertEqual(request("GET", "/api/status", Origin="https://evil.example", **auth)[0], 400)
        self.assertEqual(request("GET", "/api/status", Host="evil.example", **auth)[0], 400)
        headers = {**auth, "Content-Type": "application/json"}
        self.assertEqual(request("POST", "/api/connections", "[]", **headers)[0], 400)
        self.assertEqual(request("POST", "/api/connections", "x"*65537, **headers)[0], 400)
        self.assertEqual(request("GET", "/")[0], 200)

    def test_explicit_bind_address_keeps_auth_and_origin_checks(self):
        for value in ("127.0.0.1", "10.1.0.230", "192.168.1.20"):
            self.assertEqual(bind_address(value), value)
        for value in ("0.0.0.0", "::", "localhost", "8.8.8.8", "224.0.0.1"):
            with self.assertRaises(argparse.ArgumentTypeError):
                bind_address(value)
        # Another loopback IP exercises the non-default bind path without a LAN dependency.
        server = ThreadingHTTPServer(("127.0.0.2", 0), Handler)
        server.app = self.app
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        self.addCleanup(lambda: (server.shutdown(), thread.join(), server.server_close()))
        origin = f"http://127.0.0.2:{server.server_port}"
        cases = [
            ({}, 401),
            ({"Authorization": "Bearer incorrect"}, 401),
            ({"Authorization": "Bearer " + self.app.token, "Origin": origin}, 200),
            ({"Authorization": "Bearer " + self.app.token, "Origin": "http://evil.example"}, 400),
            ({"Authorization": "Bearer " + self.app.token, "Host": "evil.example"}, 400),
            ({"Authorization": "Bearer " + self.app.token,
              "Origin": f"http://localhost:{server.server_port}"}, 400),
        ]
        for headers, expected in cases:
            with self.subTest(headers=list(headers), expected=expected):
                connection = http.client.HTTPConnection("127.0.0.2", server.server_port, timeout=5)
                try:
                    connection.request("GET", "/api/status", headers=headers)
                    response = connection.getresponse()
                    self.assertEqual(response.status, expected)
                    response.read()
                finally:
                    connection.close()

    @unittest.skipUnless(os.environ.get("OCTOPUSHY_TEST_TOFU"), "Set OCTOPUSHY_TEST_TOFU for the real CLI smoke test")
    def test_real_opentofu_builtin_resource_and_persistent_local_state(self):
        self.app.tofu = os.environ["OCTOPUSHY_TEST_TOFU"]
        (self.repo / "main.tf").write_text('''terraform {
  backend "local" {}
}
resource "terraform_data" "example" { input = "first" }
''')
        self.commit()
        self.link()
        run = self.plan()
        self.assertEqual(run["status"], "planned", run["error"])
        result = self.apply(run)
        self.assertEqual(result["status"], "applied", result["error"])
        before = json.loads(self.state_path().read_text())
        run = self.plan()
        self.assertEqual(run["status"], "no_changes", run["error"])
        (self.repo / "main.tf").write_text((self.repo / "main.tf").read_text().replace("first", "second"))
        self.commit()
        self.app.sync(self.repo_id)
        run = self.plan()
        self.assertEqual(run["status"], "planned", run["error"])
        result = self.apply(run)
        self.assertEqual(result["status"], "applied", result["error"])
        after = json.loads(self.state_path().read_text())
        self.assertEqual(before["lineage"], after["lineage"])
        self.assertGreater(after["serial"], before["serial"])
        self.assertEqual(after["resources"][0]["instances"][0]["attributes"]["input"]["value"], "second")

    @unittest.skipUnless(os.environ.get("OCTOPUSHY_TEST_TOFU"), "Set OCTOPUSHY_TEST_TOFU for the real CLI smoke test")
    def test_real_opentofu_remote_http_state_and_locking(self):
        store = {"state": None, "lock": None, "lock_requests": 0}
        class Backend(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def reply(self, status, body=b""):
                self.send_response(status)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                self.reply(200 if store["state"] else 404, store["state"] or b"")

            def do_POST(self):
                store["state"] = self.rfile.read(int(self.headers["Content-Length"]))
                self.reply(200)

            def do_LOCK(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                if store["lock"]:
                    self.reply(423, store["lock"])
                else:
                    store["lock"] = body
                    store["lock_requests"] += 1
                    self.reply(200)

            def do_UNLOCK(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                store["lock"] = None
                self.reply(200)

        server = ThreadingHTTPServer(("127.0.0.1", 0), Backend)
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        self.addCleanup(lambda: (server.shutdown(), thread.join(), server.server_close()))
        self.app.tofu = os.environ["OCTOPUSHY_TEST_TOFU"]
        self.manifest["stacks"][0]["backend"] = "repository"
        endpoint = f"http://127.0.0.1:{server.server_port}/state"
        (self.repo / "main.tf").write_text(f'''terraform {{
  backend "http" {{
    address = "{endpoint}"
    lock_address = "{endpoint}"
    unlock_address = "{endpoint}"
  }}
}}
resource "terraform_data" "remote" {{ input = "remote-state-smoke-test" }}
''')
        self.commit()
        self.link()
        run = self.plan()
        self.assertEqual(run["status"], "planned", run["error"])
        result = self.apply(run)
        self.assertEqual(result["status"], "applied", result["error"])
        state = json.loads(store["state"])
        run = self.plan()
        self.assertEqual(run["status"], "no_changes", run["error"])
        self.assertEqual(json.loads(store["state"])["lineage"], state["lineage"])
        self.assertGreaterEqual(store["lock_requests"], 3)
        self.assertFalse(self.state_path().exists())


if __name__ == "__main__":
    unittest.main()
