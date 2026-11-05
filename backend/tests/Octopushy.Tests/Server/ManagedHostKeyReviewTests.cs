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
/// The cases an independent review of managed host keys found: a key a failed or lost apply may have put on a machine,
/// a renewal interrupted half way, a key in an apply's saved plan reaching its diagnostics, and the trust source of a
/// guest that is not a VM of an opted-in workflow. Only behaviour is asserted, so these read the same before the fix.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ManagedHostKeyReviewTests(PostgresFixture postgres)
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
    public async Task A_creating_apply_that_failed_spends_its_key_so_the_next_machine_gets_another()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        // The apply uploads the snippet and starts making the VM, then fails: that machine may hold the key now.
        var plan = await agent.NextAsync("preview");
        var first = SnippetKeyOf(plan);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "the VM did not come up");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        // The next plan replaces that machine. It must not hand the replacement the same key.
        await PlanAgainAsync(server);
        var stale = await agent.NextAsync("preview");
        Assert.Equal(first, SnippetKeyOf(stale));
        await agent.ReportAsync(stale, Outcome.Succeeded, Replaces("replace_because_tainted"), artifacts: FakeAgent.PlanArtifact("replace"));
        var replanned = await agent.NextAsync("preview");
        Assert.NotEqual(first, SnippetKeyOf(replanned));
        Assert.Equal(2, (await PublicKeysAsync(server)).Count);
    }

    [Fact]
    public async Task An_apply_whose_outcome_is_unknown_spends_its_key_too()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var plan = await agent.NextAsync("preview");
        var first = SnippetKeyOf(plan);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Uncertain, error: "the agent lost the apply");
        await server.TickAsync();
        var lost = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, lost.Status);
        await server.Engine.CloseUncertainAsync(lost.Id, "ops", CancellationToken.None);

        await PlanAgainAsync(server);
        var stale = await agent.NextAsync("preview");
        Assert.Equal(first, SnippetKeyOf(stale));
        await agent.ReportAsync(stale, Outcome.Succeeded, Replaces("replace_because_tainted"), artifacts: FakeAgent.PlanArtifact("replace"));
        var replanned = await agent.NextAsync("preview");
        Assert.NotEqual(first, SnippetKeyOf(replanned));
    }

    /// <summary>Fails the first save that adds a second host key version, after the database committed it, as a crash would.</summary>
    private sealed class CrashAfterRenewal : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public bool Crashed { get; private set; }

        private bool _renewing;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _renewing = Armed && !Crashed && eventData.Context!.ChangeTracker.Entries<GuestHostKey>().Any(e => e.State == EntityState.Added && e.Entity.Version == 2);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_renewing)
            {
                _renewing = false;
                Crashed = true;
                throw new InvalidOperationException("The server stopped right after this save.");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_renewal_interrupted_after_it_was_saved_never_leaves_the_old_plan_to_approve()
    {
        var crash = new CrashAfterRenewal();
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed(),
            services: services => services.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(crash)));
        var agent = await ReadyAsync(server);
        await ProvisionAsync(agent, server);
        var first = Assert.Single(await PublicKeysAsync(server));

        await PlanAgainAsync(server);
        var stale = await agent.NextAsync("preview");
        crash.Armed = true;
        try
        {
            await agent.ReportAsync(stale, Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, old key"));
        }
        catch (Exception)
        {
            // The crash, as the server would see it: whatever the save committed is all there is.
        }

        Assert.True(crash.Crashed);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        // Recovered, the run plans again with the new key: the old plan is never offered for approval.
        Assert.NotEqual(RunStatuses.AwaitingApproval, run.Status);
        Assert.NotEqual(StepStatuses.AwaitingApproval, run.Steps.Single(s => s.Name == StepNames.Approval).Status);
        var replanned = await agent.NextAsync("preview");
        var keys = await PublicKeysAsync(server);
        Assert.Equal(2, keys.Count);
        Assert.NotEqual(first, SnippetKeyOf(replanned));
        Assert.Equal(keys[1], SnippetKeyOf(replanned));
    }

    [Fact]
    public async Task An_apply_masks_the_guest_keys_its_saved_plan_and_state_hold()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var privateHalf = await PrivateHalfAsync(server, resource.Id, 1);
        var line = privateHalf.Split('\n')[1];

        // A plan that keeps the machine, and an apply whose error prints part of the key its state holds.
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update"));
        var apply = await agent.NextAsync("apply");
        // The agent holds it to mask, and the provider is not given it.
        Assert.Contains(privateHalf, apply.Secrets.Values);
        var secretEnv = JsonNode.Parse(apply.InputJson)!["secretEnv"]!.AsObject();
        Assert.DoesNotContain(secretEnv, p => apply.Secrets.TryGetValue(p.Value!.GetValue<string>(), out var value) && value == privateHalf);
        await agent.ReportAsync(apply, Outcome.Failed, error: $"Error: writing snippet: unexpected data {line}");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var detail = run.Steps.Single(s => s.Name == StepNames.Apply).Detail ?? "";
        Assert.Contains("unexpected data", detail);
        Assert.DoesNotContain(line, detail);
        Assert.DoesNotContain(line, run.Detail ?? "");
    }

    [Fact]
    public void Only_a_vm_whose_workflow_opts_in_gets_octopushys_host_key()
    {
        using var test = ManagedHostKeyTests.Managed();
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest(Guest)!;
        var json = guest.Json.DeepClone().AsObject();
        json["type"] = "container";
        var container = guest with { Json = json };
        // A container names no access.hostKey because Proxmox pins it some other way, not because its key is Octopushy's.
        Assert.False(ManagedHostKey.Wanted(repository, container, keyIssued: false));
        Assert.False(ManagedHostKey.Wanted(repository, container, keyIssued: true));
        Assert.False(ManagedHostKey.Offered(repository, container));
    }

    [Fact]
    public void A_run_that_logs_in_never_takes_a_missing_pin_for_octopushys_key()
    {
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var resource = new Resource { Id = Guid.NewGuid(), Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };

        // A workflow that does not opt in, on a host set up for snippets: no access.hostKey is no pin at all.
        using var plain = ManagedHostKeyTests.Managed(start: ServiceDefinitionTests.Repository());
        var workflow = Path.Combine(plain.Root, "workflows/app/workflow.yaml");
        File.WriteAllText(workflow, File.ReadAllText(workflow).Replace("    hostKey: managed\n", ""));
        var repository = LabRepositoryLoader.Load(plain.Root);
        var guest = repository.Guest(Guest)!;
        Assert.All(new Func<Run>[]
        {
            () => factory.Configure(repository, guest, resource, revision, "setup", "manual", null, null),
            () => factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(1, 2, [1])),
            () => factory.Pipeline(repository, repository.Service(ServiceDefinitionTests.Address)!, resource, resource, revision, "manual"),
        }, make => Assert.Throws<RunException>(make));

        // A guest that is not a VM, on a workflow that opts in: neither.
        using var opted = ManagedHostKeyTests.Managed();
        var optedIn = LabRepositoryLoader.Load(opted.Root);
        var vm = optedIn.Guest(Guest)!;
        var json = vm.Json.DeepClone().AsObject();
        json["type"] = "container";
        var container = vm with { Json = json };
        Assert.All(new Func<Run>[]
        {
            () => factory.Configure(optedIn, container, resource, revision, "setup", "manual", null, null),
            () => factory.RotateAccess(optedIn, container, resource, revision, new AccessRotationContext(1, 2, [1])),
        }, make => Assert.Throws<RunException>(make));
    }
}
