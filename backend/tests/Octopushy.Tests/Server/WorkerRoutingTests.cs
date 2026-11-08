using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

/// <summary>
/// Automatic routing by reachability: agents report what they reach when the controller asks, and the controller sends a
/// step only to an agent with fresh evidence of every connection the step makes. See docs/worker-routing.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkerRoutingTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    private static ProxmoxContext Proxmox(bool verifyTls = false) => new("https://10.1.0.10:8006/", verifyTls, null, "korriban", 310, "krb-app-01", "10.1.0.31", "qemu");

    private static OpenTofuContext Tofu(Dictionary<string, string>? env = null) =>
        new("home.korriban.krb-app-01", "modules/opentofu/test-vm", "ghcr.io/opentofu/opentofu:1.13.1", [], env ?? [], new() { ["PROXMOX_VE_API_TOKEN"] = SecretNames.ProxmoxToken });

    private static AnsibleContext Ansible(string address = "10.1.0.31", int port = 22) =>
        new("app@1", "setup", "modules/ansible/app", "site.yml", null, false, false, null, "docker.io/skorcius/octopushy:ansible", "krb-app-01",
            "labs/home/proxmox/korriban/guests/krb-app-01", address, port, "ops", [], [], [], [], null);

    private static RunContext Context(OpenTofuContext? tofu = null, AnsibleContext? ansible = null, SnippetsContext? snippets = null,
        IReadOnlyList<StageContext>? stages = null, Dictionary<string, string>? secrets = null) => new()
    {
        Address = Guest,
        Proxmox = Proxmox(),
        OpenTofu = tofu,
        Ansible = ansible,
        Snippets = snippets,
        Stages = stages,
        Secrets = secrets ?? new() { [SecretNames.ProxmoxReadOnlyToken] = "proxmox/korriban/read-only-token", [SecretNames.ProxmoxPrivilegedToken] = "proxmox/korriban/privileged-token" },
    };

    /// <summary>Answers every probe sent to <paramref name="agent"/> with what <paramref name="status"/> says of it, or leaves it unanswered for null.</summary>
    private static async Task AnswerAsync(FakeAgent agent, Func<ReachabilityProbe, string?> status, byte[]? certificate = null)
    {
        foreach (var probe in agent.TakeProbes())
        {
            if (status(probe) is not { } answer)
                continue;
            Assert.True(await agent.AnswerAsync(probe, answer, answer == WorkerReachability.Reachable ? "" : "connecting timed out",
                answer == WorkerReachability.Reachable && probe.TlsTrust == "collect" ? [certificate ?? Presented] : []));
        }
    }

    private static readonly byte[] Presented = FakeAgent.Certificate();

    private static Task<Attempt> PlanAttemptAsync(ServerHarness server) => server.QueryAsync(async db =>
    {
        var resource = await db.Resources.AsNoTracking().FirstAsync(r => r.Address == Guest);
        return await db.Attempts.AsNoTracking().Where(a => a.Step!.Run!.ResourceId == resource.Id && a.Step.Name == StepNames.Plan)
            .OrderByDescending(a => a.CreatedAt).FirstAsync();
    });

    [Fact]
    public async Task Requirements_follow_what_each_step_runs_and_is_released()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var snippets = new SnippetsContext("home/korriban", "octopushy-snippets", "10.1.0.11", 2222);
        await server.WithDbAsync(async db =>
        {
            async Task<NetworkNeeds> NeedsAsync(string step, RunContext context) => await NetworkRequirements.ForAsync(db, step, context, CancellationToken.None);
            string Describe(NetworkNeeds needs) => string.Join(" + ", needs.Targets.Select(t => $"{t.Kind}:{t.Endpoint}:{t.Context}"));

            var provision = Context(Tofu(), snippets: snippets);
            // The plan writes the snippet but does not upload it.
            Assert.Equal("proxmox-api:https://10.1.0.10:8006:tool", Describe(await NeedsAsync(StepNames.Plan, provision)));
            // The apply uploads it, from the tool, to the snippet host's own address and port.
            Assert.Equal("proxmox-api:https://10.1.0.10:8006:tool + snippets-ssh:ssh://10.1.0.11:2222:tool", Describe(await NeedsAsync(StepNames.Apply, provision)));
            // An apply without uploads, as a retire, needs the API alone.
            Assert.Equal("proxmox-api:https://10.1.0.10:8006:tool", Describe(await NeedsAsync(StepNames.Apply, Context(Tofu()))));
            Assert.Equal("ghcr.io/opentofu/opentofu:1.13.1", (await NeedsAsync(StepNames.Apply, provision)).Targets[1].Image);

            // Every proxmox executor action, including a container's host-key read, is the agent process's own API call.
            foreach (var step in new[] { StepNames.Observe, StepNames.Verify, StepNames.VerifyAbsent, StepNames.Unprotect, StepNames.Reprotect })
                Assert.Equal("proxmox-api:https://10.1.0.10:8006:agent", Describe(await NeedsAsync(step, Context(Tofu(), snippets: snippets) with { ContainerHostKey = true })));
            var insecure = (await NeedsAsync(StepNames.Observe, Context())).Targets.Single();
            Assert.Equal(("tls", "insecure", "10.1.0.10"), (insecure.Protocol, insecure.Trust, insecure.ServerName));
            Assert.Equal("system", (await NeedsAsync(StepNames.Observe, Context() with { Proxmox = Proxmox(verifyTls: true) })).Targets.Single().Trust);

            // A configure preview contacts nothing; the playbook reaches the guest over SSH, from the tool.
            var configure = Context(ansible: Ansible("guest.home.arpa", 2200));
            var preview = await NeedsAsync(StepNames.Preview, configure);
            Assert.True(preview.Modeled);
            Assert.Empty(preview.Targets);
            foreach (var step in new[] { StepNames.Playbook, StepNames.PlaybookApproved, StepNames.VerifyCurrent, StepNames.RemoveKey })
                Assert.Equal("guest-ssh:ssh://guest.home.arpa:2200:tool", Describe(await NeedsAsync(step, configure)));

            // A pipeline: an OpenTofu stage is not modeled, whatever its context holds; an Ansible stage reaches its guest.
            var pipeline = Context(stages: [new StageContext("infra", "web@1", Tofu(), null), new StageContext("app", "web@1", null, Ansible("10.1.0.40"))]);
            var infra = await NeedsAsync(StepNames.Apply, pipeline.ForStage("infra"));
            Assert.False(infra.Modeled);
            Assert.Contains("not modeled", infra.Coverage);
            Assert.Equal("guest-ssh:ssh://10.1.0.40:22:tool", Describe(await NeedsAsync(StepNames.Playbook, pipeline.ForStage("app"))));
            Assert.Empty((await NeedsAsync(StepNames.Preview, pipeline.ForStage("app"))).Targets);

            // A proxy in the step's own environment is not probed through: unsupported, known without asking.
            var proxied = await NeedsAsync(StepNames.Plan, Context(Tofu(new() { ["HTTPS_PROXY"] = "http://proxy.lan:3128" })));
            Assert.Contains("HTTPS_PROXY", proxied.Targets.Single().Unsupported);
            Assert.DoesNotContain("proxy.lan", proxied.Targets.Single().Unsupported);
        });
    }

    [Fact]
    public async Task Only_the_agent_that_reaches_the_installation_gets_its_work_and_an_idle_one_that_cannot_waits()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var local = await server.ConnectProbedAgentAsync("A", "local", "home");
        var remote = await server.ConnectProbedAgentAsync("B", "podman", "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        // Asked, never waited for: the plan is queued with why, and both agents have probes.
        var waiting = await PlanAttemptAsync(server);
        Assert.Equal(AttemptStatuses.Queued, waiting.Status);
        Assert.Contains("A: Proxmox API https://10.1.0.10:8006 from the tool runtime: probing", waiting.WaitingReason);
        Assert.Contains("B: Proxmox API https://10.1.0.10:8006 from the tool runtime: probing", waiting.WaitingReason);
        var probes = remote.TakeProbes();
        Assert.Contains(probes, p => p is { Context: "tool", Protocol: "tls", Host: "10.1.0.10", Port: 8006, TlsTrust: "collect", Image: "ghcr.io/opentofu/opentofu:1.13.1" });
        Assert.Contains(probes, p => p is { Context: "agent", Image: "" });
        // A container probe may pull the step's image first; the agent process's own needs no such time.
        Assert.Equal((uint)WorkerReachability.ContainerTimeoutSeconds, probes.First(p => p.Context == "tool").TimeoutSeconds);
        Assert.Equal((uint)WorkerReachability.AgentTimeoutSeconds, probes.First(p => p.Context == "agent").TimeoutSeconds);
        foreach (var probe in probes)
            Assert.True(await remote.AnswerAsync(probe, WorkerReachability.Reachable, "", Presented));
        await AnswerAsync(local, _ => "timeout");

        var plan = await remote.NextAsync("preview");
        Assert.Equal("opentofu", plan.Executor);
        Assert.Empty(local.TakeAssignments());
        var assigned = await PlanAttemptAsync(server);
        Assert.Equal(remote.Id, assigned.AgentId);
        Assert.Null(assigned.WaitingReason);
    }

    [Fact]
    public async Task With_the_only_reaching_agent_gone_work_waits_and_is_probed_again_without_flooding()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var local = await server.ConnectProbedAgentAsync("A", "local", "home");
        await server.SyncAndTrustAsync();
        for (var i = 0; i < 3; i++)
            await server.TickAsync();
        // One probe per target however often dispatch looks: the observation's (agent process) and the plan's (tool).
        var first = local.TakeProbes();
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(p => p.Context).Distinct().Count());

        var signal = server.Services.GetRequiredService<OrchestratorSignal>();
        await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        foreach (var probe in first)
            Assert.True(await local.AnswerAsync(probe, "timeout", "connecting to 10.1.0.10:8006 timed out"));
        var woken = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(woken.Elapsed < TimeSpan.FromSeconds(5), "an answer wakes dispatch");

        for (var i = 0; i < 3; i++)
            await server.TickAsync();
        Assert.Empty(local.TakeAssignments());
        Assert.Empty(local.TakeProbes());
        var attempt = await PlanAttemptAsync(server);
        Assert.Equal(AttemptStatuses.Queued, attempt.Status);
        Assert.Contains("A: Proxmox API https://10.1.0.10:8006 from the tool runtime: timeout 0s ago (connecting to 10.1.0.10:8006 timed out); next probe in 30s", attempt.WaitingReason);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.NotEqual(RunStatuses.Blocked, run.Status);

        // Negative evidence backs off, doubling, and is never permanent.
        clock.Now += TimeSpan.FromSeconds(31);
        await server.TickAsync();
        var again = local.TakeProbes();
        Assert.Equal(2, again.Count);
        foreach (var probe in again)
            await local.AnswerAsync(probe, "refused");
        clock.Now += TimeSpan.FromSeconds(31);
        await server.TickAsync();
        Assert.Empty(local.TakeProbes());
        clock.Now += TimeSpan.FromSeconds(30);
        await server.TickAsync();
        var unanswered = local.TakeProbes();
        Assert.Equal(2, unanswered.Count);

        // An agent that never answers stops counting as asked once the answer is overdue, and an answer that comes
        // after that is not taken.
        clock.Now += TimeSpan.FromSeconds(WorkerReachability.AgentTimeoutSeconds + 31);
        Assert.False(await local.AnswerAsync(unanswered[0], WorkerReachability.Reachable, "", Presented));
        await server.TickAsync();
        Assert.Contains("no-answer", (await PlanAttemptAsync(server)).WaitingReason);
    }

    [Fact]
    public async Task Positive_evidence_is_refreshed_before_it_expires_and_does_not_count_after()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectProbedAgentAsync("A", "local", "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await AnswerAsync(agent, _ => WorkerReachability.Reachable);
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());

        // Six minutes on, the apply goes on the evidence it has while a refresh is asked for.
        clock.Now += TimeSpan.FromMinutes(6);
        await server.TickAsync();
        var refresh = agent.TakeProbes();
        Assert.Contains(refresh, p => p.Context == "tool");
        var apply = Assert.Single(agent.TakeAssignments());
        Assert.Equal("apply", apply.Action);
        await agent.ReportAsync(apply, Outcome.Succeeded);

        // Past expiry with the refresh unanswered, nothing counts: the verify waits.
        clock.Now += TimeSpan.FromMinutes(5);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        var verify = (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Verify).Attempts.Single();
        Assert.Equal(AttemptStatuses.Queued, verify.Status);
        Assert.Contains("from the agent process", verify.WaitingReason);
    }

    [Fact]
    public async Task An_agent_without_reachability_reports_waits_for_network_work_and_takes_work_that_connects_to_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, current, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        // Only an older agent remains.
        server.Services.GetRequiredService<AgentConnections>().Disconnect(current.Id);
        var legacy = await server.ConnectLegacyAgentAsync("old", "home");
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

        // The preview contacts nothing, so the older agent still takes it, unchecked.
        await ConfigureRunTests.PreviewAsync(legacy);
        Assert.Empty(legacy.TakeProbes());
        // The playbook reaches the guest: it waits for an agent that reports, and is never sent blind.
        for (var i = 0; i < 3; i++)
            await server.TickAsync();
        Assert.Empty(legacy.TakeAssignments());
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var playbook = run.Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Single();
        Assert.Equal(AttemptStatuses.Queued, playbook.Status);
        Assert.Contains("guest SSH ssh://10.1.0.31:22 from the tool runtime", playbook.WaitingReason);
        Assert.Contains($"old: {WorkerReachability.UpgradeNeeded}", playbook.WaitingReason);

        var updated = await server.ConnectAgentAsync("home");
        Assert.Equal("run", (await updated.NextAsync("run")).Action);
    }

    [Fact]
    public async Task The_planning_agent_cannot_upload_snippets_so_another_eligible_agent_applies()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        await server.SetSecretAsync("access/app/ssh-key", ConfigureRunTests.SshKey);
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        await server.SetPlainAsync("proxmox/korriban/ssh-host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ root@korriban");
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var planner = await server.ConnectProbedAgentAsync("A", "local", "home");
        var other = await server.ConnectProbedAgentAsync("B", "local", "home");
        var unlabelled = await server.ConnectProbedAgentAsync("C", "local");
        await server.SyncAndTrustAsync(http);
        var installation = await server.ResourceAsync("home/korriban");
        (await http.PostAsync($"/api/installations/{installation.Id}/snippets-key", null)).EnsureSuccessStatusCode();
        await server.TickAsync();

        // A reaches the API but not the snippet host; B has not answered yet, so A plans.
        await AnswerAsync(planner, p => p.Protocol == "ssh" ? "refused" : WorkerReachability.Reachable);
        var plan = await planner.NextAsync("preview");
        await planner.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await AnswerAsync(planner, p => p.Protocol == "ssh" ? "refused" : WorkerReachability.Reachable);
        await server.TickAsync();
        Assert.Empty(planner.TakeAssignments());
        var apply = (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Apply).Attempts.Single();
        Assert.Equal(planner.Id, apply.PreferredAgentId);
        Assert.Equal(AttemptStatuses.Queued, apply.Status);
        Assert.True(apply.WaitingReason?.Contains("snippet upload SSH ssh://10.1.0.10:22 from the tool runtime: refused") == true, apply.WaitingReason);

        // B reaches both; C reaches everything but lacks the labels, which still decide.
        await AnswerAsync(other, _ => WorkerReachability.Reachable);
        await AnswerAsync(unlabelled, _ => WorkerReachability.Reachable);
        Assert.Equal("apply", (await other.NextAsync("apply")).Action);
        Assert.DoesNotContain(planner.TakeAssignments(), a => a.Action == "apply");
        Assert.Empty(unlabelled.TakeAssignments());
        Assert.Empty(unlabelled.TakeProbes());
    }

    [Fact]
    public async Task A_new_session_starts_with_no_evidence_and_answers_count_only_for_its_own_outstanding_probes()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectProbedAgentAsync("A", "local", "home");
        var other = await server.ConnectProbedAgentAsync("B", "local", "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var probes = agent.TakeProbes();
        var probe = probes.First(p => p.Context == "tool");
        var reachability = server.Services.GetRequiredService<WorkerReachability>();

        // Unsolicited, and another session's probe: dropped.
        Assert.False(await agent.AnswerAsync(new ReachabilityProbe { ProbeId = "not-sent" }, WorkerReachability.Reachable));
        Assert.False(await other.AnswerAsync(probe, WorkerReachability.Reachable, "", Presented));
        // Oversized: taken as an error, not as evidence.
        var oversized = new ReachabilityReport { ProbeId = probes.First(p => p.Context == "agent").ProbeId, Status = WorkerReachability.Reachable };
        oversized.Certificates.AddRange(Enumerable.Range(0, WorkerReachability.MaxCertificates + 1).Select(_ => Google.Protobuf.ByteString.CopyFrom(Presented)));
        Assert.True(await reachability.AcceptAsync(agent.Session, oversized, CancellationToken.None));
        Assert.Equal("error", reachability.Snapshot(agent.Session).Single(e => e.Context == "agent").State);
        // Answered, then replayed: the second is dropped.
        Assert.True(await agent.AnswerAsync(probe, WorkerReachability.Reachable, "", Presented));
        Assert.False(await agent.AnswerAsync(probe, "refused"));
        Assert.True(reachability.Snapshot(agent.Session).Single(e => e.Context == "tool").Usable);

        // The agent reconnects: its new session knows nothing, and an answer to the old session's probe is not taken.
        var reconnected = new AgentSession(agent.Id, "A", ["opentofu", "proxmox", "ansible"], [TofuStateStore.AgentCapability, WorkerReachability.Capability], "local");
        server.Services.GetRequiredService<AgentConnections>().Add(reconnected);
        Assert.Empty(reachability.Snapshot(reconnected));
        var stale = new ReachabilityReport { ProbeId = probe.ProbeId, Status = WorkerReachability.Reachable };
        Assert.False(await reachability.AcceptAsync(reconnected, stale, CancellationToken.None));
        await server.TickAsync();
        var attempt = await PlanAttemptAsync(server);
        Assert.Equal(AttemptStatuses.Queued, attempt.Status);
        Assert.Contains("A: Proxmox API https://10.1.0.10:8006 from the tool runtime: probing", attempt.WaitingReason);
    }

    [Fact]
    public async Task Work_goes_only_through_the_connection_whose_reachability_was_checked()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectProbedAgentAsync("A", "local", "home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        string[] executors = ["opentofu", "proxmox", "ansible"];
        string[] capabilities = [TofuStateStore.AgentCapability, WorkerReachability.Capability];
        AgentSession? replacement = null;
        // The agent reconnects while dispatch checks its old connection, which reaches everything.
        var checkedOn = new AgentSession(agent.Id, "A", executors, capabilities, "local")
        {
            Responder = probe =>
            {
                if (replacement is null)
                {
                    replacement = new AgentSession(agent.Id, "A", executors, capabilities, "local");
                    connections.Add(replacement);
                }

                return FakeAgent.ReachEverything(probe);
            },
        };
        connections.Add(checkedOn);
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        // The new connection has no evidence, so nothing checked on the old one is handed to it.
        Assert.NotNull(replacement);
        Assert.Empty(new FakeAgent(server, agent.Id, replacement).TakeAssignments());
    }

    [Fact]
    public async Task Untrusted_definitions_send_no_probes()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectProbedAgentAsync("A", "local", "home");
        await server.SyncAsync();
        for (var i = 0; i < 3; i++)
            await server.TickAsync();
        Assert.Empty(agent.TakeProbes());
        await server.Services.GetRequiredService<WorkerReachability>().RefreshAsync(CancellationToken.None);
        Assert.Empty(agent.TakeProbes());
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Probes_never_take_the_room_assignments_need()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectProbedAgentAsync("A", "local", "home");
        await server.SyncAndTrustAsync();
        // A busy channel: probes wait, well before it is full.
        for (var i = 0; i < WorkerReachability.MaxQueued; i++)
            Assert.True(agent.Session.TrySend(new ServerMessage()));
        await server.TickAsync();
        Assert.Contains("waiting to probe", (await PlanAttemptAsync(server)).WaitingReason);
        Assert.Empty(agent.TakeProbes());

        // With room again, they go; with evidence, the assignment goes even while the channel is busier than probes may be.
        await server.TickAsync();
        await AnswerAsync(agent, _ => WorkerReachability.Reachable);
        for (var i = 0; i < WorkerReachability.MaxQueued + 10; i++)
            Assert.True(agent.Session.TrySend(new ServerMessage()));
        await server.TickAsync();
        Assert.Contains(agent.TakeAssignments(), a => a.Action == "preview");
    }

    [Fact]
    public async Task Agents_that_see_different_certificates_at_an_unverified_endpoint_get_none_of_its_work()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var a = await server.ConnectProbedAgentAsync("A", "local", "home");
        var b = await server.ConnectProbedAgentAsync("B", "local", "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        await AnswerAsync(a, _ => WorkerReachability.Reachable, FakeAgent.Certificate("pve-one"));
        await AnswerAsync(b, _ => WorkerReachability.Reachable, FakeAgent.Certificate("pve-two"));
        await server.TickAsync();
        Assert.Empty(a.TakeAssignments());
        Assert.Empty(b.TakeAssignments());
        var attempt = await PlanAttemptAsync(server);
        Assert.Contains("agents see different certificates there", attempt.WaitingReason);

        // B leaves: what remains agrees with itself.
        server.Services.GetRequiredService<AgentConnections>().Disconnect(b.Id);
        await server.TickAsync();
        Assert.Contains(a.TakeAssignments(), x => x.Action == "preview");
    }

    [Fact]
    public async Task The_agents_api_shows_what_each_agent_reaches_and_why_not()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectProbedAgentAsync("A", "podman", "home");
        await server.ConnectLegacyAgentAsync("old", "home");
        using var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(client);
        await server.TickAsync();
        await AnswerAsync(agent, p => p.Context == "agent" ? WorkerReachability.Reachable : "unsupported");
        var agents = (await client.GetFromJsonAsync<JsonArray>("/api/agents"))!;
        var shown = agents.Single(x => x!["name"]!.GetValue<string>() == "A")!;
        Assert.True(shown["reportsReachability"]!.GetValue<bool>());
        var entries = shown["reachability"]!.AsArray();
        var api = entries.Single(e => e!["context"]!.GetValue<string>() == "agent")!;
        Assert.Equal(("proxmox-api", "https://10.1.0.10:8006", "reachable", true), (api["kind"]!.GetValue<string>(), api["endpoint"]!.GetValue<string>(),
            api["state"]!.GetValue<string>(), api["usable"]!.GetValue<bool>()));
        Assert.NotNull(api["checkedAt"]);
        Assert.NotNull(api["leafSha256"]);
        var tool = entries.Single(e => e!["context"]!.GetValue<string>() == "tool")!;
        Assert.Equal(("unsupported", "ghcr.io/opentofu/opentofu:1.13.1"), (tool["state"]!.GetValue<string>(), tool["image"]!.GetValue<string>()));
        var old = agents.Single(x => x!["name"]!.GetValue<string>() == "old")!;
        Assert.False(old["reportsReachability"]!.GetValue<bool>());
        Assert.Empty(old["reachability"]!.AsArray());

        // A run's steps say what their agents must be shown to reach; an approval connects nowhere and says nothing.
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var steps = detail["steps"]!.AsArray();
        Assert.Equal("Needs fresh reachability of Proxmox API https://10.1.0.10:8006 from the tool runtime.",
            steps.Single(x => x!["name"]!.GetValue<string>() == "plan")!["networkAccess"]!.GetValue<string>());
        Assert.Null(steps.Single(x => x!["name"]!.GetValue<string>() == "approval")!["networkAccess"]);
    }

    [Fact]
    public void A_presented_chain_is_judged_by_the_configured_ca_and_the_name_alone()
    {
        using var caKey = ECDsa.Create();
        var caRequest = new CertificateRequest("CN=Lab CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var caPem = ca.ExportCertificatePem();

        byte[] Leaf(string dns, string? ip = null, X509Certificate2? issuer = null)
        {
            using var key = ECDsa.Create();
            var request = new CertificateRequest($"CN={dns}", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(dns);
            if (ip is not null)
                names.AddIpAddress(System.Net.IPAddress.Parse(ip));
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var cert = request.Create(issuer ?? ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
            return cert.RawData;
        }

        var now = DateTimeOffset.UtcNow;
        Assert.Equal("reachable", WorkerReachability.JudgeChain(caPem, [Leaf("pve.lan")], "pve.lan", now).Status);
        Assert.Equal("reachable", WorkerReachability.JudgeChain(caPem, [Leaf("pve.lan", "10.1.0.10")], "10.1.0.10", now).Status);
        var wrongName = WorkerReachability.JudgeChain(caPem, [Leaf("other.lan")], "pve.lan", now);
        Assert.Equal(("tls-untrusted", "the certificate does not name pve.lan"), (wrongName.Status, wrongName.Detail));
        // CN alone does not name a host, as for the agent's client.
        using var cnOnlyKey = ECDsa.Create();
        using var cnOnly = new CertificateRequest("CN=pve.lan", cnOnlyKey, HashAlgorithmName.SHA256)
            .Create(ca, now.AddDays(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(8));
        Assert.Equal("tls-untrusted", WorkerReachability.JudgeChain(caPem, [cnOnly.RawData], "pve.lan", now).Status);
        // Another CA's certificate, or one past its time, is not trusted.
        Assert.Equal("tls-untrusted", WorkerReachability.JudgeChain(caPem, [FakeAgent.Certificate("pve.lan")], "pve.lan", now).Status);
        Assert.Equal("tls-untrusted", WorkerReachability.JudgeChain(caPem, [Leaf("pve.lan")], "pve.lan", now.AddDays(60)).Status);
        Assert.Equal("tls-untrusted", WorkerReachability.JudgeChain(caPem, [[1, 2, 3]], "pve.lan", now).Status);
    }
}
