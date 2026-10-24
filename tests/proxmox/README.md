# Real Proxmox end to end

This harness exercises optional **split-token mode**, not the product's simpler
one-token setup. See [credentials](../../docs/proxmox-credentials.md) for both modes.

**Live/full-lifecycle acceptance is blocked with strictly read-only planning.**
In bpg/proxmox v0.117.0, VM refresh discovers missing disk formats (especially
EFI/raw volumes) through `storage/content/{volume}`. Upstream PVE requires
`Datastore.Audit` plus `VM.Config.Disk` on the owning VM, or the broader
`Datastore.Allocate` on storage, for that lookup. A strictly read-only identity
therefore cannot universally refresh or plan updates/destroys. Fresh-create
planning has no existing VM to refresh; it does not establish full-lifecycle
support. Do not add mutation permissions to the read-only identity, fall back to
privileged credentials, or use `-refresh=false` to bypass this blocker. See the
[credential roles guide](../../docs/proxmox-credentials.md) and
[pinned research](../../docs/research/proxmox-credential-split.md).

The split fixtures and offline preflight checks below are readiness work, not
live acceptance. The intended `run.sh` lifecycle on a disposable Proxmox host is:

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

The harness explicitly grants execution trust to each **exact local fixture
commit** it creates, using the normal authenticated review/trust API. It checks
the configured repository path, branch and full commit before granting trust;
it never trusts an arbitrary fetched head. Running this harness therefore
authorizes its fixture code to receive execution credentials, separately from
the plan checks above. Review the harness and fixture before running it; do not
point it at an ordinary lab repository. Incomplete reviews, source changes and
stale confirmations stop the harness rather than bypassing the trust gate.
These trust-helper checks are tested offline; this milestone did not rerun the
live Proxmox harness.

The image (about 580 MB) is downloaded by the Proxmox host into its `local` storage and
removed with the VM.

## Two explicit credentials

Supply two independently provisioned API-token files, each formatted
`USER@REALM!ID=SECRET`, with mode `0600` or `0400`:

```sh
export PVE_READ_ONLY_TOKEN_FILE=/private/disposable-proxmox-read-only-token
export PVE_PRIVILEGED_TOKEN_FILE=/private/disposable-proxmox-privileged-token
```

Both files must be nonempty private regular files. The shared local/remote
preflight validates both before DNS, fixture/database changes or process startup.
The same file (including symlinks/hardlinks) or equal credential contents after
trimming surrounding whitespace are refused without printing values.
`PVE_TOKEN_FILE` is **not** a fallback; neither file has a default, and the harness
never reads the retained `.dev/secrets/umbrel-proxmox-token` path by default.

Fresh fixtures import the read-only and privileged values over stdin into
`proxmox/<PVE_NODE>/read-only-token` and `proxmox/<PVE_NODE>/privileged-token`.
Their `connection.readOnlyToken` and `connection.privilegedToken` reference those
distinct store keys. Observe/inventory and planning use read-only credentials;
apply uses privileged credentials. The executor wire alias remains `proxmox.token`;
it does not imply a legacy definition alias or a credential fallback.

Legacy `connection.token` definitions are intentionally invalid until explicit,
reviewed migration. Cleanup/resume refuses old or mismatched fixture credential
references before controller calls/builds. It never rewrites definitions,
copies an old token into two keys, reimports credentials, or rebinds historical
run contexts. Preserve retained state and inspect its original context rather
than editing old run records to bypass the guard.

## Offline checks

These need Python 3 and, for the shell import/preflight tests, Bash, jq and rg;
they do not start services, resolve DNS or contact Proxmox:

```sh
python3 tests/remote-worker/config.py --credentials-only
python3 -m unittest discover -s tests/remote-worker -p 'test_*.py'
python3 -m unittest discover -s tests/e2e -p 'test_*.py'
bash -n tests/proxmox/run.sh
```

Mock fixtures use distinct synthetic identities, but their API does not prove
upstream PVE privilege separation. Offline wiring checks do not establish that
the provider can refresh with a strictly read-only identity.

## Intended running mode (currently blocked)

Do not run this as passing split-credential lifecycle acceptance until the
provider/PVE blocker above is resolved and a separate live run is authorized:

```sh
just dev-db
just proxmox-e2e                 # or: UI=1 tests/proxmox/run.sh to watch at :5174
```

Other hosts: `PVE_ENDPOINT`, `PVE_NODE`, `PVE_IMAGES`, `PVE_DISKS`, `PVE_BRIDGE`,
and both explicit token-file variables. If a run stops after the VM was created,
preserve its fixture/state. `CLEANUP=1 tests/proxmox/run.sh` is intended to retire
it through Octopushy using the stopped run's state, but refresh/destroy planning
is subject to the same blocker; do not assume cleanup can complete. Logs are in
`.dev/proxmox-e2e`. Local mode also needs Python 3 for credential preflight and rg
for retained-fixture checks.

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

The unchanged Fedora helper's `PVE_TOKEN_FILE` interface receives the explicit
read-only file for its GET/config/network checks. The opt-in Portainer helper
uses guest-agent execution and receives the privileged file through that same
existing interface only when its workflow is selected. Neither helper was
converted to a new credential interface; the surrounding real lifecycle remains
blocked as described above.

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
