# Real Proxmox end to end

`run.sh` takes one VM through its whole life on a real Proxmox host, through Octopushy:

1. **Observe** the host and pick a free VMID (the first from 9900 to 9999 that Proxmox
   does not report, or `TEST_VMID`), after checking there are 3 GiB of free memory.
2. **Create** `opy-e2e-01` from the Fedora 44 cloud image with the `proxmox-vm` recipe:
   1 core, 1 GiB, a 16 GiB disk, DHCP on `vmbr0`, and a throwaway SSH key in
   `.dev/proxmox-e2e/ssh-key` for the `ops` user.
3. **Resize** it to 2 cores, 2 GiB, and a 20 GiB disk.
4. **Delete** it (`lifecycle: retired`).

The backend, a real agent, and OpenTofu (bpg/proxmox in rootless podman) do the work;
the OpenTofu state lives in the development PostgreSQL. The lab asks for approval of every
plan, and the script reviews each one before approving it: creating must only add,
resizing must update without replacing or deleting, and deleting must delete. Any other
plan is rejected and the test stops. After each step it asks Proxmox, through an observe
run, whether the VM is there with the expected size.

The image (about 580 MB) is downloaded by the Proxmox host into its `local` storage and
removed with the VM.

## The token

The host's API token goes in `.dev/secrets/umbrel-proxmox-token` (mode 0600), formatted
`USER@REALM!ID=SECRET`. The script copies it into Octopushy's variable store without
printing it. The `nodes` automation keeps it in Jenkins as `homelab-proxmox-token-umbrel`;
to copy it from there, put `user:api-token` of a Jenkins administrator in a file only you
can read, then:

```sh
JENKINS_AUTH_FILE=~/.config/octopushy/jenkins-auth \
JENKINS_URL=https://jenkins.hlab.alpetxino.com \
  tests/proxmox/token-from-jenkins.sh
```

## Running

```sh
just dev-db
just proxmox-e2e                 # or: UI=1 tests/proxmox/run.sh to watch at :5174
```

Other hosts: `PVE_ENDPOINT`, `PVE_NODE`, `PVE_IMAGES`, `PVE_DISKS`, `PVE_BRIDGE`,
`PVE_TOKEN_FILE`. If a run stops after the VM was created, the script says so; then
`CLEANUP=1 tests/proxmox/run.sh` retires the VM through Octopushy, reusing the stopped
run's state. Logs are in `.dev/proxmox-e2e`.

## Opt-in existing remote worker

`REMOTE_WORKER=1 tests/proxmox/run.sh` reuses a **dedicated existing controller**
and an **already enrolled, separately hosted worker**. It skips local builds,
backend/agent launch, enrollment and database resets. Explicit routable controller
API/agent-channel and PostgreSQL state endpoints, an exclusive worker label/UUID,
and private authentication files are required. No source checkout is needed on
the worker: assignments carry the source.

Read the exact [prerequisites, configuration, retention rules and run mode](../remote-worker/README.md)
before opting in. Existing controller repositories and fixture directories are
refused, not replaced. The default local mode still binds to loopback; remote mode
does not open listeners or change deployment settings. This is readiness work,
**not a claim of live remote-topology validation**.

## Optional Fedora service workflow

`FEDORA_WORKFLOW=1 TEST_SSH_KNOWN_HOSTS=/private/verified-known-hosts tests/proxmox/run.sh`
also exercises the reusable native
[Fedora workflow](../../examples/lab/modules/ansible/fedora/README.md) on the fresh
VM after resizing it, before retiring it. Install its pinned Ansible requirement
into `.dev/ansible` first.

This checks reusable setup, two independent rootless Compose projects and owned
firewall bindings, external HTTP verification, reboot persistence, source
restrictions, no-op reruns, an isolated
verification failure, an update, and service removal without breaking its sibling.
Supply independently verified SSH host trust. For the disposable trusted-LAN
test only, `ALLOW_TEST_SSH_TOFU=1` can replace `TEST_SSH_KNOWN_HOSTS`: it captures
an unauthenticated first key and pins it for subsequent connections. This does
not disable SELinux and does not validate production host enrollment. No existing
guest's keys are read or changed.

The Ansible steps are launched by the test harness, not the current Octopushy
controller/agent. They have no service UI, controller approvals, or automatic
Git reconciliation yet.
