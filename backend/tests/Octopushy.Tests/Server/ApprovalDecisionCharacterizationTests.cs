using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Agents;
using ConfigureRepository = Octopushy.Tests.Definitions.GuestConfigurationTests;
using PipelineRepository = Octopushy.Tests.Definitions.ServiceDefinitionTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// What the engine decides at an approval step, for each kind of run and each answer it can give: the run's and the step's
/// status and text, who decided and why, which digest an approval has to name, and what an approval naming another does.
/// Recorded from the code before the approval deciders were put behind one interface, with every case taken from a test
/// that already covers it; a change to a pinned text is a change of behaviour.
/// </summary>
internal static class ApprovalPins
{
    /// <summary>What a test knows the digests to be, so a pin can say which one the step names.</summary>
    public sealed class Digests : Dictionary<string, string>
    {
        public Digests() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public string Name(Step step)
        {
            if (step.PlanSha256 is null)
                return "none";
            if (TryGetValue(step.PlanSha256, out var name))
                return name;
            if (step.Document is not null && PipelineDocument.Digest(step.Document) == step.PlanSha256)
                return "the approval document";
            return "another digest";
        }
    }

    public static Task<Run> RunAsync(ServerHarness server, Guid id) =>
        server.QueryAsync(db => db.Runs.AsNoTracking().Include(r => r.Steps).SingleAsync(r => r.Id == id));

    /// <summary>The run and its approval step as a person and the next step would see them.</summary>
    public static string Pin(Run run, string step, Digests digests, string? stage = null)
    {
        var approval = run.Steps.Single(s => s.Name == step && s.Stage == stage);
        return string.Join("\n",
            $"run: {run.Status} | {Mask(run.Detail)}",
            $"step: {approval.Name} {approval.Status} | required {approval.ApprovalRequired} | started {approval.StartedAt is not null} | finished {approval.FinishedAt is not null}",
            $"detail: {Mask(approval.Detail)}",
            $"decision: {approval.Decision ?? "-"} by {approval.DecidedBy ?? "-"} | {Mask(approval.DecisionNote)}",
            $"digest: {digests.Name(approval)}");
    }

    /// <summary>A change id is new in every test.</summary>
    private static string Mask(string? text) =>
        text is null ? "-" : System.Text.RegularExpressions.Regex.Replace(text, "change [0-9a-f-]{36}", "change <id>");

    /// <summary>What approving with <paramref name="digest"/> does, and the run after it.</summary>
    public static async Task<string> ApproveAsync(ServerHarness server, Run run, string step, string digest, Digests digests, string? stage = null)
    {
        var approval = run.Steps.Single(s => s.Name == step && s.Stage == stage);
        string outcome;
        try
        {
            await server.Engine.ApproveAsync(run.Id, approval.Id, digest, "ops", "checked", CancellationToken.None);
            outcome = "approved";
        }
        catch (RunException e)
        {
            outcome = $"refused: {e.Message}";
        }

        return outcome + "\n" + Pin(await RunAsync(server, run.Id), step, digests, stage);
    }

    public static Task<ResourceChange> ChangeAsync(ServerHarness server, Guid id) =>
        server.QueryAsync(db => db.Changes.AsNoTracking().SingleAsync(c => c.Id == id));

    public static string Pin(ResourceChange change) =>
        $"pre-approval: {change.PreApproval ?? "-"} | {change.PreApprovalDetail ?? "-"}";

    /// <summary>A deploy recorded at the head that asked to approve its plan in the same click, as the wizard records one.</summary>
    public static Task<Guid> DeployedAsync(ServerHarness server, string address, string kind, string resourceKind) =>
        server.QueryAsync(async db =>
        {
            var head = await RepositoryTrust.HeadRevisions(db).OrderByDescending(r => r.Sequence).FirstAsync();
            var resource = await db.Resources.FirstAsync(r => r.Address == address);
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = kind, ResourceKind = resourceKind, ResourceId = resource.Id, Address = address,
                Author = "ops", BaseCommit = head.Commit, Message = "deploy", Commit = head.Commit, RevisionId = head.Id,
                Status = ChangeStatuses.Synced, PreApproval = PreApprovals.Requested, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return id;
        });

