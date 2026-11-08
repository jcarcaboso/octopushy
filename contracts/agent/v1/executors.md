# Executor inputs and outputs (agent protocol v1)

Each `Assignment` names an `executor` and an `action`. `input_json` and
`outputs_json` are JSON objects with camelCase fields, as below. Secrets never
appear in `input_json`; the input names them, and their values arrive in the
assignment's `secrets` map. The agent replaces every secret value with `***` in log
lines and error messages.

Unknown top-level input fields are ignored, so the server can add fields without breaking
older agents. An executor that cannot parse its input fails the attempt. Outputs are
`{}` when an action has none.

Messages are at most 64 MiB in each direction. `Welcome.max_concurrency` 0 means 1
and `heartbeat_seconds` 0 means 10. `Hello.running_attempt_ids` includes attempts the
agent accepted and is still holding for capacity. The agent sends `AttemptStarted`
again on each new connection while an attempt runs. `Hello.capabilities` lists features
that are not executors; a controller sends built-in OpenTofu state work only to agents
that advertise `opentofu-builtin-state`, and older agents send no capabilities.

## Enrollment token and CA

An enrollment token is `opy1.<secret>.<fingerprint>`: `secret` is base64url without
padding and goes in `EnrollRequest.token`; `fingerprint` is the lowercase hex
SHA-256 of the agent CA certificate in DER form. The agent fetches the CA from
`GET <server>/agent/ca.pem` (PEM; the TLS certificate is not checked for this one
request), refuses it unless the fingerprint matches, and from then on connects
with that CA as its only trust root.

The agent's certificate subject CN is its agent ID. The certificate is valid for
30 days; the agent renews it when less than a third of its validity remains.

## `proxmox`

All actions take a `connection` and the secret `proxmox.token`, formatted
`USER@REALM!TOKENID=SECRET` and sent as `Authorization: PVEAPIToken=<token>`.

`proxmox.token` is a wire alias, not a choice the worker makes. The controller
selects the pinned read-only credential for `observe`, `verify-guest`, and
`verify-absent`; `set-protection` receives the pinned privileged credential only
after the run's approval gate. OpenTofu `preview` also receives read-only, while
`apply` receives privileged. In shared mode, these actions use the same pinned
token, with the approval gate unchanged for privileged work.
See [credential roles and rollout limits](../../../docs/proxmox-credentials.md).

```json
{ "endpoint": "https://10.1.0.10:8006/", "verifyTls": false, "caCertificatePem": null }
```

`caCertificatePem`, when set, is the only trust root for that connection, and it is
verified even if `verifyTls` is false; a missing `verifyTls` means true. VMs and
containers are located through `GET /api2/json/cluster/resources?type=vm`, which lists
both and works for standalone hosts and clusters alike. A VMID is one namespace across
the two. Proxmox hides VMs a token cannot audit, so before
concluding that a VM is absent the executor checks that the token has `VM.Audit` on
`/vms/<vmid>`, and fails if it does not.

### `observe`

Input: `{ "connection": { … } }`

Output:

```json
{
  "version": "8.4.1",
  "cluster": { "name": "home", "quorate": true },
  "hosts": [
    {
      "name": "korriban", "online": true, "cpu": 0.07, "maxCpu": 16,
      "memoryBytes": 12884901888, "maxMemoryBytes": 68719476736, "uptimeSeconds": 86400
    }
  ],
  "guests": [
    {
      "vmid": 210, "name": "krb-crt-01", "host": "korriban", "kind": "qemu",
      "status": "running", "template": false, "tags": ["nixos", "opentofu"],
      "maxCpu": 4, "maxMemoryBytes": 8589934592, "uptimeSeconds": 3600
    }
  ]
}
```

`cluster` is null for a standalone host. A host that is offline has
`online: false` and null figures. Fails when the API is unreachable or refuses the
token.

### `verify-guest`

Input:

```json
{
  "connection": { … }, "node": "korriban", "vmid": 210, "kind": "qemu", "name": "krb-crt-01",
  "expectRunning": true, "expectIpv4": "10.1.0.21", "waitSeconds": 180, "hostKeys": null
}
```

