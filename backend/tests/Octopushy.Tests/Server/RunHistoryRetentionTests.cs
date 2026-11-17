using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class RunHistoryRetentionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(-1, 50)]
    [InlineData(180, -1)]
    public async Task Invalid_settings_do_not_delete_history(int days, int keep)
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: new()
        {
            ["Octopushy:RunRetentionDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Octopushy:RunRetentionKeep"] = keep.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var run = Finished(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-300));
        await server.WithDbAsync(async db =>
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync();
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None));

        Assert.Contains("no history was deleted", error.Message);
        Assert.True(await server.QueryAsync(db => db.Runs.AnyAsync(r => r.Id == run.Id)));
    }

    [Fact]
    public async Task Old_history_and_its_files_are_deleted_but_the_newest_fifty_per_resource_stay()
    {
        var now = DateTimeOffset.UtcNow;
        await using var server = await ServerHarness.StartAsync(postgres, clock: new TestClock { Now = now });
        var resource = Guid.NewGuid();
        var old = Finished(resource, now.AddDays(-300));
        var recent = Enumerable.Range(0, 50).Select(i => Finished(resource, now.AddDays(-200).AddMinutes(i))).ToArray();
        var other = Finished(Guid.NewGuid(), now.AddDays(-400));
        var attempt = new Attempt { Id = Guid.NewGuid(), RunId = old.Id, Executor = "test", Action = "test", Status = AttemptStatuses.Succeeded };
        await server.WithDbAsync(async db =>
        {
            db.Runs.AddRange(recent.Append(old).Append(other));
            db.Attempts.Add(attempt);
            db.Artifacts.Add(new ArtifactRecord { Id = Guid.NewGuid(), AttemptId = attempt.Id, Name = "plan", Sha256 = "hash" });
            db.SecretResolutions.Add(new SecretResolution { AttemptId = attempt.Id, AgentId = Guid.NewGuid(), Key = "test", Version = 1 });
            await db.SaveChangesAsync();
        });
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var artifacts = server.Services.GetRequiredService<ArtifactStore>();
        logs.RegisterSecrets(attempt.Id, []);
        await logs.AppendAsync(attempt.Id, 0, [(0L, "stdout", "test")], CancellationToken.None);
        await artifacts.SaveAsync(attempt.Id, "plan", [1, 2, 3], CancellationToken.None);

        await server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None);

        var kept = await server.QueryAsync(db => db.Runs.Select(r => r.Id).ToArrayAsync());
        Assert.DoesNotContain(old.Id, kept);
        Assert.Contains(other.Id, kept);
        Assert.All(recent, r => Assert.Contains(r.Id, kept));
        Assert.False(await server.QueryAsync(db => db.Attempts.AnyAsync()));
        Assert.False(await server.QueryAsync(db => db.Artifacts.AnyAsync()));
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync()));
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "logs", $"{attempt.Id}.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "artifacts", attempt.Id.ToString())));
    }

    [Fact]
    public async Task Unfinished_uncertain_referenced_and_pending_approval_runs_are_protected()
    {
        var now = DateTimeOffset.UtcNow;
        await using var server = await ServerHarness.StartAsync(postgres,
            settings: new() { ["Octopushy:RunRetentionKeep"] = "0" }, clock: new TestClock { Now = now });
        var resource = new Resource { Id = Guid.NewGuid(), Address = "test", Kind = ResourceKinds.Guest, Lab = "test" };
        var runs = Enumerable.Range(0, 9).Select(_ => Finished(resource.Id, now.AddDays(-300))).ToArray();
        runs[0].Status = RunStatuses.Running;
        runs[0].FinishedAt = null;
        runs[1].Status = RunStatuses.Uncertain;
        runs[2].Status = RunStatuses.Succeeded;
        runs[2].FinishedAt = null;
        runs[7].FinishedAt = now; // Old start, just finished.
        runs[8].CreatedAt = now; // New run.
        await server.WithDbAsync(async db =>
        {
            db.Resources.Add(resource);
            db.Runs.AddRange(runs);
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.NewGuid(), Kind = ChangeKinds.Edit, ResourceKind = ResourceKinds.Guest,
                Address = "test", Author = "ops", BaseCommit = "base", Message = "test",
                RunIds = [runs[3].Id], PreApprovalRunId = runs[4].Id,
            });
            db.GuestSchedules.Add(new GuestScheduleState { ResourceId = resource.Id, Action = "test", Cron = "* * * * *", LastRunId = runs[5].Id });
            db.Steps.Add(new Step
            {
                Id = Guid.NewGuid(), RunId = runs[6].Id, Name = "approval", Kind = StepKinds.Approval,
                Status = StepStatuses.AwaitingApproval,
            });
            await db.SaveChangesAsync();
        });

        await server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None);

        Assert.Equal(runs.Length, await server.QueryAsync(db => db.Runs.CountAsync()));
    }

    [Fact]
    public async Task The_latest_provision_and_the_run_for_the_desired_work_are_kept_however_old()
    {
        var now = DateTimeOffset.UtcNow;
        await using var server = await ServerHarness.StartAsync(postgres,
            settings: new() { ["Octopushy:RunRetentionKeep"] = "0" }, clock: new TestClock { Now = now });
        var resource = new Resource { Id = Guid.NewGuid(), Address = "test", Kind = ResourceKinds.Guest, Lab = "test", DesiredOccurrence = Guid.NewGuid() };
        var olderProvision = Finished(resource.Id, now.AddDays(-400));
        var applied = Finished(resource.Id, now.AddDays(-380));
        var latestProvision = Finished(resource.Id, now.AddDays(-300));
        // A plan cancelled before anything changed says nothing about the machine: readiness still reads the apply before it.
        latestProvision.Status = RunStatuses.Cancelled;
        // Waiting for approval has changed nothing yet, and may still be rejected: the apply before it stays.
        var waiting = Finished(resource.Id, now.AddDays(-290));
        waiting.Status = RunStatuses.AwaitingApproval;
        waiting.FinishedAt = null;
        olderProvision.Operation = applied.Operation = latestProvision.Operation = waiting.Operation = Operations.Provision;
        // Configuration reads the latest provision; a sync asks whether a run stands for the desired work.
        var desired = Finished(resource.Id, now.AddDays(-350));
        desired.Occurrence = resource.DesiredOccurrence;
        var configure = Finished(resource.Id, now.AddDays(-250));
        await server.WithDbAsync(async db =>
        {
            db.Resources.Add(resource);
            db.Runs.AddRange(olderProvision, applied, latestProvision, waiting, desired, configure);
            await db.SaveChangesAsync();
        });

        await server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None);

        var kept = await server.QueryAsync(db => db.Runs.Select(r => r.Id).ToArrayAsync());
        Assert.Equal(new[] { applied.Id, latestProvision.Id, waiting.Id, desired.Id }.Order(), kept.Order());
    }

    [Fact]
    public async Task Files_without_an_attempt_are_swept_and_live_attempt_files_are_kept()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var live = new Attempt { Id = Guid.NewGuid(), Executor = "test", Action = "observe" };
        await server.WithDbAsync(async db =>
        {
            db.Attempts.Add(live);
            await db.SaveChangesAsync();
        });
        var orphan = Guid.NewGuid();
        var artifacts = server.Services.GetRequiredService<ArtifactStore>();
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        foreach (var id in new[] { live.Id, orphan })
        {
            logs.RegisterSecrets(id, []);
            await logs.AppendAsync(id, 0, [(0L, "stdout", "test")], CancellationToken.None);
            await artifacts.SaveAsync(id, "plan", [1, 2, 3], CancellationToken.None);
        }

        await server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None);
        await server.Services.GetRequiredService<RunHistoryRetention>().PruneAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "logs", $"{orphan}.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "artifacts", orphan.ToString())));
        Assert.True(File.Exists(Path.Combine(server.DataDirectory, "logs", $"{live.Id}.jsonl")));
        Assert.Equal(new byte[] { 1, 2, 3 }, await artifacts.LoadAsync(live.Id, "plan", CancellationToken.None));
    }

    private static Run Finished(Guid resource, DateTimeOffset when) => new()
    {
        Id = Guid.NewGuid(), ResourceId = resource, Operation = "test", Reason = "manual",
        Status = RunStatuses.Succeeded, CreatedAt = when, FinishedAt = when,
    };
}
