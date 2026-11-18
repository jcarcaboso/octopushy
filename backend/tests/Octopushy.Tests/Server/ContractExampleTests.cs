using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// The inputs the backend builds, through <see cref="AssignmentBuilder"/> for the example lab's guest, against the executor
/// examples in <c>contracts/agent/v1/examples</c>, which the agent's tests read into its own input types. Set
/// <see cref="ContractExamples.WriteVariable"/> to 1 to write them there instead.
/// </summary>
public class ContractExampleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestDirectory ="labs/home/proxmox/korriban/guests/krb-app-01";
    private const string SshKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY";

    /// <summary>What depends on the run and not on the code: the commit, and the ids of the run and its steps.</summary>
    private static readonly string[] AnsibleVarying = ["context/commit", "context/run", "manifest/commit", "manifest/workflowCommit"];

    /// <summary>The commit the variables name.</summary>
    private static readonly string[] OpenTofuVarying = ["variables/octopushy/revision"];

    /// <summary>The example lab, with one guest: korriban's krb-app-01.</summary>
    private static TestRepository ExampleLab()
    {
        var repository = new TestRepository();
        foreach (var file in Directory.EnumerateFiles(TestRepository.ExampleLab, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(TestRepository.ExampleLab, file);
            if (!relative.StartsWith("labs/home/proxmox/moraband", StringComparison.Ordinal))
                repository.Write(relative, File.ReadAllText(file).TrimEnd('\n'));
        }

        return repository;
    }

    /// <summary>The agent's assignments, kept until a test asks for each, so none is dropped while waiting for another.</summary>
    internal sealed class Inbox(ServerHarness server, FakeAgent agent)
    {
        private readonly List<Assignment> _waiting = [];

        public async Task<Assignment> NextAsync(string executor, string action)
        {
            for (var i = 0; i < 6; i++)
            {
                _waiting.AddRange(agent.TakeAssignments());
                if (_waiting.FirstOrDefault(a => a.Executor == executor && a.Action == action) is { } found)
                {
                    _waiting.Remove(found);
                    return found;
                }

                await server.TickAsync();
            }

            throw new InvalidOperationException($"No {executor}/{action} assignment arrived.");
        }
    }

    [Fact]
    public async Task The_example_guests_runs_send_the_input_examples_and_read_the_output_examples()
    {
        using var repository = ExampleLab();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("proxmox/korriban/krb-app-01/machine_tools_token", "synthetic-machine-tools-token");
        await server.SetSecretAsync("proxmox/korriban/krb-app-01/ssh-key", SshKey);
        await server.SetPlainAsync("proxmox/korriban/krb-app-01/host-key", HostKey);
        var agent = await server.ConnectAgentAsync("home");
        var inbox = new Inbox(server, agent);
        await server.SyncAndTrustAsync();

        var observe = await inbox.NextAsync("proxmox", "observe");
        ContractExamples.AssertInput("proxmox", "observe", observe.InputJson);
        await agent.ReportAsync(observe, Outcome.Succeeded, new JsonObject());

        // Provision: plan, apply, verify.
        var plan = await inbox.NextAsync("opentofu", "preview");
        ContractExamples.AssertInput("opentofu", "preview", plan.InputJson, OpenTofuVarying);
        // From here on the agent reports its examples, and what the engine read of each is checked.
        await agent.ReportAsync(plan, Outcome.Succeeded, ContractExamples.Output("opentofu", "preview"), artifacts: FakeAgent.PlanArtifact());
        var apply = await inbox.NextAsync("opentofu", "apply");
        ContractExamples.AssertInput("opentofu", "apply", apply.InputJson, OpenTofuVarying);
        await agent.ReportAsync(apply, Outcome.Succeeded, ContractExamples.Output("opentofu", "apply"));
        var verify = await inbox.NextAsync("proxmox", "verify-guest");
        ContractExamples.AssertInput("proxmox", "verify-guest", verify.InputJson);
        await agent.ReportAsync(verify, Outcome.Succeeded, ContractExamples.Output("proxmox", "verify-guest"));
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        // outputs: the apply's module outputs are the guest's. tags, description and node: the verified VM is its own.
        Assert.True(JsonNode.DeepEquals(ContractExamples.Output("opentofu", "apply")["outputs"], JsonNode.Parse(guest.Outputs!)));
        Assert.Equal(310, guest.BoundVmid);

        // Configure: the setup stage's preview, then its playbook.
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        var preview = await inbox.NextAsync("ansible", "preview");
        ContractExamples.AssertInput("ansible", "preview", preview.InputJson, AnsibleVarying);
        await ReportPreviewAsync(agent, preview);
        var playbook = await inbox.NextAsync("ansible", "run");
        ContractExamples.AssertInput("ansible", "run", playbook.InputJson, AnsibleVarying);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ContractExamples.Output("ansible", "run"));
        await server.TickAsync();
        // prepared: what the preview resolved is what the guest was configured with.
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        Assert.True(JsonNode.DeepEquals(ContractExamples.Output("ansible", "preview")["prepared"],
            JsonNode.Parse((await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().SingleAsync())).Snapshot!)));

        // Retire: lift protection, destroy, check the VM is gone.
        server.Repository.Delete(GuestDirectory);
        server.Commit("Remove the guest");
        await server.SyncAsync();
        var destroy = FakeAgent.PlanArtifact("destroy");
        var retire = await inbox.NextAsync("opentofu", "preview");
        await agent.ReportAsync(retire, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: destroy);
        await server.TickAsync();
        // The example guest's deletions are manual.
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, destroy.Sha256, "ops", null, CancellationToken.None);
        var unprotect = await inbox.NextAsync("proxmox", "set-protection");
        ContractExamples.AssertInput("proxmox", "set-protection", unprotect.InputJson);
        await agent.ReportAsync(unprotect, Outcome.Succeeded);
        await agent.ReportAsync(await inbox.NextAsync("opentofu", "apply"), Outcome.Succeeded);
        var absent = await inbox.NextAsync("proxmox", "verify-absent");
        ContractExamples.AssertInput("proxmox", "verify-absent", absent.InputJson);
    }

    /// <summary>The ansible preview example, with the artifact it is the content of, as the agent sends them.</summary>
    private static Task ReportPreviewAsync(FakeAgent agent, Assignment preview)
    {
        var outputs = ContractExamples.Output("ansible", "preview");
        var content = Encoding.UTF8.GetBytes(outputs.ToJsonString());
        var artifact = new Artifact { Name = AssignmentBuilder.PreviewArtifact, Content = ByteString.CopyFrom(content), Sha256 = ArtifactStore.Sha256(content) };
        return agent.ReportAsync(preview, Outcome.Succeeded, outputs, artifacts: artifact);
    }

    [Fact]
    public async Task The_outcome_a_playbook_reports_is_read_from_its_example()
    {
        using var repository = ExampleLab();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("proxmox/korriban/krb-app-01/machine_tools_token", "synthetic-machine-tools-token");
        await server.SetSecretAsync("proxmox/korriban/krb-app-01/ssh-key", SshKey);
        await server.SetPlainAsync("proxmox/korriban/krb-app-01/host-key", HostKey);
        var agent = await server.ConnectAgentAsync("home");
        var inbox = new Inbox(server, agent);
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await inbox.NextAsync("proxmox", "observe"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await inbox.NextAsync("opentofu", "preview"), Outcome.Succeeded, ContractExamples.Output("opentofu", "preview"),
            artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await inbox.NextAsync("opentofu", "apply"), Outcome.Succeeded);
        await agent.ReportAsync(await inbox.NextAsync("proxmox", "verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ReportPreviewAsync(agent, await inbox.NextAsync("ansible", "preview"));

        var example = ContractExamples.Output("ansible", "run", "outcome");
        await agent.ReportAsync(await inbox.NextAsync("ansible", "run"), Outcome.Succeeded, example);
        await server.TickAsync();

        // outcome and detail: the example lab declares deferred, which waits for a person, saying why.
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains(example["detail"]!.GetValue<string>(), run.Steps.Single(s => s.Name == StepNames.OutcomeApproval).Detail);
    }

    [Fact]
    public async Task A_build_input_is_its_example()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        var build = await nix.NextAsync("build");
        // The build key hashes what is built, which includes the commit of the test repository.
        ContractExamples.AssertInput("nix", "build", build.InputJson, "key");
    }
}
