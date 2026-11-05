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
        { "1234", """{"vmid": 1.234e3}""", "a number as a database prints it" },
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
