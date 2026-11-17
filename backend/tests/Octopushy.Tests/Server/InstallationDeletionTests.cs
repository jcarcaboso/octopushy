using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
using Octopushy.Server.Uptime;

namespace Octopushy.Tests.Server;

public class InstallationDeletionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Address = "home/korriban";
    private const string Folder = "labs/home/proxmox/korriban";

    private static string Url(Guid id, string address = Address) =>
        $"/api/installations/{id}?address={Uri.EscapeDataString(address)}";

    [Fact]
    public async Task Deletion_requires_authentication_matching_confirmation_and_removal_from_git()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(Url(installation.Id))).StatusCode);
        using var client = await server.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(Url(installation.Id, "home/other"))).StatusCode);
        var present = await client.DeleteAsync(Url(installation.Id));
        Assert.Equal(HttpStatusCode.Conflict, present.StatusCode);
        Assert.Contains("folder", await present.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(Guid.NewGuid()))).StatusCode);
        var guest = await server.ResourceAsync(Address + "/krb-app-01");
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(guest.Id))).StatusCode);
        Assert.True(await server.QueryAsync(db => db.Resources.AnyAsync(r => r.Id == installation.Id)));
    }

    [Fact]
    public async Task Offline_deletion_purges_descendants_history_state_keys_and_files_without_removing_shared_data()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        var guest = await server.ResourceAsync(Address + "/krb-app-01");
        server.Repository.Delete(Folder);
        server.Commit("Remove offline server");
        await server.SyncAsync();

        var serviceId = Guid.NewGuid();
        var retiredId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var movedId = Guid.NewGuid();
        var movedServiceId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var ownKey = $"service-http|{Address}/krb-app-01/web|https://old.invalid/";
        var oldKey = $"guest-ssh|{Address}/renamed-guest|ssh://10.1.0.90:22";
        var movedKey = "service-http|home/elsewhere/old/web|https://old.invalid/";
        var otherKey = $"proxmox-api|{Address}-other/node|https://other.invalid/";
        await server.WithDbAsync(async db =>
        {
            db.Resources.AddRange(
                new Resource { Id = serviceId, Kind = ResourceKinds.Service, Address = guest.Address + "/web",
                    ParentAddress = guest.Address, Lab = "home", MissingSince = now },
                new Resource { Id = retiredId, Kind = ResourceKinds.Guest, Address = Address + "/old",
                    ParentAddress = Address, Lab = "home", RetiredAt = now },
                new Resource { Id = movedId, Kind = ResourceKinds.Guest, Address = "home/elsewhere/old",
                    Lab = "home", RetiredAt = now, BoundInstallation = Address },
                new Resource { Id = movedServiceId, Kind = ResourceKinds.Service, Address = "home/elsewhere/old/web",
                    ParentAddress = "home/elsewhere/old", Lab = "home", MissingSince = now },
                new Resource { Id = otherId, Kind = ResourceKinds.Installation, Address = Address + "-other", Lab = "home" });
            db.Runs.Add(new Run { Id = runId, ResourceId = serviceId, Operation = Operations.Pipeline,
                Reason = "manual", Status = RunStatuses.Failed });
            db.Steps.Add(new Step { Id = stepId, RunId = runId, Name = "apply", Kind = StepKinds.Agent });
            db.Attempts.Add(new Attempt { Id = attemptId, StepId = stepId, AgentId = agentId,
                Executor = "opentofu", Action = "apply", Status = AttemptStatuses.Uncertain, Mutating = true });
            db.Artifacts.Add(new ArtifactRecord { Id = Guid.NewGuid(), AttemptId = attemptId, Name = "plan", Sha256 = "abc" });
            db.SecretResolutions.Add(new SecretResolution { AttemptId = attemptId, AgentId = agentId, Key = "shared", Version = 1 });
            db.Observations.Add(new Observation { Id = Guid.NewGuid(), ResourceId = installation.Id, Data = "{}" });
            db.GuestConfigurations.Add(new GuestConfiguration { ResourceId = serviceId, Snapshot = "{}" });
            db.GuestSchedules.Add(new GuestScheduleState { ResourceId = guest.Id, Action = "configure", Cron = "* * * * *" });
            db.ServiceIcons.Add(new ServiceIcon { ResourceId = serviceId, Name = "web.svg" });
            db.RevisionIssues.Add(new RevisionIssues { ResourceId = guest.Id, RevisionId = Guid.NewGuid(), Issues = "[]" });
            db.TofuStates.Add(new TofuState { ResourceId = serviceId });
            db.TofuStateVersions.Add(new TofuStateVersion { Id = Guid.NewGuid(), ResourceId = serviceId,
                Version = 1, Lineage = "lineage", Sha256 = "abc", Ciphertext = [1], WrappedKey = [2] });
            db.Changes.Add(new ResourceChange { Id = changeId, Kind = ChangeKinds.Remove, ResourceKind = ResourceKinds.Guest,
                ResourceId = guest.Id, Address = guest.Address, Author = "ops", BaseCommit = "abc", Message = "Remove" });
            db.GeneratedValues.Add(new GeneratedValue { Id = Guid.NewGuid(), ChangeId = changeId, Secret = "enrollment",
                Key = "old/enrollment", Generator = "agent-enrollment-token", StoreDescription = "Generated", TokenId = tokenId });
            db.EnrollmentTokens.Add(new EnrollmentToken { Id = tokenId, TokenHash = "old-token" });
            db.InstallationSnippetKeys.Add(new InstallationSnippetKey { Installation = Address,
                PublicKey = "ssh-ed25519 public", Fingerprint = "fingerprint" });
            await db.SaveChangesAsync();
            await UptimeMonitor.RecordAsync(db, ownKey, false, null, null, null, now, CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, oldKey, false, null, null, null, now, CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, movedKey, false, null, null, null, now, CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, otherKey, true, null, null, null, now, CancellationToken.None);
        });
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
            foreach (var key in new[] { GuestAccessCredentials.Key(guest.Id), GuestHostCredentials.Key(guest.Id),
                         InstallationSnippetCredentials.Key(Address), ContainerHostKeys.Key(retiredId) })
                await store.SetInternalAsync(key, "private-material", Visibilities.Secret, null, CancellationToken.None);
            var logs = scope.ServiceProvider.GetRequiredService<AttemptLogStore>();
            logs.RegisterSecrets(attemptId, []);
            await logs.AppendAsync(attemptId, 0, [(0, "stdout", "old log")], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<ArtifactStore>().SaveAsync(attemptId, "plan", [1, 2], CancellationToken.None);
        }
        using var client = await server.SignedInClientAsync();
        var response = await client.DeleteAsync(Url(installation.Id));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        await server.WithDbAsync(async db =>
        {
            Assert.False(await db.Resources.AnyAsync(r => r.Address == Address || r.Address.StartsWith(Address + "/")));
            Assert.True(await db.Resources.AnyAsync(r => r.Id == otherId));
            Assert.False(await db.Resources.AnyAsync(r => r.Id == movedId || r.Id == movedServiceId));
            Assert.Empty(await db.Runs.ToListAsync());
            Assert.Empty(await db.Steps.ToListAsync());
            Assert.Empty(await db.Attempts.ToListAsync());
            Assert.Empty(await db.Artifacts.ToListAsync());
            Assert.Empty(await db.SecretResolutions.ToListAsync());
            Assert.Empty(await db.Observations.ToListAsync());
            Assert.Empty(await db.GuestConfigurations.ToListAsync());
            Assert.Empty(await db.GuestSchedules.ToListAsync());
            Assert.Empty(await db.GuestAccessKeys.ToListAsync());
            Assert.Empty(await db.GuestHostKeys.ToListAsync());
            Assert.Empty(await db.ServiceIcons.ToListAsync());
            Assert.Empty(await db.RevisionIssues.ToListAsync());
            Assert.Empty(await db.TofuStateVersions.ToListAsync());
            Assert.Empty(await db.TofuStates.ToListAsync());
            Assert.Empty(await db.Changes.ToListAsync());
            Assert.Empty(await db.GeneratedValues.ToListAsync());
            Assert.Empty(await db.EnrollmentTokens.ToListAsync());
            Assert.Empty(await db.InstallationSnippetKeys.ToListAsync());
            Assert.Empty(await db.InstallationTrusts.ToListAsync());
            Assert.False(await db.StoreEntries.AnyAsync(e => e.Key.StartsWith("system/guest-") || e.Key.StartsWith("system/installation-snippets/")));
            Assert.False(await db.StoreVersions.AnyAsync(e => e.Key.StartsWith("system/guest-") || e.Key.StartsWith("system/installation-snippets/")));
            Assert.False(await db.StoreEntries.AnyAsync(e => e.Key.StartsWith(ContainerHostKeys.Prefix)));
            Assert.False(await db.StoreVersions.AnyAsync(e => e.Key.StartsWith(ContainerHostKeys.Prefix)));
            Assert.True(await db.StoreEntries.AnyAsync(e => e.Key == "proxmox/korriban/read-only-token"));
            Assert.Equal([otherKey], await db.UptimeChecks.Select(c => c.Key).ToListAsync());
            Assert.Equal([otherKey], await db.UptimeBuckets.Select(c => c.CheckKey).ToListAsync());
            Assert.NotEmpty(await db.Revisions.ToListAsync());
        });
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "logs", $"{attemptId}.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "artifacts", attemptId.ToString())));
        await server.Engine.OnLogAsync(agentId, attemptId, 1, [(0, "stdout", "late log")], CancellationToken.None);
        Assert.True(await server.Engine.OnResultAsync(agentId,
            new AttemptResult { AttemptId = attemptId.ToString(), Outcome = Outcome.Succeeded }, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "logs", $"{attemptId}.jsonl")));
        await server.SyncAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/installations/{installation.Id}")).StatusCode);
        Assert.False(await server.QueryAsync(db => db.Resources.AnyAsync(r => r.Address == Address)));
    }

    [Fact]
    public async Task An_active_mutation_is_not_forgotten()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        var guest = await server.ResourceAsync(Address + "/krb-app-01");
        server.Repository.Delete(Folder);
        server.Commit("Remove offline server");
        await server.SyncAsync();
        var attemptId = Guid.NewGuid();
        await server.WithDbAsync(async db =>
        {
            db.Attempts.Add(new Attempt { Id = attemptId, ResourceId = guest.Id, Executor = "opentofu",
                Action = "apply", Status = AttemptStatuses.Running, Mutating = true });
            await db.SaveChangesAsync();
        });
        using var client = await server.SignedInClientAsync();
        var response = await client.DeleteAsync(Url(installation.Id));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Force stop", await response.Content.ReadAsStringAsync());
        Assert.True(await server.QueryAsync(db => db.Resources.AnyAsync(r => r.Id == installation.Id)));
    }

    [Theory]
    [InlineData(ChangeStatuses.Committing)]
    [InlineData(ChangeStatuses.Uncertain)]
    [InlineData(ChangeStatuses.Unknown)]
    public async Task An_unsettled_repository_change_prevents_deletion(string status)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        server.Repository.Delete(Folder);
        server.Commit("Remove offline server");
        await server.SyncAsync();
        await server.WithDbAsync(async db =>
        {
            db.Changes.Add(new ResourceChange { Id = Guid.NewGuid(), Kind = ChangeKinds.CreateGuest,
                ResourceKind = ResourceKinds.Guest, Address = Address + "/new", Author = "ops",
                BaseCommit = "abc", Message = "Create", Status = status });
            await db.SaveChangesAsync();
        });
        using var client = await server.SignedInClientAsync();
        var response = await client.DeleteAsync(Url(installation.Id));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("unsettled", await response.Content.ReadAsStringAsync());
        Assert.True(await server.QueryAsync(db => db.Resources.AnyAsync(r => r.Id == installation.Id)));
    }

    [Fact]
    public async Task Failed_file_cleanup_rolls_back_the_database_and_can_be_retried()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        server.Repository.Delete(Folder);
        server.Commit("Remove offline server");
        await server.SyncAsync();
        var attemptId = await server.QueryAsync(db => db.Attempts.Select(a => a.Id).FirstAsync());
        var blocked = Path.Combine(server.DataDirectory, "logs", $"{attemptId}.jsonl");
        Directory.CreateDirectory(blocked);
        using var client = await server.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.DeleteAsync(Url(installation.Id))).StatusCode);
        Assert.True(await server.QueryAsync(db => db.Resources.AnyAsync(r => r.Id == installation.Id)));
        Assert.True(await server.QueryAsync(db => db.Attempts.AnyAsync(a => a.Id == attemptId)));
        Directory.Delete(blocked);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(installation.Id))).StatusCode);
    }

    [Fact]
    public async Task In_flight_uptime_answers_cannot_recreate_a_deleted_servers_checks()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Address);
        var monitor = server.Services.GetRequiredService<UptimeMonitor>();
        var session = new AgentSession(Guid.NewGuid(), "checker", [], [UptimeMonitor.Capability]);
        server.Services.GetRequiredService<AgentConnections>().Add(session);
        await monitor.TickAsync(CancellationToken.None);
        Assert.NotEmpty(monitor.Targets);
        Assert.True(session.Outbound.TryRead(out var message));
        var probe = message.Probe;
        Assert.NotNull(probe);
        server.Repository.Delete(Folder);
        server.Commit("Remove offline server");
        await server.SyncAsync();
        using var client = await server.SignedInClientAsync();
        var deletion = await client.DeleteAsync(Url(installation.Id));
        Assert.True(deletion.StatusCode == HttpStatusCode.NoContent, await deletion.Content.ReadAsStringAsync());
        Assert.Empty(monitor.Targets);
        Assert.True(await monitor.AcceptAsync(session, new ReachabilityReport
        {
            ProbeId = probe.ProbeId, Status = "reachable", ElapsedMs = 1,
        }, CancellationToken.None));
        clock.Now = clock.Now!.Value.AddMinutes(2);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(monitor.Targets);
        Assert.False(await server.QueryAsync(db => db.UptimeChecks.AnyAsync()));
        Assert.False(await server.QueryAsync(db => db.UptimeBuckets.AnyAsync()));
    }
}
