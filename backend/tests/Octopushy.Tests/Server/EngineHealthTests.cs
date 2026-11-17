using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class EngineHealthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Health_requires_a_fresh_completed_tick_and_a_database()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/health")).StatusCode);
        await server.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        clock.Now = clock.Now.Value.AddSeconds(61);
        var stale = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stale.StatusCode);
        Assert.Equal("{\"status\":\"unhealthy\"}", await stale.Content.ReadAsStringAsync());
        await server.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task A_missing_database_is_unhealthy_even_with_a_fresh_tick()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = server.CreateClient();
        await server.Engine.TickAsync(CancellationToken.None);
        var connectionString = await server.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var database = connection.Database!;
        // This fixture owns a disposable database. Never issue this against a retained installation.
        Assert.Matches("^t[0-9a-f]{32}$", database);
        connection.Database = "postgres";
        // Dropping the database also ends the ownership connection, and its watchdog then stops the server
        // (ServerOwnershipTests covers that). Stop the watchdog first, or under load it wins the race with the request below.
        await server.Services.GetRequiredService<ServerOwnership>().StopAsync(CancellationToken.None);
        await using (var admin = new NpgsqlConnection(connection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"unhealthy\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_transition_logs_ids_statuses_and_a_reason_without_diagnostic_or_output_text()
    {
        var provider = new CaptureLogs();
        await using var server = await ServerHarness.StartAsync(postgres,
            services: services => services.AddLogging(builder => builder.AddProvider(provider)));
        var run = new Run { Id = Guid.NewGuid(), ResourceId = Guid.NewGuid(), Operation = "test", Reason = "manual" };
        var step = new Step { Id = Guid.NewGuid(), RunId = run.Id, Name = "test", Kind = StepKinds.Agent };
        var attempt = new Attempt { Id = Guid.NewGuid(), RunId = run.Id, StepId = step.Id, Executor = "test", Action = "test" };
        await server.Engine.ExecuteAsync(async (db, _) =>
        {
            db.Runs.Add(run);
            db.Steps.Add(step);
            db.Attempts.Add(attempt);
            await db.SaveChangesAsync();
            run.Status = RunStatuses.Failed;
            run.Detail = "secret diagnostic";
            step.Status = StepStatuses.Failed;
            step.Outputs = "{\"token\":\"secret output\"}";
            attempt.Status = AttemptStatuses.Failed;
            attempt.Error = "secret error";
            await db.SaveChangesAsync();
            return 0;
        }, CancellationToken.None);

        foreach (var kind in new[] { "run", "step", "attempt" })
        {
            var previous = kind == "step" ? StepStatuses.Pending : AttemptStatuses.Queued;
            var entry = Assert.Single(provider.Entries, e =>
                Equals(e["Entity"], kind) && Equals(e["OldStatus"], previous) &&
                Equals(e["NewStatus"], "failed"));
            Assert.Equal(run.Id, entry["RunId"]);
            Assert.Equal("execution failed", entry["Reason"]);
            if (kind != "run")
                Assert.Equal(step.Id, entry["StepId"]);
            if (kind == "attempt")
                Assert.Equal(attempt.Id, entry["AttemptId"]);
        }
        Assert.DoesNotContain(provider.Entries.SelectMany(e => e.Values).OfType<string>(), v => v.Contains("secret"));
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<Dictionary<string, object?>> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(RunTransitionLog).FullName && logLevel == LogLevel.Information &&
                    state is IEnumerable<KeyValuePair<string, object?>> values)
                    entries.Enqueue(values.ToDictionary(v => v.Key, v => v.Value));
            }
        }
    }
}
