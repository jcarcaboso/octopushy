using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Notifications;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// The webhook: each kind it sends by default is delivered once, a receiver that fails is tried again later without holding
/// up the engine, and its URL and signing secret never leave the server. See docs/notifications.md.
/// </summary>
public class WebhookTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Url = "https://hooks.example.test/T0KEN-path/abc?key=q5ecret";
    private const string PublicUrl = "https://octopushy.test/";
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A receiver in the process: records each POST and answers as the test says.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpRequestMessage Request, byte[] Body)> Received { get; } = new();

        public Func<HttpStatusCode> Answer { get; set; } = () => HttpStatusCode.NoContent;

        public string AnswerBody { get; set; } = "";

        public List<JsonObject> Events => Received.Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Received.Enqueue((request, body));
            return new HttpResponseMessage(Answer()) { Content = new StringContent(AnswerBody) };
        }

        // The server's transport disposes its handler when the server stops; this one outlives a restart.
        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>Every line any logger of the server writes, exceptions included.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture capture, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                capture.Lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }

    /// <summary>Counts the SQL commands every context of the server sends, as <see cref="IdleTickTests"/> does.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required ServerHarness Server { get; init; }
        public required Receiver Receiver { get; init; }
        public required LogCapture Logs { get; init; }
        public required CommandCounter Commands { get; init; }
        public TestClock Clock { get; init; } = null!;
        public string Secret { get; set; } = "";

        public WebhookDelivery Delivery => Server.Services.GetRequiredService<WebhookDelivery>();

        /// <summary>Sends what is due, until the sender says to wait. Returns how long.</summary>
        public async Task<TimeSpan> SendAsync()
        {
            for (var i = 0; i < 200; i++)
            {
                var wait = await Delivery.SendNextAsync(CancellationToken.None);
                if (wait > TimeSpan.Zero)
                    return wait;
            }

            throw new InvalidOperationException("The sender never waited.");
        }

        public async Task<JsonObject> ConfigureAsync(string url = Url, string[]? kinds = null)
        {
            var http = await Server.SignedInClientAsync();
            var response = await http.PutAsJsonAsync("/api/notifications/webhook", new { url, kinds });
            response.EnsureSuccessStatusCode();
            var saved = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Secret = saved["secret"]!.GetValue<string>();
            return saved;
        }

        /// <summary>Adds a notification as the hub does, in a save of its own.</summary>
        public async Task<long> NotifyAsync(string kind = NotificationKinds.RunFailed, DateTimeOffset? at = null)
        {
            var notification = new Notification { Kind = kind, Title = "Provision run failed", CreatedAt = at ?? Clock.GetUtcNow() };
            await Server.WithDbAsync(async db =>
            {
                db.Notifications.Add(notification);
                await db.SaveChangesAsync();
            });
            return notification.Id;
        }

        public Task<WebhookState> StateAsync() => Server.QueryAsync(db => db.Webhooks.AsNoTracking().SingleAsync());

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private async Task<Setup> StartAsync(TestRepository? repository = null)
    {
        var receiver = new Receiver();
        var logs = new LogCapture();
        var commands = new CommandCounter();
        var clock = new TestClock { Now = Start };
        var transport = new WebhookTransport(receiver);
        var server = await ServerHarness.StartAsync(postgres, repository, clock: clock,
            settings: new() { ["Octopushy:PublicUrl"] = PublicUrl },
            services: s =>
            {
                s.AddSingleton(transport);
                s.AddSingleton<ILoggerProvider>(logs);
                s.ConfigureDbContext<OctopushyDb>(o => o.AddInterceptors(commands));
            });
        return new Setup { Server = server, Receiver = receiver, Logs = logs, Commands = commands, Clock = clock };
    }

    private static void AssertNoTool(JsonObject body)
    {
        Assert.Equal(["id", "kind", "test", "at", "title", "run", "resource", "agent", "url"], body.Select(p => p.Key));
        Assert.DoesNotContain("Unsupported", body.ToJsonString());
    }

    [Fact]
    public async Task A_run_that_fails_is_delivered_once_with_its_run_resource_and_link()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        await setup.ConfigureAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "Error: Unsupported argument");
        await server.TickAsync();
        await setup.SendAsync();
        await setup.SendAsync();

        var run = await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Status == RunStatuses.Failed));
        var resource = await server.ResourceAsync("home/korriban/krb-app-01");
        var (request, raw) = Assert.Single(setup.Receiver.Received);
        var body = JsonNode.Parse(raw)!.AsObject();
        AssertNoTool(body);
        Assert.Equal("run-failed", body["kind"]!.GetValue<string>());
        Assert.False(body["test"]!.GetValue<bool>());
        Assert.Equal("Provision run failed", body["title"]!.GetValue<string>());
        Assert.Equal(run.Id.ToString(), body["run"]!["id"]!.GetValue<string>());
        Assert.Equal(run.Number, body["run"]!["number"]!.GetValue<long>());
        Assert.Equal(resource.Id.ToString(), body["resource"]!["id"]!.GetValue<string>());
        Assert.Equal("guest", body["resource"]!["kind"]!.GetValue<string>());
        Assert.Equal(resource.Address, body["resource"]!["address"]!.GetValue<string>());
        Assert.Null(body["agent"]);
        Assert.Equal($"https://octopushy.test/runs/{run.Id}", body["url"]!.GetValue<string>());
        Assert.Equal(body["id"]!.GetValue<string>(), request.Headers.GetValues(Webhook.EventHeader).Single());
        Assert.Equal(Url, request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_run_that_waits_for_approval_is_delivered_once()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var setup = await StartAsync(repository);
        var server = setup.Server;
        await setup.ConfigureAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await server.TickAsync();
        await setup.SendAsync();

        var body = Assert.Single(setup.Receiver.Events);
        AssertNoTool(body);
        Assert.Equal("run-approval", body["kind"]!.GetValue<string>());
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Equal(run.Id.ToString(), body["run"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_whose_outcome_is_uncertain_is_delivered_once()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        await setup.ConfigureAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Uncertain, error: "The agent cannot tell.");
        await server.TickAsync();
        await setup.SendAsync();

        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision)).Status);
        var body = Assert.Single(setup.Receiver.Events);
        AssertNoTool(body);
        Assert.Equal("run-uncertain", body["kind"]!.GetValue<string>());
        Assert.DoesNotContain("cannot tell", body.ToJsonString());
    }

    [Fact]
    public async Task An_agent_away_past_the_grace_period_is_delivered_offline_once_and_online_once_and_a_short_drop_is_not()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        var clock = setup.Clock;
        var presence = server.Services.GetRequiredService<AgentPresence>();
        var connections = server.Services.GetRequiredService<AgentConnections>();
        await setup.ConfigureAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ConnectedAt, Start).SetProperty(a => a.LastSeenAt, Start)));

        async Task AwayAsync(TimeSpan after)
        {
            connections.Remove(agent.Session);
            var at = clock.GetUtcNow();
            await server.WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.LastSeenAt, at)));
            clock.Now = at + after;
            await presence.CheckAsync(CancellationToken.None);
        }

        void Back() => connections.Add(new AgentSession(agent.Id, agent.Session.Name, agent.Session.Executors));

        // A restart of the agent: back within the grace period, and checked every minute meanwhile.
        clock.Now = Start + TimeSpan.FromMinutes(10);
        await AwayAsync(TimeSpan.FromMinutes(1));
        await AwayAsync(AgentPresence.OfflineAfter - TimeSpan.FromMinutes(2));
        Back();
        clock.Now += TimeSpan.FromMinutes(30);
        await presence.CheckAsync(CancellationToken.None);
        await setup.SendAsync();
        Assert.Empty(setup.Receiver.Received);

        // Gone for good.
        connections.Remove(connections.Get(agent.Id)!);
        await AwayAsync(AgentPresence.OfflineAfter + TimeSpan.FromSeconds(1));
        clock.Now += TimeSpan.FromMinutes(1);
        await presence.CheckAsync(CancellationToken.None);
        await setup.SendAsync();
        var offline = Assert.Single(setup.Receiver.Events);
        AssertNoTool(offline);
        Assert.Equal("agent-offline", offline["kind"]!.GetValue<string>());
        Assert.Equal(agent.Id.ToString(), offline["agent"]!["id"]!.GetValue<string>());
        Assert.Equal("fake", offline["agent"]!["name"]!.GetValue<string>());
        Assert.Equal("https://octopushy.test/agents", offline["url"]!.GetValue<string>());
        Assert.Null(offline["run"]);

        Back();
        await presence.CheckAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(1);
        await presence.CheckAsync(CancellationToken.None);
        await setup.SendAsync();
        Assert.Equal(["agent-offline", "agent-online"], setup.Receiver.Events.Select(e => e["kind"]!.GetValue<string>()));
        // The bell shows them too.
        var listed = (await (await server.SignedInClientAsync()).GetFromJsonAsync<JsonObject>("/api/notifications"))!["items"]!.AsArray();
        Assert.Equal(agent.Id, listed[0]!["agentId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task A_disabled_agent_and_one_never_connected_are_not_told_offline()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        await setup.ConfigureAsync();
        var never = await server.ConnectAgentAsync("home");
        var disabled = await server.ConnectAgentAsync("home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Remove(never.Session);
        connections.Remove(disabled.Session);
        await server.WithDbAsync(db => db.Agents.Where(a => a.Id == disabled.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, true).SetProperty(a => a.ConnectedAt, Start).SetProperty(a => a.LastSeenAt, Start)));
        setup.Clock.Now = Start + TimeSpan.FromHours(1);
        await server.Services.GetRequiredService<AgentPresence>().CheckAsync(CancellationToken.None);
        await setup.SendAsync();
        Assert.Empty(setup.Receiver.Received);
    }

    [Fact]
    public async Task An_agent_update_that_fails_is_delivered_once()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        await setup.ConfigureAsync();
        var agent = await server.ConnectAgentAsync("home");
        // As a server leaves it when it stops between claiming the update and recording its change.
        await server.WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(u => u
            .SetProperty(a => a.UpdateState, AgentUpdateStates.Updating).SetProperty(a => a.UpdateTargetVersion, AgentUpdates.TargetVersion)));
        await server.Services.GetRequiredService<AgentUpdates>().AdvanceAsync(CancellationToken.None);
        await server.Services.GetRequiredService<AgentUpdates>().AdvanceAsync(CancellationToken.None);
        await setup.SendAsync();

        var body = Assert.Single(setup.Receiver.Events);
        AssertNoTool(body);
        Assert.Equal("agent-update-failed", body["kind"]!.GetValue<string>());
        Assert.Equal("Update of agent fake failed", body["title"]!.GetValue<string>());
        Assert.Equal(agent.Id.ToString(), body["agent"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain("stopped before", body.ToJsonString());
    }

    [Fact]
    public async Task A_failing_receiver_is_retried_with_backoff_later_events_follow_and_the_engine_never_notices()
    {
        await using var setup = await StartAsync();
        var server = setup.Server;
        var saves = server.Services.GetRequiredService<SavedWrites>();
        await setup.ConfigureAsync();
        await server.TickAsync();
        var before = setup.Commands.Count;
        await server.TickAsync();
        var idleTick = setup.Commands.Count - before;

        setup.Receiver.Answer = () => HttpStatusCode.ServiceUnavailable;
        setup.Receiver.AnswerBody = "down\u001b[31m for maintenance " + new string('x', 5000);
        var first = await setup.NotifyAsync();
        var second = await setup.NotifyAsync();
        var counted = saves.Count;

        Assert.Equal(TimeSpan.FromSeconds(10), await setup.SendAsync());
        // Not due yet: nothing is sent.
        Assert.True(await setup.SendAsync() > TimeSpan.Zero);
        Assert.Single(setup.Receiver.Received);
        setup.Clock.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(20), await setup.SendAsync());
        setup.Clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(TimeSpan.FromSeconds(40), await setup.SendAsync());
        Assert.Equal(3, setup.Receiver.Received.Count);

        // While the receiver is down, a tick costs what it did and nothing the sender writes wakes the engine.
        before = setup.Commands.Count;
        await server.TickAsync();
        Assert.Equal(idleTick, setup.Commands.Count - before);
        Assert.Equal(counted, saves.Count);

        var state = await setup.StateAsync();
        Assert.Equal(3, state.Attempts);
        Assert.False(state.LastDelivered);
        Assert.Equal(503, state.LastStatus);
        Assert.StartsWith("HTTP 503: down[31m for maintenance", state.LastResult);
        Assert.True(state.LastResult!.Length <= 120);
        Assert.DoesNotContain('\u001b', state.LastResult);

        setup.Receiver.Answer = () => HttpStatusCode.OK;
        setup.Clock.Now += TimeSpan.FromSeconds(40);
        await setup.SendAsync();
        var ids = setup.Receiver.Events.Select(e => e["id"]!.GetValue<string>()).ToList();
        Assert.Equal([$"{first}", $"{first}", $"{first}", $"{first}", $"{second}"], ids);
        state = await setup.StateAsync();
        Assert.Equal(second, state.Position);
        Assert.Equal(0, state.Attempts);
        Assert.True(state.LastDelivered);
        Assert.Equal(counted, saves.Count);
    }

    [Fact]
    public async Task An_event_the_receiver_keeps_refusing_is_dropped_and_the_next_one_delivered()
    {
        await using var setup = await StartAsync();
        await setup.ConfigureAsync();
        var refused = await setup.NotifyAsync();
        var next = await setup.NotifyAsync();
        setup.Receiver.Answer = () => setup.Receiver.Received.Count <= WebhookDelivery.RefusedAttempts ? HttpStatusCode.BadRequest : HttpStatusCode.Accepted;

        for (var i = 0; i < WebhookDelivery.RefusedAttempts; i++)
            setup.Clock.Now += await setup.SendAsync();
        await setup.SendAsync();

        Assert.Equal(Enumerable.Repeat($"{refused}", WebhookDelivery.RefusedAttempts).Append($"{next}"),
            setup.Receiver.Events.Select(e => e["id"]!.GetValue<string>()));
        var state = await setup.StateAsync();
        Assert.Equal(1, state.Dropped);
        Assert.Equal(next, state.Position);
    }

    [Fact]
    public async Task Old_events_are_dropped_and_a_long_backlog_keeps_only_its_latest()
    {
        await using var setup = await StartAsync();
        await setup.ConfigureAsync();
        var stale = await setup.NotifyAsync(at: Start - WebhookDelivery.MaxAge - TimeSpan.FromMinutes(1));
        await setup.SendAsync();
        Assert.Empty(setup.Receiver.Received);
        Assert.Equal(1, (await setup.StateAsync()).Dropped);

        // A receiver back after an hour away: 60 events wait, and only the latest 50 are sent.
        var ids = new List<long>();
        for (var i = 0; i < 60; i++)
            ids.Add(await setup.NotifyAsync(at: Start - TimeSpan.FromMinutes(60 - i)));
        await setup.SendAsync();
        Assert.Equal(ids.Skip(10).Select(i => $"{i}"), setup.Receiver.Events.Select(e => e["id"]!.GetValue<string>()));
        Assert.Equal(11, (await setup.StateAsync()).Dropped);
        Assert.True(stale < ids[0]);
    }

    [Fact]
    public async Task A_receiver_that_answers_gone_stops_sending_until_the_url_is_set_again()
    {
        await using var setup = await StartAsync();
        await setup.ConfigureAsync();
        setup.Receiver.Answer = () => HttpStatusCode.Gone;
        await setup.NotifyAsync();
        await setup.SendAsync();
        await setup.NotifyAsync();
        await setup.SendAsync();
        Assert.Single(setup.Receiver.Received);

        var shown = (await (await setup.Server.SignedInClientAsync()).GetFromJsonAsync<JsonObject>("/api/notifications/webhook"))!;
        Assert.False(shown["enabled"]!.GetValue<bool>());
        Assert.True(shown["configured"]!.GetValue<bool>());
        Assert.Equal(410, shown["lastStatus"]!.GetValue<int>());
        Assert.NotNull(shown["stoppedAt"]);

        // Set again, it starts from now: what was told while it was stopped is not sent.
        setup.Receiver.Answer = () => HttpStatusCode.NoContent;
        await setup.ConfigureAsync();
        await setup.SendAsync();
        Assert.Single(setup.Receiver.Received);
        var after = await setup.NotifyAsync();
        await setup.SendAsync();
        Assert.Equal($"{after}", setup.Receiver.Events[^1]["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Setting_the_webhook_starts_from_now_and_does_not_replay_history()
    {
        await using var setup = await StartAsync();
        for (var i = 0; i < 3; i++)
            await setup.NotifyAsync();
        await setup.ConfigureAsync();
        await setup.SendAsync();
        Assert.Empty(setup.Receiver.Received);

        var after = await setup.NotifyAsync();
        // A kind it does not send is passed over.
        await setup.NotifyAsync(NotificationKinds.CheckDown);
        await setup.SendAsync();
        Assert.Equal([$"{after}"], setup.Receiver.Events.Select(e => e["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_restart_mid_queue_neither_loses_nor_repeats_and_a_retried_event_keeps_its_id()
    {
        await using var setup = await StartAsync();
        await setup.ConfigureAsync();
        var first = await setup.NotifyAsync();
        var second = await setup.NotifyAsync();
        var third = await setup.NotifyAsync();
        await setup.Delivery.SendNextAsync(CancellationToken.None);
        setup.Receiver.Answer = () => HttpStatusCode.BadGateway;
        await setup.Delivery.SendNextAsync(CancellationToken.None);

        await setup.Server.RestartFixtureAsync();
        setup.Receiver.Answer = () => HttpStatusCode.NoContent;
        setup.Clock.Now += WebhookDelivery.Backoff(1);
        await setup.SendAsync();

        var received = setup.Receiver.Received.ToList();
        Assert.Equal([$"{first}", $"{second}", $"{second}", $"{third}"], received.Select(r => r.Request.Headers.GetValues(Webhook.EventHeader).Single()));
        Assert.Equal(received[1].Body, received[2].Body);
    }

    [Fact]
    public async Task The_signature_verifies_with_the_secret_shown_once_and_not_with_another()
    {
        await using var setup = await StartAsync();
        var saved = await setup.ConfigureAsync();
        Assert.StartsWith("whsec_", setup.Secret);
        Assert.Null((await (await setup.Server.SignedInClientAsync()).GetFromJsonAsync<JsonObject>("/api/notifications/webhook"))!["secret"]);
        await setup.NotifyAsync();
        await setup.SendAsync();

        var (request, body) = Assert.Single(setup.Receiver.Received);
        var timestamp = long.Parse(request.Headers.GetValues(Webhook.TimestampHeader).Single());
        Assert.Equal(Start.ToUnixTimeSeconds(), timestamp);
        var signature = request.Headers.GetValues(Webhook.SignatureHeader).Single();
        // As a receiver checks it, with nothing but the documented recipe.
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(setup.Secret));
        var expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash([.. Encoding.ASCII.GetBytes($"{timestamp}."), .. body]));
        Assert.Equal(expected, signature);
        Assert.NotEqual(Webhook.Sign("whsec_another", timestamp, body), signature);
        Assert.NotEqual(Webhook.Sign(setup.Secret, timestamp + 1, body), signature);
        Assert.Equal("application/json; charset=utf-8", request.Content!.Headers.ContentType!.ToString());
        Assert.NotNull(saved["webhook"]);
    }

    [Fact]
    public async Task The_url_and_the_secret_are_never_returned_or_logged()
    {
        await using var setup = await StartAsync();
        var http = await setup.Server.SignedInClientAsync();
        var saved = await setup.ConfigureAsync();
        Assert.DoesNotContain("T0KEN", saved.ToJsonString());
        Assert.Equal("hooks.example.test", saved["webhook"]!["host"]!.GetValue<string>());
        setup.Receiver.Answer = () => HttpStatusCode.InternalServerError;
        await setup.NotifyAsync();
        await setup.SendAsync();
        (await http.PostAsync("/api/notifications/webhook/test", null)).EnsureSuccessStatusCode();
        // Kinds alone change; the URL and the secret stay.
        (await http.PutAsJsonAsync("/api/notifications/webhook", new { kinds = new[] { NotificationKinds.RunFailed } })).EnsureSuccessStatusCode();

        var answers = new[]
        {
            await http.GetStringAsync("/api/notifications/webhook"),
            await http.GetStringAsync("/api/variables"),
            await http.GetStringAsync("/api/notifications"),
        };
        foreach (var text in answers.Concat(setup.Logs.Lines))
        {
            Assert.DoesNotContain("T0KEN", text);
            Assert.DoesNotContain("q5ecret", text);
            Assert.DoesNotContain(setup.Secret, text);
        }

        Assert.Contains(setup.Logs.Lines, l => l.Contains("tried again"));
        // The operator endpoints for variables refuse the keys.
        var reveal = await http.PostAsJsonAsync("/api/variables/reveal", new { keys = new[] { Webhook.UrlKey, Webhook.SecretKey } });
        Assert.Equal(HttpStatusCode.BadRequest, reveal.StatusCode);
        Assert.DoesNotContain("T0KEN", await reveal.Content.ReadAsStringAsync());
        Assert.Equal([NotificationKinds.RunFailed], (await setup.StateAsync()).Kinds);
    }

    [Theory]
    [InlineData("http://hooks.example.test/x")]
    [InlineData("https://user:pass@hooks.example.test/x")]
    [InlineData("hooks.example.test/x")]
    [InlineData("https://hooks.example.test/x#frag")]
    [InlineData("ftp://hooks.example.test/x")]
    public async Task A_url_that_is_not_https_or_carries_credentials_is_refused_without_being_quoted(string url)
    {
        await using var setup = await StartAsync();
        var http = await setup.Server.SignedInClientAsync();
        var response = await http.PutAsJsonAsync("/api/notifications/webhook", new { url });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("hooks.example.test", await response.Content.ReadAsStringAsync());
        Assert.Null(await setup.Server.QueryAsync(db => db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == Webhook.UrlKey)));
    }

    [Fact]
    public async Task The_test_event_is_sent_at_once_past_the_queue_and_reports_what_the_receiver_said()
    {
        await using var setup = await StartAsync();
        var http = await setup.Server.SignedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/notifications/webhook/test", null)).StatusCode);
        await setup.ConfigureAsync();

        var result = (await (await http.PostAsync("/api/notifications/webhook/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(result["delivered"]!.GetValue<bool>());
        Assert.Equal(204, result["status"]!.GetValue<int>());
        var body = Assert.Single(setup.Receiver.Events);
        Assert.Equal("test", body["kind"]!.GetValue<string>());
        Assert.True(body["test"]!.GetValue<bool>());
        Assert.StartsWith("test-", body["id"]!.GetValue<string>());

        setup.Receiver.Answer = () => HttpStatusCode.Forbidden;
        setup.Receiver.AnswerBody = "no\r\nway";
        result = (await (await http.PostAsync("/api/notifications/webhook/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(result["delivered"]!.GetValue<bool>());
        Assert.Equal("HTTP 403: noway", result["result"]!.GetValue<string>());
        // Nothing is queued or recorded for it.
        var state = await setup.StateAsync();
        Assert.Null(state.LastAttemptAt);

        // From another site, refused before it is sent.
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/webhook/test");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(crossSite)).StatusCode);
        Assert.Equal(2, setup.Receiver.Received.Count);

        // A few in a row, then no more for the minute.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await http.PostAsync("/api/notifications/webhook/test", null)).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Clearing_the_webhook_deletes_its_url_and_secret_and_stops_sending()
    {
        await using var setup = await StartAsync();
        var http = await setup.Server.SignedInClientAsync();
        await setup.ConfigureAsync(kinds: [NotificationKinds.RunFailed, NotificationKinds.CheckDown]);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync("/api/notifications/webhook")).StatusCode);
        await setup.NotifyAsync();
        await setup.SendAsync();

        Assert.Empty(setup.Receiver.Received);
        Assert.False(await setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key.StartsWith("system/webhook/"))));
        var shown = (await http.GetFromJsonAsync<JsonObject>("/api/notifications/webhook"))!;
        Assert.False(shown["configured"]!.GetValue<bool>());
        Assert.Equal([NotificationKinds.CheckDown, NotificationKinds.RunFailed], shown["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).Order());
    }
}
