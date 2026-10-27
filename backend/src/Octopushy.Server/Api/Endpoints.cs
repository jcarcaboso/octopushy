using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

public static partial class Endpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);
        MapOverview(api);
        MapRepository(api.MapGroup("/repository").WithTags("Repository"));
        MapInventory(api);
        api.MapGuestDiskUsage();
        MapRuns(api);
        MapAgents(api.MapGroup("/agents").WithTags("Agents"));
        api.MapWorkerPreparation();
        api.MapWorkerUpdates();
        api.MapWorkerRepositoryDeployment();
        MapVariables(api.MapGroup("/variables").WithTags("Variables"));
    }

    private static void MapOverview(RouteGroupBuilder api)
    {
        api.MapGet("/overview", async (Views views, OctopushyDb db, CancellationToken ct) =>
            TypedResults.Ok(await views.OverviewAsync(await RepositoryAsync(db, views, ct), ct)))
            .WithTags("Overview")
            .WithName("getOverview");
    }

    private static void MapRepository(RouteGroupBuilder group)
    {
        group.MapGet("", async (OctopushyDb db, Views views, CancellationToken ct) => TypedResults.Ok(await RepositoryAsync(db, views, ct)))
            .WithName("getRepository");

        group.MapPut("", async Task<Results<Ok<RepositoryDto>, ProblemHttpResult>> (
            RepositoryUpdate update, RunEngine engine, RepositorySync sync, TimeProvider clock, CancellationToken ct) =>
        {
            if (update.CredentialKind is not ("none" or "token" or "ssh"))
            {
                return TypedResults.Problem(title: "The credential kind is none, token, or ssh.", statusCode: 400);
            }

            if (update.PollSeconds is < 10 or > 86400)
            {
                return TypedResults.Problem(title: "Poll between every 10 seconds and once a day.", statusCode: 400);
            }

            return await engine.ExecuteAsync(async (db, services) =>
            {
                var store = services.GetRequiredService<VariableStore>();
                var views = services.GetRequiredService<Views>();
                var settings = await db.RepositorySettings.Where(r => r.Id == 1).FirstOrDefaultAsync(ct);
                if (settings is null)
                {
                    settings = new RepositorySettings();
                    db.RepositorySettings.Add(settings);
                }

                var url = string.IsNullOrWhiteSpace(update.Url) ? null : update.Url.Trim();
                if (settings.Url != url || settings.Branch != update.Branch.Trim())
                {
                    settings.SourceId = Guid.NewGuid();
                    settings.HeadCommit = null;
                    settings.LastFetchAt = null;
                    settings.LastFetchError = null;
                }

                settings.Url = url;
                settings.Branch = update.Branch.Trim();
                settings.PollSeconds = update.PollSeconds;
                settings.CredentialKind = update.CredentialKind;
                settings.CredentialUsername = string.IsNullOrWhiteSpace(update.CredentialUsername) ? null : update.CredentialUsername.Trim();
                settings.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                if (!string.IsNullOrEmpty(update.CredentialSecret))
                {
                    await store.SetInternalAsync(RepositorySync.CredentialKey, update.CredentialSecret, Visibilities.Secret, "Credential for reading the lab repository.", ct);
                }

                sync.Trigger();
                return TypedResults.Ok(await RepositoryAsync(db, views, ct));
            }, ct);
        })
        .WithName("updateRepository");

        group.MapPost("/sync", (RepositorySync sync) =>
        {
            sync.Trigger();
            return TypedResults.Accepted((string?)null);
        })
        .WithName("syncRepository");

        group.MapGet("/revisions", async (OctopushyDb db, int? limit, CancellationToken ct) =>
        {
            var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            return TypedResults.Ok((await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).Take(Math.Clamp(limit ?? 30, 1, 200)).ToListAsync(ct))
                .Select(r => Views.Revision(r, RepositoryTrust.IsCurrentSource(r, settings))).ToList());
        })
        .WithName("listRevisions");

        group.MapGet("/revisions/{id:guid}", async Task<Results<Ok<RevisionDetailDto>, NotFound>> (Guid id, OctopushyDb db, CancellationToken ct) =>
        {
            var revision = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (revision is null) return TypedResults.NotFound();
            var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            return TypedResults.Ok(new RevisionDetailDto(Views.Revision(revision, RepositoryTrust.IsCurrentSource(revision, settings)), Views.Issues(revision.Issues)));
        })
        .WithName("getRevision");

        group.MapGet("/revisions/{id:guid}/review", async (Guid id, RunEngine engine, RepositoryMirror mirror, CancellationToken ct) =>
            TypedResults.Ok(await engine.ExecuteAsync((db, _) => RepositoryTrust.ReviewAsync(db, mirror, id, ct), ct)))
            .WithName("reviewRevision");

        group.MapPost("/revisions/{id:guid}/trust", async (Guid id, TrustRevisionRequest request, HttpContext http,
            RunEngine engine, RepositoryMirror mirror, OrchestratorSignal signal, TimeProvider clock, CancellationToken ct) =>
            TypedResults.Ok(await engine.ExecuteAsync(async (db, services) =>
            {
                var review = await RepositoryTrust.ReviewAsync(db, mirror, id, ct);
                if (!review.CanTrust || !string.Equals(request.Commit, review.Revision.Commit, StringComparison.Ordinal)
                    || !string.Equals(request.BaseCommit, review.BaseCommit, StringComparison.Ordinal))
                {
                    throw new RunException(review.BlockedReason ?? "The review changed. Review the exact revision and its current base again.");
                }
                var actor = http.User.Identity?.Name;
                if (string.IsNullOrWhiteSpace(actor)) throw new RunException("An authenticated operator is required.");
                var revision = await db.Revisions.SingleAsync(r => r.Id == id, ct);
                revision.TrustedAt = clock.GetUtcNow();
                revision.TrustedBy = actor;
                await db.SaveChangesAsync(ct);
                // Promoting the discovered head can now supersede older prepared work;
                // merely discovering it could not. Trusting an older pinned revision
                // never rolls the visible inventory back.
                var settings = await db.RepositorySettings.SingleAsync(s => s.Id == 1, ct);
                if (settings.HeadCommit == revision.Commit)
                {
                    var repository = await services.GetRequiredService<RevisionCache>().GetAsync(revision.Commit, ct);
                    await services.GetRequiredService<InventoryUpdater>().ApplyAsync(db, revision, repository, ct);
                }
                foreach (var run in await db.Runs.Where(r => r.RevisionId == id && r.Commit == revision.Commit
                    && r.Status == RunStatuses.Blocked && r.Detail != null && r.Detail.StartsWith("Waiting for trust")).ToListAsync(ct))
                {
                    run.Status = run.StartedAt is null ? RunStatuses.Queued : RunStatuses.Running;
                    run.Detail = null;
                }
                await db.SaveChangesAsync(ct);
                signal.Wake();
                return Views.Revision(revision, true);
            }, ct)))
            .WithName("trustRevision");
    }

    private static void MapInventory(RouteGroupBuilder api)
    {
        api.MapGet("/inventory", async (Views views, CancellationToken ct) => TypedResults.Ok(await views.InventoryAsync(ct)))
            .WithTags("Inventory")
            .WithName("getInventory");

        api.MapGet("/guests/{id:guid}", async Task<Results<Ok<GuestDetailDto>, NotFound>> (Guid id, Views views, CancellationToken ct) =>
            await views.GuestAsync(id, ct) is { } guest ? TypedResults.Ok(guest) : TypedResults.NotFound())
            .WithTags("Inventory")
            .WithName("getGuest");

        api.MapPost("/guests/{id:guid}/plan", async (Guid id, Planner planner, CancellationToken ct) =>
            TypedResults.Ok(new StartedRun(await planner.PlanGuestAsync(id, ct))))
            .WithTags("Inventory")
            .WithName("planGuest");

        api.MapGet("/installations/{id:guid}", async Task<Results<Ok<InstallationDetailDto>, NotFound>> (Guid id, Views views, CancellationToken ct) =>
            await views.InstallationAsync(id, ct) is { } installation ? TypedResults.Ok(installation) : TypedResults.NotFound())
            .WithTags("Inventory")
            .WithName("getInstallation");

        api.MapPost("/installations/{id:guid}/observe", async (Guid id, Planner planner, CancellationToken ct) =>
            TypedResults.Ok(new StartedRun(await planner.ObserveAsync(id, ct))))
            .WithTags("Inventory")
            .WithName("observeInstallation");
    }

    private static void MapRuns(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/runs").WithTags("Runs");

        group.MapGet("", async (OctopushyDb db, Guid? resourceId, string? status, int? limit, CancellationToken ct) =>
        {
            var query = db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsQueryable();
            if (resourceId is { } resource)
            {
                query = query.Where(r => r.ResourceId == resource);
            }

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(r => r.Status == status);
            }

            var runs = await query.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
            var ids = runs.Select(r => r.ResourceId).Distinct().ToList();
            var resources = await db.Resources.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => (r.Address, r.Kind), ct);
            return TypedResults.Ok(runs.Select(r => Views.Summary(r, resources.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList());
        })
        .WithName("listRuns");

        group.MapGet("/{id:guid}", async Task<Results<Ok<RunDetailDto>, NotFound>> (Guid id, OctopushyDb db, CancellationToken ct) =>
        {
            var run = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
            {
                return TypedResults.NotFound();
            }

            var resource = await db.Resources.AsNoTracking().Where(r => r.Id == run.ResourceId).Select(r => new { r.Address, r.Kind }).FirstOrDefaultAsync(ct);
            var agentIds = run.Steps.SelectMany(s => s.Attempts).Select(a => a.AgentId).OfType<Guid>().Distinct().ToList();
            var agents = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
            var context = RunContext.Parse(run.Context);
            var steps = run.Steps.OrderBy(s => s.Ordinal).Select(s => new StepDto(
                s.Id,
                s.Ordinal,
                s.Name,
                s.Kind,
                s.Status,
                s.Detail,
                s.StartedAt,
                s.FinishedAt,
                s.Outputs is null ? null : JsonNode.Parse(s.Outputs),
                Views.Plan(s),
                s.Kind == StepKinds.Approval
                    ? new ApprovalDto(s.ApprovalRequired, s.Decision, s.DecidedBy, s.DecidedAt, s.DecisionNote, s.PlanSha256, s.WorkerUpdateSha256)
                    : null,
                s.Attempts.OrderBy(a => a.Number).Select(a => new AttemptDto(
                    a.Id, a.Number, a.Executor, a.Action, a.Status, a.WaitingReason, a.AgentId,
                    a.AgentId is { } agent ? agents.GetValueOrDefault(agent) : null,
                    a.CreatedAt, a.AssignedAt, a.StartedAt, a.FinishedAt, a.Error, a.LateResult, a.LogLines, a.Mutating)).ToList()))
                .ToList();
            var attempts = run.Steps.SelectMany(s => s.Attempts).ToList();
            var canCancel = !RunStatuses.IsTerminal(run.Status) && run.Status != RunStatuses.Uncertain
                            && !attempts.Any(a => a.Mutating && AttemptStatuses.IsActive(a.Status));
            return TypedResults.Ok(new RunDetailDto(
                Views.Summary(run, resource?.Address ?? "?", resource?.Kind ?? "?"),
                steps,
                context.AgentLabels,
                context.AgentLabelsSetBy,
                context.Policy is { } p ? new RunPolicyDto(p.Approval, p.ApprovalSetBy, p.Destructive, p.DestructiveSetBy) : null,
                canCancel,
                run.Status == RunStatuses.Uncertain,
                context.WorkerUpdate is { } update ? ManagedWorkerUpdates.View(context.ManagedWorker!.Generation, update) : null));
        })
        .WithName("getRun");

        group.MapPost("/{id:guid}/steps/{stepId:guid}/approve", async (Guid id, Guid stepId, DecisionRequest request, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.ApproveAsync(id, stepId, request.PlanSha256 ?? "", http.User.Identity!.Name!, request.Note, ct, request.WorkerUpdateSha256);
            return TypedResults.NoContent();
        })
        .WithName("approveStep");

        group.MapPost("/{id:guid}/steps/{stepId:guid}/reject", async (Guid id, Guid stepId, DecisionRequest request, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.RejectAsync(id, stepId, http.User.Identity!.Name!, request.Note, ct);
            return TypedResults.NoContent();
        })
        .WithName("rejectStep");

        group.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.CancelAsync(id, $"Cancelled by {http.User.Identity!.Name}.", ct);
            return TypedResults.NoContent();
        })
        .WithName("cancelRun");

        group.MapPost("/{id:guid}/retry-filesystem", async (Guid id, RunEngine engine, CancellationToken ct) =>
        {
            await engine.RetryFilesystemAsync(id, ct);
            return TypedResults.NoContent();
        })
        .WithName("retryFilesystem");

        group.MapPost("/{id:guid}/close", async (Guid id, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.CloseUncertainAsync(id, http.User.Identity!.Name!, ct);
            return TypedResults.NoContent();
        })
        .WithName("closeRun");

        api.MapGet("/attempts/{id:guid}/log", async Task<Results<Ok<LogDto>, NotFound>> (Guid id, long? after, int? limit, OctopushyDb db, AttemptLogStore logs, CancellationToken ct) =>
        {
            var status = await db.Attempts.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).FirstOrDefaultAsync(ct);
            if (status is null)
            {
                return TypedResults.NotFound();
            }

            var lines = await logs.ReadAsync(id, after ?? -1, Math.Clamp(limit ?? 2000, 1, 10000), ct);
            return TypedResults.Ok(new LogDto(lines, lines.Count > 0 ? lines[^1].Sequence : after ?? -1, AttemptStatuses.IsTerminal(status)));
        })
        .WithTags("Runs")
        .WithName("getAttemptLog");
    }

    private static void MapAgents(RouteGroupBuilder group)
    {
        group.MapGet("", async (OctopushyDb db, AgentConnections connections, CancellationToken ct) =>
        {
            var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            var running = await db.Attempts.AsNoTracking()
                .Where(a => a.AgentId != null && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
                .GroupBy(a => a.AgentId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            return TypedResults.Ok(agents.Select(a => new AgentDto(
                a.Id, a.Name, a.Labels, a.MaxConcurrency, a.Disabled, connections.IsConnected(a.Id), a.EnrolledAt, a.LastSeenAt,
                a.Version, a.Executors, a.ToolRuntime, a.CertificateExpiresAt, running.GetValueOrDefault(a.Id), a.Draining)).ToList());
        })
        .WithName("listAgents");

        group.MapPatch("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, AgentUpdate update, RunEngine engine, AgentConnections connections, OrchestratorSignal signal, CancellationToken ct) =>
        {
            return await engine.ExecuteAsync<Results<NoContent, NotFound, ProblemHttpResult>>(async (db, _) =>
            {
                var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (agent is null)
                {
                    return TypedResults.NotFound();
                }
                if ((update.Draining is false || update.Disabled is true) && await ManagedWorkerSafety.HasUnfinishedUpdateAsync(db, id, ct))
                {
                    return TypedResults.Problem(statusCode: 409, title: "Resolve this worker's OS update or uncertain outcome before disabling it or ending its drain.");
                }

                if (update.Labels is { } labels)
                {
                    if (labels.Any(l => !LabelPattern().IsMatch(l)))
                    {
                        return TypedResults.Problem(title: "Labels are lowercase letters, digits, '.', '_', and '-'.", statusCode: 400);
                    }

                    agent.Labels = labels.Distinct().Order().ToArray();
                }

                if (update.MaxConcurrency is { } max)
                {
                    if (max is < 1 or > 64)
                    {
                        return TypedResults.Problem(title: "An agent runs between 1 and 64 attempts at once.", statusCode: 400);
                    }

                    agent.MaxConcurrency = max;
                }

                if (update.Disabled is { } disabled)
                {
                    agent.Disabled = disabled;
                }
                if (update.Draining is { } draining) agent.Draining = draining;

                await db.SaveChangesAsync(ct);
                if (agent.Disabled)
                {
                    connections.Disconnect(agent.Id);
                }

                signal.Wake();
                return TypedResults.NoContent();
            }, ct);
        })
        .WithName("updateAgent");

        group.MapDelete("/{id:guid}", (Guid id, RunEngine engine, AgentConnections connections, OrchestratorSignal signal, CancellationToken ct) =>
            engine.ExecuteAsync<Results<NoContent, ProblemHttpResult>>(async (db, _) =>
            {
                if (await ManagedWorkerSafety.HasUnfinishedUpdateAsync(db, id, ct))
                    return TypedResults.Problem(statusCode: 409, title: "Resolve this worker's OS update or uncertain outcome before removing its enrollment.");
                if (await db.Attempts.AnyAsync(a => a.AgentId == id
                    && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running
                        || a.Mutating && a.Status == AttemptStatuses.Uncertain && a.Step!.Status == StepStatuses.Uncertain), ct))
                {
                    return TypedResults.Problem(title: "The agent has running or unresolved mutating attempts; disable it, drain its work and inspect uncertain outcomes first.", statusCode: 409);
                }

                connections.Disconnect(id);
                await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
                signal.Wake();
                return TypedResults.NoContent();
            }, ct))
        .WithName("deleteAgent");

        group.MapGet("/tokens", async (OctopushyDb db, CancellationToken ct) =>
            TypedResults.Ok((await db.EnrollmentTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync(ct))
                .Select(t => new TokenDto(t.Id, t.Name, t.Labels, t.CreatedAt, t.ExpiresAt, t.UsedAt, t.AgentId)).ToList()))
            .WithName("listEnrollmentTokens");

        group.MapPost("/tokens", async Task<Results<Ok<CreatedToken>, ProblemHttpResult>> (TokenRequest request, OctopushyDb db, AgentCertificateAuthority ca, IOptions<OctopushyOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.Labels.Any(l => !LabelPattern().IsMatch(l)))
            {
                return TypedResults.Problem(title: "Labels are lowercase letters, digits, '.', '_', and '-'.", statusCode: 400);
            }

            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = clock.GetUtcNow();
            var token = new EnrollmentToken
            {
                Id = Guid.CreateVersion7(),
                TokenHash = EnrollmentService.TokenHash(secret),
                Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                Labels = request.Labels.Distinct().Order().ToArray(),
                MaxConcurrency = Math.Clamp(request.MaxConcurrency ?? 2, 1, 64),
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(Math.Clamp(request.ExpiresInMinutes ?? options.Value.AgentChannel.EnrollmentTokenMinutes, 5, 7 * 24 * 60)),
            };
            db.EnrollmentTokens.Add(token);
            await db.SaveChangesAsync(ct);
            var value = $"opy1.{secret}.{ca.Fingerprint}";
            var server = options.Value.AgentChannel.PublicUrl ?? $"https://{System.Net.Dns.GetHostName()}:{options.Value.AgentChannel.Port}";
            var name = token.Name is null ? "" : $" --name {token.Name}";
            return TypedResults.Ok(new CreatedToken(token.Id, value, $"octopushy agent enroll --server {server} --token {value}{name}", token.ExpiresAt, ca.Fingerprint));
        })
        .WithName("createEnrollmentToken");

        group.MapDelete("/tokens/{id:guid}", async (Guid id, OctopushyDb db, CancellationToken ct) =>
        {
            await db.EnrollmentTokens.Where(t => t.Id == id && t.UsedAt == null).ExecuteDeleteAsync(ct);
            return TypedResults.NoContent();
        })
        .WithName("deleteEnrollmentToken");
    }

    private static void MapVariables(RouteGroupBuilder group)
    {
        group.MapGet("", async (VariableStore store, OctopushyDb db, RevisionCache revisions, CancellationToken ct) =>
        {
            var uses = await UsesAsync(db, revisions, ct);
            var entries = await store.ListAsync(ct);
            var visible = new List<VariableDto>();
            foreach (var entry in entries.Where(e => !e.Key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal)))
            {
                var revoked = await db.StoreVersions.AnyAsync(v => v.Key == entry.Key && v.Version == entry.CurrentVersion && v.RevokedAt != null, ct);
                visible.Add(new VariableDto(
                    entry.Key,
                    entry.Visibility,
                    entry.Description,
                    entry.CurrentVersion,
                    entry.UpdatedAt,
                    entry.Visibility == Visibilities.Plain ? await store.GetPlainAsync(entry.Key, ct) : null,
                    uses.GetValueOrDefault(entry.Key) ?? [],
                    entry.LatestVersion,
                    revoked));
            }

            var known = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var missing = uses.Where(u => !known.Contains(u.Key)).OrderBy(u => u.Key).Select(u => new MissingVariableDto(u.Key, u.Value)).ToList();
            var system = entries.Where(e => e.Key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal))
                .Select(e => new SystemVariableDto(e.Key, e.Visibility, e.UpdatedAt)).ToList();
            return TypedResults.Ok(new VariablesDto(visible, missing, system));
        })
        .WithName("listVariables");

        group.MapPut("", async (VariableUpdate update, RunEngine engine, OrchestratorSignal signal, CancellationToken ct) =>
        {
            await engine.ExecuteAsync(async (db, services) =>
            {
                var store = services.GetRequiredService<VariableStore>();
                if (update.Value is not null)
                {
                    var existing = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == update.Key, ct);
                    await store.SetAsync(update.Key, update.Value, update.Visibility ?? existing?.Visibility ?? Visibilities.Secret, update.Description, ct);
                }
                else
                {
                    await store.DescribeAsync(update.Key, update.Description, ct);
                }
                return true;
            }, ct);

            // Steps blocked on a missing secret can continue.
            signal.Wake();
            return TypedResults.NoContent();
        })
        .WithName("setVariable");

        group.MapDelete("", async (string key, RunEngine engine, RevisionCache revisions, CancellationToken ct) =>
        {
            await engine.ExecuteAsync(async (db, services) =>
            {
                var uses = await UsesAsync(db, revisions, ct);
                await services.GetRequiredService<VariableStore>().DeleteAsync(key, uses.GetValueOrDefault(key) ?? [], ct);
                return true;
            }, ct);
            return TypedResults.NoContent();
        })
        .WithName("deleteVariable");

        group.MapPost("/candidates", async (VariableCandidateRequest request, RunEngine engine, CancellationToken ct) =>
        {
            var version = await engine.ExecuteAsync(async (_, services) =>
                await services.GetRequiredService<VariableStore>().StageAsync(request.Key, request.Value, request.Description, ct), ct);
            return TypedResults.Ok(new VariableCandidateDto(version));
        })
        .WithName("stageVariable");

        group.MapPost("/promote", async (VariablePromotionRequest request, HttpContext http, RunEngine engine, OrchestratorSignal signal, CancellationToken ct) =>
        {
            await engine.ExecuteAsync(async (_, services) =>
            {
                await services.GetRequiredService<VariableStore>().PromoteAsync(
                    request.Key, request.Version, request.ExpectedCurrentVersion, request.VerificationNote,
                    http.User.Identity?.Name ?? "administrator", ct);
                return true;
            }, ct);
            signal.Wake();
            return TypedResults.NoContent();
        })
        .WithName("promoteVariable");

        group.MapPost("/revoke", async (VariableRevocationRequest request, HttpContext http, RunEngine engine, OrchestratorSignal signal, CancellationToken ct) =>
        {
            await engine.ExecuteAsync(async (_, services) =>
            {
                await services.GetRequiredService<VariableStore>().RevokeAsync(
                    request.Key, request.Version, request.Reason, http.User.Identity?.Name ?? "administrator", ct);
                return true;
            }, ct);
            signal.Wake();
            return TypedResults.NoContent();
        })
        .WithName("revokeVariable");

        group.MapGet("/versions", async (string key, OctopushyDb db, RevisionCache revisions, CancellationToken ct) =>
        {
            VariableStore.CheckUserKey(key);
            var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, ct)
                ?? throw new StoreException($"There is no entry '{key}'.");
            var versions = await db.StoreVersions.AsNoTracking().Where(v => v.Key == key).OrderByDescending(v => v.Version).ToListAsync(ct);
            var runs = await db.Runs.AsNoTracking().Select(r => new { r.Id, r.Number, r.Context, r.Status }).ToListAsync(ct);
            var releases = await (from release in db.SecretResolutions.AsNoTracking()
                                  join attempt in db.Attempts on release.AttemptId equals attempt.Id
                                  where release.Key == key
                                  select new { attempt.RunId, release.Version }).Distinct().ToListAsync(ct);
            var uses = new Dictionary<int, List<string>>();
            void Add(int version, string use)
            {
                if (!uses.TryGetValue(version, out var list))
                {
                    uses[version] = list = [];
                }
                if (!list.Contains(use))
                {
                    list.Add(use);
                }
            }
            foreach (var run in runs)
            {
                var released = releases.Where(r => r.RunId == run.Id).ToList();
                foreach (var release in released)
                {
                    Add(release.Version, $"run #{run.Number} ({run.Status}; resolved for assignment)");
                }
                var context = RunContext.Parse(run.Context);
                foreach (var (name, reference) in context.Secrets.Where(s => s.Value == key))
                {
                    if (context.SecretVersions.TryGetValue(name, out var pinned))
                    {
                        Add(pinned, $"run #{run.Number} ({run.Status})");
                    }
                    else if (context.RequestedSecretVersions.TryGetValue(name, out var requested))
                    {
                        Add(requested, $"run #{run.Number} ({run.Status}; requested)");
                    }
                    else if (released.Count == 0 && run.Status is not (RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled))
                    {
                        Add(entry.CurrentVersion, $"run #{run.Number} ({run.Status}; not yet pinned)");
                    }
                }
            }
            var revision = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
            if (revision is not null)
            {
                var repository = await revisions.GetAsync(revision.Commit, ct);
                foreach (var use in repository.StoreUses.Where(u => u.Key == key))
                {
                    Add(use.Version ?? entry.CurrentVersion, $"definition {use.Consumer}");
                }
            }
            return TypedResults.Ok(versions.Select(v => new VariableVersionDto(
                v.Version,
                v.RevokedAt is not null ? "revoked" : v.Version == entry.CurrentVersion ? "active" : v.ActivatedAt is null ? "candidate" : "previous",
                v.CreatedAt, v.ActivatedAt, v.ActivatedBy, v.ActivationNote,
                v.RevokedAt, v.RevokedBy, v.RevocationNote,
                uses.GetValueOrDefault(v.Version) ?? [])).ToList());
        })
        .WithName("listVariableVersions");
    }

    /// <summary>Who uses each store key: definitions in the latest revision, and runs still in progress.</summary>
    private static async Task<Dictionary<string, List<string>>> UsesAsync(OctopushyDb db, RevisionCache revisions, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string consumer)
        {
            if (!result.TryGetValue(key, out var list))
            {
                result[key] = list = [];
            }

            if (!list.Contains(consumer))
            {
                list.Add(consumer);
            }
        }

        var revision = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (revision is not null)
        {
            var repository = await revisions.GetAsync(revision.Commit, ct);
            foreach (var use in repository.StoreUses)
            {
                Add(use.Key, use.Consumer);
            }
        }

        var active = await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.Number, r.Context })
            .ToListAsync(ct);
        foreach (var run in active)
        {
            foreach (var key in RunContext.Parse(run.Context).Secrets.Values)
            {
                Add(key, $"run #{run.Number}");
            }
        }

        return result;
    }

    private static async Task<RepositoryDto> RepositoryAsync(OctopushyDb db, Views views, CancellationToken ct)
    {
        var settings = await db.RepositorySettings.AsNoTracking().Where(r => r.Id == 1).FirstOrDefaultAsync(ct) ?? new RepositorySettings();
        var credentialSet = await db.StoreEntries.AsNoTracking().AnyAsync(e => e.Key == RepositorySync.CredentialKey, ct);
        return new RepositoryDto(
            settings.Url,
            settings.Branch,
            settings.PollSeconds,
            settings.CredentialKind,
            settings.CredentialUsername,
            credentialSet,
            settings.LastFetchAt,
            settings.LastFetchError,
            settings.HeadCommit,
            await views.LatestRevisionAsync(ct));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex LabelPattern();
}