`kind` is the machine the controller expects at the VMID: `qemu` for a VM, `lxc` for a
container. A missing `kind` means `qemu`, which is what a controller before containers
meant. The status, configuration and addresses are read from that kind's endpoints:
`/nodes/{node}/qemu/{vmid}/…` and the QEMU guest agent for a VM,
`/nodes/{node}/lxc/{vmid}/status/current`, `/config` and `/interfaces` for a container,
whose addresses Proxmox reads from its network namespace, with no agent inside.

`expectIpv4` is null for plain DHCP without a declared reservation; then any non-loopback IPv4 address
reported by the QEMU guest agent, or for a container by Proxmox, satisfies the address check. The executor polls
every 5 seconds until every check passes or `waitSeconds` runs out.
For DHCP with a declared `network.reservedAddress`, that address is required,
just as the address without its prefix is required for static configuration.
The reservation itself remains externally managed.

Checks, in order: `present` (the VMID exists), `kind` (it is a guest of `kind`, reported
only when it is not, and nothing else is checked then: a machine of the other kind is
not this one), `placement` (it is on `node`), `name`, `running` (when `expectRunning`),
and `address` (an IPv4 address is reported, and `expectIpv4` is among them when set).

Output:

```json
{
  "present": true, "node": "korriban", "status": "running", "name": "krb-crt-01",
  "tags": ["octopushy", "home"], "description": "Managed by Octopushy as home/korriban/krb-crt-01.",
  "ipv4Addresses": ["10.1.0.21"],
  "checks": [
    { "name": "present", "ok": true, "detail": "VM 210 exists" },
    { "name": "address", "ok": true, "detail": "guest agent reports 10.1.0.21" }
  ]
}
```

`tags` and `description` are the machine's own (`description` from its QEMU or LXC
config). They are reported, not checked: the controller records a guest as owning its VM only
when a verified VM its approved plan created carries the tag `octopushy` and a description naming
the guest's address; see [the safety boundary](../../../docs/safety-boundary.md).

Succeeds when every check passes, fails otherwise (with the outputs still set). With
`expectRunning: false` and the VM stopped, the address check is reported as passed
with "not checked".

`hostKeys`, for a container only, also reads the SSH host keys Proxmox generated when the
controller's apply created it, once every other check has passed:

```json
"hostKeys": { "createdBy": "octopushy@pve!writer", "applyStartedSecondsAgo": 240, "applyFinishedSecondsAgo": 30, "optional": false }
```

Proxmox (`PVE::LXC::Setup`, `rewrite_ssh_host_keys`, run by `post_create_hook`) generates them
on the host and writes them into the container's root file system before it first starts,
whenever the template has `/etc/ssh`, and logs each one in the create task as
`Creating SSH host key 'ssh_host_<type>_key' - …` followed by `done: SHA256:<43> <comment>`.
The executor reads the node's clock (`GET /nodes/{node}/time`) and every task of the VMID on
`node` (`GET /nodes/{node}/tasks?vmid=…&source=all`, page by page with `start` and `limit` until a
page comes back short; it fails rather than use a history it cannot show is whole), and turns the
window, counted back from when the controller made the assignment, into the node's clock, with two
minutes either side. A task's owner is its whole principal: an archived task of an API token has
`user` and `tokenid` apart (pve-manager `PVE/API2/Tasks.pm`, `convert_token_task`), an active one
has `user@realm!token` in `user`, and a user and each of its tokens are different principals. It
takes exactly one task: a `vzcreate` whose status is `OK`, started by `createdBy`, inside the
window, with no `vzcreate`, `vzrestore`, `vzdestroy`, `vzmigrate`, `qmcreate`, `qmrestore`,
`qmdestroy` or `qmigrate` of the VMID starting after it. It reads that task's log
(`GET /nodes/{node}/tasks/{upid}/log`), and adds:

```json
{ "hostKeys": ["rsa SHA256:…", "ed25519 SHA256:…"], "hostKeysTask": "UPID:korriban:…:vzcreate:320:octopushy@pve!writer:" }
```

It fails, with the other outputs still set, when the history or the log cannot be read, when no
task or more than one qualifies, when a later task changed the VMID, or when the log shows no
key. With `optional: true`, which the controller sends when it retries the read of an earlier
creation in a run that created nothing, it passes instead, without `hostKeys` and with
`hostKeysError` saying why. A `hostKeys` that is not this object fails the input. Proxmox lists a task another user or
token started only to a token with `Sys.Audit` on `/nodes/{node}`.

### `verify-absent`

Input: `{ "connection": { … }, "vmid": 210, "waitSeconds": 60 }`