    public static void Check(string expected, string actual) => Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
}

/// <summary>Provision runs: an OpenTofu plan of the guest's machine, and what the policy and the safety boundary make of it.</summary>
public class ProvisionApprovalCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const int Vmid = 310;
    private static readonly string[] Replace = ["delete", "create"];

    private async Task<ServerHarness> StartAsync(string policy = "")
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return await ServerHarness.StartAsync(postgres, repository);
    }

    /// <summary>Plans the guest with <paramref name="plan"/>, and returns its provision run once the engine has advanced it.</summary>
    private static async Task<Run> PlannedAsync(ServerHarness server, FakeAgent agent, JsonObject plan, Artifact? artifact)
    {
        var assignment = await agent.NextAsync("preview");
        if (artifact is null)
            await agent.ReportAsync(assignment, Outcome.Succeeded, plan);
        else
            await agent.ReportAsync(assignment, Outcome.Succeeded, plan, artifacts: artifact);
        await server.TickAsync();
        return await server.LatestRunAsync(Guest, Operations.Provision);
    }

    private static async Task EstablishAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.NotNull((await server.ResourceAsync(Guest)).VerifiedAt);
    }

    private static async Task FailFirstApplyAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 2), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "bridge 'lab' does not exist");
        await server.TickAsync();
    }

    private static async Task PlanAgainAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
    }

    private static JsonObject VmPlan(string[] actions, string? reason = null, int? vmid = Vmid, string? node = "korriban")
    {
        var change = new JsonObject
        {
            ["address"] = $"{GuestVmPlan.VmResourceType}.guest",
            ["type"] = GuestVmPlan.VmResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        };
        if (reason is not null) change["actionReason"] = reason;
        if (vmid is not null) change["vmId"] = vmid;
        if (node is not null) change["node"] = node;
        return new JsonObject
        {
            ["hasChanges"] = true,
            ["changes"] = new JsonArray(change),
            ["counts"] = new JsonObject
            {
                ["create"] = actions is ["create"] ? 1 : 0,
                ["update"] = actions is ["update"] ? 1 : 0,
                ["delete"] = actions is ["delete"] ? 1 : 0,
                ["replace"] = actions.Length == 2 ? 1 : 0,
            },
            ["toolVersion"] = "1.13.1",
        };
    }

    private static ApprovalPins.Digests Named(Artifact artifact) => new() { [artifact.Sha256] = "the saved plan" };

    [Fact]
    public async Task A_plan_that_creates_is_approved_by_the_default_policy()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_plan_with_no_changes_skips_the_approval()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(), null);
        ApprovalPins.Check("""
            run: running | -
            step: approval skipped | required False | started False | finished False
            detail: The plan has no changes.
            decision: - by - | -
            digest: none
            """, ApprovalPins.Pin(run, StepNames.Approval, []));
    }

    [Fact]
    public async Task A_plan_with_no_changes_skips_the_approval_under_manual_approval()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(), null);
        ApprovalPins.Check("""
            run: running | -
            step: approval skipped | required False | started False | finished False
            detail: The plan has no changes.
            decision: - by - | -
            digest: none
            """, ApprovalPins.Pin(run, StepNames.Approval, []));
    }

    [Fact]
    public async Task Manual_approval_waits_and_takes_only_the_digest_of_the_plan_shown()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var digests = Named(artifact);
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(update: 1), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, new string('0', 64), digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, FakeAgent.PlanArtifact("an earlier plan").Sha256, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, "", digests));
        ApprovalPins.Check("""
            approved
            run: running | -
            step: approval succeeded | required True | started True | finished True
            detail: Approval is manual (set by home).
            decision: approved by ops | checked
            digest: the saved plan
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, artifact.Sha256.ToUpperInvariant(), digests));
        Assert.Equal("apply", (await agent.NextAsync("apply")).Action);
    }

    [Fact]
    public async Task An_output_only_plan_waits_under_manual_approval()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(hasChanges: true), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_plan_with_changes_and_no_saved_plan_file_fails()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(update: 1), null);
        ApprovalPins.Check("""
            run: failed | The plan has no saved plan file for privileged work. Inspect and replan with an agent that retains the plan.
            step: approval failed | required False | started False | finished False
            detail: The plan has no saved plan file for privileged work. Inspect and replan with an agent that retains the plan.
            decision: - by - | -
            digest: none
            """, ApprovalPins.Pin(run, StepNames.Approval, []));
    }

    [Fact]
    public async Task A_plan_that_deletes_waits_when_destructive_is_manual()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1, delete: 1), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | The plan deletes 1 resource(s) and destructive is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: The plan deletes 1 resource(s) and destructive is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_plan_that_deletes_and_waits_under_both_says_approval_is_manual()
    {
        await using var server = await StartAsync("policy: { approval: manual, destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1, delete: 1), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home).
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_plan_that_deletes_is_approved_when_destructive_follows_automatic_approval()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1, delete: 1), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval and destructive are automatic (approval set by default, destructive default, following approval).
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task Replacing_something_other_than_the_vm_follows_destructive()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(replace: 1), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("policy: { destructive: manual }")]
    public async Task Replacing_an_established_vm_waits_for_a_person(string policy)
    {
        await using var server = await StartAsync(policy);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("replace");
        var run = await PlannedAsync(server, agent, VmPlan(Replace, "replace_because_cannot_update"), artifact);
        var expected = policy == "" ? """
            run: awaiting-approval | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step: approval awaiting-approval | required True | started True | finished False
            detail: The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            decision: - by - | -
            digest: the saved plan
            """ : """
            run: awaiting-approval | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is manual, set by home).
            step: approval awaiting-approval | required True | started True | finished False
            detail: The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is manual, set by home).
            decision: - by - | -
            digest: the saved plan
            """;
        ApprovalPins.Check(expected, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task Replacing_an_established_vm_is_approved_when_destructive_is_set_automatic()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("replace");
        var run = await PlannedAsync(server, agent, VmPlan(Replace, "replace_because_cannot_update"), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval and destructive are automatic (approval set by default, destructive home). The plan destroys a VM and its disks.
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task Deleting_an_established_vm_waits_for_a_person()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("delete");
        var run = await PlannedAsync(server, agent, VmPlan(["delete"]), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step: approval awaiting-approval | required True | started True | finished False
            detail: The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task Replacing_a_vm_whose_first_apply_failed_is_approved_by_the_policy()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("replace");
        var run = await PlannedAsync(server, agent, VmPlan(Replace, "replace_because_tainted"), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default). The plan recreates VM 310, whose first apply did not finish; it was never verified.
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task Creating_again_a_vm_the_guest_was_bound_to_waits_whatever_the_policy()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("recreate");
        var run = await PlannedAsync(server, agent, VmPlan(["create"]), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | The VM 310 that was created and verified for this guest is gone from korriban: the plan creates a new, empty one. If it was deleted by mistake, restore it from a backup at VMID 310 and plan again. Approve only to start over with an empty machine; this waits for a person whatever the policy says.
            step: approval awaiting-approval | required True | started True | finished False
            detail: The VM 310 that was created and verified for this guest is gone from korriban: the plan creates a new, empty one. If it was deleted by mistake, restore it from a backup at VMID 310 and plan again. Approve only to start over with an empty machine; this waits for a person whatever the policy says.
            decision: - by - | -
            digest: the saved plan
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_plan_that_replaces_a_machine_the_guest_does_not_own_is_refused_before_any_approval()
    {
        await using var server = await StartAsync("policy: { approval: automatic, destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, VmPlan(Replace, "replace_because_tainted", 311), artifact);
        ApprovalPins.Check("""
            run: failed | The plan step failed: The plan would change a Proxmox resource that is not this guest's VM 310 on korriban (proxmox_virtual_environment_vm.guest replaces VMID 311 on node korriban). Octopushy refuses to act on a machine it does not manage, whatever the policy says.
            step: approval skipped | required False | started False | finished False
            detail: An earlier step did not succeed.
            decision: - by - | -
            digest: none
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)));
    }

    [Fact]
    public async Task A_deploy_that_only_creates_is_approved_at_deploy()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var change = await ApprovalPins.DeployedAsync(server, Guest, ChangeKinds.CreateGuest, ResourceKinds.Guest);
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required True | started True | finished True
            detail: -
            decision: approved by ops | Approved at deploy (change <id>): the plan only creates.
            digest: the saved plan
            pre-approval: approved | Run #1's plan was approved at deploy by ops: it only creates.
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }

    [Fact]
    public async Task A_deploy_whose_plan_does_more_than_create_waits_and_says_why()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var change = await ApprovalPins.DeployedAsync(server, Guest, ChangeKinds.CreateGuest, ResourceKinds.Guest);
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1, update: 1), artifact);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home). Not approved at deploy: Plan does more than create; approve it yourself. It has 1 to update.
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home). Not approved at deploy: Plan does more than create; approve it yourself. It has 1 to update.
            decision: - by - | -
            digest: the saved plan
            pre-approval: declined | Plan does more than create; approve it yourself. It has 1 to update.
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }

    [Fact]
    public async Task A_deploy_the_policy_approves_records_that_it_was_not_needed()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var change = await ApprovalPins.DeployedAsync(server, Guest, ChangeKinds.CreateGuest, ResourceKinds.Guest);
        var artifact = FakeAgent.PlanArtifact();
        var run = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), artifact);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved plan
            pre-approval: not-needed | Run #1 needed no approval: Approval is automatic (set by default).
            """, ApprovalPins.Pin(run, StepNames.Approval, Named(artifact)) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }
}

/// <summary>Configure runs: an Ansible preview, a declared outcome, and a rotation's two decisions.</summary>
public class ConfigureApprovalCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    private static async Task SetPolicyAsync(ServerHarness server, string policy)
    {
        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + policy);
        server.Commit("Set the policy");
        await server.SyncAsync();
        await server.TickAsync();
    }

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    /// <summary>Previews the configure run, with no tasks listed when <paramref name="tasks"/> is false, and returns it advanced.</summary>
    private static async Task<(Run Run, ApprovalPins.Digests Digests)> PreviewedAsync(ServerHarness server, FakeAgent agent, bool tasks = true)
    {
        var (outputs, artifact) = ConfigureRunTests.Preview();
        if (!tasks)
            outputs["tasks"] = new JsonArray();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, outputs, artifacts: artifact);
        await server.TickAsync();
        return (await server.LatestRunAsync(Guest, Operations.Configure), new() { [artifact.Sha256] = "the saved preview" });
    }

    private static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Http)> ProvisionedAsync(PostgresFixture postgres, TestRepository repository)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        return (server, agent, http);
    }

    [Fact]
    public async Task A_preview_is_approved_by_the_default_policy()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task Manual_approval_waits_and_takes_only_the_digest_of_the_preview_shown()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await SetPolicyAsync(server, "policy: { approval: manual }\n");
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01).
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01).
            decision: - by - | -
            digest: the saved preview
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, new string('0', 64), digests));
        ApprovalPins.Check("""
            approved
            run: running | -
            step: approval succeeded | required True | started True | finished True
            detail: Approval is manual (set by home/korriban/krb-app-01).
            decision: approved by ops | checked
            digest: the saved preview
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, run.Steps.Single(s => s.Name == StepNames.Approval).PlanSha256!, digests));
        Assert.Equal("run", (await agent.NextAsync("run")).Action);
    }

    [Fact]
    public async Task A_preview_that_lists_no_task_still_waits_under_manual_approval()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await SetPolicyAsync(server, "policy: { approval: manual }\n");
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent, tasks: false);
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01).
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_preview_that_lists_no_task_is_approved_by_the_default_policy()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent, tasks: false);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task An_action_declared_destructive_waits_under_a_manual_destructive_policy()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository("policy: { destructive: manual }", destructive: "true"));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent);
        ApprovalPins.Check("""
            run: awaiting-approval | The workflow declares setup destructive and destructive is manual (set by home/korriban/krb-app-01).
            step: approval awaiting-approval | required True | started True | finished False
            detail: The workflow declares setup destructive and destructive is manual (set by home/korriban/krb-app-01).
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task An_action_declared_destructive_is_approved_when_destructive_follows_automatic_approval()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository(destructive: "true"));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval and destructive are automatic (approval set by default, destructive default, following approval).
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task An_action_with_its_own_automatic_approval_is_approved_on_a_manual_guest()
    {
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await SetPolicyAsync(server, "policy:\n  approval: manual\n  actions:\n    setup: { approval: automatic }\n");
        await ScheduleAsync(server);
        var (run, digests) = await PreviewedAsync(server, agent);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by home/korriban/krb-app-01, action setup).
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_declared_outcome_waits_for_a_person_on_the_previews_digest()
    {
        const string outcomes = """
            outcomes:
              deferred:
                description: Work is active on the guest.
                approve:
                  vars: { allow_busy: true }
            """;
        var (server, agent, http) = await ProvisionedAsync(postgres, ConfigureRepository.Repository(extraWorkflow: outcomes));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (_, digests) = await PreviewedAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads."));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        ApprovalPins.Check("""
            run: awaiting-approval | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
            step: outcome-approval awaiting-approval | required True | started True | finished False
            detail: The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.OutcomeApproval, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
            step: outcome-approval awaiting-approval | required True | started True | finished False
            detail: The playbook reported deferred: Three threads.. Work is active on the guest. Approving runs setup once more with allow_busy.
            decision: - by - | -
            digest: the saved preview
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.OutcomeApproval, new string('0', 64), digests));
    }

    [Fact]
    public async Task A_rotation_waits_for_a_person_to_promote_and_then_to_remove_whatever_the_policy()
    {
        var repository = ConfigureRepository.Repository();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        var (server, agent, http) = await ProvisionedAsync(postgres, repository);
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        var resource = await server.ResourceAsync(Guest);

        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource.Id, CancellationToken.None);
        await server.TickAsync();
        var (outputs, artifact) = ConfigureRunTests.Preview();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, outputs, artifacts: artifact);
        ApprovalPins.Digests digests = new() { [artifact.Sha256] = "the saved preview" };
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.RotateAccess);
        ApprovalPins.Check("""
            run: awaiting-approval | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the saved preview
            run: awaiting-approval | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step: promote-key awaiting-approval | required True | started True | finished False
            detail: A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.Approval, digests) + "\n" + ApprovalPins.Pin(run, StepNames.PromoteKey, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            step: promote-key awaiting-approval | required True | started True | finished False
            detail: A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            decision: - by - | -
            digest: the saved preview
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.PromoteKey, new string('0', 64), digests));
        ApprovalPins.Check("""
            approved
            run: awaiting-approval | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step: promote-key succeeded | required True | started True | finished True
            detail: A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other.
            decision: approved by ops | checked
            digest: the saved preview
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.PromoteKey, run.Steps.Single(s => s.Name == StepNames.PromoteKey).PlanSha256!, digests));
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.RotateAccess);
        ApprovalPins.Check("""
            run: awaiting-approval | Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            step: approve-removal awaiting-approval | required True | started True | finished False
            detail: Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.
            decision: - by - | -
            digest: the saved preview
            """, ApprovalPins.Pin(run, StepNames.ApproveRemoval, digests));
    }
}

