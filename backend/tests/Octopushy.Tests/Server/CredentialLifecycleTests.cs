using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class CredentialLifecycleTests(PostgresFixture postgres)
{
    private const string Token = "proxmox/korriban/api-token";
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task Candidates_require_explicit_promotion_and_never_return_values()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/variables",
            new { key = Token, value = "must-not-overwrite", visibility = "secret" })).StatusCode);

        var staged = await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "candidate-private-value" });
        staged.EnsureSuccessStatusCode();
        Assert.Equal(2, (await staged.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<int>());
        var list = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        var token = list!["entries"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == Token)!;
        Assert.Equal(1, token["version"]!.GetValue<int>());
        Assert.Equal(2, token["latestVersion"]!.GetValue<int>());
        Assert.Null(token["value"]);
        var history = await client.GetStringAsync("/api/variables/versions?key=" + Token);
        Assert.DoesNotContain("candidate-private-value", history);
        Assert.DoesNotContain("secret-token", history);
        Assert.DoesNotContain("ciphertext", history, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate", history);
        Assert.Equal(HttpStatusCode.BadRequest, (await PromoteAsync(client, 2, 1, "")).StatusCode);
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await PromoteAsync(client, 1, 1)).StatusCode);
        var versions = (await client.GetFromJsonAsync<JsonArray>("/api/variables/versions?key=" + Token))!;
        Assert.Equal("active", versions.Single(v => v!["version"]!.GetValue<int>() == 2)!["status"]!.GetValue<string>());
        Assert.Equal("ops", versions[0]!["activatedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Promotion_does_not_change_a_run_between_plan_apply_and_verification()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var plan = await agent.NextAsync("preview");
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "replacement" })).EnsureSuccessStatusCode();
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        Assert.Equal(plan.Secrets["proxmox.token"], apply.Secrets["proxmox.token"]);
        await agent.ReportAsync(apply, Outcome.Succeeded);
        var verify = await agent.NextAsync("verify-guest");
        Assert.Equal(plan.Secrets["proxmox.token"], verify.Secrets["proxmox.token"]);
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(1, RunContext.Parse(run.Context).SecretVersions["proxmox.token"]);
        Assert.DoesNotContain("secret-token", run.Context);
        var history = await client.GetStringAsync("/api/variables/versions?key=" + Token);
        Assert.Contains($"run #{run.Number}", history);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/variables?key=" + Token)).StatusCode);

        var resource = await server.ResourceAsync(Guest);
        (await client.PostAsync($"/api/guests/{resource.Id}/plan", null)).EnsureSuccessStatusCode();
        Assert.Equal("replacement", (await agent.NextAsync("preview")).Secrets["proxmox.token"]);
    }

    [Fact]
    public async Task Targeted_guest_retry_keeps_the_original_credential_version()
    {
        using var repository = Definitions.GuestFilesystemTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        var growth = await agent.NextAsync("grow");
        await agent.ReportAsync(growth, Outcome.Failed, error: "controlled known failure");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "new-access" })).EnsureSuccessStatusCode();
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).EnsureSuccessStatusCode();
        var retry = await agent.NextAsync("grow");
        Assert.Equal(growth.Secrets["proxmox.token"], retry.Secrets["proxmox.token"]);
        Assert.NotEqual(growth.AttemptId, retry.AttemptId);
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Apply).Attempts);
    }

    [Fact]
    public async Task A_matching_worker_without_the_executor_has_a_specific_queue_reason()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Add(new AgentSession(agent.Id, "native-only", ["proxmox", "nixos-rootfs"]));
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var attempt = Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.Equal(AttemptStatuses.Queued, attempt.Status);
        Assert.Contains("advertising executor opentofu", attempt.WaitingReason);
    }

    [Fact]
    public async Task A_full_worker_channel_does_not_hold_up_revocation_or_release_after_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        var session = server.Services.GetRequiredService<AgentConnections>().Get(agent.Id)!;
        for (var i = 0; i < 256; i++)
        {
            Assert.True(session.TrySend(new ServerMessage()));
        }
        Assert.False(session.TrySend(new ServerMessage()));
        await server.SyncAndTrustAsync(client);
        await server.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var attempt = Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.Equal(AttemptStatuses.Queued, attempt.Status);
        Assert.Null(attempt.AssignedAt);
        Assert.Contains("channel is full", attempt.WaitingReason);
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = Token, version = 1, reason = "Revoke before hand-off" })).EnsureSuccessStatusCode();
        Assert.Empty(agent.TakeAssignments()); // Drains only the filler messages.
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task Revoking_a_completed_backend_credential_does_not_block_guest_verification()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = "opentofu/pg", version = 1, reason = "Retiring the state credential" })).EnsureSuccessStatusCode();
        var verify = await agent.NextAsync("verify-guest");
        Assert.DoesNotContain("backend.PG_CONN_STR", verify.Secrets.Keys);
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_plain_read_cannot_return_the_secret_written_during_that_read()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        const string key = "fixture/public-to-secret";
        await using var writer = server.Services.CreateAsyncScope();
        var store = writer.ServiceProvider.GetRequiredService<VariableStore>();
        await store.SetAsync(key, "public-original", Visibilities.Plain, null, CancellationToken.None);
        var interceptor = new AfterFirstEntryRead(() => store.SetAsync(key, "private-replacement", Visibilities.Secret, null, CancellationToken.None));
        var connection = await server.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()));
        await using var reader = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(connection).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options);
        var readingStore = new VariableStore(reader, server.Services.GetRequiredService<Envelope>(), TimeProvider.System);
        Assert.Equal("public-original", await readingStore.GetPlainAsync(key, CancellationToken.None));
        Assert.True(interceptor.Ran);
        Assert.Null(await readingStore.GetPlainAsync(key, CancellationToken.None));
        Assert.Equal(("private-replacement", 2), await store.GetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Revocation_blocks_queued_dispatch_without_substituting_the_new_active_version()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SyncAndTrustAsync(client);
        await server.TickAsync(); // Pins v1, but there is no connected worker.
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.True(RunContext.Parse(run.Context).SecretBindingsPinned);
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "new" })).EnsureSuccessStatusCode();
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = Token, version = 1, reason = "Retiring old automation credential" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("v1: revoked", run.Detail);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.Equal(HttpStatusCode.BadRequest, (await PromoteAsync(client, 1, 2)).StatusCode);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Revoking_the_active_version_does_not_fall_back_to_previous_values()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = Token, version = 1, reason = "Emergency revoke" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        var listing = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        Assert.True(listing!["entries"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == Token)!["revoked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Credential_channels_reject_plain_values()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
            await store.SetAsync(Token, "plain-token", Visibilities.Plain, null, CancellationToken.None);
        }
        await server.SetSecretAsync("opentofu/pg", "postgres://fixture");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Contains("must be stored as secret", (await server.LatestRunAsync(Guest, Operations.Provision)).Detail);
    }

    [Fact]
    public async Task Exact_candidate_binding_remains_blocked_until_its_promotion()
    {
        using var repository = TestRepository.Minimal();
        var path = Path.Combine(repository.Root, "labs/home/proxmox/korriban/installation.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{ store: proxmox/korriban/api-token }", "{ store: proxmox/korriban/api-token, version: 2 }"));
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await SignInAsync(server);
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "candidate" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Contains("candidate not promoted", (await server.LatestRunAsync(Guest, Operations.Provision)).Detail);
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        Assert.Equal("candidate", (await agent.NextAsync("preview")).Secrets["proxmox.token"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_runs_recover_from_unambiguous_release_evidence_or_block(bool ambiguous)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "current-but-not-planned" })).EnsureSuccessStatusCode();
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        await server.WithDbAsync(async db =>
        {
            var persisted = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var json = JsonNode.Parse(persisted.Context)!.AsObject();
            json.Remove("secretBindingsPinned");
            json.Remove("secretVersions");
            persisted.Context = json.ToJsonString();
            if (ambiguous)
            {
                db.SecretResolutions.Add(new SecretResolution
                {
                    Key = Token,
                    Version = 2,
                    AttemptId = Guid.Parse(plan.AttemptId),
                    AgentId = agent.Id,
                    ResolvedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        });
        if (ambiguous)
        {
            await server.TickAsync();
            Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
            Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        }
        else
        {
            Assert.Equal(plan.Secrets["proxmox.token"], (await agent.NextAsync("apply")).Secrets["proxmox.token"]);
        }
    }

    [Fact]
    public async Task Requeued_legacy_work_does_not_choose_a_new_CA_without_historical_evidence()
    {
        using var repository = TestRepository.Minimal();
        const string caKey = "proxmox/korriban/ca";
        var file = Path.Combine(repository.Root, "labs/home/proxmox/korriban/installation.yaml");
        File.WriteAllText(file, File.ReadAllText(file).Replace("tls: { verify: false }", $"tls: {{ verify: false, caCertificate: {{ store: {caKey} }} }}"));
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        await store.SetAsync(caKey, "old-public-CA-fixture", Visibilities.Plain, null, CancellationToken.None);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.NextAsync("preview"); // Token and backend resolved; CA was not released to OpenTofu.
        await server.Engine.OnHelloAsync(agent.Id, new Hello { AgentVersion = "test" }, CancellationToken.None);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Null(Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts).AssignedAt);
        await store.SetAsync(caKey, "new-public-CA-fixture", Visibilities.Plain, null, CancellationToken.None);
        await server.WithDbAsync(async db =>
        {
            var persisted = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = JsonNode.Parse(persisted.Context)!.AsObject();
            context.Remove("secretBindingsPinned");
            context.Remove("secretVersions");
            persisted.Context = context.ToJsonString();
            await db.SaveChangesAsync();
        });
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains(caKey + ": legacy run", run.Detail);
    }

    [Fact]
    public async Task System_keys_are_rejected_by_every_lifecycle_endpoint()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        const string key = "system/agent-ca/key";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/variables/candidates", new { key, value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/variables/promote", new { key, version = 2, expectedCurrentVersion = 1, verificationNote = "checked" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/variables/revoke", new { key, version = 1, reason = "reason" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/variables", new { key, description = "not allowed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/variables/versions?key=" + key)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_candidate_allocation_fails_cleanly_without_overwriting_a_version()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await using var a = server.Services.CreateAsyncScope();
        await using var b = server.Services.CreateAsyncScope();
        await a.ServiceProvider.GetRequiredService<OctopushyDb>().StoreEntries.SingleAsync(e => e.Key == Token);
        await b.ServiceProvider.GetRequiredService<OctopushyDb>().StoreEntries.SingleAsync(e => e.Key == Token);
        Assert.Equal(2, await a.ServiceProvider.GetRequiredService<VariableStore>().StageAsync(Token, "a", null, CancellationToken.None));
        await Assert.ThrowsAsync<StoreConflictException>(() =>
            b.ServiceProvider.GetRequiredService<VariableStore>().StageAsync(Token, "b", null, CancellationToken.None));
        Assert.Equal(2, await server.QueryAsync(db => db.StoreVersions.CountAsync(v => v.Key == Token)));
        using var client = await SignInAsync(server);
        (await PromoteAsync(client, 2, 1)).EnsureSuccessStatusCode();
        await using var check = server.Services.CreateAsyncScope();
        Assert.Equal(("a", 2), await check.ServiceProvider.GetRequiredService<VariableStore>().GetAsync(Token, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_promotions_compare_the_active_version_in_the_database()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await using var preparation = server.Services.CreateAsyncScope();
        var store = preparation.ServiceProvider.GetRequiredService<VariableStore>();
        await store.StageAsync(Token, "a", null, CancellationToken.None);
        await store.StageAsync(Token, "b", null, CancellationToken.None);
        await using var a = server.Services.CreateAsyncScope();
        await using var b = server.Services.CreateAsyncScope();
        await a.ServiceProvider.GetRequiredService<OctopushyDb>().StoreEntries.SingleAsync(e => e.Key == Token);
        await b.ServiceProvider.GetRequiredService<OctopushyDb>().StoreEntries.SingleAsync(e => e.Key == Token);
        await a.ServiceProvider.GetRequiredService<VariableStore>().PromoteAsync(Token, 2, 1, "External verification", "a", CancellationToken.None);
        await Assert.ThrowsAsync<StoreConflictException>(() =>
            b.ServiceProvider.GetRequiredService<VariableStore>().PromoteAsync(Token, 3, 1, "External verification", "b", CancellationToken.None));
        Assert.Equal(2, await server.QueryAsync(db => db.StoreEntries.Where(e => e.Key == Token).Select(e => e.CurrentVersion).SingleAsync()));
        Assert.Null(await server.QueryAsync(db => db.StoreVersions.Where(v => v.Key == Token && v.Version == 3).Select(v => v.ActivatedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_missing_encryption_key_on_an_existing_installation_is_not_recreated()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await using var scope = server.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OctopushyOptions>>().Value;
        var key = Path.Combine(server.DataDirectory, "keys", "kek");
        File.Move(key, key + ".test-backup");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KeyEncryptionKey.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<OctopushyDb>(), options, NullLogger.Instance, CancellationToken.None));
        Assert.Contains("Restore its original key", error.Message);
        Assert.False(File.Exists(key));
    }

    [Theory]
    [InlineData(true, true, "agent", "unusable on an existing installation")]
    [InlineData(true, true, "token", "unusable on an existing installation")]
    [InlineData(false, true, "agent", "unusable on an existing installation")]
    [InlineData(true, false, "token", "unusable on an existing installation")]
    [InlineData(true, true, "none", null)]
    [InlineData(false, true, "none", null)] // an interrupted first start: nothing depends on the half-written CA
    [InlineData(true, false, "none", null)]
    public async Task A_missing_agent_ca_is_only_replaced_while_nothing_depends_on_it(bool removeKey, bool removeCertificate, string dependents, string? refusal)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var ca = server.Services.GetRequiredService<AgentCertificateAuthority>();
        var fingerprint = ca.Fingerprint;
        if (dependents == "agent")
        {
            await server.ConnectAgentAsync("home");
        }
        else if (dependents == "token")
        {
            await server.WithDbAsync(async db =>
            {
                db.EnrollmentTokens.Add(new EnrollmentToken { Id = Guid.NewGuid(), TokenHash = "fixture", CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
                await db.SaveChangesAsync();
            });
        }

        var entries = new List<string>();
        if (removeKey) entries.Add("system/agent-ca/key");
        if (removeCertificate) entries.Add("system/agent-ca/certificate");
        await server.WithDbAsync(async db =>
        {
            await db.StoreVersions.Where(v => entries.Contains(v.Key)).ExecuteDeleteAsync();
            await db.StoreEntries.Where(e => entries.Contains(e.Key)).ExecuteDeleteAsync();
        });
        var remaining = await server.QueryAsync(db => db.StoreEntries.CountAsync(e => e.Key.StartsWith("system/agent-ca/")));

        if (refusal is null)
        {
            await ca.InitializeAsync(CancellationToken.None);
            Assert.NotEqual(fingerprint, ca.Fingerprint);
            Assert.Equal(2, await server.QueryAsync(db => db.StoreEntries.CountAsync(e => e.Key.StartsWith("system/agent-ca/"))));
            return;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ca.InitializeAsync(CancellationToken.None));
        Assert.Contains(refusal, error.Message);
        Assert.Contains("Restore", error.Message);
        Assert.Equal(fingerprint, ca.Fingerprint);
        Assert.Equal(remaining, await server.QueryAsync(db => db.StoreEntries.CountAsync(e => e.Key.StartsWith("system/agent-ca/"))));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "does not belong to")]
    public async Task An_agent_ca_key_that_does_not_belong_to_its_certificate_is_never_used(bool enrolled, string? refusal)
    {
        // What an interrupted repair leaves behind: a new private key stored next to the old certificate.
        await using var server = await ServerHarness.StartAsync(postgres);
        var ca = server.Services.GetRequiredService<AgentCertificateAuthority>();
        var fingerprint = ca.Fingerprint;
        if (enrolled)
        {
            await server.ConnectAgentAsync("home");
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            await scope.ServiceProvider.GetRequiredService<VariableStore>()
                .SetInternalAsync("system/agent-ca/key", other.ExportPkcs8PrivateKeyPem(), Visibilities.Secret, null, CancellationToken.None);
        }

        if (refusal is null)
        {
            await ca.InitializeAsync(CancellationToken.None);
            Assert.NotEqual(fingerprint, ca.Fingerprint);
            await ca.InitializeAsync(CancellationToken.None); // and the repaired pair is accepted as it is
            return;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ca.InitializeAsync(CancellationToken.None));
        Assert.Contains(refusal, error.Message);
        Assert.Equal(fingerprint, ca.Fingerprint);
    }

    [Fact]
    public async Task Model_snapshot_matches_the_migrated_model()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.WithDbAsync(db =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            return Task.CompletedTask;
        });
    }

    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, int version, int current, string note = "Verified intended access externally") =>
        client.PostAsJsonAsync("/api/variables/promote", new { key = Token, version, expectedCurrentVersion = current, verificationNote = note });

    private static async Task<HttpClient> SignInAsync(ServerHarness server)
    {
        var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long fixture password" })).EnsureSuccessStatusCode();
        return client;
    }

    private sealed class AfterFirstEntryRead(Func<Task> change) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.CommandText.Contains("store_entries", StringComparison.Ordinal))
            {
                Ran = true;
                await change();
            }
            return result;
        }
    }
}
