using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// An access rotation's logins, <see cref="StepNames.VerifyCurrent"/>, <see cref="StepNames.AuthorizeKey"/>,
/// <see cref="StepNames.VerifyKey"/> and <see cref="StepNames.ConfirmRefused"/>: what each records about the guest's keys, the run that
/// verified one, and the versions revoked in the store, only once sshd refused every key to remove. Each case is one of
/// <see cref="AccessRotationTests"/>, pinned whole, with its failures and a second delivery of every result.
/// </summary>
public class AccessRotationEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    private static TestRepository Managed()
    {
        var test = Repository.Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        return test;
    }

    /// <summary>A guest Octopushy has logged in to with its own key, by a configure run.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http, Guid Resource)> ConfiguredAsync(ServerHarness server)
    {
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        return (agent, http, (await server.ResourceAsync(Guest)).Id);
    }

    /// <summary>Starts a rotation and previews it; returns its verify-current login.</summary>
    private static async Task<Assignment> RotateAsync(ServerHarness server, FakeAgent agent, Guid resource)
    {
        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource, CancellationToken.None);
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        return await agent.NextAsync("run");
    }

    private static async Task DecideAsync(ServerHarness server, HttpClient http, string stepName, bool approve = true)
    {
        var current = await server.LatestRunAsync(Guest, Operations.RotateAccess);
        var step = current.Steps.Single(s => s.Name == stepName);
        Assert.Equal(StepStatuses.AwaitingApproval, step.Status);
        var path = $"/api/runs/{current.Id}/steps/{step.Id}/{(approve ? "approve" : "reject")}";
        (await http.PostAsJsonAsync(path, new { planSha256 = step.PlanSha256, note = "checked" })).EnsureSuccessStatusCode();
    }

    /// <summary>Takes a rotation through every stage before confirm-refused, and returns that login.</summary>
    private static async Task<Assignment> ToConfirmAsync(ServerHarness server, FakeAgent agent, HttpClient http, Guid resource)
    {
        await agent.ReportAsync(await RotateAsync(server, agent, resource), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.ApproveRemoval);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        return await agent.NextAsync("run");
    }

    /// <summary>A guest whose first rotation ended at a rejected removal, so its next rotation has two old keys to remove.</summary>
    private static async Task<Assignment> ToConfirmTwoAsync(ServerHarness server, FakeAgent agent, HttpClient http, Guid resource)
    {
        await agent.ReportAsync(await RotateAsync(server, agent, resource), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.ApproveRemoval, approve: false);
        return await ToConfirmAsync(server, agent, http, resource);
    }

    private static JsonObject Refused(int keys)
    {
        var outputs = ConfigureRunTests.Recap();
        outputs["refusedKeys"] = keys;
        return outputs;
    }

    [Fact]
    public async Task Each_login_of_a_rotation_records_what_it_showed_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;

        var verifyCurrent = await RotateAsync(server, agent, resource);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: running | finished False | outputs False | attempts assigned | -
            step verify-key: pending | finished False | outputs False | attempts  | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +2
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyCurrent, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: running | finished False | outputs False | attempts assigned | -
            step verify-key: pending | finished False | outputs False | attempts  | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyCurrent, Outcome.Succeeded, ConfigureRunTests.Recap()));

        var authorize = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: running | finished False | outputs False | attempts assigned | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +2
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, authorize, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: running | finished False | outputs False | attempts assigned | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, authorize, Outcome.Succeeded, ConfigureRunTests.Recap()));

        var verifyKey = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: awaiting-approval | finished False | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: awaiting-approval | finished False | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Succeeded, ConfigureRunTests.Recap()));

        await DecideAsync(server, http, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.ApproveRemoval);
        var remove = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: running | finished False | outputs False | attempts assigned | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +2
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, remove, Outcome.Succeeded, ConfigureRunTests.Recap()));
        var verifyRemoval = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: running | finished False | outputs False | attempts assigned | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +3
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyRemoval, Outcome.Succeeded, ConfigureRunTests.Recap()));

        var confirm = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed True
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(1)));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed True
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(1)));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        // The clock moves before every second delivery, so an effect that ran again would move a time it set. By seconds, well
        // inside an attempt's lease, so every first delivery is on time.
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, Managed(), clock: clock);
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;

        async Task TwiceAsync(Assignment assignment, JsonObject outputs)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject()));
            await server.TickAsync();
            var first = await EffectPins.AccessTimesAsync(server, resource);
            clock.Now += TimeSpan.FromSeconds(5);
            await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject());
            await server.TickAsync();
            Assert.Equal(first, await EffectPins.AccessTimesAsync(server, resource));
        }

        var start = clock.Now!.Value;
        await TwiceAsync(await RotateAsync(server, agent, resource), ConfigureRunTests.Recap());
        await TwiceAsync(await agent.NextAsync("run"), ConfigureRunTests.Recap());
        await TwiceAsync(await agent.NextAsync("run"), ConfigureRunTests.Recap());
        await DecideAsync(server, http, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, StepNames.ApproveRemoval);
        await TwiceAsync(await agent.NextAsync("run"), ConfigureRunTests.Recap());
        await TwiceAsync(await agent.NextAsync("run"), ConfigureRunTests.Recap());
        await TwiceAsync(await agent.NextAsync("run"), Refused(1));

        // Each time is the one its own step's first delivery read, six seconds apart.
        string At(int step) => (start + TimeSpan.FromSeconds(6 * step - 5)).ToString("O");
        Assert.Equal($"""
            v1: authorized - | verified {At(1)} | removed {At(6)}
            v2: authorized {At(2)} | verified {At(3)} | removed -
            store v1: revoked {At(6)}
            store v2: revoked -
            """.ReplaceLineEndings("\n"), await EffectPins.AccessTimesAsync(server, resource));
    }

    [Fact]
    public async Task Confirm_refused_with_every_key_refused_removes_and_revokes_both_old_keys()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmTwoAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed True
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed True
            access key v3: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by another | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            access key v3: verified by this run | promoted True | bootstrap False
            store: current v3 | latest v3
            store v1: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v2: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v3: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(2)));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed True
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed True
            access key v3: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by another | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            access key v3: verified by this run | promoted True | bootstrap False
            store: current v3 | latest v3
            store v1: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v2: activated True | revoked True by octopushy | Removed from the guest by a rotation, and verified gone.
            store v3: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(2)));
    }

    [Fact]
    public async Task Confirm_refused_with_fewer_keys_refused_than_asked_fails_and_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmTwoAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            access key v3: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by another | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            access key v3: verified by this run | promoted True | bootstrap False
            store: current v3 | latest v3
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            store v3: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(1)));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            access key v3: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by another | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            access key v3: verified by this run | promoted True | bootstrap False
            store: current v3 | latest v3
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            store v3: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(1)));
    }

    [Fact]
    public async Task Confirm_refused_with_no_key_refused_fails_and_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(0)));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(0)));
    }

    [Fact]
    public async Task Confirm_refused_with_more_keys_refused_than_asked_fails_and_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, Refused(2)));
    }

    [Fact]
    public async Task Confirm_refused_whose_outputs_do_not_say_fails_and_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: Nothing showed sshd refusing every old key.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | Nothing showed sshd refusing every old key.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }

    [Fact]
    public async Task Confirm_refused_that_fails_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var confirm = await ToConfirmAsync(server, agent, http, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The confirm-refused step failed: sshd still accepts a key that was removed.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | -
            step promote-key: succeeded | finished True | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: succeeded | finished True | outputs False | attempts  | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step remove-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-removal: succeeded | finished True | outputs True | attempts succeeded | -
            step confirm-refused: failed | finished True | outputs True | attempts failed | sshd still accepts a key that was removed.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted True | bootstrap False
            store: current v2 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated True | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, confirm, Outcome.Failed, Refused(1), "sshd still accepts a key that was removed."));
    }

    [Fact]
    public async Task A_verify_current_that_fails_records_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        var verifyCurrent = await RotateAsync(server, agent, resource);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify-current step failed: Permission denied (publickey).
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: failed | finished True | outputs False | attempts failed | Permission denied (publickey).
            step authorize-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step promote-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step approve-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step remove-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step confirm-refused: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified False | removed False
            access key v2: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by - | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyCurrent, Outcome.Failed, null, "Permission denied (publickey)."));
    }

    [Fact]
    public async Task A_verify_key_that_fails_records_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        await agent.ReportAsync(await RotateAsync(server, agent, resource), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        var verifyKey = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify-key step failed: Permission denied (publickey).
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: failed | finished True | outputs False | attempts failed | Permission denied (publickey).
            step promote-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step approve-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step remove-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step confirm-refused: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Failed, null, "Permission denied (publickey)."));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify-key step failed: Permission denied (publickey).
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: failed | finished True | outputs False | attempts failed | Permission denied (publickey).
            step promote-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step approve-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step remove-key: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify-removal: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step confirm-refused: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Failed, null, "Permission denied (publickey)."));
    }

    [Fact]
    public async Task An_authorize_key_interrupted_is_uncertain_and_its_late_result_is_taken_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        await agent.ReportAsync(await RotateAsync(server, agent, resource), Outcome.Succeeded, ConfigureRunTests.Recap());
        var authorize = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: uncertain | finished True | outputs False | attempts uncertain | lost
            step verify-key: pending | finished False | outputs False | attempts  | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, authorize, Outcome.Uncertain, new JsonObject(), "lost"));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step verify-key: running | finished False | outputs False | attempts assigned | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +2
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, authorize, Outcome.Succeeded, ConfigureRunTests.Recap()));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step verify-key: running | finished False | outputs False | attempts assigned | -
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, authorize, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }

    [Fact]
    public async Task A_verify_key_interrupted_and_then_answered_is_taken_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource) = await ConfiguredAsync(server);
        using var _ = http;
        await agent.ReportAsync(await RotateAsync(server, agent, resource), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        var verifyKey = await agent.NextAsync("run");
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: uncertain | finished True | outputs False | attempts uncertain | lost
            step promote-key: pending | finished False | outputs False | attempts  | -
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by - | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Uncertain, new JsonObject(), "lost"));
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step preview: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify-current: succeeded | finished True | outputs True | attempts succeeded | -
            step authorize-key: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-key: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step promote-key: awaiting-approval | finished False | outputs False | attempts  | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step approve-removal: pending | finished False | outputs False | attempts  | -
            step remove-key: pending | finished False | outputs False | attempts  | -
            step verify-removal: pending | finished False | outputs False | attempts  | -
            step confirm-refused: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision this run's | at True | action setup | snapshot - | proposed another | desired another
            access key v1: last used by another at True | provisioned True | authorized False | verified True | removed False
            access key v2: last used - at False | provisioned False | authorized True | verified True | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            access key v1: verified by this run | promoted False | bootstrap True
            access key v2: verified by this run | promoted False | bootstrap False
            store: current v1 | latest v2
            store v1: activated True | revoked False by - | -
            store v2: activated False | revoked False by - | -
            """, await EffectPins.DeliverWithAccessAsync(server, agent, verifyKey, Outcome.Succeeded, ConfigureRunTests.Recap()));
    }
}
