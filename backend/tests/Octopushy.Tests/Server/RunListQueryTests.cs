using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Xunit.Abstractions;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>
/// Alone, because these tests flip <see cref="Views.WholeRuns"/>, which every server in the process reads: no other class
/// runs beside this one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WholeRunsCollection
{
    public const string Name = "whole runs";
}

/// <summary>The polled run lists read only what their summaries show, and still show the same.</summary>
[Collection(WholeRunsCollection.Name)]
public class RunListQueryTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Service = Guest + "/web";
    private const string Database = Guest + "/db";
    private const string Cache = Guest + "/cache";

    [Fact]
    public async Task The_lists_and_pages_answer_as_they_did_when_they_loaded_whole_runs()
    {
        var commands = new Commands();
        var (server, service) = await StartAsync(commands);
        await using var _ = server;
        var client = await server.SignedInClientAsync();
        string[] paths =
        [
            "/api/runs", "/api/runs?limit=3", "/api/runs?status=running", "/api/runs?status=succeeded", $"/api/runs?resourceId={service}",
            "/api/overview", "/api/inventory", $"/api/guests/{(await server.ResourceAsync(Guest)).Id}", $"/api/services/{service}",
            $"/api/installations/{(await server.ResourceAsync(Installation)).Id}",
        ];

        foreach (var path in paths)
        {
            var whole = await ReadAsync(client, path, wholeRuns: true);
            var summarised = await ReadAsync(client, path, wholeRuns: false);
            Assert.Equal(whole, summarised);
        }

        // The runs exercise what a summary derives from steps and attempts.
        var runs = await ReadAsync(client, "/api/runs", wholeRuns: false);
        Assert.Contains("\"waiting\":\"No agent has the label b.\"", runs);
        Assert.Contains("\"waiting\":\"Held before it starts.\"", runs);
        Assert.Contains("\"counts\":{\"create\":1,\"update\":2,\"delete\":0,\"replace\":1}", runs);
        Assert.Contains("\"address\":\"?\"", runs);
        Assert.Contains("\"canCancel\":false", runs);
        Assert.Contains("\"canCancel\":true", runs);
        Assert.Contains("\"failed\":[{", await ReadAsync(client, "/api/overview", wholeRuns: false));
    }

    [Fact]
    public async Task The_polled_lists_read_no_run_context_nor_attempts_they_do_not_show()
    {
        var commands = new Commands();
        var (server, _) = await StartAsync(commands);
        await using var _server = server;
        var client = await server.SignedInClientAsync();
        foreach (var path in new[] { "/api/runs", "/api/overview" })
        {
            // Loading whole runs, as before, fails the same checks.
            commands.Clear();
            await ReadAsync(client, path, wholeRuns: true);
            Assert.Contains(commands.Texts, t => t.Contains("r.context") || t.Contains("r0.context"));

            commands.Clear();
            await ReadAsync(client, path, wholeRuns: false);
            var texts = commands.Texts;
            Assert.Contains(texts, t => t.Contains("FROM runs"));
            Assert.Contains(texts, t => t.Contains("FROM attempts"));
            foreach (var text in texts)
            {
                // No context column of any table: not the run's, not an attempt's.
                Assert.DoesNotMatch(@"\.context\b", text);
                if (!text.Contains("FROM attempts AS a"))
                    continue;

                // Of an attempt, only what a summary reads, and only an unfinished run's attempts that are queued or under way.
                var columns = Regex.Matches(text, @"\ba\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
                Assert.Subset(new HashSet<string> { "id", "step_id", "number", "status", "mutating", "waiting_reason" }, columns);
                Assert.Matches(@"WHERE r0?\.status NOT IN \('succeeded', 'failed', 'cancelled'\) AND \(a\.status = 'queued' OR a\.status = 'assigned' OR a\.status = 'running'\)", text);
                // A step's outputs only for a plan of an unfinished run.
                Assert.Equal(Regex.Count(text, @"\bs\.outputs\b"), Regex.Count(text, @"WHEN s\.name = 'plan' AND r0?\.status <> 'succeeded' AND r0?\.status <> 'failed' AND r0?\.status <> 'cancelled' THEN s\.outputs\b"));
                Assert.DoesNotMatch(@"\bs\.(document|detail|decision_note)\b", text);
            }

            output.WriteLine($"{path}: {texts.Count} queries");
        }
    }

    private static async Task<string> ReadAsync(HttpClient client, string path, bool wholeRuns)
    {
        Views.WholeRuns = wholeRuns;
        try
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        finally
        {
            Views.WholeRuns = false;
        }
    }

    private async Task<(ServerHarness Server, Guid Service)> StartAsync(Commands commands)
    {
        var server = await ServerHarness.StartAsync(postgres, TestRepository.Minimal(), clock: new TestClock { Now = Now },
            services: services => services.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(commands)));
        await server.SyncAndTrustAsync();
        var guest = (await server.ResourceAsync(Guest)).Id;
        var service = Guid.NewGuid();
        var database = Guid.NewGuid();
        var cache = Guid.NewGuid();
        await server.WithDbAsync(async db =>
        {
            await db.Runs.ExecuteDeleteAsync();
            db.Resources.Add(new Resource { Id = service, Address = Service, Kind = ResourceKinds.Service, ParentAddress = Guest, Lab = "home", Valid = true });
            db.Resources.Add(new Resource { Id = database, Address = Database, Kind = ResourceKinds.Service, ParentAddress = Guest, Lab = "home", Valid = true });
            db.Resources.Add(new Resource { Id = cache, Address = Cache, Kind = ResourceKinds.Service, ParentAddress = Guest, Lab = "home", Valid = true });
            db.Runs.AddRange(Runs(guest, service, database, cache));
            await db.SaveChangesAsync();
        });
        return (server, service);
    }

    /// <summary>
    /// Runs of several kinds, finished and not, with plans, approvals and retried attempts, each with a context the
    /// lists never show.
    /// </summary>
    private static IEnumerable<Run> Runs(Guid guest, Guid service, Guid database, Guid cache)
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var context = "{\"composeImages\":{\"baselineCommit\":\"abc\",\"images\":[{\"service\":\"web\",\"before\":\"nginx:1.26\",\"after\":\"nginx:1.27\"}]},"
                      + "\"padding\":\"" + new string('x', 4000) + "\"}";
        const string plan = "{\"hasChanges\":true,\"counts\":{\"create\":1,\"update\":2,\"delete\":0,\"replace\":1},\"planSha256\":\"" + sha + "\","
                            + "\"changes\":[{\"address\":\"proxmox_vm.guest\",\"type\":\"proxmox_vm\",\"name\":\"guest\",\"actions\":[\"update\"],\"attributes\":[]}]}";

        Run Make(Guid resource, string operation, string status, TimeSpan age, string? detail = null, string reason = "manual") => new()
        {
            Id = Guid.NewGuid(),
            ResourceId = resource,
            Operation = operation,
            Reason = reason,
            ReasonDetail = reason == "manual" ? null : "Store values changed: a/b.",
            Status = status,
            Detail = detail,
            Commit = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f",
            Context = context,
            CreatedAt = Now - age,
            StartedAt = status == RunStatuses.Queued ? null : Now - age + TimeSpan.FromSeconds(5),
            FinishedAt = RunStatuses.IsTerminal(status) ? Now - age + TimeSpan.FromMinutes(3) : null,
        };

        Step Add(Run run, string name, string status, string kind = StepKinds.Agent, string? outputs = null, string? planSha = null)
        {
            var step = new Step
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Ordinal = run.Steps.Count,
                Name = name,
                Kind = kind,
                Status = status,
                Outputs = outputs,
                PlanSha256 = planSha,
                Document = planSha is null ? null : "{\"document\":true}",
                ApprovalRequired = kind == StepKinds.Approval,
            };
            run.Steps.Add(step);
            return step;
        }

        void Try(Run run, Step step, string status, bool mutating = false, string? waiting = null) => step.Attempts.Add(new Attempt
        {
            Id = Guid.NewGuid(),
            StepId = step.Id,
            RunId = run.Id,
            Number = step.Attempts.Count + 1,
            Executor = "opentofu",
            Action = step.Name,
            Mutating = mutating,
            Status = status,
            WaitingReason = waiting,
            CreatedAt = run.CreatedAt,
            Error = status == AttemptStatuses.Failed ? "It broke." : null,
            Outputs = "{\"log\":\"" + new string('y', 2000) + "\"}",
        });

        // Succeeded long ago: plan, approval, and an apply that took two attempts.
        var succeeded = Make(guest, Operations.Provision, RunStatuses.Succeeded, TimeSpan.FromDays(10), reason: "revision");
        Try(succeeded, Add(succeeded, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        Add(succeeded, StepNames.Approval, StepStatuses.Succeeded, StepKinds.Approval, planSha: sha);
        var apply = Add(succeeded, StepNames.Apply, StepStatuses.Succeeded);
        Try(succeeded, apply, AttemptStatuses.Failed, mutating: true);
        Try(succeeded, apply, AttemptStatuses.Succeeded, mutating: true);
        yield return succeeded;

        var failed = Make(guest, Operations.Configure, RunStatuses.Failed, TimeSpan.FromHours(9), "The playbook failed.");
        Try(failed, Add(failed, StepNames.Preview, StepStatuses.Succeeded, outputs: "{\"action\":\"deploy\"}"), AttemptStatuses.Succeeded);
        Try(failed, Add(failed, StepNames.Apply, StepStatuses.Failed), AttemptStatuses.Failed, mutating: true);
        yield return failed;

        var superseded = Make(guest, Operations.Provision, RunStatuses.Cancelled, TimeSpan.FromHours(8), "Superseded by 1234567.");
        Try(superseded, Add(superseded, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        Add(superseded, StepNames.Approval, StepStatuses.Cancelled, StepKinds.Approval, planSha: sha);
        yield return superseded;

        // The only run of its service: the overview lists it as failed.
        var failedService = Make(cache, Operations.Pipeline, RunStatuses.Failed, TimeSpan.FromHours(6), "The stage failed.", RunReasons.Secret);
        Try(failedService, Add(failedService, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        Try(failedService, Add(failedService, StepNames.Apply, StepStatuses.Failed), AttemptStatuses.Uncertain, mutating: true);
        yield return failedService;

        var serviceFailed = Make(service, Operations.Configure, RunStatuses.Failed, TimeSpan.FromHours(4), "It failed.");
        Try(serviceFailed, Add(serviceFailed, StepNames.Apply, StepStatuses.Failed), AttemptStatuses.Failed, mutating: true);
        yield return serviceFailed;

        // Queued behind two attempts that wait for an agent, after one that failed: the latest one's reason shows.
        var waiting = Make(service, Operations.Pipeline, RunStatuses.Running, TimeSpan.FromHours(3));
        Try(waiting, Add(waiting, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        Add(waiting, StepNames.Approval, StepStatuses.Succeeded, StepKinds.Approval, planSha: sha);
        var queued = Add(waiting, StepNames.Apply, StepStatuses.Queued);
        Try(waiting, queued, AttemptStatuses.Failed, mutating: true);
        Try(waiting, queued, AttemptStatuses.Queued, mutating: true, waiting: "No agent has the label a.");
        Try(waiting, queued, AttemptStatuses.Queued, mutating: true, waiting: "No agent has the label b.");
        yield return waiting;

        var awaiting = Make(guest, Operations.Provision, RunStatuses.AwaitingApproval, TimeSpan.FromHours(2), "Waiting for approval.");
        Try(awaiting, Add(awaiting, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        Add(awaiting, StepNames.Approval, StepStatuses.AwaitingApproval, StepKinds.Approval, planSha: sha);
        Add(awaiting, StepNames.Apply, StepStatuses.Pending);
        yield return awaiting;

        // Uncertain keeps its plan's counts: it has not finished.
        var uncertain = Make(guest, Operations.Retire, RunStatuses.Uncertain, TimeSpan.FromHours(1), "The agent was lost while it applied.");
        Try(uncertain, Add(uncertain, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        var lost = Add(uncertain, StepNames.Apply, StepStatuses.Uncertain);
        Try(uncertain, lost, AttemptStatuses.Uncertain, mutating: true);
        yield return uncertain;

        var held = Make(service, Operations.Configure, RunStatuses.Queued, TimeSpan.FromMinutes(50), "Held: another run changes the guest.");
        Add(held, StepNames.Preview, StepStatuses.Pending);
        yield return held;

        var pending = Make(database, Operations.Configure, RunStatuses.Running, TimeSpan.FromMinutes(40), "Held before it starts.");
        Add(pending, StepNames.Preview, StepStatuses.Pending);
        yield return pending;

        // Applying: it cannot be cancelled.
        var applying = Make(service, Operations.Configure, RunStatuses.Running, TimeSpan.FromMinutes(30));
        Try(applying, Add(applying, StepNames.Preview, StepStatuses.Succeeded, outputs: "{\"action\":\"deploy\"}"), AttemptStatuses.Succeeded);
        Try(applying, Add(applying, StepNames.Apply, StepStatuses.Running), AttemptStatuses.Running, mutating: true);
        yield return applying;

        var assigned = Make(database, Operations.RotateAccess, RunStatuses.Running, TimeSpan.FromMinutes(25));
        Try(assigned, Add(assigned, StepNames.Apply, StepStatuses.Running), AttemptStatuses.Assigned, mutating: false);
        yield return assigned;

        var blocked = Make(guest, Operations.Provision, RunStatuses.Blocked, TimeSpan.FromMinutes(20), "The installation is not trusted.");
        Add(blocked, StepNames.Plan, StepStatuses.Blocked);
        yield return blocked;

        // A run of a resource that is gone shows "?".
        var orphan = Make(Guid.NewGuid(), Operations.Provision, RunStatuses.Succeeded, TimeSpan.FromMinutes(10));
        Try(orphan, Add(orphan, StepNames.Plan, StepStatuses.Succeeded, outputs: plan), AttemptStatuses.Succeeded);
        yield return orphan;
    }

    private sealed class Commands : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_texts)
                    return [.. _texts];
            }
        }

        public void Clear()
        {
            lock (_texts)
                _texts.Clear();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (_texts)
                _texts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
