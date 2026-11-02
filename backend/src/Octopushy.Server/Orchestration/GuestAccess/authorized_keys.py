#!/usr/bin/python3
"""Changes the login's authorized_keys by which key a line authorizes, and nothing else.

    authorized_keys.py add "<type> <blob> [comment]"
    authorized_keys.py remove <blob>...
    authorized_keys.py absent <blob>...

A key is its blob, read from the field sshd reads it from. Text is never searched: a comment can
quote another key, options can hold anything, and fields can be separated by tabs. Any line
that is not one of the keys named stays byte for byte. Prints "changed" if the file changed;
absent exits 1 if a key named is still authorized.
"""
import os
import re
import sys
import tempfile

PATH = os.path.expanduser("~/.ssh/authorized_keys")
KEY_TYPE = re.compile(r"(ssh-|ecdsa-sha2-|sk-)[A-Za-z0-9@.+-]+")


def without_options(text):
    """What follows a line's options. Whitespace ends them, unless it is inside double quotes."""
    quoted = False
    escaped = False
    for index, character in enumerate(text):
        if escaped:
            escaped = False
        elif character == "\\":
            escaped = True
        elif character == '"':
            quoted = not quoted
        elif character in " \t" and not quoted:
            return text[index:].lstrip(" \t")
    return ""


def blob_of(line):
    """The key a line authorizes, or None for a blank line, a comment or one that is neither."""
    text = line.strip(" \t\r\n")
    if not text or text.startswith("#"):
        return None
    fields = text.split(None, 2)
    if not KEY_TYPE.fullmatch(fields[0]):
        fields = without_options(text).split(None, 2)
        if not fields or not KEY_TYPE.fullmatch(fields[0]):
            return None
    return fields[1] if len(fields) > 1 else None


def read():
    try:
        with open(PATH, encoding="utf-8", errors="surrogateescape", newline="") as stream:
            return stream.read().splitlines(keepends=True)
    except FileNotFoundError:
        return []


def write(lines):
    """Replaces the file whole, so a failure leaves it as it was. The directory gives it its label."""
    target = os.path.realpath(PATH)
    directory = os.path.dirname(target)
    os.makedirs(directory, mode=0o700, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".authorized_keys.", dir=directory)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", errors="surrogateescape", newline="") as stream:
            stream.writelines(lines)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, target)
    except BaseException:
        if os.path.exists(temporary):
            os.unlink(temporary)
        raise


def main(arguments):
    command, values = arguments[0], arguments[1:]
    lines = read()
    if command == "add":
        key = values[0].strip()
        if blob_of(key) in {blob_of(line) for line in lines}:
            return 0
        if lines and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        write(lines + [key + "\n"])
    elif command == "remove":
        kept = [line for line in lines if blob_of(line) not in values]
        if len(kept) == len(lines):
            return 0
        write(kept)
    elif command == "absent":
        return 1 if {blob_of(line) for line in lines} & set(values) else 0
    else:
        print(f"unknown command {command}", file=sys.stderr)
        return 2
    print("changed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
