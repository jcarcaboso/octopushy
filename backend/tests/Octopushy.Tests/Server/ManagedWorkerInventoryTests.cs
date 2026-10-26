using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ManagedWorkerInventoryTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Path = Definitions.ManagedWorkerDefinitionTests.GuestPath;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rejected_rename_preserves_the_binding_and_reverting_restores_the_same_resource(bool keepWorker)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var original = await server.ResourceAsync(Guest);
        Assert.NotNull(original.ManagedWorker);
        var source = File.ReadAllText(System.IO.Path.Combine(repo.Root, Path));
        var renamed = source + "\nrenamedFrom: krb-app-01\n";
        if (!keepWorker) renamed = RemoveWorker(renamed);
        repo.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        repo.Write(Path.Replace("krb-app-01", "renamed-worker"), renamed);
        server.Commit("Prohibited worker rename");
        await server.SyncAsync(); // Discovery alone must not move durable ownership.
        var retained = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Id == original.Id));
        Assert.Equal(Guest, retained.Address);
        Assert.Equal(original.ManagedWorker.Generation, retained.ManagedWorker!.Generation);
        Assert.False((await server.ResourceAsync("home/korriban/renamed-worker")).Valid);

        repo.Delete("labs/home/proxmox/korriban/guests/renamed-worker");
        repo.Write(Path, source);
        server.Commit("Revert rename");
        await server.SyncAndTrustAsync();
        var restored = await server.ResourceAsync(Guest);
        Assert.Equal(original.Id, restored.Id);
        Assert.True(restored.Valid);
        Assert.Null(restored.MissingSince);
        Assert.Equal(1, await server.QueryAsync(db => db.ManagedWorkers.CountAsync()));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("generation")]
    [InlineData("vmid")]
    public async Task An_invalid_bound_worker_edit_does_not_drop_unrelated_guest_discovery(string edit)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var original = await server.ResourceAsync(Guest);
        Assert.NotNull(original.ManagedWorker);
        var source = File.ReadAllText(System.IO.Path.Combine(repo.Root, Path));
        repo.Write(Path, edit switch
        {
            "role" => RemoveWorker(source),
            "generation" => source.Replace(Definitions.ManagedWorkerDefinitionTests.Generation, "78cd5ef0-ef9d-4ad4-a227-bd49c52d21ab"),
            _ => source.Replace("vmid: 310", "vmid: 312"),
        });
        repo.Write(Path.Replace("krb-app-01", "unrelated"), RemoveWorker(source).Replace("vmid: 310", "vmid: 311"));
        server.Commit("Rejected worker edit alongside a valid guest");
        await server.SyncAsync();
        var invalid = await server.ResourceAsync(Guest);
        Assert.False(invalid.Valid);
        Assert.Contains("Managed worker", invalid.Issues);
        Assert.Equal(original.ManagedWorker!.Generation, invalid.ManagedWorker!.Generation);
        var other = await server.LatestRunAsync("home/korriban/unrelated", Operations.Provision);
        Assert.Equal((await server.ResourceAsync("home/korriban/unrelated")).LastRevisionId, other.RevisionId);
        await server.SyncAndTrustAsync();
        Assert.Equal(other.Id, (await server.LatestRunAsync("home/korriban/unrelated")).Id);
    }

    private static string RemoveWorker(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.StartsWith("worker:")));

    [Fact]
    public async Task Correcting_an_invalid_ordinary_rename_retains_the_original_guest_identity()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var control = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded,
            FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await control.ReportAsync(await control.NextAsync("apply"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        var original = await server.ResourceAsync(Guest);
        Assert.Equal(310, original.BoundVmid);
        var source = File.ReadAllText(System.IO.Path.Combine(server.Repository.Root, Path));
        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        var renamedPath = Path.Replace("krb-app-01", "renamed-guest");
        server.Repository.Write(renamedPath, source.Replace("host: korriban", "host: invalid-host") + "\nrenamedFrom: krb-app-01\n");
        server.Commit("Invalid ordinary rename");
        await server.SyncAsync();
        Assert.Equal(Guest, (await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Id == original.Id))).Address);
        server.Repository.Write(renamedPath, source + "\nrenamedFrom: krb-app-01\n");
        server.Commit("Correct the ordinary rename");
        await server.SyncAndTrustAsync();
        var renamed = await server.ResourceAsync("home/korriban/renamed-guest");
        Assert.Equal(original.Id, renamed.Id);
        Assert.True(renamed.Valid);
        Assert.Equal(310, renamed.BoundVmid);
    }
}
