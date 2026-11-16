using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ServerOwnershipTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_second_server_is_refused_and_the_first_keeps_working()
    {
        await using var first = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var connectionString = await first.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var second = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Octopushy", connectionString);
            builder.UseSetting("Octopushy:DataDirectory", first.DataDirectory);
            builder.UseSetting("Octopushy:RunBackgroundServices", "false");
        });
        var error = Assert.Throws<InvalidOperationException>(() => _ = second.Server);
        Assert.Contains("Another Octopushy server owns this database", error.ToString());
        await first.TickAsync();
        using var client = first.CreateClient();
        (await client.GetAsync("/api/health")).EnsureSuccessStatusCode();
        Assert.Equal(0, await first.QueryAsync(db => db.Runs.CountAsync()));
    }

    [Fact]
    public async Task Losing_the_ownership_connection_stops_the_application()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
        var connectionString = await server.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND application_name = 'octopushy-server-lock'
            """, connection);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
