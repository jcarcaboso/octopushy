# Milestone 1: Proxmox and virtual machines

**Started October 20, 2026.** The user asked to start the backend, frontend, and agent
projects described in the [architecture agreement](architecture-agreement.md), and
to build Proxmox and virtual machine management first, before anything else. This
document fixes the scope and the contracts between the three projects for that
milestone. Where it makes a choice the [lab repository proposal](lab-repository.md)
leaves open, it says so; everything else follows that proposal.

## Scope

In:

- Reading the lab repository from Git: `octopushy.yaml`, `lab.yaml`,
  `installation.yaml`, `guest.yaml`, `templates/*/template.yaml`, and
  `recipes/*/recipe.yaml`, validated by one JSON Schema per kind plus reference
  checks.
- Observing each Proxmox installation through its API: hosts, their status, and
  every VM, matched against the declared guests (unmanaged VMs are shown, never
  touched).
- Provisioning a guest: an OpenTofu plan, approval when the guest's policy asks for
  it, apply of exactly that plan, then verification through the Proxmox API.
- Retiring a guest (`lifecycle: retired`): a destroy plan under the destructive
  setting, removing VM protection, apply, and verifying the VM is gone.
- Agents: enrollment with a one-time token, mutual TLS, assignments over one gRPC
  stream, leases, and an outbox for results.
- The variable store (built-in provider only), which holds the Proxmox tokens and
  state-backend credentials the definitions reference.
- A single local account with a password. Passkeys come later.

Out, for later milestones: guest setup (NixOS, Ansible), services, DNS, ports,
application clusters, maintenance windows, drafts and Git write-back, the Monaco
editor, SignalR (the frontend polls for now), the supervisor and self-updates, other
secret providers, and Proxmox containers.

## Code layout

```text
backend/     ASP.NET Core (.NET 10): API, agent channel, orchestrator, Git, store
frontend/    Vite + React + TypeScript, served by Caddy that forwards /api
agent/       Rust workspace: the octopushy binary and the octopushy-proxmox library
contracts/   agent/v1/agent.proto, schemas/*.schema.json, openapi/octopushy.json
examples/    an example lab repository, including the proxmox-vm recipe
deploy/      Compose file for the control host
old/         the archived Python PoC
```

## Definitions

The files follow the [lab repository proposal](lab-repository.md). This milestone
reads the parts below and accepts, without acting on, the sections that belong to
later milestones (`integrations`, `dns`, `setup`, `access` beyond SSH keys,
services, clusters, windows).

**State backends (choice made here).** A state backend names an OpenTofu backend
type and passes its settings through unchanged, so any backend OpenTofu supports
works. `{stack}` in a setting is replaced by the guest's stack name, its address
with dots (`home.korriban.krb-crt-01`). Credentials go in `env`, never in `config`,
because OpenTofu writes backend settings into saved plans; environment variables
are not saved.

```yaml
# octopushy.yaml
stateBackends:
  gitlab:
    type: http
    config:
      address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}
      lock_address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}/lock
      unlock_address: https://gitlab.home.arpa/api/v4/projects/12/terraform/state/{stack}/lock
      lock_method: POST
      unlock_method: DELETE
    env:
      TF_HTTP_USERNAME: octopushy
      TF_HTTP_PASSWORD: { store: opentofu/gitlab-token }
  postgres:
    type: pg
    config: { schema_name: octopushy_state }
    workspace: "{stack}"            # for backends with workspaces
    env:
      PG_CONN_STR: { store: opentofu/pg-conn }
```

**Recipes (choice made here).** The recipe contract is still open in general; this
milestone defines only provisioning recipes run by the `opentofu` executor:

```yaml
# recipes/proxmox-vm/recipe.yaml
schemaVersion: 1
operation: provision
executor: opentofu
root: ../../modules/opentofu/proxmox-vm   # an OpenTofu root module, relative to this file
inputs: inputs.schema.json                # optional: validates the guest's hardware and network
verify: proxmox                           # proxmox (default) | none
```

The root receives these variables in `octopushy.auto.tfvars.json`; it declares the
ones it uses:

