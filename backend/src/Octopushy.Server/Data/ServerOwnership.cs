using Npgsql;

namespace Octopushy.Server.Data;

/// <summary>Enforces the engine's single-server assumption for the lifetime of this process.</summary>
public sealed class ServerOwnership(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<ServerOwnership> logger)
    : BackgroundService
{
    private const long LockKey = 0x4f63746f70757368;
    private NpgsqlConnection? _connection;

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Octopushy"))
        {
            Pooling = false,
            ApplicationName = "octopushy-server-lock",
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", LockKey);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("Another Octopushy server owns this database. Only one server may run at a time.");
            _connection = connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync();
            logger.LogCritical(e, "Could not acquire exclusive server ownership of the database.");
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var command = new NpgsqlCommand("SELECT 1", _connection) { CommandTimeout = 5 };
                await command.ExecuteScalarAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "The database connection holding exclusive server ownership was lost; stopping the server.");
            lifetime.StopApplication();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection?.Dispose();
    }
}
