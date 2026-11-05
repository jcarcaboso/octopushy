using System.Collections;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;

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
    public async Task Outputs_are_masked_as_decoded_wherever_the_secret_is(string secret)
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
            [secret] = "a secret as a property name",
            ["plain"] = "ordinary output",
            ["count"] = 3,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default });
        Assert.DoesNotContain(secret, reported);

        Assert.True(logs.TryMaskOutputs(attempt, reported, out var masked));
        var outputs = Assert.IsType<JsonObject>(masked);
        Assert.DoesNotContain(secret, Decoded(outputs));
        Assert.Equal("***", outputs["echo"]!.GetValue<string>());
        Assert.Equal("prefix *** suffix", outputs["nested"]!["list"]![0]!.GetValue<string>());
        Assert.Equal("a secret as a property name", outputs["***"]!.GetValue<string>());
        Assert.Equal("ordinary output", outputs["plain"]!.GetValue<string>());
        Assert.Equal(3, outputs["count"]!.GetValue<int>());
        // And as stored, read back.
        Assert.DoesNotContain(secret, Decoded(JsonNode.Parse(outputs.ToJsonString())));
    }

    [Fact]
    public async Task Names_and_numbers_that_mask_alike_are_kept_apart_and_outputs_that_are_not_json_are_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.CreateVersion7();
        logs.RegisterSecrets(attempt, ["first-secret", "123456789"]);

        Assert.True(logs.TryMaskOutputs(attempt, """{"first-secret": 1, "***": 2, "port": 123456789, "ok": true}""", out var masked));
        Assert.Equal("""{"***":1,"***#2":2,"port":"***","ok":true}""", masked!.ToJsonString());
        Assert.ThrowsAny<JsonException>(() => logs.TryMaskOutputs(attempt, "not json", out _));
        Assert.ThrowsAny<JsonException>(() => logs.TryMaskOutputs(attempt, """{"a": 1, "a": 2}""", out _));
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

        // A writer holds the gate; a close and a second writer queue behind it.
        var holding = new BlockingLines("held while writing");
        var first = Task.Run(() => logs.AppendAsync(attempt, 1, holding, CancellationToken.None));
        Assert.True(holding.Entered.Wait(TimeSpan.FromSeconds(10)));
        var closing = Task.Run(() => logs.CloseAsync(attempt, CancellationToken.None));
        // The close queues first, so the writer below queues behind it.
        await Task.Delay(200);
        var queued = new BlockingLines("queued behind the close");
        var second = Task.Run(() => logs.AppendAsync(attempt, 2, queued, CancellationToken.None));
        await Task.Delay(200);
        Assert.False(closing.IsCompleted);
        holding.Release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await closing.WaitAsync(TimeSpan.FromSeconds(10));

        // The queued writer now holds the same gate. One that comes after the close waits for it rather than taking another.
        Assert.True(queued.Entered.Wait(TimeSpan.FromSeconds(10)));
        logs.RegisterSecrets(attempt, []);
        var fresh = Task.Run(() => logs.AppendAsync(attempt, 3, [(0, "stdout", "after the close")], CancellationToken.None));
        await Task.Delay(200);
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
