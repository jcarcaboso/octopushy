#!/usr/bin/python3
"""Changes the login's authorized_keys by which key a line authorizes, and nothing else.

    authorized_keys.py add "<public key line>"
    authorized_keys.py remove <SHA256 fingerprint>...
    authorized_keys.py absent <SHA256 fingerprint>...

The guest's own OpenSSH says what each line is: every physical line goes through ssh-keygen -l,
which reads it as sshd does, options, quoting and all. Nothing here parses a key, so nothing
here can disagree with sshd about one. Lines end at a newline and nowhere else, and every line
that is not a key named stays byte for byte.

An add appends and rewrites nothing, so it cannot lose what someone else wrote. A removal has to
replace the file: it reads it again just before doing so, and starts over if it changed.
Prints "changed" if the file changed. Absent exits 1 if a key named is still authorized. Anything
that stops the check exits 3, so an unknown never reads as gone.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

PATH = os.path.expanduser("~/.ssh/authorized_keys")
FINGERPRINT = re.compile(rb"\d+ (SHA256:[A-Za-z0-9+/]+)[ \n]")
ATTEMPTS = 5


class Fingerprints:
    """Asks ssh-keygen about one line at a time, in a private directory."""

    def __enter__(self):
        self.directory = tempfile.TemporaryDirectory(prefix="authorized-keys-")
        self.path = os.path.join(self.directory.name, "line")
        return self

    def __exit__(self, *_):
        self.directory.cleanup()

    def of(self, line):
        """The key a line authorizes, or None if sshd would not take it as one."""
        if not line.strip():
            return None
        # sshd skips a CR inside a key, where ssh-keygen would stop reading at it. Neither takes one
        # for a field separator, so without it the line still says the same thing to both.
        with open(self.path, "wb") as stream:
            stream.write(line.replace(b"\r", b"") + b"\n")
        result = subprocess.run(
            ["ssh-keygen", "-l", "-E", "sha256", "-f", self.path],
            stdin=subprocess.DEVNULL, capture_output=True, check=False,
        )
        match = FINGERPRINT.match(result.stdout) if result.returncode == 0 else None
        return match[1].decode() if match else None


def read():
    try:
        with open(PATH, "rb") as stream:
            return stream.read()
    except FileNotFoundError:
        return b""


def add(key):
    line = key.strip().encode()
    with Fingerprints() as fingerprints:
        wanted = fingerprints.of(line)
        if wanted is None:
            raise ValueError("the key to add is not a key")
        data = read()
        if wanted in {fingerprints.of(existing) for existing in data.split(b"\n")}:
            return 0
    os.makedirs(os.path.dirname(PATH), mode=0o700, exist_ok=True)
    prefix = b"\n" if data and not data.endswith(b"\n") else b""
    descriptor = os.open(PATH, os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600)
    try:
        os.write(descriptor, prefix + line + b"\n")
    finally:
        os.close(descriptor)
    print("changed")
    return 0


def replace(before, after):
    """Puts the new content in place, unless the file changed since it was read."""
    target = os.path.realpath(PATH)
    descriptor, temporary = tempfile.mkstemp(prefix=".authorized_keys.", dir=os.path.dirname(target))
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(after)
            stream.flush()
            os.fsync(stream.fileno())
        shutil.copymode(target, temporary)
        if read() != before:
            return False
        os.replace(temporary, target)
        return True
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def remove(targets):
    with Fingerprints() as fingerprints:
        for _ in range(ATTEMPTS):
            data = read()
            lines = data.split(b"\n")
            kept = [line for line in lines if fingerprints.of(line) not in targets]
            if len(kept) == len(lines):
                return 0
            if replace(data, b"\n".join(kept)):
                print("changed")
                return 0
    raise OSError("authorized_keys kept changing")


def absent(targets):
    with Fingerprints() as fingerprints:
        return 1 if {fingerprints.of(line) for line in read().split(b"\n")} & targets else 0


def main(arguments):
    command, values = arguments[0], arguments[1:]
    try:
        if command == "add":
            return add(values[0])
        if command == "remove":
            return remove(set(values))
        if command == "absent":
            return absent(set(values))
        print(f"unknown command {command}", file=sys.stderr)
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
    return 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
