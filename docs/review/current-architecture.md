# Current implementation: architecture diagrams

Snapshot: **October 21, 2026**, working tree on `proxmox-e2e` (on top of `cce0ee4`,
including the uncommitted credential-lifecycle, networking, and `nixos-rootfs` work).
These diagrams describe what the code does today, not the target design. The
target design is in [foundations-plan.md](foundations-plan.md).

## 1. System and deployment

```mermaid
flowchart LR
  subgraph browser[Operator browser]
    UI[React SPA<br/>react-query polling]
  end

  subgraph control[Control host - deploy/compose.yaml]
    Caddy[Caddy :8081<br/>static SPA + /api proxy<br/>CSP, no TLS]
    subgraph backend[octopushy-server .NET 10]
      API[REST API :8080<br/>cookie auth]
      AGP[Agent port :8443<br/>gRPC over mTLS<br/>/agent/ca.pem]
      ORCH[Orchestrator loop<br/>RunEngine + global gate]
      SYNC[RepositorySync<br/>poll / trigger]
      STORE[VariableStore<br/>AES-GCM envelope]
      CA[Agent CA]
    end
    PG[(PostgreSQL 18)]
    DATA[/data dir<br/>KEK, mirror.git, snapshots,<br/>bundles, logs, artifacts/]
  end

  subgraph worker[Worker host - systemd]
    AGENT[octopushy agent<br/>Rust, tokio]
    PODMAN[rootless podman<br/>OpenTofu container]
    OUTBOX[/state dir<br/>key, cert, outbox, work/]
  end

  GIT[(Lab Git repo)]
  PVE[Proxmox VE API]
  TFSTATE[(OpenTofu state backend<br/>pg / http)]

  UI -->|HTTPS via front proxy| Caddy --> API
  API --- ORCH
  ORCH --- STORE
  SYNC -->|git fetch / archive| GIT
  backend --- PG
  backend --- DATA
  AGENT -->|outbound mTLS gRPC| AGP
  AGENT --> PODMAN
  AGENT --- OUTBOX
  PODMAN -->|bpg/proxmox provider| PVE
  PODMAN --> TFSTATE
  AGENT -->|verify / observe / guest-exec| PVE
```

The agent always dials out. The server never connects to workers, Proxmox, or guests.
Git is read-only: the server fetches, snapshots, and bundles commits for agents.

## 2. Backend modules as they are today

```mermaid
flowchart TB
  subgraph Definitions[Octopushy.Definitions - pure]
    Loader[LabRepositoryLoader 732 l]
    Schemas[SchemaCatalog<br/>embedded JSON Schemas]
    Policy[PolicyResolver]
    Inputs[ProvisionInputs<br/>inputs hash, TreeHash]
  end

  subgraph Server[Octopushy.Server]
    Program[Program.cs<br/>DI, Kestrel, port split, startup]
    subgraph Api
      Endpoints[Endpoints 598 l<br/>handlers + domain rules]
      Views[Views 500 l<br/>read models + GuestStatus]
      Account[AccountEndpoints]
    end
    subgraph Orchestration
      Engine[RunEngine 913 l<br/>gate, state machine, approval,<br/>effects, dispatch, leases]
      Factory[RunFactory]
      Builder[AssignmentBuilder]
      Bindings[CredentialBindings static]
    end
    subgraph Agents
      Grpc[EnrollmentService<br/>AgentChannelService]
      Conns[AgentConnections<br/>in-memory sessions]
      CAx[AgentCertificateAuthority]
      Files[AttemptLogStore / ArtifactStore]
    end
    subgraph Git
      Sync[RepositorySync]
      Mirror[RepositoryMirror]
      Cache[RevisionCache]
      Cli[GitCli]
    end
    subgraph Inventory
      Updater[InventoryUpdater]
      Planner[Planner]
    end
    subgraph Store
      VS[VariableStore]
      Env[Envelope + KEK]
    end
    Db[(OctopushyDb / Entities)]
  end

  Endpoints --> Engine
  Endpoints --> VS
  Endpoints --> Db
  Endpoints --> Planner
  Views --> Db
  Views --> Mirror
  Engine --> Builder
  Engine --> Bindings
  Engine --> Conns
  Engine --> Files
  Engine --> Db
  Builder --> VS
  Builder --> Mirror
  Builder --> Files
  Bindings --> Db
  Grpc --> Engine
  Grpc --> CAx
  Grpc --> Conns
  CAx --> VS
  Sync --> Mirror --> Cli
  Sync --> Cache --> Loader
  Sync -->|ExecuteAsync under gate| Updater
  Updater --> Factory --> Inputs
  Factory --> Policy
  Updater --> Engine
  Planner --> Engine
  VS --> Env
  VS --> Db
  Loader --> Schemas
```

