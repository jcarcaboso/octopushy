# Octopushy agent

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
`executors/{proxmox,opentofu,ansible,nix}`, `images` (built templates' images, in
chunks), `tools` (podman, docker, local), `masking`, `paths`.

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
                    [--tofu-binary PATH] [--ansible-binary PATH] [--container-binary PATH]
                    [--container-network NAME] [--container-pids-limit N]
                    [--container-memory SIZE] [--require-image-digest] [--work-dir DIR]
octopushy agent doctor [the flags of run]
octopushy agent status [--state-dir DIR]
octopushy version
```

| Flag | Default | Meaning |
|---|---|---|
| `--state-dir` | see below | enrollment, outbox, workspaces, provider cache (as root with `local`: `root 0711`, its contents private) |
| `--tool-runtime` | `podman` | how OpenTofu and Ansible run |
| `--tofu-binary` | `tofu` | the binary for `--tool-runtime local` |
| `--ansible-binary` | `octopushy-ansible` | the program for `--tool-runtime local` |
| `--container-binary` | `podman`/`docker` on PATH | the container CLI |
| `--container-network` | runtime default | `--network` for tool containers |
| `--container-pids-limit` (`OCTOPUSHY_CONTAINER_PIDS_LIMIT`) | `4096` | `--pids-limit` for tool containers; `0` keeps the runtime's default |
| `--container-memory` (`OCTOPUSHY_CONTAINER_MEMORY`) | no limit | `--memory` (and `--memory-swap`) for tool containers, such as `4g`; see below |
| `--require-image-digest` (`OCTOPUSHY_REQUIRE_IMAGE_DIGEST`) | off | refuse a tool image not named `image:tag@sha256:<digest>`; see below |
| `--work-dir` | `<state>/work` | attempt workspaces |

Logs go to stderr; set the level with `OCTOPUSHY_LOG` (or `RUST_LOG`), such as
`OCTOPUSHY_LOG=octopushy=debug`. The first SIGTERM/SIGINT drains: cancellable
attempts stop, running applies finish, results are flushed; a second signal exits
at once.

At startup the enrolled state directory, work directory and provider cache are
resolved to canonical absolute paths, including relative paths and symlinks.
Container mounts and OpenTofu's repository root therefore use the same paths.
Executors are probed at start and again every 5 minutes, one probe at a time, each a
`--version` that gives up after 20 seconds: the container CLI (or `tofu version` with
`local`) for `opentofu`, `octopushy-ansible --version` with `local` for `ansible`, and
`nix --version` with the store's free space for `nix`. A probe never pulls an image,
starts a container or takes a lock, so it runs next to attempts. An executor whose probe
fails is left out of `Hello` with its reason in `Hello.unavailable`, which the Agents page
and a run waiting for that executor show. When a later probe finds a change, the agent
sends `ExecutorsChanged` on the open connection and the server offers work by the new set;
an attempt already running is not touched. `proxmox` needs no tool and is always offered.
A successful version probe does not prove image pulls, provider access or backend
connectivity. `nix` is offered only with the `local` runtime, when `nix --version` works and the
file system holding `/nix` has `OCTOPUSHY_NIX_MIN_FREE_GB` (20) free.
After a build the store is collected down to `OCTOPUSHY_NIX_STORE_MAX_GB` (40). The agent
image carries a single-user Nix 2.34 owned by `octopushy-agent` (10001), without a sandbox;
the agent, as root, runs builds and the `nix` probe as that user, never as root. Other probes
run as `nobody`.

`octopushy agent doctor` takes the flags of `run`, makes the same probes and prints one
line per executor, available or why not, then free disk and cache sizes, the Nix store's
included (a walk of at most 500 000 entries). It reads no enrollment, contacts no server and
changes nothing, so it runs next to a running agent, as the same user so it sees the same
programs. It exits 1 when an executor these flags mean to offer is unavailable; `nix` with
`podman` or `docker` is "not offered" and does not count.

```text
$ octopushy agent doctor --tool-runtime local --ansible-binary /opt/octopushy-ansible
tool runtime: local
opentofu   available (OpenTofu v1.10.6)
proxmox    available (needs no tool)
ansible    UNAVAILABLE: cannot run /opt/octopushy-ansible: No such file or directory (os error 2)
nix        UNAVAILABLE: the Nix store has 12.3 GiB free, less than the 20.0 GiB a build needs
free disk  state      41.2 GiB of 98.3 GiB
cache      providers  312.5 MiB
```

What the agent does on the channel:

- Connects with mutual TLS (its key and certificate; the stored CA as the only root),
  sends `Hello` (version, available executors, attempts still running,
  tool runtime), and waits for `Welcome` (`max_concurrency`, `heartbeat_seconds`).
- Heartbeats every `heartbeat_seconds` with the running attempt IDs. Every sixth carries a
  `StorageReport`: free space of the state, work and Nix store file systems (`statvfs`,
  each file system once) and the provider cache's and Nix cache's size, measured every 5
  minutes by a walk of at most 20 000 entries (a lower bound when it stops early).
- Runs assignments concurrently up to `max_concurrency` (extra ones wait), sends
  `AttemptStarted` (again on each new connection while the attempt runs), log
  batches every 500 ms or 100 lines, and one `AttemptResult` per attempt.
- Answers `ReachabilityProbe`s (capability `reachability-v1`): a TLS handshake or an SSH
  identification line, with no credential, from the agent process or from where tools
  connect (directly for `local`; a relay container of the step's image, with the tool
  containers' flags and network and nothing mounted, for `podman` and `docker`). Up to 4
  at once, each bounded by its timeout and stopped when the connection ends. A proxy in
  the relevant environment, or an image without `python3`/`nc`, is reported
  `unsupported`. See [worker routing](../docs/worker-routing.md).
- Reconnects with exponential backoff (1 s doubling to 60 s, jittered). Attempts keep
  running across reconnects; retained log lines (up to 20 000 lines / 8 MiB per
  attempt) and unacknowledged results are resent, and the server drops lines it has
  by sequence number.
- Renews its certificate when less than a third of its validity remains
  (`RenewCertificate` over the current connection), replaces key and certificate as
  a pair, and reconnects with the new identity.
- Normal `CancelAttempt` stops read-only attempts (`OUTCOME_CANCELLED`) but never an
  `opentofu/apply`, `proxmox/set-protection`, or `ansible/run`;
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
  plugin-cache/          the provider cache, with a user per attempt
cache/providers/       OpenTofu provider cache (TF_PLUGIN_CACHE_DIR), shared, unless
                       each attempt has a user of its own
cache/nix/             Nix's caches, the store owner's
```

