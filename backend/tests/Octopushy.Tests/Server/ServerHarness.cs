using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Configurations;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Testcontainers.PostgreSql;

namespace Octopushy.Tests.Server;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>One PostgreSQL container for all server tests; each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly HashSet<string> _databases = [];

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Native custom-format dump; accepts only databases created by this fixture.</summary>
    public async Task<byte[]> DumpAsync(string connectionString)
    {
        Assert.True(_databases.Contains(connectionString), "Backups require a database owned by this fixture.");
        var source = new NpgsqlConnectionStringBuilder(connectionString);
        var path = $"/tmp/recovery-{Guid.NewGuid():N}.dump";
        try
        {
            await ExecAsync("sh", "-c", "umask 077; exec \"$@\"", "--",
                "pg_dump", "-U", source.Username!, "--format=custom", "--no-owner", "--no-privileges",
                "--file", path, source.Database!);
            return await _container.ReadFileAsync(path);
        }
        finally
        {
            await ExecAsync("rm", "-f", path);
        }
    }

    /// <summary>Restores into a newly allocated fixture database, never an existing target.</summary>
    public async Task<string> RestoreAsync(byte[] dump)
    {
        var connectionString = await NewDatabaseAsync();
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var path = $"/tmp/recovery-{Guid.NewGuid():N}.dump";
        try
        {
            await _container.CopyAsync(dump, path, fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite);
            await ExecAsync("pg_restore", "-U", target.Username!, "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname", target.Database!, path);
            return connectionString;
        }
        finally
        {
            await ExecAsync("rm", "-f", path);
        }
    }

    private async Task ExecAsync(params string[] command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await _container.ExecAsync(command, timeout.Token);
        Assert.True(result.ExitCode == 0, $"{command[0]} failed: {result.Stderr}");
    }

    public async Task<string> NewDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        // Each test owns a unique database; retaining an idle pool per database
        // exhausts this shared disposable server as the suite grows.
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
        _databases.Add(connectionString);
        return connectionString;
    }
}

/// <summary>
/// The server with its background loops turned off, a lab repository in a local Git repository,
/// and a fake agent, so a test decides when things happen.
/// </summary>
public sealed class ServerHarness : IAsyncDisposable
{
    private WebApplicationFactory<Program> _factory;
    private readonly string _connectionString;
    private readonly string _dataDirectory;
    private readonly Dictionary<string, string>? _settings;
    private HttpClient? _trustClient;

    private ServerHarness(string connectionString, TestRepository repository, Dictionary<string, string>? settings)
    {
        Repository = repository;
        _connectionString = connectionString;
        _settings = settings;
        _dataDirectory = Path.Combine(Path.GetTempPath(), "octopushy-server-" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Octopushy", _connectionString);
            builder.UseSetting("Octopushy:DataDirectory", _dataDirectory);
            builder.UseSetting("Octopushy:MigrateOnStart", "true");
            builder.UseSetting("Octopushy:RunBackgroundServices", "false");
            builder.UseSetting("Octopushy:AgentChannel:PublicUrl", "https://octopushy.test:8443");
            foreach (var (key, value) in _settings ?? [])
            {
                builder.UseSetting(key, value);
            }
        });

    public TestRepository Repository { get; }

    public IServiceProvider Services => _factory.Services;

    public RunEngine Engine => Services.GetRequiredService<RunEngine>();

    public string DataDirectory => _dataDirectory;

    public static async Task<ServerHarness> StartAsync(PostgresFixture postgres, TestRepository? repository = null, bool withSecrets = true, Dictionary<string, string>? settings = null)
    {
        var harness = new ServerHarness(await postgres.NewDatabaseAsync(), repository ?? TestRepository.Minimal(), settings);
        _ = harness._factory.Server;
        harness.Commit("Initial definitions");
        await harness.WithDbAsync(async db =>
        {
            db.RepositorySettings.Add(new RepositorySettings { Url = harness.Repository.Root, Branch = "main", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        if (withSecrets)
        {
            await harness.SetSecretAsync("proxmox/korriban/read-only-token", "octopushy@pve!agent=secret-token");
            await harness.SetSecretAsync("proxmox/korriban/privileged-token", "octopushy@pve!writer=writer-token");
            await harness.SetSecretAsync("opentofu/pg", "postgres://state");
        }

        return harness;
    }

    // Endpoints are bound to the API port by host, so the client addresses it.
    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("http://localhost:8080") });

    public void Commit(string message)
    {
        Git("init", "--quiet", "-b", "main");
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "--allow-empty", "-m", message);
    }

    public async Task SyncAsync() => await Services.GetRequiredService<RepositorySync>().SyncOnceAsync(CancellationToken.None);

    /// <summary>Execution fixtures explicitly review and grant authority through the authenticated API.</summary>
    public async Task SyncAndTrustAsync(HttpClient? client = null)
    {
        await SyncAsync();
        if (client is null)
        {
            if (_trustClient is null)
            {
                _trustClient = CreateClient();
                var code = (await File.ReadAllTextAsync(Path.Combine(DataDirectory, "setup-code"))).Trim();
                (await _trustClient.PostAsJsonAsync("/api/auth/setup",
                    new { setupCode = code, username = "fixture-trust", password = "synthetic trust password" })).EnsureSuccessStatusCode();
            }
            client = _trustClient;
        }
        var repository = (await client.GetFromJsonAsync<JsonObject>("/api/repository"))!;
        var revision = repository["revision"]!;
        if (revision["trustedAt"] is not null) return;
        var path = $"/api/repository/revisions/{revision["id"]}";
        var review = (await client.GetFromJsonAsync<JsonObject>(path + "/review"))!;
        (await client.PostAsJsonAsync(path + "/trust", new
        {
            commit = revision["commit"]!.GetValue<string>(),
            baseCommit = review["baseCommit"]?.GetValue<string>(),
        })).EnsureSuccessStatusCode();
    }

    public Task TickAsync() => Engine.TickAsync(CancellationToken.None);

    /// <summary>Restarts only this disposable in-process test server, retaining its fixture DB/files.</summary>
    public async Task RestartFixtureAsync()
    {
        _trustClient?.Dispose();
        _trustClient = null;
        await _factory.DisposeAsync();
        _factory = CreateFactory();
        _ = _factory.Server;
    }

    public async Task SetSecretAsync(string key, string value)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VariableStore>().SetAsync(key, value, Visibilities.Secret, null, CancellationToken.None);
    }

