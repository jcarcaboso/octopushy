#!/usr/bin/env python3
"""The part of the Proxmox API that bpg/proxmox needs to create a container, and nothing more.

It creates nothing: it remembers what it was asked for, in requests.log for the test to read,
and answers as Proxmox does. The container the test connects to is started by run.sh; the
SSH keys it receives here go to where that container reads them.
"""
import http.server
import json
from pathlib import Path
import re
import ssl
import sys
import urllib.parse

share, certificate, key = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
files = {}
containers = {}
firewalls = {}
UPID = "UPID:korriban:00000001:00000001:00000001:fake:0:root@pam:"


def stored_config(vmid, container):
    """What Proxmox answers: integers, a description with a newline, and allocated volumes."""
    hidden = {"status", "ssh-public-keys", "ostemplate", "vmid", "storage", "start", "node"}
    config = {k: int(v) if str(v).isdigit() else v for k, v in container.items() if k not in hidden}
    config["description"] = container.get("description", "") + "\n"
    config["digest"] = "a" * 40
    for name, value in container.items():
        if re.fullmatch(r"mp\d+", name):
            parts = dict(part.split("=", 1) for part in value.split(","))
            datastore, size = parts["volume"].split(":")
            backup = parts.get("backup", 0)
            config[name] = f"{datastore}:vm-{vmid}-disk-1,mp={parts['mp']},backup={backup},size={size}G"
        if name == "rootfs":
            datastore, size = value.removeprefix("volume=").split(":")
            config[name] = f"{datastore}:vm-{vmid}-disk-0,size={size}G"
    return config


def interfaces(container):
    address = re.search(r"ip=([0-9.]+)", container.get("net0", "")).group(1)
    return [
        {"name": "lo", "hwaddr": "00:00:00:00:00:00", "inet": "127.0.0.1/8"},
        {"name": "eth0", "hwaddr": "bc:24:11:00:00:02", "hardware-address": "bc:24:11:00:00:02",
         "inet": f"{address}/24",
         "ip-addresses": [{"ip-address-type": "inet", "ip-address": address, "prefix": 24}]},
    ]


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def answer(self, data, status=200, reason=None):
        body = json.dumps({"data": data}).encode()
        self.send_response(status, reason)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def form(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode()
        if "json" in self.headers.get("Content-Type", ""):
            return json.loads(raw or "{}")
        return {k: v[0] for k, v in urllib.parse.parse_qs(raw, keep_blank_values=True).items()}

    def handle_request(self, method):
        path = urllib.parse.urlsplit(self.path).path.removeprefix("/api2/json")
        form = self.form() if method in ("POST", "PUT") else {}
        if method != "GET":
            with open(share / "requests.log", "a") as log:
                log.write(json.dumps({"method": method, "path": path, "form": form}) + "\n")
        node = re.match(r"^/nodes/([^/]+)/(.*)$", path)
        rest = node.group(2) if node else ""

        if path == "/version":
            return self.answer({"version": "9.1.1", "release": "9.1", "repoid": "fake"})
        # What an agent asks when it verifies a guest and observes an installation.
        if path == "/access/permissions":
            asked = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query).get("path", ["/"])[0]
            return self.answer({asked: {"VM.Audit": 1, "Sys.Audit": 1, "Datastore.Audit": 1}})
        if path == "/nodes":
            return self.answer([{"node": "ziost", "id": "node/ziost", "type": "node", "status": "online", "level": ""}])
        if path == "/cluster/resources":
            return self.answer([{"id": f"lxc/{vmid}", "type": "lxc", "vmid": vmid, "node": container["node"],
                                 "name": container.get("hostname"), "status": container["status"]}
                                for vmid, container in containers.items()])
        if rest.startswith("query-url-metadata"):
            return self.answer({"filename": "template.tar.zst", "size": 1, "mimetype": "application/zstd"})
        if download := re.fullmatch(r"storage/([^/]+)/download-url", rest):
            volume = f"{download.group(1)}:vztmpl/{form['filename']}"
            files[volume] = {"volid": volume, "content": "vztmpl", "format": "tzst", "size": 1, "ctime": 1}
            return self.answer(UPID)
        if re.fullmatch(r"storage/[^/]+/content", rest):
            return self.answer(list(files.values()))
        if re.fullmatch(r"tasks/.*/status", rest):
            return self.answer({"status": "stopped", "exitstatus": "OK", "pid": 1, "type": "x", "upid": UPID})
        if rest == "lxc" and method == "POST":
            vmid = int(form["vmid"])
            containers[vmid] = {**form, "status": "stopped", "node": node.group(1)}
            (share / "ssh/authorized_keys").write_text(form.get("ssh-public-keys", "") + "\n")
            return self.answer(UPID)
        if guest := re.fullmatch(r"lxc/(\d+)(/.*)?", rest):
            vmid, sub = int(guest.group(1)), guest.group(2) or ""
            container = containers.get(vmid)
            if container is None:
                return self.answer(None, 500, f"Configuration file 'nodes/x/lxc/{vmid}.conf' does not exist")
            if sub in ("/status/start", "/status/reboot"):
                container["status"] = "running"
                return self.answer(UPID)
            if sub == "/status/current":
                return self.answer({"status": container["status"], "vmid": vmid, "name": container.get("hostname")})
            if sub == "/interfaces":
                return self.answer(interfaces(container))
            if sub == "/config":
                if method == "PUT":
                    container.update(form)
                    return self.answer(None)
                return self.answer(stored_config(vmid, container))
            if sub.startswith("/firewall"):
                empty = [] if sub.endswith("rules") else {}
                current = firewalls.setdefault((vmid, sub), empty)
                if method != "GET":
                    current.append(form) if isinstance(current, list) else current.update(form)
                return self.answer(current)
        with open(share / "requests.log", "a") as log:
            log.write(f"UNHANDLED {method} {self.path}\n")
        return self.answer(None, 501, "not implemented")

    def do_GET(self):
        self.handle_request("GET")

    do_HEAD = do_GET

    def do_POST(self):
        self.handle_request("POST")

    def do_PUT(self):
        self.handle_request("PUT")

    def do_DELETE(self):
        self.handle_request("DELETE")


server = http.server.ThreadingHTTPServer(("0.0.0.0", 8006), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(certificate, key)
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
