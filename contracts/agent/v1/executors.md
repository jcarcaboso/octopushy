# Executor inputs and outputs (agent protocol v1)

Each `Assignment` names an `executor` and an `action`. `input_json` and
`outputs_json` are JSON objects with camelCase fields, as below. Secrets never
appear in `input_json`; the input names them, and their values arrive in the
assignment's `secrets` map. The agent replaces every secret value with `***` in log
lines, error messages, and every string of `outputs_json`, at any depth, for every
executor.

Unknown top-level input fields are ignored, so the server can add fields without breaking
older agents. An executor that cannot parse its input fails the attempt. Outputs are
`{}` when an action has none.

Messages are at most 64 MiB in each direction. `Welcome.max_concurrency` 0 means 1
and `heartbeat_seconds` 0 means 10. `Hello.running_attempt_ids` includes attempts the
agent accepted and is still holding for capacity. The agent sends `AttemptStarted`
again on each new connection while an attempt runs. `Hello.capabilities` lists features
that are not executors; a controller sends built-in OpenTofu state work only to agents
that advertise `opentofu-builtin-state`, a run holding a protected secret only to agents that
advertise `opentofu-reviewed`, and older agents send no capabilities.

## Enrollment token and CA

An enrollment token is `opy1.<secret>.<fingerprint>`: `secret` is base64url without
padding and goes in `EnrollRequest.token`; `fingerprint` is the lowercase hex
SHA-256 of the agent CA certificate in DER form. The agent fetches the CA from
`GET <server>/agent/ca.pem` (PEM; the TLS certificate is not checked for this one
request), refuses it unless the fingerprint matches, and from then on connects
with that CA as its only trust root.

The agent's certificate subject CN is its agent ID. The certificate is valid for
30 days; the agent renews it when less than a third of its validity remains.

## `guest`

Advertised separately from `proxmox`, so older workers cannot receive guest setup.

### `install-disk-reporter`

Runs after VM verification on provision runs only, including no-change plans. The
controller never schedules it for a container: Proxmox has no guest exec for LXC.
Takes the same `connection` object as `proxmox` and secrets `proxmox.token` and
`disk-reporter.token` (64 hexadecimal characters). No repository bundle or plan
artifact is needed.

```json
{
  "connection": { "endpoint": "https://10.1.0.10:8006/", "verifyTls": true },
  "node": "korriban",
  "vmid": 310,
  "name": "krb-app-01",
  "reporter": {
    "endpoint": "https://octopushy.example/api/guests/RESOURCE-UUID/disk-usage",
    "binaryUrl": "https://releases.example/disk-reporter-v1",
    "binarySha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "intervalSeconds": 3600,
    "caCertificatePem": null
  }
}
```

For ordinary guests this is `installationMode=fhs-download-v1` (the default;
controllers omit the mode field on this legacy wire shape for compatibility).
`reporter` is a strict object: HTTPS URLs without userinfo/query/fragment, a SHA-256
digest, interval 60–604800 seconds, and an optional public CA bundle (at most
16 KiB) for both download and reporting.

The executor confirms a running, non-template QEMU VM with matching name/node
and checks guest-exec privileges. It sends the installer as plain `input-data`
to `POST /nodes/{node}/qemu/{vmid}/agent/exec`, then polls `agent/exec-status`
with the returned PID. The guest command is
`/usr/bin/timeout --kill-after=5s 240s /bin/sh`; credentials are on stdin, not in
command arguments. Guest output is discarded, including on failure.

The installer downloads a pinned binary, checks its digest, installs the systemd
oneshot/timer, and starts a first report. Successful output:

```json
{ "installed": true, "firstReportSent": true, "binarySha256": "…", "intervalSeconds": 3600 }
```

Managed NixOS workers instead freeze `installationMode=managed-nixos-v1`, an
explicitly qualified `imageSha256`, and the existing address/generation/VM
identity expectations in the run. Their `reporter` object omits `binaryUrl` and
`binarySha256`: the image-native executable is **not** the downloadable release.
The assignment additionally carries top-level `address` and canonical UUID
`generation`. Native-v1 requires `intervalSeconds=3600`.