Output: `{ "present": false }`. Fails if the VMID still exists when the wait ends.

### `set-protection`

Input: `{ "connection": { … }, "node": "korriban", "vmid": 210, "kind": "qemu", "protection": false }`

Output: `{ "absent": false, "previous": true, "protection": false }`. Sets the machine's
`protection` option with `PUT /nodes/{node}/qemu/{vmid}/config`, or for `kind: lxc`
`PUT /nodes/{node}/lxc/{vmid}/config`. A missing `kind` means `qemu`, and a guest of
another kind at the VMID is refused, with nothing changed. Succeeds with
`{ "absent": true, "previous": null, "protection": null }` when the VM does not exist,
and fails when it is on another node than `node`. It is `uncertain` when the write may
have reached Proxmox, or was accepted and could not be read back, because a plain failure
tells the controller that protection did not change.

The controller sends it for the `unprotect` step with `protection: false` and for the
`reprotect` step with `protection: true`, always with the run's own `node` and `vmid`.

## `opentofu`

Runs OpenTofu in a container image (`image`), rootless, with only the attempt's
workspace and the agent's provider cache mounted. The `local` tool runtime runs a
`tofu` binary on the agent instead, for development and tests.

Input (the same for both actions):

```json
{
  "stack": "home.korriban.krb-crt-01",
  "root": "modules/opentofu/proxmox-vm",
  "image": "ghcr.io/opentofu/opentofu:1.13.1",
  "variables": { "octopushy": { … }, "guest": { … }, "host": { … } },
  "env": { "PROXMOX_VE_ENDPOINT": "https://10.1.0.10:8006/", "PROXMOX_VE_INSECURE": "true" },
  "secretEnv": { "PROXMOX_VE_API_TOKEN": "proxmox.token" },
  "destroy": false
}
```

- `files_tar_gz` holds the repository snapshot (without `.git`); `root` is a
  directory inside it. The executor extracts it into the attempt's workspace and
  refuses entries that escape it: absolute paths, `..`, hard links, devices, and
  symbolic links that dangle or point outside the tree. (The server sends no links.)
- `env` and `secretEnv` may not set what the agent or the container
  runtime needs itself: `HOME`, `PATH`, `TMPDIR`, `USER`, `XDG_RUNTIME_DIR`, the `TF_*`
  variables the agent sets, and names starting with `TF_CLI_ARGS`, `CONTAINERS_`,
  `PODMAN_`, `DOCKER_`, or `BUILDAH_`.
- The executor writes `octopushy.auto.tfvars.json` (from `variables`) and
  `octopushy_backend.tf.json` (the built-in state below) into the root, so the root must
  not declare a backend of its own.
- `env` and `secretEnv` are set for every OpenTofu command, together with
  `TF_IN_AUTOMATION=1`, `TF_INPUT=0`, and `TF_PLUGIN_CACHE_DIR`. Secret values are
  passed through the environment, never as command-line arguments.

### Built-in state

The controller keeps every stack's state; there is no other backend, and the input names
none. See [how it works](../../../docs/opentofu-state.md).

- The state travels on the agent channel only: an artifact `terraform.tfstate` in the
  assignment of `preview` and `apply`, an artifact of the same name in the `apply` result,
  and `AttemptStateSnapshot` messages during the apply. Its `sha256` is checked. It is
  never logged.
- A stack with no state yet still gets one: an empty seed with serial 0 and a stable
  lineage. With no state at all, OpenTofu's `local` backend drops the first resource of
  an apply from its intermediate writes (seen in 1.13.2), which is what a crash leaves.
- The executor writes it to `<workspace>/state/terraform.tfstate` and generates
  `{"terraform":{"backend":{"local":{"path":"<relative path to it>"}}}}`. The path is
  relative because the saved plan records it and applies from another workspace, and a
  container sees different absolute paths. OpenTofu needs no route to anything, so this
  works the same with every tool runtime and container network.
- `preview` returns no state. The controller ignores state from anything but the `apply`
  attempt holding the stack's lock.
- `apply` reports the state with its result whatever the outcome, because the state records
  what a failed apply did. OpenTofu writes the file when a resource finishes and at least
  20 seconds passed since its last write, as it does for remote backends. The executor
  checks the file every `poll_interval` and sends each complete new state as a snapshot;
  the newest replaces any not sent yet. An agent that restarts during an apply adds the state in the workspace to the
  `uncertain` result it reports.