**Outbox and restart recovery.** A result is written to the outbox (temp file,
fsync, rename, fsync of the directory) before it is sent and deleted only on
`ResultAck`; the workspace is removed then too. On start, a workspace whose marker
says `running` but has no result means the agent died mid-attempt: it reports
`OUTCOME_UNCERTAIN` for `opentofu/apply`, `proxmox/set-protection`, and
`ansible/run`, and
`OUTCOME_FAILED` ("agent restarted during the attempt") otherwise. A `queued`
marker fails with "restarted before the attempt started".

**Leftover containers.** Every container the agent starts carries the label
`io.octopushy.agent=<agent ID>` (and `io.octopushy.attempt=<attempt ID>` or
`io.octopushy.probe=true`). A container is started with `--rm`, but one whose agent was
killed, or whose runtime crashed, stays, and its config on disk holds its environment,
secrets included. With `podman` or `docker`, the agent removes every container carrying
its own agent ID at startup, before recovery, logging each with its attempt. One still
running a change is stopped by that removal; its attempt is reported uncertain, as above,
and never retried. Another agent's containers on the same runtime carry another ID and
are left alone. Containers started by an agent older than this label are not removed.

## Executors

**`proxmox`** (`observe`, `verify-guest`, `verify-absent`, `set-protection`) uses
`octopushy-proxmox` with the secret `proxmox.token`. `caCertificatePem`, when set, is
the only trust root; otherwise `verifyTls` chooses the system trust store or no
verification. Before concluding that a VM is absent, the token's `VM.Audit` on
`/vms/<vmid>` is checked, because Proxmox silently hides VMs a token cannot audit.
`verify-guest` polls every 5 s until all checks pass or `waitSeconds` ends.
`set-protection` refuses a VM found on another node than `node`.

