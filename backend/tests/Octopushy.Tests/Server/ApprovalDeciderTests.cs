using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>The approval step of a run is decided by its kind's decider, and a kind without one approves nothing.</summary>
public class ApprovalDeciderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";

    private static IEnumerable<string> Kinds() =>
        typeof(RunKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_run_kind_has_one_decider_and_nothing_else_has_one()
    {
        Assert.Equal(Kinds().Order(StringComparer.Ordinal), RunEngine.ApprovalDeciders.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(RunEngine.ApprovalDeciders.Count, RunEngine.ApprovalDeciders.Values.Distinct().Count());
        foreach (var kind in Kinds())
            Assert.Same(RunEngine.ApprovalDeciders[kind], RunEngine.ApprovalDeciderFor(Context(kind)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-kind")]
    [InlineData("Provision")]
    [InlineData(" pipeline")]
    public void A_kind_without_a_decider_is_an_error(string? kind)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => RunEngine.ApprovalDeciderFor(Context(kind)));
        Assert.Contains("No approval decider", refused.Message);
    }

    [Fact]
    public async Task A_run_whose_stored_kind_has_no_decider_is_never_approved()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = JsonNode.Parse(stored.Context)!.AsObject();
            Assert.Equal(RunKinds.Provision, context["kind"]!.GetValue<string>());
            context["kind"] = "no-such-kind";
            stored.Context = context.ToJsonString();
            await db.SaveChangesAsync();
        });

        // The result is kept; the advance after it stops at the approval.
        var plan = await agent.NextAsync("preview");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact()));
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Succeeded, run.Steps.Single(s => s.Name == StepNames.Plan).Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal((StepStatuses.Pending, null, null), (approval.Status, approval.Decision, approval.PlanSha256));
        Assert.Equal("Advancing this run failed: No approval decider for the run kind 'no-such-kind'.", run.Detail);
        Assert.NotEqual(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        Assert.Equal(0, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.RunId == run.Id && a.Mutating)));
    }

    private static RunContext Context(string? kind) => new()
    {
        Address = Guest,
        Kind = kind,
        Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", null),
    };
}
