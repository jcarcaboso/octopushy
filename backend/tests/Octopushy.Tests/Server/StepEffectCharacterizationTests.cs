using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// What a finished step's effects write, pinned through the real engine: the run's and every step's status and text, the
/// guest's configuration and access keys, the rows each table gained, and what a second delivery of the same result does.
/// Recorded from the code before the effects moved out of <c>RunEngine.ApplyEffectsAsync</c>, with every case taken from a
/// test that already covers it; a change to a pinned line is a change of behaviour.
/// </summary>
internal static class EffectPins
{
    /// <summary>The run, its steps, and what its effects write about the guest, as the next step and a person would see them.</summary>
    public static Task<string> PinAsync(ServerHarness server, Guid runId) =>
        server.QueryAsync(async db =>
        {
            var run = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).SingleAsync(r => r.Id == runId);
            var lines = new List<string> { $"run: {run.Status} | finished {run.FinishedAt is not null} | {Mask(run.Detail)}" };
            foreach (var step in run.Steps.OrderBy(s => s.Ordinal))
            {
                lines.Add($"step {step.Name}: {step.Status} | finished {step.FinishedAt is not null} | outputs {step.Outputs is not null}"
                    + $" | attempts {string.Join(",", step.Attempts.OrderBy(a => a.Number).Select(a => a.Status))} | {Mask(step.Detail)}");
            }

            var hashes = await db.Runs.AsNoTracking().Where(r => r.ResourceId == run.ResourceId)
                .Select(r => new { r.Id, r.InputsHash, r.RevisionId }).ToListAsync();
            string Hash(string? hash) => hash is null ? "-" : hash == run.InputsHash ? "this run's" : "another";
            string Revision(Guid? revision) => revision is null ? "-" : revision == run.RevisionId ? "this run's" : "another";
            var configuration = await db.GuestConfigurations.AsNoTracking().SingleOrDefaultAsync(c => c.ResourceId == run.ResourceId);
            lines.Add(configuration is null
                ? "configuration: none"
                : $"configuration: applied {Hash(configuration.AppliedHash)} | revision {Revision(configuration.AppliedRevisionId)}"
                  + $" | at {configuration.AppliedAt is not null} | action {configuration.AppliedAction ?? "-"} | snapshot {configuration.Snapshot ?? "-"}"
                  + $" | proposed {Hash(configuration.ProposedHash)} | desired {Hash(configuration.DesiredHash)}");
            foreach (var key in await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).OrderBy(k => k.Version).ToListAsync())
            {
                lines.Add($"access key v{key.Version}: last used {(key.LastUsedRunId is null ? "-" : key.LastUsedRunId == run.Id ? "by this run" : "by another")}"
                    + $" at {key.LastUsedAt is not null} | provisioned {key.ProvisionedAt is not null} | authorized {key.AuthorizedAt is not null}"
                    + $" | verified {key.VerifiedAt is not null} | removed {key.RemovedAt is not null}");
            }

