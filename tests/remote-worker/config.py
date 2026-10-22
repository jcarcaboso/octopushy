#!/usr/bin/env python3
"""Offline remote-harness validation; DNS checks only with explicit --preflight."""

import argparse
import ipaddress
import os
from pathlib import Path
import re
import socket
import stat
import sys
from urllib.parse import parse_qs, urlsplit
import uuid


def require(env, name):
    value = env.get(name, "")
    if not value:
        raise ValueError(f"{name} is required in remote mode")
    return value


def check_host(host):
    if not host or "%" in host or "/" in host:
        raise ValueError("requires a network hostname or IP address")
    name = host.lower().rstrip(".")
    if name == "localhost" or name.endswith(".localhost"):
        raise ValueError("loopback is not a remote endpoint")
    try:
        address = ipaddress.ip_address(name)
    except ValueError:
        return
    address = getattr(address, "ipv4_mapped", None) or address
    if address.is_loopback or address.is_unspecified or address.is_link_local or address.is_multicast:
        raise ValueError("loopback, wildcard, link-local and multicast endpoints are not supported")


def endpoint(name, value, schemes, preflight=False, credentials=False):
    # Never include URLs or parser/socket errors: a PostgreSQL URL contains credentials.
    try:
        if any(c.isspace() or ord(c) < 32 for c in value):
            raise ValueError("must not contain whitespace")
        url = urlsplit(value)
        if url.scheme not in schemes or not url.hostname or url.fragment:
            raise ValueError("requires an explicit supported URL with a hostname and no fragment")
        if not credentials and (url.username is not None or url.password is not None or url.query):
            raise ValueError("credentials and query strings are not supported here")
        port = url.port
        if port == 0:
            raise ValueError("port must be between 1 and 65535")
        check_host(url.hostname)
        if credentials:
            if not url.path.strip("/"):
                raise ValueError("a dedicated database name is required")
            if set(parse_qs(url.query)) & {"host", "hostaddr", "port", "service", "servicefile", "dbname"}:
                raise ValueError("query parameters must not override the backend endpoint")
        if preflight:
            default_port = 5432 if credentials else 443 if url.scheme == "https" else 80
            addresses = socket.getaddrinfo(url.hostname, port or default_port, type=socket.SOCK_STREAM)
            if not addresses:
                raise ValueError("hostname resolved to no addresses")
            for address in addresses:
                check_host(address[4][0])
        return url
    except (ValueError, OSError):
        raise ValueError(f"{name}: invalid or non-routable endpoint (check scheme, host, port and DNS)") from None


def private_file(env, name):
    try:
        path = Path(require(env, name))
        info = path.stat()
        if not stat.S_ISREG(info.st_mode) or info.st_size == 0 or stat.S_IMODE(info.st_mode) not in (0o400, 0o600):
            raise ValueError()
        return path
    except (OSError, ValueError):
        raise ValueError(f"{name}: requires a nonempty private regular file (mode 0600 or 0400)") from None


def validate(env, preflight=False):
    for name in ("FEDORA_WORKFLOW", "PORTAINER_WORKFLOW", "RESUME_SERVICE", "KEEP_GUEST", "KEEP", "UI"):
        if env.get(name, "0") != "0":
            raise ValueError(f"{name} is not supported in remote-worker mode")
    for name, schemes in (
        ("REMOTE_API_URL", ("http", "https")),
        ("REMOTE_AGENT_URL", ("https",)),
        ("PVE_ENDPOINT", ("https",)),
    ):
        endpoint(name, require(env, name), schemes, preflight)
    try:
        uuid.UUID(require(env, "REMOTE_AGENT_ID"))
    except ValueError:
        raise ValueError("REMOTE_AGENT_ID must be the enrolled worker UUID") from None
    label = require(env, "REMOTE_AGENT_LABEL")
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", label):
        raise ValueError("REMOTE_AGENT_LABEL must be a valid exclusive worker label")
    if not Path(require(env, "REMOTE_WORK_DIR")).is_absolute() or Path(env["REMOTE_WORK_DIR"]) == Path("/"):
        raise ValueError("REMOTE_WORK_DIR must be an absolute non-root fixture directory")
    private_file(env, "REMOTE_COOKIE_FILE")
    private_file(env, "PVE_TOKEN_FILE")
    pg_file = private_file(env, "REMOTE_TOFU_PG_FILE")
    try:
        pg = pg_file.read_text().rstrip("\n")
    except (OSError, UnicodeError):
        raise ValueError("REMOTE_TOFU_PG_FILE could not be read") from None
    endpoint("REMOTE_TOFU_PG_FILE", pg, ("postgres", "postgresql"), preflight, credentials=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true", help="also resolve endpoint DNS (no provisioning)")
    args = parser.parse_args()
    try:
        validate(os.environ, args.preflight)
    except ValueError as error:
        sys.exit(f"Remote configuration: {error}")
    print("Remote configuration valid" + ("; endpoint DNS checked" if args.preflight else " (offline)"))