| Variable | Contents |
|---|---|
| `octopushy` | `address`, `stack`, `lab`, `installation`, `revision` |
| `guest` | `id`, `vmid`, `host`, `template`, `profiles`, `hardware`, `network`, `sshAuthorizedKeys` |
| `host` | `id`, `datastores`, `bridges` |
| `installation` | `id`, `type`, `endpoint`, `verifyTls` |
| `template` | `id`, `os`, `source` (`url`, `checksum`, `checksumAlgorithm`, `fileName`), or null |

Proxmox credentials reach the provider (bpg/proxmox, as in `nodes`) as environment
variables, so they never enter a plan: `PROXMOX_VE_ENDPOINT`, `PROXMOX_VE_INSECURE`,
and `PROXMOX_VE_API_TOKEN` from the installation's token reference.

**Templates (choice made here).** A template's `source` is a downloadable image:
`url`, `checksum` (`sha256:<hex>` or `sha512:<hex>`), and optionally `fileName`.
Building images (`source.build`) is later work.

**SSH keys (choice made here).** `access.sshAuthorizedKeys` in `guest.yaml` lists
public keys for cloud-init. They are public, so they are written directly.

## Runs

A run belongs to one resource and one definition revision. Its steps are visible,
each with attempts; a retry is a new attempt.

| Run | Steps |
|---|---|
| Provision a guest | plan → approval → apply → verify |
| Retire a guest | plan (destroy) → approval → unprotect → apply → verify absent |
| Observe an installation | observe |

- **When runs start.** A new revision starts a provision run for each guest whose
  inputs changed: its definition, its host and installation settings, its template,
  its recipe, the files of the recipe's root, or its state backend. A retired guest
  gets a retire run. "Plan now" starts one by hand. A guest whose folder disappears
  without being retired is reported as missing; nothing runs.
- **A plan without changes** ends the run as succeeded and records the revision as
  checked.
- **Approval.** Automatic guests are approved by policy, and the record says so.
  Manual guests wait. A plan with deletes, outside a recreate, follows the
  destructive setting. Approval names the plan's SHA-256, and apply refuses any
  other plan.
- **Locks and limits.** One run per guest at a time; a newer revision cancels a run
  that has not started applying. `limits.concurrentOperations` on a host caps the
  attempts running against its guests. Each agent runs at most its server-side
  `maxConcurrency` attempts. Waiting for capacity, waiting for an agent, and being
  blocked (for example by a missing secret) are shown differently.
- **Identity.** A guest's address binds to its installation and VMID after its
  first successful apply. Changing the VMID of a bound guest, or declaring a VMID
  bound to another guest, is a validation error for that guest only.
- **Uncertain outcomes.** An attempt whose agent stops reporting is uncertain, not
  failed. Nothing is retried automatically; the user plans again or closes the run.
  A result that arrives later is still recorded.

## Agent protocol

[`contracts/agent/v1/agent.proto`](../contracts/agent/v1/agent.proto) is the
contract. In short:

- **Enrollment.** The UI creates a one-time token for chosen labels. The token
  carries the SHA-256 fingerprint of the server's agent CA. The agent fetches the
  CA certificate, checks the fingerprint, generates its own key pair, and sends a
  certificate request with the token. The server signs a certificate valid for 30
  days, which the agent renews over mutual TLS when a third of it remains. Labels
  are set on the server; the agent never claims them.
- **Channel.** The agent opens one bidirectional stream, says hello with its
  version, executors, and attempts still running, and heartbeats every 10 seconds.
  The server sends assignments with the inputs, the secrets resolved for that
  attempt only, the repository files it needs, and artifacts from earlier steps.
  The agent streams log lines and sends one result per attempt.
- **Leases.** Heartbeats extend the leases of running attempts (60 seconds). When a
  lease expires, the attempt becomes uncertain.
- **Outbox.** The agent writes each result to disk before sending it and deletes it
  when the server acknowledges it, so results survive restarts and reconnects. An
  attempt that was running when the agent stopped is reported as uncertain.

## Executors

Inputs and outputs are JSON. Secrets arrive in the assignment's `secrets` map and
are referenced by name from the input; the agent masks their values in every log
line.