            var resource = await db.Resources.AsNoTracking().SingleAsync(r => r.Id == run.ResourceId);
            lines.Add($"resource: checked {resource.CheckedAt is not null} | applied {resource.AppliedAt is not null} | verified {resource.VerifiedAt is not null}"
                + $" | bound {resource.BoundVmid?.ToString() ?? "-"} | outputs {resource.Outputs is not null}");
            return string.Join("\n", lines);
        });

    /// <summary>How many rows each table holds, to tell what a result added.</summary>
    public static Task<Dictionary<string, int>> RowsAsync(ServerHarness server) =>
        server.QueryAsync(async db =>
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var table in db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null)
                         .Select(t => (Schema: t.GetSchema(), Name: t.GetTableName()!)).Distinct().OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var name = table.Schema is null ? $"\"{table.Name}\"" : $"\"{table.Schema}\".\"{table.Name}\"";
                // Names from the model, not from input.
                var sql = "SELECT COUNT(*)::int AS \"Value\" FROM " + name;
                counts[table.Name] = await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
            }

            return counts;
        });

    /// <summary>The tables whose row count changed since <paramref name="before"/>.</summary>
    public static async Task<string> AddedAsync(ServerHarness server, Dictionary<string, int> before)
    {
        var after = await RowsAsync(server);
        var changed = after.Where(t => t.Value != before.GetValueOrDefault(t.Key)).Select(t => $"{t.Key} {t.Value - before.GetValueOrDefault(t.Key):+0;-0}").ToList();
        return "rows: " + (changed.Count == 0 ? "none" : string.Join(", ", changed));
    }

    /// <summary>Reports <paramref name="assignment"/> and advances, and pins what that did.</summary>
    public static async Task<string> DeliverAsync(ServerHarness server, FakeAgent agent, Assignment assignment, Outcome outcome, JsonObject? outputs, string? error = null,
        params Artifact[] artifacts)
    {
        var before = await RowsAsync(server);
        var taken = await agent.ReportAsync(assignment, outcome, outputs?.DeepClone().AsObject(), error, artifacts);
        await server.TickAsync();
        var runId = await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        return $"taken: {taken}\n" + await PinAsync(server, runId!.Value) + "\n" + await AddedAsync(server, before);
    }

    /// <summary>
    /// What a rotation records beside <see cref="PinAsync"/>: which run verified each access key and whether it was promoted, and
    /// every version of the guest's key in the store, which is current and which are revoked, by whom and why.
    /// </summary>
    public static Task<string> AccessAsync(ServerHarness server, Guid runId) =>
        server.QueryAsync(async db =>
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
            var lines = new List<string>();
            foreach (var key in await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).OrderBy(k => k.Version).ToListAsync())
            {
                lines.Add($"access key v{key.Version}: verified by {(key.VerifiedRunId is null ? "-" : key.VerifiedRunId == run.Id ? "this run" : "another")}"
                    + $" | promoted {key.PromotedAt is not null} | bootstrap {key.Bootstrap}");
            }

            var name = Octopushy.Server.Store.GuestAccessCredentials.Key(run.ResourceId);
            var entry = await db.StoreEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Key == name);
            lines.Add(entry is null ? "store: none" : $"store: current v{entry.CurrentVersion} | latest v{entry.LatestVersion}");
            foreach (var version in await db.StoreVersions.AsNoTracking().Where(v => v.Key == name).OrderBy(v => v.Version).ToListAsync())
            {
                lines.Add($"store v{version.Version}: activated {version.ActivatedAt is not null} | revoked {version.RevokedAt is not null}"
                    + $" by {version.RevokedBy ?? "-"} | {version.RevocationNote ?? "-"}");
            }

            return string.Join("\n", lines);
        });

    /// <summary>Every time a rotation's effects set, exactly, to tell a second delivery that wrote them again.</summary>
    public static Task<string> AccessTimesAsync(ServerHarness server, Guid resourceId) =>
        server.QueryAsync(async db =>
        {
            var lines = new List<string>();
            foreach (var key in await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == resourceId).OrderBy(k => k.Version).ToListAsync())
                lines.Add($"v{key.Version}: authorized {Time(key.AuthorizedAt)} | verified {Time(key.VerifiedAt)} | removed {Time(key.RemovedAt)}");
            var name = Octopushy.Server.Store.GuestAccessCredentials.Key(resourceId);
            foreach (var version in await db.StoreVersions.AsNoTracking().Where(v => v.Key == name).OrderBy(v => v.Version).ToListAsync())
                lines.Add($"store v{version.Version}: revoked {Time(version.RevokedAt)}");
            return string.Join("\n", lines);
            static string Time(DateTimeOffset? time) => time?.ToString("O") ?? "-";
        });

    /// <summary><see cref="DeliverAsync"/>, followed by <see cref="AccessAsync"/>.</summary>
    public static async Task<string> DeliverWithAccessAsync(ServerHarness server, FakeAgent agent, Assignment assignment, Outcome outcome, JsonObject? outputs,
        string? error = null)
    {
        var pinned = await DeliverAsync(server, agent, assignment, outcome, outputs, error);
        var runId = await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        return pinned + "\n" + await AccessAsync(server, runId!.Value);
    }

    /// <summary>
    /// What a guest's machine steps record beside <see cref="PinAsync"/>: the run's operation, the hashes and revisions it marked,
    /// the binding, the reservation, retirement and attachment, and the outputs it kept.
    /// </summary>
    public static Task<string> GuestAsync(ServerHarness server, Guid runId) =>
        server.QueryAsync(async db =>
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
            var resource = await db.Resources.AsNoTracking().SingleAsync(r => r.Id == run.ResourceId);
            string Hash(string? hash) => hash is null ? "-" : hash == run.InputsHash ? "this run's" : "another";
            string Revision(Guid? revision) => revision is null ? "-" : revision == run.RevisionId ? "this run's" : "another";
            return string.Join("\n",
                $"operation: {run.Operation}",
                $"guest: checked {Hash(resource.CheckedInputsHash)} {Revision(resource.CheckedRevisionId)} | applied {Hash(resource.AppliedInputsHash)}"
                + $" {Revision(resource.AppliedRevisionId)} | synced {Hash(resource.SyncedInputsHash)} | outputs {resource.Outputs ?? "-"}",
                $"guest: bound {resource.BoundInstallation ?? "-"} {resource.BoundVmid?.ToString() ?? "-"} at {resource.BoundAt is not null}"
                + $" | reserved {resource.ReservedInstallation ?? "-"} {resource.ReservedNode ?? "-"} {resource.ReservedVmid?.ToString() ?? "-"}"
                + $" at {resource.ReservedAt is not null} applied {resource.ReservationAppliedAt is not null}",
                $"guest: retired {resource.RetiredAt is not null} | attached {resource.AttachedAt is not null} by {resource.AttachedBy ?? "-"}"
                + $" | detached {resource.DetachedAt is not null} | waiting {resource.ProvisionWaiting ?? "-"}");
        });

    /// <summary>Every time a guest's machine steps set, exactly, to tell a second delivery that wrote them again.</summary>
    public static Task<string> GuestTimesAsync(ServerHarness server, Guid resourceId) =>
        server.QueryAsync(async db =>
        {
            var resource = await db.Resources.AsNoTracking().SingleAsync(r => r.Id == resourceId);
            static string Time(DateTimeOffset? time) => time?.ToString("O") ?? "-";
            return $"checked {Time(resource.CheckedAt)} | applied {Time(resource.AppliedAt)} | verified {Time(resource.VerifiedAt)}"
                   + $" | bound {Time(resource.BoundAt)} | reservation applied {Time(resource.ReservationAppliedAt)} | retired {Time(resource.RetiredAt)}"
                   + $" | attached {Time(resource.AttachedAt)}";
        });

    /// <summary><see cref="DeliverAsync"/>, followed by <see cref="GuestAsync"/>.</summary>
    public static async Task<string> DeliverWithGuestAsync(ServerHarness server, FakeAgent agent, Assignment assignment, Outcome outcome, JsonObject? outputs,
        string? error = null, params Artifact[] artifacts)
    {
        var pinned = await DeliverAsync(server, agent, assignment, outcome, outputs, error, artifacts);
        var runId = await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        return pinned + "\n" + await GuestAsync(server, runId!.Value);
    }

    /// <summary>
    /// What a guest's plan records beside <see cref="GuestAsync"/>: the plan kept on its step, whole, and the approval after it,
    /// with who decided and whether the hash it approves is the kept plan's digest.
    /// </summary>
    public static Task<string> PlanAsync(ServerHarness server, Guid runId) =>
        server.QueryAsync(async db =>
        {
            var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == runId && s.Stage == null).ToListAsync();
            var plan = steps.Single(s => s.Name == StepNames.Plan);
            var digest = plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)?["planSha256"]?.ToString();
            var approval = steps.Single(s => s.Name == StepNames.Approval);
            var approved = approval.PlanSha256 is null ? "-" : approval.PlanSha256 == digest ? "the plan's digest" : "another";
            return $"plan: {plan.Outputs ?? "-"}\n"
                   + $"approval: required {approval.ApprovalRequired} | decision {approval.Decision ?? "-"} by {approval.DecidedBy ?? "-"} at {approval.DecidedAt is not null}"
                   + $" | hash {approved}";
        });

    /// <summary><see cref="DeliverWithGuestAsync"/>, followed by <see cref="PlanAsync"/>.</summary>
    public static async Task<string> DeliverPlanAsync(ServerHarness server, FakeAgent agent, Assignment assignment, Outcome outcome, JsonObject? outputs,
        string? error = null, params Artifact[] artifacts)
    {
        var pinned = await DeliverWithGuestAsync(server, agent, assignment, outcome, outputs, error, artifacts);
        var runId = await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        return pinned + "\n" + await PlanAsync(server, runId!.Value);
    }

    /// <summary>
    /// What retiring a guest leaves beside <see cref="GuestAsync"/>: the store entries and versions of its three credentials, its
    /// access and host key rows, and every service, its own and others', with whether it is retired and its runs' statuses.
    /// </summary>
    public static Task<string> RetiredAsync(ServerHarness server, Guid runId) =>
        server.QueryAsync(async db =>
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
            var id = run.ResourceId;
            var lines = new List<string>();
            foreach (var (name, key) in new[]
                     {
                         ("access", Octopushy.Server.Store.GuestAccessCredentials.Key(id)),
                         ("host key", Octopushy.Server.Store.GuestHostCredentials.Key(id)),
                         ("container host keys", Octopushy.Server.Store.ContainerHostKeys.Key(id)),
                     })
            {
                lines.Add($"store {name}: entry {await db.StoreEntries.AsNoTracking().AnyAsync(e => e.Key == key)}"
                          + $" | versions {await db.StoreVersions.AsNoTracking().CountAsync(v => v.Key == key)}");
            }

            lines.Add($"keys: access {await db.GuestAccessKeys.AsNoTracking().CountAsync(k => k.ResourceId == id)}"
                      + $" | host {await db.GuestHostKeys.AsNoTracking().CountAsync(k => k.ResourceId == id)}");
            foreach (var service in await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Service).OrderBy(r => r.Address).ToListAsync())
            {
                var runs = await db.Runs.AsNoTracking().Where(r => r.ResourceId == service.Id).OrderBy(r => r.CreatedAt).ToListAsync();
                lines.Add($"service {service.Address}: retired {service.RetiredAt is not null} | runs "
                          + string.Join(", ", runs.Select(r => $"{r.Status} ({Mask(r.Detail)})")));
            }

            return string.Join("\n", lines);
        });

    /// <summary><see cref="DeliverWithGuestAsync"/>, followed by <see cref="RetiredAsync"/>.</summary>
    public static async Task<string> DeliverRetiredAsync(ServerHarness server, FakeAgent agent, Assignment assignment, Outcome outcome, JsonObject? outputs,
        string? error = null)
    {
        var pinned = await DeliverWithGuestAsync(server, agent, assignment, outcome, outputs, error);
        var runId = await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        return pinned + "\n" + await RetiredAsync(server, runId!.Value);
    }

    /// <summary>A change id is new in every test.</summary>
    private static string Mask(string? text) =>
        text is null ? "-" : System.Text.RegularExpressions.Regex.Replace(text, "change [0-9a-f-]{36}", "change <id>");

    public static void Check(string expected, string actual) => Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
}