- When OpenTofu cannot write the backend file it leaves `errored.tfstate` in the root, and
  the executor reports that instead, because it is the only copy of what the apply did. A
  file of that name from the repository is removed before OpenTofu runs. If that file is
  incomplete, no state is reported, because the backend file is out of date. If OpenTofu
  prints the line `Error: Failed to persist state to backend` and the file is missing, which
  it can be even when OpenTofu says it wrote it, the attempt is `uncertain`. So is an apply
  that exits with anything but 0 or 1, such as one that was killed.
- States over 32 MiB are not sent. An apply that started and returns no state is `uncertain`,
  whatever it reported.
- `AttemptStarted` means the executor ran. An attempt that finished without running, such as
  one cancelled while queued, sends no `AttemptStarted`.

### `preview`

Runs `init`, then `plan -detailed-exitcode -out=…` (with `-destroy` when `destroy`
is true).

Output:

```json
{
  "hasChanges": true,
  "changes": [
    {
      "address": "proxmox_virtual_environment_vm.guest", "type": "proxmox_virtual_environment_vm",
      "name": "guest", "actions": ["create"]
    }
  ],
  "counts": { "create": 1, "update": 0, "delete": 0, "replace": 0 },
  "toolVersion": "1.13.1"
}
```

`changes` comes from `tofu show -json` of the saved plan and leaves out `no-op` and
`read` actions. A replace has the actions `["delete", "create"]` or
`["create", "delete"]` and counts as `replace`, not as `delete`. The JSON plan itself
is not logged, because it can hold configuration values, and nothing of it is returned
but what this section names; the only configuration values among that are a VM's
`attributes`, and never a sensitive one.

A change also carries `actionReason`, OpenTofu's `action_reason`, when the plan gives
one, such as `replace_because_tainted`. A change to a
`proxmox_virtual_environment_vm` that exists carries `vmId` and `node`, the `vm_id` and
`node_name` its state holds. The controller lifts a provision run's protection only for
a delete or replace whose `vmId` and `node` are the guest's own, so an agent that leaves
them out never gets that VM's protection lifted, and the apply fails on it as before.

Every change carries `importing`: true when OpenTofu adopts an existing object into the
state (`change.importing` in the JSON plan). An import is listed even when its actions
are `["no-op"]`; it is not counted. The controller refuses any plan with an import.

A change to a resource of a Proxmox provider, one whose type starts with `proxmox_` or
whose `provider_name` ends in `/proxmox`, carries `proxmox: true` and, in `targets`,
every VM and node it names, from both `before` and `after`. A target has `vmId`, `node`,
or both. They come from the top-level attributes `vm_id`, `vmid` and `container_id`
(each a target of its own), `node_name`, `node` and `target_node`, and from guests named
inside `resource_id` or `path` (`vm:100`, `ct:100`, `qemu/100`, `lxc/100`, `/vms/100`).
`targetUnknown: true` says one of those attributes is not known until apply. Absent
fields stay off the wire.

```json
{
  "address": "proxmox_virtual_environment_firewall_options.vm",
  "type": "proxmox_virtual_environment_firewall_options", "name": "vm",
  "actions": ["create"], "importing": false, "proxmox": true,
  "targets": [{ "vmId": 310, "node": "korriban" }]
}
```

A change to a `proxmox_virtual_environment_vm` carries, in `attributes`, each of a few
of its attributes that the change sets to something else, so a person can see what the
plan does before approving it. Only these are read, from bpg/proxmox's blocks, which
`tofu show -json` holds as lists:

| `name`                     | read from                                        |
|----------------------------|--------------------------------------------------|
| `cpu.cores`, `cpu.sockets` | `cpu[0].cores`, `cpu[0].sockets`                 |
| `memory.dedicated`         | `memory[0].dedicated` (MiB)                      |
| `disk.<interface>.size`    | the `disk` entry with that `interface` (GiB)     |
| `tags`                     | `tags`, sorted                                   |
| `network_device.<i>.bridge`| `network_device[i].bridge`                       |

An attribute has `name`, `before` and `after`. A side that does not exist, such as
`before` of a create, is absent. A value is a number, a string, a bool, or for `tags` a
list of strings; an attribute with any other value is left out. When `before_sensitive`
or `after_sensitive` marks the value, a parent of it or anything inside it, the
attribute carries `sensitive: true` and neither value. When `after_unknown` marks it,
`after` is absent and the attribute carries `unknown: true`. A change to any other type
carries no `attributes`.

