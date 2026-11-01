using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ContainerGuestRunTests(PostgresFixture postgres)
{
    private const string Guest = Definitions.ContainerGuestTests.Guest;
    private const string GuestFile = Definitions.ContainerGuestTests.GuestPath;
    private const string Password = "a-dns-admin-password";

    private static readonly Dictionary<string, string> Reporter = new()
    {
        ["Octopushy:DiskReporter:Enabled"] = "true",
        ["Octopushy:DiskReporter:PublicUrl"] = "https://octopushy.test",
        ["Octopushy:DiskReporter:BinaryUrl"] = "https://releases.test/disk-reporter",
        ["Octopushy:DiskReporter:BinarySha256"] = new string('a', 64),
    };

    private static TestRepository Repository()
    {
        var test = Definitions.ContainerGuestTests.Repository();
        var text = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        test.Write(GuestFile, text.TrimEnd('\n') + "\n  secrets:\n    admin_password: { store: dns/admin }");
        // Only the container: the test agent answers one guest's steps.
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        return test;
    }

    [Fact]
    public async Task A_container_goes_through_the_lxc_api_and_only_the_apply_receives_its_secret()
    {
        using var repository = Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: Reporter);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);

        var plan = await agent.NextAsync("preview");
        var planInput = JsonNode.Parse(plan.InputJson)!;
        Assert.False(planInput["secretEnv"]!.AsObject().ContainsKey("TF_VAR_admin_password"));
        Assert.DoesNotContain(Password, plan.Secrets.Values);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());

        var apply = await agent.NextAsync("apply");
        var applyInput = JsonNode.Parse(apply.InputJson)!;
        Assert.Equal("recipe.TF_VAR_admin_password", applyInput["secretEnv"]!["TF_VAR_admin_password"]!.GetValue<string>());
        Assert.Equal(Password, apply.Secrets["recipe.TF_VAR_admin_password"]);
        Assert.DoesNotContain(Password, apply.InputJson);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject(), ["sensitiveOutputs"] = new JsonArray() });

        var verify = await agent.NextAsync("verify-guest");
        var verifyInput = JsonNode.Parse(verify.InputJson)!;
        Assert.Equal("lxc", verifyInput["guestKind"]!.GetValue<string>());
        Assert.Equal("10.1.0.33", verifyInput["expectIpv4"]!.GetValue<string>());
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.InstallDiskReporter);
        Assert.DoesNotContain(Password, run.Context);
        Assert.Empty(agent.TakeAssignments());

        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        Assert.Equal("container", inventory["labs"]![0]!["installations"]![0]!["guests"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_secret_blocks_the_run_before_its_plan()
    {
        using var repository = Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("dns/admin", run.Detail);
    }

    [Fact]
    public async Task Retiring_a_container_goes_through_the_lxc_api_without_its_secret()
    {
        using var repository = Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject(), ["sensitiveOutputs"] = new JsonArray() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();

        var text = await File.ReadAllTextAsync(Path.Combine(repository.Root, GuestFile));
        repository.Write(GuestFile, "lifecycle: retired\n" + text.TrimEnd('\n'));
        server.Commit("Retire the container");
        await server.SyncAndTrustAsync();

        var plan = await agent.NextAsync("preview");
        Assert.False(JsonNode.Parse(plan.InputJson)!["secretEnv"]!.AsObject().ContainsKey("TF_VAR_admin_password"));
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));

        var unprotect = await agent.NextAsync("set-protection");
        var input = JsonNode.Parse(unprotect.InputJson)!;
        Assert.Equal("lxc", input["guestKind"]!.GetValue<string>());
        Assert.Equal("krb-ct-01", input["name"]!.GetValue<string>());
        await agent.ReportAsync(unprotect, Outcome.Succeeded, new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });

        var apply = await agent.NextAsync("apply");
        Assert.False(apply.Secrets.ContainsKey("recipe.TF_VAR_admin_password"));
    }
}
