using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>What the overview lists as failed now, and how many runs the last week made.</summary>
[Collection(PostgresCollection.Name)]
public class OverviewTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Service = Guest + "/web";

    [Fact]
    public async Task Failed_lists_the_failed_guests_and_services_and_week_counts_the_last_7_days()
    {
        await using var server = await ServerHarness.StartAsync(postgres, TestRepository.Minimal(), clock: new TestClock { Now = Now });
        await server.SyncAndTrustAsync();
        var guest = await server.ResourceAsync(Guest);
        var service = new Resource { Id = Guid.NewGuid(), Address = Service, Kind = ResourceKinds.Service, ParentAddress = Guest, Lab = "home", Valid = true };
        await server.WithDbAsync(async db =>
        {
            await db.Runs.ExecuteDeleteAsync();
            db.Resources.Add(service);
            await db.SaveChangesAsync();
        });
        var client = await server.SignedInClientAsync();

        await AddRunAsync(server, guest.Id, RunStatuses.Failed, Now.AddHours(-3));
        await AddRunAsync(server, service.Id, RunStatuses.Failed, Now.AddHours(-2));
        await AddRunAsync(server, guest.Id, RunStatuses.Succeeded, Now.AddDays(-8));
        var overview = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        Assert.Equal([Service, Guest], overview.Failed.Select(r => r.Address));
        Assert.Equal(new OverviewWeekDto(2, 0, 2, 0, 0), overview.Week);

        // A later run takes the guest out, whether it is still going or it succeeded.
        await AddRunAsync(server, guest.Id, RunStatuses.Running, Now.AddHours(-1));
        await AddRunAsync(server, service.Id, RunStatuses.Cancelled, Now.AddMinutes(-90));
        overview = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        Assert.Empty(overview.Failed);
        Assert.Equal(new OverviewWeekDto(4, 0, 2, 1, 1), overview.Week);

        await server.WithDbAsync(db => db.Runs.Where(r => r.Status == RunStatuses.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Succeeded)));
        overview = (await client.GetFromJsonAsync<OverviewDto>("/api/overview"))!;
        Assert.Empty(overview.Failed);
        Assert.Equal(new OverviewWeekDto(4, 1, 2, 1, 0), overview.Week);
    }

    private static Task AddRunAsync(ServerHarness server, Guid resourceId, string status, DateTimeOffset createdAt) =>
        server.WithDbAsync(async db =>
        {
            db.Runs.Add(new Run
            {
                Id = Guid.NewGuid(),
                ResourceId = resourceId,
                Operation = Operations.Provision,
                Reason = "manual",
                Status = status,
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync();
        });
}
