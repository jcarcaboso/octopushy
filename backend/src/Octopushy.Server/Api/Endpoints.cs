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
        MapRuns(api);
        MapAgents(api.MapGroup("/agents").WithTags("Agents"));
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
            RepositoryUpdate update, OctopushyDb db, Views views, VariableStore store, RepositorySync sync, TimeProvider clock, CancellationToken ct) =>
        {
            if (update.CredentialKind is not ("none" or "token" or "ssh"))
            {
                return TypedResults.Problem(title: "The credential kind is none, token, or ssh.", statusCode: 400);
            }

            if (update.PollSeconds is < 10 or > 86400)
            {
                return TypedResults.Problem(title: "Poll between every 10 seconds and once a day.", statusCode: 400);
            }

            var settings = await db.RepositorySettings.Where(r => r.Id == 1).FirstOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = new RepositorySettings();
                db.RepositorySettings.Add(settings);
            }

            var url = string.IsNullOrWhiteSpace(update.Url) ? null : update.Url.Trim();
            if (settings.Url != url || settings.Branch != update.Branch.Trim())
            {
                settings.HeadCommit = null;
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
        })
        .WithName("updateRepository");

        group.MapPost("/sync", (RepositorySync sync) =>
        {
            sync.Trigger();
            return TypedResults.Accepted((string?)null);
        })
        .WithName("syncRepository");

        group.MapGet("/revisions", async (OctopushyDb db, int? limit, CancellationToken ct) =>
            TypedResults.Ok((await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).Take(Math.Clamp(limit ?? 30, 1, 200)).ToListAsync(ct))
                .Select(Views.Revision).ToList()))
            .WithName("listRevisions");

        group.MapGet("/revisions/{id:guid}", async Task<Results<Ok<RevisionDetailDto>, NotFound>> (Guid id, OctopushyDb db, CancellationToken ct) =>
            await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } revision
                ? TypedResults.Ok(new RevisionDetailDto(Views.Revision(revision), Views.Issues(revision.Issues)))
                : TypedResults.NotFound())
            .WithName("getRevision");
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
                    ? new ApprovalDto(s.ApprovalRequired, s.Decision, s.DecidedBy, s.DecidedAt, s.DecisionNote, s.PlanSha256)
                    : null,
                s.Attempts.OrderBy(a => a.Number).Select(a => new AttemptDto(
                    a.Id, a.Number, a.Executor, a.Action, a.Status, a.WaitingReason, a.AgentId,
                    a.AgentId is { } agent ? agents.GetValueOrDefault(agent) : null,
                    a.CreatedAt, a.AssignedAt, a.StartedAt, a.FinishedAt, a.Error, a.LateResult, a.LogLines)).ToList()))
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
                run.Status == RunStatuses.Uncertain));
        })
        .WithName("getRun");

        group.MapPost("/{id:guid}/steps/{stepId:guid}/approve", async (Guid id, Guid stepId, DecisionRequest request, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.ApproveAsync(id, stepId, request.PlanSha256 ?? "", http.User.Identity!.Name!, request.Note, ct);
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
                a.Version, a.Executors, a.ToolRuntime, a.CertificateExpiresAt, running.GetValueOrDefault(a.Id))).ToList());
        })
        .WithName("listAgents");

        group.MapPatch("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, AgentUpdate update, OctopushyDb db, AgentConnections connections, OrchestratorSignal signal, CancellationToken ct) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (agent is null)
            {
                return TypedResults.NotFound();
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

            await db.SaveChangesAsync(ct);
            if (agent.Disabled)
            {
                connections.Disconnect(agent.Id);
            }

            signal.Wake();
            return TypedResults.NoContent();
        })
        .WithName("updateAgent");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, OctopushyDb db, AgentConnections connections, CancellationToken ct) =>
        {
            if (await db.Attempts.AnyAsync(a => a.AgentId == id && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running), ct))
            {
                return TypedResults.Problem(title: "The agent is running attempts; disable it and wait for them to finish.", statusCode: 409);
            }

            connections.Disconnect(id);
            await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
            return TypedResults.NoContent();
        })
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
                visible.Add(new VariableDto(
                    entry.Key,
                    entry.Visibility,
                    entry.Description,
                    entry.CurrentVersion,
                    entry.UpdatedAt,
                    entry.Visibility == Visibilities.Plain ? await store.GetPlainAsync(entry.Key, ct) : null,
                    uses.GetValueOrDefault(entry.Key) ?? []));
            }

            var known = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var missing = uses.Where(u => !known.Contains(u.Key)).OrderBy(u => u.Key).Select(u => new MissingVariableDto(u.Key, u.Value)).ToList();
            var system = entries.Where(e => e.Key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal))
                .Select(e => new SystemVariableDto(e.Key, e.Visibility, e.UpdatedAt)).ToList();
            return TypedResults.Ok(new VariablesDto(visible, missing, system));
        })
        .WithName("listVariables");

        group.MapPut("", async (VariableUpdate update, VariableStore store, OctopushyDb db, OrchestratorSignal signal, CancellationToken ct) =>
        {
            if (update.Value is not null)
            {
                var existing = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == update.Key, ct);
                await store.SetAsync(update.Key, update.Value, update.Visibility ?? existing?.Visibility ?? Visibilities.Secret, update.Description, ct);
            }
            else
            {
                await store.DescribeAsync(update.Key, update.Description, ct);
            }

            // Steps blocked on a missing secret can continue.
            signal.Wake();
            return TypedResults.NoContent();
        })
        .WithName("setVariable");

        group.MapDelete("", async (string key, VariableStore store, OctopushyDb db, RevisionCache revisions, CancellationToken ct) =>
        {
            var uses = await UsesAsync(db, revisions, ct);
            await store.DeleteAsync(key, uses.GetValueOrDefault(key) ?? [], ct);
            return TypedResults.NoContent();
        })
        .WithName("deleteVariable");
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