Almost everything that changes runs, steps, or attempts goes through one in-process
`SemaphoreSlim` in `RunEngine` (two partial files, 525 + 388 lines), including git
bundling, artifact writes, heartbeats, and variable mutations. Agent deletion and
repository settings are written outside it. That gate is the main coupling point.

A lease expiry marks the attempt `uncertain` whether or not it was mutating; only a
mutating one makes the step and run `uncertain`, while a read-only one gets a new attempt.
Only an `uncertain` *mutating* attempt blocks targeted filesystem retry (`RunEngine.cs:129`); a read-only one that lost its agent does not.

## 3. Data model

Only `Step → Run` and `Attempt → Step` are real foreign keys; every other line is a
logical reference (a Guid or string with no constraint).

```mermaid
erDiagram
  RUN ||--|{ STEP : "FK cascade"
  STEP ||--|{ ATTEMPT : "FK cascade"
  RESOURCE ||--o{ RUN : "resource_id (logical)"
  REVISION ||--o{ RUN : "revision_id (logical)"
  REVISION ||--o{ RESOURCE : "desired/checked/applied (logical)"
  ATTEMPT ||--o{ ARTIFACT : "attempt_id (logical)"
  ATTEMPT ||--o{ SECRET_RESOLUTION : "audit (logical)"
  AGENT ||--o{ ATTEMPT : "agent_id (logical)"
  AGENT ||--o{ ENROLLMENT_TOKEN : "agent_id after use"
  STORE_ENTRY ||--|{ STORE_VERSION : "key (logical)"
  RESOURCE ||--o{ OBSERVATION : "installations, newest 50"

  RUN {
    guid id
    long number
    string operation "provision | retire | observe"
    string status
    string inputs_hash
    jsonb context "RunContext: secrets map, pinned versions"
  }
  STEP {
    int ordinal
    string name "plan, approval, apply, verify..."
    string kind "agent | approval"
    string status
    string plan_sha256
    string decision
  }
  ATTEMPT {
    string executor
    string action
    bool mutating
    string status
    timestamp lease_expires_at
    text_array required_labels
    string host_key
  }
  RESOURCE {
    string address "lab/installation/guest"
    string kind
    jsonb definition
    string desired_inputs_hash
    string applied_inputs_hash
    int bound_vmid
  }
  STORE_VERSION {
    bytea ciphertext
    bytea wrapped_key
    timestamp activated_at
    timestamp revoked_at
  }
  AGENT {
    string certificate_serial
    string previous_certificate_serial
    text_array labels
    int max_concurrency
  }
  ENROLLMENT_TOKEN {
    string token_hash "SHA-256"
    timestamp expires_at
    timestamp used_at
  }
```

## 4. From Git commit to runs

```mermaid
sequenceDiagram
  autonumber
  participant Sync as RepositorySync
  participant Mirror as RepositoryMirror
  participant Loader as LabRepositoryLoader
  participant Upd as InventoryUpdater
  participant Fac as RunFactory
  participant Eng as RunEngine
  Sync->>Mirror: fetch branch (token askpass / ssh key)
  Mirror-->>Sync: head commit
  alt head is new
    Sync->>Mirror: snapshot(commit) via git archive
    Sync->>Loader: Load(snapshotDir) (cached, LRU 8)
    Loader-->>Sync: LabRepository + issues
    Sync->>Eng: ExecuteAsync (takes gate)
    Eng->>Upd: ApplyAsync(revision)
    Upd->>Upd: upsert Resources, renames, VMID binding
    Upd->>Upd: supersede stale runs
    Upd->>Fac: Guest() when desired inputs hash changed
    Fac-->>Upd: Run + Steps + RunContext
    Upd->>Eng: AddRunsAsync, wake signal
  end
```

## 5. Run and step lifecycle

Steps of a provision run: `plan → approval → apply → verify [→ grow-root → verify-root]`.
A retire run uses `plan(destroy) → approval → unprotect → apply → verify-absent`.

```mermaid
stateDiagram-v2
  [*] --> queued
  queued --> running: no earlier unfinished run on this resource
  running --> blocked: credential binding missing or revoked
  blocked --> running: binding fixed
  running --> awaiting_approval: plan needs manual approval
  awaiting_approval --> running: approve (plan sha must match)
  awaiting_approval --> cancelled: reject
  running --> succeeded: all steps succeeded or skipped
  running --> failed: a step failed
  running --> uncertain: mutating attempt lost its lease
  uncertain --> running: late result accepted
  uncertain --> failed: operator closes
  failed --> running: retry-filesystem (grow-root / verify-root only)
  queued --> cancelled: superseded or cancelled
  running --> cancelled: cancel (no active mutating attempt)
  succeeded --> [*]
  failed --> [*]
  cancelled --> [*]
```

