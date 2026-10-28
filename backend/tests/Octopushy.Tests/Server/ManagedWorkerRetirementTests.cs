using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ManagedWorkerRetirementTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task Removing_an_enrollment_does_not_leave_a_worker_looking_enrolled_or_retire_its_guest()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var control = await server.ConnectAgentAsync("home");
        var agentId = await ReadyAsync(server, http, control);
        var before = await WorkerAsync(http);

        (await http.DeleteAsync($"/api/agents/{agentId}")).EnsureSuccessStatusCode();

        var worker = await WorkerAsync(http);
        Assert.Equal("Enrollment removed", worker["status"]!.GetValue<string>());
        Assert.Equal(before["generation"]!.GetValue<string>(), worker["generation"]!.GetValue<string>());
        var guest = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{worker["resourceId"]}"))!;
        Assert.Null(guest["retiredAt"]);
        var agents = (await http.GetFromJsonAsync<JsonArray>("/api/agents"))!;
        Assert.DoesNotContain(agents, a => a!["id"]!.GetValue<string>() == agentId.ToString());
    }

    [Theory]
    [InlineData(AttemptStatuses.Assigned)]
    [InlineData(AttemptStatuses.Running)]
    [InlineData(AttemptStatuses.Uncertain)]
    public async Task Enrollment_removal_refuses_active_or_unresolved_mutating_work_even_with_a_stale_run_status(string status)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var control = await server.ConnectAgentAsync("home");
        var agentId = await ReadyAsync(server, http, control);
        var run = await server.LatestRunAsync(Guest);
        await server.WithDbAsync(async db =>
        {
            await db.Attempts.Where(a => a.RunId == run.Id && a.Action == "apply").ExecuteUpdateAsync(u =>
                u.SetProperty(a => a.AgentId, agentId).SetProperty(a => a.Status, status));
            if (status == AttemptStatuses.Uncertain)
                await db.Steps.Where(s => s.RunId == run.Id && s.Name == StepNames.Apply)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatuses.Uncertain));
        });
        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/agents/{agentId}")).StatusCode);
        var agents = (await http.GetFromJsonAsync<JsonArray>("/api/agents"))!;
        Assert.Contains(agents, a => a!["id"]!.GetValue<string>() == agentId.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_checks_absence_without_destroying_a_vm_and_preserves_identity_history(bool present)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var control = await server.ConnectAgentAsync("home");
        var agentId = await ReadyAsync(server, http, control);
        var worker = await WorkerAsync(http);
        var resourceId = worker["resourceId"]!.GetValue<string>();
        (await http.DeleteAsync($"/api/agents/{agentId}")).EnsureSuccessStatusCode();
        File.Delete(Path.Combine(repo.Root, Definitions.ManagedWorkerDefinitionTests.GuestPath));
        server.Commit("Remove the disposable worker definition");
        await server.SyncAndTrustAsync(http);

        var response = await http.PostAsJsonAsync($"/api/managed-worker-preparation/{resourceId}/retire",
            new { generation = worker["generation"]!.GetValue<string>(), confirmAlreadyRemoved = true });
        response.EnsureSuccessStatusCode();
        var started = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var repeated = await http.PostAsJsonAsync($"/api/managed-worker-preparation/{resourceId}/retire",
            new { generation = worker["generation"]!.GetValue<string>(), confirmAlreadyRemoved = true });
        repeated.EnsureSuccessStatusCode();
        Assert.Equal(started["runId"]!.GetValue<string>(), (await repeated.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>());
        var verification = await control.NextAsync("verify-absent");
        Assert.Equal("proxmox", verification.Executor);
        Assert.Single(verification.Secrets);
        Assert.Contains("proxmox.token", verification.Secrets.Keys);
        await control.ReportAsync(verification, present ? Outcome.Failed : Outcome.Succeeded,
            new JsonObject { ["present"] = present }, error: present ? "VM still exists" : null);
        await server.TickAsync();

        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{started["runId"]}"))!;
        Assert.Equal(present ? "failed" : "succeeded", detail["run"]!["status"]!.GetValue<string>());
        var step = Assert.Single(detail["steps"]!.AsArray())!;
        Assert.Equal("verify-absent", step["name"]!.GetValue<string>());
        Assert.False(Assert.Single(step["attempts"]!.AsArray())!["mutating"]!.GetValue<bool>());
        var guest = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resourceId}"))!;
        Assert.Equal(!present, guest["retiredAt"] is not null);
        var preparation = (await http.GetFromJsonAsync<JsonObject>("/api/managed-worker-preparation"))!;
        Assert.Equal(present, preparation["existingWorkers"]!.AsArray().Any(w => w!["address"]!.GetValue<string>() == Guest));
        Assert.True(await server.QueryAsync(db => db.ManagedWorkers.AnyAsync(w => w.ResourceId == Guid.Parse(resourceId))));
    }

    [Theory]
    [InlineData("definition-present", HttpStatusCode.Conflict)]
    [InlineData("enrollment-present", HttpStatusCode.Conflict)]
    [InlineData("untrusted", HttpStatusCode.Conflict)]
    [InlineData("source-changed", HttpStatusCode.Conflict)]
    [InlineData("endpoint-changed", HttpStatusCode.Conflict)]
    [InlineData("wrong-generation", HttpStatusCode.Conflict)]
    [InlineData("no-confirmation", HttpStatusCode.BadRequest)]
    [InlineData("unknown-resource", HttpStatusCode.NotFound)]
    [InlineData("uncertain-mutation", HttpStatusCode.Conflict)]
    public async Task Retirement_fails_closed_before_any_new_credential_release(string reason, HttpStatusCode expected)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var control = await server.ConnectAgentAsync("home");
        var agentId = await ReadyAsync(server, http, control);
        var worker = await WorkerAsync(http);
        if (reason != "enrollment-present")
            (await http.DeleteAsync($"/api/agents/{agentId}")).EnsureSuccessStatusCode();
        if (reason != "definition-present")
        {
            File.Delete(Path.Combine(repo.Root, Definitions.ManagedWorkerDefinitionTests.GuestPath));
            server.Commit("Remove worker before verifying retirement");
            if (reason == "untrusted") await server.SyncAsync();
            else await server.SyncAndTrustAsync(http);
        }
        if (reason == "source-changed")
            await server.WithDbAsync(db => db.RepositorySettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceId, Guid.NewGuid())));
        if (reason == "endpoint-changed")
        {
            const string file = "labs/home/proxmox/korriban/installation.yaml";
            var original = RunContext.Parse((await server.LatestRunAsync(Guest)).Context).Proxmox.Endpoint.TrimEnd('/');
            var before = File.ReadAllText(Path.Combine(repo.Root, file));
            var changed = before.Replace(original, "https://another-installation.invalid:8006");
            Assert.NotEqual(before, changed);
            repo.Write(file, changed);
            server.Commit("Change the installation endpoint");
            await server.SyncAndTrustAsync(http);
        }
        if (reason == "uncertain-mutation")
        {
            var id = Guid.Parse(worker["runId"]!.GetValue<string>());
            await server.WithDbAsync(async db =>
            {
                await db.Runs.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RunStatuses.Failed));
                await db.Steps.Where(s => s.RunId == id && s.Name == StepNames.Apply).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatuses.Uncertain));
                await db.Attempts.Where(a => a.RunId == id && a.Action == "apply").ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, AttemptStatuses.Uncertain));
            });
        }
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        var resourceId = reason == "unknown-resource" ? Guid.NewGuid().ToString() : worker["resourceId"]!.GetValue<string>();
        var generation = reason == "wrong-generation" ? Guid.NewGuid().ToString() : worker["generation"]!.GetValue<string>();
        var response = await http.PostAsJsonAsync($"/api/managed-worker-preparation/{resourceId}/retire",
            new { generation, confirmAlreadyRemoved = reason != "no-confirmation" });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        Assert.DoesNotContain(control.TakeAssignments(), a => a.Action == "verify-absent");
    }

    [Theory]
    [InlineData("restored-definition")]
    [InlineData("changed-head")]
    [InlineData("missing-evidence")]
    [InlineData("present-evidence")]
    public async Task A_stale_or_incomplete_result_never_marks_a_worker_retired(string reason)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var control = await server.ConnectAgentAsync("home");
        var agentId = await ReadyAsync(server, http, control);
        var worker = await WorkerAsync(http);
        var definition = File.ReadAllText(Path.Combine(repo.Root, Definitions.ManagedWorkerDefinitionTests.GuestPath));
        (await http.DeleteAsync($"/api/agents/{agentId}")).EnsureSuccessStatusCode();
        File.Delete(Path.Combine(repo.Root, Definitions.ManagedWorkerDefinitionTests.GuestPath));
        server.Commit("Remove disposable worker");
        await server.SyncAndTrustAsync(http);
        var response = await http.PostAsJsonAsync($"/api/managed-worker-preparation/{worker["resourceId"]}/retire",
            new { generation = worker["generation"]!.GetValue<string>(), confirmAlreadyRemoved = true });
        response.EnsureSuccessStatusCode();
        var started = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var verification = await control.NextAsync("verify-absent");
        if (reason is "restored-definition" or "changed-head")
        {
            if (reason == "restored-definition") repo.Write(Definitions.ManagedWorkerDefinitionTests.GuestPath, definition);
            else repo.Write("README.md", "A newer source head");
            server.Commit("Source changed during retirement verification");
            await server.SyncAndTrustAsync(http);
        }
        var outputs = reason == "missing-evidence" ? new JsonObject() : new JsonObject { ["present"] = reason == "present-evidence" };
        await control.ReportAsync(verification, Outcome.Succeeded, outputs);
        await server.TickAsync();
        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{started["runId"]}"))!;
        Assert.Equal(reason == "changed-head" ? "cancelled" : "failed", detail["run"]!["status"]!.GetValue<string>());
        var guest = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{worker["resourceId"]}"))!;
        Assert.Null(guest["retiredAt"]);
    }

    private static async Task<JsonNode> WorkerAsync(HttpClient http)
    {
        var preparation = (await http.GetFromJsonAsync<JsonObject>("/api/managed-worker-preparation"))!;
        return preparation["existingWorkers"]!.AsArray().Single(w => w!["address"]!.GetValue<string>() == Guest)!;
    }

    internal static async Task<Guid> ReadyAsync(ServerHarness server, HttpClient http, FakeAgent control)
    {
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(http);
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await control.ReportAsync(await control.NextAsync("apply"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(await control.NextAsync("prepare"), Outcome.Succeeded, new JsonObject
        {
            ["protocolVersion"] = 1,
            ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
            ["csrDer"] = Convert.ToBase64String(new CertificateRequest("CN=worker", key, HashAlgorithmName.SHA256).CreateSigningRequest()),
            ["agentVersion"] = "test",
        });
        var install = await control.NextAsync("install");
        var id = Guid.Parse(JsonNode.Parse(install.InputJson)!["identity"]!["agentId"]!.GetValue<string>());
        await control.ReportAsync(install, Outcome.Succeeded, new JsonObject
        {
            ["protocolVersion"] = 1,
            ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
            ["agentId"] = id.ToString(),
            ["installed"] = true,
        });
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Add(new AgentSession(id, "worker", ["opentofu", "proxmox"]));
        var hello = new Hello { AgentVersion = "test" };
        hello.Executors.AddRange(["opentofu", "proxmox"]);
        await server.Engine.OnHelloAsync(id, hello, CancellationToken.None);
        await server.TickAsync();
        Assert.Equal("Ready", (await WorkerAsync(http))["status"]!.GetValue<string>());
        return id;
    }
}
