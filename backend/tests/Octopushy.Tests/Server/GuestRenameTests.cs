using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class GuestRenameTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

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
