"""Prepare one disposable VM; verified host keys by default, gated test-only TOFU."""
import ipaddress
import json
import os
from pathlib import Path
import re
import ssl
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


def captured_host_key(address, text):
    lines = [line.split() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    if (len(lines) != 1 or len(lines[0]) != 3 or lines[0][0] != address
            or lines[0][1] != "ssh-ed25519" or not re.fullmatch(r"[A-Za-z0-9+/=]+", lines[0][2])):
        raise ValueError("Could not capture a single valid test SSH host key.")
    return " ".join(lines[0]) + "\n"


def main(work):
    vmid = int((work / "vmid").read_text())
    node = os.environ.get("PVE_NODE", "umbrel")
    token = Path(os.environ["PVE_TOKEN_FILE"]).read_text().strip()
    endpoint = os.environ.get("PVE_ENDPOINT", "https://10.1.0.85:8006/").rstrip("/")
    context = ssl._create_unverified_context()  # Same explicit trusted-LAN test assumption as provisioning.

    def api(path, data=None):
        body = None if data is None else json.dumps(data).encode()
        request = urllib.request.Request(endpoint + "/api2/json" + path, data=body,
            headers={"Authorization": "PVEAPIToken=" + token, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, context=context, timeout=30) as response:
            return json.load(response)["data"]

    prefix = f"/nodes/{urllib.parse.quote(node, safe='')}/qemu/{vmid}"
    guest = api(prefix + "/config")
    if guest.get("name") != os.environ.get("TEST_NAME", "opy-e2e-01") or "octopushy" not in guest.get("tags", "").split(";"):
        raise ValueError("Refusing to execute commands in a guest other than the disposable Octopushy test VM.")
    interfaces = api(prefix + "/agent/network-get-interfaces")["result"]
    addresses = [address for interface in interfaces if interface["name"] != "lo"
        for address in interface.get("ip-addresses", [])
        if address.get("ip-address-type") == "ipv4"
        and not ipaddress.ip_address(address["ip-address"]).is_link_local]
    if len(addresses) != 1:
        raise ValueError("The fresh test guest must have exactly one non-loopback LAN IPv4 address.")
    address = addresses[0]["ip-address"]
    source = str(ipaddress.ip_network(f'{address}/{addresses[0]["prefix"]}', strict=False))
    output = work / "fedora"
    trusted_file = os.environ.get("TEST_SSH_KNOWN_HOSTS")
    if not trusted_file and (output / "known_hosts").is_file():
        trusted_file = str(output / "known_hosts")
    if trusted_file:
        known_hosts = Path(trusted_file).read_text()
        if not known_hosts.strip():
            raise ValueError("TEST_SSH_KNOWN_HOSTS is empty.")
    elif os.environ.get("ALLOW_TEST_SSH_TOFU") == "1":
        print("WARNING: explicit test-only SSH TOFU; the first host key is NOT independently authenticated.")
        scan = subprocess.run(["ssh-keyscan", "-T", "10", "-t", "ed25519", address],
            capture_output=True, text=True, timeout=20, check=True)
        known_hosts = captured_host_key(address, scan.stdout)
    else:
        raise ValueError("Supply TEST_SSH_KNOWN_HOSTS verified out of band, or explicitly authorize ALLOW_TEST_SSH_TOFU=1 for the disposable LAN test.")
    output.mkdir(mode=0o700, exist_ok=True)
    (output / "known_hosts").write_text(known_hosts)
    inventory = (f"[fedora]\ntest-vm ansible_host={address} ansible_user=ops\n\n[fedora:vars]\n"
        f"ansible_ssh_private_key_file={work / 'ssh-key'}\n"
        "ansible_ssh_common_args='-o StrictHostKeyChecking=yes "
        f"-o UserKnownHostsFile={output / 'known_hosts'} -o IdentitiesOnly=yes'\n")
    (output / "inventory.ini").write_text(inventory)
    image = "docker.io/hashicorp/http-echo@sha256:fcb75f691c8b0414d670ae570240cbf95502cc18a9ba57e982ecac589760a186"
    for identity, port, text in [("app-a", 8080, "octopushy-a-v1"), ("app-b", 8081, "octopushy-b-v1")]:
        config = {"service": {"id": identity, "port": port, "address": address, "sources": [source],
            "container_port": 5678, "image": image, "command": ["-listen=:5678", "-text=" + text],
            "expect": text, "verify_path": "/"}}
        (output / (identity + ".json")).write_text(json.dumps(config, indent=2) + "\n")
    first = json.loads((output / "app-a.json").read_text())
    denied = json.loads(json.dumps(first))
    denied["service"]["sources"] = ["192.0.2.0/24"]
    (output / "app-a-denied.json").write_text(json.dumps(denied, indent=2) + "\n")
    updated = json.loads(json.dumps(first))
    updated["service"].update(command=["-listen=:5678", "-text=octopushy-a-v2"], expect="octopushy-a-v2")
    (output / "app-a-v2.json").write_text(json.dumps(updated, indent=2) + "\n")
    first["service"]["expect"] = "deliberately incorrect response"
    (output / "app-a-bad-check.json").write_text(json.dumps(first, indent=2) + "\n")
    (output / "address").write_text(address + "\n")
    print(f"Pinned the fresh VM's SSH host key for subsequent connections; LAN address {address}.")


if __name__ == "__main__":
    try:
        main(Path(sys.argv[1]).resolve())
    except (ValueError, KeyError, OSError, urllib.error.URLError) as error:
        # Request headers and credential values are deliberately never printed.
        print("Fedora test preparation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