```json
{
  "address": "proxmox_virtual_environment_vm.guest",
  "type": "proxmox_virtual_environment_vm", "name": "guest",
  "actions": ["update"], "vmId": 210, "node": "korriban", "importing": false, "proxmox": true,
  "targets": [{ "vmId": 210, "node": "korriban" }],
  "attributes": [
    { "name": "memory.dedicated", "before": 4096, "after": 8192 },
    { "name": "disk.scsi0.size", "before": 32, "after": 64 },
    { "name": "tags", "before": ["octopushy"], "after": ["agents", "octopushy"] }
  ]
}
```

The controller refuses, before approval, a plan whose Proxmox change with a `create`,
`update` or `delete` action names a VM other than the run's own, names none and is not
of a type that never acts on a VM, or has `targetUnknown`; see
[the safety boundary](../../../docs/safety-boundary.md). So a summary that leaves
targets out fails closed.

When `hasChanges` is true, the result carries the artifact `plan.tar.gz`, holding
`plan.bin` and the root's `.terraform.lock.hcl` after init. A root without providers
has no lock file; then the archive holds only `plan.bin`.

### `apply`

The assignment carries the artifact `plan.tar.gz` from the preview. The executor
restores `.terraform.lock.hcl` into the root before `init` (then `-lockfile=readonly`),
runs `apply` on `plan.bin`, then `output -json`. If only `output -json` fails, the
attempt still succeeds, with empty outputs and a logged warning, because the change
was made.

Output:

```json
{ "outputs": { "vm_id": 210, "ipv4_addresses": [["10.1.0.21"]] }, "sensitiveOutputs": [] }
```

Sensitive outputs are listed by name in `sensitiveOutputs` and left out of
`outputs`. A failed apply is `failed`; OpenTofu's state records what it did, and the
next plan shows what is left. An apply interrupted by an agent restart is reported
as `uncertain` once the agent is back.

## `ansible`

Runs a repository's playbooks against one guest over SSH. See
[configuring a guest](../../../docs/guest-configuration.md). The container image's
entrypoint, and the `local` runtime's `--ansible-binary`, is `octopushy-ansible`, which
takes `ansible-playbook` arguments. It is not advertised if that program does not run.

Input (the same for both actions, except as noted):

```json
{
  "image": "docker.io/skorcius/octopushy:ansible-2.21.5-0123456789ab",
  "root": "modules/ansible/machine-tools",
  "action": "configure",
  "playbook": "configure.yml",
  "prepare": "prepare.yml",
  "prepared": null,
  "host": { "name": "krb-app-01", "address": "10.1.0.31", "port": 22, "user": "ops", "hostKey": "ssh-ed25519 AAAA…" },
  "vars": { "machine_tools_channel": "stable" },
  "approvedVars": {},
  "secretVars": { "machine_tools_token": "ansible.var.machine_tools_token" },
  "secretFiles": {},
  "sshKey": "ansible.ssh-key",
  "context": { "address": "home/korriban/krb-app-01", "commit": "…", "action": "configure", "run": "…" },
  "guestDirectory": "labs/home/proxmox/korriban/guests/krb-app-01",
  "outcomes": ["deferred"],
  "timeoutMinutes": 180,
  "manifest": { }
}
```

- `files_tar_gz` holds the repository snapshot, as for `opentofu`.
- `hostKey` is the pin, one `type base64 [comment]` per line. The executor writes
  `known_hosts` from it. It is the value of a plain store entry, so it travels in the input.
  The executor never scans a host.
- With `hostKeyFingerprints: true`, `hostKey` is instead a container's pin by fingerprint:
  one `type SHA256:<43>` per line, `type` being `rsa`, `ecdsa` or `ed25519`, as its create task
  logged them (see `verify-guest`). `known_hosts` stays empty, and ssh gets
  `KnownHostsCommand` (OpenSSH 8.5 or later) with `%I %H %f %t %K`: a short script that prints a
  `known_hosts` line for the key the server presented only when that key's SHA-256 fingerprint
  is pinned, and prints nothing otherwise. `StrictHostKeyChecking=yes` then accepts that one key
  and refuses any other, as it would with the key in the file: a fingerprint names exactly one
  key. `HostKeyAlgorithms` is limited to the pinned types and `FingerprintHash` is `sha256`.
