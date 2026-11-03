using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class KeyEncryptionKeyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Key_initialization_checks_orphaned_artifacts_even_when_the_database_is_empty()
    {
        await using var db = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);
        await db.Database.MigrateAsync();
        var directory = Path.Combine(Path.GetTempPath(), "octopushy-key-test-" + Guid.NewGuid().ToString("N"));
        var options = new OctopushyOptions { DataDirectory = directory };
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            await File.WriteAllTextAsync(Path.Combine(directory, "artifacts", "retained-plan"), "encrypted fixture");
            await Assert.ThrowsAsync<InvalidOperationException>(() => KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "keys", "kek")));
            File.Delete(Path.Combine(directory, "artifacts", "retained-plan"));
            await KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None);
            var key = await File.ReadAllBytesAsync(Path.Combine(directory, "keys", "kek"));
            Assert.Equal(KeyEncryptionKey.Size, key.Length);
            await KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(key, await File.ReadAllBytesAsync(Path.Combine(directory, "keys", "kek")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("/run/secrets/kek", false)]
    public void A_key_inside_the_data_directory_is_called_out_at_startup(string? keyFile, bool warns)
    {
        var data = Path.Combine(Path.GetTempPath(), "octopushy-warn-test");
        var options = new OctopushyOptions { DataDirectory = data, Store = { KeyFile = keyFile } };
        var logger = new CapturingLogger();
        KeyEncryptionKey.WarnIfBackedUpWithData(keyFile ?? Path.Combine(data, "keys", "kek"), options, logger);
        Assert.Equal(warns, logger.Warnings.Count == 1);
        Assert.All(logger.Warnings, w => Assert.Contains("Octopushy:Store:KeyFile", w));
        // A sibling directory with the same prefix is not inside the data directory.
        logger.Warnings.Clear();
        KeyEncryptionKey.WarnIfBackedUpWithData(data + "-keys/kek", options, logger);
        Assert.Empty(logger.Warnings);
        // A data directory at the filesystem root puts every absolute path inside it.
        KeyEncryptionKey.WarnIfBackedUpWithData("/keys/kek", new OctopushyOptions { DataDirectory = "/" }, logger);
        Assert.Single(logger.Warnings);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
