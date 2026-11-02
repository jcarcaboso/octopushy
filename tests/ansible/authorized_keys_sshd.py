#!/usr/bin/env python3
"""The authorized_keys program against a real sshd: what it removes is what sshd stops accepting.

Each case writes a file, logs in with each key, removes the old key, and logs in again. The old
key has to stop working, and every other key has to go on doing exactly what it did. Lines are
what sshd takes them to be, so these are the shapes a text search gets wrong.

    authorized_keys_sshd.py <image>      an image with sshd, python3 and ssh-keygen, as tests/ansible/target
"""
import json
import os
import secrets
from pathlib import Path
import socket
import subprocess
import sys
import tempfile

SCRIPT = Path(__file__).resolve().parents[2] / "backend/src/Octopushy.Server/Orchestration/GuestAccess/authorized_keys.py"
CONTAINER = f"octopushy-authorized-keys-{secrets.token_hex(4)}"


def run(*arguments, data=None):
    return subprocess.run(list(arguments), input=data, capture_output=True, check=False)


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def cases(old, new, person):
    blob = old.split()[1]
    plain = f"ssh-ed25519 {blob}"
    tail = f"{new}\n{person}\n".encode()
    yield "plain", f"{old}\n".encode() + tail
    yield "CRLF", f"{old}\r\n".encode() + tail
    yield "no final newline", tail + old.encode()
    yield "tabs", f"ssh-ed25519\t{blob}\tcomment\n".encode() + tail
    yield "two copies", f"{old}\n".encode() + tail + f"{old}\n".encode()
    yield "options", f'environment="VAR=a b",no-pty {old}\n'.encode() + tail
    yield "two blanks after the options", f"no-pty  {old}\n".encode() + tail
    yield "a tab and blanks after the options", f"no-pty \t {old}\n".encode() + tail
    yield "PRIVATE KEY in the comment", f"{old} PRIVATE KEY\n".encode() + tail
    yield "PRIVATE KEY in an option", f'environment="A=PRIVATE KEY" {old}\n'.encode() + tail
    yield "options ending in two backslashes", ('environment="VAR=a\\\\" b" ' + plain + " c\n").encode() + tail
    yield "not UTF-8", plain.encode() + b" comment-\xff\n" + tail
    yield "long comment", old.encode() + b"x" * 200000 + b"\n" + tail
    yield "NUL after the key", plain.encode() + b"\0 comment\n" + tail
    yield "CR inside the key", f"ssh-ed25519 {blob[:20]}\r{blob[20:]} c\n".encode() + tail
    yield "VT inside the key", f"ssh-ed25519 {blob[:20]}\v{blob[20:]} c\n".encode() + tail
    # A person's line that mentions the old key, in every way that could be taken for it.
    yield "comment quotes it", f"{old}\n{new}\n{person}, replaces {old}\n".encode()
    for name, separator in (("CR", "\r"), ("VT", "\v"), ("FF", "\f"), ("NEL", "\x85"), ("LS", "\u2028")):
        yield f"comment quotes it after {name}", f"{old}\n{new}\n{person} {separator}{old}\n".encode()
    yield "option holds CR", f'{old}\nenvironment="A=b\r{old}" {person}\n{new}\n'.encode()
    yield "option holds it", ('environment="VAR=a\\\\" ' + plain + ' copied" ' + person + "\n" + old + "\n" + new + "\n").encode()


def main():
    image = sys.argv[1]
    work = tempfile.TemporaryDirectory(prefix="authorized-keys-")
    keys = {}
    for name in ("old", "new", "person"):
        path = os.path.join(work.name, name)
        run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path)
        keys[name] = Path(path + ".pub").read_text().strip()
    Path(work.name, "authorized_keys").write_text(keys["new"] + "\n")
    old_print, new_print = (
        run("ssh-keygen", "-l", "-E", "sha256", "-f", os.path.join(work.name, f"{key}.pub")).stdout.decode().split()[1] for key in ("old", "new")
    )
    blobs = [keys[key].split()[1].encode() for key in ("new", "person")]
    port = free_port()
    started = run(
        "podman", "run", "-d", "--name", CONTAINER, "-p", f"127.0.0.1:{port}:22", "-v", f"{work.name}:/keys:ro,Z",
        "--cap-add", "SYS_CHROOT", "--cap-add", "AUDIT_WRITE", image, "/bin/sh", "-c",
        # Without PerSourcePenalties, refused logins here would get this address refused altogether.
        "ssh-keygen -A && cp /keys/authorized_keys /home/ops/.ssh/authorized_keys && chown ops:ops /home/ops/.ssh/authorized_keys"
        " && exec /usr/sbin/sshd -D -e -o UsePAM=no -o PasswordAuthentication=no -o PerSourcePenalties=no",
    )
    if started.returncode:
        sys.exit(started.stderr.decode())

    def login(name):
        return run(
            "ssh", "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=4", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null", "-i", os.path.join(work.name, name), "-p", str(port), "ops@127.0.0.1", "true",
        ).returncode == 0

    failures = 0
    try:
        for _ in range(40):
            if login("new"):
                break
        run("podman", "cp", str(SCRIPT), f"{CONTAINER}:/tmp/authorized_keys.py")
        for name, content in cases(keys["old"], keys["new"], keys["person"]):
            run("podman", "exec", "-i", CONTAINER, "sh", "-c", "cat > /home/ops/.ssh/authorized_keys; chown ops:ops /home/ops/.ssh/authorized_keys", data=content)
            before = {key: login(key) for key in keys}
            program = ("podman", "exec", "-u", "ops", "-e", "HOME=/home/ops", CONTAINER, "python3", "/tmp/authorized_keys.py")
            removed = run(*program, "remove", new_print, old_print)
            absent = run(*program, "absent", new_print, old_print)
            after = {key: login(key) for key in keys}
            left = run("podman", "exec", CONTAINER, "cat", "/home/ops/.ssh/authorized_keys").stdout
            kept = [line for line in left.split(b"\n") if line] == [
                line for line in content.split(b"\n") if any(blob in line for blob in blobs)
            ]
            # Every key has to work first, or the case proves nothing.
            good = all(before.values()) and removed.returncode == 0 and absent.returncode == 0 \
                and not after["old"] and after["new"] and after["person"] and kept
            print(("ok  " if good else "FAIL"), name, json.dumps({"before": before, "after": after, "remove": removed.returncode, "absent": absent.returncode, "byte for byte": kept}), flush=True)
            failures += 0 if good else 1
    finally:
        run("podman", "rm", "-f", CONTAINER)
        work.cleanup()
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
