"""Temporary, single-image HTTP source restricted to the selected Proxmox IP.

OpenTofu also verifies the image checksum. Stop this process after import.
"""
import argparse
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--bind", required=True)
    parser.add_argument("--allow", required=True)
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()
    artifact = args.artifact.resolve(strict=True)

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *positional, **kwargs):
            super().__init__(*positional, directory=str(artifact.parent), **kwargs)

        def send_head(self):
            if self.client_address[0] != args.allow or self.path != "/" + artifact.name:
                self.send_error(403)
                return None
            return super().send_head()

    ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
