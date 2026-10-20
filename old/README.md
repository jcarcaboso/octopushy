# Octopushy PoC (archived)

**Archived October 20, 2026.** This is the first Python proof of concept, kept
for reference. Octopushy is now being built from scratch as a separate backend,
frontend, and agent; see the [repository README](../README.md). Commands below run
from this `old/` directory.

A small, self-hosted **Git → OpenTofu plan → review → apply** control plane for
homelab VMs. This repository now contains a runnable local PoC, not just a mockup.

**Architecture alignment:** the [agreed product direction](../docs/architecture-agreement.md)
is being designed independently of this PoC. The
[comparison with the existing `nodes` automation](../docs/nodes-comparison.md)
records cases to consider, not a one-to-one Jenkins migration plan. The capabilities
and run instructions below describe only the current prototype.

- Register Proxmox API connections using **environment-variable references**.
- Link Git repositories or initialize a local repository with a VM template.
- Track **desired / checked / successfully applied** Git revisions per stack.
- Review resource changes and approve an **exact saved OpenTofu plan**.
- Preserve state outside disposable Git checkouts, or retain a remote backend.
- Inspect state-derived inventory, run history, command output, and recovery guidance.

OpenTofu owns infrastructure state. SQLite holds app metadata, not a replacement
state format. One stack owns one state and can contain multiple VMs. VM placement
comes from each resource's `node_name`.

## Run

Requirements: **Linux, Python 3.12+, Git, and OpenTofu** on `PATH`.
No Python packages or frontend build dependencies are required.

```sh
python3 octopushy.py serve
```

Open `http://127.0.0.1:8088`. Read `.octopushy/access-token` locally and enter that
**controller token** in the browser. It is not your Proxmox API token. Reloading
locks the browser. The data directory is private to the OS user.

Optional CLI settings go **before** the subcommand:

```sh
python3 octopushy.py --data-dir /secure/path/octopushy --tofu /path/to/tofu serve --port 8088
```

Run as a dedicated, non-root OS user. Loopback is the default. For a temporary
preview from another device on a **trusted LAN**, bind the host's specific LAN IP:

```sh
python3 octopushy.py serve --host 10.1.0.230 --port 8088
```

Replace the example IP with your host's address and open `http://10.1.0.230:8088`.
Token authentication and strict Host/Origin checks remain enabled. Wildcard and
public bind addresses are rejected. **HTTP, including the access token, is
unencrypted. Never port-forward this server or expose it to an untrusted/shared
network.** Use a secure tunnel for access outside your trusted LAN; this is not
a production authentication or job-isolation system.

## Connect your lab

1. Supply a Proxmox token through the controller's environment, preferably from
   your existing secret manager. For an interactive Bash session, avoid putting
   the value in shell history:

   ```sh
   read -rsp 'Proxmox token (user@realm!token-id=secret): ' OCTOPUSHY_SECRET_PVE
   printf '\n'
   export OCTOPUSHY_SECRET_PVE
   python3 octopushy.py serve
   ```

2. Open **Connections & repository setup**. Register `lab`, your HTTPS API
   endpoint, and the reference `OCTOPUSHY_SECRET_PVE`. Registration does not test
   API reachability or permissions.
3. Link a trusted repository with the manifest below, or choose **Initialize &
   link**. This creates `.octopushy/sources/<name>` and an initial Git commit;
   it never pushes to a forge.
4. Review the generated configuration, choose a compatible `bpg/proxmox` provider
   version, run `tofu init` locally, and commit `.terraform.lock.hcl`. The initial
   VM map is empty. Add reviewed VM/template IDs and placement in `main.tf`, then
   **commit** your changes.
5. **Sync Git → Create fresh plan → inspect the readable plan → Apply reviewed
   plan.** Sync alone never plans or applies anything.

Alternatively initialize a source repository at a path you choose, then link it:

```sh
python3 octopushy.py init-repo /absolute/new/path/homelab --connection lab
```

The generated example clones existing templates and starts clones stopped.
It does **not** build Nix/Fedora images, configure guest SSH, or install services.
Review provider compatibility, API permissions, storage, networking, TLS trust,
and template configuration before real use. TLS verification stays enabled;
configure private-CA trust on the controller.

## Manifest

Commit `.octopushy.json` at the Git repository root:

```json
{
  "version": 1,
  "stacks": [
    {
      "id": "vms",
      "path": "infra/vms",
      "connection": "lab",
      "backend": "managed-local"
    }
  ]
}
```

- `id`: stable stack name; `path`: root module inside the repository.
- `connection`: registered Proxmox connection; omit for provider-free testing.
- `managed-local`: opt in to **new** app-managed state; declare
  `backend "local" {}` inside your `terraform` block.
- `repository`: retain a committed **remote** backend definition. Local or
  implicit backends are rejected. State and credentials must not be in Git.
- Optional `env`: map `TF_VAR_*`, `AWS_*`, or `TF_HTTP_USERNAME` /
  `TF_HTTP_PASSWORD` to `OCTOPUSHY_SECRET_*` references. Example:
  `"env": {"TF_HTTP_PASSWORD": "OCTOPUSHY_SECRET_STATE_TOKEN"}`.
  Other backend authentication schemes need additional integration.

Connection credentials become `PROXMOX_VE_ENDPOINT` and `PROXMOX_VE_API_TOKEN` at
execution time; their values are not stored in SQLite or the manifest. Use an
SSH agent for private Git repositories. HTTPS URLs cannot embed credentials.
Git submodules, Git LFS, interactive authentication, and forge APIs are not built.

## State safety

Read [the operating guide](docs/poc.md) **before linking existing infrastructure**.

- Existing local state is **not automatically adopted/migrated**. Do not point
  new empty state at VMs already managed by Jenkins.
- Stop other writers during cutover. Remote backends must have locking configured.
- Stack binding changes are not automatic migrations.
- Do not delete `.octopushy/` to reset the UI: it holds state, history, and keys.
- Repositories/providers execute with your OS user's privileges. Only trusted
  code is supported. State, plans, and logs can contain sensitive data.

## Tests

```sh
python3 -m unittest discover -s tests -v
node --check web/app.js  # optional syntax check; Node is not a runtime dependency
```

Default tests use real temporary Git repositories and a fake OpenTofu executable.
No Proxmox calls occur. Add the real OpenTofu smoke tests with:

```sh
OCTOPUSHY_TEST_TOFU="$(command -v tofu)" python3 -m unittest discover -s tests -v
```

Those tests use only built-in `terraform_data`: persistent local state and a
local HTTP backend with locking. No VMs/cloud accounts. Tested with **1.13.1**.

## Deliberately outside this slice

Execution is **serial**, with manual sync/planning. No webhooks, file-based
triggers, schedules, notifications, remote agents, Ansible/SSH workflows, service
deployments, or live-health checks. A failed plan permits another plan; an
uncertain apply blocks execution until explicitly reconciled.

The [older architecture proposal](../docs/architecture-and-poc.md) is historical
reference, **not** the agreed specification or implemented capabilities.
[Domain vocabulary](CONTEXT.md),
[alternatives](../docs/research/jenkins-alternatives.md),
[service research](../docs/research/service-management-fit.md), and the
[older simulated dashboard](../docs/dashboard-concept.html) remain reference material.