    public async Task WithDbAsync(Func<OctopushyDb, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
    }

    public async Task<T> QueryAsync<T>(Func<OctopushyDb, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
    }

    public Task<Resource> ResourceAsync(string address) =>
        QueryAsync(db => db.Resources.AsNoTracking().Where(r => r.Address == address).OrderBy(r => r.RetiredAt.HasValue).FirstAsync());

    public Task<Run> LatestRunAsync(string address, string? operation = null) =>
        QueryAsync(async db =>
        {
            var resource = await db.Resources.AsNoTracking().Where(r => r.Address == address).OrderBy(r => r.RetiredAt.HasValue).FirstAsync();
            return await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
                .Where(r => r.ResourceId == resource.Id && (operation == null || r.Operation == operation))
                .OrderByDescending(r => r.CreatedAt)
                .FirstAsync();
        });

    public async Task<FakeAgent> ConnectAgentAsync(params string[] labels)
    {
        var agent = new AgentRecord
        {
            Id = Guid.CreateVersion7(),
            Name = "fake",
            Labels = labels,
            MaxConcurrency = 4,
            EnrolledAt = DateTimeOffset.UtcNow,
            Executors = ["opentofu", "proxmox", "nixos-rootfs", "guest", "managed-worker"],
            CertificateSerial = Guid.NewGuid().ToString("N"),
            CertificateExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        await WithDbAsync(async db =>
        {
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
        });
        var session = new AgentSession(agent.Id, agent.Name, agent.Executors);
        Services.GetRequiredService<AgentConnections>().Add(session);
        await Engine.OnHelloAsync(agent.Id, new Hello { AgentVersion = "test" }, CancellationToken.None);
        return new FakeAgent(this, agent.Id, session);
    }

    public async ValueTask DisposeAsync()
    {
        _trustClient?.Dispose();
        await _factory.DisposeAsync();
        Repository.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Repository.Root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}

/// <summary>An agent without a process: the test reads its assignments and reports results.</summary>
public sealed class FakeAgent(ServerHarness harness, Guid id, AgentSession session)
{
    public Guid Id { get; } = id;

    public List<Assignment> TakeAssignments()
    {
        var result = new List<Assignment>();
        while (session.Outbound.TryRead(out var message))
        {
            if (message.KindCase == ServerMessage.KindOneofCase.Assignment)
            {
                result.Add(message.Assignment);
            }
        }

        return result;
    }

    /// <summary>Ticks the engine until this agent receives an assignment for the action.</summary>
    public async Task<Assignment> NextAsync(string action)
    {
        for (var i = 0; i < 5; i++)
        {
            await harness.TickAsync();
            var assignment = TakeAssignments().FirstOrDefault(a => a.Action == action);
            if (assignment is not null)
            {
                return assignment;
            }
        }

        throw new InvalidOperationException($"No {action} assignment arrived.");
    }

    public Task<bool> ReportAsync(Assignment assignment, Outcome outcome, JsonObject? outputs = null, string? error = null, params Artifact[] artifacts) =>
        ReportAsync(assignment.AttemptId, outcome, outputs, error, artifacts);

    public async Task<bool> ReportAsync(string attemptId, Outcome outcome, JsonObject? outputs = null, string? error = null, params Artifact[] artifacts)
    {
        await harness.Engine.OnStartedAsync(Id, Guid.Parse(attemptId), CancellationToken.None);
        var result = new AttemptResult { AttemptId = attemptId, Outcome = outcome, OutputsJson = outputs?.ToJsonString() ?? "", Error = error ?? "" };
        result.Artifacts.AddRange(artifacts);
        return await harness.Engine.OnResultAsync(Id, result, CancellationToken.None);
    }

    public static JsonObject Plan(int create = 0, int update = 0, int delete = 0, int replace = 0, bool? hasChanges = null) => new()
    {
        ["hasChanges"] = hasChanges ?? create + update + delete + replace > 0,
        ["changes"] = new JsonArray(Enumerable.Range(0, create + update + delete + replace)
            .Select(i => (JsonNode)new JsonObject
            {
                ["address"] = $"terraform_data.r{i}",
                ["actions"] = new JsonArray(i < create ? "create" : i < create + update ? "update" : i < create + update + delete ? "delete" : "delete", "create"),
            })
            .ToArray()),
        ["counts"] = new JsonObject { ["create"] = create, ["update"] = update, ["delete"] = delete, ["replace"] = replace },
        ["toolVersion"] = "1.13.1",
    };

    public static Artifact PlanArtifact(string content = "a saved plan")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new Artifact { Name = AssignmentBuilder.PlanArtifact, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }
}
