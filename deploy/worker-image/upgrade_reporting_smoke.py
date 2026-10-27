#!/usr/bin/env python3
"""Disposable old-image activation and reporting fixture, never a live updater.

Only synthetic HTTPS is reachable through QEMU guestfwd with restrict=on.
No controller, Proxmox, SSH, retained identity or real enrollment is used.
This tests the native program, not controller mTLS readiness or live ingress.
"""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import http.server
import json
import os
from pathlib import Path
import secrets
import shlex
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time

from smoke import execute, rpc

GENERATION = "884d09b4-0934-43a4-bffb-d2f7977bc01c"
AGENT = "65090495-3f06-4929-90d0-3dca8c3ed12f"
GUEST = "671fa68a-13d2-42fa-aeef-fb6a2f457853"
IDENTITY = "/var/lib/octopushy/identity"
BIN = "/run/current-system/sw/bin"


def checked(stream, program, args=(), stdin=None, timeout=120):
    result = execute(stream, program, list(args), stdin=stdin, timeout=timeout)
    if result["exitcode"] != 0:
        # No reporting configuration/token is ever included in an exception.
        raise RuntimeError(f"Fixture command failed: {Path(program).name}; "
                           f"exit={result['exitcode']}; {result['stderr'][-2000:]}")
    return result["stdout"]


def connect(path, process, console):
    deadline = time.monotonic() + 240
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("QEMU exited: " + (console.parent / "qemu.log").read_text()[-2000:])
        sock = socket.socket(socket.AF_UNIX)
        stream = None
        try:
            sock.settimeout(3)
            sock.connect(str(path))
            stream = sock.makefile("rwb", buffering=0)
            token = time.monotonic_ns()
            stream.write(b"\xff" + json.dumps({
                "execute": "guest-sync-delimited", "arguments": {"id": token},
            }).encode() + b"\n")
            while json.loads(stream.readline().lstrip(b"\xff")).get("return") != token:
                pass
            sock.settimeout(10)
            return sock, stream
        except (OSError, ValueError):
            if stream:
                stream.close()
            sock.close()
            time.sleep(1)
    raise RuntimeError("QGA boot timeout: " + console.read_text()[-3000:])