The native executor verifies the exact management tag/address/generation in VM
configuration before sending a single fixed command:
`/run/current-system/sw/bin/octopushy-worker-reporting` (no arguments). Its strict,
bounded stdin JSON contains `protocolVersion=1`, `generation`, `endpoint`,
`intervalSeconds`, optional `caCertificatePem`, and the separately resolved
reporter `token`. Never put this sensitive JSON into command arguments or the
**public-only** managed-identity bootstrap payload. A missing helper never selects
a shell or FHS fallback. New native assignments require an updated independent
control worker; older executors reject the unknown native mode before execution.

The fixed helper checks the enrolled guest generation, atomically writes protected
runtime credential files, starts the image's hardened systemd oneshot, and waits
for its new authenticated HTTPS/204 upload before starting the hourly timer.
Native success outputs have `installed`, `firstReportSent`, `installationMode`,
`imageSha256` (frozen image provenance expectation, not a measured running-OS
attestation), and `intervalSeconds`, but **no** `binarySha256`. Unconfigured image
units are inactive. See [rollout and validation](../../../docs/managed-worker-disk-reporting.md).

This action is **mutating and not cancellable**. A known nonzero exit fails;
ambiguous dispatch or lost status returns `OUTCOME_UNCERTAIN`. No automatic
remote-execution retries. Native helper timeout (124/137) or interruption is also
uncertain. Guest output is never forwarded. The controller requests a saved infrastructure plan even
when it has no changes, binding approval to its `planSha256` before releasing any
installation credentials. Missing or mismatched plans block installation.

## `proxmox`

All actions take a `connection` and the secret `proxmox.token`, formatted
`USER@REALM!TOKENID=SECRET` and sent as `Authorization: PVEAPIToken=<token>`.

`proxmox.token` is a wire alias, not a choice the worker makes. The controller
selects the pinned read-only credential for `observe`, `verify-guest`, and
`verify-absent`; `set-protection` receives the pinned privileged credential only
after the run's approval gate. OpenTofu `preview` also receives read-only, while
`apply`, **both** `nixos-rootfs` actions, and `guest` / `install-disk-reporter`
receive privileged. Root verification uses QGA command execution, even though its
retry classification is read-only. In shared mode, these actions use the same
pinned token, with the approval gate unchanged for privileged work.
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

`verify-guest` and `set-protection` take an optional `guestKind`, `qemu` or `lxc`. It
selects the API the guest answers under, `/nodes/{node}/qemu/...` or
`/nodes/{node}/lxc/...`. Left out it means `qemu`, so older payloads behave as before, and
the controller sets it only for containers. Any other value fails the attempt. A guest of
the other kind under the VMID fails both actions. `observe` and `verify-absent` take no
`guestKind`: `/cluster/resources` lists both kinds, each with its `kind`.

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
  "expectRunning": true, "expectIpv4": "10.1.0.21", "waitSeconds": 180, "guestKind": "qemu"
}
```

`expectIpv4` is null for plain DHCP without a declared reservation; then any non-loopback IPv4 address
reported by the QEMU guest agent satisfies the address check. A container has no guest
agent: its addresses come from `GET /nodes/{node}/lxc/{vmid}/interfaces`, which reads its
network namespace and needs only `VM.Audit`. The executor polls
every 5 seconds until every check passes or `waitSeconds` runs out.
For DHCP with a declared `network.reservedAddress`, that address is required,
just as the address without its prefix is required for static configuration.
The reservation itself remains externally managed.

Checks, in order: `present` (the VMID exists), `placement` (it is on `node`),
`name`, for a container `template` (it is not one), `running` (when `expectRunning`),
and `address` (the guest agent or the container reports an IPv4 address, and
`expectIpv4` is among them when set). Details name a container `CT 312`.

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

Input: `{ "connection": { … }, "node": "korriban", "vmid": 210, "protection": false }`,
plus `guestKind` and, for a container, `name`.

Output: `{ "absent": false, "previous": true, "protection": false }`. Sets the VM's
`protection` option with `PUT /nodes/{node}/qemu/{vmid}/config`, or the same path under
`lxc`. Succeeds with `{ "absent": true, "previous": null, "protection": null }` when the VM
does not exist, and fails when it is on another node than `node`. It also fails for a
container that is a template or whose name is not `name`.

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
  "secretEnv": {
    "PROXMOX_VE_API_TOKEN": "proxmox.token", "PG_CONN_STR": "backend.PG_CONN_STR",
    "TF_VAR_admin_password": "recipe.TF_VAR_admin_password"
  },
  "destroy": false,
  "retainNoChangesPlan": false,
  "replace": [],
  "files": {},
  "reviewed": null
}
```

