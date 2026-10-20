#!/usr/bin/env python3
"""Deterministic test double. Never contacts Proxmox or executes infrastructure."""
import json
import os
from pathlib import Path
import sys
import time

args = sys.argv[1:]
command = args[0]
config = json.loads(Path("fixture.json").read_text())
if config.get("trace"):
    with open(config["trace"], "a") as trace:
        trace.write(json.dumps(args) + "\n")
if command == "init":
    path = next((a.split("=", 2)[2] for a in args if a.startswith("-backend-config=path=")), config.get("state_file"))
    Path(".terraform").mkdir(exist_ok=True)
    Path(".terraform/terraform.tfstate").write_text(json.dumps({
        "backend": {"type": config.get("backend", "local"), "config": {"path": path}, "hash": 1}
    }))
    print("Initialized test backend.")
    sys.exit(0)
metadata = json.loads(Path(".terraform/terraform.tfstate").read_text())
state_file = Path(metadata["backend"]["config"]["path"])
state = json.loads(state_file.read_text()) if state_file.exists() else {}
if command == "validate":
    if config.get("invalid"):
        print("Invalid test configuration. " + os.environ.get("PROXMOX_VE_API_TOKEN", ""), file=sys.stderr)
        sys.exit(1)
    print("Configuration is valid.")
elif command == "version":
    print(json.dumps({"terraform_version": "fake-1", "platform": "linux_amd64"}))
elif command == "state":
    if state:
        print(json.dumps(state))
    else:
        print("No state file was found!", file=sys.stderr)
        sys.exit(1)
elif command == "plan":
    if config.get("sleep"):
        time.sleep(config["sleep"])
    target = config.get("generation", 1)
    desired = [] if target == 0 else [{
        "mode": "managed", "type": "proxmox_virtual_environment_vm", "name": "vm",
        "instances": [{"attributes": {"vm_id": 201, "node_name": "pve-test", "name": "test-vm",
                                     "started": False, "generation": target,
                                     "api_token": os.environ.get("PROXMOX_VE_API_TOKEN", "")},
                       "sensitive_attributes": [[{"type": "get_attr", "value": "api_token"}]]}],
    }]
    old = state.get("resources", [])
    actions = config.get("actions", ["create" if not old else "delete" if not desired else "update"])
    changes = [] if old == desired else [{"address": "proxmox_virtual_environment_vm.vm", "change": {"actions": actions}}]
    plan = {"resource_changes": changes, "desired": desired, "config": config}
    out = next(a[5:] for a in args if a.startswith("-out="))
    Path(out).write_text(json.dumps(plan))
    print(f"Test plan: {len(changes)} changed resources.")
    sys.exit(2 if changes else 0)
elif command == "show":
    plan = json.loads(Path(args[-1]).read_text())
    if "-json" in args:
        print(json.dumps(plan))
    else:
        for r in plan["resource_changes"]:
            print(r["address"], " / ".join(r["change"]["actions"]))
        print("TEST DOUBLE — no real infrastructure.")
elif command == "apply":
    assert "-auto-approve" not in args and Path(args[-1]).is_file(), "Expected saved plan"
    plan = json.loads(Path(args[-1]).read_text())
    state_file.parent.mkdir(parents=True, exist_ok=True)
    state_file.write_text(json.dumps({"version": 4, "lineage": state.get("lineage", "test-lineage"),
                                     "serial": state.get("serial", 0) + 1, "resources": plan["desired"]}))
    if plan["config"].get("fail_apply"):
        print("Test partial apply failure.", file=sys.stderr)
        sys.exit(1)
    print("Test apply complete.")
else:
    sys.exit("Unsupported test command: " + command)
