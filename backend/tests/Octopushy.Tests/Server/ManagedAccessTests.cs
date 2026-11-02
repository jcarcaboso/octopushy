using System.Diagnostics;
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
public class ManagedAccessTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string PrivateMarker = "OPENSSH PRIVATE KEY";

    private sealed class Cleanup(DirectoryInfo directory) : IDisposable
    {
        public void Dispose() => directory.Delete(recursive: true);
    }

    private static TestRepository Managed()
    {
        var test = Repository.Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        return test;
    }

    private static Task ConfigureAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    private static async Task<T> WithDbAsync<T>(ServerHarness server, Func<OctopushyDb, Task<T>> read)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
    }

    private static Task<GuestAccessKey> KeyAsync(ServerHarness server) =>
        WithDbAsync(server, db => db.GuestAccessKeys.AsNoTracking().SingleAsync());

    private static async Task<Assignment> ConfiguredAsync(ServerHarness server, FakeAgent agent)
    {
        await ConfigureAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        return playbook;
    }

    [Fact]
    public void A_generated_key_is_one_ssh_reads_and_its_public_half_matches()
    {
        var (privateKey, publicKey, fingerprint) = GuestAccessCredentials.Generate("octopushy@krb-app-01");
        var directory = Directory.CreateTempSubdirectory("octopushy-key-");
        using var cleanup = new Cleanup(directory);
        var file = Path.Combine(directory.FullName, "key");
        File.WriteAllText(file, privateKey);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        string SshKeygen(params string[] arguments)
        {
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return output;
        }

        Assert.StartsWith(string.Join(' ', publicKey.Split(' ')[..2]), SshKeygen("-y", "-f", file));
        Assert.Contains(fingerprint, SshKeygen("-l", "-f", file));
        Assert.EndsWith("octopushy@krb-app-01", publicKey);
    }

    [Fact]
    public async Task The_public_half_reaches_provisioning_and_only_a_playbook_gets_the_private_half()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var key = await KeyAsync(server);
        Assert.Equal(1, key.Version);
        Assert.True(key.Bootstrap);
        Assert.NotNull(key.ProvisionedAt);
        Assert.Null(key.VerifiedAt);

        await ConfigureAsync(server);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        Assert.False(preview.Secrets.ContainsKey(SecretNames.AnsibleSshKey));
        Assert.DoesNotContain(PrivateMarker, preview.InputJson);

        var playbook = await agent.NextAsync("run");
        Assert.Contains(PrivateMarker, playbook.Secrets[SecretNames.AnsibleSshKey]);
        Assert.DoesNotContain(PrivateMarker, playbook.InputJson);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var verified = await KeyAsync(server);
        Assert.Equal(run.Id, verified.VerifiedRunId);

        var resolution = await WithDbAsync(server, db => db.SecretResolutions.AsNoTracking()
            .SingleAsync(r => r.Key == GuestAccessCredentials.Key(run.ResourceId)));
        Assert.Equal(1, resolution.Version);
    }

    [Fact]
    public async Task A_plan_carries_the_public_key_with_the_guests_own_and_no_secret()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var http = server.CreateClient();
        using var _ = http;
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);

        var plan = await agent.NextAsync("preview");
        Assert.DoesNotContain(PrivateMarker, plan.InputJson);
        Assert.DoesNotContain(plan.Secrets.Values, v => v.Contains(PrivateMarker));
        var keys = JsonNode.Parse(plan.InputJson)!["variables"]!["guest"]!["sshAuthorizedKeys"]!.AsArray();
        var key = await KeyAsync(server);
        Assert.Contains(keys, k => k!.GetValue<string>() == key.PublicKey);
    }

    [Fact]
    public async Task The_guest_page_shows_the_public_half_and_no_response_has_the_private_half()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ConfiguredAsync(server, agent);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var key = await KeyAsync(server);

        var page = await http.GetStringAsync($"/api/guests/{run.ResourceId}");
        Assert.Equal(key.PublicKey, JsonNode.Parse(page)!["managedAccess"]!["publicKey"]!.GetValue<string>());
        Assert.Equal(key.Fingerprint, JsonNode.Parse(page)!["managedAccess"]!["fingerprint"]!.GetValue<string>());
        foreach (var path in new[] { $"/api/guests/{run.ResourceId}", $"/api/runs/{run.Id}", "/api/variables" })
        {
            Assert.DoesNotContain(PrivateMarker, await http.GetStringAsync(path));
        }

        var attempt = run.Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Single();
        Assert.True(await WithDbAsync(server, db => db.SecretResolutions.AnyAsync(r => r.AttemptId == attempt.Id)));
    }

    [Fact]
    public async Task A_rebuild_keeps_the_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var before = await KeyAsync(server);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(run.ResourceId, CancellationToken.None);
        var plan = await agent.NextAsync("preview");
        var keys = JsonNode.Parse(plan.InputJson)!["variables"]!["guest"]!["sshAuthorizedKeys"]!.AsArray();
        Assert.Contains(keys, k => k!.GetValue<string>() == before.PublicKey);
        Assert.Equal(before.PublicKey, (await KeyAsync(server)).PublicKey);
    }

    [Fact]
    public async Task A_playbook_that_ran_nothing_on_the_guest_proves_nothing_about_the_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ConfigureAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, new JsonObject
        {
            ["exitCode"] = 0,
            ["tasks"] = 0,
            ["recap"] = new JsonObject { ["ok"] = 0, ["changed"] = 0 },
        });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        Assert.Null((await KeyAsync(server)).VerifiedAt);
    }

    [Fact]
    public async Task A_declared_key_makes_no_key_of_octopushys_own()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var playbook = await ConfiguredAsync(server, agent);
        Assert.DoesNotContain(PrivateMarker, playbook.InputJson);
        Assert.Equal(0, await WithDbAsync(server, db => db.GuestAccessKeys.CountAsync()));
    }

    [Fact]
    public async Task A_run_cannot_bind_another_guests_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ConfigureAsync(server);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);

        await WithDbAsync(server, async db =>
        {
            var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = RunContext.Parse(stored.Context);
            context.Secrets[SecretNames.AnsibleSshKey] = GuestAccessCredentials.Key(Guid.NewGuid());
            context.SecretBindingsPinned = false;
            stored.Context = context.Serialize();
            var problems = await CredentialBindings.CheckAsync(db, stored, context, CancellationToken.None);
            Assert.Contains(problems, p => p.Contains("reserved system scope"));
            return 0;
        });
    }

    [Fact]
    public async Task Retiring_the_guest_removes_the_key_and_leaves_the_record_of_its_use()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ConfiguredAsync(server, agent);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var entry = GuestAccessCredentials.Key(run.ResourceId);

        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Remove the guest");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded, new JsonObject { ["present"] = false });
        await server.TickAsync();

        Assert.Equal(0, await WithDbAsync(server, db => db.GuestAccessKeys.CountAsync()));
        Assert.Equal(0, await WithDbAsync(server, db => db.StoreEntries.CountAsync(e => e.Key == entry)));
        Assert.Equal(0, await WithDbAsync(server, db => db.StoreVersions.CountAsync(v => v.Key == entry)));
        Assert.True(await WithDbAsync(server, db => db.SecretResolutions.AnyAsync(r => r.Key == entry)));
    }
}
