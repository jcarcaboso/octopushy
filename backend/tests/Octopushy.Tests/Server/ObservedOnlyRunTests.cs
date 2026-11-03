using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary><c>manage: false</c>: no run is ever made for the installation, and its token goes to observations alone. See docs/safety-boundary.md.</summary>
[Collection(PostgresCollection.Name)]
public class ObservedOnlyRunTests(PostgresFixture postgres)
{
    private const string ZiostToken = "ziost-root-token";

    [Fact]
    public async Task An_observed_only_installation_is_observed_and_its_guest_gets_no_run()
    {
        var test = TestRepository.Minimal();
        ObservedOnlyInstallationTests.ObserveOnly(test);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var guest = await server.ResourceAsync(Guest);
        Assert.False(guest.Valid);
        Assert.Contains("home/korriban is observed only (manage: false", guest.Issues);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync()));

        // Its read-only token goes to an observation, and to nothing else.
        var observe = await agent.NextAsync("observe");
        Assert.Equal("octopushy@pve!agent=secret-token", Assert.Single(observe.Secrets).Value);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());

        var refused = await Assert.ThrowsAsync<RunException>(() =>
            server.Services.GetRequiredService<Planner>().PlanGuestAsync(guest.Id, CancellationToken.None));
        Assert.Contains("invalid", refused.Message);

        var installation = await server.ResourceAsync(Installation);
        await using var scope = server.Services.CreateAsyncScope();
        var view = await scope.ServiceProvider.GetRequiredService<Views>().InstallationAsync(installation.Id, CancellationToken.None);
        Assert.False(view!.Installation.Managed);
        Assert.True(view.Installation.Valid);
    }

    [Fact]
    public void No_run_is_made_for_an_observed_only_installation()
    {
        using var test = PipelineDefinitionTests.Repository();
        ObservedOnlyInstallationTests.ObserveOnly(test);
        var repository = LabRepositoryLoader.Load(test.Root);
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var guest = repository.Guest(Guest)!;
        var resource = new Resource { Id = Guid.NewGuid(), Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };

        Assert.All(new Func<Run>[]
        {
            () => factory.Guest(repository, guest, resource, revision, "manual", false),
            () => factory.Configure(repository, guest, resource, revision, "apply", "manual", null, null),
            () => factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(1, 2, [1])),
            () => factory.Pipeline(repository, repository.Service(PipelineDefinitionTests.Address)!, resource, resource, revision, "manual"),
        }, make => Assert.Contains("home/korriban is observed only", Assert.Throws<RunException>(make).Message));
    }

    [Fact]
    public async Task A_run_whose_installation_became_observed_only_fails_and_never_waits()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        await ObserveOnlyInDatabaseAsync(server, Installation);

        // The engine asks before it starts anything, even with no agent to wait for.
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("home/korriban is observed only", run.Detail);
        Assert.DoesNotContain(run.Steps.SelectMany(s => s.Attempts), a => a.Status != AttemptStatuses.Cancelled);

        // A run handed to the engine afterwards is refused as it is added.
        var added = await server.Engine.ExecuteAsync(async (db, _) =>
        {
            var resource = await db.Resources.FirstAsync(r => r.Address == Guest);
            var revision = await db.Revisions.OrderByDescending(r => r.Sequence).FirstAsync();
            // Made from the repository, which still manages the installation.
            var repository = LabRepositoryLoader.Load(server.Repository.Root);
            var created = server.Services.GetRequiredService<RunFactory>().Guest(repository, repository.Guest(Guest)!, resource, revision, "manual", false);
            await server.Engine.AddRunsAsync(db, [created], CancellationToken.None);
            return created.Id;
        }, CancellationToken.None);
        run = await server.QueryAsync(db => db.Runs.Include(r => r.Steps).FirstAsync(r => r.Id == added));
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("home/korriban is observed only", run.Detail);
        Assert.All(run.Steps, s => Assert.Equal(StepStatuses.Skipped, s.Status));
    }

    [Fact]
    public async Task A_run_whose_context_names_an_observed_only_installation_is_refused_at_dispatch()
    {
        var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/ziost/installation.yaml", """
            schemaVersion: 1
            manage: false
            type: standalone
            connection:
              endpoint: https://10.9.0.100:8006/
              tls: { verify: false }
              token: { store: proxmox/ziost/token }
            hosts:
              ziost: {}
            """);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("proxmox/ziost/token", ZiostToken);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var plan = run.Steps.Single(s => s.Name == StepNames.Plan);
        Assert.Equal(StepStatuses.Queued, plan.Status);

        // The guest's run, with ziost's token put in for the read-only one after it was made.
        var context = RunContext.Parse(run.Context);
        context.Secrets[SecretNames.ProxmoxReadOnlyToken] = "proxmox/ziost/token";
        await server.WithDbAsync(async db =>
        {
            (await db.Runs.FirstAsync(r => r.Id == run.Id)).Context = context.Serialize();
            await db.SaveChangesAsync();
        });
        var agent = await server.ConnectAgentAsync("home");

        // The builder refuses it before it resolves any credential.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
            var tampered = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).FirstAsync(r => r.Id == run.Id);
            var step = tampered.Steps.Single(s => s.Name == StepNames.Plan);
            var refused = await Assert.ThrowsAsync<AssignmentException>(() => scope.ServiceProvider.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(step.Attempts.Single(), step, tampered, RunContext.Parse(tampered.Context), agent.Id, CancellationToken.None));
            Assert.Contains("home/ziost is observed only", refused.Message);
        }

        // Dispatch fails it, and only observations go out: ziost's token with ziost's alone.
        await server.Engine.DispatchAsync(CancellationToken.None);
        var sent = agent.TakeAssignments();
        Assert.All(sent, a => Assert.Equal("observe", a.Action));
        Assert.DoesNotContain(sent, a => a.Action != "observe" && a.Secrets.Values.Contains(ZiostToken));
        Assert.Contains(sent, a => a.Secrets.Values.SequenceEqual([ZiostToken])
            && JsonNode.Parse(a.InputJson)!["connection"]!["endpoint"]!.GetValue<string>() == "https://10.9.0.100:8006/");
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("home/ziost is observed only", run.Detail);
        Assert.Equal(AttemptStatuses.Failed, run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single().Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action != "observe");
    }

    /// <summary>Sets <c>manage: false</c> on an installation as the engine last read it, without a new revision.</summary>
    private static Task ObserveOnlyInDatabaseAsync(ServerHarness server, string address) =>
        server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == address);
            var definition = JsonNode.Parse(installation.Definition!)!.AsObject();
            definition["manage"] = false;
            installation.Definition = definition.ToJsonString();
            await db.SaveChangesAsync();
        });
}
