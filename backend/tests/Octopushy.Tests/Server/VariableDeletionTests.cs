using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

/// <summary>Deleting a key under Variables: refused while anything needs it, and otherwise its values go and its evidence stays.</summary>
[Collection(PostgresCollection.Name)]
public class VariableDeletionTests(PostgresFixture postgres)
{
    private const string Token = "proxmox/korriban/read-only-token";
    private const string Writer = "proxmox/korriban/privileged-token";
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task A_key_with_execution_history_is_deleted_and_who_received_it_stays_on_record()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var run = await ProvisionAsync(server, agent);
        Assert.Equal(RunStatuses.Succeeded, run.Status);

        // The old key: a finished run pinned it, and one of its attempts was given it. Nothing at the branch head uses it now.
        const string Old = "bb/tailscale-auth-key";
        await server.SetSecretAsync(Old, "the old auth key");
        var attempt = run.Steps.SelectMany(s => s.Attempts).First();
        await server.WithDbAsync(async db =>
        {
            var record = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = RunContext.Parse(record.Context);
            context.Secrets["tailscale.auth_key"] = Old;
            context.SecretVersions["tailscale.auth_key"] = 1;
            record.Context = context.Serialize();
            db.SecretResolutions.Add(new SecretResolution { Key = Old, Version = 1, AttemptId = attempt.Id, AgentId = agent.Id, ResolvedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/variables?key=" + Old)).StatusCode);

        var list = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        Assert.DoesNotContain(list!["entries"]!.AsArray(), v => v!["key"]!.GetValue<string>() == Old);
        Assert.DoesNotContain(list["missing"]!.AsArray(), v => v!["key"]!.GetValue<string>() == Old);
        Assert.False(await server.QueryAsync(db => db.StoreVersions.AnyAsync(v => v.Key == Old)));
        var kept = await server.QueryAsync(db => db.SecretResolutions.AsNoTracking().SingleAsync(r => r.Key == Old));
        Assert.Equal((1, attempt.Id), (kept.Version, kept.AttemptId));
        var deleted = await server.QueryAsync(db => db.DeletedStoreKeys.AsNoTracking().SingleAsync(d => d.Key == Old));
        Assert.Equal((1, "ops"), (deleted.LatestVersion, deleted.DeletedBy));
        // The run's page still reads, and masking what its attempt reported needs nothing more.
        (await client.GetAsync($"/api/runs/{run.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/variables/versions?key=" + Old)).StatusCode);
        // Deleting a key that is gone changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/variables?key=" + Old)).StatusCode);
    }

    [Fact]
    public async Task Deleting_is_refused_while_a_definition_at_the_branch_head_references_the_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SyncAndTrustAsync(client);

        var response = await client.DeleteAsync("/api/variables?key=" + Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var title = (await response.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>();
        Assert.StartsWith($"'{Token}' is still used by ", title);
        Assert.Contains("home/korriban", title);
        Assert.True(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == Token)));
        Assert.True(await server.QueryAsync(db => db.StoreVersions.AnyAsync(v => v.Key == Token)));
    }

    [Fact]
    public async Task Deleting_is_refused_while_a_run_that_has_not_finished_binds_the_key_and_allowed_once_it_has()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);

        // Before its first dispatch it has pinned nothing, and would take the active version.
        Assert.False(RunContext.Parse(run.Context).SecretBindingsPinned);
        Assert.Contains($"run #{run.Number}", (await RefusedAsync(server, Writer)).Message);

