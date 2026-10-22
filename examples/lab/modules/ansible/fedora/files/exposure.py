#!/usr/bin/env python3
"""Reconcile only one service's IPv4/TCP firewalld rules; no global reload."""
import fcntl
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


class Firewall:
    def query(self, permanent, zone, rule):
        args = ["/usr/bin/firewall-cmd"]
        if permanent:
            args.append("--permanent")
        args.append("--zone=" + zone)
        query = subprocess.run(args + ["--query-rich-rule=" + rule], capture_output=True, text=True)
        if query.returncode not in (0, 1):
            raise RuntimeError("Could not query firewalld: " + query.stderr.strip())
        return args, query.returncode == 0

    def has(self, permanent, zone, rule):
        return self.query(permanent, zone, rule)[1]

    def change(self, permanent, zone, rule, present):
        args, exists = self.query(permanent, zone, rule)
        if exists == present:
            return False
        action = "--add-rich-rule=" if present else "--remove-rich-rule="
        result = subprocess.run(args + [action + rule], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError("Could not update firewalld: " + result.stderr.strip())
        return True


def desired(config):
    port = config["port"]
    if type(port) is not int or not 1024 <= port <= 65535:
        raise ValueError("The first workflow accepts unprivileged TCP ports 1024..65535.")
    address = str(ipaddress.IPv4Address(config["address"]))
    if ipaddress.ip_address(address).is_unspecified or ipaddress.ip_address(address).is_loopback:
        raise ValueError("Bind to the VM's explicit LAN IPv4 address.")
    zone = config.get("zone", "public")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,40}", zone):
        raise ValueError("Invalid firewalld zone.")
    sources = sorted({str(ipaddress.IPv4Network(s, strict=True)) for s in config["sources"]})
    if not sources or any(ipaddress.ip_network(s).prefixlen == 0 for s in sources):
        raise ValueError("Declare explicit allowed IPv4 source networks, not 0.0.0.0/0.")
    suffix = f'destination address="{address}" port port="{port}" protocol="tcp"'
    # The drop is installed first. Source-specific accepts run before it, even if
    # another administrator has a broader positive-priority allow for this port.
    rules = [f'rule priority="-200" family="ipv4" {suffix} drop']
    rules += [f'rule priority="-300" family="ipv4" source address="{s}" {suffix} accept' for s in sources]
    return {"port": port, "address": address, "zone": zone, "rules": rules}


def save(path, value):
    with tempfile.NamedTemporaryFile("w", dir=path.parent, delete=False) as stream:
        temporary = stream.name
        json.dump(value, stream, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def reconcile(config, root=Path("/var/lib/octopushy/exposure"), firewall=None):
    identity = config["id"]
    if not re.fullmatch(r"[a-z][a-z0-9-]{0,39}", identity):
        raise ValueError("Invalid service id.")
    operation = config["operation"]
    if operation not in ("apply", "check", "remove"):
        raise ValueError("Expected apply, check, or remove.")
    firewall = firewall or Firewall()
    root.mkdir(parents=True, exist_ok=True, mode=0o750)
    with (root / ".lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        path = root / (identity + ".json")
        previous = json.loads(path.read_text()) if path.exists() else None
        if operation == "remove":
            if previous is None:
                return False
            changed = False
            # Remove grants before removing the default deny; the workload must
            # already be stopped by the calling workflow.
            for rule in sorted(previous["rules"], key=lambda r: r.endswith(" drop")):
                for permanent in (False, True):
                    changed = firewall.change(permanent, previous["zone"], rule, False) or changed
            path.unlink()
            return changed
        target = desired(config)
        if operation == "check":
            if previous != dict(target, pending=False):
                raise ValueError("Run expose for these exact service inputs before deploying.")
            if not all(firewall.has(p, target["zone"], r) for r in target["rules"] for p in (False, True)):
                raise ValueError("Exposure drift detected; run expose and verify it before deploying.")
            return False
        if previous and any(previous[k] != target[k] for k in ("port", "address", "zone")):
            raise ValueError("Changing a binding requires explicit service removal first.")
        for other in root.glob("*.json"):
            if other == path:
                continue
            owner = json.loads(other.read_text())
            if (owner["port"], owner["address"]) == (target["port"], target["address"]):
                raise ValueError("The binding is owned by service " + other.stem)
        old_rules = previous["rules"] if previous else []
        for rule in set(target["rules"]) - set(old_rules):
            if any(firewall.has(p, target["zone"], rule) for p in (False, True)):
                raise ValueError("A required firewall rule already exists without this service's ownership; refusing adoption.")
        # Record ownership/intended changes before remote effects. A partial
        # failure retains the union, so an explicit rerun/remove can reconcile it.
        pending = dict(target, rules=sorted(set(old_rules + target["rules"])), pending=True)
        save(path, pending)
        changed = False
        for rule in target["rules"]:
            if rule.endswith(" drop"):
                for permanent in (False, True):
                    changed = firewall.change(permanent, target["zone"], rule, True) or changed
        for rule in set(old_rules) - set(target["rules"]):
            for permanent in (False, True):
                changed = firewall.change(permanent, target["zone"], rule, False) or changed
        for rule in target["rules"]:
            if not rule.endswith(" drop"):
                for permanent in (False, True):
                    changed = firewall.change(permanent, target["zone"], rule, True) or changed
        save(path, dict(target, pending=False))
        return changed


if __name__ == "__main__":
    try:
        if os.geteuid() != 0:
            raise ValueError("Run the exposure operation as root on the managed guest.")
        print(json.dumps({"changed": reconcile(json.load(sys.stdin))}))
    except (KeyError, TypeError, ValueError, RuntimeError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
