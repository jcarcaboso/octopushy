using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class ValidationRefreshTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private static readonly DefinitionIssue OldError = new(IssueSeverity.Error, GuestFile, null, "hardware is required", Guest);
    private static readonly DefinitionIssue OldWarning = new(IssueSeverity.Warning, GuestFile, null, "Old validator warning", Guest);

    private static string Issues(params DefinitionIssue[] issues) => JsonSerializer.Serialize(issues, InventoryUpdater.IssueJson);

    private static Task<DefinitionRevision> HeadAsync(ServerHarness server) =>
        server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());

    private static Task StaleAsync(ServerHarness server, Guid revisionId) => server.WithDbAsync(async db =>
    {
        var revision = await db.Revisions.SingleAsync(r => r.Id == revisionId);
        revision.RepositoryValid = false;
        revision.ErrorCount = revision.WarningCount = 1;
        revision.Issues = Issues(OldError, OldWarning);
        var resource = await db.Resources.SingleAsync(r => r.Address == Guest);
        resource.Valid = false;
        resource.Issues = revision.Issues;
        var recorded = await db.RevisionIssues.SingleOrDefaultAsync(i => i.RevisionId == revision.Id && i.ResourceId == resource.Id);
        if (recorded is null)
            db.RevisionIssues.Add(new RevisionIssues { RevisionId = revision.Id, ResourceId = resource.Id, Issues = Issues(OldError) });
        else
            recorded.Issues = Issues(OldError);
        await db.SaveChangesAsync();
    });

    private static Task<string> WorkAsync(ServerHarness server) => server.QueryAsync(async db => JsonSerializer.Serialize(new
    {
        Runs = await db.Runs.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
        Attempts = await db.Attempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
        Configurations = await db.GuestConfigurations.AsNoTracking().OrderBy(c => c.ResourceId).ToListAsync(),
    }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_unchanged_applied_head_refreshes_validation_without_reapplying_or_rewriting_history(bool restart, bool externalWorkflows)
    {
        using var workflows = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, externalWorkflows ? workflows.Config() : TestRepository.Minimal(),
            settings: new() { ["Octopushy:AllowLocalWorkflowSources"] = "true" });
        if (externalWorkflows) await server.ConfirmWorkflowSourceAsync();
        await server.SyncAndTrustAsync();
        var historical = await HeadAsync(server);
        server.Repository.Write("labs/home/lab.yaml", File.ReadAllText(Path.Combine(server.Repository.Root, "labs/home/lab.yaml")) + "description: Metadata only\n");
        server.Commit("Describe the lab");
        await server.SyncAsync();
        var head = await HeadAsync(server);
        Assert.Equal(externalWorkflows, head.WorkflowsDigest.Length > 0);
        var original = await server.ResourceAsync(Guest);
        var work = await WorkAsync(server);
        var history = JsonSerializer.Serialize(await server.QueryAsync(db => db.Revisions.AsNoTracking().SingleAsync(r => r.Id == historical.Id)));
        await StaleAsync(server, head.Id);
        if (restart) await server.RestartFixtureAsync();

        await server.SyncAsync();
        var refreshed = await HeadAsync(server);
        Assert.Equal(JsonSerializer.Serialize(head), JsonSerializer.Serialize(refreshed));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await server.ResourceAsync(Guest)));
        Assert.Equal(work, await WorkAsync(server));
        Assert.Equal(history, JsonSerializer.Serialize(await server.QueryAsync(db => db.Revisions.AsNoTracking().SingleAsync(r => r.Id == historical.Id))));
        Assert.False(await server.QueryAsync(db => db.RevisionIssues.AnyAsync(i => i.RevisionId == head.Id)));
        Assert.Equal(2, await server.QueryAsync(db => db.Revisions.CountAsync()));

        await server.SyncAsync();
        Assert.Equal(JsonSerializer.Serialize(refreshed), JsonSerializer.Serialize(await HeadAsync(server)));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await server.ResourceAsync(Guest)));
        Assert.Equal(work, await WorkAsync(server));
    }

    [Fact]
    public async Task Refreshing_validation_does_not_repeat_a_completed_provision_or_configuration()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Definitions.GuestConfigurationTests.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var client = http;
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        var work = await WorkAsync(server);
        var resource = await server.ResourceAsync(Guest);
        await StaleAsync(server, (await HeadAsync(server)).Id);

        await server.SyncAsync();
        await server.SyncAsync();
        Assert.Equal(work, await WorkAsync(server));
        Assert.Equal(JsonSerializer.Serialize(resource), JsonSerializer.Serialize(await server.ResourceAsync(Guest)));
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task An_unconfirmed_workflow_source_does_not_refresh_the_applied_revision()
    {
        using var workflows = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, workflows.Config(),
            settings: new() { ["Octopushy:AllowLocalWorkflowSources"] = "true" });
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.ConfirmWorkflowSourceAsync(client);
        await server.SyncAsync();
        var head = await HeadAsync(server);
        await StaleAsync(server, head.Id);
        await server.WithDbAsync(async db =>
        {
            var settings = await db.RepositorySettings.SingleAsync();
            settings.ConfirmedWorkflowsRepository = null;
            settings.WorkflowsConfirmedAt = null;
            settings.WorkflowsConfirmedBy = null;
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();
        var stale = await HeadAsync(server);
        Assert.Equal(head.Id, stale.Id);
        Assert.False(stale.RepositoryValid);
        Assert.Equal(new[] { OldError, OldWarning }, JsonSerializer.Deserialize<List<DefinitionIssue>>(stale.Issues, InventoryUpdater.IssueJson));
        Assert.False((await server.ResourceAsync(Guest)).Valid);
    }

    [Fact]
    public async Task Runtime_errors_are_not_cleared_with_old_definition_errors()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        var head = await HeadAsync(server);
        await StaleAsync(server, head.Id);
        var runtime = new DefinitionIssue(IssueSeverity.Error, GuestFile, null, "VMID is already bound to another guest", Guest);
        await server.WithDbAsync(async db =>
        {
            (await db.Resources.SingleAsync(r => r.Address == Guest)).Issues = Issues(OldError, OldWarning, runtime);
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.False(guest.Valid);
        Assert.Equal(new[] { runtime }, JsonSerializer.Deserialize<List<DefinitionIssue>>(guest.Issues, InventoryUpdater.IssueJson));
        var recorded = await server.QueryAsync(db => db.RevisionIssues.Where(i => i.RevisionId == head.Id && i.ResourceId == guest.Id).Select(i => i.Issues).SingleAsync());
        Assert.Equal(new[] { runtime }, JsonSerializer.Deserialize<List<DefinitionIssue>>(recorded, InventoryUpdater.IssueJson));
        Assert.Equal(0, (await HeadAsync(server)).ErrorCount);
        Assert.Empty(await server.QueryAsync(db => db.InstallationTrusts.ToListAsync()));
    }

    [Fact]
    public async Task Becoming_definition_valid_still_checks_the_durable_machine_identity()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        var head = await HeadAsync(server);
        await StaleAsync(server, head.Id);
        await server.WithDbAsync(async db =>
        {
            var guest = await db.Resources.SingleAsync(r => r.Address == Guest);
            guest.BoundVmid = 999;
            guest.BoundInstallation = "home/korriban";
            await db.SaveChangesAsync();
        });
        var work = await WorkAsync(server);
        await server.SyncAsync();
        var resource = await server.ResourceAsync(Guest);
        Assert.False(resource.Valid);
        Assert.Contains("bound to VMID 999", resource.Issues);
        Assert.DoesNotContain(OldError.Message, resource.Issues);
        Assert.Equal(999, resource.BoundVmid);
        Assert.Equal(work, await WorkAsync(server));
    }

    [Fact]
    public async Task Current_validation_errors_replace_old_errors_and_repeated_syncs_keep_them()
    {
        var repository = TestRepository.Minimal();
        repository.Write(GuestFile, "schemaVersion: 1\ntype: vm\nvmid: invalid\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        var head = await HeadAsync(server);
        var guest = await server.ResourceAsync(Guest);
        Assert.False(guest.Valid);
        Assert.True(head.ErrorCount > 0);
        await StaleAsync(server, head.Id);
        await server.SyncAsync();
        Assert.Equal(JsonSerializer.Serialize(head), JsonSerializer.Serialize(await HeadAsync(server)));
        Assert.Equal(JsonSerializer.Serialize(guest), JsonSerializer.Serialize(await server.ResourceAsync(Guest)));
        await server.SyncAsync();
        Assert.Equal(JsonSerializer.Serialize(guest), JsonSerializer.Serialize(await server.ResourceAsync(Guest)));
    }
}
