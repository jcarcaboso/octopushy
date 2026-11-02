using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class AccessRotationTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    private static TestRepository Managed(string policy = "")
    {
        var test = Repository.Repository(policy);
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        return test;
    }

    private static async Task<T> WithDbAsync<T>(ServerHarness server, Func<OctopushyDb, Task<T>> read)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
    }

    private static Task<List<GuestAccessKey>> KeysAsync(ServerHarness server) =>
        WithDbAsync(server, db => db.GuestAccessKeys.AsNoTracking().OrderBy(k => k.Version).ToListAsync());

    private static Task<int> InUseAsync(ServerHarness server, Guid resource)
    {
        var name = GuestAccessCredentials.Key(resource);
        return WithDbAsync(server, db => db.StoreEntries.AsNoTracking().Where(e => e.Key == name).Select(e => e.CurrentVersion).SingleAsync());
    }

    /// <summary>A guest Octopushy has logged in to, so it has a key known to work.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http, Guid Resource, string OldKey)> ConfiguredAsync(ServerHarness server)
    {
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        return (agent, http, run.ResourceId, playbook.Secrets[SecretNames.AnsibleSshKey]);
    }

    private static async Task<Run> RotateAsync(ServerHarness server, FakeAgent agent, Guid resource)
    {
        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource, CancellationToken.None);
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        return await server.LatestRunAsync(Guest, Operations.RotateAccess);
    }

    private static async Task DecideAsync(ServerHarness server, HttpClient http, Run run, string stepName, bool approve = true)
    {
        var current = await server.LatestRunAsync(Guest, Operations.RotateAccess);
        var step = current.Steps.Single(s => s.Name == stepName);
        Assert.Equal(StepStatuses.AwaitingApproval, step.Status);
        var path = $"/api/runs/{current.Id}/steps/{step.Id}/{(approve ? "approve" : "reject")}";
        var response = await http.PostAsJsonAsync(path, new { planSha256 = step.PlanSha256, note = "checked" });
        response.EnsureSuccessStatusCode();
    }

    private static JsonNode Input(Assignment assignment) => JsonNode.Parse(assignment.InputJson)!;

    [Fact]
    public async Task A_rotation_adds_verifies_promotes_and_then_removes_in_stages()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, oldKey) = await ConfiguredAsync(server);
        using var _ = http;
        var old = Assert.Single(await KeysAsync(server));

        var run = await RotateAsync(server, agent, resource);
        Assert.Equal(new[]
        {
            StepNames.Preview, StepNames.Approval, StepNames.VerifyCurrent, StepNames.AuthorizeKey, StepNames.VerifyKey,
            StepNames.PromoteKey, StepNames.ApproveRemoval, StepNames.RemoveKey, StepNames.VerifyRemoval, StepNames.ConfirmRefused,
        }, run.Steps.OrderBy(s => s.Ordinal).Select(s => s.Name));
        var candidate = (await KeysAsync(server))[1];
        Assert.NotEqual(old.PublicKey, candidate.PublicKey);
        Assert.False(candidate.Bootstrap);

        // The key in use adds the candidate. The run's files are the server's, not a repository's.
        var authorize = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Authorize, Input(authorize)["playbook"]!.GetValue<string>());
        Assert.Equal(oldKey, authorize.Secrets[SecretNames.AnsibleSshKey]);
        Assert.Equal(candidate.PublicKey, Input(authorize)["vars"]!["access_candidate_key"]!.GetValue<string>());
        await using (var files = new GZipStream(new MemoryStream(authorize.FilesTarGz.ToByteArray()), CompressionMode.Decompress))
        await using (var reader = new TarReader(files))
        {
            Assert.Equal("guest-access/authorize.yml", (await reader.GetNextEntryAsync())!.Name);
        }

        await agent.ReportAsync(authorize, Outcome.Succeeded, ConfigureRunTests.Recap());
        Assert.NotNull((await KeysAsync(server))[1].AuthorizedAt);

        // The candidate alone logs in, and only the candidate's private half is in the assignment.
        var verify = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Verify, Input(verify)["playbook"]!.GetValue<string>());
        Assert.NotEqual(oldKey, verify.Secrets[SecretNames.AnsibleSshKey]);
        Assert.DoesNotContain(oldKey, verify.Secrets.Values);
        await agent.ReportAsync(verify, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        Assert.NotNull((await KeysAsync(server))[1].VerifiedAt);

        // Nothing moves until a person says so, even under an automatic policy.
        Assert.Equal(old.Version, await InUseAsync(server, resource));
        await DecideAsync(server, http, run, StepNames.PromoteKey);
        Assert.Equal(candidate.Version, await InUseAsync(server, resource));
        var promoted = (await KeysAsync(server))[1];
        Assert.Equal("ops", promoted.PromotedBy);
        Assert.Contains("a fresh login with this key alone", promoted.PromotionEvidence);
        Assert.Contains("checked", promoted.PromotionEvidence);

        // Removal is its own decision, and until then the old key is still on the guest.
        await server.TickAsync();
        Assert.Null((await KeysAsync(server))[0].RemovedAt);
        await DecideAsync(server, http, run, StepNames.ApproveRemoval);
        var remove = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Remove, Input(remove)["playbook"]!.GetValue<string>());
        Assert.Equal(old.Fingerprint, Input(remove)["vars"]!["access_removed_keys"]!.AsArray().Single()!.GetValue<string>());
        Assert.DoesNotContain(oldKey, remove.Secrets.Values);
        await agent.ReportAsync(remove, Outcome.Succeeded, ConfigureRunTests.Recap());
        var verifyRemoval = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Verify, Input(verifyRemoval)["playbook"]!.GetValue<string>());
        await agent.ReportAsync(verifyRemoval, Outcome.Succeeded, ConfigureRunTests.Recap());

        // The file says it is gone. Only sshd can say it no longer gets in, so that login uses the old key.
        Assert.Null((await KeysAsync(server))[0].RemovedAt);
        var confirm = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Refused, Input(confirm)["playbook"]!.GetValue<string>());
        Assert.True(Input(confirm)["expectRefused"]!.GetValue<bool>());
        Assert.Contains(oldKey, confirm.Secrets.Values);
        await agent.ReportAsync(confirm, Outcome.Succeeded, Refused());
        await server.TickAsync();

        var finished = await server.LatestRunAsync(Guest, Operations.RotateAccess);
        Assert.Equal(RunStatuses.Succeeded, finished.Status);
        var keys = await KeysAsync(server);
        Assert.NotNull(keys[0].RemovedAt);
        var name = GuestAccessCredentials.Key(resource);
        var revoked = await WithDbAsync(server, db => db.StoreVersions.AsNoTracking().SingleAsync(v => v.Key == name && v.Version == old.Version));
        Assert.NotNull(revoked.RevokedAt);

        // A provisioning plan is not touched by any of it, and OpenTofu still has the first key.
        Assert.True(keys[0].Bootstrap);
        Assert.False(keys[1].Bootstrap);

        var page = await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resource}");
        Assert.Equal(candidate.PublicKey, page!["managedAccess"]!["publicKey"]!.GetValue<string>());
        Assert.Equal("removed", page["managedAccess"]!["keys"]!.AsArray().Single(k => k!["version"]!.GetValue<int>() == old.Version)!["state"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE KEY", await http.GetStringAsync($"/api/runs/{finished.Id}"));
    }

    [Fact]
    public async Task A_candidate_that_cannot_log_in_leaves_working_access_and_is_used_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, oldKey) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "Permission denied (publickey).");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        Assert.Equal(1, await InUseAsync(server, resource));
        var keys = await KeysAsync(server);
        Assert.Null(keys[1].VerifiedAt);
        Assert.Null(keys[0].RemovedAt);

        // Runs carry on with the key that works.
        await server.Services.GetRequiredService<Planner>().ConfigureGuestAsync(resource, "configure", CancellationToken.None);
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        Assert.Equal(oldKey, playbook.Secrets[SecretNames.AnsibleSshKey]);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        // Trying again reuses the candidate, which may already be on the guest.
        await RotateAsync(server, agent, resource);
        Assert.Equal(2, (await KeysAsync(server)).Count);
    }

    [Fact]
    public async Task An_interruption_is_uncertain_changes_nothing_in_use_and_removes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var __ = http;
        await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Uncertain, error: "lost");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        Assert.Equal(1, await InUseAsync(server, resource));
        var keys = await KeysAsync(server);
        Assert.Null(keys[1].AuthorizedAt);
        Assert.Null(keys[0].RemovedAt);
        await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource, CancellationToken.None));
    }

    [Fact]
    public async Task Rejecting_the_promotion_keeps_the_key_in_use()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var __ = http;
        var run = await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.PromoteKey, approve: false);

        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        Assert.Equal(1, await InUseAsync(server, resource));
    }

    [Fact]
    public async Task An_old_key_left_by_a_rejected_removal_goes_with_the_next_rotation()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var __ = http;
        var run = await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.ApproveRemoval, approve: false);
        Assert.Equal(2, await InUseAsync(server, resource));

        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource, CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, RunContext.Parse((await server.LatestRunAsync(Guest, Operations.RotateAccess)).Context).AccessRotation!.Remove);
    }

    [Fact]
    public async Task A_rotation_starts_by_proving_the_key_in_use_and_stops_if_it_cannot()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var provision = await server.LatestRunAsync(Guest, Operations.Provision);
        var old = Assert.Single(await KeysAsync(server));
        Assert.Null(old.VerifiedAt);

        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(provision.ResourceId, CancellationToken.None);
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        var verify = await agent.NextAsync("run");
        Assert.Equal(GuestAccessPlaybooks.Verify, Input(verify)["playbook"]!.GetValue<string>());
        await agent.ReportAsync(verify, Outcome.Failed, error: "Permission denied (publickey).");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        Assert.Equal(1, await InUseAsync(server, provision.ResourceId));
        Assert.Null((await KeysAsync(server))[1].AuthorizedAt);
    }

    [Fact]
    public async Task A_declared_key_is_not_rotated()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, _, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().RotateAccessAsync(run.ResourceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_replacement_after_a_rotation_plans_again_with_the_key_in_use()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        var run = await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.ApproveRemoval);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Refused());
        await server.TickAsync();
        var keys = await KeysAsync(server);

        // A plan for the machine that exists carries the key it was created with, so it changes nothing.
        var planner = server.Services.GetRequiredService<Planner>();
        await planner.PlanGuestAsync(resource, CancellationToken.None);
        var plan = await agent.NextAsync("preview");
        Assert.Equal(new[] { keys[0].PublicKey }, ManagedKeys(plan, keys));

        // One that makes a new machine stops, because that machine would have a key nobody holds.
        await agent.ReportAsync(plan, Outcome.Succeeded, MachinePlan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var stopped = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, stopped.Status);
        Assert.Contains("Plan again", stopped.Detail);

        await planner.PlanGuestAsync(resource, CancellationToken.None);
        Assert.Equal(new[] { keys[1].PublicKey }, ManagedKeys(await agent.NextAsync("preview"), keys));
    }

    /// <summary>Which of Octopushy's keys a plan carries, read as values: JSON escapes some characters of a key.</summary>
    private static string[] ManagedKeys(Assignment plan, List<GuestAccessKey> keys) =>
        Input(plan)["variables"]!["guest"]!["sshAuthorizedKeys"]!.AsArray().Select(k => k!.GetValue<string>())
            .Intersect(keys.Select(k => k.PublicKey)).ToArray();

    private static JsonObject MachinePlan(int vmid = 310) => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = "proxmox_virtual_environment_vm.guest",
            ["type"] = GuestVmPlan.VmResourceType,
            ["actions"] = new JsonArray("create"),
            ["vmId"] = vmid,
            ["node"] = "korriban",
        }),
        ["counts"] = new JsonObject { ["create"] = 1, ["update"] = 0, ["delete"] = 0, ["replace"] = 0 },
        ["toolVersion"] = "1.13.1",
    };

    private static JsonObject Refused()
    {
        var outputs = ConfigureRunTests.Recap();
        outputs["refused"] = true;
        return outputs;
    }

    private static async Task RotateToTheEndAsync(ServerHarness server, FakeAgent agent, HttpClient http, Guid resource)
    {
        var run = await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.PromoteKey);
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.ApproveRemoval);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
    }

    [Fact]
    public async Task A_plan_that_makes_another_machine_leaves_this_ones_key_alone()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateToTheEndAsync(server, agent, http, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Refused());
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, MachinePlan(vmid: 999), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.True((await KeysAsync(server))[0].Bootstrap);
    }

    [Fact]
    public async Task A_plan_that_makes_something_else_leaves_the_machines_key_alone()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateToTheEndAsync(server, agent, http, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Refused());
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.True((await KeysAsync(server))[0].Bootstrap);
        Assert.NotEqual(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_removal_nobody_could_verify_stays_on_the_list()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateToTheEndAsync(server, agent, http, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "Connection timed out");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        var name = GuestAccessCredentials.Key(resource);
        Assert.Null((await KeysAsync(server))[0].RemovedAt);
        Assert.Null(await WithDbAsync(server, db => db.StoreVersions.AsNoTracking().Where(v => v.Key == name && v.Version == 1).Select(v => v.RevokedAt).SingleAsync()));

        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource, CancellationToken.None);
        Assert.Contains(1, RunContext.Parse((await server.LatestRunAsync(Guest, Operations.RotateAccess)).Context).AccessRotation!.Remove);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_key_sshd_was_not_seen_to_refuse_stays_on_the_list(bool reportedSuccess)
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateToTheEndAsync(server, agent, http, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        // An agent too old to know the check logs in, or fails, and says nothing of sshd refusing.
        if (reportedSuccess)
            await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        else
            await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "sshd still accepts a key that was removed.");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.RotateAccess)).Status);
        var name = GuestAccessCredentials.Key(resource);
        Assert.Null((await KeysAsync(server))[0].RemovedAt);
        Assert.Null(await WithDbAsync(server, db => db.StoreVersions.AsNoTracking().Where(v => v.Key == name && v.Version == 1).Select(v => v.RevokedAt).SingleAsync()));
        Assert.Equal(2, await InUseAsync(server, resource));
    }

    [Fact]
    public async Task A_plan_that_cannot_place_a_machine_stops_once_the_key_has_been_rotated()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        await RotateToTheEndAsync(server, agent, http, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Refused());
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource, CancellationToken.None);
        var plan = MachinePlan();
        plan["changes"]![0]!.AsObject().Remove("vmId");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("without saying its VMID", run.Detail);
        Assert.True((await KeysAsync(server))[0].Bootstrap);
    }

    [Fact]
    public async Task Promoting_one_guests_key_leaves_anothers_alone()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, resource, _) = await ConfiguredAsync(server);
        using var _ = http;
        var other = Guid.NewGuid();
        var otherKey = GuestAccessCredentials.Key(other);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<VariableStore>().SetInternalAsync(otherKey, "another guest's", Visibilities.Secret, null, CancellationToken.None);
        }

        var run = await RotateAsync(server, agent, resource);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await DecideAsync(server, http, run, StepNames.PromoteKey);

        Assert.Equal(2, await InUseAsync(server, resource));
        Assert.Equal(1, await WithDbAsync(server, db => db.StoreEntries.AsNoTracking().Where(e => e.Key == otherKey).Select(e => e.CurrentVersion).SingleAsync()));
    }
}