**`proxmox`** (the agent's own Proxmox library; the token arrives as the secret
`proxmox.token`, formatted `USER@REALM!TOKENID=SECRET`):

| Action | Input | Output |
|---|---|---|
| `observe` | `connection` | `version`, `cluster`, `hosts[]` (status, CPU, memory, uptime), `guests[]` (VMID, name, host, status, template, tags) |
| `verify-guest` | `connection`, `node`, `vmid`, `name`, `expectIpv4`, `waitSeconds` | `present`, `status`, `ipv4Addresses`, `checks[]` |
| `verify-absent` | `connection`, `vmid` | `present` |
| `set-protection` | `connection`, `node`, `vmid`, `protection` | `previous`, `protection`, `absent` |

`connection` is `{ endpoint, verifyTls, caCertificatePem }`.

**`opentofu`** (OpenTofu in a pinned container image, run rootless by the agent with
only the attempt's workspace mounted):

| Action | Input | Output |
|---|---|---|
| `preview` | `stack`, `root`, `backend`, `variables`, `env`, `secretEnv`, `destroy`, `image` | `hasChanges`, `changes[]` (address, type, actions), `counts`; artifact `plan.tar.gz` |
| `apply` | the same, plus the artifact `plan.tar.gz` | `outputs` (sensitive values withheld) |

`secretEnv` maps an environment variable to a secret name. The files of the root
arrive as `files_tar_gz`.

## Security notes

- The backend runs no tool code. Agents run OpenTofu in rootless containers with no
  runtime socket and only the attempt's workspace.
- Store values are encrypted with a fresh data key each, under a key-encryption
  key in a file outside the database (AES-256-GCM, bound to the key name and
  version). Secret values are never returned by the API.
- Keys under `system/` (the agent CA key, Git credentials) cannot be referenced
  from definitions.
- The first account is created with a one-time setup code that the backend writes
  to its data directory, so an exposed fresh install cannot be claimed by others.
- Saved plans are stored encrypted, because a plan can contain values from the
  configuration.

## Testing

- Backend: unit tests for definitions, policy, plan classification, and the store;
  integration tests against PostgreSQL in a container.
- Agent: the Proxmox library against a mock HTTP server; the OpenTofu executor with
  a fake `tofu`; the outbox.
- End to end: PostgreSQL, the backend, and an agent running real OpenTofu in a
  container against the example repository's `test-vm` recipe (`terraform_data`, no
  Proxmox needed) with a `pg` state backend, and a mock Proxmox API for observation.
  The `proxmox-vm` recipe is checked with `tofu validate`; it has not run against a
  real Proxmox installation.

## Status (October 20, 2026)

Built and verified:

- Backend: 47 tests, including provisioning, manual and destructive approval, superseded
  runs, missing secrets, lost leases and late results, retirement, VMID binding, missing
  folders, server-side secret masking, and the API with sign-in.
- Agent: 79 tests, including mutual-TLS enrollment, renewal, and the channel against a
  test server; Proxmox client against a mock; OpenTofu with a fake `tofu`; and, run by
  hand, OpenTofu 1.13.1 in rootless podman.
- Frontend: 39 tests; every page checked in a browser against the backend.
- End to end (`just e2e`): the backend and a real agent provision the test guest with
  OpenTofu in rootless podman and a `pg` state backend, observe the installation through
  the mock Proxmox API, then retire the guest. `just demo` leaves the same setup running
  with manual approval, to try the web interface.

Not done or not verified yet:

- The `proxmox-vm` recipe passes `tofu validate` but has not run against a real Proxmox
  installation. Its image must run the QEMU guest agent for verification to see an
  address.
- A custom CA for Proxmox (`tls.caCertificate`) reaches the agent's own Proxmox client,
  but not OpenTofu's provider, which uses the system trust store.
- If the apply of a retire run fails after protection was lifted, the VM stays
  unprotected until its next provision run restores it.
- The agent binary links against glibc (no musl target here), its systemd unit has not
  run on a real host, and tool images are pinned by tag, not digest.
- An agent killed hard does not look for its orphaned tool containers on restart.
- Passkeys, live updates (the frontend polls), drafts and the editor, the supervisor and
  self-updates, and other secret providers come later.