/// <summary>Pipeline runs: one approval of a document that covers every stage, OpenTofu and Ansible.</summary>
public class PipelineApprovalCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Service = PipelineRepository.Address;
    private const string PipelineFile = PipelineRepository.ServiceFile;

    private static Task ScheduleAsync(ServerHarness server) => server.Services.GetRequiredService<Planner>().SchedulePipelinesAsync(CancellationToken.None);

    private static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Http)> ReadyAsync(PostgresFixture postgres, TestRepository repository)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("web/password", "a-web-password");
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        return (server, agent, http);
    }

    private static TestRepository Policy(string policy, string service = PipelineRepository.Service)
    {
        var test = PipelineRepository.Repository();
        test.Write(PipelineFile, service.Replace("workflow:", $"{policy}\nworkflow:"));
        return test;
    }

    /// <summary>Plans the OpenTofu stage with <paramref name="plan"/> and previews the Ansible one, then advances the run.</summary>
    private static async Task<(Run Run, ApprovalPins.Digests Digests)> PlannedAsync(ServerHarness server, FakeAgent agent, JsonObject plan, bool ansible = true)
    {
        var artifact = FakeAgent.PlanArtifact();
        ApprovalPins.Digests digests = new() { [artifact.Sha256] = "the stage's saved plan" };
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: artifact);
        if (ansible)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("a preview");
            var preview = new Artifact { Name = AssignmentBuilder.PreviewArtifact, Content = Google.Protobuf.ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
            digests[preview.Sha256] = "the stage's saved preview";
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
                new JsonObject { ["action"] = "deploy", ["playbook"] = "deploy.yml", ["tasks"] = new JsonArray() }, artifacts: preview);
        }

        await server.TickAsync();
        return (await server.LatestRunAsync(Service, Operations.Pipeline), digests);
    }

    [Fact]
    public async Task A_pipeline_with_changes_is_approved_by_the_default_policy()
    {
        var (server, agent, http) = await ReadyAsync(postgres, PipelineRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1));
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_pipeline_whose_stages_change_nothing_needs_no_approval()
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { approval: manual }", PipelineRepository.TofuOnly));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(), ansible: false);
        ApprovalPins.Check("""
            run: succeeded | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | No stage has anything to change, so this needs no approval.
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task An_ansible_stage_with_a_plan_that_changes_nothing_still_waits_under_manual_approval()
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { approval: manual }"));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan());
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01/web).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01/web).
            decision: - by - | -
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task Manual_approval_waits_and_takes_only_the_digest_of_the_document()
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { approval: manual }"));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1));
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01/web).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01/web).
            decision: - by - | -
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
        ApprovalPins.Check("""
            refused: The plan changed since it was shown; review the current plan.
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01/web).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01/web).
            decision: - by - | -
            digest: the approval document
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, FakeAgent.PlanArtifact().Sha256, digests));
        ApprovalPins.Check("""
            approved
            run: running | -
            step: approval succeeded | required True | started True | finished True
            detail: Approval is manual (set by home/korriban/krb-app-01/web).
            decision: approved by ops | checked
            digest: the approval document
            """, await ApprovalPins.ApproveAsync(server, run, StepNames.Approval, run.Steps.Single(s => s.Name == StepNames.Approval).PlanSha256!, digests));
        Assert.Equal("apply", (await agent.NextAsync("apply")).Action);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task A_stage_plan_that_deletes_or_replaces_waits_under_destructive_manual(int delete, int replace)
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { destructive: manual }"));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(delete: delete, replace: replace));
        ApprovalPins.Check("""
            run: awaiting-approval | Destructive is manual (set by home/korriban/krb-app-01/web): expose (the plan deletes or replaces 1 resource(s)).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Destructive is manual (set by home/korriban/krb-app-01/web): expose (the plan deletes or replaces 1 resource(s)).
            decision: - by - | -
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_stage_plan_that_deletes_is_approved_when_destructive_follows_automatic_approval()
    {
        var (server, agent, http) = await ReadyAsync(postgres, PipelineRepository.Repository());
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1, delete: 1));
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval and destructive are automatic (approval set by default, destructive default, following approval).
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_stage_the_workflow_declares_destructive_waits_under_destructive_manual()
    {
        var test = Policy("policy: { destructive: manual }");
        test.Write("workflows/web/workflow.yaml", File.ReadAllText(Path.Combine(test.Root, "workflows/web/workflow.yaml")).Replace("destructive: false", "destructive: true"));
        var (server, agent, http) = await ReadyAsync(postgres, test);
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1));
        ApprovalPins.Check("""
            run: awaiting-approval | Destructive is manual (set by home/korriban/krb-app-01/web): deploy (the workflow declares deploy destructive).
            step: approval awaiting-approval | required True | started True | finished False
            detail: Destructive is manual (set by home/korriban/krb-app-01/web): deploy (the workflow declares deploy destructive).
            decision: - by - | -
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_pipeline_of_one_opentofu_stage_is_approved_by_the_default_policy()
    {
        var (server, agent, http) = await ReadyAsync(postgres, PipelineRepository.Repository(PipelineRepository.TofuOnly));
        await using var _ = server;
        using var __ = http;
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), ansible: false);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the approval document
            """, ApprovalPins.Pin(run, StepNames.Approval, digests));
    }

    [Fact]
    public async Task A_deployed_pipeline_of_opentofu_stages_that_only_create_is_approved_at_deploy()
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { approval: manual }", PipelineRepository.TofuOnly));
        await using var _ = server;
        using var __ = http;
        var change = await ApprovalPins.DeployedAsync(server, Service, ChangeKinds.CreateService, ResourceKinds.Service);
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), ansible: false);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required True | started True | finished True
            detail: -
            decision: approved by ops | Approved at deploy (change <id>): the plan only creates.
            digest: the approval document
            pre-approval: approved | Run #3's plan was approved at deploy by ops: it only creates.
            """, ApprovalPins.Pin(run, StepNames.Approval, digests) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }

    [Fact]
    public async Task A_deployed_pipeline_with_an_ansible_stage_waits_and_says_why()
    {
        var (server, agent, http) = await ReadyAsync(postgres, Policy("policy: { approval: manual }"));
        await using var _ = server;
        using var __ = http;
        var change = await ApprovalPins.DeployedAsync(server, Service, ChangeKinds.CreateService, ResourceKinds.Service);
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1));
        ApprovalPins.Check("""
            run: awaiting-approval | Approval is manual (set by home/korriban/krb-app-01/web). Not approved at deploy: Plan does more than create; approve it yourself. Stage deploy runs Ansible, whose preview cannot be shown to only create.
            step: approval awaiting-approval | required True | started True | finished False
            detail: Approval is manual (set by home/korriban/krb-app-01/web). Not approved at deploy: Plan does more than create; approve it yourself. Stage deploy runs Ansible, whose preview cannot be shown to only create.
            decision: - by - | -
            digest: the approval document
            pre-approval: declined | Plan does more than create; approve it yourself. Stage deploy runs Ansible, whose preview cannot be shown to only create.
            """, ApprovalPins.Pin(run, StepNames.Approval, digests) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }

    [Fact]
    public async Task A_deployed_pipeline_the_policy_approves_records_that_it_was_not_needed()
    {
        var (server, agent, http) = await ReadyAsync(postgres, PipelineRepository.Repository(PipelineRepository.TofuOnly));
        await using var _ = server;
        using var __ = http;
        var change = await ApprovalPins.DeployedAsync(server, Service, ChangeKinds.CreateService, ResourceKinds.Service);
        await ScheduleAsync(server);
        var (run, digests) = await PlannedAsync(server, agent, FakeAgent.Plan(create: 1), ansible: false);
        ApprovalPins.Check("""
            run: running | -
            step: approval succeeded | required False | started True | finished True
            detail: -
            decision: approved by policy | Approval is automatic (set by default).
            digest: the approval document
            pre-approval: not-needed | Run #3 needed no approval: Approval is automatic (set by default).
            """, ApprovalPins.Pin(run, StepNames.Approval, digests) + "\n" + ApprovalPins.Pin(await ApprovalPins.ChangeAsync(server, change)));
    }
}
