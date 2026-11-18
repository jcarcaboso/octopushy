using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>
/// A retire's <see cref="StepNames.VerifyAbsent"/>: the guest is retired and verified, its binding and reservation are cleared, its
/// access key, host key and container host keys are removed, and its services are retired with their runs that can still be
/// ended. Each case is one of <see cref="ProvisioningTests"/>, <see cref="GuestRemovalTests"/> or <see cref="PipelineRunTests"/>,
/// pinned whole with a failure, a late result and a second delivery of every result. A run whose operation the code does not
/// know, as a row written by another version would hold, is pinned too.
/// </summary>
public class VerifyAbsentEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string UnknownOperation = "no-such-operation";
    private const string Other = "home/korriban/krb-other-01";

    private static readonly JsonObject Absent = new() { ["present"] = false };

    /// <summary>A bound guest retired up to its verify-absent, which is returned.</summary>
    private static async Task<Assignment> VerifyAbsentAsync(ServerHarness server, FakeAgent agent)
    {
        await EstablishAsync(server, agent);
        await RetireAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        return await agent.NextAsync("verify-absent");
    }

    /// <summary>
    /// Gives the guest, and another, each credential Octopushy can hold for one, and services with runs: the guest's own, one
    /// already retired, and another guest's. A run that has not ended stays blocked at every tick, as no revision is current for it.
    /// </summary>
    private static async Task<Guid> HoldAsync(ServerHarness server, DateTimeOffset? at = null)
    {
        var guest = await server.ResourceAsync(Guest);
        var other = Guid.NewGuid();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        var now = at ?? DateTimeOffset.UtcNow;
        // A context a run of the code has: the guest's own retire's.
        var context = (await server.LatestRunAsync(Guest, Operations.Retire)).Context;
        db.Resources.Add(new Resource { Id = other, Kind = ResourceKinds.Guest, Address = Other, ParentAddress = Installation, Lab = "home" });
        foreach (var id in new[] { guest.Id, other })
        {
            await GuestAccessCredentials.EnsureAsync(db, store, id, "krb", now, CancellationToken.None);
            await GuestHostCredentials.EnsureAsync(db, store, id, "krb", now, CancellationToken.None);
            await ContainerHostKeys.RecordAsync(store, id, "ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0\n", CancellationToken.None);
        }

        void Service(string address, string parent, DateTimeOffset? retired, params string[] statuses)
        {
            var id = Guid.NewGuid();
            db.Resources.Add(new Resource { Id = id, Kind = ResourceKinds.Service, Address = address, ParentAddress = parent, Lab = "home", RetiredAt = retired });
            foreach (var (status, i) in statuses.Select((status, i) => (status, i)))
            {
                var run = new Run
                {
                    Id = Guid.CreateVersion7(), ResourceId = id, Operation = Operations.Pipeline, Reason = "manual", Status = status,
                    CreatedAt = now.AddSeconds(i), Context = context,
                };
                db.Runs.Add(run);
                // A run that waits has an approval that waits for a person, as a pipeline's does.
                if (status == RunStatuses.AwaitingApproval)
                    db.Steps.Add(new Step { Id = Guid.CreateVersion7(), RunId = run.Id, Name = StepNames.Approval, Kind = StepKinds.Approval, Status = StepStatuses.AwaitingApproval });
            }
        }

        Service($"{Guest}/web", Guest, null, RunStatuses.AwaitingApproval, RunStatuses.Uncertain, RunStatuses.Succeeded);
        Service($"{Guest}/old", Guest, now.AddDays(-1), RunStatuses.AwaitingApproval);
        Service($"{Other}/web", Other, null, RunStatuses.AwaitingApproval);
        await db.SaveChangesAsync();
        return other;
    }

    /// <summary>The other guest's credentials, which retiring this one leaves.</summary>
    private static Task<string> OtherAsync(ServerHarness server, Guid other) =>
        server.QueryAsync(async db =>
        {
            var keys = new[] { GuestAccessCredentials.Key(other), GuestHostCredentials.Key(other), ContainerHostKeys.Key(other) };
            return $"other: entries {await db.StoreEntries.CountAsync(e => keys.Contains(e.Key))} | versions {await db.StoreVersions.CountAsync(v => keys.Contains(v.Key))}"
                   + $" | access {await db.GuestAccessKeys.CountAsync(k => k.ResourceId == other)} | host {await db.GuestHostKeys.CountAsync(k => k.ResourceId == other)}";
        });

    private static Task RenameOperationAsync(ServerHarness server, Assignment assignment) =>
        server.WithDbAsync(db => db.Runs.Where(r => db.Attempts.Any(a => a.Id == Guid.Parse(assignment.AttemptId) && a.RunId == r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Operation, UnknownOperation)));

    [Fact]
    public async Task A_verify_absent_retires_the_guest_and_ends_what_it_held_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var absent = await VerifyAbsentAsync(server, agent);
        var other = await HoldAsync(server);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: guest_access_keys -1, guest_host_keys -1, store_entries -3, store_versions -3
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            store access: entry False | versions 0
            store host key: entry False | versions 0
            store container host keys: entry False | versions 0
            keys: access 0 | host 0
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired True | runs cancelled (The guest was removed.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Succeeded, Absent) + "\n" + await OtherAsync(server, other));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            store access: entry False | versions 0
            store host key: entry False | versions 0
            store container host keys: entry False | versions 0
            keys: access 0 | host 0
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired True | runs cancelled (The guest was removed.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Succeeded, Absent) + "\n" + await OtherAsync(server, other));
    }

    [Fact]
    public async Task A_verify_absent_that_fails_records_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var absent = await VerifyAbsentAsync(server, agent);
        var other = await HoldAsync(server);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify-absent step failed: VM 310 is still on korriban
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: failed | finished True | outputs False | attempts failed | VM 310 is still on korriban
            configuration: none
            access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs False
            rows: notifications +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            store access: entry True | versions 1
            store host key: entry True | versions 1
            store container host keys: entry True | versions 1
            keys: access 1 | host 1
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Failed, null, "VM 310 is still on korriban") + "\n" + await OtherAsync(server, other));
    }

    [Fact]
    public async Task A_verify_absent_reported_uncertain_is_settled_once_by_its_late_result()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var absent = await VerifyAbsentAsync(server, agent);
        var other = await HoldAsync(server);
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: uncertain | finished True | outputs False | attempts uncertain | lost
            configuration: none
            access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs False
            rows: notifications +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            store access: entry True | versions 1
            store host key: entry True | versions 1
            store container host keys: entry True | versions 1
            keys: access 1 | host 1
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Uncertain, new JsonObject(), "lost") + "\n" + await OtherAsync(server, other));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: guest_access_keys -1, guest_host_keys -1, store_entries -3, store_versions -3
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            store access: entry False | versions 0
            store host key: entry False | versions 0
            store container host keys: entry False | versions 0
            keys: access 0 | host 0
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired True | runs cancelled (The guest was removed.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Succeeded, Absent) + "\n" + await OtherAsync(server, other));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            store access: entry False | versions 0
            store host key: entry False | versions 0
            store container host keys: entry False | versions 0
            keys: access 0 | host 0
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired True | runs cancelled (The guest was removed.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Succeeded, Absent) + "\n" + await OtherAsync(server, other));
    }

    [Fact]
    public async Task A_verify_absent_of_an_operation_the_code_does_not_know_retires_the_guest_as_a_retire_does()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var absent = await VerifyAbsentAsync(server, agent);
        var other = await HoldAsync(server);
        await RenameOperationAsync(server, absent);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: guest_access_keys -1, guest_host_keys -1, store_entries -3, store_versions -3
            operation: no-such-operation
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            store access: entry False | versions 0
            store host key: entry False | versions 0
            store container host keys: entry False | versions 0
            keys: access 0 | host 0
            service home/korriban/krb-app-01/old: retired True | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            service home/korriban/krb-app-01/web: retired True | runs cancelled (The guest was removed.), uncertain (-), succeeded (-)
            service home/korriban/krb-other-01/web: retired False | runs blocked (Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.)
            other: entries 3 | versions 3 | access 1 | host 1
            """, await EffectPins.DeliverRetiredAsync(server, agent, absent, Outcome.Succeeded, Absent) + "\n" + await OtherAsync(server, other));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        var absent = await VerifyAbsentAsync(server, agent);
        var resource = (await server.ResourceAsync(Guest)).Id;
        Task<string> ServicesAsync() => server.QueryAsync(async db => string.Join(", ",
            (await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Service).OrderBy(r => r.Address).ToListAsync())
            .Select(r => $"{r.Address} {r.RetiredAt?.ToString("O") ?? "-"}")));
        await HoldAsync(server, clock.Now);

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(absent, Outcome.Succeeded, Absent.DeepClone().AsObject()));
        await server.TickAsync();
        var first = await EffectPins.GuestTimesAsync(server, resource) + "\n" + await ServicesAsync();
        clock.Now += TimeSpan.FromSeconds(5);
        await agent.ReportAsync(absent, Outcome.Succeeded, Absent.DeepClone().AsObject());
        await server.TickAsync();
        Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource) + "\n" + await ServicesAsync());
        EffectPins.Check("""
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:00.0000000+00:00 | verified 2026-11-17T09:00:01.0000000+00:00 | bound 2026-11-17T09:00:00.0000000+00:00 | reservation applied - | retired 2026-11-17T09:00:01.0000000+00:00 | attached -
            home/korriban/krb-app-01/old 2026-11-16T09:00:00.0000000+00:00, home/korriban/krb-app-01/web 2026-11-17T09:00:01.0000000+00:00, home/korriban/krb-other-01/web -
            """, first);
    }
}