def certificate(openssl, root, name, subject, ca=None, csr=None, extensions=None):
    key, cert = root / f"{name}.key", root / f"{name}.pem"
    if ca is None:
        command = ["req", "-new", "-x509", "-newkey", "rsa:2048", "-nodes",
                   "-keyout", key, "-out", cert, "-days", "1", "-subj", subject,
                   "-addext", "basicConstraints=critical,CA:TRUE",
                   "-addext", "keyUsage=critical,keyCertSign,cRLSign,digitalSignature"]
    else:
        if csr is None:
            csr = root / f"{name}.csr"
            subprocess.run([openssl, "req", "-new", "-newkey", "rsa:2048", "-nodes",
                            "-keyout", key, "-out", csr, "-subj", subject],
                           check=True, capture_output=True)
        extension = root / f"{name}.ext"
        extension.write_text(extensions)
        command = ["x509", "-req", "-in", csr, "-CA", ca[0], "-CAkey", ca[1],
                   "-set_serial", "2" if name == "server" else "3", "-days", "1",
                   "-not_before", (datetime.now(timezone.utc) - timedelta(minutes=5)).strftime("%Y%m%d%H%M%SZ"),
                   "-subj", subject, "-extfile", extension, "-out", cert]
    subprocess.run([openssl, *map(str, command)], check=True, capture_output=True)
    return cert, key


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old-image", type=Path, required=True)
    parser.add_argument("--old-image-sha256", required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--system-path", required=True)
    parser.add_argument("--program", type=Path, required=True)
    parser.add_argument("--qemu", type=Path, required=True)
    parser.add_argument("--firmware", type=Path, required=True)
    parser.add_argument("--openssl", type=Path, required=True)
    parser.add_argument("--genisoimage", type=Path, required=True)
    args = parser.parse_args()
    assert hashlib.file_digest(args.old_image.open("rb"), "sha256").hexdigest() == args.old_image_sha256
    digest = hashlib.file_digest(args.archive.open("rb"), "sha256").hexdigest()
    with tempfile.TemporaryDirectory(prefix="worker-upgrade-") as temporary:
        root = Path(temporary)
        ca = certificate(args.openssl, root, "ca", "/CN=Disposable worker fixture CA")
        server_cert, server_key = certificate(
            args.openssl, root, "server", "/CN=10.0.2.100", ca=ca,
            extensions="basicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\nsubjectAltName=IP:10.0.2.100\n")
        token = secrets.token_hex(32)
        reports = []

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *unused):
                pass

            def reply(self, code):
                self.send_response(code)
                self.send_header("Content-Length", "0")
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True

            def do_GET(self):
                if self.path != "/worker-system.nar.xz":
                    return self.reply(404)
                self.send_response(200)
                self.send_header("Content-Length", str(args.archive.stat().st_size))
                self.end_headers()
                with args.archive.open("rb") as source:
                    shutil.copyfileobj(source, self.wfile)

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                if self.path != f"/api/guests/{GUEST}/disk-usage" or not 0 < length < 1024:
                    return self.reply(400)
                body = self.rfile.read(length)
                if self.headers.get("Authorization") != f"Bearer {token}":
                    return self.reply(401)
                data = json.loads(body)
                assert isinstance(data["usedBytes"], int) and data["usedBytes"] > 0
                reports.append(data["usedBytes"])
                self.reply(204)

        class Server(http.server.ThreadingHTTPServer):
            def handle_error(self, *unused):
                pass  # Expected TLS refusals must not dump fixture material.

        server = Server(("127.0.0.1", 0), Handler)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(server_cert, server_key)
        server.socket = tls.wrap_socket(server.socket, server_side=True)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        forward = root / "forward.py"
        forward.write_text(
            "import os,select,socket,sys\n"
            "s=socket.create_connection(('127.0.0.1',int(sys.argv[1])))\n"
            "while True:\n"
            " r,_,_=select.select([0,s],[],[])\n"
            " for fd in r:\n"
            "  b=os.read(0,65536) if fd==0 else s.recv(65536)\n"
            "  if not b: sys.exit(0)\n"
            "  if fd==0: s.sendall(b)\n"
            "  else: os.write(1,b)\n")
        forward_command = " ".join(map(shlex.quote, [
            os.sys.executable, str(forward), str(server.server_port),
        ]))
        seed = root / "seed"
        seed.mkdir()
        (seed / "meta-data").write_text("instance-id: disposable-worker-upgrade\nlocal-hostname: disposable-worker\n")
        (seed / "user-data").write_text("#cloud-config\nhostname: disposable-worker\n")
        subprocess.run([args.genisoimage, "-quiet", "-o", root / "seed.iso", "-V", "cidata",
                        "-r", "-J", "meta-data", "user-data"],
                       cwd=seed, check=True, capture_output=True)
        subprocess.run([args.qemu / "bin/qemu-img", "create", "-f", "qcow2", "-F", "qcow2",
                        "-b", args.old_image.resolve(), root / "boot.qcow2"], check=True, capture_output=True)
        subprocess.run([args.qemu / "bin/qemu-img", "create", "-f", "qcow2", root / "state.qcow2", "16G"],
                       check=True, capture_output=True)
        shutil.copyfile(args.firmware / "FV/OVMF_VARS.fd", root / "vars.fd")
        qga_path = root / "qga.sock"
        command = [
            str(args.qemu / "bin/qemu-system-x86_64"), "-machine", "q35,accel=kvm",
            "-cpu", "host", "-m", "2048", "-smp", "2", "-display", "none",
            "-nic", f"user,model=virtio-net-pci,restrict=on,guestfwd=tcp:10.0.2.100:443-cmd:{forward_command}",
            "-drive", f"if=pflash,format=raw,readonly=on,file={args.firmware}/FV/OVMF_CODE.fd",
            "-drive", f"if=pflash,format=raw,file={root}/vars.fd",
            "-drive", f"file={root}/seed.iso,format=raw,media=cdrom,readonly=on",
            "-device", "virtio-scsi-pci,id=scsi0",
            "-drive", f"file={root}/boot.qcow2,format=qcow2,if=none,id=boot",
            "-device", "scsi-hd,drive=boot,bus=scsi0.0,lun=0,device_id=drive-scsi0",
            "-device", "virtio-scsi-pci,id=scsi1",
            "-drive", f"file={root}/state.qcow2,format=qcow2,if=none,id=state",
            "-device", "scsi-hd,drive=state,bus=scsi1.0,lun=1,device_id=drive-scsi1,serial=opy-worker-state-v1",
            "-device", "virtio-serial-pci",
            "-chardev", f"socket,path={qga_path},server=on,wait=off,id=qga",
            "-device", "virtserialport,chardev=qga,name=org.qemu.guest_agent.0",
            "-serial", f"file:{root}/console.log",
        ]
        with (root / "qemu.log").open("w") as log:
            process = subprocess.Popen(command, stdout=log, stderr=log)
            sock = stream = None
            try:
                sock, stream = connect(qga_path, process, root / "console.log")
                checked(stream, f"{BIN}/systemctl", ["start", "octopushy-worker-directories.service"])
                checked(stream, f"{BIN}/systemctl", ["start", "cloud-final.service"])
                helper = f"{BIN}/octopushy-worker-bootstrap"
                prepared = json.loads(checked(stream, helper, ["prepare", "--generation", GENERATION]))
                csr = root / "worker.csr"
                csr.write_text("-----BEGIN CERTIFICATE REQUEST-----\n" + prepared["csrDer"] + "\n-----END CERTIFICATE REQUEST-----\n")
                issued, _ = certificate(args.openssl, root, "worker", f"/CN={AGENT}", ca=ca, csr=csr,
                                        extensions="basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=clientAuth\n")
                payload = {"protocolVersion": 1, "generation": GENERATION, "agentId": AGENT,
                           "name": "disposable-worker", "serverUrl": "https://10.0.2.100",
                           "certificatePem": issued.read_text(), "caCertificatePem": ca[0].read_text()}
                checked(stream, helper, ["install", "--generation", GENERATION,
                                        "--payload-base64", base64.b64encode(json.dumps(payload).encode()).decode()])
                identity_args = [f"{IDENTITY}/{f}" for f in ["agent.json", "managed-identity.json", "key.pem", "cert.pem", "ca.pem"]]
                before = checked(stream, f"{BIN}/sha256sum", identity_args)
                uuid = checked(stream, f"{BIN}/findmnt", ["-n", "-o", "UUID", "--target", "/var/lib/octopushy"])
                program = "set +e\n(\n" + args.program.read_text() + "\n)\nstatus=$?\n[ \"$status\" -ne 75 ] || exit 1\nexit \"$status\"\n"

                def activate(action):
                    output = checked(stream, f"{BIN}/flock", [
                        "-n", "-E", "75", "/run/lock/octopushy-nixos.lock",
                        f"{BIN}/timeout", "--kill-after=30s", "1800s", f"{BIN}/bash", "-c",
                        program, "octopushy-worker-os", action, "https://10.0.2.100/worker-system.nar.xz",
                        digest, args.system_path, GENERATION, AGENT,
                        base64.b64encode(ca[0].read_bytes()).decode(),
                    ], timeout=1860)
                    proof = json.loads(output)
                    assert proof == {"protocolVersion": 1, "generation": GENERATION,
                                     "archiveSha256": digest, "systemPath": args.system_path,
                                     "identityPreserved": True, "stateFilesystemPreserved": True}

                print("Activating the pinned closure on a disposable old-image overlay", flush=True)
                activate("update-os")
                activate("verify-os")
                activate("update-os")  # Exact completed retry verifies, never re-enrolls.
                assert checked(stream, f"{BIN}/sha256sum", identity_args) == before
                assert checked(stream, f"{BIN}/findmnt", ["-n", "-o", "UUID", "--target", "/var/lib/octopushy"]) == uuid
                config = {"protocolVersion": 1, "generation": GENERATION,
                          "endpoint": f"https://10.0.2.100/api/guests/{GUEST}/disk-usage",
                          "intervalSeconds": 3600, "token": token, "caCertificatePem": ca[0].read_text()}
                reporter = f"{BIN}/octopushy-worker-reporting"
                for bad in [{**config, "caCertificatePem": None}, {**config, "token": "f" * 64}]:
                    refused = execute(stream, reporter, [], stdin=json.dumps(bad).encode())
                    assert refused["exitcode"] != 0
                    assert token not in refused["stdout"] + refused["stderr"]
                configured = checked(stream, reporter, stdin=json.dumps(config).encode())
                assert configured == "" and reports  # Exit zero follows a real HTTPS/204 oneshot.
                boot = checked(stream, f"{BIN}/cat", ["/proc/sys/kernel/random/boot_id"])
                rpc(stream, "guest-exec", {"path": f"{BIN}/systemctl", "arg": ["reboot"]})
                stream.close()
                sock.close()
                deadline = time.monotonic() + 240
                while True:
                    sock, stream = connect(qga_path, process, root / "console.log")
                    try:
                        if checked(stream, f"{BIN}/cat", ["/proc/sys/kernel/random/boot_id"]) != boot:
                            break
                    except (OSError, ValueError):
                        pass
                    stream.close()
                    sock.close()
                    assert time.monotonic() < deadline
                    time.sleep(1)
                count = len(reports)
                assert checked(stream, f"{BIN}/sha256sum", identity_args) == before
                assert checked(stream, f"{BIN}/findmnt", ["-n", "-o", "UUID", "--target", "/var/lib/octopushy"]) == uuid
                assert checked(stream, f"{BIN}/readlink", ["-f", "/run/current-system"]).strip() == args.system_path
                while len(reports) <= count and time.monotonic() < deadline:
                    time.sleep(1)
                assert len(reports) > count, "No real post-boot HTTPS report was received"
                timer = checked(stream, f"{BIN}/systemctl", ["cat", "octopushy-disk-reporter.timer"])
                assert "OnUnitActiveSec=3600s" in timer
                print(json.dumps({"oldImageSha256": args.old_image_sha256, "archiveSha256": digest,
                                  "systemPath": args.system_path, "nativeActivation": "passed",
                                  "identityAndState": "preserved", "badCaAndToken": "refused",
                                  "https204Reporting": "passed", "postRebootReport": "received",
                                  "timerCadenceConfiguration": "3600s",
                                  "controllerMtlsAndLiveIngress": "not exercised"}), flush=True)
            finally:
                if stream:
                    stream.close()
                if sock:
                    sock.close()
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                server.shutdown()
                server.server_close()


if __name__ == "__main__":
    os.umask(0o077)
    main()
