# Stack review: .NET server, Rust agent, React UI

**Reviewed: October 15, 2026.** Scope: the user's proposed stack (.NET 10 with EF
Core and SignalR for the server, Rust for the agent, Vite + React + TypeScript
for the UI), checked against the decisions in the
[architecture agreement](../architecture-agreement.md) and the requirement for
single-node deployment with automatic updates of the server and agents.
Documentation review only; nothing was built or benchmarked. Recommendations are
engineering proposals, not decisions.

## Conclusion

The split fits the architecture. The server is mostly records, orchestration,
authentication, Git, and cryptography, where .NET's built-in libraries cover most
needs. The agent is a long-running daemon on remote hosts that must be small,
static, and robust on bad networks, which suits Rust. The user already runs .NET
services backed by PostgreSQL in Compose (`krb-crt-01_watchtower` in `nodes`) and
writes Rust tooling (`homelab/tools/node-manager`).

The cost is two toolchains. It stays bounded if the agent is thin (no policy,
ordering, or long-lived state) and the server–agent protocol is defined once, in
Protobuf, and generated for both languages.

## Findings

| Topic | Finding | Consequence |
|---|---|---|
| .NET 10 | LTS, released November 11, 2025, supported until November 14, 2028. [S1] | A sound base for a multi-year project |
| EF Core and Native AOT | Microsoft calls NativeAOT and precompiled queries "highly experimental" and "not yet suited for production use". [S2] | Publish the server self-contained, not Native AOT. The server does not need AOT. |
| SignalR | Official browser client; trimming/AOT support has limits (System.Text.Json source generation, no strongly typed hubs). [S3] | Fine for browser live updates, which are not AOT-published. No official Rust client, so not for agents. |
| Passkeys | ASP.NET Core Identity in .NET 10 supports passkey sign-in, scoped to authentication. [S4] | The single user can sign in with a passkey without an external identity provider |
| LibGit2Sharp | SSH comes through a separate community package, `LibGit2Sharp-SSH`; its native binaries package was last updated in 2016. [S5] uv moved its Git fetching to the Git CLI because of libgit2 SSH limitations. [S6] | Use the Git CLI on the server |
| `git merge-tree --write-tree` | Since Git 2.38, performs a real merge without reading or writing a working tree or index, and reports conflicted files. [S7] | Fits drafts kept as branches: merge, detect conflicts, and build the three-way view without touching a checkout |
| Proxmox client in Rust | According to a Proxmox forum reply, Proxmox publishes its Rust crates only to its own development APT repository, not crates.io. [S8] Community crates are small and partly documented. [S9] In `nodes`, OpenTofu (bpg/proxmox) already owns the VM lifecycle; the `homelab/scripts/tofu` wrapper calls the API directly only to locate a VM, check a permission, read its IP address and SSH host key through the guest agent, and toggle protection. | The user chose a dedicated Octopushy Proxmox library, starting with those calls beside the OpenTofu executor |
| Rust self-update | `self_update` supports Gitea and S3 sources with optional signatures (1.0 still a release candidate); `self-replace` swaps a running binary. [S10] rustup removed GPG validation that could not abort an install. [S11] | The agent and supervisor run natively, so they can update themselves from the server (architecture agreement, section 6) |
| age in .NET | AgeSharp, a preview C# implementation of age targeting .NET 10, claims interoperability with the reference tool. [S12] | Usable for export bundles once interoperability is tested against `age` |
| Key-encryption key | systemd encrypted credentials use AES-256-GCM keyed by a TPM2 key, a host key, or both, and are tied to the machine. [S13] | A good place for the variable store's key-encryption key, outside the database and its backups |

## Compose packaging follow-up

The user proposed one Compose project for server, PostgreSQL, agent, and Watchtower,
with the agent starting tool containers through Docker.

| Topic | Finding | Consequence |
|---|---|---|
| Watchtower status | containrrr/watchtower was archived on December 17, 2025; no further releases or security fixes. [S14] The maintained fork is published as `nickfedor/watchtower`. [S15] | Use the fork |
| Container selection | Watchtower watches every container on the daemon unless filtered by labels or scope. [S16] | Label only Octopushy's containers |
| Pre-update hooks | In the fork, exit code 75 skips updating that container; any other non-zero code aborts the update process. Hooks are off by default, time out after one minute, and do not run if the container is stopped. [S17] | The agent can refuse an update while it holds leases |
| Rollback | No rollback is documented. With `--rolling-restart`, a container that fails its health check is logged and Watchtower continues. [S18] | Backups before migrations and backward-compatible schemas are Octopushy's job |
| HTTP API mode | `/v1/update` triggers an update; periodic polls stop unless re-enabled. The fork reworked the HTTP API in v1.20. [S19] | Octopushy can later trigger its own updates |
| Docker-in-Docker | `docker:dind` and the rootless variant both require `--privileged`, and nested containers need a volume at the daemon's data path. [S20] Operation nested under a rootless host daemon was not confirmed. | Prefer the VM's own Docker daemon on a dedicated VM |