- `reviewed`, when present, marks a run that holds a protected secret, which receives only the
  folders its reviewed code covers. `reviewed.modules` lists the repository folders of the modules the
  recipe lists, and `reviewed.noModules` says the root is known to call none. Absent, nothing changes. Present, the executor fails before running `tofu` unless the
  root has a `.terraform.lock.hcl` as a regular file, and fails if the root or a listed module holds
  `.terraform`. Every `tofu init` then gets `-lockfile=readonly`, and `-get=false` when `noModules` is
  true. After the init, `.terraform/modules/modules.json` is read and the step fails if any module
  has a source that does not start with `./` or `../`, or a directory that is not inside the root or a
  listed module after resolving it. Nothing is planned or applied then.
- `files_tar_gz` holds the repository snapshot (without `.git`); `root` is a
  directory inside it. The executor extracts it into the attempt's workspace and
  refuses entries that escape it: absolute paths, `..`, hard links, devices, and
  symbolic links that dangle or point outside the tree. (The server sends no links.)
- The backend type `local` is refused, because its state would vanish with the
  workspace; the built-in state below is the supported way to have none to set up.
  `env` and `secretEnv` may not set what the agent or the container
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
- `TF_VAR_<name>` in `secretEnv`, with the secret `recipe.TF_VAR_<name>`, carries a guest's
  `provision.secrets`. The controller sends these to `apply` only, so what `preview` saves
  cannot hold them. The root declares each as a variable with a null default, because it
  is unset while planning, and `ephemeral = true`, so the state does not hold it either.

### Built-in state

When no external backend is configured the input is `"backend": { "type": "octopushy" }`,
with no `config`, no `workspace`, and no backend credentials in `secretEnv`. The controller
keeps the state; see [how it works](../../../docs/opentofu-state.md).

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
is not logged or returned, because it can hold configuration values.

`variables.guest.inputs` holds the guest's `provision.inputs`, when it has any.

Optional `files` holds files to write into the root, by name, for what OpenTofu reads by path. A
name may not start with a dot or end in `.tf`, `.tofu`, or `.json`, so this cannot add code.

Optional `replace` lists resource addresses, each passed as `-replace=<address>`. It is for what
only the controller knows must happen again, so that it is in the preview and follows approval. An
address that is not in the state changes nothing.

When `hasChanges` is true, or optional `retainNoChangesPlan` is true (default
false), the result carries the artifact `plan.tar.gz`, holding
`plan.bin` and the root's `.terraform.lock.hcl` after init. A root without providers
has no lock file; then the archive holds only `plan.bin`.

The controller requests retention for provision runs that include privileged
guest filesystem work or disk reporter installation. This supplies an exact plan
digest for approval even when infrastructure itself has no changes; it does not
request `show` or `apply` for a no-change plan. Older workers may ignore the field,
but a missing required plan artifact blocks subsequent privileged work rather
than bypassing approval.

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

## `technitium`

