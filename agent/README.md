# Octopushy agent

For the separate one-shot guest disk reporter and its configurable systemd timer,
see [disk reporter installation](packaging/disk-reporter/README.md).

The Rust side of Octopushy: the `octopushy` binary (agent mode; a supervisor mode
comes later), Octopushy's own Proxmox API client, and a fake Proxmox API for tests.
The protocol is [`contracts/agent/v1/agent.proto`](../contracts/agent/v1/agent.proto)
and the executor inputs and outputs are in
[`contracts/agent/v1/executors.md`](../contracts/agent/v1/executors.md).

```text
agent/
├── crates/proto/         octopushy-proto: tonic/prost code generated from the contract
├── crates/proxmox/       octopushy-proxmox: typed async Proxmox VE API client
├── crates/octopushy/     octopushy: the agent library and binary
├── crates/mock-proxmox/  mock-proxmox: fake Proxmox API (library + binary)
└── packaging/            example hardened systemd unit
```

Agent modules (`crates/octopushy/src`): `cli`, `enrollment`, `renewal`,
`identity` (stored credentials), `tls`, `connection` (the gRPC channel), `outbox`,
`attempts` (manager, logs, workspaces, restart recovery, concurrency limiter),
`executors/{proxmox,opentofu}`, `tools` (podman, docker, local), `masking`, `paths`.

## Build

```sh
cargo build --release          # target/release/octopushy and target/release/mock-proxmox
cargo test --workspace
```

No `protoc` is needed: the proto is compiled with `protox` (pure Rust). TLS is
rustls with the `ring` provider everywhere; nothing links OpenSSL. The release
profile uses LTO and strips symbols. The binary links glibc dynamically (a static
musl build needs the musl target, which this host lacks).

## Enroll

The UI creates a one-time token `opy1.<secret>.<fingerprint>`. On the agent host:

```sh
OCTOPUSHY_ENROLLMENT_TOKEN='opy1.…' octopushy agent enroll \
    --server https://octopushy.lan:8443 [--name krb-agent] [--state-dir DIR] [--force]
```

1. Fetches `GET <server>/agent/ca.pem` without verifying TLS (this one request only)
   and refuses it unless the SHA-256 of its DER matches the token's fingerprint.
2. Generates an ECDSA P-256 key and a PKCS#10 request (CN = the name; the server
   sets CN = agent ID).
3. Calls `Enrollment.Enroll` over TLS that trusts only that CA, checks that the
   returned certificate is for its key, and stores everything in the state directory.

`--token` also works, but command-line arguments are visible to other local users.
An existing enrollment is only replaced with `--force`.

## Run

```sh
octopushy agent run [--state-dir DIR] [--tool-runtime podman|docker|local]
                    [--tofu-binary PATH] [--container-binary PATH]
                    [--container-network NAME] [--work-dir DIR]
octopushy agent status [--state-dir DIR]
octopushy version
```

| Flag | Default | Meaning |
|---|---|---|
| `--state-dir` | see below | enrollment, outbox, workspaces, provider cache |
| `--tool-runtime` | `podman` | how OpenTofu runs |
| `--tofu-binary` | `tofu` | the binary for `--tool-runtime local` |
| `--container-binary` | `podman`/`docker` on PATH | the container CLI |
| `--container-network` | runtime default | `--network` for tool containers |
| `--work-dir` | `<state>/work` | attempt workspaces |

Logs go to stderr; set the level with `OCTOPUSHY_LOG` (or `RUST_LOG`), such as
`OCTOPUSHY_LOG=octopushy=debug`. The first SIGTERM/SIGINT drains: cancellable
attempts stop, running applies finish, results are flushed; a second signal exits
at once.

