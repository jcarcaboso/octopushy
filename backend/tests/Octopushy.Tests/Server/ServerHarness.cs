using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Configurations;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

/// <summary>The server's time, for a test that has to say what it is. It is real time until one does.</summary>
public sealed class TestClock : TimeProvider
{
    private int _readsLeft;
    private DateTimeOffset? _then;

    public DateTimeOffset? Now { get; set; }

    /// <summary>Moves the time on after this many reads, to land a change of time inside an operation.</summary>
    public void MoveOnAfter(int reads, DateTimeOffset then) => (_readsLeft, _then) = (reads, then);

    public override DateTimeOffset GetUtcNow()
    {
        var now = Now ?? System.GetUtcNow();
        if (_then is { } then && _readsLeft-- <= 0)
        {
            Now = then;
            _then = null;
        }

        return now;
    }
}

/// <summary>
/// The one PostgreSQL container for all server tests, started once per test run, with every migration applied once to a
/// template database. Each class takes it as a class fixture, so classes run in parallel; each test gets its own database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Template = "octopushy_template";
    private static readonly Lazy<Task<PostgreSqlContainer>> Shared = new(StartAsync);
    private readonly ConcurrentDictionary<string, bool> _databases = new();
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync() => _container = await Shared.Value;

    // The container outlives each class; it is disposed when the test run's process exits.
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        // Tests run side by side, each with its own pool, and a finished test's pool takes seconds to empty.
        var container = new PostgreSqlBuilder("postgres:18-alpine").WithCommand("-c", "max_connections=500").Build();
        await container.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        await ExecuteAsync(container.GetConnectionString(), $"CREATE DATABASE {Template}");
        // Unpooled, so no connection to the template outlives the migration.
        var template = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = Template, Pooling = false };
        await using (var db = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(template.ConnectionString).UseSnakeCaseNamingConvention().Options))
        {
            await db.Database.MigrateAsync();
        }

        // CREATE DATABASE ... TEMPLATE fails while anyone is connected to the template, so nobody may connect to it now.
        await ExecuteAsync(container.GetConnectionString(), $"ALTER DATABASE {Template} WITH ALLOW_CONNECTIONS false");
        return container;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Native custom-format dump; accepts only databases created by this fixture.</summary>
    public async Task<byte[]> DumpAsync(string connectionString)
    {
        Assert.True(_databases.ContainsKey(connectionString), "Backups require a database owned by this fixture.");
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

    /// <summary>An empty database, for a test of the migrations themselves or of a restore.</summary>
    public Task<string> NewDatabaseAsync() => CreateDatabaseAsync("");

    /// <summary>A database with every migration applied, copied from the template.</summary>
    public Task<string> NewMigratedDatabaseAsync() => CreateDatabaseAsync($" TEMPLATE {Template}");

    private async Task<string> CreateDatabaseAsync(string options)
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(_container.GetConnectionString(), $"CREATE DATABASE {name}{options}");
        // Each test owns a unique database, so a pool that stayed open for each one would
        // exhaust this shared disposable server as the suite grows. Pools empty themselves within
        // seconds instead: turning pooling off makes every query log in again, which is most of
        // what these tests would then spend their time on.
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
        _databases[connectionString] = true;
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
    private readonly TimeProvider? _clock;
    private readonly Action<IServiceCollection>? _services;
    private HttpClient? _trustClient;

    private ServerHarness(string connectionString, TestRepository repository, Dictionary<string, string>? settings, TimeProvider? clock,
        Action<IServiceCollection>? services = null)
    {
        Repository = repository;
        _connectionString = connectionString;
        _settings = settings;
        _clock = clock;
        _services = services;
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

            if (_clock is { } clock)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(clock));
            }

            if (_services is { } configure)
                builder.ConfigureTestServices(configure);
        });

    public TestRepository Repository { get; }

    public IServiceProvider Services => _factory.Services;

    public RunEngine Engine => Services.GetRequiredService<RunEngine>();

    public string DataDirectory => _dataDirectory;

    /// <param name="services">Replaces or adds services, such as an interceptor that fails a save on purpose.</param>
    /// <param name="dataDirectory">Fills the data directory before the server first starts, as an earlier release left it.</param>
    public static async Task<ServerHarness> StartAsync(PostgresFixture postgres, TestRepository? repository = null, bool withSecrets = true, Dictionary<string, string>? settings = null, TimeProvider? clock = null,
        Action<IServiceCollection>? services = null, Action<string>? dataDirectory = null)
    {
        var harness = new ServerHarness(await postgres.NewMigratedDatabaseAsync(), repository ?? TestRepository.Minimal(), settings, clock, services);
        dataDirectory?.Invoke(harness._dataDirectory);
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
        }

        return harness;
    }

    // Endpoints are bound to the API port by host, so the client addresses it.
    public HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("http://localhost:8080") });
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        return client;
    }

    public void Commit(string message)
    {
        Git("init", "--quiet", "-b", "main");
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "--allow-empty", "-m", message);
    }

    public async Task SyncAsync() => await Services.GetRequiredService<RepositorySync>().SyncOnceAsync(CancellationToken.None);

    /// <summary>Explicit fixture setup for tests that need a confirmed external workflows source. Never called by SyncAsync.</summary>
    public async Task ConfirmWorkflowSourceAsync(HttpClient? client = null)
    {
        client ??= await SignedInClientAsync();
        await SyncAsync();
        var repository = (await client.GetFromJsonAsync<Octopushy.Server.Api.RepositoryDto>("/api/repository"))!;
        var trust = repository.WorkflowsTrust!;
        var pending = Assert.IsType<Octopushy.Server.Api.WorkflowSourcePairDto>(trust.Pending);
        (await client.PostAsJsonAsync("/api/repository/workflows/trust", new Octopushy.Server.Api.ConfirmWorkflowSourceRequest(
            trust.SourceId, trust.PendingCommit!, pending.Repository, pending.CredentialKey, pending.CredentialVersion))).EnsureSuccessStatusCode();
        await SyncAsync();
    }

    /// <summary>Syncs, then trusts every installation with untrusted endpoints through the authenticated API.</summary>
    public async Task SyncAndTrustAsync(HttpClient? client = null)
    {
        await SyncAsync();
        client ??= await SignedInClientAsync();
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        foreach (var installation in inventory["labs"]!.AsArray().SelectMany(l => l!["installations"]!.AsArray()))
        {
            var trust = installation!["trust"]!;
            if (!installation["valid"]!.GetValue<bool>() || trust["untrusted"]!.AsArray().Count == 0) continue;
            (await client.PostAsJsonAsync($"/api/installations/{installation["id"]}/trust", new { endpoints = trust["endpoints"] }))
                .EnsureSuccessStatusCode();
        }
    }

    public Task TickAsync() => Engine.TickAsync(CancellationToken.None);

    /// <summary>A client signed in as the operator setup created; the same one each time. Owned by the harness.</summary>
    public async Task<HttpClient> SignedInClientAsync()
    {
        if (_trustClient is null)
        {
            _trustClient = CreateClient();
            var code = (await File.ReadAllTextAsync(Path.Combine(DataDirectory, "setup-code"))).Trim();
            (await _trustClient.PostAsJsonAsync("/api/auth/setup",
                new { setupCode = code, username = "fixture-trust", password = "synthetic trust password" })).EnsureSuccessStatusCode();
        }

        return _trustClient;
    }

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

    public async Task SetPlainAsync(string key, string value)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VariableStore>().SetAsync(key, value, Visibilities.Plain, null, CancellationToken.None);
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

    /// <summary>The context the installation's latest observation was sent, or waits to be sent, with.</summary>
    public Task<RunContext> LatestObservationAsync(string address) =>
        QueryAsync(async db =>
        {
            var resource = await db.Resources.AsNoTracking().FirstAsync(r => r.Address == address);
            var attempt = await db.Attempts.AsNoTracking()
                .Where(a => a.ResourceId == resource.Id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstAsync();
            return RunContext.Parse(attempt.Context!);
        });

    /// <summary>An agent that reaches everything it is asked about, at once: tests of anything but routing need no more.</summary>
    public Task<FakeAgent> ConnectAgentAsync(params string[] labels) =>
        ConnectAsync("fake", labels, [TofuStateStore.AgentCapability, WorkerReachability.Capability], "podman", FakeAgent.ReachEverything);

    /// <summary>An agent that reaches everything, connected from <paramref name="address"/>, as one running on a guest is.</summary>
    public Task<FakeAgent> ConnectAgentFromAsync(System.Net.IPAddress address, string name, params string[] labels) =>
        ConnectAsync(name, labels, [TofuStateStore.AgentCapability, WorkerReachability.Capability], "podman", FakeAgent.ReachEverything,
            remote: address);

    /// <summary>An agent from before built-in state and reachability reports.</summary>
    public Task<FakeAgent> ConnectOlderAgentAsync(params string[] labels) => ConnectAsync("fake", labels, [], null, null);

    /// <summary>An agent from before reachability reports, with built-in state.</summary>
    public Task<FakeAgent> ConnectLegacyAgentAsync(string name, params string[] labels) =>
        ConnectAsync(name, labels, [TofuStateStore.AgentCapability], "local", null);

    /// <summary>An agent whose probes the test answers, from <see cref="FakeAgent.TakeProbes"/>.</summary>
    public Task<FakeAgent> ConnectProbedAgentAsync(string name, string runtime = "local", params string[] labels) =>
        ConnectAsync(name, labels, [TofuStateStore.AgentCapability, WorkerReachability.Capability], runtime, null);

    /// <summary>An agent that also answers uptime probes, each as <paramref name="responder"/> says, at once.</summary>
    /// <param name="tcp">Whether it also answers <c>tcp</c> probes, as an agent announcing <c>uptime-tcp-v1</c> does.</param>
    public Task<FakeAgent> ConnectUptimeAgentAsync(string name, Func<ReachabilityProbe, ReachabilityReport> responder, bool tcp = false) =>
        ConnectAsync(name, [], [TofuStateStore.AgentCapability, WorkerReachability.Capability, Octopushy.Server.Uptime.UptimeMonitor.Capability,
            .. tcp ? new[] { Octopushy.Server.Uptime.UptimeMonitor.TcpCapability } : []], "podman", responder);

    /// <summary>
    /// An agent that advertises only <c>nix</c>, with no labels and no reachability reports, as one that builds templates' images
    /// may be.
    /// </summary>
    public Task<FakeAgent> ConnectNixAgentAsync(string name = "nix", int maxConcurrency = 4) =>
        ConnectAsync(name, [], [], "local", null, ["nix"], maxConcurrency);

    /// <summary>An agent like <see cref="ConnectAgentAsync"/> that lacks the executors <paramref name="unavailable"/> names, for its reasons.</summary>
    public Task<FakeAgent> ConnectAgentLackingAsync(string name, IReadOnlyDictionary<string, string> unavailable, params string[] labels) =>
        ConnectAsync(name, labels, [TofuStateStore.AgentCapability, WorkerReachability.Capability], "podman", FakeAgent.ReachEverything,
            new[] { "opentofu", "proxmox", "ansible" }.Except(unavailable.Keys).ToArray(), unavailable: unavailable);

    /// <summary>
    /// <paramref name="agent"/> connects again as a new process would, saying hello with <paramref name="version"/> and offering
    /// <paramref name="executors"/>, or what it offered before. Its old connection is closed; use the agent returned.
    /// </summary>
    /// <param name="running">The attempts its Hello says it still executes.</param>
    public async Task<FakeAgent> ReconnectAsync(FakeAgent agent, string version, string[]? executors = null,
        IReadOnlyDictionary<string, string>? unavailable = null, params string[] running)
    {
        var old = agent.Session;
        var clock = Services.GetRequiredService<TimeProvider>();
        await WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(u => u
            .SetProperty(a => a.Version, version).SetProperty(a => a.ConnectedAt, clock.GetUtcNow()).SetProperty(a => a.LastSeenAt, clock.GetUtcNow())));
        var session = new AgentSession(agent.Id, old.Name, executors ?? old.Executors, old.Capabilities, old.ToolRuntime,
            unavailable is null ? null : AgentAvailability.Unavailable(unavailable))
        {
            Responder = old.Responder, RemoteAddress = old.RemoteAddress, Version = version,
        };
        session.ReportRunning(running, clock.GetUtcNow());
        // As the channel does: the Hello is settled and the connection registered in one step.
        var connections = Services.GetRequiredService<AgentConnections>();
        await Engine.RegisterAsync(session, new Hello { AgentVersion = version, RunningAttemptIds = { running } },
            () => connections.Add(session), CancellationToken.None);
        return new FakeAgent(this, agent.Id, session);
    }

    private async Task<FakeAgent> ConnectAsync(string name, string[] labels, string[] capabilities, string? runtime,
        Func<ReachabilityProbe, ReachabilityReport>? responder, string[]? executors = null, int maxConcurrency = 4,
        System.Net.IPAddress? remote = null, IReadOnlyDictionary<string, string>? unavailable = null)
    {
        var agent = new AgentRecord
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Labels = labels,
            MaxConcurrency = maxConcurrency,
            EnrolledAt = DateTimeOffset.UtcNow,
            Executors = executors ?? ["opentofu", "proxmox", "ansible"],
            CertificateSerial = Guid.NewGuid().ToString("N"),
            CertificateExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        await WithDbAsync(async db =>
        {
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
        });
        var session = new AgentSession(agent.Id, agent.Name, agent.Executors, capabilities, runtime,
            unavailable is null ? null : AgentAvailability.Unavailable(unavailable)) { Responder = responder, RemoteAddress = remote };
        await Engine.OnHelloAsync(agent.Id, new Hello { AgentVersion = "test" }, CancellationToken.None);
        Services.GetRequiredService<AgentConnections>().Add(session);
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
    private readonly List<ReachabilityProbe> _probes = [];
    private readonly List<Assignment> _assignments = [];
    private readonly List<string> _cancels = [];

    public Guid Id { get; } = id;

    public AgentSession Session => session;

    private void Drain()
    {
        while (session.Outbound.TryRead(out var message))
        {
            if (message.KindCase == ServerMessage.KindOneofCase.Assignment)
                _assignments.Add(message.Assignment);
            else if (message.KindCase == ServerMessage.KindOneofCase.Probe)
                _probes.Add(message.Probe);
            else if (message.KindCase == ServerMessage.KindOneofCase.Cancel)
                _cancels.Add(message.Cancel.AttemptId);
        }
    }

    public List<Assignment> TakeAssignments()
    {
        Drain();
        var result = _assignments.ToList();
        _assignments.Clear();
        return result;
    }

    /// <summary>The attempts this agent was asked to cancel since the last call.</summary>
    public List<string> TakeCancels()
    {
        Drain();
        var result = _cancels.ToList();
        _cancels.Clear();
        return result;
    }

    /// <summary>The probes sent to this agent since the last call.</summary>
    public List<ReachabilityProbe> TakeProbes()
    {
        Drain();
        var result = _probes.ToList();
        _probes.Clear();
        return result;
    }

    /// <summary>Answers a probe as the agent would; returns whether the server took the answer.</summary>
    public Task<bool> AnswerAsync(ReachabilityProbe probe, string status, string detail = "", params byte[][] certificates)
    {
        var report = new ReachabilityReport { ProbeId = probe.ProbeId, Status = status, Detail = detail };
        report.Certificates.AddRange(certificates.Select(ByteString.CopyFrom));
        return harness.Services.GetRequiredService<WorkerReachability>().AcceptAsync(session, report, CancellationToken.None);
    }

    /// <summary>Answers every pending probe with <paramref name="status"/>.</summary>
    public async Task AnswerAllAsync(string status, params byte[][] certificates)
    {
        foreach (var probe in TakeProbes())
            await AnswerAsync(probe, status, "", certificates);
    }

    /// <summary>A self-signed certificate, for answers to probes that collect one.</summary>
    public static byte[] Certificate(string name = "pve.test")
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.RawData;
    }

    private static readonly Lazy<byte[]> Presented = new(() => Certificate());

    /// <summary>Reaches everything, presenting one certificate wherever one is collected.</summary>
    public static ReachabilityReport ReachEverything(ReachabilityProbe probe)
    {
        var report = new ReachabilityReport { ProbeId = probe.ProbeId, Status = WorkerReachability.Reachable };
        if (probe.TlsTrust == "collect")
            report.Certificates.Add(ByteString.CopyFrom(Presented.Value));
        return report;
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

    /// <summary>
    /// Reports an assignment's result. An OpenTofu apply that succeeded or failed hands back the
    /// state it was given unless the test passes one, as an agent always returns the state; use
    /// the overload that takes an attempt ID to report an apply without it.
    /// </summary>
    public Task<bool> ReportAsync(Assignment assignment, Outcome outcome, JsonObject? outputs = null, string? error = null, params Artifact[] artifacts) =>
        ReportAsync(assignment, outcome, outputs, error, checkShape: true, artifacts);

    /// <summary>
    /// Reports outputs in a shape no agent of the contract sends, which <see cref="ReportAsync(Assignment, Outcome, JsonObject?, string?, Artifact[])"/>
    /// refuses: for a test of what the server does with an older agent's or a hostile tool's outputs.
    /// </summary>
    public Task<bool> ReportOffContractAsync(Assignment assignment, Outcome outcome, JsonObject outputs, string? error = null, params Artifact[] artifacts) =>
        ReportAsync(assignment, outcome, outputs, error, checkShape: false, artifacts);

    private async Task<bool> ReportAsync(Assignment assignment, Outcome outcome, JsonObject? outputs, string? error, bool checkShape, Artifact[] artifacts)
    {
        if (assignment is { Executor: "opentofu", Action: "apply" } && outcome is Outcome.Succeeded or Outcome.Failed
            && artifacts.All(a => a.Name != TofuStateStore.ArtifactName)
            && assignment.Artifacts.FirstOrDefault(a => a.Name == TofuStateStore.ArtifactName) is { } state)
            artifacts = [.. artifacts, state];
        if (outputs is not null && checkShape)
            CheckShape(assignment, outputs);
        return await ReportAsync(assignment.AttemptId, outcome,
            await AsTheModuleWouldAsync(assignment, outcome, outputs ?? ExampleOutputs(assignment, outcome), outputs), error, artifacts);
    }

    /// <summary>
    /// Keys whose names are the user's and not the contract's: an apply's module outputs, what a prepare playbook resolved,
    /// and the manifest the controller sent.
    /// </summary>
    private static readonly string[] UserKeys = ["outputs", "prepared", "manifest"];

    /// <summary>
    /// Fields executors.md names that no example holds yet; see contracts/agent/v1/examples/README.md. A test may report
    /// them until an example does.
    /// </summary>
    private static readonly Dictionary<(string, string), string[]> NotYetInAnExample = new()
    {
        // An attribute's after is a number, a string or a list of tags, as the attribute is: its kind is not the contract's.
        [("opentofu", "preview")] = ["changes/*/actionReason", "changes/*/targetUnknown", "changes/*/attributes/*/before",
            "changes/*/attributes/*/after", "changes/*/attributes/*/unknown", "changes/*/attributes/*/sensitive"],
        [("proxmox", "verify-guest")] = ["hostKeys", "hostKeysTask", "hostKeysError"],
        [("ansible", "run")] = ["refusedKeys"],
    };

    /// <summary>
    /// The agent's outputs for a success the test gave none for: its example in contracts/agent/v1/examples, which the
    /// agent's tests produce from its own code. An observation stays empty: the example's lists VMs, among them the
    /// example guest's VMID, which a test of anything else would see as taken.
    /// </summary>
    private static JsonObject? ExampleOutputs(Assignment assignment, Outcome outcome) =>
        outcome == Outcome.Succeeded && assignment.Action != "observe" && ContractExamples.Exists(assignment.Executor, assignment.Action)
            ? ContractExamples.Output(assignment.Executor, assignment.Action)
            : null;

    /// <summary>
    /// Refuses outputs a test made up in a shape no agent reports: a key not in the action's examples, or a value of another
    /// kind. A test may leave keys out and change values. See <see cref="ContractExamples.ShapeProblems"/>.
    /// </summary>
    private static void CheckShape(Assignment assignment, JsonObject outputs)
    {
        var (executor, action) = (assignment.Executor, assignment.Action);
        if (!ContractExamples.Exists(executor, action))
            return;
        var example = ContractExamples.OutputShape(executor, action);
        var free = UserKeys.Concat(NotYetInAnExample.GetValueOrDefault((executor, action), [])).ToList();
        var reported = outputs.DeepClone().AsObject();
        reported.Remove(NoMachine);
        var problems = ContractExamples.ShapeProblems(example, reported, free);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"These {executor}/{action} outputs have a shape its examples in contracts/agent/v1/examples do not: {string.Join("; ", problems)}");
    }

    /// <summary>Plan outputs that make the guest's own machine nowhere, for a test that needs a provision plan that does not.</summary>
    public const string NoMachine = "fakeNoMachine";

    /// <summary>
    /// What the example provision module reports, filled in where a test leaves it out. A provision plan of a guest not
    /// bound to its VM yet that creates something and names no Proxmox resource makes the guest's own VM with its first
    /// create, as the module does once, and a retire plan's first delete is of that VM, unless the outputs say <see cref="NoMachine"/>. A verified VM carries the module's tag and
    /// description unless the test says what it carries. Explicit outputs are left as they are.
    /// </summary>
    /// <summary>A create, as <see cref="Plan"/> lists one: its actions name create and not delete.</summary>
    private static bool IsCreate(JsonNode? change) =>
        change?["actions"] is JsonArray a && a.Any(x => x?.ToString() == "create") && a.All(x => x?.ToString() != "delete");

    /// <summary>A delete, as <see cref="Plan"/> lists one, which a destroy plan of the module makes of the guest's VM.</summary>
    private static bool IsDelete(JsonNode? change) => change?["actions"] is JsonArray a && a.Any(x => x?.ToString() == "delete");

    private async Task<JsonObject?> AsTheModuleWouldAsync(Assignment assignment, Outcome outcome, JsonObject? outputs, JsonObject? given)
    {
        if (outcome != Outcome.Succeeded || !Guid.TryParse(assignment.AttemptId, out var attemptId))
            return outputs;
        if (outputs?.Remove(NoMachine) == true)
            return outputs;
        var preview = assignment is { Executor: "opentofu", Action: "preview" } && outputs?["changes"] is JsonArray listed
            && listed.All(c => c?["type"]?.ToString()?.StartsWith("proxmox_", StringComparison.Ordinal) != true)
            && listed.Any(c => IsCreate(c) || IsDelete(c));
        var verify = assignment.Action == "verify-guest" && given?.ContainsKey("description") != true;
        if (!preview && !verify)
            return outputs;
        var run = await harness.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == attemptId)
            .Select(a => new
            {
                a.Step!.Stage, a.Step.Run!.Operation, a.Step.Run.Context,
                Bound = db.Resources.Any(r => r.Id == a.Step.Run.ResourceId && r.BoundVmid != null),
            }).FirstOrDefaultAsync());
        if (run is null || run.Stage is not null)
            return outputs;
        var context = RunContext.Parse(run.Context);
        var retire = run.Operation == Operations.Retire;
        if (preview && (run.Operation == Operations.Provision && !run.Bound && outputs!["changes"]!.AsArray().Any(IsCreate)
                        || retire && outputs!["changes"]!.AsArray().Any(IsDelete)))
        {
            outputs = outputs!.DeepClone().AsObject();
            var first = outputs["changes"]!.AsArray().OfType<JsonObject>().First(c => retire ? IsDelete(c) : IsCreate(c));
            first["actions"] = new JsonArray(retire ? "delete" : "create");
            first["address"] = $"{GuestVmPlan.VmResourceType}.guest";
            first["type"] = GuestVmPlan.VmResourceType;
            first["name"] = "guest";
            first["vmId"] = context.Proxmox.Vmid;
            first["node"] = context.Proxmox.Node;
        }
        else if (verify)
        {
            outputs = outputs?.DeepClone().AsObject() ?? [];
            if (given?["node"] is null)
                outputs["node"] = context.Proxmox.Node;
            outputs["tags"] = new JsonArray(SafetyBoundary.OwnerTag, context.Address.Split('/')[0]);
            outputs["description"] = SafetyBoundary.OwnerDescription(context.Address);
        }

        return outputs;
    }

    public async Task<bool> ReportAsync(string attemptId, Outcome outcome, JsonObject? outputs = null, string? error = null, params Artifact[] artifacts)
    {
        await harness.Engine.OnStartedAsync(Id, Guid.Parse(attemptId), CancellationToken.None);
        var result = new AttemptResult { AttemptId = attemptId, Outcome = outcome, OutputsJson = outputs?.ToJsonString() ?? "", Error = error ?? "" };
        result.Artifacts.AddRange(artifacts);
        return await harness.Engine.OnResultAsync(Id, result, CancellationToken.None);
    }

    public static JsonObject Plan(int create = 0, int update = 0, int delete = 0, int replace = 0, bool? hasChanges = null, bool machine = true)
    {
        var plan = PlanOf(create, update, delete, replace, hasChanges);
        if (!machine)
            plan[NoMachine] = true;
        return plan;
    }

    private static JsonObject PlanOf(int create, int update, int delete, int replace, bool? hasChanges) => new()
    {
        ["hasChanges"] = hasChanges ?? create + update + delete + replace > 0,
        ["changes"] = new JsonArray(Enumerable.Range(0, create + update + delete + replace)
            .Select(i => (JsonNode)new JsonObject
            {
                ["address"] = $"terraform_data.r{i}",
                ["type"] = "terraform_data",
                ["name"] = $"r{i}",
                ["importing"] = false,
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