**`opentofu`** (`preview`, `apply`):

- Extracts `files_tar_gz` into `repo/`, refusing absolute paths, `..`, hard links,
  devices and FIFOs, symlinks that leave the tree (checked lexically and by
  resolving them afterwards), writing through symlinks, and more than 256 MiB.
- Writes `octopushy.auto.tfvars.json` and `octopushy_backend.tf.json` into the root.
  The generated backend is the built-in state: a `local` file the agent restores from
  the assignment and reports back; see
  [executors](../contracts/agent/v1/executors.md#built-in-state).
- Runs `version -json`, `init -input=false -no-color`, then
  `plan -detailed-exitcode -out=…/out/plan.bin [-destroy]` and `show -json` (parsed,
  never logged). Each change reports its address, type, name, actions and OpenTofu's
  `action_reason`, plus, for a `proxmox_virtual_environment_vm`, the `vm_id` and
  `node_name` its state holds; no other plan value leaves the agent. With changes,
  the result carries `plan.tar.gz` (`plan.bin` and the root's `.terraform.lock.hcl`,
  packed deterministically).
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
  is kept). The shipped agent container, Compose stack and Proxmox installer use
  this mode. How tools are kept apart depends on who the agent runs as, and it logs
  which at start (`tool runtime local, isolated` or `shared`):
  - **As root** (the agent image's default), each attempt's tools run as an
    unprivileged user of its own, `octopushy-tool-<n>` from
    [`packaging/tool-users.sh`](packaging/tool-users.sh), with no other groups, no
    capabilities and `no_new_privs`. The user owns its workspace's contents and nothing
    else; the agent's state is root's and private. Nix builds run as the store's owner,
    one at a time. Attempts run in parallel, up to the server's `max_concurrency` and the
    number of users. Tool users cannot write to `/tmp`, `/var/tmp`, `/run/lock` or
    `/dev/mqueue`, which the agent locks at start; temporary files go in the workspace.
    When an attempt ends every process of its user is killed and what it left in
    `/dev/shm` is removed before another attempt gets it; a user that cannot be cleaned
    up is quarantined. A process of the user that outlives a tool run stops the attempt,
    uncertain if it was a change. As root without those users the agent refuses to start. See
    [the safety boundary](../docs/safety-boundary.md#tools-apart-from-the-agent-and-from-each-other).
  - **As any other user** (`just agent`, or the image with `--user`), tools run as the
    agent's own user, which can read the agent's files, one attempt at a time whatever
    the server allows. This is not a sandbox; use it for development and trusted code.
    An image whose state directory a root agent has taken over needs a `chown` before
    it runs as another user again.

Tool-written plans, lock files, prepared variables and state are read with byte
caps, without following file or directory links, and must be regular files inside
the attempt workspace.

Environment: `TF_IN_AUTOMATION=1`, `TF_INPUT=0`, `TF_PLUGIN_CACHE_DIR`, and `HOME`
(inside the workspace) are passed as `-e NAME=VALUE`. The input's `env` and `secretEnv`
go in an env file the container CLI reads as `--env-file /dev/fd/N`: an in-memory file
(`memfd`) only that process holds, so neither names nor values appear in its command
line, its environment or any file system. A value on more than one line (a private key),
which an env file cannot hold, is passed as `-e NAME` with the value in the CLI's own
environment, which `/proc/<pid>/environ` shows only to the agent's user. Either way the
runtime keeps the container's environment in its config on disk while the container
exists, which is why leftovers are removed at startup. `env` and `secretEnv` names must
start with `PROXMOX_VE_` or `TF_VAR_`, the only prefixes the controller sends; any other
name is refused. With `local`, the same names and values are set on the tool's process.

Limits: every container gets `--pids-limit=4096` (`--container-pids-limit`). There is no
memory limit unless `--container-memory` sets one. A tool that reaches it is killed, and
if that tool was running `tofu apply` or a playbook, the change is left half made and the
attempt uncertain. Set it only with room to spare for your largest apply.

Image digests: with `--require-image-digest` the agent refuses, before pulling or running
it, a tool image (and a reachability probe from it) not named by digest, such as
`ghcr.io/opentofu/opentofu:1.13.1@sha256:<64 hex>`. A tag can be moved to another image
by whoever controls the registry; a digest cannot. It is recommended for anyone who pins
their images: set `Octopushy:Tools:OpenTofuImage` and `Octopushy:Tools:AnsibleImage` on
the server, and any `image` a workflow stage names, to `name:tag@sha256:...`. It is off
by default because the server's default Ansible image is named by a tag made from the
content of `agent/ansible` and published by the release workflow, so its digest is not
known when the source is written, and the agent would refuse the server's default.

OpenTofu has a 60-minute attempt timeout by default, configurable with
`timeoutMinutes` from 1 to 1440. Stopping OpenTofu sends SIGINT to allow it to save
state, then SIGTERM after 10 seconds, then a hard kill after another 10 seconds.
A timed-out preview fails; a timed-out apply is uncertain and reports saved state.
Other tools receive SIGTERM, then a hard kill after 10 seconds. Normal server
cancellation still never interrupts a running change. An explicit operator Force
stop is the exception: agents advertising `forced-mutation-stop-v1` stop the
running tool and return uncertain, with complete saved state for OpenTofu. Ansible
uses SIGTERM and then a hard kill. Older agents keep the tool running.

Ansible runs the same way, without the provider cache, through `octopushy-ansible`
from [`ansible/`](ansible/): its secrets arrive on stdin and stay in memory, so with
the `local` runtime `/tmp` has to be a tmpfs, or it refuses to run. The agent
announces `ansible` only if the program works. See
[configuring a guest](../docs/guest-configuration.md).

## Security notes

- Trust: only the CA pinned by the token's fingerprint; mutual TLS for the channel.
  The private key is mode 0600 and never leaves the host; renewal makes a new key.
- Secrets: every log line and error is masked (`***`, longest value first, also the
  secret part of a Proxmox token and lines of multi-line values). Secrets never go on
  a command line, into tracing logs, or into `Debug` output (the Proxmox token type
  redacts itself). The full assignment is never logged.
- Tools run in rootless containers with only the attempt workspace and the provider
  cache mounted; the agent's identity and outbox are not mounted. With `local` and the
  agent as root, they run as a user of their own per attempt, as above.
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
- `tests/tool_users.rs` (ignored unless root): tools as users of their own. A tool tries to
  read the agent's key, outbox and work directory, a neighbouring attempt's workspace and the
  agent's `/proc`, and to write its own marker and change record, and fails at each; two
  attempts run side by side; nothing of a user's outlives its attempt; a stopped tool takes
  every process of its user with it; probes never run as root; a Nix build cannot get the
  agent to upload its key. As root in a container, with the image's tool users:

  ```sh
  tests/tool-users/run.sh              # AGENT_IMAGE=<image> also checks a built image
  ```
- `tests/opentofu_fake.rs`: the opentofu executor with the local runtime and a fake
  `tofu` script that records arguments and environment (secrets in the environment,
  never in arguments; outputs; artifact contents; destroy; replace counting; lock-file
  restore; corrupt artifacts; cancel; the built-in state; a template image fetched into
  the root and refused when its size or SHA-256 differs).
- `tests/nix_build.rs`: the nix executor with a fake `nix` over real mutual TLS: when
  Hello advertises it, the build command, the lab's lock, the image's 4 MiB chunks, its
  outputs, garbage collection, and a server that reports another image.
- `tests/proxmox_executor.rs`: all four proxmox actions against mock-proxmox.
- `tests/podman_smoke.rs` (ignored by default): real OpenTofu in rootless podman,
  `tofu version` through the container runner and a full preview + apply of a
  `terraform_data` root with the built-in state:

  ```sh
  cargo test -p octopushy --test podman_smoke -- --ignored --nocapture
  ```