At startup the enrolled state directory, work directory and provider cache are
resolved to canonical absolute paths, including relative paths and symlinks.
Container mounts and OpenTofu's repository root therefore use the same paths.
The runtime is probed once: if that probe fails, the agent warns and omits
`opentofu` from `Hello`, while native `proxmox` and `nixos-rootfs` remain advertised.
Fix the tool/runtime and restart the agent to advertise OpenTofu again. A successful
version probe does not prove image pulls, provider access or backend connectivity.

What the agent does on the channel:

- Connects with mutual TLS (its key and certificate; the stored CA as the only root),
  sends `Hello` (version, available executors, attempts still running,
  tool runtime), and waits for `Welcome` (`max_concurrency`, `heartbeat_seconds`).
- Heartbeats every `heartbeat_seconds` with the running attempt IDs.
- Runs assignments concurrently up to `max_concurrency` (extra ones wait), sends
  `AttemptStarted` (again on each new connection while the attempt runs), log
  batches every 500 ms or 100 lines, and one `AttemptResult` per attempt.
- Reconnects with exponential backoff (1 s doubling to 60 s, jittered). Attempts keep
  running across reconnects; retained log lines (up to 20 000 lines / 8 MiB per
  attempt) and unacknowledged results are resent, and the server drops lines it has
  by sequence number.
- Renews its certificate when less than a third of its validity remains
  (`RenewCertificate` over the current connection), replaces key and certificate as
  a pair, and reconnects with the new identity.
- `CancelAttempt` stops read-only attempts (`OUTCOME_CANCELLED`) but never an
  `opentofu/apply`, `proxmox/set-protection`, or `guest/install-disk-reporter`;
  the refusal is logged in the attempt.
- gRPC messages may be up to 64 MiB each way (repository files, saved plans).

## State directory

Default: `$STATE_DIRECTORY` (systemd `StateDirectory=`), else
`/var/lib/octopushy-agent` as root, else `$XDG_STATE_HOME/octopushy-agent` or
`~/.local/state/octopushy-agent`.

```text
agent.json             agent ID, server URL, name (written last at enrollment)
key.pem                private key, mode 0600
cert.pem, ca.pem       agent certificate and the pinned agent CA
key.pem.next, cert.pem.next   only during a renewal; finished on the next start
outbox/<attempt>.json  results until acknowledged (artifacts inline, base64)
work/<attempt>/        attempt workspace:
  attempt.json           marker: executor, action, phase queued|running
  repo/                  extracted repository files
  out/plan.bin           saved plan
  home/                  HOME for OpenTofu (its .terraform.d lands here)
cache/providers/       OpenTofu provider cache (TF_PLUGIN_CACHE_DIR), shared
```

**Outbox and restart recovery.** A result is written to the outbox (temp file,
fsync, rename, fsync of the directory) before it is sent and deleted only on
`ResultAck`; the workspace is removed then too. On start, a workspace whose marker
says `running` but has no result means the agent died mid-attempt: it reports
`OUTCOME_UNCERTAIN` for `opentofu/apply`, `proxmox/set-protection`, and
`guest/install-disk-reporter`, and
`OUTCOME_FAILED` ("agent restarted during the attempt") otherwise. A `queued`
marker fails with "restarted before the attempt started".

## Executors

**`proxmox`** (`observe`, `verify-guest`, `verify-absent`, `set-protection`) uses
`octopushy-proxmox` with the secret `proxmox.token`. `caCertificatePem`, when set, is
the only trust root; otherwise `verifyTls` chooses the system trust store or no
verification. Before concluding that a VM is absent, the token's `VM.Audit` on
`/vms/<vmid>` is checked, because Proxmox silently hides VMs a token cannot audit.
`verify-guest` polls every 5 s until all checks pass or `waitSeconds` ends.
`set-protection` refuses a VM found on another node than `node`.

