using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class RecoveryTests(PostgresFixture postgres) : IDisposable
{
    private const string Token = "fixture/recovery-token";
    private readonly string _directory = Directory.CreateTempSubdirectory("octopushy-recovery-").FullName;

    [Fact]
    public async Task Matched_dump_and_key_restore_credential_versions_without_adopting_later_writes()
    {
        await using var source = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await using var scope = source.Services.CreateAsyncScope();
        var original = scope.ServiceProvider.GetRequiredService<VariableStore>();
        var sourceDb = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        await original.SetAsync(Token, "fixture-old", Visibilities.Secret, null, CancellationToken.None);
        await original.StageAsync(Token, "fixture-active", null, CancellationToken.None);
        await original.PromoteAsync(Token, 2, 1, "Synthetic verification", "fixture", CancellationToken.None);
        await original.RevokeAsync(Token, 1, "Synthetic retirement", "fixture", CancellationToken.None);
        await original.StageAsync(Token, "fixture-candidate", null, CancellationToken.None);
        var captured = await sourceDb.StoreVersions.AsNoTracking().Where(v => v.Key == Token).OrderBy(v => v.Version).ToListAsync();

        var keyPath = Path.Combine(_directory, "escrow-key");
        File.Copy(Path.Combine(source.DataDirectory, "keys", "kek"), keyPath);
        var dump = await postgres.DumpAsync(sourceDb.Database.GetConnectionString()!);
        await original.StageAsync(Token, "written-after-backup", null, CancellationToken.None);
        await original.RevokeAsync(Token, 2, "Revoked after backup", "fixture", CancellationToken.None);
        Assert.Null(await original.GetAsync(Token, CancellationToken.None));

        await using var restored = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.RestoreAsync(dump)).UseSnakeCaseNamingConvention().Options);
        var options = Options.Create(new OctopushyOptions { DataDirectory = Path.Combine(_directory, "data"), Store = { KeyFile = keyPath } });
        var envelope = new Envelope(new KeyEncryptionKey(options, NullLogger<KeyEncryptionKey>.Instance));
        var store = new VariableStore(restored, envelope, TimeProvider.System);

        Assert.Equal(("fixture-active", 2), await store.GetAsync(Token, CancellationToken.None));
        Assert.Null(await store.GetPlainAsync(Token, CancellationToken.None));
        var versions = await restored.StoreVersions.Where(v => v.Key == Token).OrderBy(v => v.Version).ToListAsync();
        Assert.Equal([1, 2, 3], versions.Select(v => v.Version));
        Assert.NotNull(versions[0].RevokedAt);
        Assert.Equal("Synthetic retirement", versions[0].RevocationNote);
        Assert.Equal("fixture", versions[1].ActivatedBy);
        Assert.Null(versions[2].ActivatedAt);
        foreach (var (version, expected) in versions.Zip(new[] { "fixture-old", "fixture-active", "fixture-candidate" }))
        {
            Assert.Equal(captured[version.Version - 1].Ciphertext, version.Ciphertext);
            Assert.Equal(captured[version.Version - 1].WrappedKey, version.WrappedKey);
            Assert.Equal(expected, Encoding.UTF8.GetString(envelope.Open(version.Ciphertext, version.WrappedKey, $"store\0{Token}\0{version.Version}")));
        }
        await restored.Database.MigrateAsync();
        Assert.Equal(3, await restored.StoreVersions.CountAsync(v => v.Key == Token));
        Assert.Equal(4, await store.StageAsync(Token, "independent-restored-write", null, CancellationToken.None));
        foreach (var version in new[] { 1, 3 })
        {
            var (values, missing) = await store.ResolveForAttemptAsync(
                new Dictionary<string, StoreReference> { ["token"] = new(Token, version) },
                new HashSet<string> { "token" }, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
            Assert.Empty(values);
            Assert.Single(missing);
        }
    }

    [Fact]
    public async Task Restore_preserves_pinned_uncertain_work_approved_plan_logs_and_ca_identity()
    {
        await using var source = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await source.SetSecretAsync(Token, "fixture-token");
        var agent = await source.ConnectAgentAsync("recovery-fixture");
        var ca = source.Services.GetRequiredService<AgentCertificateAuthority>();
        using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=fixture", agentKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        var issued = ca.IssueAgentCertificate(csr, agent.Id);
        using var certificate = X509CertificateLoader.LoadCertificate(issued.Der);
        var runId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var applyId = Guid.NewGuid();
        var content = "synthetic saved plan, not executable infrastructure"u8.ToArray();
        var digest = ArtifactStore.Sha256(content);
        var context = new RunContext
        {
            Address = "fixture.host.guest",
            Proxmox = new("https://proxmox.invalid", true, null, "host", 999, "fixture", null),
            Secrets = new() { ["token"] = Token },
            SecretVersions = new() { ["token"] = 1 },
            SecretBindingsPinned = true,
        };
        await source.WithDbAsync(async db =>
        {
            var registered = await db.Agents.SingleAsync(a => a.Id == agent.Id);
            registered.CertificateSerial = issued.Serial;
            registered.CertificateExpiresAt = issued.NotAfter;
            db.Resources.Add(new Resource { Id = resourceId, Address = context.Address, Kind = ResourceKinds.Guest, Lab = "fixture" });
            db.Runs.Add(new Run
            {
                Id = runId,
                ResourceId = resourceId,
                Operation = Operations.Provision,
                Reason = "manual",
                Status = RunStatuses.Uncertain,
                Context = context.Serialize(),
                CreatedAt = DateTimeOffset.UtcNow,
                Steps =
                [
                    new Step
                    {
                        Id = Guid.NewGuid(), Ordinal = 0, Name = StepNames.Plan, Kind = StepKinds.Agent, Status = StepStatuses.Succeeded,
                        Attempts = [new Attempt { Id = planId, RunId = runId, Number = 1, Executor = "opentofu", Action = "preview", Status = AttemptStatuses.Succeeded, AgentId = agent.Id }],
                    },
                    new Step
                    {
                        Id = Guid.NewGuid(), Ordinal = 1, Name = StepNames.Approval, Kind = StepKinds.Approval,
                        Status = StepStatuses.Succeeded, Decision = "approved", DecidedBy = "fixture", PlanSha256 = digest,
                    },
                    new Step
                    {
                        Id = Guid.NewGuid(), Ordinal = 2, Name = StepNames.Apply, Kind = StepKinds.Agent, Status = StepStatuses.Uncertain,
                        Attempts = [new Attempt { Id = applyId, RunId = runId, Number = 1, Executor = "opentofu", Action = "apply", Mutating = true, Status = AttemptStatuses.Uncertain, AgentId = agent.Id, LogLines = 1 }],
                    },
                ],
            });
            db.Artifacts.Add(new ArtifactRecord { Id = Guid.NewGuid(), AttemptId = planId, Name = "plan.tar.gz", Sha256 = digest, Size = content.Length });
            db.SecretResolutions.Add(new SecretResolution { Key = Token, Version = 1, AttemptId = applyId, AgentId = agent.Id, ResolvedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        await source.Services.GetRequiredService<ArtifactStore>().SaveAsync(planId, "plan.tar.gz", content, CancellationToken.None);
        await source.Services.GetRequiredService<AttemptLogStore>().AppendAsync(applyId, 0, [(0, "stderr", "fixture outcome unknown")], CancellationToken.None);

        await using var services = await RestoreServicesAsync(source);
        // Simulate a certificate issued after the backup: trust still matches, but the
        // restored registration must not magically acquire its newer serial.
        var later = ca.IssueAgentCertificate(csr, agent.Id);
        await source.WithDbAsync(async db =>
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CertificateSerial, later.Serial)));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var options = services.GetRequiredService<IOptions<OctopushyOptions>>().Value;
        await KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None);
        var restoredCa = services.GetRequiredService<AgentCertificateAuthority>();
        await restoredCa.InitializeAsync(CancellationToken.None);

        Assert.Equal(ca.Fingerprint, restoredCa.Fingerprint);
        Assert.True(restoredCa.ValidateClientCertificate(certificate));
        Assert.Equal(issued.Serial, (await db.Agents.SingleAsync()).CertificateSerial);
        Assert.NotEqual(later.Serial, (await db.Agents.SingleAsync()).CertificateSerial);
        var run = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery().SingleAsync();
        Assert.Equal(runId, run.Id);
        Assert.Equal(resourceId, run.ResourceId);
        Assert.Equal(context.Address, (await db.Resources.SingleAsync()).Address);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.True(RunContext.Parse(run.Context).SecretBindingsPinned);
        Assert.Equal(1, RunContext.Parse(run.Context).SecretVersions["token"]);
        Assert.Equal(digest, run.Steps.Single(s => s.Name == StepNames.Approval).PlanSha256);
        Assert.Equal("fixture", run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy);
        var attempt = run.Steps.Single(s => s.Name == StepNames.Apply).Attempts.Single();
        Assert.Equal(applyId, attempt.Id);
        Assert.True(attempt.Mutating);
        Assert.Equal(AttemptStatuses.Uncertain, attempt.Status);
        Assert.Equal(agent.Id, attempt.AgentId);
        Assert.Equal(1, (await db.SecretResolutions.SingleAsync()).Version);
        Assert.Equal(applyId, (await db.SecretResolutions.SingleAsync()).AttemptId);
        Assert.Equal(digest, (await db.Artifacts.SingleAsync()).Sha256);
        var restoredContent = await services.GetRequiredService<ArtifactStore>().LoadAsync(planId, "plan.tar.gz", CancellationToken.None);
        Assert.Equal(content, restoredContent);
        Assert.Equal(digest, ArtifactStore.Sha256(restoredContent));
        var lines = await services.GetRequiredService<AttemptLogStore>().ReadAsync(applyId, -1, 10, CancellationToken.None);
        Assert.Equal("fixture outcome unknown", Assert.Single(lines).Text);
        Assert.Equal(("fixture-token", 1), await scope.ServiceProvider.GetRequiredService<VariableStore>().GetAsync(Token, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restored_data_cannot_be_opened_with_a_missing_or_wrong_key(bool missing)
    {
        await using var source = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await source.SetSecretAsync(Token, "fixture-value");
        var attemptId = Guid.NewGuid();
        await source.Services.GetRequiredService<ArtifactStore>().SaveAsync(attemptId, "plan.tar.gz", "fixture-plan"u8.ToArray(), CancellationToken.None);
        await using var services = await RestoreServicesAsync(source);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var options = services.GetRequiredService<IOptions<OctopushyOptions>>().Value;
        var before = (await db.StoreVersions.AsNoTracking().SingleAsync(v => v.Key == Token)).Ciphertext;
        if (missing)
        {
            File.Delete(options.Store.KeyFile!);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None));
            Assert.Contains("Restore its original key", error.Message);
            Assert.False(File.Exists(options.Store.KeyFile));
        }
        else
        {
            await File.WriteAllBytesAsync(options.Store.KeyFile!, RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size));
            // A correctly sized wrong key passes the file check, not authenticated decryption.
            await KeyEncryptionKey.InitializeAsync(db, options, NullLogger.Instance, CancellationToken.None);
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                scope.ServiceProvider.GetRequiredService<VariableStore>().GetAsync(Token, CancellationToken.None));
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                services.GetRequiredService<ArtifactStore>().LoadAsync(attemptId, "plan.tar.gz", CancellationToken.None));
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                services.GetRequiredService<AgentCertificateAuthority>().InitializeAsync(CancellationToken.None));
        }
        Assert.Equal(before, (await db.StoreVersions.AsNoTracking().SingleAsync(v => v.Key == Token)).Ciphertext);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("missing-certificate")]
    [InlineData("mismatched-key")]
    public async Task Incomplete_ca_restore_with_enrolled_agents_is_not_silently_reinitialized(string damage)
    {
        await using var source = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await source.ConnectAgentAsync("recovery-fixture");
        await using var services = await RestoreServicesAsync(source);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        if (damage == "mismatched-key")
        {
            using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await scope.ServiceProvider.GetRequiredService<VariableStore>().SetInternalAsync(
                "system/agent-ca/key", unrelatedKey.ExportPkcs8PrivateKeyPem(), Visibilities.Secret, null, CancellationToken.None);
        }
        else
        {
            var key = damage == "missing-key" ? "system/agent-ca/key" : "system/agent-ca/certificate";
            await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync();
            await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync();
        }
        var count = await db.StoreVersions.CountAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<AgentCertificateAuthority>().InitializeAsync(CancellationToken.None));
        Assert.Contains("Restore", error.Message);
        Assert.Equal(count, await db.StoreVersions.CountAsync());
        Assert.Equal(1, await db.Agents.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_tampered_artifact_files_do_not_pass_restore_verification(bool missing)
    {
        await using var source = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var attemptId = Guid.NewGuid();
        await source.Services.GetRequiredService<ArtifactStore>().SaveAsync(attemptId, "plan.tar.gz", "fixture-plan"u8.ToArray(), CancellationToken.None);
        await using var services = await RestoreServicesAsync(source);
        var directory = services.GetRequiredService<IOptions<OctopushyOptions>>().Value.DataDirectory;
        var path = Path.Combine(directory, "artifacts", attemptId.ToString(), "plan.tar.gz");
        if (missing)
        {
            File.Delete(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                services.GetRequiredService<ArtifactStore>().LoadAsync(attemptId, "plan.tar.gz", CancellationToken.None));
        }
        else
        {
            var blob = await File.ReadAllBytesAsync(path);
            blob[^1] ^= 1;
            await File.WriteAllBytesAsync(path, blob);
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                services.GetRequiredService<ArtifactStore>().LoadAsync(attemptId, "plan.tar.gz", CancellationToken.None));
        }
    }

    [Fact]
    public async Task Native_backup_helpers_reject_foreign_databases_and_invalid_archives()
    {
        var foreign = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => postgres.DumpAsync("Host=not-a-fixture.invalid"));
        Assert.Contains("owned by this fixture", foreign.Message);
        var corrupt = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => postgres.RestoreAsync("not a PostgreSQL archive"u8.ToArray()));
        Assert.Contains("pg_restore failed", corrupt.Message);
    }

    private async Task<ServiceProvider> RestoreServicesAsync(ServerHarness source)
    {
        var connection = await source.QueryAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        var restoredConnection = await postgres.RestoreAsync(await postgres.DumpAsync(connection));
        Assert.NotEqual(connection, restoredConnection);
        var options = new OctopushyOptions
        {
            DataDirectory = Path.Combine(_directory, "restored-data"),
            Store = { KeyFile = Path.Combine(_directory, "escrow-key") },
            RunBackgroundServices = false,
        };
        File.Copy(Path.Combine(source.DataDirectory, "keys", "kek"), options.Store.KeyFile);
        // Deliberately exercise only the required artifact/log subset. This is not
        // a backup implementation for Git mirrors, web sessions, or deployment config.
        foreach (var name in new[] { "artifacts", "logs" })
        {
            var directory = Path.Combine(source.DataDirectory, name);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(options.DataDirectory, Path.GetRelativePath(source.DataDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOptions<OctopushyOptions>>(Options.Create(options))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddDbContext<OctopushyDb>(o => o.UseNpgsql(restoredConnection).UseSnakeCaseNamingConvention())
            .AddSingleton<KeyEncryptionKey>()
            .AddSingleton<Envelope>()
            .AddScoped<VariableStore>()
            .AddSingleton<ArtifactStore>()
            .AddSingleton<AttemptLogStore>()
            .AddSingleton<AgentCertificateAuthority>()
            .BuildServiceProvider(validateScopes: true);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
