using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class CredentialMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Pre_upgrade_backup_restores_old_schema_and_keys_without_downgrading_the_upgraded_source()
    {
        await using var db = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020083859_Initial");
        var escrowKey = RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size);
        var envelope = new Envelope(new KeyEncryptionKey(escrowKey));
        var now = DateTimeOffset.UtcNow;
        const string key = "fixture/token";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO store_entries (key, visibility, description, current_version, created_at, updated_at)
            VALUES ({key}, 'secret', NULL, 2, {now}, {now})
            """);
        for (var version = 1; version <= 2; version++)
        {
            var value = System.Text.Encoding.UTF8.GetBytes($"legacy-value-{version}");
            var (ciphertext, wrapped) = envelope.Seal(value, $"store\0{key}\0{version}");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO store_versions (key, version, ciphertext, wrapped_key, created_at)
                VALUES ({key}, {version}, {ciphertext}, {wrapped}, {now})
                """);
        }
        var preUpgradeDump = await postgres.DumpAsync(db.Database.GetConnectionString()!);
        await migrator.MigrateAsync();
        var store = new VariableStore(db, envelope, TimeProvider.System);
        Assert.Equal(("legacy-value-2", 2), await store.GetAsync(key, CancellationToken.None));
        Assert.Equal(3, await store.StageAsync(key, "new-candidate", null, CancellationToken.None));
        Assert.Equal(("legacy-value-2", 2), await store.GetAsync(key, CancellationToken.None));
        var (values, missing) = await store.ResolveForAttemptAsync(
            new Dictionary<string, Octopushy.Definitions.StoreReference> { ["token"] = new(key, 1) },
            new HashSet<string> { "token" }, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(missing);
        Assert.Equal("legacy-value-1", values["token"]);
        Assert.False(db.Database.HasPendingModelChanges());

        // Downgrading would discard revocation/candidate semantics. Refusal must leave
        // both the applied migration and the newly allocated candidate intact.
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261020083859_Initial"));
        Assert.Contains("matched pre-upgrade database and key backup", error.Message);
        Assert.Contains("20261021180000_CredentialLifecycle", await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(3, await db.StoreVersions.CountAsync());

        // Restore the actual pre-upgrade archive into a different, fresh database.
        // It must not inherit the source database's later schema or candidate.
        await using var recovered = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.RestoreAsync(preUpgradeDump)).UseSnakeCaseNamingConvention().Options);
        Assert.Equal(["20261020083859_Initial"], await recovered.Database.GetAppliedMigrationsAsync());
        var legacy = await recovered.StoreVersions.Where(v => v.Key == key && v.Version == 2)
            .Select(v => new { v.Ciphertext, v.WrappedKey }).SingleAsync();
        Assert.Equal("legacy-value-2", System.Text.Encoding.UTF8.GetString(envelope.Open(legacy.Ciphertext, legacy.WrappedKey, $"store\0{key}\0{2}")));
        await recovered.Database.MigrateAsync();
        var recoveredStore = new VariableStore(recovered, new Envelope(new KeyEncryptionKey(escrowKey.ToArray())), TimeProvider.System);
        Assert.Equal(("legacy-value-2", 2), await recoveredStore.GetAsync(key, CancellationToken.None));
        Assert.Equal(2, await recovered.StoreVersions.CountAsync());
        Assert.All(await recovered.StoreVersions.ToListAsync(), v => Assert.Equal("legacy", v.ActivatedBy));
        Assert.Equal(3, await recoveredStore.StageAsync(key, "independent-recovered-candidate", null, CancellationToken.None));
        Assert.Equal(("legacy-value-2", 2), await recoveredStore.GetAsync(key, CancellationToken.None));
    }

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