- `vars`, `approvedVars`, `secretVars`, `secretFiles` and the keys of a prepared document may not
  start with `ansible_` or `octopushy`, in any case.
- Extra vars are, in order: `vars`, prepared values, `approvedVars`, the generated connection,
  secrets. The connection sets `ansible_host`, `ansible_port`, `ansible_user`,
  `ansible_host_key_checking: true`, `ansible_ssh_args`, the `ssh`, `scp` and `sftp` executables,
  and `octopushy`.
- No `--check`, `--tags`, `--limit` or `--start-at-task` is ever passed.
- Outputs of a failed or uncertain run never hold a task's result.
- `expectRefused`, on `run` only, names a secret holding keys that were removed, one after another.
  The playbook runs once with each key alone, at `LogLevel=VERBOSE`, and each time nothing may reach
  the guest, the last thing `ssh` reports must be `<user>@<address>: Permission denied (…).`, and it
  must not report that it authenticated. That report comes before a session starts, so a forced
  command cannot take it back. Then it runs with `sshKey`, which has to log in. Only then does the
  run succeed, with the number of keys in `refusedKeys` in its outputs. Anything else is a failure. From the third key on, runs are spaced so that `PerSourcePenalties` does not block the
  address.

### `preview`

Read-only. Carries no secrets and no `sshKey`, and does not contact the guest. Runs
`--version`, `--syntax-check` and `--list-tasks`, with `(secret)` standing in for each secret,
then the `prepare` playbook locally, if there is one, with `OCTOPUSHY_PREPARED_FILE` set. The
file must hold a JSON object of at most 256 KiB. `prepared` in the input supplies an earlier
run's values instead, and setting it with `prepare` is refused.

Output, and the content of the artifact `preview.json`:

```json
{
  "schemaVersion": 1,
  "action": "configure",
  "playbook": "configure.yml",
  "tasks": ["playbook: configure.yml", "  play #1 (all): Configure the machine"],
  "prepared": { "machine_tools_release": "2.0.0" },
  "toolVersion": "ansible-playbook [core 2.21.5]",
  "hostKeyFingerprints": ["ssh-ed25519 SHA256:…"],
  "manifest": { }
}
```

The controller records the artifact's SHA-256 as `planSha256`, which the approval names.

### `run`

**Mutating and not cancellable.** The assignment carries `preview.json`, whose SHA-256 is
checked, and the secrets `ansible.ssh-key`, `ansible.var.<name>` and `ansible.file.<name>`. They
reach `octopushy-ansible` on stdin as `{"sshKey", "secretVars", "secretFiles"}`, never in argv,
the environment or the workspace. A secret file's extra var holds its path.

```json
{
  "exitCode": 0, "tasks": 12,
  "recap": { "ok": 11, "changed": 3, "unreachable": 0, "failures": 0, "skipped": 1, "rescued": 0, "ignored": 0 },
  "failedTask": null, "unreachableTask": null, "outcome": null, "detail": null
}
```

The result comes from the exit status and the recap of the `octopushy_recap` callback. A task's
name can hold a secret, so the recap stays in memory until the playbook ends, when the
callback writes one line to stderr, `octopushy-recap: {…}`. The agent holds that line back
from the log, parses it as written, then masks its text and cuts it to 1024 characters.

| What happened | Outcome |
|---|---|
| Exit 0 | `succeeded` |
| `set_stats` set a declared `octopushy_outcome`, and no task was unreachable | `succeeded`, with `outcome` and `detail` |
| `octopushy_outcome` is not declared | `failed`, without repeating it |
| A task failed, and the recap was written | `failed`, naming the task |
| Exit 78, 125, 126 or 127 with no recap: the tool never ran | `failed` |
| ssh never connected on the first task: refused host key, refused login, no route | `failed` |
| The guest was lost after that, even after an outcome or in a task the play ignored | `uncertain` |
| Killed, or ended any other way without a recap | `uncertain` |
| Past `timeoutMinutes`, which is stopped with SIGTERM | `uncertain` |

An attempt interrupted by an agent restart is `uncertain`, as for other mutating actions.
With the `local` runtime the agent removes `/tmp/octopushy-attempt-<id>` afterwards, and on
restart for attempts it lost.