```mermaid
stateDiagram-v2
  direction LR
  state "Attempt" as A {
    [*] --> a_queued
    a_queued --> assigned: DispatchAsync picks agent<br/>(labels, executor, host limit, capacity)
    assigned --> a_running: AttemptStarted
    a_running --> a_succeeded: Result SUCCEEDED
    a_running --> a_failed: Result FAILED
    a_running --> a_uncertain: Result UNCERTAIN, or any lease expiry
    assigned --> a_queued: TrySend failed / not reported on Hello (read-only)
    a_uncertain --> retry: read-only and retries ≤ 2 → new attempt
    a_uncertain --> a_succeeded: one late result
  }
```

## 6. Agent protocol

```mermaid
sequenceDiagram
  autonumber
  participant Op as Operator (UI)
  participant API as Backend API
  participant AG as Agent
  participant GW as Agent port (gRPC mTLS)
  participant Eng as RunEngine
  Op->>API: POST /api/agents/tokens
  API-->>Op: opy1.<secret>.<ca-sha256> (hash stored)
  Note over AG: octopushy agent enroll
  AG->>GW: GET /agent/ca.pem (unverified TLS)
  AG->>AG: sha256(CA) == fingerprint from token
  AG->>AG: generate P-256 key + CSR
  AG->>GW: Enrollment.Enroll(secret, name, CSR)  [TLS pinned to CA]
  GW->>GW: claim token (UPDATE ... WHERE used_at IS NULL)
  GW-->>AG: agent_id, certificate, CA
  Note over AG: octopushy agent run
  AG->>GW: Connect stream (client cert)
  AG->>GW: Hello(executors, running ids, tool runtime)
  GW-->>AG: Welcome(max_concurrency, heartbeat)
  GW->>Eng: OnHelloAsync (renew leases / requeue)
  loop every heartbeat
    AG->>GW: Heartbeat(active ids) → lease renewed
  end
  Eng->>GW: Assignment(input_json, secrets map, files_tar_gz, artifacts)
  GW-->>AG: Assignment
  AG->>AG: workspace + queued marker, wait for limiter
  AG->>GW: AttemptStarted, AttemptLog batches (masked)
  AG->>AG: result written durably to outbox
  AG->>GW: AttemptResult(outcome, outputs_json, artifacts)
  GW->>Eng: OnResultAsync (verify sha, store encrypted)
  GW-->>AG: ResultAck → delete outbox, workspace
  opt cert past 2/3 of lifetime
    AG->>GW: RenewCertificate(new CSR)
    GW-->>AG: new certificate (old serial kept as previous)
  end
```

## 7. Agent internals

```mermaid
flowchart TB
  main[main.rs<br/>signals, tracing] --> cli[cli.rs<br/>enroll / run / status]
  cli --> run[run.rs<br/>composition root]
  run --> ident[identity + renewal + tls]
  run --> outbox[Outbox<br/>one JSON per result, fsync]
  run --> probe[ToolRuntime::probe<br/>podman / docker / local]
  run --> mgr[AttemptManager<br/>entries, limiter, recovery]
  run --> conn[connection::run<br/>reconnect + backoff]
  conn --> session[session select! loop<br/>Hello/Welcome, heartbeat,<br/>flush 500 ms, renewal]
  session -->|accept| mgr
  session -->|collect cursor| mgr
  mgr -->|spawn per attempt| exec{executors::run<br/>string match}
  exec --> tofu[opentofu<br/>preview / apply]
  exec --> pvx[proxmox<br/>observe / verify / set-protection]
  exec --> nix[nixos-rootfs<br/>grow / verify via guest-exec]
  tofu --> tools[tools::ToolRuntime<br/>podman run --read-only --cap-drop=all]
  pvx --> lib[octopushy-proxmox crate]
  nix --> lib
  mgr --> logs[LogBuffer + Masker]
  mgr --> outbox
```

Attempts never write to the connection. They fill a `LogBuffer` or the outbox and wake
the session, which pulls messages through a per-connection `SessionCursor`, so every
reconnect replays started markers, retained logs, and unacknowledged results.

## 8. Lab repository model

```mermaid
flowchart LR
  root[octopushy.yaml<br/>defaults, stateBackends] --> lab[labs/&lt;lab&gt;/lab.yaml]
  lab --> inst[proxmox/&lt;inst&gt;/installation.yaml<br/>connection: endpoint, tls, token→store]
  inst --> hosts[hosts: datastores, bridges, limits]
  inst --> guest[guests/&lt;guest&gt;/guest.yaml]
  guest -->|host| hosts
  guest -->|template| tpl[templates/&lt;id&gt;/template.yaml]
  guest -->|provision.recipe| rec[recipes/&lt;id&gt;/recipe.yaml]
  guest -->|state.backend| root
  guest -->|access.sshKey| vs[(variable store)]
  inst -->|token, CA| vs
  rec -->|root| mod[modules/opentofu/...]
  rec -->|inputs| sch[inputs.schema.json]
  tpl -->|build.recipe| rec
```

Policy (`approval`, `destructive`) and `execution.agentLabels` cascade
`octopushy.yaml → lab → installation → guest`; the nearest setting wins.
