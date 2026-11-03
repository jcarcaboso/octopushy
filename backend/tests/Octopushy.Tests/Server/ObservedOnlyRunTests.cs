using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Agent.V1;
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
    private const string ZiostFile = "labs/home/proxmox/ziost/installation.yaml";
    private const string KorribanFile = "labs/home/proxmox/korriban/installation.yaml";

    private const string Ziost = """
        schemaVersion: 1
        manage: false
        type: standalone
        connection:
          endpoint: https://10.9.0.100:8006/
          tls: { verify: false }
          token: { store: proxmox/ziost/token }
        hosts:
          ziost: {}
        """;

    /// <summary>The minimal repository with ziost beside korriban, observed only, and both trusted.</summary>
    private async Task<ServerHarness> WithZiostAsync()
    {
        var test = TestRepository.Minimal();
        test.Write(ZiostFile, Ziost);
        var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("proxmox/ziost/token", ZiostToken);
        await server.SyncAndTrustAsync();
        return server;
    }

    [Fact]
    public async Task An_invalid_definition_keeps_the_installation_observed_only()
    {
        await using var server = await WithZiostAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        // The first observations finish, so the next revision observes again.
        foreach (var observe in agent.TakeAssignments().Where(a => a.Action == "observe"))
            await agent.ReportAsync(observe, Outcome.Succeeded, new JsonObject { ["hosts"] = new JsonArray(), ["guests"] = new JsonArray() });

        // One commit breaks ziost's file and points korriban's read-only token at ziost's key.
        server.Repository.Write(ZiostFile, Ziost + "\nbogus: true\n");
        var korriban = File.ReadAllText(Path.Combine(server.Repository.Root, KorribanFile));
        server.Repository.Write(KorribanFile, korriban.Replace("proxmox/korriban/read-only-token", "proxmox/ziost/token"));
        server.Commit("Break ziost and borrow its token");
        await server.SyncAndTrustAsync();

        var ziost = await server.ResourceAsync("home/ziost");
        Assert.False(ziost.Valid);
        Assert.Null(ziost.Definition);
        Assert.True(ziost.ObservedOnly);
        Assert.Equal(["proxmox/ziost/token"], ziost.ObservedOnlyStoreKeys);
        Assert.Equal(["https://10.9.0.100:8006"], ziost.ObservedOnlyEndpoints);

        // Korriban's own work names ziost's key now: its run fails, its observation fails, and the token goes nowhere.
        await server.TickAsync();
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("proxmox/ziost/token", RunContext.Parse(run.Context).Secrets.Values);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("home/ziost is observed only", run.Detail);
        var sent = agent.TakeAssignments();
        Assert.DoesNotContain(sent, a => a.Secrets.Values.Contains(ZiostToken));
        Assert.Contains("credential or endpoint of home/ziost", (await server.ResourceAsync(Installation)).ObserveError);
    }

    [Fact]
    public async Task A_missing_definition_keeps_the_installation_observed_only()
    {
        await using var server = await WithZiostAsync();
        server.Repository.Delete("labs/home/proxmox/ziost");
        server.Commit("Remove ziost");
        await server.SyncAsync();

        var ziost = await server.ResourceAsync("home/ziost");
        Assert.NotNull(ziost.MissingSince);
        Assert.True(ziost.ObservedOnly);
        var list = await server.QueryAsync(db => SafetyBoundary.ObservedOnlyInstallationsAsync(db, CancellationToken.None));
        Assert.Contains(list, i => i.Address == "home/ziost" && i.ObservedOnly && i.StoreKeys.Contains("proxmox/ziost/token"));
    }

    [Fact]
    public async Task A_trip_through_observed_only_ends_the_trust_given_while_managed()
    {
        var test = TestRepository.Minimal();
        test.Write(ZiostFile, Ziost.Replace("manage: false\n", ""));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("proxmox/ziost/token", ZiostToken);
        await server.SyncAndTrustAsync();
        Assert.False((await server.QueryAsync(db => db.InstallationTrusts.AsNoTracking().FirstAsync(t => t.Address == "home/ziost"))).ObservedOnly);

        // Observed only for one commit: the grant given while managed now covers observations alone.
        server.Repository.Write(ZiostFile, Ziost);
        server.Commit("Observe ziost only");
        await server.SyncAsync();
        Assert.True((await server.QueryAsync(db => db.InstallationTrusts.AsNoTracking().FirstAsync(t => t.Address == "home/ziost"))).ObservedOnly);
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.Contains(agent.TakeAssignments(), a => a.Action == "observe" && a.Secrets.Values.Contains(ZiostToken));

        // Managed again, with no trust in between: its work waits.
        server.Repository.Write(ZiostFile, Ziost.Replace("manage: false\n", ""));
        server.Repository.Write("labs/home/proxmox/ziost/guests/zst-app-01/guest.yaml", ZiostGuest);
        server.Commit("Manage ziost again");
        await server.SyncAsync();
        await server.TickAsync();
        var run = await server.LatestRunAsync("home/ziost/zst-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("was trusted while it was observed only", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.InputJson.Contains("zst-app-01") || a.Secrets.Values.Contains(ZiostToken));
    }

    private const string ZiostGuest = """
        schemaVersion: 1
        type: vm
        host: ziost
        vmid: 400
        hardware: { cores: 2, memory: 2048 }
        network: { bridge: vmbr0, mode: static, address: 10.9.0.40/24, gateway: 10.9.0.1 }
        workflow: test-vm@1
        """;

    [Fact]
    public async Task An_installation_that_asks_to_be_managed_waits_to_be_trusted_again()
    {
        await using var server = await WithZiostAsync();
        var trustedAsObservedOnly = await server.QueryAsync(db => db.InstallationTrusts.AsNoTracking().FirstAsync(t => t.Address == "home/ziost"));
        Assert.True(trustedAsObservedOnly.ObservedOnly);

        server.Repository.Write(ZiostFile, Ziost.Replace("manage: false", "manage: true"));
        server.Repository.Write("labs/home/proxmox/ziost/guests/zst-app-01/guest.yaml", """
            schemaVersion: 1
            type: vm
            host: ziost
            vmid: 400
            hardware: { cores: 2, memory: 2048 }
            network: { bridge: vmbr0, mode: static, address: 10.9.0.40/24, gateway: 10.9.0.1 }
            workflow: test-vm@1
            """);
        server.Commit("Manage ziost");
        await server.SyncAsync();

        var ziost = await server.ResourceAsync("home/ziost");
        Assert.False(ziost.ObservedOnly);
        // Until it is trusted as managed, its keys stay refused to other installations' work.
        Assert.Equal(["proxmox/ziost/token"], ziost.ObservedOnlyStoreKeys);
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        var run = await server.LatestRunAsync("home/ziost/zst-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("was trusted while it was observed only", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.Values.Contains(ZiostToken));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var view = await scope.ServiceProvider.GetRequiredService<Views>().InstallationAsync(ziost.Id, CancellationToken.None);
            Assert.True(view!.Installation.Trust.ManagementChanged);
            Assert.Equal(["https://10.9.0.100:8006/"], view.Installation.Trust.Untrusted);
        }

        await server.SyncAndTrustAsync();
        ziost = await server.ResourceAsync("home/ziost");
        Assert.Empty(ziost.ObservedOnlyStoreKeys);
        Assert.False((await server.QueryAsync(db => db.InstallationTrusts.AsNoTracking().FirstAsync(t => t.Address == "home/ziost"))).ObservedOnly);
        var preview = await agent.NextAsync("preview");
        Assert.Equal(ZiostToken, Assert.Single(preview.Secrets).Value);
    }

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
        using var test = ServiceDefinitionTests.Repository();
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
            () => factory.Pipeline(repository, repository.Service(ServiceDefinitionTests.Address)!, resource, resource, revision, "manual"),
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

    /// <summary>Records an installation as observed only, as a sync of <c>manage: false</c> does, without a new revision.</summary>
    private static Task ObserveOnlyInDatabaseAsync(ServerHarness server, string address) =>
        server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == address);
            var definition = JsonNode.Parse(installation.Definition!)!.AsObject();
            definition["manage"] = false;
            installation.Definition = definition.ToJsonString();
            installation.ObservedOnly = true;
            installation.ObservedOnlyStoreKeys = SafetyBoundary.TokenKeys(definition).ToList();
            installation.ObservedOnlyEndpoints = SafetyBoundary.Endpoints(definition).ToList();
            await db.SaveChangesAsync();
        });
}
