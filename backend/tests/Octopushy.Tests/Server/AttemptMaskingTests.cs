using System.Collections;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>What an attempt reports is masked as it reads once decoded, and its log lines are written by one writer at a time.</summary>
[Collection(PostgresCollection.Name)]
public class AttemptMaskingTests(PostgresFixture postgres)
{
    public static TheoryData<string> Secrets => new()
    {
        "with \"quotes\" inside",
        @"back\slash\and\\more",
        "angle <brackets> & ampersands",
        "unicode é ü 中文 and a \u0001 control",
        "line one of it\nline two of it\r\nline three",
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public async Task Strings_are_masked_as_decoded_and_stay_strings(string secret)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, [secret]);

        // As an agent serializes it: every character JSON escapes is escaped, so the raw secret is not in the text.
        var reported = new JsonObject
        {
            ["echo"] = secret,
            ["nested"] = new JsonObject { ["list"] = new JsonArray($"prefix {secret} suffix", "ordinary") },
            ["plain"] = "ordinary output",
            ["count"] = 3,
            ["ok"] = true,
            ["none"] = null,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default });
        Assert.DoesNotContain(secret, reported);

        Assert.True(logs.TryMaskOutputs(attempt, reported, out var masked, out var withheld), withheld);
        var outputs = Assert.IsType<JsonObject>(masked);
        Assert.DoesNotContain(secret, Decoded(outputs));
        Assert.Equal("***", outputs["echo"]!.GetValue<string>());
        Assert.Equal("prefix *** suffix", outputs["nested"]!["list"]![0]!.GetValue<string>());
        Assert.Equal("ordinary output", outputs["plain"]!.GetValue<string>());
        // Nothing but strings is rewritten: what reads the other values keeps its types.
        Assert.Equal(3, outputs["count"]!.GetValue<int>());
        Assert.True(outputs["ok"]!.GetValue<bool>());
        Assert.True(outputs.ContainsKey("none") && outputs["none"] is null);
        Assert.DoesNotContain(secret, Decoded(JsonNode.Parse(outputs.ToJsonString())));
    }

    public static TheoryData<string, string, string> Withheld => new()
    {
        // secret, outputs, why
        { "first-secret", """{"first-secret": 1}""", "a property name" },
        { "first-secret", """{"a": {"x-first-secret-y": 1}}""", "part of a nested property name" },
        { "123456789", """{"port": 123456789}""", "a number" },
        { "true", """{"a": [1, {"hasChanges": true}]}""", "true, nested" },
        { "false", """[false]""", "false, in an array" },
        { "null", """{"v": null, "s": "null"}""", "null, nested" },
        { "null", """[1, null]""", "null, in an array" },
        { "null", "null", "a null root" },
        { """{"username":"alice","password":"hunter2"}""", """{"credential": {"password": "hunter2", "username": "alice"}}""", "a whole JSON object, keys in any order" },
        { """["alice","hunter2"]""", """{"pair": ["alice", "hunter2"]}""", "a whole JSON array" },
        { "ab\": \"cd", """{"ab": "cd"}""", "a name and its value, as jsonb prints them" },
        { "ab\":\"cd", """{"ab":"cd"}""", "a name and its value, compact" },
        { "ab\",\"cd", """["ab","cd"]""", "two array values" },
        { "1234", """{"vmid": 12345}""", "part of a number" },
    };

    [Theory]
    [MemberData(nameof(Withheld))]
    public async Task Outputs_with_a_secret_outside_a_string_are_withheld_whole(string secret, string outputs, string why)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, [secret]);
        Assert.False(logs.TryMaskOutputs(attempt, outputs, out var masked, out var withheld), why);
        Assert.Null(masked);
        Assert.Equal(OutputMasking.SecretOutsideStrings, withheld);
    }

    [Theory]
    [InlineData("""{"v": 1e41}""")]
    [InlineData("""{"v": 1e-41}""")]
    [InlineData("""{"v": 1.23456789012345678901234567890123456789012e2}""")]
    [InlineData("""{"a": [{"v": 2.5e+100}]}""")]
    [InlineData("""[1, 1e999999]""")]
    public async Task Outputs_with_a_number_too_long_to_write_as_the_database_keeps_it_are_withheld(string outputs)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, ["unrelated-secret"]);
        Assert.False(logs.TryMaskOutputs(attempt, outputs, out _, out var withheld));
        Assert.Equal(OutputMasking.NumberForm, withheld);

        // Plain numbers are kept as they are written, and an attempt that holds no secret keeps any number.
        Assert.True(logs.TryMaskOutputs(attempt, """{"cpu": 0.0123, "n": 1.50, "neg": -0.5, "zero": 0, "big": 98765098765098765098765}""", out var plain, out _));
        Assert.Equal("""{"cpu":0.0123,"n":1.50,"neg":-0.5,"zero":0,"big":98765098765098765098765}""", plain!.ToJsonString());
        var none = Guid.CreateVersion7();
        logs.RegisterSecrets(none, []);
        Assert.True(logs.TryMaskOutputs(none, outputs, out _, out _));
    }

    [Fact]
    public async Task A_number_is_rewritten_exactly_as_postgres_stores_it_and_then_checked()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        string[] numbers = ["4.2e-7", "1.50e1", "1e30", "1.234e30", "1e-30", "2.5e+3", "-0", "-0.00", "-0e5", "0e5", "1.250e-2", "0.5e1",
            "-1.5E-3", "123.456e-1", "9.99e40", "1e-40", "1.234567890123456789012345678901234567890e0"];
        foreach (var number in numbers)
        {
            var stored = await server.QueryAsync(db => db.Database.SqlQuery<string>($"SELECT ('[' || {number} || ']')::jsonb::text AS \"Value\"").SingleAsync());
            Assert.Equal(stored, $"[{OutputMasking.PlainForm(number)}]");
        }

        // Rewritten before it is checked: the secret is found in what would be stored.
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, ["1234"]);
        Assert.False(logs.TryMaskOutputs(attempt, """{"v": 1.234e30}""", out _, out var withheld));
        Assert.Equal(OutputMasking.SecretOutsideStrings, withheld);
        Assert.True(logs.TryMaskOutputs(attempt, """{"cpu": 4.2e-7, "s": 1.50e1, "z": -0.0}""", out var masked, out _));
        Assert.Equal("""{"cpu":0.00000042,"s":15.0,"z":0.0}""", masked!.ToJsonString());
    }

    [Fact]
    public async Task An_older_agents_small_float_is_kept_as_postgres_stores_it_and_an_unbounded_number_is_withheld()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "an ordinary writer credential");
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "1234");
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var installation = await server.ResourceAsync("home/korriban");

        // As an agent before 0.6.2 writes a nearly idle host's load.
        var older = await agent.NextAsync("observe");
        Assert.True(await agent.ReportAsync(older, Outcome.Succeeded,
            JsonNode.Parse("""{"hosts": [{"name": "korriban", "online": true, "cpu": 4.2e-7}], "ratio": 1.50e1, "n": 1.50}""")!.AsObject()));
        var kept = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(older.AttemptId)));
        Assert.Equal(AttemptStatuses.Succeeded, kept.Status);
        var stored = await server.QueryAsync(db => db.Observations.AsNoTracking().SingleAsync(o => o.AttemptId == kept.Id));
        Assert.Contains("\"cpu\": 0.00000042", stored.Data);
        Assert.Contains("\"ratio\": 15.0", stored.Data);
        Assert.Contains("\"n\": 1.50", stored.Data);
        Assert.Null((await server.ResourceAsync("home/korriban")).ObserveError);

        // One that cannot be written out briefly is withheld, as is one that would store the secret.
        foreach (var (outputs, reason) in new[] { ("""{"v": 1e50}""", OutputMasking.NumberForm), ("""{"v": 1.234e30}""", OutputMasking.SecretOutsideStrings) })
        {
            await server.Services.GetRequiredService<Planner>().ObserveAsync(installation.Id, CancellationToken.None);
            var observation = await agent.NextAsync("observe");
            Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, JsonNode.Parse(outputs)!.AsObject()));
            var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(observation.AttemptId)));
            Assert.Equal((AttemptStatuses.Failed, reason), (attempt.Status, attempt.Error));
            Assert.False(await server.QueryAsync(db => db.Observations.AnyAsync(o => o.AttemptId == attempt.Id)));
        }

        Assert.DoesNotContain(await server.QueryAsync(db => db.Observations.AsNoTracking().Select(o => o.Data).ToListAsync()), d => d.Contains("1234"));
    }

    [Fact]
    public async Task What_is_added_to_outputs_after_masking_is_checked_with_them()
    {
        // A plan's digest is added to its outputs once they are masked; here a secret is part of it.
        var digest = ArtifactStore.Sha256(FakeAgent.PlanArtifact().Content.Span);
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "an ordinary writer credential");
        await server.SetSecretAsync("proxmox/korriban/read-only-token", digest[..12]);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await agent.NextAsync("preview");
        Assert.True(await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact()));
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(plan.AttemptId)));
        Assert.Equal((AttemptStatuses.Failed, OutputMasking.SecretOutsideStrings), (attempt.Status, attempt.Error));
        Assert.Null(attempt.Outputs);
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Null(run.Steps.Single(s => s.Name == StepNames.Plan).Outputs);
        Assert.DoesNotContain(digest[..12], await client.GetStringAsync($"/api/runs/{run.Id}"));
        // The digest itself is kept as it was: the artifact's record is not rewritten.
        Assert.Equal(digest, await server.QueryAsync(db => db.Artifacts.Where(a => a.AttemptId == attempt.Id).Select(a => a.Sha256).SingleAsync()));
    }

    [Fact]
    public async Task An_observation_whose_assembled_data_holds_a_secret_is_withheld()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "an ordinary writer credential");
        // The observation reports nothing; the data kept is given a property of this name.
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "connection");
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(observation.AttemptId)));
        Assert.Equal((AttemptStatuses.Failed, OutputMasking.SecretOutsideStrings), (attempt.Status, attempt.Error));
        Assert.False(await server.QueryAsync(db => db.Observations.AnyAsync(o => o.AttemptId == attempt.Id)));
        Assert.Equal(OutputMasking.SecretOutsideStrings, (await server.ResourceAsync("home/korriban")).ObserveError);
    }

    [Fact]
    public async Task Masks_read_back_while_a_result_closes_the_attempt_are_not_left_behind()
    {
        var pause = new PauseOnce("secret_resolutions");
        await using var server = await ServerHarness.StartAsync(postgres,
            services: services => services.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(pause)));
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        var id = Guid.Parse(observation.AttemptId);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var masks = server.Services.GetRequiredService<AttemptMasks>();
        // As after a restart: the masks the attempt was handed out with are forgotten while it still runs.
        await logs.CloseAsync(id, CancellationToken.None);
        Assert.False(logs.Holds(id));

        // A late log starts reading them back, from an attempt that still runs, and waits before its query.
        pause.Arm();
        var reading = Task.Run(() => masks.EnsureAsync(id, CancellationToken.None));
        Assert.True(await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        // The result arrives meanwhile, settles the attempt and closes it.
        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        Assert.Equal(AttemptStatuses.Succeeded, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == id).Select(a => a.Status).SingleAsync()));

        // The read finishes from its stale snapshot: what it registers is only for its holder.
        pause.Release();
        var held = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(logs.Knows(id));
        held.Dispose();
        Assert.False(logs.Knows(id));
        Assert.False(logs.Holds(id));
    }

    [Fact]
    public async Task A_replay_of_a_result_whose_outputs_were_withheld_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "an ordinary reader credential");
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "true");
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        Assert.True(await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact()));
        var apply = await agent.NextAsync("apply");
        Assert.True(await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["ok"] = true }));
        var id = Guid.Parse(apply.AttemptId);
        var first = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(AttemptStatuses.Uncertain, first.Status);
        Assert.False(first.LateResult);

        Assert.True(await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["ok"] = true }));
        var replayed = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal((first.Status, first.FinishedAt, first.LateResult, first.Error), (replayed.Status, replayed.FinishedAt, replayed.LateResult, replayed.Error));
    }

    [Fact]
    public async Task Outputs_that_are_not_json_too_deep_or_named_twice_are_withheld_and_large_ones_are_masked_in_linear_time()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, ["secret", "other-secret"]);
        foreach (var json in new[] { "not json", """{"a": 1, "a": 2}""", """{"x": {"a": 1, "\u0061": 2}}""", new string('[', 65) + "1" + new string(']', 65) })
        {
            Assert.False(logs.TryMaskOutputs(attempt, json, out _, out var why));
            Assert.Equal(OutputMasking.NotJson, why);
        }

        // Many names that would all mask alike: withheld at the first, with nothing renamed.
        var colliding = new JsonObject();
        for (var i = 0; i < 16384; i++)
            colliding[string.Concat(Enumerable.Range(0, 14).Select(bit => (i & (1 << bit)) != 0 ? "secret" : "***"))] = i;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(logs.TryMaskOutputs(attempt, colliding.ToJsonString(), out _, out _));

        // A large output of many strings and a long one: every secret masked, in time proportional to its size.
        var large = new JsonObject { ["long"] = new string('x', 8 * 1024 * 1024) + "secret" };
        var items = new JsonArray();
        for (var i = 0; i < 100_000; i++)
            items.Add(new JsonObject { ["name"] = $"item {i} other-secret", ["n"] = i });
        large["items"] = items;
        Assert.True(logs.TryMaskOutputs(attempt, large.ToJsonString(), out var masked, out _));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        Assert.EndsWith("x***", masked!["long"]!.GetValue<string>());
        Assert.Equal("item 99999 ***", masked["items"]![99_999]!["name"]!.GetValue<string>());
        Assert.Equal(99_999, masked["items"]![99_999]!["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_secret_json_escapes_stays_out_of_outputs_observations_and_the_api()
    {
        const string Secret = "review-special<&\"\\credential";
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/read-only-token", Secret);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        Assert.Contains(Secret, observation.Secrets.Values);
        var id = Guid.Parse(observation.AttemptId);

        await agent.ReportAsync(observation, Outcome.Succeeded,
            new JsonObject { ["echo"] = Secret, ["plain"] = "ordinary output", ["cluster"] = new JsonObject { ["name"] = Secret } }, $"error {Secret}");

        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.DoesNotContain(Secret, Decoded(JsonNode.Parse(attempt.Outputs!)));
        Assert.Equal("***", JsonNode.Parse(attempt.Outputs!)!["echo"]!.GetValue<string>());
        Assert.Equal("ordinary output", JsonNode.Parse(attempt.Outputs!)!["plain"]!.GetValue<string>());
        Assert.Equal("error ***", attempt.Error);
        var observed = await server.QueryAsync(db => db.Observations.AsNoTracking().SingleAsync(o => o.AttemptId == id));
        Assert.DoesNotContain(Secret, Decoded(JsonNode.Parse(observed.Data)));
        var resource = await server.ResourceAsync("home/korriban");
        var installation = await client.GetFromJsonAsync<JsonObject>($"/api/installations/{resource.Id}");
        Assert.DoesNotContain(Secret, Decoded(installation));
        Assert.Equal("***", installation!["clusterName"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_close_between_two_writers_never_lets_a_second_writer_run_beside_the_first()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, []);
        await logs.AppendAsync(attempt, 0, [(0, "stdout", "first")], CancellationToken.None);

        // A writer holds the gate. A close, then a second writer, queue behind it in that order: each call reaches the gate's
        // wait before it returns.
        var holding = new BlockingLines("held while writing");
        var first = Task.Run(() => logs.AppendAsync(attempt, 1, holding, CancellationToken.None));
        Assert.True(holding.Entered.Wait(TimeSpan.FromSeconds(10)));
        var closing = logs.CloseAsync(attempt, CancellationToken.None);
        var queued = new BlockingLines("queued behind the close");
        var second = logs.AppendAsync(attempt, 2, queued, CancellationToken.None);
        Assert.False(closing.IsCompleted);
        Assert.False(second.IsCompleted);
        holding.Release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await closing.WaitAsync(TimeSpan.FromSeconds(10));

        // The queued writer now holds the same gate. One that comes after the close waits for it rather than taking another.
        Assert.True(queued.Entered.Wait(TimeSpan.FromSeconds(10)));
        logs.RegisterSecrets(attempt, []);
        var fresh = logs.AppendAsync(attempt, 3, [(0, "stdout", "after the close")], CancellationToken.None);
        Assert.False(fresh.IsCompleted);
        queued.Release.Set();
        await Task.WhenAll(second, fresh).WaitAsync(TimeSpan.FromSeconds(10));

        var lines = await logs.ReadAsync(attempt, -1, 10, CancellationToken.None);
        Assert.Equal([0L, 1L, 2L, 3L], lines.Select(l => l.Sequence));
        // The queued line was masked once the attempt was registered again, with no secrets: what it is masked with is read
        // as the line is written, under the gate. Close then append, with nothing registered, is withheld: see
        // VariableDeletionTests.
        Assert.Equal(["first", "held while writing", "queued behind the close", "after the close"], lines.Select(l => l.Text));

        // Deleting takes the same gate, and a later line starts the log again.
        await logs.DeleteAsync(attempt, CancellationToken.None);
        Assert.Empty(await logs.ReadAsync(attempt, -1, 10, CancellationToken.None));
        logs.RegisterSecrets(attempt, []);
        Assert.Equal(1, await logs.AppendAsync(attempt, 0, [(0, "stdout", "again")], CancellationToken.None));
    }

    [Fact]
    public async Task A_late_log_and_a_duplicate_result_of_a_finished_attempt_leave_nothing_held()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        var id = Guid.Parse(observation.AttemptId);
        var secret = observation.Secrets.Values.First();
        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        Assert.False(logs.Holds(id));

        // A late log line reads the masks back for itself, is masked with them, and they go with it.
        var masks = server.Services.GetRequiredService<AttemptMasks>();
        using (await masks.EnsureAsync(id, CancellationToken.None))
            await logs.AppendAsync(id, 0, [(0, "stderr", $"late {secret}")], CancellationToken.None);
        Assert.Equal("late ***", (await logs.ReadAsync(id, -1, 10, CancellationToken.None)).Single().Text);
        Assert.False(logs.Holds(id));

        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        Assert.False(logs.Holds(id));
    }

    [Fact]
    public async Task A_plan_whose_outputs_hold_a_secret_true_settles_failed_and_is_acknowledged()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "an ordinary writer credential");
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "true");
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await agent.NextAsync("preview");
        Assert.Contains("true", plan.Secrets.Values);

        Assert.True(await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact()));
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(plan.AttemptId)));
        Assert.Equal(AttemptStatuses.Failed, attempt.Status);
        Assert.Equal(OutputMasking.SecretOutsideStrings, attempt.Error);
        Assert.Null(attempt.Outputs);
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(StepStatuses.Failed, run.Steps.Single(s => s.Name == StepNames.Plan).Status);
        Assert.Null(run.Steps.Single(s => s.Name == StepNames.Plan).Outputs);
        (await client.GetAsync($"/api/runs/{run.Id}")).EnsureSuccessStatusCode();
        // A replay is acknowledged, and changes nothing.
        Assert.True(await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact()));
    }

    [Fact]
    public async Task An_observation_whose_vmid_is_a_secret_is_withheld_and_the_earlier_evidence_still_refuses_a_collision()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "an ordinary writer credential");
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "1234");
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        static JsonObject Guests(int vmid) => new()
        {
            ["guests"] = new JsonArray(new JsonObject { ["vmid"] = vmid, ["name"] = "foreign-vm", ["host"] = "korriban", ["template"] = false }),
        };

        // First an observation that shows a VM Octopushy did not create at the guest's VMID.
        var installation = await server.ResourceAsync("home/korriban");
        var first = await agent.NextAsync("observe");
        Assert.True(await agent.ReportAsync(first, Outcome.Succeeded, Guests(310)));
        var guest = await server.ResourceAsync("home/korriban/krb-app-01");
        Assert.NotNull(await server.QueryAsync(db => SafetyBoundary.VmidCollisionAsync(db, guest, "home/korriban", 310, CancellationToken.None)));

        // Then one whose outputs carry the read-only token as a VMID: withheld whole, so it says nothing.
        await server.Services.GetRequiredService<Planner>().ObserveAsync(installation.Id, CancellationToken.None);
        var second = await agent.NextAsync("observe");
        Assert.True(await agent.ReportAsync(second, Outcome.Succeeded, Guests(1234)));
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(second.AttemptId)));
        Assert.Equal(AttemptStatuses.Failed, attempt.Status);
        Assert.Null(attempt.Outputs);
        Assert.False(await server.QueryAsync(db => db.Observations.AnyAsync(o => o.AttemptId == attempt.Id)));
        Assert.Equal(OutputMasking.SecretOutsideStrings, (await server.ResourceAsync("home/korriban")).ObserveError);
        Assert.NotNull(await server.QueryAsync(db => SafetyBoundary.VmidCollisionAsync(db, guest, "home/korriban", 310, CancellationToken.None)));
        (await client.GetAsync("/api/inventory")).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/installations/{installation.Id}")).EnsureSuccessStatusCode();

        // A VMID that cannot be read is unknown, not absent: it may be this one.
        await server.WithDbAsync(async db =>
        {
            db.Observations.Add(new Observation
            {
                Id = Guid.CreateVersion7(), ResourceId = installation.Id, ObservedAt = DateTimeOffset.UtcNow.AddMinutes(1),
                Data = """{"guests": [{"vmid": "***", "name": "masked", "host": "korriban"}]}""",
            });
            await db.SaveChangesAsync();
        });
        Assert.NotNull(await server.QueryAsync(db => SafetyBoundary.VmidCollisionAsync(db, guest, "home/korriban", 311, CancellationToken.None)));

        // Nor is one that is there as null.
        await server.WithDbAsync(async db =>
        {
            db.Observations.Add(new Observation
            {
                Id = Guid.CreateVersion7(), ResourceId = installation.Id, ObservedAt = DateTimeOffset.UtcNow.AddMinutes(2),
                Data = """{"guests": [{"vmid": null, "host": "korriban"}]}""",
            });
            await db.SaveChangesAsync();
        });
        Assert.NotNull(await server.QueryAsync(db => SafetyBoundary.VmidCollisionAsync(db, guest, "home/korriban", 312, CancellationToken.None)));
    }

    /// <summary>Every string in a JSON value, decoded: property names and values alike.</summary>
    private static string Decoded(JsonNode? node) => node switch
    {
        JsonObject o => string.Join("\n", o.Select(p => p.Key + "\n" + Decoded(p.Value))),
        JsonArray a => string.Join("\n", a.Select(Decoded)),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        null => "",
        _ => node.ToJsonString(),
    };

    private static async Task<HttpClient> SignInAsync(ServerHarness server)
    {
        var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long fixture password" })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Holds the first command that reads <paramref name="table"/> after <see cref="Arm"/> until <see cref="Release"/>.</summary>
    private sealed class PauseOnce(string table) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.TrySetResult(true);
                await _release.Task;
            }

            return result;
        }
    }

    /// <summary>One line, handed over only once the test lets the writer go: it holds the gate meanwhile.</summary>
    private sealed class BlockingLines(string text) : IReadOnlyList<(long UnixMs, string Stream, string Text)>
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int Count => 1;

        public (long UnixMs, string Stream, string Text) this[int index]
        {
            get
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
                return (0, "stdout", text);
            }
        }

        public IEnumerator<(long UnixMs, string Stream, string Text)> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
