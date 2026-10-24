# Executor inputs and outputs (agent protocol v1)

Each `Assignment` names an `executor` and an `action`. `input_json` and
`outputs_json` are JSON objects with camelCase fields, as below. Secrets never
appear in `input_json`; the input names them, and their values arrive in the
assignment's `secrets` map. The agent replaces every secret value with `***` in log
lines and error messages.

Unknown input fields are ignored, so the server can add fields without breaking
older agents. An executor that cannot parse its input fails the attempt. Outputs are
`{}` when an action has none.

Messages are at most 64 MiB in each direction. `Welcome.max_concurrency` 0 means 1
and `heartbeat_seconds` 0 means 10. `Hello.running_attempt_ids` includes attempts the
agent accepted and is still holding for capacity. The agent sends `AttemptStarted`
again on each new connection while an attempt runs.

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
`apply` and **both** `nixos-rootfs` actions receive privileged. Root verification
uses QGA command execution, even though its retry classification is read-only.
See [credential roles and rollout limits](../../../docs/proxmox-credentials.md).

```json
{ "endpoint": "https://10.1.0.10:8006/", "verifyTls": false, "caCertificatePem": null }
```

`caCertificatePem`, when set, is the only trust root for that connection, and it is
verified even if `verifyTls` is false; a missing `verifyTls` means true. VMs are
located through `GET /api2/json/cluster/resources?type=vm`, which works for
standalone hosts and clusters alike. Proxmox hides VMs a token cannot audit, so before
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
  "connection": { … }, "node": "korriban", "vmid": 210, "name": "krb-crt-01",
  "expectRunning": true, "expectIpv4": "10.1.0.21", "waitSeconds": 180
}
```

`expectIpv4` is null for plain DHCP without a declared reservation; then any non-loopback IPv4 address
reported by the QEMU guest agent satisfies the address check. The executor polls
every 5 seconds until every check passes or `waitSeconds` runs out.
For DHCP with a declared `network.reservedAddress`, that address is required,
just as the address without its prefix is required for static configuration.
The reservation itself remains externally managed.

Checks, in order: `present` (the VMID exists), `placement` (it is on `node`),
`name`, `running` (when `expectRunning`), and `address` (the guest agent reports
an IPv4 address, and `expectIpv4` is among them when set).

Output:

```json
{
  "present": true, "node": "korriban", "status": "running", "name": "krb-crt-01",
  "ipv4Addresses": ["10.1.0.21"],
  "checks": [
    { "name": "present", "ok": true, "detail": "VM 210 exists" },
    { "name": "address", "ok": true, "detail": "guest agent reports 10.1.0.21" }
  ]
}
```

Succeeds when every check passes, fails otherwise (with the outputs still set). With
`expectRunning: false` and the VM stopped, the address check is reported as passed
with "not checked".

### `verify-absent`

Input: `{ "connection": { … }, "vmid": 210, "waitSeconds": 60 }`

Output: `{ "present": false }`. Fails if the VMID still exists when the wait ends.

### `set-protection`

Input: `{ "connection": { … }, "node": "korriban", "vmid": 210, "protection": false }`

Output: `{ "absent": false, "previous": true, "protection": false }`. Sets the VM's
`protection` option with `PUT /nodes/{node}/qemu/{vmid}/config`. Succeeds with
`{ "absent": true, "previous": null, "protection": null }` when the VM does not exist,
and fails when it is on another node than `node`.

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
  "backend": {
    "type": "pg",
    "config": { "schema_name": "octopushy_state" },
    "workspace": "home.korriban.krb-crt-01"
  },
  "variables": { "octopushy": { … }, "guest": { … }, "host": { … } },
  "env": { "PROXMOX_VE_ENDPOINT": "https://10.1.0.10:8006/", "PROXMOX_VE_INSECURE": "true" },
  "secretEnv": { "PROXMOX_VE_API_TOKEN": "proxmox.token", "PG_CONN_STR": "backend.PG_CONN_STR" },
  "destroy": false,
  "retainNoChangesPlan": false
}
```

- `files_tar_gz` holds the repository snapshot (without `.git`); `root` is a
  directory inside it. The executor extracts it into the attempt's workspace and
  refuses entries that escape it: absolute paths, `..`, hard links, devices, and
  symbolic links that dangle or point outside the tree. (The server sends no links.)
- The backend type `local` is refused, because its state would vanish with the
  workspace. `env` and `secretEnv` may not set what the agent or the container
  runtime needs itself: `HOME`, `PATH`, `TMPDIR`, `USER`, `XDG_RUNTIME_DIR`, the `TF_*`
  variables the agent sets, and names starting with `TF_CLI_ARGS`, `CONTAINERS_`,
  `PODMAN_`, `DOCKER_`, or `BUILDAH_`.
- The executor writes `octopushy.auto.tfvars.json` (from `variables`) and
  `octopushy_backend.tf.json` (`{"terraform":{"backend":{"<type>":{}}}}`) into the
  root, so the root must not declare a backend of its own.
- `backend.config` becomes `-backend-config=key=value` arguments to `tofu init`.
  `backend.workspace`, when set, is selected with `tofu workspace select
  -or-create=true` after init.
- `env` and `secretEnv` are set for every OpenTofu command, together with
  `TF_IN_AUTOMATION=1`, `TF_INPUT=0`, and `TF_PLUGIN_CACHE_DIR`. Secret values are
  passed through the environment, never as command-line arguments.

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
is not logged or returned, because it can hold configuration values.

When `hasChanges` is true, or optional `retainNoChangesPlan` is true (default
false), the result carries the artifact `plan.tar.gz`, holding
`plan.bin` and the root's `.terraform.lock.hcl` after init. A root without providers
has no lock file; then the archive holds only `plan.bin`.

The controller requests retention for provision runs that include privileged
guest filesystem work. This supplies an exact plan digest for approval even when
infrastructure itself has no changes; it does not request `show` or `apply` for a
no-change plan. Older workers may ignore the field, but a missing required plan
artifact blocks subsequent privileged work rather than bypassing approval.

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

## `nixos-rootfs`

An explicitly advertised capability, selected only by recipes opting into
`guestFilesystem: nixos-ext4-root`. See
[supported layout and recovery](../../../docs/guest-filesystem-growth.md).

Actions: `grow` (mutating), `verify` (read-only). Uses only `proxmox.token`
and the existing connection TLS/CA policy, never OpenTofu backend credentials.
Both actions take:

```json
{
  "connection": { "endpoint": "https://pve.example:8006/", "verifyTls": true },
  "node": "pve",
  "vmid": 9210,
  "name": "opy-service-01",
  "address": "home/pve/opy-service-01",
  "diskBytes": 27917287424,
  "waitSeconds": 180
}
```

The VM name, Octopushy tag, and exact managed-by description must match before
any guest command is submitted. A fixed argument-array command uses QEMU
guest-agent execution and the native NixOS operation lock. No caller-supplied
script or path is accepted. Its stdout reports `diskBytes`, `partitionBytes`,
`filesystemBytes` (ext4 block count × block size), `usableBytes` (filesystem
statistics), `uuid`, and `changed`. Verification must report `changed: false`.

There is one command submission per attempt; status is polled by PID.
Lost submission/status responses, deadline expiry, or signals after a growth
submission are `uncertain`. Native lock contention and completed nonzero
commands are known failures; capacity checks still run on a retry. A completed
growth process and a successful infrastructure apply are not substitutes for
the subsequent independent `verify` step.