**`guest`** (`install-disk-reporter`) installs the one-shot reporter through the
existing Proxmox connection and QEMU guest agent after verification. It validates
the VM identity and guest-exec privilege, downloads a checksum-pinned HTTPS binary,
installs the systemd timer, and starts the first report. Credentials travel over
QEMU stdin, never command-line arguments or OpenTofu state. Guest output is not
forwarded because it could expose credentials. The remote command is bounded to
240 seconds (plus a 5-second forced-kill grace period); a lost dispatch response
or execution status is **uncertain**, not automatically retried. See the
[installation guide](packaging/disk-reporter/README.md).

**`opentofu`** (`preview`, `apply`):

- Extracts `files_tar_gz` into `repo/`, refusing absolute paths, `..`, hard links,
  devices and FIFOs, symlinks that leave the tree (checked lexically and by
  resolving them afterwards), writing through symlinks, and more than 256 MiB.
- Writes `octopushy.auto.tfvars.json` and `octopushy_backend.tf.json` into the root
  (a configured `local` backend is refused: its state would vanish with the workspace).
  With the built-in state, the generated backend is a `local` file the agent restores
  from the assignment and reports back; see
  [executors](../contracts/agent/v1/executors.md#built-in-state).
- Runs `version -json`, `init -input=false -no-color -backend-config=k=v…`,
  `workspace select -or-create=true` (when a workspace is set), then
  `plan -detailed-exitcode -out=…/out/plan.bin [-destroy]` and `show -json` (parsed,
  never logged). With changes, the result carries `plan.tar.gz` (`plan.bin` and the
  root's `.terraform.lock.hcl`, packed deterministically).
- `apply` checks the artifact's SHA-256, restores the lock file, runs
  `init -lockfile=readonly`, `apply -input=false <plan.bin>`, and `output -json`
  (sensitive values withheld, names listed in `sensitiveOutputs`).
- Tool output streams as stdout/stderr log lines; agent lines (`$ tofu init …`) use
  `LOG_STREAM_AGENT`.

## Tool runtimes

On a separately hosted worker, use routable controller, Proxmox and OpenTofu
backend addresses. Loopback refers to the worker (or the tool container), not the
controller. The controller sends source files/artifacts with assignments, so no
shared checkout is required. See the opt-in
[remote-worker acceptance prerequisites](../tests/remote-worker/README.md).

- **podman** (default, rootless): `podman run --rm` with the attempt workspace at
  `/work` (`:Z`, private SELinux label) and the provider cache at
  `/octopushy/plugin-cache` (`:z`, shared label, since attempts share it),
  `--userns=keep-id` (files stay owned by the agent user), `--read-only` with a
  tmpfs `/tmp`, `--cap-drop=all`, `--security-opt=no-new-privileges`,
  `--log-driver=none` (tool output can hold secrets; Fedora's default log driver is
  journald), `--pull=missing`, `-w` = the root. No runtime socket, never
  `--privileged`. The image's entrypoint is already `tofu`.
- **docker**: the same flags without keep-id; on a rootful daemon it adds
  `--user <uid>:<gid>` and warns, because access to a rootful daemon equals root.
- **local**: runs `--tofu-binary` directly with a cleared environment (only `PATH`
  is kept), for development and tests.

Environment: `TF_IN_AUTOMATION=1`, `TF_INPUT=0`, `TF_PLUGIN_CACHE_DIR`, and `HOME`
(inside the workspace) are passed as `-e NAME=VALUE`; the input's `env` and
`secretEnv` are passed as `-e NAME` only, with values set in the container CLI's own
environment, so they never appear in `/proc/<pid>/cmdline`. Names the agent or the
container runtime use (`HOME`, `PATH`, `TF_CLI_ARGS*`, `CONTAINERS_*`, …) are refused.

Cancel sends SIGTERM to the tool's process group (podman forwards it to OpenTofu),
then after 10 s kills the container (`podman kill`) and the group.

## Security notes

- Trust: only the CA pinned by the token's fingerprint; mutual TLS for the channel.
  The private key is mode 0600 and never leaves the host; renewal makes a new key.
- Secrets: every log line and error is masked (`***`, longest value first, also the
  secret part of a Proxmox token and lines of multi-line values). Secrets never go on
  a command line, into tracing logs, or into `Debug` output (the Proxmox token type
  redacts itself). The full assignment is never logged.
- Tools run in rootless containers with only the attempt workspace and the provider
  cache mounted; the agent's identity and outbox are not mounted.
- The example unit [`packaging/octopushy-agent.service`](packaging/octopushy-agent.service)
  is as strict as rootless podman allows and documents each relaxation
  (`NoNewPrivileges=no` for newuidmap, namespaces, `/dev/fuse`, `/dev/net/tun`,
  cgroup delegation) and how to tighten it with `--tool-runtime local`.

## mock-proxmox

```sh
cargo run -p mock-proxmox -- --listen 127.0.0.1:18006 \
    --fixture crates/mock-proxmox/fixtures/home.json --tls-self-signed --ca-out /tmp/pve-ca.pem
curl --cacert /tmp/pve-ca.pem \
    -H 'Authorization: PVEAPIToken=octopushy@pve!agent=00000000-0000-4000-8000-000000000000' \
    https://127.0.0.1:18006/api2/json/cluster/resources?type=vm
```

The fixture sets the only accepted token, the version, an optional cluster, nodes
(online or offline), VMs (`vmid`, `name`, `node`, `status`, `template`, `tags`,
`protection`, `ipv4` or null when the guest agent is not running), and optionally the
token's privileges. The example has a cluster `home` with hosts `korriban` and
`moraband`, a template (9000), an unmanaged VM (150), and a VM whose guest agent is
down (211). Responses imitate Proxmox: the `data` envelope, `0`/`1` booleans,
`;`-separated tags, messages in the HTTP status line (`500 QEMU guest agent is not
running`). `PUT …/config` updates protection in memory. Admin routes without auth:
`GET /mock/vms`, `POST /mock/vms` (add or replace a VM, fixture JSON),
`DELETE /mock/vms/{vmid}`. With `--tls-self-signed` it generates a CA and a server
certificate for 127.0.0.1, ::1, and localhost; `--ca-out` writes the CA for
`caCertificatePem`.

## Tests

`cargo test --workspace` needs no network beyond loopback:

- `crates/proxmox/tests`: the client against mock-proxmox over HTTPS with its CA
  (TLS modes, auth, errors, protection, guest agent) and against wiremock (exact auth
  header, form encoding, lenient decoding, error mapping).
- `crates/octopushy` unit tests: masking, archive safety (traversal, absolute paths,
  symlink escapes and chains, hard links, devices, limits, PAX), outbox, identity
  switch recovery, restart classification, the attempt manager (concurrency, cancel,
  refusal for mutations, resend, ack cleanup, drain), log buffers, paths, backoff.
- `tests/grpc_e2e.rs`: a fake server (tonic, real mutual TLS, test CA from rcgen,
  `GET /agent/ca.pem` on the same port): enrollment → connect → Hello → proxmox
  observe against mock-proxmox → logs → result → ack → outbox empty; wrong
  fingerprint and token; renewal and reconnect with the new certificate; restart
  recovery and outbox resend; attempts surviving a server-side disconnect;
  cancellation; unknown executors.
- `tests/opentofu_fake.rs`: the opentofu executor with the local runtime and a fake
  `tofu` script that records arguments and environment (secrets in the environment,
  never in arguments; outputs; artifact contents; destroy; replace counting; lock-file
  restore; corrupt artifacts; cancel; the built-in state).
- `tests/proxmox_executor.rs`: all four proxmox actions against mock-proxmox.
- `tests/podman_smoke.rs` (ignored by default): real OpenTofu in rootless podman,
  `tofu version` through the container runner and a full preview + apply of a
  `terraform_data` root against an in-test `http` state backend:

  ```sh
  cargo test -p octopushy --test podman_smoke -- --ignored --nocapture
  ```