        // Pinned, its plan done and its apply waiting for the agent: the apply must still get the version it pinned.
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        Assert.True(RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Provision)).Context).SecretBindingsPinned);
        Assert.Equal($"'{Writer}' is still used by run #{run.Number}. Delete it once nothing uses it.", (await RefusedAsync(server, Writer)).Message);

        var apply = await agent.NextAsync("apply");
        Assert.Contains($"run #{run.Number}", (await RefusedAsync(server, Writer)).Message);
        await agent.ReportAsync(apply, Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        // Finished: what it was given stays on record, and the value can go. The definition is left out of this call.
        var released = await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer));
        Assert.True(released > 0);
        await using (var scope = server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VariableStore>().DeleteAsync(Writer, [], "ops", CancellationToken.None);
        Assert.False(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == Writer)));
        Assert.False(await server.QueryAsync(db => db.StoreVersions.AnyAsync(v => v.Key == Writer)));
        Assert.Equal(released, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
        (await client.GetAsync($"/api/runs/{run.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deleting_is_refused_while_an_observation_that_has_not_finished_takes_the_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var observation = await server.LatestObservationAsync("home/korriban");
        Assert.Contains(Token, observation.Secrets.Values);
        await server.WithDbAsync(db => db.Runs.ExecuteUpdateAsync(r => r.SetProperty(x => x.Status, RunStatuses.Cancelled)));

        Assert.Equal($"'{Token}' is still used by an observation of home/korriban. Delete it once nothing uses it.", (await RefusedAsync(server, Token)).Message);
    }

    [Fact]
    public async Task A_key_written_again_continues_its_version_numbers()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        const string Key = "bb/tailscale-auth-key";
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        Assert.Equal(1, await store.NextVersionAsync(Key, CancellationToken.None));
        await store.SetAsync(Key, "first", Visibilities.Secret, null, CancellationToken.None);
        var candidate = await store.StageAsync(Key, "second", null, CancellationToken.None);
        await store.PromoteAsync(Key, candidate, 1, "Checked the replacement externally.", "ops", CancellationToken.None);
        Assert.Equal(3, await store.StageAsync(Key, "an unpromoted third", null, CancellationToken.None));
        await store.DeleteAsync(Key, [], "ops", CancellationToken.None);

        // Version 3 was never active, and is still not reused.
        Assert.Equal(4, await store.NextVersionAsync(Key, CancellationToken.None));
        await store.SetAsync(Key, "again", Visibilities.Secret, null, CancellationToken.None);
        Assert.Equal(("again", 4), await store.GetAsync(Key, CancellationToken.None));
        Assert.Null(await store.OpenForMaskingAsync(Key, 2, CancellationToken.None));
        var versions = await server.QueryAsync(db => db.StoreVersions.AsNoTracking().Where(v => v.Key == Key).Select(v => v.Version).ToListAsync());
        Assert.Equal([4], versions);

        await store.DeleteAsync(Key, [], "ops", CancellationToken.None);
        Assert.Equal(4, (await server.QueryAsync(db => db.DeletedStoreKeys.AsNoTracking().SingleAsync(d => d.Key == Key))).LatestVersion);
        await store.SetAsync(Key, "plain now", Visibilities.Plain, null, CancellationToken.None);
        Assert.Equal("plain now", await store.GetPlainAsync(Key, CancellationToken.None));
        Assert.Equal(("plain now", 5), await store.GetAsync(Key, CancellationToken.None));

        // system/ keys stay out of reach.
        await Assert.ThrowsAsync<StoreException>(() => store.DeleteAsync("system/guest-access/x", [], "ops", CancellationToken.None));
    }

    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";

    [Fact]
    public async Task After_a_rollback_the_key_the_branch_head_uses_is_kept_and_listed_as_used()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SyncAndTrustAsync(client);
        var original = TestWorkflows.Run(server.Repository.Root, "rev-parse", "HEAD").Trim();
        await ReplaceConnectionKeysAsync(server, "Replace connection keys");
        await server.SyncAsync();
        TestWorkflows.Run(server.Repository.Root, "reset", "--quiet", "--hard", original);
        await server.SyncAsync();
        await FinishAllAsync(server);
        // The head is the earlier revision again, while a newer one is on record.
        var head = await server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).Select(r => r.Commit).SingleAsync());
        Assert.Equal(original, head);
        Assert.NotEqual(head, await server.QueryAsync(db => db.Revisions.OrderByDescending(r => r.Sequence).Select(r => r.Commit).FirstAsync()));

        var list = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        var token = list!["entries"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == Token)!;
        Assert.Contains("home/korriban", token["usedBy"]!.AsArray().Select(u => u!.GetValue<string>()));
        var response = await client.DeleteAsync("/api/variables?key=" + Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"{InstallationFile} at the branch head", await TitleAsync(response));
        Assert.True(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == Token)));
    }

    [Fact]
    public async Task A_definition_that_fails_validation_still_keeps_the_keys_it_names()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SyncAndTrustAsync(client);
        var text = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, InstallationFile));
        Assert.Contains(Token, text);
        server.Repository.Write(InstallationFile, text.Replace("schemaVersion: 1", "schemaVersion: 999"));
        server.Commit("A schema typo, its references unchanged");
        await server.SyncAsync();
        await FinishAllAsync(server);
        Assert.False((await server.ResourceAsync("home/korriban")).Valid);

        var response = await client.DeleteAsync("/api/variables?key=" + Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"{InstallationFile} at the branch head", await TitleAsync(response));

        // A definition file that is not YAML at all could name any key: nothing is deleted while it is there.
        await server.SetSecretAsync("unused/key", "a value nothing uses");
        server.Repository.Write("labs/home/broken.yaml", "a: [unclosed");
        server.Commit("Not YAML");
        await server.SyncAsync();
        response = await client.DeleteAsync("/api/variables?key=unused/key");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("could not be read", await TitleAsync(response));
        Assert.True(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == "unused/key")));
    }

    [Fact]
    public async Task While_the_head_is_invalid_the_keys_scheduled_observations_send_are_kept()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SyncAndTrustAsync(client);
        server.Repository.Write("octopushy.yaml", "schemaVersion: 999\n");
        await ReplaceConnectionKeysAsync(server, "An invalid head that names other connection keys");
        await server.SyncAsync();
        await FinishAllAsync(server);

        var response = await client.DeleteAsync("/api/variables?key=" + Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal($"'{Token}' is still used by scheduled observations of home/korriban. Delete it once nothing uses it.", await TitleAsync(response));
        // And the scheduler can still observe with it.
        await server.WithDbAsync(db => db.Resources.Where(r => r.Kind == ResourceKinds.Installation)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ObserveRequestedAt, DateTimeOffset.UtcNow.AddDays(-1))));
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        Assert.Null((await server.ResourceAsync("home/korriban")).ObserveError);
    }

    [Fact]
    public async Task What_an_attempt_reports_is_withheld_unless_every_secret_it_holds_is_known()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        const string Secret = "a synthetic secret value";

        // Never registered: nothing says what to mask, so nothing passes through.
        var unknown = Guid.CreateVersion7();
        await logs.AppendAsync(unknown, 0, [(0, "stderr", $"line {Secret}")], CancellationToken.None);
        Assert.Equal(AttemptLogStore.Withheld, (await logs.ReadAsync(unknown, -1, 10, CancellationToken.None)).Single().Text);
        Assert.Null(logs.MaskOutputs(unknown, $"{{\"v\":\"{Secret}\"}}"));
        Assert.Equal(AttemptLogStore.Withheld, logs.MaskDiagnostic(unknown, $"error {Secret}"));

        // Registered with no secrets: its lines are kept as they are.
        var plain = Guid.CreateVersion7();
        logs.RegisterSecrets(plain, []);
        await logs.AppendAsync(plain, 0, [(0, "stdout", "an ordinary line")], CancellationToken.None);
        Assert.Equal("an ordinary line", (await logs.ReadAsync(plain, -1, 10, CancellationToken.None)).Single().Text);

        // Registered, then closed by its result before a log handler that had already checked appends: withheld, never plaintext.
        var raced = Guid.CreateVersion7();
        logs.RegisterSecrets(raced, [Secret]);
        await logs.AppendAsync(raced, 0, [(0, "stderr", $"before {Secret}")], CancellationToken.None);
        await logs.CloseAsync(raced, CancellationToken.None);
        await logs.AppendAsync(raced, 1, [(0, "stderr", $"after {Secret}")], CancellationToken.None);
        var lines = await logs.ReadAsync(raced, -1, 10, CancellationToken.None);
        Assert.Equal(["before ***", AttemptLogStore.Withheld], lines.Select(l => l.Text));
        Assert.Equal([0L, 1L], lines.Select(l => l.Sequence));
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "logs", $"{raced}.jsonl")));
    }

    private static async Task ReplaceConnectionKeysAsync(ServerHarness server, string message)
    {
        await server.SetSecretAsync("replacement/read", "a replacement read-only token");
        await server.SetSecretAsync("replacement/write", "a replacement privileged token");
        var text = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, InstallationFile));
        server.Repository.Write(InstallationFile, text.Replace(Token, "replacement/read").Replace(Writer, "replacement/write"));
        server.Commit(message);
    }

    /// <summary>Ends every run and attempt, so only definitions and scheduled work hold a key.</summary>
    private static Task FinishAllAsync(ServerHarness server) =>
        server.WithDbAsync(async db =>
        {
            await db.Runs.ExecuteUpdateAsync(q => q.SetProperty(x => x.Status, RunStatuses.Succeeded));
            await db.Attempts.ExecuteUpdateAsync(q => q.SetProperty(x => x.Status, AttemptStatuses.Succeeded));
        });

    private static async Task<string> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>();

    private static async Task<StoreException> RefusedAsync(ServerHarness server, string key)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var refusal = await Assert.ThrowsAsync<StoreException>(() =>
            scope.ServiceProvider.GetRequiredService<VariableStore>().DeleteAsync(key, [], "ops", CancellationToken.None));
        Assert.True(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == key)));
        return refusal;
    }

    private static async Task<Run> ProvisionAsync(ServerHarness server, FakeAgent agent)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();
        return await server.LatestRunAsync(Guest, Operations.Provision);
    }

    private static async Task<HttpClient> SignInAsync(ServerHarness server)
    {
        var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long fixture password" })).EnsureSuccessStatusCode();
        return client;
    }
}