## Recommendations

1. **Server:** ASP.NET Core on .NET 10, published self-contained. EF Core with
   Npgsql for the domain model and migrations; plain SQL (`FOR UPDATE SKIP
   LOCKED`, `LISTEN`/`NOTIFY`) for the work queue and leases. Do not adopt a job
   framework such as Hangfire as the orchestrator: its retry-means-rerun model
   conflicts with attempts, leases, and uncertain outcomes.
2. **Agent channel:** gRPC bidirectional streaming over HTTP/2 with mutual TLS,
   on its own port, using Grpc.AspNetCore on the server and tonic with rustls on
   the agent.
3. **Browser channel:** REST with a generated TypeScript client from the
   server's OpenAPI document, and SignalR only for pushing state changes and log
   tails.
4. **UI:** Vite, React, TypeScript, Monaco with schema validation. The same JSON
   Schema validates definitions on the server and in the editor. Build the UI
   into the server so UI and API versions always match. *Superseded October 16,
   2026: the user splits the backend and frontend into separate images; see
   section 6 of the architecture agreement.*
5. **Git:** the Git CLI, using `git merge-tree --write-tree` for draft merges.
6. **One version** for server, UI, and agent, built and signed by one pipeline.

## Sources

- **[S1] Microsoft — .NET and .NET Core Support Policy:**
  https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- **[S2] Microsoft — EF Core NativeAOT support and precompiled queries:**
  https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries
- **[S3] ABP — .NET 9.0 SignalR supports trimming and Native AOT:**
  https://abp.io/community/articles/.net-9.0-signalr-supports-trimming-and-native-aot-4oxx0qbs
- **[S4] Microsoft — Passkeys in ASP.NET Core:**
  https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys
- **[S5] NuGet — LibGit2Sharp-SSH.NativeBinaries:**
  https://www.nuget.org/packages/LibGit2Sharp-SSH.NativeBinaries
- **[S6] uv — switch Git fetching to the CLI:**
  https://app.semanticdiff.com/gh/astral-sh/uv/commit/71ec568d0f02ed097b3f9160f9639ee206eddc6f
- **[S7] Git — git-merge-tree:** https://git-scm.com/docs/git-merge-tree/2.38.0
- **[S8] Proxmox forum — Dev env:** https://forum.proxmox.com/threads/dev-env.159856/
- **[S9] docs.rs — proxmox-api:** https://docs.rs/proxmox-api/
- **[S10] docs.rs — self_update:** https://docs.rs/crate/self_update/1.0.0-rc.6
- **[S11] Rust blog — Announcing Rustup 1.25.2:**
  https://blog.rust-lang.org/2023/02/01/Rustup-1.25.2/
- **[S12] NuGet — AgeSharp:** https://www.nuget.org/packages/AgeSharp
- **[S13] systemd-creds(1):** https://man7.org/linux/man-pages/man1/systemd-creds.1.html
- **[S14] Linuxiac — Docker update tool Watchtower reaches end of maintenance:**
  https://linuxiac.com/docker-update-tool-watchtower-reaches-end-of-maintenance/
- **[S15] nicholas-fedor/watchtower:** https://github.com/nicholas-fedor/watchtower
- **[S16] Watchtower (fork) — Container selection:**
  https://watchtower.nickfedor.com/v1.23.1/configuration/container-selection/
- **[S17] Watchtower (fork) — Lifecycle hooks:**
  https://watchtower.nickfedor.com/v1.23.1/advanced-features/lifecycle-hooks/
- **[S18] Watchtower (fork) — Update behavior:**
  https://watchtower.nickfedor.com/v1.23.1/configuration/update-behavior/
- **[S19] Watchtower — HTTP API mode:** https://containrrr.dev/watchtower/http-api-mode/
- **[S20] Docker — Rootless mode tips (Docker in Docker):**
  https://docs.docker.com/engine/security/rootless/tips/
