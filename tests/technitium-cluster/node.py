#!/usr/bin/env python3
"""What the test does to a member by hand, as an operator would, and what it asks over DNS.

node.py dns ADDRESS NAME            prints blocked, resolves, nxdomain, or the rcode
node.py call ADDRESS PATH [k=v ...] prints the response, with the password in TECHNITIUM_PASSWORD
node.py export ADDRESS PATH         prints an export
"""
import json
import os
import random
import socket
import ssl
import struct
import sys
import urllib.parse
import urllib.request

context = ssl.create_default_context()
context.check_hostname = False
context.verify_mode = ssl.CERT_NONE


def post(address, path, **form):
    request = urllib.request.Request(f"https://{address}:53443{path}", urllib.parse.urlencode(form).encode())
    with urllib.request.urlopen(request, context=context, timeout=30) as response:
        return json.load(response)


def login(address):
    return post(address, "/api/user/login", user="admin", **{"pass": os.environ["TECHNITIUM_PASSWORD"]})["token"]


def dns(address, name):
    identifier = random.randrange(65536)
    query = struct.pack(">HHHHHH", identifier, 0x0100, 1, 0, 0, 1)
    for label in name.split("."):
        query += bytes([len(label)]) + label.encode()
    query += b"\0" + struct.pack(">HH", 1, 1) + b"\0" + struct.pack(">HHIH", 41, 1232, 0, 0)
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.settimeout(5)
    server.sendto(query, (address, 53))
    answer = server.recvfrom(4096)[0]
    flags, _, answers, _, _ = struct.unpack(">HHHHH", answer[2:12])
    rcode = flags & 15
    # EDE 15, "Blocked", in the answer's OPT record.
    if b"\x00\x0f" in answer and b"source=block" in answer:
        return "blocked"
    return "resolves" if rcode == 0 and answers else "nxdomain" if rcode == 3 else f"rcode {rcode}"


if __name__ == "__main__":
    command, address, *rest = sys.argv[1:]
    if command == "dns":
        print(dns(address, rest[0]))
    elif command == "call":
        form = dict(pair.split("=", 1) for pair in rest[1:])
        result = post(address, rest[0], token=login(address), **form)
        print(json.dumps(result))
        sys.exit(0 if result["status"] == "ok" else 1)
    elif command == "export":
        url = f"https://{address}:53443{rest[0]}?token={login(address)}"
        with urllib.request.urlopen(url, context=context, timeout=30) as response:
            print(response.read().decode(), end="")
