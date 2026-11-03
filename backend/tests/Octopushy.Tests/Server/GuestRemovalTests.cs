using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>Deleting a guest's folder removes the guest.</summary>
[Collection(PostgresCollection.Name)]
public class GuestRemovalTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Folder = "labs/home/proxmox/korriban/guests/krb-app-01";

    private static async Task<(ServerHarness Server, FakeAgent Agent, string Applied)> ProvisionedAsync(PostgresFixture postgres)
    {
        var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var applied = (await server.LatestRunAsync(Guest)).Commit!;
        return (server, agent, applied);
    }

    [Fact]
    public async Task A_deleted_folder_destroys_the_guest_from_its_applied_definition()
    {
        var (server, agent, applied) = await ProvisionedAsync(postgres);
        await using var _ = server;
        server.Repository.Delete(Folder);
        server.Commit("Remove the guest");

        await server.SyncAsync();
        Assert.NotNull((await server.ResourceAsync(Guest)).MissingSince);
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(applied, run.Commit);
        var context = RunContext.Parse(run.Context);
        Assert.NotNull(context.DefinitionRemovedIn);
        Assert.NotEqual(applied, context.DefinitionRemovedIn);
        Assert.Equal([StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.VerifyAbsent],
            run.Steps.OrderBy(s => s.Ordinal).Select(s => s.Name));

        var plan = await agent.NextAsync("preview");
        Assert.True(JsonNode.Parse(plan.InputJson)!["destroy"]!.GetValue<bool>());
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        // The example lab's policy is automatic, so the guest's own policy approves the delete.
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded, new JsonObject { ["present"] = false });

        var resource = await server.ResourceAsync(Guest);
        Assert.NotNull(resource.RetiredAt);
        Assert.Null(resource.BoundVmid);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);

        // Later revisions propose nothing more for it.
        server.Commit("Unrelated");
        await server.SyncAndTrustAsync();
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Retire)));
    }

    [Fact]
    public async Task Restoring_the_folder_cancels_a_removal_that_changed_nothing_yet()
    {
        var (server, agent, _) = await ProvisionedAsync(postgres);
        await using var __ = server;
        var definition = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, Folder, "guest.yaml"));
        server.Repository.Delete(Folder);
        server.Commit("Remove the guest");
        await server.SyncAndTrustAsync();
        var removal = await server.LatestRunAsync(Guest, Operations.Retire);
        await agent.NextAsync("preview");

        server.Repository.Write($"{Folder}/guest.yaml", definition.TrimEnd('\n'));
        server.Commit("Keep the guest after all");
        await server.SyncAndTrustAsync();

        var resource = await server.ResourceAsync(Guest);
        Assert.Null(resource.MissingSince);
        Assert.Null(resource.RetiredAt);
        Assert.Equal(RunStatuses.Cancelled, (await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == removal.Id))).Status);
    }

    [Fact]
    public async Task Plan_now_on_a_deleted_guest_proposes_its_removal_again_after_one_was_cancelled()
    {
        var (server, _, applied) = await ProvisionedAsync(postgres);
        await using var __ = server;
        server.Repository.Delete(Folder);
        server.Commit("Remove the guest");
        await server.SyncAndTrustAsync();
        var first = await server.LatestRunAsync(Guest, Operations.Retire);
        await server.Engine.CancelAsync(first.Id, "ops", CancellationToken.None);

        var resource = await server.ResourceAsync(Guest);
        var again = await server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        var run = await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == again));
        Assert.Equal(Operations.Retire, run.Operation);
        Assert.Equal(applied, run.Commit);
        await Assert.ThrowsAsync<RunException>(() =>
            server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>().PlanGuestAsync(resource.Id, CancellationToken.None));
    }
}
