# Real Proxmox end to end

`run.sh` takes one VM through its whole life on a real Proxmox host, through Octopushy:

1. **Observe** the host and pick a free VMID (the first from 9900 to 9999 that Proxmox
   does not report, or `TEST_VMID`), after checking there are 5 GiB of free memory.
2. **Create** `opy-e2e-01` from the Fedora 44 cloud image with the `proxmox-vm` recipe:
   2 cores, 2 GiB, a 16 GiB disk, DHCP on `vmbr0`, and a throwaway SSH key in
   `.dev/proxmox-e2e/ssh-key` for the `ops` user.
3. **Resize** it to 4 cores, 4 GiB, and a 20 GiB disk.
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