Works on one application cluster through each member's own API at `https://<address>:53443`, as
`admin`, with the secret `technitium.password`. It never receives Proxmox tokens or a repository
bundle. The controller releases the password only for endpoints trusted for the cluster
([how](../../../docs/installation-trust.md#application-clusters)). Every action takes:

```json
{
  "cluster": "home/clusters/technitium",
  "clusterDomain": "cluster.home.arpa",
  "minimumVersion": "15.6.2",
  "formed": true,
  "members": [
    { "guest": "home/korriban/krb-dns-01", "role": "primary", "address": "10.1.0.32",
      "endpoint": "https://10.1.0.32:53443", "certificateSha256": "ab12…" }
  ],
  "settings": {
    "enableBlocking": true, "blockingType": "NxDomain", "blockListUrls": [],
    "blockListUpdateIntervalHours": 24, "recursion": "UseSpecifiedNetworkACL",
    "recursionNetworkACL": ["10.1.0.0/24"]
  },
  "blocked": ["bad.example.com"], "allowed": [], "listProbes": [], "forwardZones": ["home.arpa"],
  "control": null,
  "syncWaitSeconds": 90,
  "joinSettleSeconds": 120
}
```

The agent connects to a member only with its `certificateSha256`: the SHA-256 of the certificate,
in hexadecimal. It accepts that exact leaf, whatever its name or issuer, and the handshake must be
signed by it. A member with no `certificateSha256` fails the attempt before anything is sent, and
one presenting another certificate gets a failed handshake, so no request reaches it. There is no way
to accept any certificate, except in `certificates`, which sends no credentials.

`settings` is compared key by key with `/api/settings/get`, and lists as sets; a list a member never
held reads as `null` and equals an empty one. A member's optional `dnsPort` defaults to 53. Each
session is closed with `/api/user/logout`.

A member is not trusted with the agent's memory: an answer is read as it streams and given up at 8 MiB,
or 16 MiB and 200,000 entries for a list export, whatever its length claims.

### `certificates`

Read-only, with no secret, so it needs no trust. It connects to each member, accepting anything, and
reports what it presents. Only the bare request of an unauthenticated `GET /` is sent.

```json
{
  "certificates": [{ "guest": "home/korriban/krb-dns-01", "endpoint": "https://10.1.0.32:53443",
                     "sha256": "ab12…", "pem": "-----BEGIN CERTIFICATE-----\n…" }],
  "unreachable": [{ "guest": "home/moraband/mrb-dns-01", "error": "…" }]
}
```

### `inspect`

Read-only; runs before the plan. Fails when the primary cannot be reached or refuses the password,
saying that the secondaries keep answering, when the primary presents another certificate, and when
`formed` is true and the primary reports no cluster: forming again would split it.

```json
{
  "formed": true,
  "members": [{ "guest": "home/moraband/mrb-dns-01", "address": "10.1.0.42", "state": "joined",
                "peerId": 22, "peerName": "mrb-dns-01.cluster.home.arpa" }],
  "undeclared": { "blocked": ["hand.example.com"], "allowed": [] },
  "certificates": []
}
```

`members` holds the secondaries, each in one state: `joined`; `moved`, joined at another address than
the primary has for it; `absent`, not in a cluster and not listed, so it can be joined; `stale`, not
in a cluster but listed by the primary, which is a rebuilt member; `foreign`, in a cluster the primary
does not list it in; `unreachable`; `refused`, when it refuses the password; or `unverified`, when it
presents another certificate, which `certificates` then reports as in the action above.

The controller keeps `peerId` and `peerName` on the member's binding. `undeclared` is what the
primary's blocked and allowed lists hold beyond `blocked` and `allowed`: the provider reads only the
entries it knows, so a plan cannot find these. The controller turns `absent` into
`replace: ["terraform_data.secondary[\"<guest>\"]"]` and anything in `undeclared` into
`replace: ["terraform_data.undeclared_entries"]` on the `preview` that follows.

### `detach`

**Mutating and not cancellable.** Runs before the apply, so that a failure leaves the delete in the
next plan. Takes `remove`, the members the approved plan deletes, as they were bound:

```json
{ "remove": [{ "guest": "home/umbrel/umb-dns-01", "address": "10.1.0.52", "peerId": 22,
               "peerName": "umb-dns-01.cluster.home.arpa" }] }
```

A peer is matched by `peerId` together with its `peerName` or its `address`, or by both `peerName`
and `address`, and only among secondaries. An id alone is not enough, since another peer may hold it
by now. A removal that identifies no peer is refused and removes nothing: an address does not say who
is there now. Being
undeclared says nothing about whose a peer is, and no other peer is touched. A match that holds a
declared member's address is refused, with the same way on as for an address alone: compare the
primary's peers by hand, delete the stale one there, and acknowledge it on the cluster's page. It goes with `removeSecondary`, or `deleteSecondary` when that
fails, as it does for a peer that cannot answer, and what the primary lists afterwards decides. Output,
also on failure: `{ "detached": [...], "unconfirmed": [...] }`. `detached` was told to leave and is
gone. `unconfirmed` is not listed by the primary, and nothing saw it leave: its record was deleted, or
was already gone. Only `detached` ends a guest's binding.

### `reconcile`

**Mutating and not cancellable.** Runs after the apply. Takes `join`, guest addresses from the approved
plan's changes to `terraform_data.secondary`, and `pruneBlocked` and `pruneAllowed`, what inspection
named as undeclared when the plan replaced `terraform_data.undeclared_entries`, and otherwise empty.

- `join`: an `absent` member joins with `initJoin` on the member itself, with the declared
  `<primary guest id>.<clusterDomain>` in `primaryNodeUrl` and the declared address in
  `primaryNodeIpAddress`. An IP-literal URL is refused upstream, and no value a node reports is used.
  `ignoreCertificateErrors` is always true, because Technitium cannot pin there, so this leg carries
  no admin password: the agent creates the administrator `octopushy-cluster-join` on the primary with
  a random password for that join, and deletes it afterwards, also when the join was refused, its
  outcome was lost, or preparing it failed part way. An agent that dies leaves it, and so does a delete
  that failed, which the step reports: the next join replaces it, and `verify` names it until then. A `moved` member calls
  `updateIpAddress`. `joined` is a no-op. A `stale`, `foreign`, or unreachable one fails with its
  recovery and does not stop the others.
- `pruneBlocked`, `pruneAllowed`: deletes exactly these on the primary. Entries that appeared since
  inspection stay, for verification to report and the next plan to show.

Output: `{ "joined": [...], "members": [{ "guest": ..., "peerId": ..., "peerName": ... }] }`. Only an
explicit refusal is a known failure of a join. An answer that was lost, cut off, or cannot be read says
nothing about whether it happened, so both nodes are asked for up to `joinSettleSeconds`: it succeeded
if the member reports a cluster and the primary lists it, and is otherwise `uncertain`. The password
is a form field of `initJoin`, never in a URL.

An answered join is checked too, up to `joinSettleSeconds` and at most 20 seconds: the primary, over
the pinned connection, has to list the member, and the member has to report the declared cluster domain
and a primary at the declared primary's address. Otherwise the join is a known failure, naming the member
and saying that whatever answered for the primary's address may not have been the primary. The leg
itself is not pinned, so a relay cannot be told from the primary.

A member whose outcome is unknown ends the joining: members after it are not attempted, because the
cluster's state is not known. The members joined before it, and the approved prunes, are done, and the
attempt then ends `uncertain` with the same output
(`joined`, `members`) as any other end, so that the controller keeps what it learned.

### `refresh-block-lists`

**Mutating.** Calls `forceUpdateBlockLists` on the primary, which covers every member. It takes no
URL, and it needs no approved plan: the controller runs it when the declared lists are not known to
be downloaded, so a download that failed is retried by a run with no changes. Output: `{ "refreshed": true }`.

### `verify`

Read-only. Runs on every cluster run, including with no changes, and for a cluster's background
observation. It connects to every member and checks, on each:

| Check | Passes when |
| --- | --- |
| `reachable`, `login` | The API answers, and takes the password |
| `certificate` | It presents the trusted certificate. If not, it is sent nothing |
| `cluster` | `clusterInitialized` is true, in `clusterDomain` |
| `version` | At least `minimumVersion` |
| `membership` | Its `clusterNodes` are the declared members, in their roles |
| `peers` | Each is `Connected`. A peer that is down or rebuilt is reported as itself, not here |
| `settings` | `settings` matches |
| `blocked`, `allowed` | Its lists are exactly the declared ones |
| `zones` | It serves `forwardZones` |
| `lists` | Lists are declared with `listProbes`, when blocking is enabled |
| `blocking` | Up to 5 `blocked` names and 5 `listProbes` answer with extended DNS error 15, when blocking is enabled |
| `control` | `control`, or the primary's name in the cluster zone, resolves |

`blocking` and `control` are raw UDP DNS to that member. Extended DNS error 15, "Blocked", is what
proves a block: Technitium sends it whatever the blocking type and whether or not the name exists,
while NXDOMAIN alone also answers a name nobody registered. Each member reports `blockingVerified`,
true only if such a question was asked and answered that way, so a cluster with nothing to ask does
not claim anything.

What can be sync lag is checked again every `poll_interval` for `syncWaitSeconds`. What cannot,
such as a member that reports no cluster, fails at once.

```json
{
  "ok": false,
  "certificates": [],
  "members": [
    { "guest": "home/korriban/krb-dns-01", "address": "10.1.0.32", "role": "primary", "ok": true,
      "version": "15.6.2", "blockingVerified": true,
      "checks": [{ "name": "cluster", "ok": true, "detail": "in cluster.home.arpa" }] },
    { "guest": "home/moraband/mrb-dns-01", "address": "10.1.0.42", "role": "secondary", "ok": false,
      "version": "15.6.2", "blockingVerified": false,
      "checks": [{ "name": "cluster", "ok": false, "detail": "it reports clusterInitialized=false, ..." }],
      "recovery": "the primary still lists it as Unreachable with id 22. Sign in to ..." }
  ]
}
```

A failure carries these outputs, and its message names each failing member with its recovery. It
never calls `deleteSecondary`: that decision is the operator's.

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
