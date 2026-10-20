#!/usr/bin/env python3
"""Single-admin Git/OpenTofu PoC. Loopback by default; opt-in trusted LAN access."""

import argparse
import contextlib
import fcntl
import hashlib
import hmac
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import selectors
import shutil
import signal
import sqlite3
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit
import uuid

ROOT = Path(__file__).resolve().parent
BUSY = ("planning", "applying")
IDENTIFIER = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")
SECRET_REF = re.compile(r"^OCTOPUSHY_SECRET_[A-Z0-9_]+$")
MAX_OUTPUT = 8 * 1024 * 1024


class Problem(Exception):
    pass


def bind_address(value):
    try:
        address = ipaddress.IPv4Address(value)
    except ipaddress.AddressValueError:
        raise argparse.ArgumentTypeError("Use a specific loopback or private LAN IPv4 address.")
    if not address.is_private or address.is_unspecified or address.is_multicast or address.is_reserved:
        raise argparse.ArgumentTypeError("Use a specific loopback or private LAN IPv4 address, not a wildcard/public address.")
    return str(address)


def encode(value):
    return json.dumps(value, sort_keys=True)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def identifier(value):
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise Problem("Use a name of 1–64 letters, numbers, underscores or hyphens.")
    return value


def base_env():
    # Do not leak the controller's full environment into repositories/providers.
    allowed = ("PATH", "HOME", "LANG", "LC_ALL", "SSH_AUTH_SOCK", "SSL_CERT_FILE",
               "SSL_CERT_DIR", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY")
    env = {k: os.environ[k] for k in allowed if k in os.environ}
    env.update(GIT_TERMINAL_PROMPT="0", GIT_CONFIG_NOSYSTEM="1",
               GIT_CONFIG_GLOBAL=os.devnull, TF_IN_AUTOMATION="1",
               TF_INPUT="0", TF_WORKSPACE="default")
    return env


def redact(text, values):
    for value in sorted(set(values), key=len, reverse=True):
        if value:
            text = text.replace(value, "[REDACTED]")
            # Proxmox's combined token includes a separately printable secret.
            if "=" in value and len(value.rsplit("=", 1)[-1]) >= 8:
                text = text.replace(value.rsplit("=", 1)[-1], "[REDACTED]")
    return text


def source_url(value):
    if not isinstance(value, str) or not value or len(value) > 2048:
        raise Problem("Provide an absolute local repository path or an HTTPS/SSH Git URL.")
    if value.startswith("/"):
        path = Path(value).resolve()
        if not path.is_dir():
            raise Problem("Local repository directory does not exist.")
        return str(path)
    parsed = urlsplit(value)
    if parsed.scheme in ("https", "ssh") and parsed.hostname:
        if parsed.password or parsed.query or parsed.fragment or (
            parsed.scheme == "https" and parsed.username
        ):
            raise Problem("Do not embed credentials or query parameters in repository URLs.")
        return value
    if re.fullmatch(r"[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9_./-]+", value):
        return value
    raise Problem("Only local paths, HTTPS, and SSH Git repositories are supported.")


def scaffold(path, connection):
    """Create a new local repository, never overwrite or push an existing one."""
    identifier(connection)
    path = Path(path)
    if path.exists():
        raise Problem("Repository destination already exists; nothing was overwritten.")
    (path / "infra" / "vms").mkdir(parents=True)
    (path / ".octopushy.json").write_text(json.dumps({
        "version": 1,
        "stacks": [{"id": "vms", "path": "infra/vms", "connection": connection,
                    "backend": "managed-local"}],
    }, indent=2) + "\n")
    (path / ".gitignore").write_text(
        ".terraform/\n*.tfstate\n*.tfstate.*\n*.tfplan\n*.auto.tfvars\n"
        "*.auto.tfvars.json\ncrash.log\n.env\n"
    )
    (path / "infra/vms/main.tf").write_text('''terraform {
  required_version = ">= 1.6.0"
  required_providers {
    # Select a release compatible with your Proxmox version, then commit the lock file.
    proxmox = { source = "bpg/proxmox" }
  }
  backend "local" {}
}

# Octopushy supplies PROXMOX_VE_ENDPOINT and PROXMOX_VE_API_TOKEN at execution time.
# TLS verification stays enabled. Configure trust for your private CA on the host.
provider "proxmox" {}

variable "vms" {
  description = "Explicitly opt in to cloning VMs. The initial repository creates none."
  type = map(object({
    node_name   = string
    vm_id       = number
    template_id = number
  }))
  default = {}
}

resource "proxmox_virtual_environment_vm" "vm" {
  for_each  = var.vms
  name      = each.key
  node_name = each.value.node_name
  vm_id     = each.value.vm_id
  clone {
    vm_id = each.value.template_id
    full  = true
  }
  # Review template networking, guest-agent and startup settings before use.
  started = false
}
''')
    (path / "infra/vms/vms.tfvars.example").write_text('''# For Git-driven changes, edit variable "vms" default in main.tf and commit it.
# Alternatively force-add a reviewed non-secret .auto.tfvars file (ignored by default).
# Each VMID must be unused; the template must be available on the selected node.
vms = {
  "test-vm" = { node_name = "pve-a", vm_id = 201, template_id = 9000 }
}
''')
    (path / "README.md").write_text(
        "# Homelab OpenTofu repository\n\n"
        "Review `infra/vms/main.tf` before running it. Initially no VMs are declared.\n"
        "Select a bpg/proxmox version compatible with your Proxmox installation; run\n"
        "`tofu init` and commit `.terraform.lock.hcl` for reproducible future plans.\n"
        "Provide credentials through the registered connection, never Git.\n\n"
        "This manifest opts into a NEW controller-managed local state. Do not use it\n"
        "to take over existing infrastructure without a reviewed state migration.\n"
        "For an existing remote backend, set `backend` to `repository` and retain\n"
        "its committed backend block. No backend migration is performed by the app.\n"
    )
    env = base_env()
    for args in (["init", "--initial-branch=main", "--template=", str(path)],
                 ["-C", str(path), "add", "."],
                 ["-C", str(path), "-c", "user.name=Octopushy",
                  "-c", "user.email=octopushy@localhost", "commit", "-m", "Initialize homelab"]):
        subprocess.run(["git", *args], env=env, check=True, capture_output=True)
    return path


class App:
    def __init__(self, data, tofu="tofu", timeout=1800):
        self.data = Path(data).resolve()
        self.data.mkdir(parents=True, exist_ok=True, mode=0o700)
        os.chmod(self.data, 0o700)
        self.lock_file = open(self.data / "controller.lock", "a+")
        try:
            fcntl.flock(self.lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            self.lock_file.close()
            raise Problem("Controller or inherited OpenTofu process still holds the data lock.")
        try:
            self.initialize(tofu, timeout)
        except BaseException:
            self.lock_file.close()
            raise

    def initialize(self, tofu, timeout):
        self.tofu = shutil.which(tofu) or tofu
        self.timeout = timeout
        self.guard = threading.RLock()
        self.threads = []
        self.token_file = self.data / "access-token"
        if not self.token_file.exists():
            self.token_file.write_text(secrets.token_urlsafe(32))
        os.chmod(self.token_file, 0o600)
        self.token = self.token_file.read_text().strip()
        if not self.token:
            raise Problem("Access token file must not be empty.")
        key_file = self.data / "fingerprint-key"
        if not key_file.exists():
            key_file.write_bytes(secrets.token_bytes(32))
        os.chmod(key_file, 0o600)
        self.fingerprint_key = key_file.read_bytes()
        if len(self.fingerprint_key) != 32:
            raise Problem("Fingerprint key is corrupt; restore it from backup.")
        with self.db() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS connections (
                  name TEXT PRIMARY KEY, endpoint TEXT NOT NULL, token_env TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS repositories (
                  id TEXT PRIMARY KEY, name TEXT UNIQUE NOT NULL, source TEXT NOT NULL,
                  branch TEXT NOT NULL, revision TEXT, synced_at REAL);
                CREATE TABLE IF NOT EXISTS stacks (
                  id TEXT PRIMARY KEY, repo_id TEXT NOT NULL, name TEXT NOT NULL,
                  config TEXT NOT NULL, applied_revision TEXT, checked_revision TEXT,
                  backend_fingerprint TEXT, observed_at REAL, resources TEXT,
                  UNIQUE(repo_id, name));
                CREATE TABLE IF NOT EXISTS runs (
                  id TEXT PRIMARY KEY, stack_id TEXT NOT NULL, revision TEXT NOT NULL,
                  status TEXT NOT NULL, created_at REAL NOT NULL, updated_at REAL NOT NULL,
                  plan_hash TEXT, source_hash TEXT, input_hash TEXT, state_hash TEXT,
                  tool_hash TEXT, summary TEXT, error TEXT, approved_at REAL, approval TEXT);
            """)
            # Never redispatch an interrupted process; its side effects may have occurred.
            db.execute("UPDATE runs SET status='outcome_unknown', error=? WHERE status IN (?,?)",
                       ("Controller stopped during execution. Reconcile before continuing.", *BUSY))

    @contextlib.contextmanager
    def db(self):
        with contextlib.closing(sqlite3.connect(self.data / "controller.sqlite3", timeout=10)) as db:
            db.row_factory = sqlite3.Row
            with db:
                yield db

    def close(self):
        for thread in self.threads:
            thread.join()
        self.lock_file.close()

    def one(self, table, key):
        with self.db() as db:
            row = db.execute(f"SELECT * FROM {table} WHERE id=?", (key,)).fetchone()
        if not row:
            raise Problem("Record not found.")
        return dict(row)

    def update_run(self, run_id, **fields):
        fields["updated_at"] = time.time()
        with self.db() as db:
            db.execute("UPDATE runs SET " + ",".join(f"{k}=?" for k in fields) + " WHERE id=?",
                       (*fields.values(), run_id))

    def command(self, args, cwd=None, env=None, run_id=None, allowed=(0,), quiet=False):
        """No shell. Bound time/output; keep the controller lock in the child."""
        env = env or base_env()
        hidden = [v for k, v in env.items() if k.startswith(("PROXMOX_", "TF_VAR_", "TF_HTTP_", "AWS_"))]
        with subprocess.Popen(
            args, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            start_new_session=True, pass_fds=(self.lock_file.fileno(),),
        ) as child:
            buffers = [bytearray(), bytearray()]
            deadline = time.monotonic() + self.timeout
            size = 0
            try:
                with selectors.DefaultSelector() as selector:
                    selector.register(child.stdout, selectors.EVENT_READ, 0)
                    selector.register(child.stderr, selectors.EVENT_READ, 1)
                    while selector.get_map():
                        if time.monotonic() >= deadline:
                            raise Problem("Execution timed out; external effects may need reconciliation.")
                        for key, _ in selector.select(timeout=0.1):
                            chunk = os.read(key.fd, 65536)
                            if not chunk:
                                selector.unregister(key.fileobj)
                                continue
                            size += len(chunk)
                            if size > MAX_OUTPUT:
                                raise Problem("Command output exceeded the 8 MiB limit.")
                            buffers[key.data].extend(chunk)
                    try:
                        child.wait(timeout=max(0.01, deadline - time.monotonic()))
                    except subprocess.TimeoutExpired:
                        raise Problem("Execution timed out; external effects may need reconciliation.")
            except BaseException:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(child.pid, signal.SIGTERM)
                try:
                    child.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    pass
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(child.pid, signal.SIGKILL)
                child.wait()
                raise
        out, err = buffers
        stdout = out.decode("utf-8", errors="replace")
        stderr = err.decode("utf-8", errors="replace")
        if run_id and not quiet:
            with (self.run_dir(run_id) / "log.txt").open("a") as log:
                log.write(redact(f"\n$ {Path(args[0]).name} {args[1]}\n{stdout}{stderr}", hidden))
        if child.returncode not in allowed:
            message = redact(stderr or stdout or f"Command exited {child.returncode}", hidden)
            raise Problem(message[-4000:])
        return child.returncode, stdout, stderr

    def git(self, repo, *args):
        return self.command(["git", "-c", "protocol.ext.allow=never",
                             "--git-dir", str(self.data / "git" / repo["id"]), *args])[1].strip()

    def register_connection(self, name, endpoint, token_env):
        identifier(name)
        if not isinstance(endpoint, str) or not isinstance(token_env, str):
            raise Problem("Endpoint and credential reference must be strings.")
        parsed = urlsplit(endpoint)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username
                or parsed.password or parsed.query or parsed.fragment):
            raise Problem("Proxmox endpoint must be HTTPS, without credentials/query/fragment.")
        if not SECRET_REF.fullmatch(token_env):
            raise Problem("Credential references must name an OCTOPUSHY_SECRET_* variable.")
        with self.db() as db:
            if db.execute("SELECT 1 FROM connections WHERE name=?", (name,)).fetchone():
                raise Problem("Connection already exists. Rotate its environment secret and restart.")
            db.execute("INSERT INTO connections VALUES (?,?,?)", (name, endpoint, token_env))

    def link_repository(self, name, source, branch="main", trusted=False):
        if trusted is not True:
            raise Problem("Only explicitly trusted repositories may execute on this host.")
        identifier(name)
        source = source_url(source)
        if not isinstance(branch, str) or not branch or branch.startswith("-"):
            raise Problem("Provide a valid Git branch.")
        self.command(["git", "check-ref-format", "--branch", branch])
        repo_id = uuid.uuid4().hex
        folder = self.data / "git" / repo_id
        folder.parent.mkdir(exist_ok=True)
        with self.guard:
            self.ensure_idle()
            with self.db() as db:
                if db.execute("SELECT 1 FROM repositories WHERE name=?", (name,)).fetchone():
                    raise Problem("Repository name already linked.")
            self.command(["git", "-c", "protocol.ext.allow=never", "clone",
                          "--bare", "--no-hardlinks", "--template=", "--", source, str(folder)])
            with self.db() as db:
                db.execute("INSERT INTO repositories (id,name,source,branch) VALUES (?,?,?,?)",
                           (repo_id, name, source, branch))
            try:
                self.sync(repo_id)
            except Exception:
                with self.db() as db:
                    db.execute("DELETE FROM repositories WHERE id=?", (repo_id,))
                shutil.rmtree(folder)
                raise
        return repo_id

    def initialize_repository(self, name, connection):
        identifier(name)
        with self.db() as db:
            if not db.execute("SELECT 1 FROM connections WHERE name=?", (connection,)).fetchone():
                raise Problem("Register the connection first.")
        path = scaffold(self.data / "sources" / name, connection)
        return self.link_repository(name, str(path), trusted=True)

    def ensure_idle(self):
        # ponytail: one controller-wide execution slot; per-stack reservations come later.
        with self.db() as db:
            if db.execute("SELECT 1 FROM runs WHERE status IN (?,?,?)",
                          (*BUSY, "outcome_unknown")).fetchone():
                raise Problem("Execution is active or unresolved; inspect its run first.")

    def manifest(self, repo, revision):
        try:
            value = json.loads(self.git(repo, "show", f"{revision}:.octopushy.json"))
        except (ValueError, Problem):
            raise Problem("The revision must contain a valid .octopushy.json manifest.")
        if not isinstance(value, dict) or value.get("version") != 1 or not isinstance(value.get("stacks"), list):
            raise Problem("Expected manifest version 1 and a stacks array.")
        if not 1 <= len(value["stacks"]) <= 20:
            raise Problem("Declare 1–20 stacks.")
        seen = set()
        result = []
        for config in value["stacks"]:
            if not isinstance(config, dict):
                raise Problem("Each stack must be an object.")
            name = identifier(config.get("id"))
            if name in seen:
                raise Problem("Duplicate stack ID.")
            seen.add(name)
            path = config.get("path", ".")
            if (not isinstance(path, str) or not path or "\x00" in path or PurePosixPath(path).is_absolute()
                    or ".." in PurePosixPath(path).parts or "\\" in path):
                raise Problem("Stack path must stay within the repository.")
            backend = config.get("backend")
            if backend not in ("managed-local", "repository"):
                raise Problem("Set backend to managed-local (NEW state) or repository (remote).")
            refs = config.get("env", {})
            if not isinstance(refs, dict):
                raise Problem("env must map permitted environment names to secret references.")
            for key, ref in refs.items():
                if not re.fullmatch(r"(TF_VAR_[A-Za-z0-9_]+|AWS_[A-Z0-9_]+|TF_HTTP_USERNAME|TF_HTTP_PASSWORD)", key):
                    raise Problem("Only TF_VAR_*, AWS_*, and TF_HTTP_USERNAME/PASSWORD mappings are supported.")
                if not isinstance(ref, str) or not SECRET_REF.fullmatch(ref):
                    raise Problem("Environment mappings require OCTOPUSHY_SECRET_* references.")
            connection = config.get("connection")
            if connection:
                identifier(connection)
                with self.db() as db:
                    if not db.execute("SELECT 1 FROM connections WHERE name=?", (connection,)).fetchone():
                        raise Problem(f"Register connection {connection!r} before linking.")
            result.append({"id": name, "path": str(PurePosixPath(path)), "backend": backend,
                           "connection": connection, "env": refs})
        return result

    def sync(self, repo_id):
        with self.guard:
            self.ensure_idle()
            repo = self.one("repositories", repo_id)
            ref = f"refs/heads/{repo['branch']}"
            self.git(repo, "fetch", "--prune", "origin", f"+{ref}:{ref}")
            revision = self.git(repo, "rev-parse", "--verify", f"{ref}^{{commit}}")
            configs = self.manifest(repo, revision)
            with self.db() as db:
                existing = {r["name"]: dict(r) for r in db.execute(
                    "SELECT * FROM stacks WHERE repo_id=?", (repo_id,))}
                if set(existing) - {c["id"] for c in configs}:
                    raise Problem("Removing a registered stack requires manual state reconciliation.")
                for config in configs:
                    old = existing.get(config["id"])
                    if old and json.loads(old["config"]) != config:
                        raise Problem("Stack bindings are immutable in this PoC; no automatic backend/path/credential migration.")
                for config in configs:
                    if config["id"] not in existing:
                        db.execute("INSERT INTO stacks (id,repo_id,name,config) VALUES (?,?,?,?)",
                                   (uuid.uuid4().hex, repo_id, config["id"], encode(config)))
                db.execute("UPDATE repositories SET revision=?,synced_at=? WHERE id=?",
                           (revision, time.time(), repo_id))
                if repo["revision"] != revision:
                    db.execute("""UPDATE runs SET status='superseded', updated_at=?
                        WHERE status='planned' AND stack_id IN
                        (SELECT id FROM stacks WHERE repo_id=?)""", (time.time(), repo_id))
            return revision

    def run_dir(self, run_id):
        return self.data / "runs" / run_id

    def environment(self, config):
        env = base_env()
        if config["connection"]:
            with self.db() as db:
                conn = db.execute("SELECT * FROM connections WHERE name=?",
                                  (config["connection"],)).fetchone()
            secret = os.environ.get(conn["token_env"])
            if not secret:
                raise Problem(f"Missing credential environment variable {conn['token_env']}.")
            env.update(PROXMOX_VE_ENDPOINT=conn["endpoint"], PROXMOX_VE_API_TOKEN=secret)
        for key, ref in config["env"].items():
            if not os.environ.get(ref):
                raise Problem(f"Missing environment reference {ref}.")
            env[key] = os.environ[ref]
        return env

    def input_hash(self, env):
        # Keyed fingerprints do not expose low-entropy input values via plain hashes.
        return hmac.new(self.fingerprint_key, encode(env).encode(), hashlib.sha256).hexdigest()

    def source_hash(self, work):
        files = []
        for path in sorted(work.rglob("*")):
            relative = path.relative_to(work)
            if ".git" in relative.parts or ".terraform" in relative.parts:
                continue
            if path.is_symlink():
                raise Problem("Symlinks in execution snapshots are not supported.")
            if path.is_file():
                files.append((str(relative), digest(path.read_bytes())))
        return digest(encode(files).encode())

    def start_plan(self, stack_id):
        with self.guard:
            self.ensure_idle()
            stack = self.one("stacks", stack_id)
            repo = self.one("repositories", stack["repo_id"])
            if not shutil.which(self.tofu):
                raise Problem("OpenTofu executable not found. Install tofu or set --tofu.")
            run_id = uuid.uuid4().hex
            self.run_dir(run_id).mkdir(parents=True, mode=0o700)
            now = time.time()
            with self.db() as db:
                db.execute("UPDATE runs SET status='superseded' WHERE stack_id=? AND status='planned'",
                           (stack_id,))
                db.execute("""INSERT INTO runs
                    (id,stack_id,revision,status,created_at,updated_at)
                    VALUES (?,?,?,'planning',?,?)""", (run_id, stack_id, repo["revision"], now, now))
            self.launch(self.plan, run_id)
            return run_id

    def launch(self, target, run_id):
        self.threads = [thread for thread in self.threads if thread.is_alive()]
        thread = threading.Thread(target=target, args=(run_id,), daemon=False)
        self.threads.append(thread)
        thread.start()

    def context(self, run_id):
        run = self.one("runs", run_id)
        stack = self.one("stacks", run["stack_id"])
        config = json.loads(stack["config"])
        work = self.run_dir(run_id) / "source"
        cwd = (work / config["path"]).resolve()
        if not cwd.is_relative_to(work.resolve()) or not cwd.is_dir():
            raise Problem("Stack directory missing or outside its snapshot.")
        return run, stack, config, work, cwd, self.environment(config)

    def tofu_command(self, cwd, env, run_id, *args, **kwargs):
        return self.command([self.tofu, *args], cwd=cwd, env=env, run_id=run_id, **kwargs)

    def state(self, cwd, env, run_id, config, stack_id):
        if config["backend"] == "managed-local":
            path = self.data / "states" / stack_id / "terraform.tfstate"
            text = path.read_text() if path.exists() else ""
        else:
            code, text, err = self.tofu_command(
                cwd, env, run_id, "state", "pull", allowed=(0, 1), quiet=True)
            if code:
                if "No state file was found" in err or "No state file was found" in text:
                    text = ""
                else:
                    raise Problem("Remote state could not be read; refusing to assume it is empty.")
        value = json.loads(text) if text.strip() else {}
        if not isinstance(value, dict) or (value and value.get("version") != 4):
            raise Problem("Expected conventional version-4 JSON state; encrypted/legacy state requires explicit integration.")
        return digest(encode(value).encode()), value

    def observe(self, stack_id, state):
        # Never expose raw state, generic resource attributes, outputs, or secret values.
        resources = []
        for resource in state.get("resources", []):
            if resource.get("mode") != "managed":
                continue
            for instance in resource.get("instances", []):
                entry = {"type": resource.get("type"), "name": resource.get("name"),
                         "module": resource.get("module", "")}
                if resource.get("type") == "proxmox_virtual_environment_vm":
                    attrs = instance.get("attributes", {})
                    hidden = {path[0].get("value") if isinstance(path[0], dict) else path[0]
                              for path in instance.get("sensitive_attributes", []) if path}
                    if [] in instance.get("sensitive_attributes", []):
                        hidden.update(attrs)
                    entry["vm"] = {k: attrs[k] for k in ("vm_id", "node_name", "name", "started")
                                   if k in attrs and k not in hidden}
                resources.append(entry)
        with self.db() as db:
            db.execute("UPDATE stacks SET resources=?,observed_at=? WHERE id=?",
                       (encode(resources), time.time(), stack_id))

    def plan(self, run_id):
        try:
            run = self.one("runs", run_id)
            stack = self.one("stacks", run["stack_id"])
            repo = self.one("repositories", stack["repo_id"])
            work = self.run_dir(run_id) / "source"
            self.git(repo, "worktree", "add", "--detach", str(work), run["revision"])
            run, stack, config, work, cwd, env = self.context(run_id)
            self.source_hash(work)  # Validate symlinks before invoking repository code.
            if any(".terraform" in p.relative_to(work).parts or ".tfstate" in p.name
                   for p in work.rglob("*")):
                raise Problem("Repository must not contain state or .terraform data. Migrate existing state explicitly.")
            init = ["init", "-input=false", "-no-color"]
            if config["backend"] == "managed-local":
                state_dir = self.data / "states" / stack["id"]
                state_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
                init += [f"-backend-config=path={state_dir / 'terraform.tfstate'}",
                         f"-backend-config=workspace_dir={state_dir / 'workspaces'}"]
            self.tofu_command(cwd, env, run_id, *init)
            metadata = json.loads((cwd / ".terraform/terraform.tfstate").read_text())
            backend = metadata.get("backend", {})
            kind = backend.get("type")
            if config["backend"] == "repository" and kind in (None, "local"):
                raise Problem("Repository mode requires an explicit remote backend; local state must be managed-local.")
            if config["backend"] == "managed-local" and kind != "local":
                raise Problem("Managed-local requires the local backend. Existing backends are never migrated.")
            fingerprint = hmac.new(self.fingerprint_key, encode(backend).encode(), hashlib.sha256).hexdigest()
            if stack["backend_fingerprint"] and stack["backend_fingerprint"] != fingerprint:
                raise Problem("Backend identity/configuration changed; manual migration is required.")
            with self.db() as db:
                db.execute("UPDATE stacks SET backend_fingerprint=? WHERE id=?", (fingerprint, stack["id"]))
            self.tofu_command(cwd, env, run_id, "validate", "-no-color")
            tool = self.tofu_command(cwd, env, run_id, "version", "-json", quiet=True)[1]
            before, _ = self.state(cwd, env, run_id, config, stack["id"])
            plan_file = self.run_dir(run_id) / "plan.bin"
            code, _, _ = self.tofu_command(
                cwd, env, run_id, "plan", "-input=false", "-no-color", "-detailed-exitcode",
                "-lock-timeout=30s", f"-out={plan_file}", allowed=(0, 2))
            raw = self.tofu_command(cwd, env, run_id, "show", "-json", str(plan_file), quiet=True)[1]
            plan = json.loads(raw)
            changes = [{"address": r["address"], "actions": r["change"]["actions"]}
                       for r in plan.get("resource_changes", [])
                       if r["change"]["actions"] != ["no-op"]]
            summary = {"changes": changes, "outputs_changed": any(
                c.get("actions") != ["no-op"] for c in plan.get("output_changes", {}).values())}
            self.tofu_command(cwd, env, run_id, "show", "-no-color", str(plan_file))
            after, state = self.state(cwd, env, run_id, config, stack["id"])
            if before != after:
                raise Problem("State changed while planning; create a fresh plan.")
            self.observe(stack["id"], state)
            with self.db() as db:
                db.execute("UPDATE stacks SET checked_revision=? WHERE id=?", (run["revision"], stack["id"]))
            self.update_run(run_id, status="planned" if code == 2 else "no_changes",
                            plan_hash=digest(plan_file.read_bytes()), summary=encode(summary),
                            source_hash=self.source_hash(work), input_hash=self.input_hash(env),
                            state_hash=after, tool_hash=digest(tool.encode()))
        except Exception as exc:
            self.update_run(run_id, status="plan_failed", error=str(exc)[:4000])

    def start_apply(self, run_id, plan_hash, confirm_destroy=""):
        with self.guard:
            self.ensure_idle()
            run = self.one("runs", run_id)
            if (run["status"] != "planned" or not isinstance(plan_hash, str)
                    or not hmac.compare_digest(run["plan_hash"], plan_hash)):
                raise Problem("This is not the currently reviewable saved plan.")
            stack = self.one("stacks", run["stack_id"])
            changes = json.loads(run["summary"])["changes"]
            if any("delete" in item["actions"] for item in changes) and confirm_destroy != stack["name"]:
                raise Problem("Destruction/replacement requires typing the stack name.")
            now = time.time()
            if now - run["created_at"] > 3600:
                raise Problem("Plan expired after one hour; create a fresh plan.")
            self.update_run(run_id, status="applying", approved_at=now,
                            approval=encode({"plan_hash": plan_hash, "actor": "local-admin"}))
            self.launch(self.apply, run_id)
            return run_id

    def apply(self, run_id):
        invoked = False
        try:
            run, stack, config, work, cwd, env = self.context(run_id)
            repo = self.one("repositories", stack["repo_id"])
            # Re-fetch before approval execution: a moving branch cannot silently replace a plan.
            ref = f"refs/heads/{repo['branch']}"
            self.git(repo, "fetch", "--prune", "origin", f"+{ref}:{ref}")
            head = self.git(repo, "rev-parse", "--verify", f"{ref}^{{commit}}")
            tool = self.tofu_command(cwd, env, run_id, "version", "-json", quiet=True)[1]
            state_hash, _ = self.state(cwd, env, run_id, config, stack["id"])
            plan_file = self.run_dir(run_id) / "plan.bin"
            if (head != run["revision"] or repo["revision"] != run["revision"]
                    or digest(plan_file.read_bytes()) != run["plan_hash"]
                    or self.source_hash(work) != run["source_hash"]
                    or self.input_hash(env) != run["input_hash"]
                    or state_hash != run["state_hash"] or digest(tool.encode()) != run["tool_hash"]):
                raise Problem("Plan, source, inputs, tool, state, or Git branch changed; re-sync and re-plan.")
            invoked = True
            self.tofu_command(cwd, env, run_id, "apply", "-input=false", "-no-color",
                              "-lock-timeout=30s", str(plan_file))
            with self.db() as db:
                db.execute("UPDATE stacks SET applied_revision=? WHERE id=?", (run["revision"], stack["id"]))
            try:
                _, state = self.state(cwd, env, run_id, config, stack["id"])
                self.observe(stack["id"], state)
                self.update_run(run_id, status="applied")
            except Exception:
                self.update_run(run_id, status="applied_unverified",
                                error="Apply exited successfully, but state observation failed. Do not replay apply.")
        except Exception as exc:
            # Conservative: even a nonzero/timeout apply may have effects or surviving remote work.
            self.update_run(run_id, status="outcome_unknown" if invoked else "invalidated",
                            error=str(exc)[:4000])

    def resolve(self, run_id, acknowledged=False):
        with self.guard:
            run = self.one("runs", run_id)
            if not acknowledged or run["status"] != "outcome_unknown":
                raise Problem("Only unresolved runs can be reconciled after explicit operator acknowledgement.")
            self.update_run(run_id, status="reconciled",
                            error="Operator confirmed old execution stopped and inspected state. A fresh plan is required.")

    def snapshot(self):
        with self.db() as db:
            result = {table: [dict(r) for r in db.execute(f"SELECT * FROM {table}")]
                      for table in ("connections", "repositories", "stacks")}
            result["runs"] = [dict(r) for r in db.execute(
                "SELECT * FROM runs ORDER BY created_at DESC LIMIT 100")]
        for stack in result["stacks"]:
            stack["config"] = json.loads(stack["config"])
            stack["resources"] = json.loads(stack["resources"] or "[]")
            stack.pop("backend_fingerprint", None)
        for run in result["runs"]:
            run["summary"] = json.loads(run["summary"] or "{}")
            for key in ("input_hash", "state_hash", "tool_hash", "source_hash", "approval"):
                run.pop(key, None)
        for conn in result["connections"]:
            conn["credential_available"] = bool(os.environ.get(conn["token_env"]))
        result["tofu_available"] = bool(shutil.which(self.tofu))
        return result


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass  # Request paths/headers must never become a token log.

    def send(self, status, data, content_type="application/json"):
        body = json.dumps(data).encode() if content_type == "application/json" else data
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; script-src 'self'; style-src 'self'; "
                         "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'")
        self.end_headers()
        self.wfile.write(body)

    def handle_request(self):
        app = self.server.app
        host = self.headers.get("Host", "")
        address = self.server.server_address[0]
        allowed_hosts = {f"{address}:{self.server.server_port}"}
        if address == "127.0.0.1":
            allowed_hosts.add(f"localhost:{self.server.server_port}")
        if host not in allowed_hosts:
            raise Problem("Unrecognized Host. Use the configured dashboard address.")
        origin = self.headers.get("Origin")
        if origin and origin != f"http://{host}":
            raise Problem("Cross-origin requests are forbidden.")
        if self.command == "GET" and self.path in ("/", "/app.js", "/style.css"):
            files = {"/": ("index.html", "text/html; charset=utf-8"),
                     "/app.js": ("app.js", "text/javascript; charset=utf-8"),
                     "/style.css": ("style.css", "text/css; charset=utf-8")}
            filename, kind = files[self.path]
            return self.send(200, (ROOT / "web" / filename).read_bytes(), kind)
        token = self.headers.get("Authorization", "").removeprefix("Bearer ")
        if not hmac.compare_digest(token, app.token):
            return self.send(401, {"error": "Enter the access token from the controller's private token file."})
        if self.command == "GET":
            if self.path == "/api/status":
                return self.send(200, app.snapshot())
            match = re.fullmatch(r"/api/runs/([0-9a-f]{32})/log", self.path)
            if match:
                app.one("runs", match[1])
                path = app.run_dir(match[1]) / "log.txt"
                text, truncated = "", False
                if path.exists():
                    with path.open("rb") as log:
                        size = log.seek(0, os.SEEK_END)
                        truncated = size > 1000000
                        log.seek(max(0, size - 1000000))
                        text = log.read().decode("utf-8", errors="replace")
                return self.send(200, {"log": text, "truncated": truncated})
            return self.send(404, {"error": "Not found"})
        if self.headers.get("Content-Type") != "application/json":
            raise Problem("Expected application/json.")
        length = int(self.headers.get("Content-Length", "0"))
        if not 0 < length <= 65536:
            raise Problem("Request must contain 1–65536 bytes.")
        body = json.loads(self.rfile.read(length))
        if not isinstance(body, dict):
            raise Problem("Expected a JSON object.")
        if self.path == "/api/connections":
            app.register_connection(body["name"], body["endpoint"], body["token_env"])
            return self.send(201, {"ok": True})
        if self.path == "/api/repositories":
            result = app.link_repository(body["name"], body["source"], body.get("branch", "main"),
                                         body.get("trusted", False))
            return self.send(201, {"id": result})
        if self.path == "/api/repositories/initialize":
            return self.send(201, {"id": app.initialize_repository(body["name"], body["connection"])})
        match = re.fullmatch(r"/api/repositories/([0-9a-f]{32})/sync", self.path)
        if match:
            return self.send(200, {"revision": app.sync(match[1])})
        match = re.fullmatch(r"/api/stacks/([0-9a-f]{32})/plan", self.path)
        if match:
            return self.send(202, {"id": app.start_plan(match[1])})
        match = re.fullmatch(r"/api/runs/([0-9a-f]{32})/apply", self.path)
        if match:
            return self.send(202, {"id": app.start_apply(match[1], body.get("plan_hash", ""),
                                                        body.get("confirm_destroy", ""))})
        return self.send(404, {"error": "Not found"})

    def dispatch(self):
        try:
            self.handle_request()
        except (Problem, ValueError, KeyError, TypeError) as exc:
            self.send(400, {"error": str(exc)[:4000]})
        except Exception:
            self.send(500, {"error": "Internal error. Check local configuration and storage."})

    do_GET = dispatch
    do_POST = dispatch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-dir", default=".octopushy")
    parser.add_argument("--tofu", default="tofu")
    sub = parser.add_subparsers(dest="command", required=True)
    serve = sub.add_parser("serve")
    serve.add_argument("--host", type=bind_address, default="127.0.0.1",
                       help="Specific private LAN IPv4 for trusted-network HTTP access; defaults to loopback.")
    serve.add_argument("--port", type=int, default=8088)
    init = sub.add_parser("init-repo")
    init.add_argument("path")
    init.add_argument("--connection", required=True)
    resolve = sub.add_parser("resolve-run")
    resolve.add_argument("run_id")
    resolve.add_argument("--acknowledge-stopped-and-state-inspected", action="store_true")
    args = parser.parse_args()
    os.umask(0o077)
    if args.command == "init-repo":
        print(scaffold(args.path, args.connection))
        return
    app = App(args.data_dir, args.tofu)
    try:
        if args.command == "resolve-run":
            app.resolve(args.run_id, args.acknowledge_stopped_and_state_inspected)
            print("Recorded reconciliation. Restart the server and create a fresh plan.")
        else:
            server = ThreadingHTTPServer((args.host, args.port), Handler)
            server.app = app
            print(f"Dashboard: http://{args.host}:{server.server_port}", flush=True)
            print(f"Access token file (private): {app.token_file}", flush=True)
            if not ipaddress.ip_address(args.host).is_loopback:
                print("WARNING: LAN HTTP is unencrypted, including the access token. "
                      "Trusted LAN only; never port-forward this server.", flush=True)
            print("Trusted configuration only. Not a sandbox or a production network server.", flush=True)
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                print("\nWaiting for active execution to finish; do not force-kill an apply.")
            finally:
                server.server_close()
    finally:
        app.close()


if __name__ == "__main__":
    try:
        main()
    except (Problem, subprocess.CalledProcessError) as exc:
        raise SystemExit(str(exc))
