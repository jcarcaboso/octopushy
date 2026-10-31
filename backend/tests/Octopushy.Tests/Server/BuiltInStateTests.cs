using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class BuiltInStateTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Secret = "a-password-inside-the-state";

    private static Artifact State(long serial, string lineage = "lineage-1", int resources = 1)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 4,
            serial,
            lineage,
            secret = Secret,
            resources = Enumerable.Range(0, resources).Select(i => new { name = $"r{i}" }),
        });
        return new Artifact { Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }

    private static JsonObject Applied() => new() { ["outputs"] = new JsonObject() };

    private static Task<ServerHarness> StartAsync(PostgresFixture postgres, TestRepository? repository = null) =>
        ServerHarness.StartAsync(postgres, repository ?? TestRepository.BuiltInState());

    private static Task<TofuState?> LockAsync(ServerHarness server, Guid resource) =>
        server.QueryAsync(db => db.TofuStates.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resource));

    private static Task<List<TofuStateVersion>> VersionsAsync(ServerHarness server, Guid resource) =>
        server.QueryAsync(db => db.TofuStateVersions.AsNoTracking().Where(v => v.ResourceId == resource).OrderBy(v => v.Version).ToListAsync());

    private static async Task<Assignment> PlanToApplyAsync(ServerHarness server, FakeAgent agent)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(apply.AttemptId), CancellationToken.None);
        return apply;
    }

    private static async Task<Guid> ApplyOnceAsync(ServerHarness server, FakeAgent agent, long serial = 1)
    {
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), Outcome.Succeeded, Applied(), artifacts: State(serial));
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();
        return (await server.ResourceAsync(Guest)).Id;
    }

    private static async Task<Assignment> ReplanAsync(ServerHarness server, FakeAgent agent, Guid resource)
    {
        await server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>().PlanGuestAsync(resource, CancellationToken.None);
        return await agent.NextAsync("preview");
    }

    private static async Task ExpireLeaseAsync(ServerHarness server, Assignment assignment)
    {
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(assignment.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
    }

    [Fact]
    public async Task A_lab_without_a_backend_provisions_and_the_next_plan_gets_the_state()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var plan = await agent.NextAsync("preview");
        var input = JsonNode.Parse(plan.InputJson)!;
        Assert.Equal(BackendContext.BuiltInType, input["backend"]!["type"]!.GetValue<string>());
        Assert.Null(input["backend"]!["workspace"]);
        Assert.Empty(input["backend"]!["config"]!.AsObject());
        Assert.Equal(["PROXMOX_VE_API_TOKEN"], input["secretEnv"]!.AsObject().Select(p => p.Key));
        var resource = (await server.ResourceAsync(Guest)).Id;
        var seed = Assert.Single(plan.Artifacts);
        var seeded = JsonNode.Parse(seed.Content.ToByteArray())!;
        Assert.Equal((0, resource.ToString()), (seeded["serial"]!.GetValue<int>(), seeded["lineage"]!.GetValue<string>()));
        Assert.Equal(ArtifactStore.Sha256(seed.Content.Span), seed.Sha256);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());

        var apply = await agent.NextAsync("apply");
        Assert.Contains(apply.Artifacts, a => a.Name == AssignmentBuilder.PlanArtifact);
        Assert.Equal(seed.Content, apply.Artifacts.Single(a => a.Name == TofuStateStore.ArtifactName).Content);
        Assert.Equal(Guid.Parse(apply.AttemptId), (await LockAsync(server, resource))!.LockAttemptId);

        await agent.ReportAsync(apply, Outcome.Succeeded, Applied(), artifacts: State(1));
        Assert.Null((await LockAsync(server, resource))!.LockAttemptId);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        var version = Assert.Single(await VersionsAsync(server, resource));
        Assert.Equal((1, 1L, "lineage-1", 1), (version.Version, version.Serial, version.Lineage, version.ResourceCount));
        Assert.Equal(Guid.Parse(apply.AttemptId), version.AttemptId);
        // Encrypted at rest, and never written to the artifact files outside the database dump.
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(version.Ciphertext));
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "artifacts", apply.AttemptId)));

        var next = await ReplanAsync(server, agent, resource);
        var sent = Assert.Single(next.Artifacts);
        Assert.Equal(State(1).Content, sent.Content);
        Assert.Equal(version.Sha256, sent.Sha256);
    }

    [Fact]
    public async Task A_plan_and_a_foreign_agent_cannot_write_state()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var other = await server.ConnectAgentAsync("elsewhere");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        Assert.Empty(other.TakeAssignments());

        await server.Engine.OnStateSnapshotAsync(agent.Id, Guid.Parse(plan.AttemptId), State(9), CancellationToken.None);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: [FakeAgent.PlanArtifact(), State(9)]);
        Assert.Equal(0, await server.QueryAsync(db => db.TofuStateVersions.CountAsync()));

        var apply = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(apply.AttemptId), CancellationToken.None);
        await server.Engine.OnStateSnapshotAsync(other.Id, Guid.Parse(apply.AttemptId), State(9), CancellationToken.None);
        await other.ReportAsync(apply, Outcome.Succeeded, Applied(), artifacts: State(9));
        Assert.Equal(0, await server.QueryAsync(db => db.TofuStateVersions.CountAsync()));
        Assert.NotNull((await LockAsync(server, (await server.ResourceAsync(Guest)).Id))!.LockAttemptId);
    }

    [Fact]
    public async Task An_untrusted_endpoint_gets_no_state()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ApplyOnceAsync(server, agent);

        var path = "labs/home/proxmox/korriban/installation.yaml";
        server.Repository.Write(path, File.ReadAllText(Path.Combine(server.Repository.Root, path)).Replace("https://10.1.0.10:8006/", "https://10.1.0.99:8006/"));
        server.Commit("Move the Proxmox endpoint");
        await server.SyncAsync();
        for (var i = 0; i < 3; i++)
        {
            await server.TickAsync();
        }

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Artifacts.Any(x => x.Name == TofuStateStore.ArtifactName));
        await server.SyncAndTrustAsync();
        Assert.Single((await agent.NextAsync("preview")).Artifacts);
    }

    [Fact]
    public async Task Snapshots_replace_each_other_and_earlier_applies_stay_recoverable()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());

        var apply = await agent.NextAsync("apply");
        Assert.Contains(apply.Artifacts, a => a.Name == TofuStateStore.ArtifactName);
        var attempt = Guid.Parse(apply.AttemptId);
        await server.Engine.OnStateSnapshotAsync(agent.Id, attempt, State(2), CancellationToken.None);
        await server.Engine.OnStateSnapshotAsync(agent.Id, attempt, State(3), CancellationToken.None);
        var during = await VersionsAsync(server, resource);
        Assert.Equal([1L, 3L], during.Select(v => v.Serial));

        await agent.ReportAsync(apply, Outcome.Failed, error: "a provider failed", artifacts: State(4));
        var after = await VersionsAsync(server, resource);
        Assert.Equal([(1, 1L), (2, 4L)], after.Select(v => (v.Version, v.Serial)));
        Assert.Null((await LockAsync(server, resource))!.LockAttemptId);
    }

    [Fact]
    public async Task A_first_apply_that_changed_nothing_stores_nothing()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await agent.ReportAsync(apply, Outcome.Failed, error: "the provider failed at once", artifacts: plan.Artifacts.Single());
        Assert.Equal(0, await server.QueryAsync(db => db.TofuStateVersions.CountAsync()));
        Assert.Null((await LockAsync(server, (await server.ResourceAsync(Guest)).Id))!.LockAttemptId);
    }

    [Fact]
    public async Task A_finished_apply_with_an_unchanged_state_adds_no_version()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, Applied(), artifacts: State(1));
        Assert.Single(await VersionsAsync(server, resource));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":4,"serial":-1,"lineage":"l"}""")]
    [InlineData("""{"version":4,"serial":1}""")]
    [InlineData("""{"version":4,"serial":"1","lineage":"lineage-1"}""")]
    public async Task A_finished_apply_with_an_invalid_state_is_uncertain_and_keeps_the_last_good_one(string content)
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());

        var bytes = Encoding.UTF8.GetBytes(content);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, Applied(), artifacts: new Artifact
        {
            Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes),
        });

        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Contains("not saved", run.Steps.Single(s => s.Name == StepNames.Apply).Detail);
        Assert.Single(await VersionsAsync(server, resource));
    }

    [Theory]
    [InlineData(5L, "another-lineage")]
    [InlineData(1L, "lineage-1")]
    public async Task A_state_from_another_lineage_or_an_older_serial_is_refused(long serial, string lineage)
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent, serial: 3);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());

        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, Applied(), artifacts: State(serial, lineage));
        Assert.Equal(AttemptStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.Provision)).Steps
            .Single(s => s.Name == StepNames.Apply).Attempts.Single().Status);
        Assert.Equal(3L, Assert.Single(await VersionsAsync(server, resource)).Serial);
    }

    [Fact]
    public async Task A_finished_apply_without_its_state_is_uncertain()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, Applied());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_started_apply_that_failed_without_its_state_is_uncertain()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(apply.AttemptId), CancellationToken.None);
        await agent.ReportAsync(apply, Outcome.Failed, error: "the provider failed");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.Single(await VersionsAsync(server, resource));
    }

    [Fact]
    public async Task An_apply_that_never_started_can_fail_without_a_state()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await server.Engine.OnResultAsync(agent.Id, new AttemptResult { AttemptId = apply.AttemptId, Outcome = Outcome.Failed, Error = "the agent is shutting down" }, CancellationToken.None);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_running_apply_keeps_plans_of_the_stack_waiting()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);

        var holder = Guid.CreateVersion7();
        await server.WithDbAsync(async db =>
        {
            db.Attempts.Add(new Attempt { Id = holder, Executor = "opentofu", Action = "apply", Status = AttemptStatuses.Running, CreatedAt = DateTimeOffset.UtcNow, LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await db.TofuStates.Where(s => s.ResourceId == resource).ExecuteUpdateAsync(u => u.SetProperty(s => s.LockAttemptId, holder));
            await db.SaveChangesAsync();
        });
        await server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>().PlanGuestAsync(resource, CancellationToken.None);
        await server.TickAsync();
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("state lock", run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single().WaitingReason);

        // The holder crashes: nothing has to clear the lock for the stack to move again.
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == holder)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, AttemptStatuses.Uncertain)));
        Assert.Equal("preview", (await agent.NextAsync("preview")).Action);
    }

    [Fact]
    public async Task A_crashed_apply_can_report_late_until_another_apply_takes_its_lock()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        var crashed = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(crashed.AttemptId), CancellationToken.None);
        await ExpireLeaseAsync(server, crashed);

        // Still the last writer, so what it did is kept.
        await server.Engine.OnStateSnapshotAsync(agent.Id, Guid.Parse(crashed.AttemptId), State(2), CancellationToken.None);
        Assert.Equal(2L, (await VersionsAsync(server, resource))[^1].Serial);

        var uncertain = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, uncertain.Status);
        await server.Engine.CloseUncertainAsync(uncertain.Id, "ops", CancellationToken.None);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        var next = await agent.NextAsync("apply");
        Assert.Equal(Guid.Parse(next.AttemptId), (await LockAsync(server, resource))!.LockAttemptId);
        Assert.Equal(State(2).Content, next.Artifacts.Single(a => a.Name == TofuStateStore.ArtifactName).Content);

        // Now it has lost: neither its snapshots nor its late result reach the state.
        await server.Engine.OnStateSnapshotAsync(agent.Id, Guid.Parse(crashed.AttemptId), State(50), CancellationToken.None);
        await agent.ReportAsync(crashed, Outcome.Succeeded, Applied(), artifacts: State(51));
        Assert.Equal(2L, (await VersionsAsync(server, resource))[^1].Serial);
        Assert.Equal(Guid.Parse(next.AttemptId), (await LockAsync(server, resource))!.LockAttemptId);

        await agent.ReportAsync(next, Outcome.Succeeded, Applied(), artifacts: State(3));
        Assert.Equal(3L, (await VersionsAsync(server, resource))[^1].Serial);
    }

    [Fact]
    public async Task A_plan_is_not_applied_after_the_state_moved_on()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        var plan = await ReplanAsync(server, agent, resource);
        await server.WithDbAsync(db => db.TofuStates.Where(s => s.ResourceId == resource)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.HeadVersion, 2)));
        await server.WithDbAsync(async db =>
        {
            var head = await db.TofuStateVersions.AsNoTracking().SingleAsync(v => v.ResourceId == resource);
            db.TofuStateVersions.Add(new TofuStateVersion
            {
                Id = Guid.CreateVersion7(), ResourceId = resource, Version = 2, Serial = 9, Lineage = head.Lineage,
                Sha256 = head.Sha256, Ciphertext = head.Ciphertext, WrappedKey = head.WrappedKey,
            });
            await db.SaveChangesAsync();
        });
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Plan again", run.Steps.Single(s => s.Name == StepNames.Apply).Detail);
    }

    [Fact]
    public async Task An_older_agent_is_not_given_built_in_state_work()
    {
        await using var server = await StartAsync(postgres);
        var older = await server.ConnectOlderAgentAsync("home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();
        Assert.DoesNotContain(older.TakeAssignments(), a => a.Executor == "opentofu");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("built-in OpenTofu state", run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single().WaitingReason);

        var current = await server.ConnectAgentAsync("home");
        Assert.Equal("preview", (await current.NextAsync("preview")).Action);
    }

    [Fact]
    public async Task A_rename_keeps_the_state()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);

        var source = File.ReadAllText(Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml"));
        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Repository.Write("labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml", source + "\nrenamedFrom: krb-app-01\n");
        server.Commit("Rename the guest");
        await server.SyncAndTrustAsync();

        var plan = await ReplanAsync(server, agent, resource);
        Assert.Equal("home.korriban.krb-app-02", JsonNode.Parse(plan.InputJson)!["stack"]!.GetValue<string>());
        Assert.Equal(State(1).Content, Assert.Single(plan.Artifacts).Content);
        Assert.Equal(resource, (await server.ResourceAsync("home/korriban/krb-app-02")).Id);
    }

    [Fact]
    public async Task Removing_an_external_backend_from_an_applied_guest_is_refused_rather_than_planned_empty()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), Outcome.Succeeded, Applied());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();

        var path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(path, File.ReadAllText(Path.Combine(server.Repository.Root, path)).Replace("  state: { backend: postgres }\n", ""));
        server.Commit("Drop the backend");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Restore provision.state", run.Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Theory]
    [InlineData(Outcome.Failed)]
    [InlineData(Outcome.Uncertain)]
    public async Task Removing_an_external_backend_after_an_unfinished_apply_is_refused(Outcome outcome)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), outcome, error: "stopped after creating some resources");
        await server.TickAsync();
        var first = await server.LatestRunAsync(Guest, Operations.Provision);
        if (first.Status == RunStatuses.Uncertain)
        {
            await server.Engine.CloseUncertainAsync(first.Id, "ops", CancellationToken.None);
        }

        var path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(path, File.ReadAllText(Path.Combine(server.Repository.Root, path)).Replace("  state: { backend: postgres }\n", ""));
        server.Commit("Drop the backend");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        Assert.Contains("Restore provision.state", (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Fact]
    public async Task An_empty_built_in_version_does_not_hide_a_later_external_apply()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), Outcome.Succeeded, Applied(), artifacts: State(1, resources: 0));
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();

        var path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var builtIn = File.ReadAllText(Path.Combine(server.Repository.Root, path));
        server.Repository.Write("octopushy.yaml", "schemaVersion: 1\nstateBackends:\n  postgres: { type: pg }\n");
        server.Repository.Write(path, builtIn.Replace("  recipe: test-vm\n", "  recipe: test-vm\n  state: { backend: postgres }\n"));
        server.Commit("Add a backend");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), Outcome.Failed, error: "stopped after creating some resources");
        await server.TickAsync();

        server.Repository.Write(path, builtIn);
        server.Commit("Drop the backend");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        Assert.Contains("Restore provision.state", (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Fact]
    public async Task Moving_to_an_external_backend_after_an_unfinished_built_in_apply_is_refused()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await PlanToApplyAsync(server, agent), Outcome.Uncertain, error: "the agent restarted");
        await server.TickAsync();
        await server.Engine.CloseUncertainAsync((await server.LatestRunAsync(Guest, Operations.Provision)).Id, "ops", CancellationToken.None);

        server.Repository.Write("octopushy.yaml", "schemaVersion: 1\nstateBackends:\n  postgres: { type: pg }\n");
        var path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(path, File.ReadAllText(Path.Combine(server.Repository.Root, path)).Replace("  recipe: test-vm\n", "  recipe: test-vm\n  state: { backend: postgres }\n"));
        server.Commit("Add a backend");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        Assert.Contains("Remove provision.state", (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Fact]
    public async Task Moving_a_guest_with_built_in_state_to_an_external_backend_is_refused()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ApplyOnceAsync(server, agent);

        server.Repository.Write("octopushy.yaml", "schemaVersion: 1\nstateBackends:\n  postgres: { type: pg }\n");
        var path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(path, File.ReadAllText(Path.Combine(server.Repository.Root, path)).Replace("  recipe: test-vm\n", "  recipe: test-vm\n  state: { backend: postgres }\n"));
        server.Commit("Add a backend");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Remove provision.state", run.Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Fact]
    public async Task The_api_shows_versions_without_content_and_restores_an_earlier_one()
    {
        await using var server = await StartAsync(postgres);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, Applied(), artifacts: State(7, resources: 0));
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();

        var response = await client.GetAsync($"/api/guests/{resource}/state");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("ciphertext", body, StringComparison.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<TofuStateDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, state.HeadVersion);
        Assert.False(state.LockHeld);
        Assert.Equal([2, 1], state.Versions.Select(v => v.Version));

        var conflict = await client.PostAsJsonAsync($"/api/guests/{resource}/state/rollback", new TofuStateRollback(1, ExpectedHead: 1));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var restored = await client.PostAsJsonAsync($"/api/guests/{resource}/state/rollback", new TofuStateRollback(1, ExpectedHead: 2));
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);

        var versions = await VersionsAsync(server, resource);
        Assert.Equal((3, 8L, 1, 1), (versions[^1].Version, versions[^1].Serial, versions[^1].RestoredFrom, versions[^1].ResourceCount));
        var next = await ReplanAsync(server, agent, resource);
        var document = JsonNode.Parse(Assert.Single(next.Artifacts).Content.ToByteArray())!;
        Assert.Equal(8, document["serial"]!.GetValue<int>());
        Assert.Equal(Secret, document["secret"]!.GetValue<string>());
        await agent.ReportAsync(next, Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());

        // An apply is in flight: nothing may change the state underneath it.
        var apply = await agent.NextAsync("apply");
        var busy = await client.PostAsJsonAsync($"/api/guests/{resource}/state/rollback", new TofuStateRollback(2, ExpectedHead: 3));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.True((await client.GetFromJsonAsync<TofuStateDto>($"/api/guests/{resource}/state"))!.LockHeld);
        await agent.ReportAsync(apply, Outcome.Succeeded, Applied(), artifacts: State(9));
    }

    [Fact]
    public async Task Restoring_a_version_fences_off_an_apply_that_crashed_before_it()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await agent.ReportAsync(await ReplanAsync(server, agent, resource), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        var crashed = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(crashed.AttemptId), CancellationToken.None);
        await server.Engine.OnStateSnapshotAsync(agent.Id, Guid.Parse(crashed.AttemptId), State(2), CancellationToken.None);
        await ExpireLeaseAsync(server, crashed);
        await server.Engine.CloseUncertainAsync((await server.LatestRunAsync(Guest, Operations.Provision)).Id, "ops", CancellationToken.None);

        await server.Engine.ExecuteAsync(async (_, services) =>
        {
            await services.GetRequiredService<TofuStateStore>().RollBackAsync(resource, 1, 2, "ops", CancellationToken.None);
            return true;
        }, CancellationToken.None);
        await server.Engine.OnStateSnapshotAsync(agent.Id, Guid.Parse(crashed.AttemptId), State(50), CancellationToken.None);
        Assert.Equal((3, 3L), ((await VersionsAsync(server, resource))[^1].Version, (await VersionsAsync(server, resource))[^1].Serial));
    }

    [Fact]
    public async Task A_state_that_cannot_be_decrypted_fails_its_guest_without_stopping_dispatch()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        await server.WithDbAsync(async db =>
        {
            var head = await db.TofuStateVersions.SingleAsync(v => v.ResourceId == resource);
            head.Ciphertext[^1] ^= 1;
            db.Entry(head).Property(v => v.Ciphertext).IsModified = true;
            await db.SaveChangesAsync();
        });

        await server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>().PlanGuestAsync(resource, CancellationToken.None);
        await server.TickAsync();
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "opentofu");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("cannot be decrypted", run.Detail);
    }

    [Fact]
    public async Task Only_the_newest_versions_are_retained()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        var attempt = Guid.CreateVersion7();
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<TofuStateStore>();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        for (var serial = 2; serial <= TofuStateStore.RetainedVersions + 5; serial++)
        {
            var state = await db.TofuStates.SingleAsync(s => s.ResourceId == resource);
            state.LockAttemptId = Guid.CreateVersion7();
            Assert.Null(await store.WriteAsync(state.LockAttemptId.Value, resource, State(serial).Content.ToByteArray(), CancellationToken.None));
            await db.SaveChangesAsync();
        }

        var kept = await VersionsAsync(server, resource);
        Assert.Equal(TofuStateStore.RetainedVersions, kept.Count);
        Assert.Equal(TofuStateStore.RetainedVersions + 5, kept[^1].Version);
    }

    [Fact]
    public async Task A_dump_with_the_original_key_restores_state_and_another_key_cannot_read_it()
    {
        await using var server = await StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = await ApplyOnceAsync(server, agent);
        var connection = await server.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        var dump = await postgres.DumpAsync(connection);

        await using var restored = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.RestoreAsync(dump)).UseSnakeCaseNamingConvention().Options);
        var version = await restored.TofuStateVersions.SingleAsync(v => v.ResourceId == resource);
        var context = $"tofu-state\0{resource}\0{version.Id}";
        var key = new KeyEncryptionKey(Options.Create(new OctopushyOptions { DataDirectory = server.DataDirectory }), NullLogger<KeyEncryptionKey>.Instance);
        Assert.Equal(State(1).Content.ToByteArray(), new Envelope(key).Open(version.Ciphertext, version.WrappedKey, context));
        var wrong = new Envelope(new KeyEncryptionKey(RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size)));
        Assert.ThrowsAny<CryptographicException>(() => wrong.Open(version.Ciphertext, version.WrappedKey, context));

        // Only state makes an installation non-empty here, and that alone refuses a new key.
        await restored.StoreVersions.ExecuteDeleteAsync();
        await restored.StoreEntries.ExecuteDeleteAsync();
        var data = Directory.CreateTempSubdirectory("octopushy-state-key-").FullName;
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KeyEncryptionKey.InitializeAsync(
                restored, new OctopushyOptions { DataDirectory = data }, NullLogger.Instance, CancellationToken.None));
            Assert.Contains("OpenTofu state", error.Message);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
