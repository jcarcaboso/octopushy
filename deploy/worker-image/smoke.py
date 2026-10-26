#!/usr/bin/env python3
"""Boot a disposable image with NO network; exercise only fixed local guest seams.

Run inside the image-build tool container with QEMU/OVMF paths supplied. No
Proxmox, controller, SSH, DNS, enrollment or retained disks are contacted.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--qemu", type=Path, required=True)
    parser.add_argument("--firmware", type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="worker-boot-") as temporary:
        root = Path(temporary)
        subprocess.run([args.qemu / "bin/qemu-img", "create", "-f", "qcow2", "-F", "qcow2",
                        "-b", args.image.resolve(), root / "boot.qcow2"], check=True, capture_output=True)
        subprocess.run([args.qemu / "bin/qemu-img", "create", "-f", "qcow2",
                        root / "state.qcow2", "16G"], check=True, capture_output=True)
        shutil.copyfile(args.firmware / "FV/OVMF_VARS.fd", root / "vars.fd")
        agent_socket = root / "qga.sock"
        command = [
            str(args.qemu / "bin/qemu-system-x86_64"), "-machine", "q35,accel=kvm",
            "-cpu", "host", "-m", "2048", "-smp", "2", "-display", "none", "-nic", "none",
            "-drive", f"if=pflash,format=raw,readonly=on,file={args.firmware}/FV/OVMF_CODE.fd",
            "-drive", f"if=pflash,format=raw,file={root}/vars.fd",
            "-device", "virtio-scsi-pci,id=scsi0",
            "-drive", f"file={root}/boot.qcow2,format=qcow2,if=none,id=boot",
            "-device", "scsi-hd,drive=boot,bus=scsi0.0",
            "-drive", f"file={root}/state.qcow2,format=qcow2,if=none,id=state",
            "-device", "scsi-hd,drive=state,bus=scsi0.0,serial=opy-worker-state-v1",
            "-device", "virtio-serial-pci",
            "-chardev", f"socket,path={agent_socket},server=on,wait=off,id=qga",
            "-device", "virtserialport,chardev=qga,name=org.qemu.guest_agent.0",
            "-serial", f"file:{root}/console.log",
        ]
        with (root / "qemu.log").open("w") as log:
            proc = subprocess.Popen(command, stdout=log, stderr=log)
            try:
                # Bounded local boot readiness, not a CI/PR polling loop.
                deadline = time.monotonic() + 240
                while True:
                    if proc.poll() is not None:
                        raise RuntimeError((root / "qemu.log").read_text()[-4000:])
                    stream = None
                    try:
                        qga = socket.socket(socket.AF_UNIX)
                        # QEMU accepts the socket before qemu-ga has opened the
                        # guest port. Initial bytes can be discarded at startup.
                        qga.settimeout(2)
                        qga.connect(str(agent_socket))
                        stream = qga.makefile("rwb", buffering=0)
                        token = time.monotonic_ns()
                        stream.write(b"\xff" + json.dumps({
                            "execute": "guest-sync-delimited", "arguments": {"id": token},
                        }).encode() + b"\n")
                        # The leading sentinel can itself produce a parse error;
                        # ignore stale/error replies until our exact sync token.
                        while json.loads(stream.readline().lstrip(b"\xff")).get("return") != token:
                            pass
                        qga.settimeout(5)
                        break
                    except (OSError, ValueError):
                        if stream is not None:
                            stream.close()
                        qga.close()
                        if time.monotonic() > deadline:
                            raise RuntimeError("QGA boot timeout: " + (root / "console.log").read_text()[-6000:])
                        time.sleep(1)
                with qga:
                    print("QGA ready; checking state mount, rootless runtime and public identity", flush=True)
                    status = execute(stream, "/run/current-system/sw/bin/systemctl",
                                     ["start", "octopushy-worker-directories.service"])
                    if status["exitcode"] != 0:
                        raise RuntimeError("Worker state setup did not complete: " + str(status))
                    mount = execute(stream, "/run/current-system/sw/bin/systemctl",
                                    ["show", "var-lib-octopushy.mount", "-p", "What", "-p", "After"])
                    device = execute(stream, "/run/current-system/sw/bin/readlink",
                                     ["-f", "/dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_opy-worker-state-v1"])
                    assert device["exitcode"] == 0 and "What=" + device["stdout"].strip() + "\n" in mount["stdout"], mount
                    assert "octopushy-worker-state.service" in mount["stdout"], mount
                    assert "by-label" not in mount["stdout"], mount
                    runtime = execute(stream, "/run/current-system/sw/bin/runuser", [
                        "-u", "octopushy", "--", "/run/current-system/sw/bin/env",
                        "HOME=/var/lib/octopushy/home", "XDG_RUNTIME_DIR=/run/user/1001",
                        "PATH=/run/wrappers/bin:/run/current-system/sw/bin",
                        "podman", "info", "--format", "json",
                    ])
                    assert runtime["exitcode"] == 0, runtime
                    assert json.loads(runtime["stdout"])["host"]["security"]["rootless"] is True
                    helper = "/run/current-system/sw/bin/octopushy-worker-bootstrap"
                    generation = "78cd5ef0-ef9d-4ad4-a227-bd49c52d21ab"
                    first = execute(stream, helper, ["prepare", "--generation", generation])
                    assert first["exitcode"] == 0, first
                    report = json.loads(first["stdout"])
                    assert set(report) == {"protocolVersion", "generation", "csrDer", "agentVersion"}
                    assert report["generation"] == generation
                    assert "PRIVATE KEY" not in first["stdout"]
                    again = execute(stream, helper, ["prepare", "--generation", generation])
                    assert json.loads(again["stdout"]) == report
                    refused = execute(stream, helper, ["prepare", "--generation",
                                                      "bd770da2-b442-44c0-bf9e-5fca1c0ff6a0"])
                    assert refused["exitcode"] != 0
                    assert execute(stream, "/run/current-system/sw/bin/systemctl",
                                   ["is-active", "octopushy-worker.service"])["exitcode"] != 0
                    print(json.dumps({"boot": "passed", "stateDisk": "mounted", "csr": "public-only",
                                      "sameGeneration": "idempotent", "otherGeneration": "refused",
                                      "unenrolledService": "inactive", "rootlessPodman": "passed",
                                      "network": "disabled"}))
            finally:
                proc.terminate()
                try:
                    proc.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait()


def rpc(stream, method, arguments=None):
    stream.write(json.dumps({"execute": method, "arguments": arguments or {}}).encode() + b"\n")
    response = json.loads(stream.readline().lstrip(b"\xff"))
    if "error" in response:
        raise ValueError(response["error"])
    return response["return"]


def execute(stream, path, arguments):
    pid = rpc(stream, "guest-exec", {"path": path, "arg": arguments, "capture-output": True})["pid"]
    deadline = time.monotonic() + 120
    while True:
        status = rpc(stream, "guest-exec-status", {"pid": pid})
        if status.get("exited"):
            return {"exitcode": status.get("exitcode", -1),
                    "stdout": base64.b64decode(status.get("out-data", "")).decode(),
                    "stderr": base64.b64decode(status.get("err-data", "")).decode()}
        if time.monotonic() > deadline:
            raise RuntimeError("Local smoke command timed out")
        time.sleep(0.2)


if __name__ == "__main__":
    os.umask(0o077)
    main()
