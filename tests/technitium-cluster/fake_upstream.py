#!/usr/bin/env python3
"""What the private network has instead of the internet: a DNS server that answers every A
question with 192.0.2.10, so a name resolves unless a member blocks it, and a block list over
HTTP. Nothing leaves the network, and names under .test are not needed."""
import http.server
import socket
import struct
import sys
import threading

lists = sys.argv[1]


def serve_dns():
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.bind(("0.0.0.0", 53))
    while True:
        query, peer = server.recvfrom(4096)
        at = 12
        while query[at]:
            at += query[at] + 1
        question = query[12 : at + 5]
        kind = struct.unpack(">H", query[at + 1 : at + 3])[0]
        answer = b"\xc0\x0c" + struct.pack(">HHIH", 1, 1, 5, 4) + bytes([192, 0, 2, 10]) if kind == 1 else b""
        header = query[:2] + struct.pack(">HHHHH", 0x8580, 1, 1 if answer else 0, 0, 0)
        server.sendto(header + question + answer, peer)


class Lists(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        try:
            with open(lists + self.path, "rb") as file:
                body = file.read()
        except OSError:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *arguments):
        pass


threading.Thread(target=serve_dns, daemon=True).start()
http.server.ThreadingHTTPServer(("0.0.0.0", 8000), Lists).serve_forever()
