"""Explicit live test on the single VM just provisioned by tests/proxmox/run.sh.

VM lifecycle belongs to the controller's PostgreSQL OpenTofu state. NixOS
configuration/Portainer lifecycle here is native, not a controller executor.
No blanket cleanup on failure: retain evidence and inspect uncertain outcomes.
"""
import datetime
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

NIX_IMAGE = "docker.io/nixos/nix@sha256:53b5ec518b696373829ea305c3c07a8c870113e44648c88ec484ef078550fd4f"
PROBE_IMAGE = "docker.io/library/busybox@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e"


class Live:
    def __init__(self, work, config):
        self.work, self.config = work.resolve(), config.resolve()
        self.output = self.work / "portainer"
        self.output.mkdir(mode=0o700, exist_ok=True)
        self.vmid = int((self.work / "vmid").read_text())
        self.name = os.environ["TEST_NAME"]
        self.node = os.environ["PVE_NODE"]
        self.token = Path(os.environ["PVE_TOKEN_FILE"]).read_text().strip()
        self.prefix = f"/nodes/{urllib.parse.quote(self.node, safe='')}/qemu/{self.vmid}"
        self.pve_url = os.environ["PVE_ENDPOINT"].rstrip("/") + "/api2/json"
        # Same explicitly selected trusted-LAN TLS policy as this test installation.
        self.pve_tls = ssl._create_unverified_context()
        self.password_file = self.output / "admin-password"
        if not self.password_file.exists():
            with self.password_file.open("x") as file:
                file.write(secrets.token_urlsafe(24) + "\n")
            self.password_file.chmod(0o600)
        self.password = self.password_file.read_text().strip()
        self.results = {}
        self.tunnel = None
        self.jwt = None

    def pve(self, path, data=None):
        request = urllib.request.Request(self.pve_url + path,
            data=None if data is None else json.dumps(data).encode(),
            headers={"Authorization": "PVEAPIToken=" + self.token, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, context=self.pve_tls, timeout=45) as response:
            return json.load(response)["data"]

    def check(self, name, passed=True):
        if not passed:
            raise RuntimeError("Verification failed: " + name)
        self.results[name] = True
        self.save()
        print("PASS:", name, flush=True)

    def save(self):
        (self.output / "verification.json").write_text(json.dumps({
            "at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "vmid": self.vmid, "name": self.name,
            "address": getattr(self, "ip", None),
            "checks": self.results,
        }, indent=2) + "\n")

    def guest_command(self, command):
        pid = self.pve(self.prefix + "/agent/exec", {"command": command})["pid"]
        for _ in range(60):
            result = self.pve(self.prefix + "/agent/exec-status?pid=" + str(pid))
            if result.get("exited"):
                if result.get("exitcode") != 0:
                    raise RuntimeError("Guest-agent identity command failed")
                return result.get("out-data", "")
            time.sleep(1)
        raise TimeoutError("Guest-agent command outcome unknown; inspect before retrying")

    def discover(self):
        vm = self.pve(self.prefix + "/config")
        if (vm.get("name") != self.name
                or "octopushy" not in vm.get("tags", "").split(";")
                or f"home/{self.node}/{self.name}" not in vm.get("description", "")):
            raise RuntimeError("Refusing a VM that does not match the controller-owned test identity")
        self.mac = re.search(r"virtio=([A-Fa-f0-9:]{17})(?:,|$)", vm["net0"])[1].lower()
        interfaces = self.pve(self.prefix + "/agent/network-get-interfaces")["result"]
        addresses = [(entry["ip-address"], entry["prefix"])
            for interface in interfaces if interface.get("hardware-address", "").lower() == self.mac
            for entry in interface.get("ip-addresses", []) if entry.get("ip-address-type") == "ipv4"
            and not ipaddress.ip_address(entry["ip-address"]).is_link_local]
        if len(addresses) != 1:
            raise RuntimeError("Expected exactly one IPv4 address on the declared virtio NIC")
        self.ip, prefix = addresses[0]
        self.network = str(ipaddress.ip_network(f"{self.ip}/{prefix}", strict=False))
        if not self.network.startswith("10.1.0."):
            raise RuntimeError("This live fixture is bounded to the approved 10.1.0.0 LAN")
        (self.output / "address").write_text(self.ip + "\n")
        if hasattr(self, "host_key"):
            (self.output / "known_hosts").write_text(f"{self.ip} {self.host_key}\n")

    def ssh_args(self):
        return ["ssh", "-F", "/dev/null", "-i", str(self.work / "ssh-key"),
            "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + str(self.output / "known_hosts"),
            "-o", "GlobalKnownHostsFile=/dev/null", "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15", "ops@" + self.ip]

    def ssh(self, command, data=None, check=True):
        return subprocess.run(self.ssh_args() + [command], input=data,
            capture_output=True, check=check, timeout=600)

    def runner(self):
        return ["podman", "run", "--rm", "--cpus=2", "--memory=1800m", "--pids-limit=256",
            "--cap-drop=all", "--cap-add=CHOWN", "--cap-add=FOWNER", "--cap-add=DAC_OVERRIDE",
            "--security-opt=no-new-privileges", "--security-opt=label=disable",
            "-v", "octopushy-nix-validation-store:/nix",
            "-v", str(self.config) + ":/config:ro", NIX_IMAGE]

    def activate(self, label, *, enabled=True, exposed=False, sources=None):
        guest = {"name": self.name, "vmid": self.vmid, "mac": self.mac, "ip": self.ip,
            "sshKey": (self.work / "ssh-key.pub").read_text().strip(),
            "enable": enabled, "expose": exposed, "allowedNetworks": sources or []}
        (self.config / "guest.json").write_text(json.dumps(guest, indent=2) + "\n")
        build = ["nix", "--extra-experimental-features", "nix-command flakes", "build", "-L",
            "--no-update-lock-file", "--out-link", "/nix/var/nix/gcroots/octopushy-service-installed",
            "--option", "sandbox", "false", "--option", "build-users-group", "",
            "--max-jobs", "1", "--cores", "2",
            "path:/config#nixosConfigurations.service.config.system.build.toplevel"]
        with (self.output / f"{label}-build.log").open("wb") as log:
            subprocess.run(self.runner() + build, stdout=log, stderr=subprocess.STDOUT, check=True)
        closure = subprocess.check_output(self.runner() + [
            "readlink", "-f", "/nix/var/nix/gcroots/octopushy-service-installed"], text=True).strip()
        if not re.fullmatch(r"/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+", closure):
            raise RuntimeError("Unexpected system closure")
        required = subprocess.check_output(self.runner() + [
            "nix-store", "--query", "--requisites", closure], text=True).splitlines()
        present = set(self.ssh("sudo -n nix-store --query --requisites /run/current-system").stdout.decode().splitlines())
        missing = [path for path in required if path not in present]
        if missing:
            exporter = subprocess.Popen(self.runner() + ["nix-store", "--export", *missing],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            importer = subprocess.run(self.ssh_args() + ["sudo -n nix-store --import"],
                stdin=exporter.stdout, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            exporter.stdout.close()
            if exporter.wait() or importer.returncode:
                raise RuntimeError("Closure copy failed; activation was not attempted")
        with (self.output / f"{label}-activate.log").open("wb") as log:
            result = self.ssh("sudo -n flock /run/lock/octopushy-nixos.lock "
                "nixos-rebuild switch --no-reexec --store-path " + closure, check=False)
            log.write(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError(f"Activation outcome needs inspection: {label}-activate.log")
        (self.output / "active-closure").write_text(closure + "\n")
        self.check(label + " exact NixOS closure activated")

    def wait_ready(self):
        for _ in range(90):
            result = self.ssh("sudo -n systemctl is-active docker-portainer && "
                "curl -ksSf --max-time 3 https://127.0.0.1:9443/api/status", check=False)
            if result.returncode == 0:
                return
            time.sleep(2)
        raise TimeoutError("Portainer readiness timed out")

    def closed(self, port=9443):
        try:
            with socket.create_connection((self.ip, port), timeout=4):
                return False
        except (TimeoutError, ConnectionRefusedError):
            return True

    def reboot(self):
        old_boot = self.ssh("cat /proc/sys/kernel/random/boot_id").stdout.strip()
        self.ssh("sudo -n systemctl reboot", check=False)
        time.sleep(10)
        for _ in range(90):
            try:
                self.discover()
                result = self.ssh("cat /proc/sys/kernel/random/boot_id", check=False)
                if result.returncode == 0 and result.stdout.strip() != old_boot:
                    self.wait_ready()
                    return
            except (OSError, urllib.error.URLError, RuntimeError, subprocess.TimeoutExpired):
                pass
            time.sleep(3)
        raise TimeoutError("VM reboot verification timed out; inspect rather than reapply")

    def api(self, path, data=None, method=None):
        headers = {"Content-Type": "application/json"}
        if self.jwt:
            headers["Authorization"] = "Bearer " + self.jwt
        request = urllib.request.Request(self.base + path,
            data=None if data is None else json.dumps(data).encode(), headers=headers, method=method)
        with urllib.request.urlopen(request, context=self.tls, timeout=120) as response:
            raw = response.read()
            return None if not raw else json.loads(raw)

    def login_and_environment(self):
        self.jwt = None
        self.jwt = self.api("/api/auth", {"Username": "admin", "Password": self.password})["jwt"]
        endpoints = self.api("/api/endpoints")
        if len(endpoints) != 1 or endpoints[0]["Type"] != 1:
            raise RuntimeError("Expected exactly one local Docker environment")
        self.endpoint = endpoints[0]["Id"]
        info = self.api(f"/api/endpoints/{self.endpoint}/docker/info")
        self.check("authenticated local Docker environment", info["Name"] == self.name)
        return endpoints[0]

    def run(self):
        self.discover()
        for _ in range(90):
            state = dict(line.split("=", 1) for line in self.guest_command([
                "/run/current-system/sw/bin/systemctl", "show", "cloud-final.service",
                "-p", "LoadState", "-p", "SubState", "-p", "Result"]).splitlines() if "=" in line)
            if state.get("LoadState") == "not-found":
                break  # Already on an installed generation with cloud-init disabled.
            if state.get("SubState") == "exited" and state.get("Result") == "success":
                # Compatibility for the earlier image: its sshd could still hold a
                # pre-cloud-init key in memory. Only the verified, fresh test VM is touched.
                self.guest_command(["/run/current-system/sw/bin/systemctl", "restart", "sshd.service"])
                break
            if state.get("Result") not in (None, "success"):
                raise RuntimeError("Cloud-init failed; inspect guest bootstrap before SSH")
            time.sleep(2)
        else:
            raise TimeoutError("Cloud-init completion was not verified")
        key = self.guest_command(["cat", "/etc/ssh/ssh_host_ed25519_key.pub"]).strip().split()
        if len(key) < 2 or key[0] != "ssh-ed25519" or not re.fullmatch(r"[A-Za-z0-9+/=]+", key[1]):
            raise RuntimeError("Invalid guest-agent SSH host key")
        self.host_key = " ".join(key[:2])
        self.discover()
        self.check("SSH host key obtained through owned VM management channel")
        self.ssh("sudo -n install -d -m 0700 /var/lib/octopushy-secrets && "
            "sudo -n sh -c 'umask 077; cat > /var/lib/octopushy-secrets/portainer-admin-password'",
            (self.password + "\n").encode())
        self.activate("closed-bootstrap")
        self.wait_ready()
        if self.ssh("test -d /sys/class/net/ens18", check=False).returncode:
            # .link naming takes effect on device discovery, not a networkd reload.
            # Reboot while the service is CLOSED, before evaluating source restrictions.
            self.reboot()
        self.check("installed NIC identity established before exposure tests",
            self.ssh("cat /sys/class/net/ens18/address").stdout.decode().strip() == self.mac)
        self.check("uninitialized/admin-bootstrap UI never exposed to LAN", self.closed())
        certificate = self.ssh("openssl s_client -connect 127.0.0.1:9443 -showcerts </dev/null").stdout.decode()
        match = re.search(r"-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", certificate, re.S)
        if not match:
            raise RuntimeError("No server certificate available over verified SSH")
        (self.output / "portainer-cert.pem").write_text(match[0] + "\n")
        self.tls = ssl.create_default_context(cafile=str(self.output / "portainer-cert.pem"))
        # Trust the exact SSH-retrieved self-signed certificate. Portainer's generated
        # certificate is not issued for the current DHCP IP; do not disable chain validation.
        self.tls.check_hostname = False
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            local_port = sock.getsockname()[1]
        self.tunnel = subprocess.Popen(self.ssh_args()[:-1] + [
            "-o", "ExitOnForwardFailure=yes", "-N",
            "-L", f"127.0.0.1:{local_port}:127.0.0.1:9443", self.ssh_args()[-1]],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.base = f"https://127.0.0.1:{local_port}"
        for _ in range(30):
            if self.tunnel.poll() is not None:
                raise RuntimeError("Bootstrap SSH tunnel failed")
            try:
                self.api("/api/status")
                break
            except (OSError, urllib.error.URLError):
                time.sleep(1)
        else:
            raise TimeoutError("Bootstrap tunnel timed out")
        original_endpoint = self.login_and_environment()["Id"]
        self.api("/api/users/admin/check")
        self.check("administrator initialized before LAN exposure")
        self.activate("source-denied", exposed=True, sources=["10.1.0.254/32"])
        self.check("non-allowed LAN source denied while SSH works", self.closed())
        self.activate("lan-exposed", exposed=True, sources=[self.network])
        self.base = f"https://{self.ip}:9443"
        self.login_and_environment()
        self.check("direct LAN HTTPS works with pinned certificate")
        self.check("HTTP and Edge ports not exposed", self.closed(9000) and self.closed(8000))
        with (self.output / "probe-pull.log").open("wb") as log:
            result = self.ssh("sudo -n docker pull " + PROBE_IMAGE, check=False)
            log.write(result.stdout + result.stderr)
            if result.returncode:
                raise RuntimeError("Probe image pull failed")
        docker = f"/api/endpoints/{self.endpoint}/docker"
        probe = self.api(docker + "/containers/create?name=octopushy-e2e-probe", {
            "Image": PROBE_IMAGE, "Cmd": ["sh", "-c", "echo portainer-e2e-ok; sleep 600"],
            "Labels": {"octopushy.e2e": self.name},
            "HostConfig": {"NetworkMode": "none", "Memory": 33554432,
                "PidsLimit": 32, "CapDrop": ["ALL"], "SecurityOpt": ["no-new-privileges"]},
        })["Id"]
        if not re.fullmatch(r"[a-f0-9]{64}", probe):
            raise RuntimeError("Invalid Docker probe identity; inspect outcome before retry")
        self.api(docker + f"/containers/{probe}/start", method="POST")
        self.check("Portainer creates and starts a Docker workload",
            self.api(docker + f"/containers/{probe}/json")["State"]["Running"])
        self.ssh("sudo -n systemctl stop docker-portainer")
        self.check("stopping Portainer does not stop sibling Docker workload",
            self.ssh("sudo -n docker inspect -f '{{.State.Running}}' " + probe).stdout.strip() == b"true")
        self.ssh("sudo -n systemctl start docker-portainer")
        self.wait_ready()
        self.login_and_environment()
        self.api(docker + f"/containers/{probe}?force=true", method="DELETE")
        self.check("Portainer removes its test workload")
        self.activate("role-disabled", enabled=False)
        self.check("role removal keeps persistent data",
            self.ssh("sudo -n docker volume inspect octopushy-portainer-data", check=False).returncode == 0)
        self.activate("role-restored", exposed=True, sources=[self.network])
        self.wait_ready()
        self.check("role restore preserves administrator and environment",
            self.login_and_environment()["Id"] == original_endpoint)
        self.reboot()
        self.base = f"https://{self.ip}:9443"
        self.check("reboot preserves administrator and environment",
            self.login_and_environment()["Id"] == original_endpoint)
        self.check("stable MAC-matched ens18 interface after reboot",
            self.ssh("cat /sys/class/net/ens18/address").stdout.decode().strip() == self.mac)
        container = self.ssh("sudo -n docker inspect -f '{{.Id}}' portainer").stdout.strip()
        self.activate("repeat-same-configuration", exposed=True, sources=[self.network])
        self.check("unchanged configuration does not recreate Portainer",
            self.ssh("sudo -n docker inspect -f '{{.Id}}' portainer").stdout.strip() == container)
        self.login_and_environment()
        (self.output / "url").write_text(self.base + "\n")
        self.save()
        print(f"Ready: {self.base} (admin); password is in {self.password_file}", flush=True)

    def close(self):
        if self.tunnel:
            self.tunnel.terminate()
            self.tunnel.wait(timeout=15)


if __name__ == "__main__":
    os.umask(0o077)
    test = Live(Path(sys.argv[1]), Path(sys.argv[2]))
    try:
        test.run()
    except Exception as error:
        # Never print request bodies, headers, response tokens, or credentials.
        print(f"Live Portainer test stopped ({type(error).__name__}): {error}", file=sys.stderr)
        sys.exit(1)
    finally:
        test.close()
