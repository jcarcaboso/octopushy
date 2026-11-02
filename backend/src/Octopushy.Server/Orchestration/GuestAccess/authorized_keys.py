#!/usr/bin/python3
"""Changes the login's authorized_keys by which key a line authorizes.

    authorized_keys.py add "<public key line>"
    authorized_keys.py remove <fingerprint in use> <SHA256 fingerprint>...
    authorized_keys.py absent <fingerprint in use> <SHA256 fingerprint>...

The guest's own OpenSSH says what each line is: every physical line goes through ssh-keygen -l,
so nothing here parses a key. Lines end at a newline and nowhere else. What is left is the key
Octopushy logged in with: if it cannot be found, the check has not worked, whatever the reason, and
nothing is written and nothing reads as gone. So a removal can never take the key in use.

A line whose key is one named goes, whatever else it says. Every other line it read stays byte for
byte.

An add appends and rewrites nothing, so it cannot lose what someone else wrote. A removal has to
replace the file: it reads it again just before doing so, and starts over if it changed. A write to
the file in the instant between that last read and the replacement is lost, a person's key included.
sshd has no lock to take, so this is an accepted limit.

Prints "changed" if the file changed. Absent exits 1 if a key named is still authorized, and
anything that stops the check exits 3. This is the guest's word on a file. Whether sshd takes a
key is sshd's: confirm-refused asks it.
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


def past_options(text):
    """Where sshd's advance_past_options stops: a backslash escapes only a double quote."""
    quoted = False
    index = 0
    while index < len(text):
        character = text[index:index + 1]
        if not quoted and character in (b" ", b"\t"):
            break
        if character == b"\\" and text[index + 1:index + 2] == b'"':
            index += 2
            continue
        if character == b'"':
            quoted = not quoted
        index += 1
    return index


def as_sshd_reads(line):
    """The line, in the shape ssh-keygen needs to read it as sshd does.

    sshd skips a CR inside a key and any run of blanks after the options. ssh-keygen stops at the
    first and takes only one of the second. Neither changes which key a line holds.
    """
    text = line.split(b"\0", 1)[0].replace(b"\r", b"").lstrip(b" \t")
    end = past_options(text)
    return text[:end] + b" " + text[end:].lstrip(b" \t")


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
        # A blank first line, because on the first line "PRIVATE KEY" anywhere reads as a private key.
        with open(self.path, "wb") as stream:
            stream.write(b"\n" + as_sshd_reads(line) + b"\n")
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
        payload = prefix + line + b"\n"
        if os.write(descriptor, payload) != len(payload):
            raise OSError("the key was only partly written")
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
        # Mode and SELinux label, then owner, as far as this login may set them.
        shutil.copystat(target, temporary)
        status = os.stat(target)
        try:
            os.chown(temporary, status.st_uid, status.st_gid)
        except PermissionError:
            pass
        if read() != before:
            return False
        os.replace(temporary, target)
        return True
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def remove(in_use, targets):
    with Fingerprints() as fingerprints:
        for _ in range(ATTEMPTS):
            data = read()
            lines = data.split(b"\n")
            prints = [fingerprints.of(line) for line in lines]
            if in_use not in prints:
                raise ValueError("the key in use is not in authorized_keys, so this file cannot be read")
            kept = [line for line, printed in zip(lines, prints) if printed not in targets]
            if len(kept) == len(lines):
                return 0
            if replace(data, b"\n".join(kept)):
                print("changed")
                return 0
    raise OSError("authorized_keys kept changing")


def absent(in_use, targets):
    with Fingerprints() as fingerprints:
        prints = {fingerprints.of(line) for line in read().split(b"\n")}
    if in_use not in prints:
        raise ValueError("the key in use is not in authorized_keys, so this file cannot be read")
    return 1 if prints & targets else 0


def main(arguments):
    command, values = arguments[0], arguments[1:]
    try:
        if command == "add":
            return add(values[0])
        if command == "remove":
            return remove(values[0], set(values[1:]))
        if command == "absent":
            return absent(values[0], set(values[1:]))
        print(f"unknown command {command}", file=sys.stderr)
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
    return 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