/// <summary>
/// A configure run's playbook, <see cref="StepNames.Playbook"/> and <see cref="StepNames.PlaybookApproved"/>: what it promotes,
/// what a declared outcome makes of the run, the key use it records, and a step name the code does not know.
/// </summary>
public class ConfigureEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    private static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Http)> ProvisionedAsync(PostgresFixture postgres, TestRepository repository)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        return (server, agent, http);
    }

    /// <summary>Schedules the configure run, previews it, and returns its playbook's assignment.</summary>
    private static async Task<Assignment> PlaybookAsync(ServerHarness server, FakeAgent agent, JsonObject? prepared = null)
    {
        await ScheduleAsync(server);
        await ConfigureRunTests.PreviewAsync(agent, prepared);
        return await agent.NextAsync("run");
    }

    private static readonly JsonObject Idle = new()
    {
        ["exitCode"] = 0,
        ["tasks"] = 0,
        ["recap"] = new JsonObject { ["ok"] = 0, ["changed"] = 0 },
    };

    [Fact]
    public async Task A_playbook_that_succeeds_promotes_what_was_prepared_and_its_second_delivery_changes_nothing()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository());
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "1.2.3" });
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1.2.3"} | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1.2.3"} | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        // The pins above keep times as set or not set. Here the clock moves between the deliveries, so an effect that ran
        // again would move them.
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        var repository = Repository.Repository();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        var server = await ServerHarness.StartAsync(postgres, repository, clock: clock);
        await using var _ = server;
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent);
        var guest = (await server.ResourceAsync(Guest)).Id;
        Task<(DateTimeOffset? Applied, DateTimeOffset? Used)> TimesAsync() => server.QueryAsync(async db => (
            (await db.GuestConfigurations.AsNoTracking().SingleAsync(c => c.ResourceId == guest)).AppliedAt,
            (await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == guest).OrderBy(k => k.Version).FirstAsync()).LastUsedAt));

        Assert.True(await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
        await server.TickAsync();
        var first = await TimesAsync();
        Assert.Equal(clock.Now, first.Applied);
        Assert.Equal(clock.Now, first.Used);

        clock.Now += TimeSpan.FromMinutes(5);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        Assert.Equal(first, await TimesAsync());
    }

    [Fact]
    public async Task A_result_after_an_uncertain_one_is_taken_once()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository());
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent);
        const string Lost = "the agent lost the attempt's stored result";
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | the agent lost the attempt's stored result
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: uncertain | finished True | outputs False | attempts uncertain | the agent lost the attempt's stored result
            configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Uncertain, new JsonObject(), Lost));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_playbook_holding_the_managed_key_records_its_use_only_when_it_did_something(bool didSomething)
    {
        var repository = Repository.Repository();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        var (server, agent, http) = await ProvisionedAsync(postgres, repository);
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent);
        var recap = didSomething ? ConfigureRunTests.Recap() : Idle;
        EffectPins.Check(didSomething
            ? """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
                access key v1: last used by this run at True | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, recap));
        EffectPins.Check(didSomething
            ? """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
                access key v1: last used by this run at True | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, recap));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("succeeded")]
    public async Task An_outcome_no_person_can_approve_ends_the_run_as_declared(string result)
    {
        var outcomes = $$"""
            outcomes:
              pending-reboot: { result: {{result}} }
            """;
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "2" });
        var recap = ConfigureRunTests.Recap("pending-reboot", "A reboot is due.");
        EffectPins.Check(result == "failed"
            ? """
                taken: True
                run: failed | finished True | The playbook step failed: It reported pending-reboot: A reboot is due..
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: failed | finished True | outputs True | attempts succeeded | It reported pending-reboot: A reboot is due..
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: notifications +1
                """
            : """
                taken: True
                run: succeeded | finished True | The playbook reported pending-reboot: A reboot is due..
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "2"} | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, recap));
        EffectPins.Check(result == "failed"
            ? """
                taken: True
                run: failed | finished True | The playbook step failed: It reported pending-reboot: A reboot is due..
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: failed | finished True | outputs True | attempts succeeded | It reported pending-reboot: A reboot is due..
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: succeeded | finished True | The playbook reported pending-reboot: A reboot is due..
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "2"} | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, recap));
    }

    [Fact]
    public async Task An_outcome_that_does_not_promote_succeeds_and_promotes_nothing()
    {
        const string Outcomes = """
            outcomes:
              up-to-date: { description: Nothing would move., result: succeeded, promote: false }
            """;
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository(extraWorkflow: Outcomes));
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "2" });
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | The playbook reported up-to-date: Every tool is at the approved version..
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded,
            ConfigureRunTests.Recap("up-to-date", "Every tool is at the approved version.")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_approvable_outcome_waits_once_and_the_approved_attempt_decides(bool succeedsAgain)
    {
        const string Outcomes = """
            outcomes:
              deferred:
                description: Work is active on the guest.
                approve:
                  vars: { allow_busy: true }
            """;
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository(extraWorkflow: Outcomes));
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "1.2.3" });
        EffectPins.Check(succeedsAgain
            ? """
                taken: True
                run: awaiting-approval | finished False | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: awaiting-approval | finished False | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: pending | finished False | outputs False | attempts  | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: notifications +1
                """
            : """
                taken: True
                run: awaiting-approval | finished False | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: awaiting-approval | finished False | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: pending | finished False | outputs False | attempts  | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: notifications +1
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads.")));
        EffectPins.Check(succeedsAgain
            ? """
                taken: True
                run: awaiting-approval | finished False | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: awaiting-approval | finished False | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: pending | finished False | outputs False | attempts  | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: awaiting-approval | finished False | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: awaiting-approval | finished False | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: pending | finished False | outputs False | attempts  | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads.")));

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var gate = run.Steps.Single(s => s.Name == StepNames.OutcomeApproval);
        await server.Engine.ApproveAsync(run.Id, gate.Id, gate.PlanSha256!, "ops", null, CancellationToken.None);
        var again = await agent.NextAsync("run");
        var recap = succeedsAgain ? ConfigureRunTests.Recap() : ConfigureRunTests.Recap("deferred", "Still busy.");
        EffectPins.Check(succeedsAgain
            ? """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: succeeded | finished True | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1.2.3"} | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: failed | finished True | The playbook-approved step failed: It reported deferred: Still busy., after the one approved attempt.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: succeeded | finished True | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: failed | finished True | outputs True | attempts succeeded | It reported deferred: Still busy., after the one approved attempt.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: notifications +1
                """,
            await EffectPins.DeliverAsync(server, agent, again, Outcome.Succeeded, recap));
        EffectPins.Check(succeedsAgain
            ? """
                taken: True
                run: succeeded | finished True | -
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: succeeded | finished True | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1.2.3"} | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """
            : """
                taken: True
                run: failed | finished True | The playbook-approved step failed: It reported deferred: Still busy., after the one approved attempt.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                step outcome-approval: succeeded | finished True | outputs False | attempts  | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
                step playbook-approved: failed | finished True | outputs True | attempts succeeded | It reported deferred: Still busy., after the one approved attempt.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, again, Outcome.Succeeded, recap));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("succeeded")]
    public async Task What_the_effect_writes_from_a_reported_outcome_is_masked_whole(string result)
    {
        var outcomes = $$"""
            outcomes:
              pending-reboot: { result: {{result}} }
            """;
        var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        await using var _ = server;
        // A secret that neither reported value holds, but the text joining them does.
        await server.SetSecretAsync("access/app/ssh-key", ConfigureRunTests.SshKey);
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        await server.SetSecretAsync("app/tailscale", "pending-reboot: A reboot is due");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server, setKeys: false);
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent);
        EffectPins.Check(result == "failed"
            ? """
                taken: True
                run: failed | finished True | The playbook step failed: It reported ***.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: failed | finished True | outputs True | attempts succeeded | It reported ***.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: notifications +1
                """
            : """
                taken: True
                run: succeeded | finished True | The playbook reported ***.
                step preview: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step playbook: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied this run's | revision this run's | at True | action setup | snapshot - | proposed this run's | desired this run's
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                """,
            await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded,
            ConfigureRunTests.Recap("pending-reboot", "A reboot is due")));
    }

    [Fact]
    public async Task A_scheduled_run_does_not_wait_on_an_approvable_outcome_and_promotes_nothing()
    {
        const string Deferred = """
            outcomes:
              deferred:
                description: Work is running.
                approve: { vars: { allow_busy: true } }
            """;
        var repository = Repository.Repository("", Deferred);
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile)).Replace(
            "access:\n  user: ops", "schedules:\n    - { action: update-system, cron: \"0 6 * * *\" }\naccess:\n  user: ops"));
        var (server, agent, http) = await ProvisionedAsync(postgres, repository);
        await using var _ = server;
        using var __ = http;
        var first = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "1" });
        await agent.ReportAsync(first, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(2);
        var planner = server.Services.GetRequiredService<Planner>();
        await planner.FireSchedulesAsync(today.AddHours(1), CancellationToken.None);
        await planner.FireSchedulesAsync(today.AddHours(6).AddMinutes(1), CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | The playbook reported deferred: Three threads..
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | It reported deferred. A scheduled run does not wait for a person, so the next time tries again.
            step outcome-approval: skipped | finished False | outputs False | attempts  | The playbook reported an outcome that ends the run.
            step playbook-approved: skipped | finished False | outputs False | attempts  | The playbook reported an outcome that ends the run.
            configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1"} | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads.")));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | The playbook reported deferred: Three threads..
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step playbook: succeeded | finished True | outputs True | attempts succeeded | It reported deferred. A scheduled run does not wait for a person, so the next time tries again.
            step outcome-approval: skipped | finished False | outputs False | attempts  | The playbook reported an outcome that ends the run.
            step playbook-approved: skipped | finished False | outputs False | attempts  | The playbook reported an outcome that ends the run.
            configuration: applied this run's | revision this run's | at True | action setup | snapshot {"version": "1"} | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads.")));
    }

    /// <summary>
    /// A step whose name the code does not know, as a row written by another version would hold: its result is recorded and
    /// the step succeeds, and no effect runs, so nothing is promoted and nothing after it is skipped.
    /// </summary>
    [Fact]
    public async Task A_step_name_the_code_does_not_know_has_no_effect()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, Repository.Repository());
        await using var _ = server;
        using var __ = http;
        var playbook = await PlaybookAsync(server, agent, new JsonObject { ["version"] = "1.2.3" });
        await server.WithDbAsync(db => db.Steps.Where(s => s.Attempts.Any(a => a.Id == Guid.Parse(playbook.AttemptId)))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, "no-such-step")));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step no-such-step: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step no-such-step: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed this run's | desired this run's
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            """, await EffectPins.DeliverAsync(server, agent, playbook, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }
}
