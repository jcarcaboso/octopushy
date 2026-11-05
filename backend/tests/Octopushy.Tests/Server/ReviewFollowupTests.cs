using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// The reproductions of a re-check of managed host keys, copied as the reviewer wrote them: a keep-machine plan after an
/// abandoned replacement must not pin a key the machine was never given, and a restarted controller must still mask what
/// an outstanding attempt reports.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReviewFollowupTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string NodeKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ root@korriban";

    private static async Task<FakeAgent> ReadyAsync(ServerHarness server)
    {
        await server.SetSecretAsync("access/app/ssh-key", "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n");
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        await server.SetPlainAsync("proxmox/korriban/ssh-host-key", NodeKey);
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var installation = await server.ResourceAsync("home/korriban");
        (await http.PostAsync($"/api/installations/{installation.Id}/snippets-key", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        return agent;
    }

    private static async Task ProvisionAsync(FakeAgent agent, ServerHarness server)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
    }

    private static async Task PlanAgainAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
    }

    /// <summary>A plan that replaces the guest's own VM, as the agent summarizes one.</summary>
    private static JsonObject Replaces(string reason = "replace_because_cannot_update") => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = $"{GuestVmPlan.VmResourceType}.guest",
            ["type"] = GuestVmPlan.VmResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray("delete", "create"),
            ["actionReason"] = reason,
            ["vmId"] = 310,
            ["node"] = "korriban",
        }),
        ["counts"] = new JsonObject { ["create"] = 0, ["update"] = 0, ["delete"] = 0, ["replace"] = 1 },
        ["toolVersion"] = "1.13.1",
    };

    private static string SnippetKeyOf(Assignment plan) =>
        JsonNode.Parse(plan.InputJson)!["variables"]!["octopushy_snippets"]!["hostPublicKey"]!.GetValue<string>();

    private static Task<List<string>> PublicKeysAsync(ServerHarness server) =>
        server.QueryAsync(db => db.GuestHostKeys.AsNoTracking().OrderBy(k => k.CreatedAt).Select(k => k.PublicKey).ToListAsync());

    private static Task<string> PrivateHalfAsync(ServerHarness server, Guid resource, int version) => server.QueryAsync(async _ =>
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<VariableStore>().GetVersionInternalAsync(GuestHostCredentials.Key(resource), version, CancellationToken.None))!;
    });


    [Fact]
    public async Task Restarted_controller_masks_late_apply_diagnostics()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var line = (await PrivateHalfAsync(server, resource.Id, 1)).Split('\n')[1];
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update"));
        var apply = await agent.NextAsync("apply");
        Assert.Contains(SecretNames.HostKeyMask(1), apply.Secrets.Keys);
        await server.RestartFixtureAsync();
        Assert.True(await agent.ReportAsync(apply, Outcome.Failed, error: $"provider diagnostic {line}"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(apply.AttemptId)));
        Assert.False((attempt.Error ?? "").Contains(line), "Late apply error persisted an unmasked host private-key line after controller restart.");
        Assert.DoesNotContain(line, run.Steps.Single(s => s.Name == StepNames.Apply).Detail ?? "");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_newer_plan_does_not_discard_last_spent_key(bool firstWasDelivered)
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        if (firstWasDelivered)
        {
            await ProvisionAsync(agent, server);
            await PlanAgainAsync(server);
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("stale key"));
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("new key"));
            var replacement = await server.LatestRunAsync(Guest, Operations.Provision);
            var approval = replacement.Steps.Single(s => s.Name == StepNames.Approval);
            await server.Engine.ApproveAsync(replacement.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
            await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
                new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        }
        else
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        // The create really installed this key, then a later provider operation failed.
        var actualKey = (await PublicKeysAsync(server)).Last();
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "VM created, later provider operation failed");
        if (firstWasDelivered)
            await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["protection"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        // A replacement plan makes another unused key, then is rejected.
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("spent key"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("unused key"));
        var rejected = await server.LatestRunAsync(Guest, Operations.Provision);
        var rejectApproval = rejected.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.RejectAsync(rejected.Id, rejectApproval.Id, "ops", null, CancellationToken.None);
        // The next plan keeps the existing VM and only changes its snippet.
        await PlanAgainAsync(server);
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("keep existing VM"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var version = RunContext.Parse(run.Context).SecretVersions[SecretNames.GuestHostKey];
        var carried = await server.QueryAsync(db => db.GuestHostKeys.SingleAsync(k => k.ResourceId == run.ResourceId && k.Version == version));
        if (run.Steps.Single(s => s.Name == StepNames.Plan).Status != StepStatuses.Succeeded)
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("keep VM, rebased key"));
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        var pinned = await server.QueryAsync(db => GuestHostCredentials.PinnedAsync(db, run.ResourceId, CancellationToken.None));
        Assert.True(actualKey == pinned!.PublicKey, $"Keep-machine update succeeded but pinned v{pinned.Version}, not the key of the VM created by the failed apply; prior delivery={firstWasDelivered}.");
    }
}
